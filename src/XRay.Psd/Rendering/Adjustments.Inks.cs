using XRay.Psd.Imaging.Icc;
using XRay.Psd.Layers;

namespace XRay.Psd.Rendering;

/// <summary>
/// Adjustment layers of CMYK, grayscale and duotone documents. Photoshop evaluates the
/// channel-wise kinds on the document's own channels, not on RGB; the reference does the
/// same through its <c>InkSpace</c> (<c>apply_adjustment_in_ink_space</c> and
/// <c>adjustment_runs_in_ink_space</c> in .reference/src/core/adjustment_layer.cpp,
/// .reference/docs/adjustments-calibration.md "Adjustment layers of CMYK documents").
/// </summary>
internal static partial class Adjustments
{
    /// <summary>
    /// Levels, Curves, Invert, Posterize and Brightness/Contrast run on the inks, as in the
    /// reference. Threshold runs on a gray channel like them; on four inks it compares one
    /// luma (<see cref="CmykThresholdAdjustment"/>), which the reference leaves on RGB.
    /// Exposure runs on a gray channel too, but in that channel's linear light
    /// (<see cref="GrayExposureTable"/>); Photoshop offers no Exposure in CMYK, so a CMYK
    /// document's keeps the RGB math with the other kinds that mix channels.
    /// </summary>
    internal static bool RunsOnInks(string key, PsdColorMode mode) => mode is PsdColorMode.Cmyk or PsdColorMode.Grayscale or PsdColorMode.Duotone && key switch
    {
        "levl" or "curv" or "nvrt" or "post" or "brit" or "thrs" => true,
        "expA" => mode != PsdColorMode.Cmyk,
        _ => false,
    };

    private static IAdjustment? CreateInkAdjustment(PsdLayer layer, string key, ReadOnlySpan<byte> data, IccInkSpace space)
    {
        if (key == "thrs" && space.Channels == 4)
        {
            return ParseThreshold(data) is { } level ? new CmykThresholdAdjustment(space, level) : null;
        }

        if (key == "post" && layer.Document.Depth == 16)
        {
            return ParsePosterize(data) is { } levels ? new InkPosterize16Adjustment(space, levels) : null;
        }

        var tables = key == "expA"
            ? ParseExposure(data) is { } exposure ? [GrayExposureTable(exposure, space)] : null
            : InkTables(layer, key, data);
        if (tables is null)
        {
            return null;
        }

        var channels = space.Channels == 1 ? new[] { tables[0] } : tables;
        return channels.All(IsIdentity) ? null : new InkAdjustment(space, channels);
    }

    /// <summary>
    /// The four per-ink tables (cyan, magenta, yellow, black; a gray document uses the
    /// first). Cyan, magenta and yellow read the records an RGB document calls red, green
    /// and blue; the black ink runs the same stages with its own record
    /// (<c>build_ink_adjustment_tables</c>).
    /// </summary>
    internal static byte[][]? InkTables(PsdLayer layer, string key, ReadOnlySpan<byte> data)
    {
        switch (key)
        {
            case "nvrt":
                {
                    var invert = new byte[256];
                    for (var v = 0; v < 256; v++)
                    {
                        invert[v] = (byte)(255 - v);
                    }

                    return [invert, invert, invert, invert];
                }

            case "thrs":
                {
                    if (ParseThreshold(data) is not { } level)
                    {
                        return null;
                    }

                    var threshold = new byte[256];
                    for (var v = 0; v < 256; v++)
                    {
                        threshold[v] = (byte)(v >= level ? 255 : 0);
                    }

                    return [threshold, threshold, threshold, threshold];
                }

            case "post":
                return ParsePosterize(data) is { } levels ? Uniform(PosterizeLut(levels)) : null;
            case "brit":
                return ResolveBrightnessContrast(layer, data) is { } bc ? Uniform(BuildBrightnessContrastLut(bc)) : null;
            case "expA":
                return ParseExposure(data) is { } exposure ? Uniform(BuildExposureLut(exposure)) : null;
            case "levl":
                {
                    if (ParseLevels(data) is not { } settings)
                    {
                        return null;
                    }

                    settings = ForDocument(layer, settings);
                    var luts = BuildLevelsLut(settings);
                    var black = BuildLevelsLut(settings with { Red = settings.BlackInk }).Red;
                    return [luts.Red, luts.Green, luts.Blue, black];
                }

            case "curv":
                {
                    if (ParseCurves(data) is not { } settings)
                    {
                        return null;
                    }

                    settings = ForDocument(layer, settings);
                    var luts = BuildCurvesLut(settings);
                    var black = BuildCurvesLut(settings with { Red = settings.BlackInk }).Red;
                    return [luts.Red, luts.Green, luts.Blue, black];
                }

            default:
                return null;
        }

        static byte[][] Uniform(byte[] lut) => [lut, lut, lut, lut];
    }

