using XRay.Psd.Descriptors;
using XRay.Psd.IO;
using XRay.Psd.Layers;

namespace XRay.Psd.Rendering;

/// <summary>One Levels record (<c>LevelsRecord</c> in .reference/src/core/adjustment_layer.hpp).</summary>
internal readonly record struct LevelsRecord(int BlackInput = 0, int WhiteInput = 255, int GammaPercent = 100, int BlackOutput = 0, int WhiteOutput = 255)
{
    public static LevelsRecord Identity => new(0, 255, 100, 0, 255);

    /// <summary><c>clamp_levels_record</c>: the ranges the dialogs and the codec share.</summary>
    public LevelsRecord Clamped()
    {
        var blackInput = Math.Clamp(BlackInput, 0, 254);
        var blackOutput = Math.Clamp(BlackOutput, 0, 255);
        return new LevelsRecord(
            blackInput,
            Math.Clamp(WhiteInput, blackInput + 1, 255),
            Math.Clamp(GammaPercent, 10, 999),
            blackOutput,
            Math.Clamp(WhiteOutput, blackOutput, 255));
    }

    public bool HasEffect
    {
        get
        {
            var r = Clamped();
            return r.BlackInput != 0 || r.WhiteInput != 255 || r.GammaPercent != 100 || r.BlackOutput != 0 || r.WhiteOutput != 255;
        }
    }
}

/// <summary>Composite (master) and per-channel Levels records.</summary>
internal sealed record LevelsSettings(LevelsRecord Master, LevelsRecord Red, LevelsRecord Green, LevelsRecord Blue)
{
    public static LevelsSettings Identity => new(LevelsRecord.Identity, LevelsRecord.Identity, LevelsRecord.Identity, LevelsRecord.Identity);
}

/// <summary>A Curves control point, 0..255 on both axes.</summary>
internal readonly record struct CurvePoint(int Input, int Output);

/// <summary>Composite and per-channel Curves control points (<c>CurvesAdjustment</c>).</summary>
internal sealed record CurvesSettings(CurvePoint[] Rgb, CurvePoint[] Red, CurvePoint[] Green, CurvePoint[] Blue)
{
    public static CurvePoint[] IdentityPoints => [new(0, 0), new(255, 255)];

    public static CurvesSettings Identity => new(IdentityPoints, IdentityPoints, IdentityPoints, IdentityPoints);
}

/// <summary>One of Photoshop's six per-hue-range bands (<c>HueSaturationBand</c>).</summary>
internal readonly record struct HueSaturationBand(int OuterStart, int InnerStart, int InnerEnd, int OuterEnd, int Hue, int Saturation, int Lightness)
{
    public bool HasEffect => Hue != 0 || Saturation != 0 || Lightness != 0;
}

/// <summary>Hue/Saturation settings (<c>HueSaturationAdjustment</c>).</summary>
internal sealed record HueSaturationSettings
{
    public int Hue { get; init; }

    public int Saturation { get; init; }

    public int Lightness { get; init; }

    public bool Colorize { get; init; }

    /// <summary>0..360, the model's convention (the file stores -180..180).</summary>
    public int ColorizeHue { get; init; }

    public int ColorizeSaturation { get; init; } = 25;

    public int ColorizeLightness { get; init; }

    public HueSaturationBand[] Bands { get; init; } = Adjustments.DefaultHueSaturationBands();
}

/// <summary>Color Balance midtones (the only range the reference renders).</summary>
internal readonly record struct ColorBalanceSettings(int CyanRed, int MagentaGreen, int YellowBlue);

/// <summary>Brightness/Contrast; legacy ranges -100..100, modern brightness -150..150 and contrast -50..100.</summary>
internal readonly record struct BrightnessContrastSettings(int Brightness, int Contrast, bool UseLegacy);

/// <summary>Exposure at Photoshop's field precision: stops / 100, offset / 10000, gamma / 100.</summary>
internal readonly record struct ExposureSettings(int ExposureHundredths, int OffsetTenThousandths, int GammaHundredths)
{
    public ExposureSettings Clamped() => new(
        Math.Clamp(ExposureHundredths, -2000, 2000),
        Math.Clamp(OffsetTenThousandths, -5000, 5000),
        Math.Clamp(GammaHundredths, 1, 999));
}

/// <summary>Adjustment payload parsers, ported from .reference/src/psd/psd_adjustments.cpp.</summary>
internal static partial class Adjustments
{
    private const int LevelsRecordCount = 29;

