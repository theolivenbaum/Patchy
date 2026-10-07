using XRay.Psd.Descriptors;
using XRay.Psd.Rendering;

namespace XRay.Psd.Layers;

/// <summary>The kind of a layer effect in the Layer Style dialog.</summary>
public enum PsdLayerEffectKind
{
    DropShadow,
    InnerShadow,
    OuterGlow,
    InnerGlow,
    BevelEmboss,
    Satin,
    ColorOverlay,
    GradientOverlay,
    PatternOverlay,
    Stroke,
}

/// <summary>Where a stroke effect sits relative to the layer edge.</summary>
public enum PsdStrokePosition
{
    Outside,
    Inside,
    Center,
}

/// <summary>Bevel and Emboss style.</summary>
public enum PsdBevelStyle
{
    InnerBevel,
    OuterBevel,
    Emboss,
    PillowEmboss,
    StrokeEmboss,
}

/// <summary>Bevel and Emboss technique.</summary>
public enum PsdBevelTechnique
{
    Smooth,
    ChiselHard,
    ChiselSoft,
}

/// <summary>
/// One layer effect as the layer compositor reads it: the values carry the
/// same defaults and clamps as rendering, and the angle already resolves "Use
/// Global Light". Properties that do not apply to <see cref="Kind"/> are null.
/// </summary>
public sealed record PsdLayerEffect
{
    public PsdLayerEffectKind Kind { get; init; }

    /// <summary>The effect's own check box. Disabled effects are listed but not rendered.</summary>
    public bool Enabled { get; init; }

    /// <summary>Blend mode (the highlight mode for Bevel and Emboss).</summary>
    public PsdBlendMode BlendMode { get; init; }

    /// <summary>Opacity from 0 to 1 (the highlight opacity for Bevel and Emboss).</summary>
    public float Opacity { get; init; }

    /// <summary>The solid color (the highlight color for Bevel and Emboss); null for gradient and pattern fills.</summary>
    public PsdColor? Color { get; init; }

    /// <summary>Size (blur radius or stroke width) in pixels.</summary>
    public float? Size { get; init; }

    /// <summary>Offset distance in pixels (shadows and satin).</summary>
    public float? Distance { get; init; }

    /// <summary>Light or offset angle in degrees.</summary>
    public float? Angle { get; init; }

    /// <summary>Spread (drop shadow, outer glow) or choke (inner shadow, inner glow) in percent.</summary>
    public float? Spread { get; init; }

    /// <summary>Whether the effect paints a gradient (gradient overlay, gradient stroke, gradient glow).</summary>
    public bool HasGradient { get; init; }

    public PsdStrokePosition? StrokePosition { get; init; }

    public PsdBevelStyle? BevelStyle { get; init; }

    public PsdBevelTechnique? BevelTechnique { get; init; }

    /// <summary>Bevel light altitude in degrees.</summary>
    public float? Altitude { get; init; }

    /// <summary>Bevel depth in percent.</summary>
    public float? Depth { get; init; }

    public PsdBlendMode? ShadowBlendMode { get; init; }

    public float? ShadowOpacity { get; init; }

    public PsdColor? ShadowColor { get; init; }

    /// <summary>The effect's raw descriptor.</summary>
    public Descriptor? Descriptor { get; init; }

    public override string ToString()
    {
        var parts = new List<string> { Kind.ToString() };
        if (!Enabled)
        {
            parts.Add("off");
        }

        parts.Add(BlendMode.ToString());
        parts.Add(FormattableString.Invariant($"{Opacity * 100:0}%"));
        if (Color is { } color)
        {
            parts.Add(color.ToString());
        }

        if (HasGradient)
        {
            parts.Add("gradient");
        }

        if (Size is { } size)
        {
            parts.Add(FormattableString.Invariant($"size {size:0.##}"));
        }

        if (Distance is { } distance)
        {
            parts.Add(FormattableString.Invariant($"distance {distance:0.##}"));
        }

        if (Angle is { } angle)
        {
            parts.Add(FormattableString.Invariant($"angle {angle:0.##}"));
        }

        if (StrokePosition is { } position)
        {
            parts.Add(position.ToString());
        }

        if (BevelStyle is { } style)
        {
            parts.Add(style.ToString());
        }

        return string.Join(' ', parts);
    }
}

