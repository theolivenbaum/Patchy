using XRay.Psd.Descriptors;
using XRay.Psd.IO;

namespace XRay.Psd.Rendering;

/// <summary>Channel Mixer: one output channel as percents of R, G, B plus a constant (all -200..200).</summary>
internal readonly record struct MixerChannel(int Red, int Green, int Blue, int Constant);

/// <summary>Channel Mixer (<c>mixr</c>).</summary>
internal sealed record ChannelMixerSettings(bool Monochrome, MixerChannel Red, MixerChannel Green, MixerChannel Blue);

/// <summary>Black and White (<c>blwh</c>): percents for the six hue ranges plus an optional tint.</summary>
internal sealed record BlackWhiteSettings(int Reds, int Yellows, int Greens, int Cyans, int Blues, int Magentas, PsdColor? Tint)
{
    public static BlackWhiteSettings Default => new(40, 60, 40, 60, 20, 80, null);
}

/// <summary>Selective Color (<c>selc</c>): CMYK corrections (-100..100) for nine color ranges.</summary>
internal sealed record SelectiveColorSettings(bool Absolute, SelectiveColorRange[] Ranges);

/// <summary>One Selective Color range correction.</summary>
internal readonly record struct SelectiveColorRange(int Cyan, int Magenta, int Yellow, int Black)
{
    public bool HasEffect => Cyan != 0 || Magenta != 0 || Yellow != 0 || Black != 0;
}

/// <summary>Photo Filter (<c>phfl</c>): filter color, density (0..100) and Preserve Luminosity.</summary>
internal readonly record struct PhotoFilterSettings(double Red, double Green, double Blue, int Density, bool PreserveLuminosity);

/// <summary>Vibrance (<c>vibA</c>): vibrance and saturation, -100..100.</summary>
internal readonly record struct VibranceSettings(int Vibrance, int Saturation);

/// <summary>
/// The adjustments the reference does not model: Channel Mixer, Black and White,
/// Selective Color, Gradient Map, Photo Filter and Vibrance. Payload layouts follow
/// Adobe's PSD file format specification; the math and its status against Photoshop are
/// recorded in docs/adjustments.md.
/// </summary>
internal static partial class Adjustments
{
    // ---- Parsing --------------------------------------------------------------------

    /// <summary>
    /// <c>mixr</c>: u16 version 1, u16 monochrome, then five-value records (red, green,
    /// blue, a fourth ink used by CMYK documents, constant) for the output channels. A
    /// monochrome mixer stores its gray record first.
    /// </summary>
    internal static ChannelMixerSettings? ParseChannelMixer(ReadOnlySpan<byte> data)
    {
        var reader = new BigEndianReader(data.ToArray());
        if (reader.Remaining < 14 || reader.ReadUInt16() != 1)
        {
            return null;
        }

        var monochrome = reader.ReadUInt16() != 0;
        var records = new MixerChannel[3];
        for (var i = 0; i < records.Length; i++)
        {
            if (reader.Remaining < 10)
            {
                if (i == 0 || !monochrome)
                {
                    return null;
                }

                records[i] = records[0];
                continue;
            }

            var red = reader.ReadInt16();
            var green = reader.ReadInt16();
            var blue = reader.ReadInt16();
            reader.Skip(2);
            var constant = reader.ReadInt16();
            records[i] = new MixerChannel(Math.Clamp((int)red, -200, 200), Math.Clamp((int)green, -200, 200), Math.Clamp((int)blue, -200, 200), Math.Clamp((int)constant, -200, 200));
        }

        return monochrome
            ? new ChannelMixerSettings(true, records[0], records[0], records[0])
            : new ChannelMixerSettings(false, records[0], records[1], records[2]);
    }

