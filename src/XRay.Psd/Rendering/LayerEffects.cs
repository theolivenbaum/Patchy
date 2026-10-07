using XRay.Psd.Descriptors;

namespace XRay.Psd.Rendering;

internal enum StrokePosition
{
    Outside,
    Inside,
    Center,
}

internal sealed record ShadowEffect(PsdBlendMode Mode, PsdColor Color, float Opacity, float AngleDegrees, float Distance, float SpreadOrChoke, float Size, bool LayerConceals);

internal sealed record GlowEffect(PsdBlendMode Mode, PsdColor Color, float Opacity, float SpreadOrChoke, float Size, float Range, bool CenterSource, bool Precise, Gradient? Gradient);

/// <summary>A color, gradient or pattern overlay; <paramref name="Dither"/> is the gradient's <c>Dthr</c> flag.</summary>
internal sealed record OverlayEffect(PsdBlendMode Mode, PsdColor Color, float Opacity, Gradient? Gradient, PatternPlacement? Pattern = null, bool Dither = false);

/// <summary>A stroke (<c>FrFX</c>); <paramref name="Dither"/> is the gradient paint's <c>Dthr</c> flag.</summary>
internal sealed record StrokeEffect(PsdBlendMode Mode, PsdColor Color, float Opacity, float Size, StrokePosition Position, bool Overprint, Gradient? Gradient, bool Dither = false);

internal enum BevelStyle
{
    InnerBevel,
    OuterBevel,
    Emboss,
    PillowEmboss,
    StrokeEmboss,
}

internal enum BevelTechnique
{
    Smooth,
    ChiselHard,
    ChiselSoft,
}

internal sealed record BevelEffect(
    PsdBlendMode HighlightMode, PsdColor HighlightColor, float HighlightOpacity,
    PsdBlendMode ShadowMode, PsdColor ShadowColor, float ShadowOpacity,
    float AngleDegrees, float AltitudeDegrees, float Depth, float Size, bool DirectionUp,
    BevelStyle Style, BevelTechnique Technique, float Soften,
    StyleContour Gloss, bool GlossAntiAliased,
    StyleContour? Contour, bool ContourAntiAliased, float ContourRange,
    PatternPlacement? Texture, bool TextureInvert, float TextureDepth);

internal sealed record SatinEffect(PsdBlendMode Mode, PsdColor Color, float Opacity, float AngleDegrees, float Distance, float Size, bool Invert);

/// <summary>
/// The enabled layer effects of one layer, parsed from its <c>lfx2</c>/<c>lmfx</c>
/// descriptor with the defaults and clamps of the reference
/// (.reference/src/psd/psd_layer_styles.cpp).
/// </summary>
internal sealed class LayerEffects
{
    public List<ShadowEffect> DropShadows { get; } = [];

    public List<ShadowEffect> InnerShadows { get; } = [];

    public List<GlowEffect> OuterGlows { get; } = [];

    public List<GlowEffect> InnerGlows { get; } = [];

    /// <summary>Color and gradient overlays in Photoshop's bottom-to-top order (gradient under color).</summary>
    public List<OverlayEffect> Overlays { get; } = [];

    public List<StrokeEffect> Strokes { get; } = [];

    public List<SatinEffect> Satins { get; } = [];

    public List<BevelEffect> Bevels { get; } = [];

    /// <summary>"Layer Mask Hides Effects" (<c>lmgm</c>) or "Vector Mask Hides Effects" (<c>vmgm</c>).</summary>
    public bool MaskHidesEffects { get; set; }

    public bool HasExterior => DropShadows.Count > 0 || OuterGlows.Count > 0;

    public bool IsEmpty => DropShadows.Count == 0 && InnerShadows.Count == 0 && OuterGlows.Count == 0 && InnerGlows.Count == 0 && Overlays.Count == 0 && Strokes.Count == 0 && Satins.Count == 0 && Bevels.Count == 0;

