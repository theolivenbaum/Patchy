namespace XRay.Psd;

/// <summary>Maps PSD blend-mode keys (layer records and descriptor enums) to <see cref="PsdBlendMode"/>.</summary>
public static class BlendModeKeys
{
    /// <summary>Maps a four-character layer-record key such as <c>norm</c> or <c>mul </c>.</summary>
    public static PsdBlendMode FromKey(string key) => key switch
    {
        "norm" or "Nrml" => PsdBlendMode.Normal,
        "pass" => PsdBlendMode.PassThrough,
        "diss" or "Dslv" => PsdBlendMode.Dissolve,
        "dark" or "Drkn" => PsdBlendMode.Darken,
        "mul " or "Mltp" => PsdBlendMode.Multiply,
        "idiv" or "CBrn" => PsdBlendMode.ColorBurn,
        "lbrn" => PsdBlendMode.LinearBurn,
        "dkCl" => PsdBlendMode.DarkerColor,
        "lite" or "Lghn" => PsdBlendMode.Lighten,
        "scrn" or "Scrn" => PsdBlendMode.Screen,
        "div " or "CDdg" => PsdBlendMode.ColorDodge,
        "lddg" => PsdBlendMode.LinearDodge,
        "lgCl" => PsdBlendMode.LighterColor,
        "over" or "Ovrl" => PsdBlendMode.Overlay,
        "sLit" or "SftL" => PsdBlendMode.SoftLight,
        "hLit" or "HrdL" => PsdBlendMode.HardLight,
        "vLit" => PsdBlendMode.VividLight,
        "lLit" => PsdBlendMode.LinearLight,
        "pLit" => PsdBlendMode.PinLight,
        "hMix" => PsdBlendMode.HardMix,
        "diff" or "Dfrn" => PsdBlendMode.Difference,
        "smud" or "Xclu" => PsdBlendMode.Exclusion,
        "fsub" => PsdBlendMode.Subtract,
        "fdiv" => PsdBlendMode.Divide,
        "hue " or "H   " => PsdBlendMode.Hue,
        "sat " or "Strt" => PsdBlendMode.Saturation,
        "colr" or "Clr " => PsdBlendMode.Color,
        "lum " or "Lmns" => PsdBlendMode.Luminosity,
        _ => PsdBlendMode.Normal,
    };

    /// <summary>Maps a descriptor <c>BlnM</c> enum value (string IDs such as <c>linearDodge</c> or char IDs such as <c>Mltp</c>).</summary>
    public static PsdBlendMode FromDescriptorEnum(string value) => value switch
    {
        "normal" => PsdBlendMode.Normal,
        "passThrough" => PsdBlendMode.PassThrough,
        "dissolve" => PsdBlendMode.Dissolve,
        "darken" => PsdBlendMode.Darken,
        "multiply" => PsdBlendMode.Multiply,
        "colorBurn" => PsdBlendMode.ColorBurn,
        "linearBurn" => PsdBlendMode.LinearBurn,
        "darkerColor" => PsdBlendMode.DarkerColor,
        "lighten" => PsdBlendMode.Lighten,
        "screen" => PsdBlendMode.Screen,
        "colorDodge" => PsdBlendMode.ColorDodge,
        "linearDodge" => PsdBlendMode.LinearDodge,
        "lighterColor" => PsdBlendMode.LighterColor,
        "overlay" => PsdBlendMode.Overlay,
        "softLight" => PsdBlendMode.SoftLight,
        "hardLight" => PsdBlendMode.HardLight,
        "vividLight" => PsdBlendMode.VividLight,
        "linearLight" => PsdBlendMode.LinearLight,
        "pinLight" => PsdBlendMode.PinLight,
        "hardMix" => PsdBlendMode.HardMix,
        "difference" => PsdBlendMode.Difference,
        "exclusion" => PsdBlendMode.Exclusion,
        "blendSubtraction" => PsdBlendMode.Subtract,
        "blendDivide" => PsdBlendMode.Divide,
        "hue" => PsdBlendMode.Hue,
        "saturation" => PsdBlendMode.Saturation,
        "color" => PsdBlendMode.Color,
        "luminosity" => PsdBlendMode.Luminosity,
        _ => FromKey(value),
    };
}