    /// <summary><c>blwh</c>: a version-16 descriptor with the six range percents, <c>useTint</c> and <c>tintColor</c>.</summary>
    internal static BlackWhiteSettings? ParseBlackWhite(ReadOnlyMemory<byte> data)
    {
        if (ReadVersionedDescriptor(data) is not { } descriptor)
        {
            return null;
        }

        var fallback = BlackWhiteSettings.Default;
        int Percent(string key, int value) => (int)Math.Clamp(Math.Round(descriptor.GetNumber(key, value)), -200, 300);
        var tint = descriptor.GetBoolean("useTint") ? descriptor.GetColor("tintColor") : null;
        return new BlackWhiteSettings(
            Percent("Rd  ", fallback.Reds),
            Percent("Yllw", fallback.Yellows),
            Percent("Grn ", fallback.Greens),
            Percent("Cyn ", fallback.Cyans),
            Percent("Bl  ", fallback.Blues),
            Percent("Mgnt", fallback.Magentas),
            tint);
    }

    /// <summary>
    /// <c>selc</c>: u16 version 1, u16 method (0 relative, 1 absolute), then ten records of
    /// four i16 percents (cyan, magenta, yellow, black). The first record is unused; the
    /// rest are reds, yellows, greens, cyans, blues, magentas, whites, neutrals, blacks.
    /// </summary>
    internal static SelectiveColorSettings? ParseSelectiveColor(ReadOnlySpan<byte> data)
    {
        var reader = new BigEndianReader(data.ToArray());
        if (reader.Remaining < 84 || reader.ReadUInt16() != 1)
        {
            return null;
        }

        var absolute = reader.ReadUInt16() == 1;
        reader.Skip(8);
        var ranges = new SelectiveColorRange[9];
        for (var i = 0; i < ranges.Length; i++)
        {
            ranges[i] = new SelectiveColorRange(
                Math.Clamp((int)reader.ReadInt16(), -100, 100),
                Math.Clamp((int)reader.ReadInt16(), -100, 100),
                Math.Clamp((int)reader.ReadInt16(), -100, 100),
                Math.Clamp((int)reader.ReadInt16(), -100, 100));
        }

        return new SelectiveColorSettings(absolute, ranges);
    }

    /// <summary>
    /// <c>phfl</c>: u16 version; version 3 stores the color as three i32 Lab values times
    /// 100, version 2 as a binary color (u16 space, four u16 components); then u32 density
    /// percent and a Preserve Luminosity byte.
    /// </summary>
    internal static PhotoFilterSettings? ParsePhotoFilter(ReadOnlySpan<byte> data)
    {
        var reader = new BigEndianReader(data.ToArray());
        if (reader.Remaining < 2)
        {
            return null;
        }

        var version = reader.ReadUInt16();
        (double R, double G, double B) color;
        if (version == 3 && reader.Remaining >= 17)
        {
            var l = reader.ReadInt32() / 100.0;
            var a = reader.ReadInt32() / 100.0;
            var b = reader.ReadInt32() / 100.0;
            color = Imaging.ColorSpaces.LabToSrgb(Math.Clamp(l, 0, 100), Math.Clamp(a, -128, 127), Math.Clamp(b, -128, 127));
        }
        else if (version == 2 && reader.Remaining >= 15)
        {
            var space = reader.ReadUInt16();
            Span<ushort> components = stackalloc ushort[4];
            for (var i = 0; i < 4; i++)
            {
                components[i] = reader.ReadUInt16();
            }

            if (BinaryColor(space, components) is not { } binary)
            {
                return null;
            }

            color = binary;
        }
        else
        {
            return null;
        }

        var density = (int)Math.Min(reader.ReadUInt32(), 100u);
        var preserve = reader.ReadByte() != 0;
        return new PhotoFilterSettings(Math.Clamp(color.R, 0, 1), Math.Clamp(color.G, 0, 1), Math.Clamp(color.B, 0, 1), density, preserve);
    }