    internal static int? ParseThreshold(ReadOnlySpan<byte> data) =>
        data.Length >= 2 ? Math.Clamp((data[0] << 8) | data[1], 1, 255) : null;

    internal static int? ParsePosterize(ReadOnlySpan<byte> data) =>
        data.Length >= 2 ? Math.Clamp((data[0] << 8) | data[1], 2, 255) : null;

    /// <summary>
    /// <c>levl</c>: u16 version 2, then 29 records of five u16 (black in, white in, black
    /// out, white out, gamma percent). Records 0..3 are composite, red, green, blue; record
    /// 4 is a CMYK document's black ink, which the RGB math never reads.
    /// </summary>
    internal static LevelsSettings? ParseLevels(ReadOnlySpan<byte> data)
    {
        var reader = new BigEndianReader(data.ToArray());
        if (reader.ReadUInt16() != 2 || reader.Remaining < LevelsRecordCount * 10)
        {
            return null;
        }

        var records = new LevelsRecord[4];
        for (var i = 0; i < records.Length; i++)
        {
            var blackInput = reader.ReadUInt16();
            var whiteInput = reader.ReadUInt16();
            var blackOutput = reader.ReadUInt16();
            var whiteOutput = reader.ReadUInt16();
            var gamma = reader.ReadUInt16();
            records[i] = new LevelsRecord(blackInput, whiteInput, gamma, blackOutput, whiteOutput).Clamped();
        }

        return new LevelsSettings(records[0], records[1], records[2], records[3]);
    }

    /// <summary><c>curv</c>: one zero byte, then the ACV body (<see cref="ReadAcv"/>).</summary>
    internal static CurvesSettings? ParseCurves(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty || data[0] != 0)
        {
            return null;
        }