/// <summary>
/// A read-only view of a layer's style (<c>lfx2</c>/<c>lmfx</c>): every effect
/// in file order, enabled or not. It projects the compositor's own effect
/// parsing (<c>Rendering/LayerEffects.cs</c>), so the values match what renders.
/// </summary>
public sealed class PsdLayerStyle
{
    private PsdLayerStyle(bool visible, IReadOnlyList<PsdLayerEffect> effects, Descriptor descriptor)
    {
        Visible = visible;
        Effects = effects;
        Descriptor = descriptor;
    }

    /// <summary>The master "effects visible" switch (<c>masterFXSwitch</c>).</summary>
    public bool Visible { get; }

    /// <summary>Every effect instance in file order, including disabled ones.</summary>
    public IReadOnlyList<PsdLayerEffect> Effects { get; }

    /// <summary>The effects that render: enabled ones, when <see cref="Visible"/> is on.</summary>
    public IEnumerable<PsdLayerEffect> ActiveEffects => Visible ? Effects.Where(e => e.Enabled) : [];

    /// <summary>The layer-effects descriptor.</summary>
    public Descriptor Descriptor { get; }

    internal static PsdLayerStyle? Create(PsdLayer layer)
    {
        if (layer.Effects is not { } root)
        {
            return null;
        }

        var document = layer.Document;
        var effects = new List<PsdLayerEffect>();
        foreach (var (key, value) in root.Items)
        {
            if (KindOf(key) is not { } kind)
            {
                continue;
            }

            IEnumerable<DescriptorValue> instances = value.Type == DescriptorValueType.List && value.List is not null ? value.List : [value];
            foreach (var instance in instances)
            {
                if (instance.Object is not { } descriptor)
                {
                    continue;
                }

                // Parse a one-effect copy with the effect switched on, so disabled
                // effects report the same defaults the renderer would use.
                var single = new Descriptor { Name = root.Name, ClassId = root.ClassId };
                single.Add(key, new DescriptorValue { Type = DescriptorValueType.Object, Object = Enabled(descriptor) });
                LayerEffects? parsed;
                try
                {
                    parsed = LayerEffects.Parse(single, document.GlobalLightAngle, document.GlobalLightAltitude);
                }
                catch (PsdFormatException)
                {
                    parsed = null;
                }

                effects.Add(Project(kind, descriptor.GetBoolean("enab"), descriptor, parsed));
            }
        }

        return new PsdLayerStyle(layer.EffectsVisible, effects, root);
    }

    private static PsdLayerEffectKind? KindOf(string key) => key switch
    {
        "DrSh" or "dropShadowMulti" => PsdLayerEffectKind.DropShadow,
        "IrSh" or "innerShadowMulti" => PsdLayerEffectKind.InnerShadow,
        "OrGl" or "outerGlowMulti" => PsdLayerEffectKind.OuterGlow,
        "IrGl" or "innerGlowMulti" => PsdLayerEffectKind.InnerGlow,
        "ebbl" or "bevelEmbossMulti" => PsdLayerEffectKind.BevelEmboss,
        "ChFX" or "chromeFXMulti" => PsdLayerEffectKind.Satin,
        "SoFi" or "solidFillMulti" => PsdLayerEffectKind.ColorOverlay,
        "GrFl" or "gradientFillMulti" => PsdLayerEffectKind.GradientOverlay,
        "patternFill" or "patternFillMulti" => PsdLayerEffectKind.PatternOverlay,
        "FrFX" or "frameFXMulti" => PsdLayerEffectKind.Stroke,
        _ => null,
    };

    private static Descriptor Enabled(Descriptor source)
    {
        var copy = new Descriptor { Name = source.Name, ClassId = source.ClassId };
        foreach (var (key, value) in source.Items)
        {
            if (key != "enab")
            {
                copy.Add(key, value);
            }
        }

        copy.Add("enab", new DescriptorValue { Type = DescriptorValueType.Boolean, Boolean = true });
        return copy;
    }

