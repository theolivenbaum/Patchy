using System.Buffers;
using System.Numerics;
using XRay.Psd.Layers;

namespace XRay.Psd.Rendering;

/// <summary>A color transform applied by an adjustment layer to the backdrop.</summary>
internal interface IAdjustment
{
    void Apply(Span<float> r, Span<float> g, Span<float> b);
}

/// <summary>
/// Factory for the adjustment layers the renderer supports. Every transform runs on
/// 8-bit values, as the reference compositor does (<c>adjust_color</c> in
/// .reference/src/render/compositor.cpp): the float backdrop is quantized to a byte,
/// mapped through the calibrated Photoshop math, and returned as byte / 255.
/// Payload parsing follows .reference/src/psd/psd_adjustments.cpp; the math follows
/// .reference/src/core/adjustment_layer.cpp and .reference/docs/adjustments-calibration.md.
/// </summary>
/// <remarks>
/// Not modeled, as in the reference: Vibrance, Black and White, Channel Mixer, Photo
/// Filter, Gradient Map, Selective Color and Color Lookup (those layers render as no-ops),
/// and the ink-space evaluation of CMYK and gray documents (which needs their ICC
/// profiles). Color Balance is parsed but not rendered (see the <c>blnc</c> case).
/// </remarks>
internal static partial class Adjustments
{
    public static IAdjustment? Create(PsdLayer layer)
    {
        if (layer.ContentKey is not { } key)
        {
            return null;
        }

        var block = layer.GetTaggedBlock(key);
        var data = block is null ? [] : block.Data.Span;
        try
        {
            return key switch
            {
                "nvrt" => new InvertAdjustment(),
                "thrs" => ParseThreshold(data) is { } level ? new ThresholdAdjustment(level) : null,
                "post" => ParsePosterize(data) is { } levels ? LutAdjustment.Uniform(PosterizeLut(levels)) : null,
                "levl" => ParseLevels(data) is { } levelsSettings ? LutAdjustment.From(BuildLevelsLut(ForDocument(layer, levelsSettings))) : null,
                "curv" => ParseCurves(data) is { } curves ? LutAdjustment.From(BuildCurvesLut(ForDocument(layer, curves))) : null,
                "hue2" or "hue " => ParseHueSaturation(data) is { } hue ? new HueSaturationAdjustment(hue) : null,
                // Color Balance stays unrendered on purpose: the reference's midtones-only
                // model (BuildColorBalanceLut) adds round(slider * 2.55) flat, which is far
                // from Photoshop (max 98/255 off on photoshop-color-balance.psd, against 29
                // for leaving the layer out). See TODO.md.
                "blnc" => null,
                "brit" => ResolveBrightnessContrast(layer, data) is { } bc ? LutAdjustment.Uniform(BuildBrightnessContrastLut(bc)) : null,
                "expA" => ParseExposure(data) is { } exposure ? LutAdjustment.Uniform(BuildExposureLut(exposure)) : null,
                _ => null,
            };
        }
        catch (PsdFormatException)
        {
            // A malformed payload stays unrendered, as the reference keeps it on the opaque
            // layer path instead of guessing settings.
            return null;
        }
    }

    /// <summary>
    /// One-plane documents keep their Levels record and curve in the slot RGB documents
    /// call red; the reader copies it to green and blue because the plane becomes
    /// R = G = B (psd_document_io.cpp, the <c>is_grayscale_color_mode</c> branch).
    /// </summary>
    private static bool IsOnePlane(PsdLayer layer) =>
        layer.Document.ColorMode is PsdColorMode.Grayscale or PsdColorMode.Duotone or PsdColorMode.Bitmap or PsdColorMode.Indexed;

    private static LevelsSettings ForDocument(PsdLayer layer, LevelsSettings settings) =>
        IsOnePlane(layer) ? settings with { Green = settings.Red, Blue = settings.Red } : settings;

    private static CurvesSettings ForDocument(PsdLayer layer, CurvesSettings settings) =>
        IsOnePlane(layer) ? settings with { Green = settings.Red, Blue = settings.Red } : settings;

    /// <summary>The reference's <c>clamp_byte</c>: <c>lround</c> of a float, clamped to a byte.</summary>
    internal static byte ClampByte(float value) => (byte)Math.Clamp(Math.Round((double)value, MidpointRounding.AwayFromZero), 0, 255);