    /// <summary>
    /// <c>grdm</c>: u16 version (1, or 3 with a four-byte interpolation key after the
    /// flags: <c>Gcls</c>, <c>Perc</c>, <c>Lnr </c>), reverse and dither bytes, the
    /// gradient name, color stops (i32 location 0..4096, i32 midpoint percent, binary
    /// color, u16 stop type), transparency stops (i32, i32, u16 opacity 0..255), then
    /// expansion count, smoothness (0..4096), length, form (1 = noise), u32 seed,
    /// transparency and vector-color flags, u32 roughness, u16 color model and the four
    /// noise minimums and maximums on a 0..32768 scale.
    /// </summary>
    internal static (Gradient Gradient, bool Reverse)? ParseGradientMap(ReadOnlySpan<byte> data)
    {
        try
        {
            return ReadGradientMap(new BigEndianReader(data.ToArray()));
        }
        catch (PsdFormatException)
        {
            return null;
        }
    }

    private static (Gradient Gradient, bool Reverse)? ReadGradientMap(BigEndianReader reader)
    {
        if (reader.Remaining < 4)
        {
            return null;
        }

        var version = reader.ReadUInt16();
        if (version is not (1 or 3))
        {
            return null;
        }

        var reverse = reader.ReadByte() != 0;
        reader.Skip(1); // dither: not modeled
        var interpolation = GradientInterpolation.Classic;
        if (version == 3)
        {
            interpolation = reader.ReadSignature() switch
            {
                "Perc" => GradientInterpolation.Perceptual,
                "Lnr " => GradientInterpolation.Linear,
                _ => GradientInterpolation.Classic,
            };
        }

        reader.ReadUnicodeString();
        var colorCount = reader.ReadUInt16();
        var colors = new List<GradientColorStop>();
        Span<ushort> components = stackalloc ushort[4];
        for (var i = 0; i < colorCount; i++)
        {
            var location = reader.ReadInt32();
            var midpoint = reader.ReadInt32();
            var space = reader.ReadUInt16();
            for (var c = 0; c < 4; c++)
            {
                components[c] = reader.ReadUInt16();
            }

            reader.Skip(2);
            var (r, g, b) = BinaryColor(space, components) ?? (0, 0, 0);
            colors.Add(new GradientColorStop(
                Math.Clamp(location / 4096f, 0f, 1f),
                new PsdColor(RoundByte(r * 255), RoundByte(g * 255), RoundByte(b * 255)),
                Math.Clamp(midpoint / 100f, 0f, 1f)));
        }

        var alphaCount = reader.ReadUInt16();
        var alphas = new List<GradientAlphaStop>();
        for (var i = 0; i < alphaCount; i++)
        {
            var location = reader.ReadInt32();
            var midpoint = reader.ReadInt32();
            var opacity = reader.ReadUInt16();
            alphas.Add(new GradientAlphaStop(Math.Clamp(location / 4096f, 0f, 1f), Math.Clamp(opacity / 255f, 0f, 1f), Math.Clamp(midpoint / 100f, 0f, 1f)));
        }

        var smoothness = 4096;
        GradientNoise? noise = null;
        if (reader.Remaining >= 8)
        {
            reader.Skip(2);
            smoothness = Math.Min((int)reader.ReadUInt16(), 4096);
            reader.Skip(2);
            var form = reader.ReadUInt16();
            if (form == 1 && reader.Remaining >= 30)
            {
                var seed = reader.ReadUInt32();
                var transparency = reader.ReadUInt16() != 0;
                reader.Skip(2);
                var roughness = (int)Math.Min(reader.ReadUInt32(), 4096u);
                var model = reader.ReadUInt16() switch
                {
                    4 => GradientNoiseModel.Hsb,
                    6 => GradientNoiseModel.Lab,
                    _ => GradientNoiseModel.Rgb,
                };
                var minimum = new int[4];
                var maximum = new int[4];
                for (var c = 0; c < 4; c++)
                {
                    minimum[c] = (int)Math.Round(reader.ReadUInt16() * 100.0 / 32768.0);
                }

                for (var c = 0; c < 4; c++)
                {
                    maximum[c] = (int)Math.Round(reader.ReadUInt16() * 100.0 / 32768.0);
                }

                noise = new GradientNoise(seed, roughness, transparency, model, minimum, maximum);
            }
        }

        if (noise is null && colors.Count == 0)
        {
            return null;
        }

        var gradient = new Gradient { Interpolation = interpolation, Smoothness = smoothness / 4096f, Noise = noise };
        gradient.ColorStops.AddRange(colors.OrderBy(s => s.Location));
        gradient.AlphaStops.AddRange(alphas.OrderBy(s => s.Location));
        return (gradient, reverse);
    }