    /// <summary>
    /// Exposure on a gray channel: each stored level is linearized through the document's
    /// gray conversion (the luminance its profile gives it), exposed, offset and
    /// gamma-corrected as on RGB (<see cref="ExposureValue"/>, without the 2.2 power), and
    /// mapped back to the nearest stored level. On psd-tools' exposure_grayscale.psd (Dot
    /// Gain 20%) this is within 5/255 of Photoshop everywhere and within 1/255 on all but
    /// 0.2% of the pixels; the RGB math (a 2.2 power on the converted color) is up to 20/255
    /// off in the shadows, and the 2.2 power on the stored level up to 23/255.
    /// </summary>
    internal static byte[] GrayExposureTable(ExposureSettings settings, IccInkSpace space)
    {
        settings = settings.Clamped();
        var gain = Math.Pow(2.0, settings.ExposureHundredths / 100.0);
        var offset = settings.OffsetTenThousandths / 10000.0;
        var table = new byte[256];
        for (var v = 0; v < 256; v++)
        {
            var exposed = (space.LevelLuminance(v) * gain) + offset;
            var corrected = Math.Pow(Math.Max(0.0, exposed), 100.0 / settings.GammaHundredths);
            table[v] = (byte)space.LevelFromLuminance(Math.Clamp(corrected, 0.0, 1.0));
        }

        return table;
    }

    private static bool IsIdentity(byte[] table)
    {
        for (var v = 0; v < 256; v++)
        {
            if (table[v] != v)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Per-ink tables applied through the document's ink space.</summary>
    private sealed class InkAdjustment(IccInkSpace space, byte[][] tables) : IAdjustment
    {
        private readonly TableMap _map = new(tables);

        public void Apply(Span<float> r, Span<float> g, Span<float> b) => space.Apply(r, g, b, _map);
    }

    /// <summary>8-bit semantics: each channel rounds to a stored byte and goes through its table.</summary>
    private readonly struct TableMap(byte[][] tables) : IInkMap
    {
        public void Map(Span<float> channels)
        {
            for (var c = 0; c < channels.Length; c++)
            {
                channels[c] = tables[c][Byte(channels[c])] / 255f;
            }
        }
    }

    /// <summary>Posterize of a 16-bit document on its channels (<see cref="Posterize16Value"/>).</summary>
    private sealed class InkPosterize16Adjustment(IccInkSpace space, int levels) : IAdjustment
    {
        private readonly Posterize16Map _map = new(levels);

        public void Apply(Span<float> r, Span<float> g, Span<float> b) => space.Apply(r, g, b, _map);
    }

    private readonly struct Posterize16Map(int levels) : IInkMap
    {
        public void Map(Span<float> channels)
        {
            for (var c = 0; c < channels.Length; c++)
            {
                channels[c] = Posterize16Value(channels[c], levels);
            }
        }
    }

    /// <summary>
    /// Threshold in a CMYK document, fitted to psd-tools' threshold_cmyk.psd (SWOP, four
    /// levels): the inks become black ink alone (C = M = Y = 0, K = 100%) or no ink, by the
    /// rounded 30/59/11 luma of the naive mix <c>R = C' K' / 255</c> (stored values, 255 =
    /// no ink) against the level. Right on 99.9% of 34,000 pixels; the misses sit within
    /// half a level of the threshold. Luma of the converted sRGB color misclassifies 7%.
    /// </summary>
    private sealed class CmykThresholdAdjustment(IccInkSpace space, int level) : IAdjustment
    {
        private readonly ThresholdMap _map = new(level);

        public void Apply(Span<float> r, Span<float> g, Span<float> b) => space.Apply(r, g, b, _map);
    }

    private readonly struct ThresholdMap(int level) : IInkMap
    {
        public void Map(Span<float> channels)
        {
            var k = Byte(channels[3]);
            var luma = ((30.0 * Byte(channels[0]) * k) + (59.0 * Byte(channels[1]) * k) + (11.0 * Byte(channels[2]) * k)) / (100.0 * 255.0);
            var white = Math.Floor(luma + 0.5) >= level;
            channels[0] = channels[1] = channels[2] = 1f;
            channels[3] = white ? 1f : 0f;
        }
    }
}