    public static LayerEffects? Parse(Descriptor root, float globalAngle, float globalAltitude = 30f)
    {
        if (!root.GetBoolean("masterFXSwitch", true))
        {
            return null;
        }

        var effects = new LayerEffects();
        var patterns = new List<OverlayEffect>();
        var gradients = new List<OverlayEffect>();
        var colors = new List<OverlayEffect>();
        foreach (var (key, value) in root.Items)
        {
            foreach (var effect in Instances(key, value))
            {
                if (!effect.Descriptor.GetBoolean("enab"))
                {
                    continue;
                }

                var d = effect.Descriptor;
                switch (effect.Kind)
                {
                    case "DrSh":
                        effects.DropShadows.Add(new ShadowEffect(
                            Mode(d, "mul "), d.GetColor("Clr ") ?? PsdColor.Black, Percent(d, "Opct", 75),
                            Angle(d, globalAngle), Math.Max(0, (float)d.GetNumber("Dstn", 5)),
                            Math.Clamp((float)d.GetNumber("Ckmt", 0), 0, 100), Math.Max(0, (float)d.GetNumber("blur", 5)),
                            d.GetBoolean("layerConceals", true)));
                        break;
                    case "IrSh":
                        effects.InnerShadows.Add(new ShadowEffect(
                            Mode(d, "mul "), d.GetColor("Clr ") ?? PsdColor.Black, Percent(d, "Opct", 75),
                            Angle(d, globalAngle), Math.Max(0, (float)d.GetNumber("Dstn", 5)),
                            Math.Clamp((float)d.GetNumber("Ckmt", 0), 0, 100), Math.Max(0, (float)d.GetNumber("blur", 5)), false));
                        break;
                    case "OrGl":
                    case "IrGl":
                        {
                            var glow = new GlowEffect(
                                Mode(d, "scrn"), d.GetColor("Clr ") ?? new PsdColor(255, 255, 190), Percent(d, "Opct", 75),
                                Math.Clamp((float)d.GetNumber("Ckmt", 0), 0, 100), Math.Max(0, (float)d.GetNumber("blur", 5)),
                                Math.Clamp((float)d.GetNumber("Inpr", 100), 1, 100), d.GetEnum("glwS") == "SrcC",
                                d.GetEnum("GlwT") == "PrBL",
                                null); // Gradient glows render with their solid color, as in the reference (docs/rendering.md).
                            (effect.Kind == "OrGl" ? effects.OuterGlows : effects.InnerGlows).Add(glow);
                            break;
                        }

                    case "SoFi":
                        colors.Add(new OverlayEffect(Mode(d, "norm"), d.GetColor("Clr ") ?? new PsdColor(255, 0, 0), Percent(d, "Opct", 100), null));
                        break;
                    case "patternFill":
                        if (PatternPlacement.FromDescriptor(d) is { } placement)
                        {
                            patterns.Add(new OverlayEffect(Mode(d, "norm"), PsdColor.Black, Percent(d, "Opct", 100), null, placement));
                        }

                        break;
                    case "GrFl":
                        gradients.Add(new OverlayEffect(Mode(d, "norm"), PsdColor.Black, Percent(d, "Opct", 100), Gradient.FromDescriptor(d), Dither: d.GetBoolean("Dthr")));
                        break;
                    case "FrFX":
                        effects.Strokes.Add(new StrokeEffect(
                            Mode(d, "norm"), d.GetColor("Clr ") ?? PsdColor.Black, Percent(d, "Opct", 100),
                            Math.Max(1, (float)d.GetNumber("Sz  ", 3)),
                            d.GetEnum("Styl") switch { "InsF" => StrokePosition.Inside, "CtrF" => StrokePosition.Center, _ => StrokePosition.Outside },
                            d.GetBoolean("overprint"),
                            d.GetEnum("PntT") == "GrFl" ? Gradient.FromDescriptor(d) : null,
                            d.GetBoolean("Dthr")));
                        break;
                    case "ebbl":
                        {
                            var global = d.GetBoolean("uglg");
                            var style = d.GetEnum("bvlS") switch
                            {
                                "OtrB" => BevelStyle.OuterBevel,
                                "Embs" => BevelStyle.Emboss,
                                "PlEb" => BevelStyle.PillowEmboss,
                                "strokeEmboss" => BevelStyle.StrokeEmboss,
                                _ => BevelStyle.InnerBevel,
                            };
                            var technique = d.GetEnum("bvlT") switch
                            {
                                "PrBL" => BevelTechnique.ChiselHard,
                                "Slmt" => BevelTechnique.ChiselSoft,
                                _ => BevelTechnique.Smooth,
                            };
                            var useContour = d.GetBoolean("useShape");
                            var useTexture = d.GetBoolean("useTexture");
                            PatternPlacement? texture = null;
                            if (useTexture && PatternPlacement.FromDescriptor(d) is { } texturePlacement)
                            {
                                // Texture has no angle; its scale and phase follow the effect keys.
                                texture = texturePlacement with { AngleDegrees = 0 };
                            }

                            effects.Bevels.Add(new BevelEffect(
                                Mode(d, "hglM", "scrn"), d.GetColor("hglC") ?? PsdColor.White, Percent(d, "hglO", 75),
                                Mode(d, "sdwM", "mul "), d.GetColor("sdwC") ?? PsdColor.Black, Percent(d, "sdwO", 75),
                                global ? globalAngle : (float)d.GetNumber("lagl", 120),
                                global ? globalAltitude : (float)d.GetNumber("Lald", 30),
                                Math.Max(0.01f, (float)(d.GetNumber("srgR", 100) / 100)),
                                Math.Max(1f, (float)d.GetNumber("blur", 5)),
                                d.GetEnum("bvlD") != "Out ",
                                style,
                                technique,
                                Math.Max(0f, (float)d.GetNumber("Sftn", 0)),
                                StyleContour.FromDescriptor(d.GetObject("TrnS")),
                                d.GetBoolean("antialiasGloss"),
                                useContour ? StyleContour.FromDescriptor(d.GetObject("MpgS")) : null,
                                d.GetBoolean("AntA"),
                                Math.Clamp((float)(d.GetNumber("Inpr", 50) / 100), 0f, 1f),
                                texture,
                                d.GetBoolean("InvT"),
                                Math.Clamp((float)(d.GetNumber("textureDepth", 100) / 100), -10f, 10f)));
                            break;
                        }

                    case "ChFX":
                        effects.Satins.Add(new SatinEffect(
                            Mode(d, "mul "), d.GetColor("Clr ") ?? PsdColor.Black, Percent(d, "Opct", 50),
                            (float)d.GetNumber("lagl", 19), Math.Max(0, (float)d.GetNumber("Dstn", 11)),
                            Math.Max(0, (float)d.GetNumber("blur", 14)), d.GetBoolean("Invr", true)));
                        break;
                }
            }
        }

        effects.Overlays.AddRange(patterns);
        effects.Overlays.AddRange(gradients);
        effects.Overlays.AddRange(colors);
        return effects.IsEmpty ? null : effects;
    }