    /// <summary>
    /// The Gradient Map ramp: luminosity 0..255 mapped along the gradient (reversed when
    /// the layer says so), with the eased segment interpolation of gradient fill layers.
    /// </summary>
    internal static ChannelLuts BuildGradientMapLut(Gradient gradient, bool reverse)
    {
        var red = new byte[256];
        var green = new byte[256];
        var blue = new byte[256];
        for (var v = 0; v < 256; v++)
        {
            var position = v / 255f;
            var (r, g, b) = gradient.Color(reverse ? 1f - position : position, endpointSmoothing: true);
            red[v] = ClampByte(r * 255f);
            green[v] = ClampByte(g * 255f);
            blue[v] = ClampByte(b * 255f);
        }

        return new ChannelLuts(red, green, blue);
    }

    /// <summary><c>vibA</c>: a version-16 descriptor with optional <c>vibrance</c> and <c>Strt</c> integers.</summary>
    internal static VibranceSettings? ParseVibrance(ReadOnlyMemory<byte> data)
    {
        if (ReadVersionedDescriptor(data) is not { } descriptor)
        {
            return null;
        }

        var vibrance = descriptor.GetNumber("vibrance", 0);
        var saturation = descriptor.GetNumber("Strt", 0);
        return new VibranceSettings((int)Math.Clamp(Math.Round(vibrance), -100, 100), (int)Math.Clamp(Math.Round(saturation), -100, 100));
    }

    /// <summary>
    /// A Photoshop binary color (the <c>Color</c> structure of the file format: u16 space
    /// and four u16 components) as sRGB in [0,1]. CMYK and gray use the naive ink formulas.
    /// </summary>
    internal static (double R, double G, double B)? BinaryColor(int space, ReadOnlySpan<ushort> c)
    {
        switch (space)
        {
            case 0:
                return (c[0] / 65535.0, c[1] / 65535.0, c[2] / 65535.0);
            case 1:
                {
                    var (r, g, b) = HsbToRgb(c[0] / 65536.0, c[1] / 65535.0, c[2] / 65535.0);
                    return (r, g, b);
                }

            case 2:
                // Components store 65535 - ink.
                return (c[0] / 65535.0 * (c[3] / 65535.0), c[1] / 65535.0 * (c[3] / 65535.0), c[2] / 65535.0 * (c[3] / 65535.0));
            case 7:
                {
                    var (r, g, b) = Imaging.ColorSpaces.LabToSrgb(c[0] / 100.0, (short)c[1] / 100.0, (short)c[2] / 100.0);
                    return (Math.Clamp(r, 0, 1), Math.Clamp(g, 0, 1), Math.Clamp(b, 0, 1));
                }

            case 8:
                {
                    // Gray components are 0..10000 of black ink.
                    var v = 1.0 - Math.Clamp(c[0] / 10000.0, 0, 1);
                    return (v, v, v);
                }

            default:
                return null;
        }
    }

    private static (double R, double G, double B) HsbToRgb(double h, double s, double v)
    {
        h = (h - Math.Floor(h)) * 6;
        var i = (int)Math.Floor(h) % 6;
        var f = h - Math.Floor(h);
        var p = v * (1 - s);
        var q = v * (1 - (s * f));
        var t = v * (1 - (s * (1 - f)));
        return i switch
        {
            0 => (v, t, p),
            1 => (q, v, p),
            2 => (p, v, t),
            3 => (p, q, v),
            4 => (t, p, v),
            _ => (v, p, q),
        };
    }

