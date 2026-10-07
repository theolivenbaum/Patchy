using XRay.Psd.Imaging.Icc;

namespace XRay.Psd.Imaging;

/// <summary>
/// Converts single colors read from descriptors (<c>CMYC</c>, <c>Grsc</c>,
/// <c>RGBC</c>), text engine data (<c>/FillColor</c> types 0, 1 and 2), legacy
/// 10-byte color records and pattern tiles to sRGB the same way the document's
/// pixels convert, so effect and text colors keep their relationship with the
/// converted pixels. This is the reference's <c>CmykColorConverter</c>
/// (.reference/src/psd/psd_io_internal.hpp): colors quantize to the 8-bit
/// channel convention first (CMYK inverted, gray 0 = black), then go through the
/// document's ICC transform with the same tables as an 8-bit pixel plane. Without
/// a usable profile, or with color management off, the built-in formulas apply.
/// </summary>
internal sealed class DocumentColors
{
    private readonly PsdDocument _document;

    public DocumentColors(PsdDocument document) => _document = document;

    /// <summary>
    /// The document's ICC transform when it converts <paramref name="channels"/>
    /// device channels (1 gray, 3 RGB, 4 CMYK), else null.
    /// </summary>
    public IccSrgbTransform? TransformFor(int channels) =>
        _document.ColorTransform is { } transform && transform.Channels == channels ? transform : null;

    /// <summary>
    /// CMYK ink fractions (0 to 1, 1 = full ink) to sRGB: through the document's
    /// CMYK profile, else the naive mix <c>R = (1 - C)(1 - K)</c>
    /// (<c>rgb_from_ink</c>).
    /// </summary>
    public PsdColor FromInk(double cyan, double magenta, double yellow, double black, byte alpha = 255)
    {
        cyan = Unit(cyan);
        magenta = Unit(magenta);
        yellow = Unit(yellow);
        black = Unit(black);
        if (TransformFor(4) is { } transform)
        {
            static float Inverted(double ink) => (float)(Math.Round((1 - ink) * 255) / 255);
            if (Convert(transform, [Inverted(cyan), Inverted(magenta), Inverted(yellow), Inverted(black)], 8, alpha) is { } managed)
            {
                return managed;
            }
        }

        return new PsdColor(Level((1 - cyan) * (1 - black)), Level((1 - magenta) * (1 - black)), Level((1 - yellow) * (1 - black)), alpha);
    }

    /// <summary>
    /// Gray lightness (0 = black, 1 = white) to sRGB: through the gray profile of a
    /// grayscale or duotone document, else a neutral copy (<c>rgb_from_gray</c>).
    /// </summary>
    public PsdColor FromGray(double lightness, byte alpha = 255)
    {
        var level = Level(lightness);
        if (TransformFor(1) is { } transform && Convert(transform, [level / 255f], 8, alpha) is { } managed)
        {
            return managed;
        }

        return new PsdColor(level, level, level, alpha);
    }

    /// <summary>
    /// RGB components on the 0 to 255 scale, as stored by text engine data and
    /// legacy records: through the document's RGB profile (RGB and indexed
    /// documents with a profile that is not sRGB), else rounded as they are.
    /// </summary>
    public PsdColor FromRgb(double red, double green, double blue, byte alpha = 255)
    {
        byte r = Byte(red), g = Byte(green), b = Byte(blue);
        if (TransformFor(3) is { } transform && Convert(transform, [r / 255f, g / 255f, b / 255f], 8, alpha) is { } managed)
        {
            return managed;
        }

        return new PsdColor(r, g, b, alpha);
    }

    /// <summary>
    /// Descriptor RGB (<c>Rd  </c>, <c>Grn </c>, <c>Bl  </c> on 0 to 255). In 32-bit
    /// documents these are linear light like the float channels
    /// (<c>rgb_from_descriptor_rgb</c> with <c>linear_rgb</c>: Photoshop shows the
    /// fill of psd-tools' 300dpi.psb as its sRGB encoding), so they convert like 32-bit pixels;
    /// otherwise as <see cref="FromRgb"/>.
    /// </summary>
    public PsdColor FromDescriptorRgb(double red, double green, double blue)
    {
        if (_document.Depth != 32)
        {
            return FromRgb(red, green, blue);
        }

        float Linear(double value) => double.IsFinite(value) ? (float)Math.Clamp(value / 255.0, 0, 1) : 0f;
        float[] linear = [Linear(red), Linear(green), Linear(blue)];
        if (TransformFor(3) is { } transform && Convert(transform, linear, 32, 255) is { } managed)
        {
            return managed;
        }

        ColorSpaces.LinearToSrgb(linear);
        return new PsdColor(Level(linear[0]), Level(linear[1]), Level(linear[2]));
    }

    /// <summary>
    /// Converts device planes (PSD conventions, 8-bit levels as floats) through the
    /// document transform for their channel count. Returns false, leaving
    /// <paramref name="image"/> untouched, when that transform does not apply.
    /// </summary>
    public bool TryConvert(float[][] planes, PlanarImage image)
    {
        return TransformFor(planes.Length) is { } transform && transform.Apply(planes, 8, image);
    }

    /// <summary>One color through the planar path (the pixel tables), or null when the transform does not manage that depth.</summary>
    private static PsdColor? Convert(IccSrgbTransform transform, float[] device, int depth, byte alpha)
    {
        var planes = new float[device.Length][];
        for (var i = 0; i < device.Length; i++)
        {
            planes[i] = [device[i]];
        }

        var image = new PlanarImage(new PsdRect(0, 0, 1, 1));
        if (!transform.Apply(planes, depth, image))
        {
            return null;
        }

        Span<byte> rgb = stackalloc byte[3];
        PlanarImage.UnitFloatToBytes([image.R[0], image.G[0], image.B[0]], rgb);
        return new PsdColor(rgb[0], rgb[1], rgb[2], alpha);
    }

    private static double Unit(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;

    private static byte Level(double unit) => (byte)Math.Clamp(Math.Round(Unit(unit) * 255), 0, 255);

    private static byte Byte(double value) => double.IsFinite(value) ? (byte)Math.Clamp(Math.Round(value), 0, 255) : (byte)0;
}