    private static IEnumerable<(string Kind, Descriptor Descriptor)> Instances(string key, DescriptorValue value)
    {
        var kind = key switch
        {
            "dropShadowMulti" => "DrSh",
            "innerShadowMulti" => "IrSh",
            "outerGlowMulti" => "OrGl",
            "innerGlowMulti" => "IrGl",
            "solidFillMulti" => "SoFi",
            "gradientFillMulti" => "GrFl",
            "frameFXMulti" => "FrFX",
            "chromeFXMulti" => "ChFX",
            "patternFillMulti" => "patternFill",
            "bevelEmbossMulti" => "ebbl",
            _ => key,
        };
        if (value.Type == DescriptorValueType.List && value.List is not null)
        {
            foreach (var item in value.List)
            {
                if (item.Object is not null)
                {
                    yield return (kind, item.Object);
                }
            }
        }
        else if (value.Object is not null)
        {
            yield return (kind, value.Object);
        }
    }

    private static PsdBlendMode Mode(Descriptor d, string fallback) => Mode(d, "Md  ", fallback);

    private static PsdBlendMode Mode(Descriptor d, string key, string fallback)
    {
        var mode = d.GetEnum(key);
        return mode is null ? BlendModeKeys.FromKey(fallback) : BlendModeKeys.FromDescriptorEnum(mode);
    }

    private static float Percent(Descriptor d, string key, double fallback) => Math.Clamp((float)(d.GetNumber(key, fallback) / 100), 0f, 1f);

    private static float Angle(Descriptor d, float globalAngle) => d.GetBoolean("uglg") ? globalAngle : (float)d.GetNumber("lagl", 120);
}