    private static Descriptor? ReadVersionedDescriptor(ReadOnlyMemory<byte> data)
    {
        if (data.Length < 4)
        {
            return null;
        }

        try
        {
            return Descriptor.ReadVersioned(new BigEndianReader(data));
        }
        catch (PsdFormatException)
        {
            return null;
        }
    }

    // ---- Math -----------------------------------------------------------------------

    /// <summary>
    /// Channel Mixer: each output is <c>(R*r + G*g + B*b)/100 + constant * 255/100</c>,
    /// rounded and clamped.
    /// </summary>
    internal static (int R, int G, int B) ChannelMixerValue(int red, int green, int blue, ChannelMixerSettings settings)
    {
        static int Mix(MixerChannel c, int r, int g, int b) =>
            RoundByte(((r * c.Red) + (g * c.Green) + (b * c.Blue)) / 100.0 + (c.Constant * 2.55));

        return (Mix(settings.Red, red, green, blue), Mix(settings.Green, red, green, blue), Mix(settings.Blue, red, green, blue));
    }

    /// <summary>
    /// Black and White: <c>gray = min + (mid - min) * secondary + (max - mid) * primary</c>,
    /// where the primary weight is the range of the largest channel (red, green or blue)
    /// and the secondary the range the two largest channels make (yellow, cyan or
    /// magenta). Neutral pixels keep their value. A tint takes the tint's hue and
    /// saturation at the gray's luminosity (Photoshop's Color blend).
    /// </summary>
    internal static (int R, int G, int B) BlackWhiteValue(int red, int green, int blue, BlackWhiteSettings settings)
    {
        var max = Math.Max(red, Math.Max(green, blue));
        var min = Math.Min(red, Math.Min(green, blue));
        var mid = red + green + blue - max - min;
        // Ties pick any matching range: the difference it weights is zero.
        var primary = max == red ? settings.Reds : max == green ? settings.Greens : settings.Blues;
        var secondary = min == blue ? settings.Yellows : min == red ? settings.Cyans : settings.Magentas;
        var gray = min + ((mid - min) * secondary / 100.0) + ((max - mid) * primary / 100.0);
        var value = RoundByte(gray);
        if (settings.Tint is not { } tint)
        {
            return (value, value, value);
        }

        var (tr, tg, tb) = SetLuminosity(tint.R, tint.G, tint.B, value);
        return (RoundByte(tr), RoundByte(tg), RoundByte(tb));
    }

    /// <summary>
    /// Photoshop's non-separable SetLum/ClipColor (the Color and Luminosity blend modes)
    /// on 0..255 values with Lum = 0.3R + 0.59G + 0.11B.
    /// </summary>
    internal static (double R, double G, double B) SetLuminosity(double red, double green, double blue, double luminosity)
    {
        var delta = luminosity - Luminosity(red, green, blue);
        var r = red + delta;
        var g = green + delta;
        var b = blue + delta;
        var l = Luminosity(r, g, b);
        var n = Math.Min(r, Math.Min(g, b));
        var x = Math.Max(r, Math.Max(g, b));
        if (n < 0 && l - n > 1e-9)
        {
            r = l + ((r - l) * l / (l - n));
            g = l + ((g - l) * l / (l - n));
            b = l + ((b - l) * l / (l - n));
        }

        if (x > 255 && x - l > 1e-9)
        {
            r = l + ((r - l) * (255 - l) / (x - l));
            g = l + ((g - l) * (255 - l) / (x - l));
            b = l + ((b - l) * (255 - l) / (x - l));
        }

        return (r, g, b);
    }

    private static double Luminosity(double r, double g, double b) => (0.3 * r) + (0.59 * g) + (0.11 * b);