    /// <summary><c>std::lround</c> of a double, clamped to a byte.</summary>
    internal static byte RoundByte(double value) => (byte)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), 0, 255);

    private static int Byte(float value) => (int)Math.Clamp(MathF.Round(value * 255f), 0f, 255f);

    /// <summary>Quantizes a row to byte indices (round to nearest, clamped), vectorized.</summary>
    private static void Quantize(ReadOnlySpan<float> values, Span<int> bytes)
    {
        var i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var scale = new Vector<float>(255f);
            var zero = Vector<float>.Zero;
            for (; i <= values.Length - Vector<float>.Count; i += Vector<float>.Count)
            {
                var v = Vector.Round(Vector.Min(Vector.Max(new Vector<float>(values[i..]) * scale, zero), scale));
                Vector.ConvertToInt32(v).CopyTo(bytes[i..]);
            }
        }

        for (; i < values.Length; i++)
        {
            bytes[i] = Byte(values[i]);
        }
    }

    private sealed class InvertAdjustment : IAdjustment
    {
        public void Apply(Span<float> r, Span<float> g, Span<float> b)
        {
            Invert(r);
            Invert(g);
            Invert(b);
        }

        private static void Invert(Span<float> values)
        {
            var i = 0;
            var one = Vector<float>.One;
            for (; i <= values.Length - Vector<float>.Count; i += Vector<float>.Count)
            {
                (one - new Vector<float>(values[i..])).CopyTo(values[i..]);
            }

            for (; i < values.Length; i++)
            {
                values[i] = 1f - values[i];
            }
        }
    }

    /// <summary>Photoshop's Threshold: integer luma (30/59/11) against the level (<c>threshold_luminance</c>).</summary>
    private sealed class ThresholdAdjustment(int level) : IAdjustment
    {
        public void Apply(Span<float> r, Span<float> g, Span<float> b)
        {
            for (var i = 0; i < r.Length; i++)
            {
                var value = ThresholdLuminance(Byte(r[i]), Byte(g[i]), Byte(b[i])) >= level ? 1f : 0f;
                r[i] = g[i] = b[i] = value;
            }
        }
    }

    internal static int ThresholdLuminance(int red, int green, int blue) => ((red * 30) + (green * 59) + (blue * 11)) / 100;

    /// <summary>
    /// Photoshop's Posterize (<c>posterize_channel_value</c>): equal-width input buckets
    /// mapped to the truncated output ramp. Byte-exact at 3, 7, 13 and 21 levels.
    /// </summary>
    internal static byte PosterizeValue(int value, int levels)
    {
        levels = Math.Clamp(levels, 2, 255);
        var bucket = value * levels / 256;
        return (byte)(bucket * 255 / (levels - 1));
    }

    internal static byte[] PosterizeLut(int levels)
    {
        var lut = new byte[256];
        for (var v = 0; v < 256; v++)
        {
            lut[v] = PosterizeValue(v, levels);
        }

        return lut;
    }

    /// <summary>A channel-separable adjustment as three exact 256-entry tables (<c>build_adjustment_lut</c>).</summary>
    internal sealed class LutAdjustment : IAdjustment
    {
        private readonly float[] _red;
        private readonly float[] _green;
        private readonly float[] _blue;

        private LutAdjustment(byte[] red, byte[] green, byte[] blue)
        {
            _red = ToUnit(red);
            _green = ReferenceEquals(green, red) ? _red : ToUnit(green);
            _blue = ReferenceEquals(blue, red) ? _red : ReferenceEquals(blue, green) ? _green : ToUnit(blue);
        }

        public static LutAdjustment Uniform(byte[] lut) => new(lut, lut, lut);

        public static LutAdjustment From(ChannelLuts luts) => new(luts.Red, luts.Green, luts.Blue);

        public void Apply(Span<float> r, Span<float> g, Span<float> b)
        {
            var indices = ArrayPool<int>.Shared.Rent(r.Length);
            try
            {
                Map(r, _red, indices);
                Map(g, _green, indices);
                Map(b, _blue, indices);
            }
            finally
            {
                ArrayPool<int>.Shared.Return(indices);
            }
        }

        private static void Map(Span<float> values, float[] table, int[] indices)
        {
            var bytes = indices.AsSpan(0, values.Length);
            Quantize(values, bytes);
            for (var i = 0; i < values.Length; i++)
            {
                values[i] = table[bytes[i] & 0xFF];
            }
        }

        private static float[] ToUnit(byte[] lut)
        {
            var table = new float[256];
            for (var i = 0; i < 256; i++)
            {
                table[i] = lut[i] / 255f;
            }

            return table;
        }
    }
}

/// <summary>Per-channel 256-entry transfer tables.</summary>
internal sealed record ChannelLuts(byte[] Red, byte[] Green, byte[] Blue);