    private static PsdLayerEffect Project(PsdLayerEffectKind kind, bool enabled, Descriptor descriptor, LayerEffects? parsed)
    {
        var effect = new PsdLayerEffect { Kind = kind, Enabled = enabled, Descriptor = descriptor };
        if (parsed is null)
        {
            return effect;
        }

        switch (kind)
        {
            case PsdLayerEffectKind.DropShadow or PsdLayerEffectKind.InnerShadow:
                {
                    var list = kind == PsdLayerEffectKind.DropShadow ? parsed.DropShadows : parsed.InnerShadows;
                    if (list.Count > 0)
                    {
                        var s = list[0];
                        effect = effect with { BlendMode = s.Mode, Opacity = s.Opacity, Color = s.Color, Size = s.Size, Distance = s.Distance, Angle = s.AngleDegrees, Spread = s.SpreadOrChoke };
                    }

                    break;
                }

            case PsdLayerEffectKind.OuterGlow or PsdLayerEffectKind.InnerGlow:
                {
                    var list = kind == PsdLayerEffectKind.OuterGlow ? parsed.OuterGlows : parsed.InnerGlows;
                    if (list.Count > 0)
                    {
                        var g = list[0];
                        var gradient = descriptor.GetObject("Grad") is not null;
                        effect = effect with { BlendMode = g.Mode, Opacity = g.Opacity, Color = gradient ? null : g.Color, Size = g.Size, Spread = g.SpreadOrChoke, HasGradient = gradient };
                    }

                    break;
                }

            case PsdLayerEffectKind.ColorOverlay or PsdLayerEffectKind.GradientOverlay or PsdLayerEffectKind.PatternOverlay:
                if (parsed.Overlays.Count > 0)
                {
                    var o = parsed.Overlays[0];
                    effect = effect with
                    {
                        BlendMode = o.Mode,
                        Opacity = o.Opacity,
                        Color = kind == PsdLayerEffectKind.ColorOverlay ? o.Color : null,
                        HasGradient = o.Gradient is not null,
                        Angle = o.Gradient is not null ? (float)descriptor.GetNumber("Angl", 90) : null,
                    };
                }

                break;
            case PsdLayerEffectKind.Stroke:
                if (parsed.Strokes.Count > 0)
                {
                    var s = parsed.Strokes[0];
                    var solid = descriptor.GetEnum("PntT") is null or "SClr";
                    effect = effect with
                    {
                        BlendMode = s.Mode,
                        Opacity = s.Opacity,
                        Color = solid ? s.Color : null,
                        Size = s.Size,
                        HasGradient = s.Gradient is not null,
                        StrokePosition = s.Position switch
                        {
                            Rendering.StrokePosition.Inside => PsdStrokePosition.Inside,
                            Rendering.StrokePosition.Center => PsdStrokePosition.Center,
                            _ => PsdStrokePosition.Outside,
                        },
                    };
                }

                break;
            case PsdLayerEffectKind.Satin:
                if (parsed.Satins.Count > 0)
                {
                    var s = parsed.Satins[0];
                    effect = effect with { BlendMode = s.Mode, Opacity = s.Opacity, Color = s.Color, Size = s.Size, Distance = s.Distance, Angle = s.AngleDegrees };
                }

                break;
            case PsdLayerEffectKind.BevelEmboss:
                if (parsed.Bevels.Count > 0)
                {
                    var b = parsed.Bevels[0];
                    effect = effect with
                    {
                        BlendMode = b.HighlightMode,
                        Opacity = b.HighlightOpacity,
                        Color = b.HighlightColor,
                        ShadowBlendMode = b.ShadowMode,
                        ShadowOpacity = b.ShadowOpacity,
                        ShadowColor = b.ShadowColor,
                        Size = b.Size,
                        Angle = b.AngleDegrees,
                        Altitude = b.AltitudeDegrees,
                        Depth = b.Depth * 100,
                        BevelStyle = (PsdBevelStyle)(int)b.Style,
                        BevelTechnique = (PsdBevelTechnique)(int)b.Technique,
                    };
                }

                break;
        }

        return effect;
    }
}