    /// <summary>
    /// Selective Color. Range weights follow the pixel's channel order: reds, greens and
    /// blues by <c>max - mid</c> when that channel is the largest; yellows, cyans and
    /// magentas by <c>mid - min</c> when blue, red or green is the smallest; whites by
    /// <c>2 * (min - 128)</c>, blacks by <c>2 * (128 - max)</c>, neutrals by
    /// <c>255 - |max - 128| - |min - 128|</c>. Each channel's ink (cyan for red and so on)
    /// and black then move it by <c>((-1 - ink) * black - ink)</c>, scaled by the
    /// channel's ink amount <c>1 - v</c> in relative mode.
    /// </summary>
    internal static (int R, int G, int B) SelectiveColorValue(int red, int green, int blue, SelectiveColorSettings settings)
    {
        Span<int> rgb = [red, green, blue];
        var max = Math.Max(red, Math.Max(green, blue));
        var min = Math.Min(red, Math.Min(green, blue));
        var mid = red + green + blue - max - min;
        Span<double> weights = stackalloc double[9];
        if (max != min)
        {
            // Reds 0, Yellows 1, Greens 2, Cyans 3, Blues 4, Magentas 5.
            var top = max == red ? 0 : max == green ? 1 : 2;
            var bottom = min == blue ? 2 : min == red ? 0 : 1;
            weights[top * 2] = (max - mid) / 255.0;
            weights[bottom switch { 2 => 1, 0 => 3, _ => 5 }] = (mid - min) / 255.0;
        }

        weights[6] = min > 128 ? (min - 128) * 2 / 255.0 : 0;
        weights[7] = Math.Max(0, 255 - Math.Abs(max - 128) - Math.Abs(min - 128)) / 255.0;
        weights[8] = max < 128 ? (128 - max) * 2 / 255.0 : 0;

        Span<double> result = stackalloc double[3];
        for (var c = 0; c < 3; c++)
        {
            var v = rgb[c] / 255.0;
            var delta = 0.0;
            for (var i = 0; i < 9; i++)
            {
                var range = settings.Ranges[i];
                if (weights[i] <= 0 || !range.HasEffect)
                {
                    continue;
                }

                var ink = (c switch { 0 => range.Cyan, 1 => range.Magenta, _ => range.Yellow }) / 100.0;
                var black = range.Black / 100.0;
                var change = ((-1 - ink) * black) - ink;
                if (!settings.Absolute)
                {
                    change *= 1 - v;
                }

                delta += Math.Clamp(change, -v, 1 - v) * weights[i];
            }

            result[c] = (v + delta) * 255.0;
        }

        return (RoundByte(result[0]), RoundByte(result[1]), RoundByte(result[2]));
    }

    /// <summary>
    /// Photo Filter: a gel in front of the lens. Each channel is multiplied in linear light
    /// by the filter color at the density, <c>lin(c) * (1 - d + d * lin(filter))</c>; with
    /// Preserve Luminosity, SetLum then restores the original luminosity.
    /// </summary>
    internal static (int R, int G, int B) PhotoFilterValue(int red, int green, int blue, PhotoFilterSettings settings) =>
        new PhotoFilterMap(settings).Map(red, green, blue);

    /// <summary>The linear-light filter of one channel as a 256-entry table on the 0..255 scale.</summary>
    private static double[] PhotoFilterTable(double filter, double density)
    {
        var gain = 1 - density + (density * Gradient.SrgbToLinear(filter));
        var table = new double[256];
        for (var v = 0; v < 256; v++)
        {
            table[v] = 255.0 * Imaging.ColorSpaces.EncodeSrgb(Gradient.SrgbToLinear(v / 255.0) * gain);
        }

        return table;
    }

