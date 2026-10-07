using System.Numerics;

namespace Patchy.Psd.Imaging;

/// <summary>
/// Converts decoded channel planes from the document color mode to straight
/// sRGB float planes. CMYK uses the uncalibrated inverse-ink formula (no ICC);
/// Lab uses the CIE D50 to sRGB (Bradford-adapted) transform; 32-bit linear
/// data is gamma-encoded with the sRGB transfer curve.
/// </summary>
internal static class ColorSpaces
{
    public static int ColorChannelCount(PsdColorMode mode) => mode switch
    {
        PsdColorMode.Rgb or PsdColorMode.Lab => 3,
        PsdColorMode.Cmyk => 4,
        _ => 1,
    };

    public static void ToRgb(PsdColorMode mode, int depth, float[]?[] planes, PlanarImage image, PsdColor[]? palette)
    {
        var count = image.R.Length;
        float[] Plane(int index, float fallback)
        {
            if (index < planes.Length && planes[index] is { } p)
            {
                return p;
            }

            var filled = new float[count];
            filled.AsSpan().Fill(fallback);
            return filled;
        }

        switch (mode)
        {
            case PsdColorMode.Rgb:
                Plane(0, 0).AsSpan().CopyTo(image.R);
                Plane(1, 0).AsSpan().CopyTo(image.G);
                Plane(2, 0).AsSpan().CopyTo(image.B);
                if (depth == 32)
                {
                    LinearToSrgb(image.R);
                    LinearToSrgb(image.G);
                    LinearToSrgb(image.B);
                }

                break;
            case PsdColorMode.Cmyk:
                CmykToRgb(Plane(0, 1), Plane(1, 1), Plane(2, 1), Plane(3, 1), image);
                break;
            case PsdColorMode.Lab:
                LabToRgb(Plane(0, 1), Plane(1, 0.5f), Plane(2, 0.5f), depth, image);
                break;
            case PsdColorMode.Indexed:
                IndexedToRgb(Plane(0, 0), palette, image);
                break;
            default:
                {
                    // Grayscale, bitmap, duotone (shown as its gray base) and multichannel (first channel).
                    var gray = Plane(0, 0);
                    if (depth == 32)
                    {
                        LinearToSrgb(gray);
                    }

                    gray.AsSpan().CopyTo(image.R);
                    gray.AsSpan().CopyTo(image.G);
                    gray.AsSpan().CopyTo(image.B);
                    break;
                }
        }
    }

    /// <summary>
    /// PSD CMYK samples are stored inverted (1 means no ink), so the naive
    /// device conversion is a plain product: R = C' * K'.
    /// </summary>
    private static void CmykToRgb(float[] c, float[] m, float[] y, float[] k, PlanarImage image)
    {
        Multiply(c, k, image.R);
        Multiply(m, k, image.G);
        Multiply(y, k, image.B);
    }

    private static void Multiply(ReadOnlySpan<float> a, ReadOnlySpan<float> b, Span<float> destination)
    {
        var i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            for (; i <= a.Length - Vector<float>.Count; i += Vector<float>.Count)
            {
                (new Vector<float>(a[i..]) * new Vector<float>(b[i..])).CopyTo(destination[i..]);
            }
        }

        for (; i < a.Length; i++)
        {
            destination[i] = a[i] * b[i];
        }
    }

    private static void IndexedToRgb(float[] indices, PsdColor[]? palette, PlanarImage image)
    {
        for (var i = 0; i < indices.Length; i++)
        {
            var index = (int)MathF.Round(indices[i] * 255f);
            if (palette is not null && index < palette.Length)
            {
                var color = palette[index];
                image.R[i] = color.R / 255f;
                image.G[i] = color.G / 255f;
                image.B[i] = color.B / 255f;
            }
        }
    }

    private static void LabToRgb(float[] l, float[] a, float[] b, int depth, PlanarImage image)
    {
        // 8-bit: a/b bytes are offset by 128. 16-bit: 0..32768 maps L, a/b center on 16384*2.
        for (var i = 0; i < l.Length; i++)
        {
            var lightness = l[i] * 100.0;
            double aa, bb;
            if (depth == 16)
            {
                aa = (a[i] * 65535.0 / 257.0) - 128.0;
                bb = (b[i] * 65535.0 / 257.0) - 128.0;
            }
            else
            {
                aa = (a[i] * 255.0) - 128.0;
                bb = (b[i] * 255.0) - 128.0;
            }

            var (r, g, bl) = LabToSrgb(lightness, aa, bb);
            image.R[i] = (float)Math.Clamp(r, 0, 1);
            image.G[i] = (float)Math.Clamp(g, 0, 1);
            image.B[i] = (float)Math.Clamp(bl, 0, 1);
        }
    }

    /// <summary>CIE L*a*b* (D50) to gamma-encoded sRGB in [0,1] (unclamped).</summary>
    public static (double R, double G, double B) LabToSrgb(double l, double a, double b)
    {
        var fy = (l + 16.0) / 116.0;
        var fx = fy + (a / 500.0);
        var fz = fy - (b / 200.0);
        static double Finv(double t) => t > 6.0 / 29.0 ? t * t * t : 3 * (6.0 / 29.0) * (6.0 / 29.0) * (t - (4.0 / 29.0));

        // D50 white point.
        var x = 0.96422 * Finv(fx);
        var y = 1.00000 * Finv(fy);
        var z = 0.82521 * Finv(fz);

        // XYZ (D50) -> linear sRGB (D65) via the Bradford-adapted matrix.
        var rl = (3.1338561 * x) - (1.6168667 * y) - (0.4906146 * z);
        var gl = (-0.9787684 * x) + (1.9161415 * y) + (0.0334540 * z);
        var bl = (0.0719453 * x) - (0.2289914 * y) + (1.4052427 * z);
        return (EncodeSrgb(rl), EncodeSrgb(gl), EncodeSrgb(bl));
    }

    public static double EncodeSrgb(double linear)
    {
        if (linear <= 0)
        {
            return 0;
        }

        return linear <= 0.0031308 ? linear * 12.92 : (1.055 * Math.Pow(linear, 1.0 / 2.4)) - 0.055;
    }

    public static void LinearToSrgb(Span<float> values)
    {
        for (var i = 0; i < values.Length; i++)
        {
            var v = values[i];
            values[i] = v <= 0f ? 0f : v >= 1f ? 1f : (float)EncodeSrgb(v);
        }
    }
}
