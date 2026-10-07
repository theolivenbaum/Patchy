using System.Numerics;

namespace XRay.Psd.Imaging;

/// <summary>
/// Straight-alpha RGBA image stored as four float planes, positioned at
/// <see cref="Bounds"/> in document space. Planar storage lets every blend and
/// conversion loop run over contiguous <see cref="Vector{T}"/> lanes.
/// </summary>
internal sealed class PlanarImage
{
    public PlanarImage(PsdRect bounds)
    {
        Bounds = bounds;
        var count = bounds.Width * bounds.Height;
        R = new float[count];
        G = new float[count];
        B = new float[count];
        A = new float[count];
    }

    public PsdRect Bounds { get; }

    public int Width => Bounds.Width;

    public int Height => Bounds.Height;

    public float[] R { get; }

    public float[] G { get; }

    public float[] B { get; }

    public float[] A { get; }

    public int RowOffset(int documentY, int documentX) => ((documentY - Bounds.Top) * Width) + (documentX - Bounds.Left);

    public PlanarImage Clone()
    {
        var copy = new PlanarImage(Bounds);
        R.CopyTo(copy.R, 0);
        G.CopyTo(copy.G, 0);
        B.CopyTo(copy.B, 0);
        A.CopyTo(copy.A, 0);
        return copy;
    }

    /// <summary>Fills every pixel with an opaque or translucent color.</summary>
    public void Fill(float r, float g, float b, float a)
    {
        R.AsSpan().Fill(r);
        G.AsSpan().Fill(g);
        B.AsSpan().Fill(b);
        A.AsSpan().Fill(a);
    }

    /// <summary>Converts the region <paramref name="rect"/> (document space) to 8-bit straight RGBA. Pixels outside this image are transparent.</summary>
    public RgbaImage ToRgba(PsdRect rect)
    {
        var output = new RgbaImage(rect.Width, rect.Height);
        var overlap = rect.Intersect(Bounds);
        if (overlap.IsEmpty)
        {
            return output;
        }

        var width = overlap.Width;
        var r = new byte[width];
        var g = new byte[width];
        var b = new byte[width];
        var a = new byte[width];
        var pixels = output.Pixels;
        for (var y = overlap.Top; y < overlap.Bottom; y++)
        {
            var source = RowOffset(y, overlap.Left);
            UnitFloatToBytes(R.AsSpan(source, width), r);
            UnitFloatToBytes(G.AsSpan(source, width), g);
            UnitFloatToBytes(B.AsSpan(source, width), b);
            UnitFloatToBytes(A.AsSpan(source, width), a);
            var target = (((y - rect.Top) * rect.Width) + (overlap.Left - rect.Left)) * 4;
            for (var x = 0; x < width; x++, target += 4)
            {
                if (a[x] == 0)
                {
                    continue;
                }

                pixels[target] = r[x];
                pixels[target + 1] = g[x];
                pixels[target + 2] = b[x];
                pixels[target + 3] = a[x];
            }
        }

        return output;
    }

    /// <summary>SIMD conversion of [0,1] floats to rounded, clamped bytes.</summary>
    public static void UnitFloatToBytes(ReadOnlySpan<float> source, Span<byte> destination)
    {
        var i = 0;
        if (Vector.IsHardwareAccelerated && source.Length >= Vector<float>.Count)
        {
            var count = Vector<float>.Count;
            var scale = new Vector<float>(255f);
            var half = new Vector<float>(0.5f);
            var zero = Vector<float>.Zero;
            var max = new Vector<float>(255f);
            Span<int> lanes = stackalloc int[count];
            for (; i <= source.Length - count; i += count)
            {
                var v = Vector.Min(Vector.Max((new Vector<float>(source[i..]) * scale) + half, zero), max);
                Vector.ConvertToInt32(v).CopyTo(lanes);
                for (var lane = 0; lane < count; lane++)
                {
                    destination[i + lane] = (byte)lanes[lane];
                }
            }
        }

        for (; i < source.Length; i++)
        {
            var v = (source[i] * 255f) + 0.5f;
            destination[i] = v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)v;
        }
    }
}