    /// <summary>
    /// Vibrance: a saturation change weighted toward less saturated colors. Each
    /// non-maximum channel moves toward (or away from) the maximum by
    /// <c>vibrance * (1 - s)</c>, where <c>s = (max - min)/max</c>; the Saturation slider
    /// then scales the distance from the luminosity uniformly.
    /// </summary>
    internal static (int R, int G, int B) VibranceValue(int red, int green, int blue, VibranceSettings settings)
    {
        double r = red;
        double g = green;
        double b = blue;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        if (settings.Vibrance != 0 && max > 0 && max != min)
        {
            var amount = settings.Vibrance / 100.0 * (1 - ((max - min) / max));
            r += (r - max) * amount;
            g += (g - max) * amount;
            b += (b - max) * amount;
        }

        if (settings.Saturation != 0)
        {
            var l = Luminosity(r, g, b);
            var scale = 1 + (settings.Saturation / 100.0);
            r = l + ((r - l) * scale);
            g = l + ((g - l) * scale);
            b = l + ((b - l) * scale);
        }

        return (RoundByte(r), RoundByte(g), RoundByte(b));
    }

    /// <summary>A per-pixel color map on bytes.</summary>
    internal interface IPixelMap
    {
        (int R, int G, int B) Map(int red, int green, int blue);
    }

    /// <summary>
    /// Runs a struct <see cref="IPixelMap"/> over a row: one specialized loop per map, no
    /// per-pixel dispatch. Flat areas repeat colors, so the last mapping is remembered.
    /// </summary>
    internal sealed class PixelAdjustment<TMap>(TMap map) : IAdjustment
        where TMap : struct, IPixelMap
    {
        private readonly TMap _map = map;

        public void Apply(Span<float> r, Span<float> g, Span<float> b)
        {
            var lastIn = -1;
            var lastOut = (0f, 0f, 0f);
            for (var i = 0; i < r.Length; i++)
            {
                var red = Byte(r[i]);
                var green = Byte(g[i]);
                var blue = Byte(b[i]);
                var key = (red << 16) | (green << 8) | blue;
                if (key != lastIn)
                {
                    var (or, og, ob) = _map.Map(red, green, blue);
                    lastOut = (or / 255f, og / 255f, ob / 255f);
                    lastIn = key;
                }

                (r[i], g[i], b[i]) = lastOut;
            }
        }
    }

    internal readonly struct ChannelMixerMap(ChannelMixerSettings settings) : IPixelMap
    {
        public (int R, int G, int B) Map(int red, int green, int blue) => ChannelMixerValue(red, green, blue, settings);
    }

    internal readonly struct BlackWhiteMap(BlackWhiteSettings settings) : IPixelMap
    {
        public (int R, int G, int B) Map(int red, int green, int blue) => BlackWhiteValue(red, green, blue, settings);
    }

    internal readonly struct PhotoFilterMap(PhotoFilterSettings settings) : IPixelMap
    {
        private readonly double[] _red = PhotoFilterTable(settings.Red, settings.Density / 100.0);
        private readonly double[] _green = PhotoFilterTable(settings.Green, settings.Density / 100.0);
        private readonly double[] _blue = PhotoFilterTable(settings.Blue, settings.Density / 100.0);
        private readonly bool _preserve = settings.PreserveLuminosity;

        public (int R, int G, int B) Map(int red, int green, int blue)
        {
            var r = _red[red];
            var g = _green[green];
            var b = _blue[blue];
            if (_preserve)
            {
                (r, g, b) = SetLuminosity(r, g, b, Luminosity(red, green, blue));
            }

            return (RoundByte(r), RoundByte(g), RoundByte(b));
        }
    }

    internal readonly struct VibranceMap(VibranceSettings settings) : IPixelMap
    {
        public (int R, int G, int B) Map(int red, int green, int blue) => VibranceValue(red, green, blue, settings);
    }

    /// <summary>Gradient Map: the pixel's luminosity (Threshold's 30/59/11) picks a color from a 256-entry ramp.</summary>
    internal readonly struct GradientMapMap(byte[] red, byte[] green, byte[] blue) : IPixelMap
    {
        public (int R, int G, int B) Map(int r, int g, int b)
        {
            var index = ThresholdLuminance(r, g, b);
            return (red[index], green[index], blue[index]);
        }
    }
}