        return ReadAcv(data[1..]);
    }

    /// <summary>
    /// <c>hue2</c> (and the Photoshop 4 <c>hue </c> key, which Adobe documents with the
    /// same layout): u16 version 2, colorize u8, pad, colorize h/s/l and master h/s/l as
    /// i16, then six band records of four i16 range stops and an i16 h/s/l triple.
    /// Payloads that stop after the 16-byte header keep the default hextants.
    /// </summary>
    internal static HueSaturationSettings? ParseHueSaturation(ReadOnlySpan<byte> data)
    {
        var reader = new BigEndianReader(data.ToArray());
        if (reader.ReadUInt16() != 2 || reader.Remaining < 14)
        {
            return null;
        }

        var colorize = reader.ReadByte() != 0;
        reader.Skip(1);
        var colorizeHue = Wrap360(reader.ReadInt16());
        var colorizeSaturation = Math.Clamp((int)reader.ReadInt16(), 0, 100);
        var colorizeLightness = Math.Clamp((int)reader.ReadInt16(), -100, 100);
        var hue = Math.Clamp((int)reader.ReadInt16(), -180, 180);
        var saturation = Math.Clamp((int)reader.ReadInt16(), -100, 100);
        var lightness = Math.Clamp((int)reader.ReadInt16(), -100, 100);
        var bands = DefaultHueSaturationBands();
        if (reader.Remaining >= 14 * bands.Length)
        {
            for (var i = 0; i < bands.Length; i++)
            {
                bands[i] = new HueSaturationBand(
                    Wrap360(reader.ReadInt16()),
                    Wrap360(reader.ReadInt16()),
                    Wrap360(reader.ReadInt16()),
                    Wrap360(reader.ReadInt16()),
                    Math.Clamp((int)reader.ReadInt16(), -180, 180),
                    Math.Clamp((int)reader.ReadInt16(), -100, 100),
                    Math.Clamp((int)reader.ReadInt16(), -100, 100));
            }
        }

        return new HueSaturationSettings
        {
            Colorize = colorize,
            ColorizeHue = colorizeHue,
            ColorizeSaturation = colorizeSaturation,
            ColorizeLightness = colorizeLightness,
            Hue = hue,
            Saturation = saturation,
            Lightness = lightness,
            Bands = bands,
        };
    }

    /// <summary>
    /// <c>blnc</c>: shadows, midtones and highlights as three i16 triples plus a Preserve
    /// Luminosity byte. The reference models the midtones triple only (the shadows,
    /// highlights and luminosity bytes are preserved but not rendered).
    /// </summary>
    internal static ColorBalanceSettings? ParseColorBalance(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12)
        {
            return null;
        }

        return new ColorBalanceSettings(ReadClampedInt16(data, 6, 100), ReadClampedInt16(data, 8, 100), ReadClampedInt16(data, 10, 100));
    }

    /// <summary>
    /// Brightness/Contrast: a parseable <c>CgEd</c> descriptor is authoritative (modern
    /// Photoshop writes an all-zero compatibility <c>brit</c> beside it); a file with
    /// <c>brit</c> alone is the CS-era legacy record.
    /// </summary>
    private static BrightnessContrastSettings? ResolveBrightnessContrast(PsdLayer layer, ReadOnlySpan<byte> brit)
    {
        if (layer.GetTaggedBlock("CgEd") is { } descriptor && ParseBrightnessContrastDescriptor(descriptor.Data) is { } modern)
        {
            return modern;
        }

        return ParseBrightnessContrastLegacy(brit);
    }

    internal static BrightnessContrastSettings? ParseBrightnessContrastLegacy(ReadOnlySpan<byte> data) =>
        data.Length >= 4 ? new BrightnessContrastSettings(ReadClampedInt16(data, 0, 100), ReadClampedInt16(data, 2, 100), UseLegacy: true) : null;

    /// <summary>
    /// The <c>CgEd</c> descriptor: u32 version 16, then a descriptor whose <c>Brgh</c> and
    /// <c>Cntr</c> must be integers; <c>useLegacy</c> is optional and defaults to false.
    /// </summary>
    internal static BrightnessContrastSettings? ParseBrightnessContrastDescriptor(ReadOnlyMemory<byte> data)
    {
        if (data.Length < 4)
        {
            return null;
        }

        Descriptor descriptor;
        try
        {
            descriptor = Descriptor.ReadVersioned(new BigEndianReader(data));
        }
        catch (PsdFormatException)
        {
            return null;
        }

        if (descriptor["Brgh"] is not { Type: DescriptorValueType.Integer } brightness ||
            descriptor["Cntr"] is not { Type: DescriptorValueType.Integer } contrast)
        {
            return null;
        }

        var useLegacy = descriptor["useLegacy"] is { Type: DescriptorValueType.Boolean } legacy && legacy.Boolean;
        var b = (int)Math.Clamp(brightness.Integer, int.MinValue, int.MaxValue);
        var c = (int)Math.Clamp(contrast.Integer, int.MinValue, int.MaxValue);
        return useLegacy
            ? new BrightnessContrastSettings(Math.Clamp(b, -100, 100), Math.Clamp(c, -100, 100), true)
            : new BrightnessContrastSettings(Math.Clamp(b, -150, 150), Math.Clamp(c, -50, 100), false);
    }

    /// <summary>
    /// <c>expA</c>: u16 version 1, then float32 exposure, offset and gamma. Values are
    /// rounded to Photoshop's field precision after clamping as doubles.
    /// </summary>
    internal static ExposureSettings? ParseExposure(ReadOnlySpan<byte> data)
    {
        if (data.Length < 14)
        {
            return null;
        }

        var reader = new BigEndianReader(data.ToArray());
        if (reader.ReadUInt16() != 1)
        {
            return null;
        }

        var exposure = reader.ReadSingle();
        var offset = reader.ReadSingle();
        var gamma = reader.ReadSingle();
        if (!float.IsFinite(exposure) || !float.IsFinite(offset) || !float.IsFinite(gamma))
        {
            return null;
        }

        static int Scaled(float value, double scale, int low, int high) =>
            (int)Math.Round(Math.Clamp(value * scale, low, high), MidpointRounding.AwayFromZero);

        return new ExposureSettings(Scaled(exposure, 100.0, -2000, 2000), Scaled(offset, 10000.0, -5000, 5000), Scaled(gamma, 100.0, 1, 999));
    }

    internal static HueSaturationBand[] DefaultHueSaturationBands() =>
    [
        new(315, 345, 15, 45, 0, 0, 0),
        new(15, 45, 75, 105, 0, 0, 0),
        new(75, 105, 135, 165, 0, 0, 0),
        new(135, 165, 195, 225, 0, 0, 0),
        new(195, 225, 255, 285, 0, 0, 0),
        new(255, 285, 315, 345, 0, 0, 0),
    ];

    private static int Wrap360(int value) => ((value % 360) + 360) % 360;

    private static int ReadClampedInt16(ReadOnlySpan<byte> data, int offset, int limit) =>
        Math.Clamp((int)(short)((data[offset] << 8) | data[offset + 1]), -limit, limit);
}
