using Patchy.Psd.Imaging;
using Patchy.Psd.Layers;

namespace Patchy.Psd.Rendering;

/// <summary>
/// Layer-effect passes, following the order and rules of the reference
/// <c>composite_pixel_layer</c> (.reference/src/render/layer_compositor.hpp)
/// and .reference/docs/layer-effects-render.md:
/// drop shadows and outer glows below the layer; gradient and color overlays
/// and satin folded into the layer's own color; inner glows, inner shadows
/// and strokes above it. Effects scale with layer opacity but not with Fill.
/// </summary>
internal sealed partial class LayerCompositor
{
    private readonly Dictionary<PsdLayer, LayerEffects?> _effectsCache = [];

    /// <summary>A float plane in document space.</summary>
    private sealed class Plane(PsdRect rect, float[] data)
    {
        public PsdRect Rect { get; } = rect;

        public float[] Data { get; } = data;

        public float At(int x, int y) => Rect.Contains(x, y) ? Data[((y - Rect.Top) * Rect.Width) + (x - Rect.Left)] : 0f;

        public ReadOnlySpan<float> Row(int y, int x0, int width) => Data.AsSpan(((y - Rect.Top) * Rect.Width) + (x0 - Rect.Left), width);
    }

    /// <summary>Everything the effect passes of one layer share.</summary>
    private sealed class StyledLayer
    {
        public required PsdLayer Layer { get; init; }

        public required LayerEffects Effects { get; init; }

        public required PlanarImage Source { get; init; }

        /// <summary>The effect domain: source bounds plus the largest effect reach.</summary>
        public required PsdRect Domain { get; init; }

        /// <summary>Layer coverage over the domain: pixel alpha times the layer's masks (unless masks hide effects).</summary>
        public required float[] Matte { get; init; }

        public MaskSampler? LayerMask { get; init; }

        /// <summary>Pass-through groups fade their whole result afterwards, so their effects draw at full strength.</summary>
        public float? OpacityOverride { get; init; }

        public float Opacity => OpacityOverride ?? Layer.Opacity / 255f;

        public float Fill => Layer.FillOpacity / 255f;

        public float MatteAt(int x, int y) => Domain.Contains(x, y) ? Matte[((y - Domain.Top) * Domain.Width) + (x - Domain.Left)] : 0f;

        /// <summary>Content knockout from strokes without Overprint (1 = keep content).</summary>
        public Plane? Knockout { get; set; }

        /// <summary>Stroke planes drawn above the content (inner band share).</summary>
        public List<(StrokeEffect Stroke, Plane Over, EffectPaint? Paint)> StrokePlanes { get; } = [];

        /// <summary>Stroke planes drawn under the content (outer band share, where content is missing).</summary>
        public List<(StrokeEffect Stroke, Plane Under, EffectPaint? Paint)> StrokeUnderlays { get; } = [];
    }

    private LayerEffects? EffectsOf(PsdLayer layer)
    {
        if (_effectsCache.TryGetValue(layer, out var cached))
        {
            return cached;
        }

        LayerEffects? effects = null;
        if (layer.Effects is { } descriptor && layer.EffectsVisible)
        {
            effects = LayerEffects.Parse(descriptor, _document.GlobalLightAngle, _document.GlobalLightAltitude);
            if (effects is not null)
            {
                var hides = layer.GetTaggedBlock("lmgm") is { Data.Length: > 0 } lmgm && lmgm.Data.Span[0] != 0;
                hides |= layer.GetTaggedBlock("vmgm") is { Data.Length: > 0 } vmgm && vmgm.Data.Span[0] != 0;
                effects.MaskHidesEffects = hides;
            }
        }

        _effectsCache[layer] = effects;
        return effects;
    }

    private static int EffectReach(LayerEffects fx)
    {
        var reach = 0;
        foreach (var s in fx.DropShadows)
        {
            reach = Math.Max(reach, (int)MathF.Ceiling(s.Distance + s.Size) + 3);
        }

        foreach (var g in fx.OuterGlows)
        {
            reach = Math.Max(reach, (int)MathF.Ceiling(g.Size) + 3);
        }

        foreach (var s in fx.Strokes)
        {
            reach = Math.Max(reach, (int)MathF.Ceiling(s.Size) + 3);
        }

        foreach (var s in fx.InnerShadows)
        {
            reach = Math.Max(reach, (int)MathF.Ceiling(s.Distance + s.Size) + 3);
        }

        foreach (var g in fx.InnerGlows)
        {
            reach = Math.Max(reach, (int)MathF.Ceiling(g.Size) + 3);
        }

        foreach (var s in fx.Satins)
        {
            reach = Math.Max(reach, (int)MathF.Ceiling(s.Distance + s.Size) + 3);
        }

        foreach (var b in fx.Bevels)
        {
            reach = Math.Max(reach, (int)MathF.Ceiling(b.Size + b.Soften) + 4);
        }

        // Keep pathological descriptors (30000 px distances) bounded to the canvas scale.
        return Math.Min(reach, 4096);
    }

    private StyledLayer? PrepareStyled(PsdLayer layer, PlanarImage source, LayerEffects fx, float? opacityOverride = null)
    {
        var reach = EffectReach(fx);
        var bounds = source.Bounds;
        var domain = new PsdRect(bounds.Left - reach, bounds.Top - reach, bounds.Right + reach, bounds.Bottom + reach);
        // Effects cannot draw outside the canvas; the matte is zero beyond the layer,
        // so a domain clipped to canvas plus reach keeps every blur sample available.
        var limit = new PsdRect(_canvas.Left - reach, _canvas.Top - reach, _canvas.Right + reach, _canvas.Bottom + reach);
        domain = domain.Intersect(limit);
        if (domain.IsEmpty)
        {
            return null;
        }

        var layerMask = LayerMasks(layer, domain);
        var matte = new float[domain.Width * domain.Height];
        var inner = bounds.Intersect(domain);
        var row = new float[Math.Max(1, inner.Width)];
        for (var y = inner.Top; y < inner.Bottom; y++)
        {
            source.A.AsSpan(source.RowOffset(y, inner.Left), inner.Width).CopyTo(row);
            if (!fx.MaskHidesEffects)
            {
                layerMask?.MultiplyRow(y, inner.Left, row.AsSpan(0, inner.Width));
            }

            row.AsSpan(0, inner.Width).CopyTo(matte.AsSpan(((y - domain.Top) * domain.Width) + (inner.Left - domain.Left)));
        }

        return new StyledLayer { Layer = layer, Effects = fx, Source = source, Domain = domain, Matte = matte, LayerMask = layerMask, OpacityOverride = opacityOverride };
    }

    private void CompositeStyledPixels(PlanarImage target, PsdLayer layer, PlanarImage source, LayerEffects fx, MaskChain? masks, PsdBlendMode mode, bool clipMode)
    {
        var styled = PrepareStyled(layer, source, fx);
        if (styled is null)
        {
            return;
        }

        RenderExteriorEffects(target, styled, masks, clipMode);
        PrepareStrokes(styled);
        CompositeContent(target, styled, source, mode, masks, clipMode);
        RenderInteriorEffects(target, styled, masks, clipMode);
    }

    private void RenderExteriorEffects(PlanarImage target, StyledLayer styled, MaskChain? masks, bool clipMode)
    {
        var fx = styled.Effects;
        var domain = styled.Domain;
        var paintScale = styled.Fill * styled.Opacity;
        foreach (var shadow in fx.DropShadows)
        {
            if (shadow.Opacity <= 0)
            {
                continue;
            }

            var (dx, dy) = Offset(shadow.AngleDegrees, shadow.Distance);
            var plane = Shifted(styled, dx, dy);
            EffectMasks.SoftExterior(plane, domain.Width, domain.Height, shadow.Size, shadow.SpreadOrChoke);
            var conceals = shadow.LayerConceals;
            DrawEffect(target, styled, new Plane(domain, plane), shadow.Opacity * styled.Opacity, shadow.Color, shadow.Mode, masks, clipMode,
                (x, y) => conceals ? ExteriorKnockout(styled.MatteAt(x, y), paintScale) : 1f);
        }

        foreach (var glow in fx.OuterGlows)
        {
            if (glow.Opacity <= 0 || glow.Size <= 0)
            {
                continue;
            }

            var plane = (float[])styled.Matte.Clone();
            EffectMasks.SoftExterior(plane, domain.Width, domain.Height, glow.Size, glow.SpreadOrChoke);
            ApplyRange(plane, glow.Range, center: false);
            DrawEffect(target, styled, new Plane(domain, plane), glow.Opacity * styled.Opacity, glow.Color, glow.Mode, masks, clipMode,
                (x, y) => ExteriorKnockout(styled.MatteAt(x, y), paintScale));
        }
    }

    private void RenderInteriorEffects(PlanarImage target, StyledLayer styled, MaskChain? masks, bool clipMode)
    {
        var fx = styled.Effects;
        var domain = styled.Domain;
        var knockout = styled.Knockout;
        float Inside(int x, int y) => styled.MatteAt(x, y) * (knockout?.At(x, y) ?? 1f);

        foreach (var glow in fx.InnerGlows)
        {
            if (glow.Opacity <= 0 || (glow.Size <= 0 && !glow.CenterSource))
            {
                continue;
            }

            var plane = (float[])styled.Matte.Clone();
            EffectMasks.SoftInterior(plane, domain.Width, domain.Height, glow.Size, glow.SpreadOrChoke);
            ApplyRange(plane, glow.Range, glow.CenterSource);
            DrawEffect(target, styled, new Plane(domain, plane), glow.Opacity * styled.Opacity, glow.Color, glow.Mode, masks, clipMode, Inside);
        }

        foreach (var shadow in fx.InnerShadows)
        {
            if (shadow.Opacity <= 0)
            {
                continue;
            }

            var (dx, dy) = Offset(shadow.AngleDegrees, shadow.Distance);
            var plane = Shifted(styled, dx, dy);
            EffectMasks.SoftInterior(plane, domain.Width, domain.Height, shadow.Size, shadow.SpreadOrChoke);
            DrawEffect(target, styled, new Plane(domain, plane), shadow.Opacity * styled.Opacity, shadow.Color, shadow.Mode, masks, clipMode, Inside);
        }

        foreach (var (stroke, over, paint) in styled.StrokePlanes)
        {
            DrawEffect(target, styled, over, stroke.Opacity * styled.Opacity, stroke.Color, stroke.Mode, masks, clipMode, null, paint);
        }

        RenderBevels(target, styled, masks, clipMode);
    }

    /// <summary>
    /// Builds stroke bands: the outer share (where content is missing) paints
    /// immediately, under the content; the inner share is kept for the pass above
    /// the content; strokes without Overprint knock the content out of their band.
    /// </summary>
    private void PrepareStrokes(StyledLayer styled)
    {
        var fx = styled.Effects;
        if (fx.Strokes.Count == 0)
        {
            return;
        }

        var domain = styled.Domain;
        var width = domain.Width;
        var height = domain.Height;
        var needOutside = fx.Strokes.Any(s => s.Position != StrokePosition.Inside);
        var needInside = fx.Strokes.Any(s => s.Position != StrokePosition.Outside);
        EffectMasks.StrokeDistanceFields(styled.Matte, width, height, needOutside, needInside, out var outside, out var inside);
        float[]? knockout = null;
        foreach (var stroke in fx.Strokes)
        {
            var bandOut = stroke.Position switch { StrokePosition.Inside => 0f, StrokePosition.Center => stroke.Size * 0.5f, _ => stroke.Size };
            var bandIn = stroke.Position switch { StrokePosition.Outside => 0f, StrokePosition.Center => stroke.Size * 0.5f, _ => stroke.Size };
            var under = new float[styled.Matte.Length];
            var over = new float[styled.Matte.Length];
            var any = false;
            for (var i = 0; i < styled.Matte.Length; i++)
            {
                var a = Math.Clamp(styled.Matte[i], 0f, 1f);
                var outBand = bandOut > 0 && outside is not null ? Math.Clamp(bandOut + 1f - outside[i], 0f, 1f) : 0f;
                var inBand = bandIn > 0 && inside is not null ? Math.Clamp(bandIn + 1f - inside[i], 0f, 1f) : 0f;
                var innerShare = a * inBand;
                under[i] = outBand * (1f - innerShare);
                over[i] = innerShare;
                any |= under[i] > 0 || over[i] > 0;
                if (!stroke.Overprint && innerShare > 0)
                {
                    knockout ??= Enumerable.Repeat(1f, styled.Matte.Length).ToArray();
                    knockout[i] *= 1f - innerShare;
                }
            }

            if (!any)
            {
                continue;
            }

            var paint = StrokePaint(styled, stroke, bandOut, bandIn, outside, inside);
            styled.StrokePlanes.Add((stroke, new Plane(domain, over), paint));
            styled.StrokeUnderlays.Add((stroke, new Plane(domain, under), paint));
        }

        if (knockout is not null)
        {
            styled.Knockout = new Plane(domain, knockout);
        }
    }

    /// <summary>The layer's own pixels with overlays and satin folded in, stroke underlays first.</summary>
    private void CompositeContent(PlanarImage target, StyledLayer styled, PlanarImage source, PsdBlendMode mode, MaskChain? masks, bool clipMode)
    {
        foreach (var (stroke, under, paint) in styled.StrokeUnderlays)
        {
            DrawEffect(target, styled, under, stroke.Opacity * styled.Opacity, stroke.Color, stroke.Mode, masks, clipMode, null, paint);
        }

        var region = source.Bounds.Intersect(target.Bounds);
        if (region.IsEmpty)
        {
            return;
        }

        var fx = styled.Effects;

        // Overlays and satin fold into the layer's color only when that is what
        // Photoshop computes: a Normal layer (or "Blend Interior Effects as Group")
        // at full Fill. Otherwise they paint after the layer's own blend, unscaled by Fill.
        var fold = styled.Fill >= 1f && (mode is PsdBlendMode.Normal || styled.Layer.BlendInteriorElements);
        var satinPlanes = new List<(SatinEffect Satin, Plane Plane)>();
        foreach (var satin in fold ? fx.Satins : [])
        {
            if (satin.Opacity > 0)
            {
                satinPlanes.Add((satin, SatinPlane(styled, satin)));
            }
        }

        var overlays = fold ? fx.Overlays : [];
        var overlayPaints = overlays.Select(o => OverlayPaint(styled, o)).ToList();

        var gradientBounds = MatteBounds(styled);
        var width = region.Width;
        var alpha = new float[width];
        var r = new float[width];
        var g = new float[width];
        var b = new float[width];
        var er = new float[width];
        var eg = new float[width];
        var eb = new float[width];
        var ea = new float[width];
        var ones = Enumerable.Repeat(1f, width).ToArray();
        var dissolve = mode == PsdBlendMode.Dissolve;
        var contentMode = dissolve ? PsdBlendMode.Normal : mode;
        for (var y = region.Top; y < region.Bottom; y++)
        {
            var s = source.RowOffset(y, region.Left);
            source.R.AsSpan(s, width).CopyTo(r);
            source.G.AsSpan(s, width).CopyTo(g);
            source.B.AsSpan(s, width).CopyTo(b);

            // Interior overlays fold into the layer's straight color: gradient under color, then satin.
            for (var o = 0; o < overlays.Count; o++)
            {
                var overlay = overlays[o];
                var paint = overlayPaints[o];
                for (var i = 0; i < width; i++)
                {
                    if (paint is null)
                    {
                        er[i] = overlay.Color.R / 255f;
                        eg[i] = overlay.Color.G / 255f;
                        eb[i] = overlay.Color.B / 255f;
                        ea[i] = overlay.Opacity;
                    }
                    else
                    {
                        float factor;
                        (er[i], eg[i], eb[i], factor) = paint(region.Left + i, y);
                        ea[i] = overlay.Opacity * factor;
                    }
                }

                FoldInto(overlay.Mode, er, eg, eb, ea, r, g, b, ones, region.Left, y);
            }

            foreach (var (satin, plane) in satinPlanes)
            {
                var row = plane.Row(y, region.Left, width);
                for (var i = 0; i < width; i++)
                {
                    er[i] = satin.Color.R / 255f;
                    eg[i] = satin.Color.G / 255f;
                    eb[i] = satin.Color.B / 255f;
                    ea[i] = row[i] * satin.Opacity;
                }

                FoldInto(satin.Mode, er, eg, eb, ea, r, g, b, ones, region.Left, y);
            }

            source.A.AsSpan(s, width).CopyTo(alpha);
            BlendKernels.Scale(alpha, styled.Opacity * styled.Fill);
            styled.LayerMask?.MultiplyRow(y, region.Left, alpha);
            for (var chain = masks; chain is not null; chain = chain.Next)
            {
                chain.Sampler.MultiplyRow(y, region.Left, alpha);
            }

            if (styled.Knockout is { } knockout)
            {
                BlendKernels.MultiplyInPlace(alpha, knockout.Row(y, region.Left, width));
            }

            if (dissolve)
            {
                Dissolve(alpha, region.Left, y);
            }

            var t = target.RowOffset(y, region.Left);
            BlendKernels.CompositeRow(contentMode, r, g, b, alpha, target.R.AsSpan(t, width), target.G.AsSpan(t, width), target.B.AsSpan(t, width), target.A.AsSpan(t, width), clipMode);
        }

        if (!fold)
        {
            RenderOverlayPasses(target, styled, masks, clipMode);
        }
    }

    /// <summary>Folds an effect color into straight color rows (destination treated as opaque), with the burn/dodge alpha fold.</summary>
    private static void FoldInto(PsdBlendMode mode, float[] er, float[] eg, float[] eb, float[] ea, float[] r, float[] g, float[] b, float[] ones, int x0, int y)
    {
        PrepareEffectColor(mode, er, eg, eb, ea, null, x0, y, out var effectiveMode);
        var scratch = (float[])ones.Clone();
        BlendKernels.CompositeRow(effectiveMode, er, eg, eb, ea, r, g, b, scratch, clipMode: true);
    }

    /// <summary>
    /// Photoshop composites effect planes with burn-mode alpha folded into the color
    /// (toward white for Linear/Color Burn, toward black for Color Dodge) and blends at
    /// full coverage over opaque destination pixels; Dissolve becomes a coverage decision.
    /// </summary>
    private static void PrepareEffectColor(PsdBlendMode mode, float[] r, float[] g, float[] b, float[] a, ReadOnlySpan<float> destinationAlpha, int x0, int y, out PsdBlendMode effectiveMode)
    {
        effectiveMode = mode;
        switch (mode)
        {
            case PsdBlendMode.Dissolve:
                Dissolve(a, x0, y);
                effectiveMode = PsdBlendMode.Normal;
                return;
            case PsdBlendMode.LinearBurn:
            case PsdBlendMode.ColorBurn:
                for (var i = 0; i < a.Length; i++)
                {
                    if (a[i] > 0 && (destinationAlpha.IsEmpty || destinationAlpha[i] >= 1f))
                    {
                        r[i] = 1f - ((1f - r[i]) * a[i]);
                        g[i] = 1f - ((1f - g[i]) * a[i]);
                        b[i] = 1f - ((1f - b[i]) * a[i]);
                        a[i] = 1f;
                    }
                }

                return;
            case PsdBlendMode.ColorDodge:
                for (var i = 0; i < a.Length; i++)
                {
                    if (a[i] > 0 && (destinationAlpha.IsEmpty || destinationAlpha[i] >= 1f))
                    {
                        r[i] *= a[i];
                        g[i] *= a[i];
                        b[i] *= a[i];
                        a[i] = 1f;
                    }
                }

                return;
        }
    }

    /// <summary>Composites a solid-color effect plane into the target.</summary>
    private void DrawEffect(PlanarImage target, StyledLayer styled, Plane plane, float scale, PsdColor color, PsdBlendMode mode, MaskChain? masks, bool clipMode, Func<int, int, float>? factor, EffectPaint? paint = null)
    {
        var region = plane.Rect.Intersect(target.Bounds);
        if (region.IsEmpty || scale <= 0)
        {
            return;
        }

        var width = region.Width;
        var r = new float[width];
        var g = new float[width];
        var b = new float[width];
        var a = new float[width];
        for (var y = region.Top; y < region.Bottom; y++)
        {
            plane.Row(y, region.Left, width).CopyTo(a);
            var any = false;
            for (var i = 0; i < width; i++)
            {
                var value = a[i] * scale;
                if (value > 0 && factor is not null)
                {
                    value *= factor(region.Left + i, y);
                }

                a[i] = value;
                any |= value > 0;
            }

            if (!any)
            {
                continue;
            }

            if (styled.Effects.MaskHidesEffects)
            {
                styled.LayerMask?.MultiplyRow(y, region.Left, a);
            }

            for (var chain = masks; chain is not null; chain = chain.Next)
            {
                chain.Sampler.MultiplyRow(y, region.Left, a);
            }

            if (paint is null)
            {
                r.AsSpan().Fill(color.R / 255f);
                g.AsSpan().Fill(color.G / 255f);
                b.AsSpan().Fill(color.B / 255f);
            }
            else
            {
                for (var i = 0; i < width; i++)
                {
                    if (a[i] <= 0)
                    {
                        continue;
                    }

                    var (pr, pg, pb, pa) = paint(region.Left + i, y);
                    r[i] = pr;
                    g[i] = pg;
                    b[i] = pb;
                    a[i] *= pa;
                }
            }

            var t = target.RowOffset(y, region.Left);
            PrepareEffectColor(mode, r, g, b, a, target.A.AsSpan(t, width), region.Left, y, out var effectiveMode);
            BlendKernels.CompositeRow(effectiveMode, r, g, b, a, target.R.AsSpan(t, width), target.G.AsSpan(t, width), target.B.AsSpan(t, width), target.A.AsSpan(t, width), clipMode);
        }
    }

    /// <summary>Per-pixel paint of a gradient or pattern overlay; null for solid colors (and unresolvable patterns paint nothing).</summary>
    private EffectPaint? OverlayPaint(StyledLayer styled, OverlayEffect overlay)
    {
        if (overlay.Gradient is { } gradient)
        {
            var placement = gradient.AlignWithLayer ? MatteBounds(styled) : styled.Source.Bounds;
            return (x, y) =>
            {
                var position = gradient.Position(placement, x, y, GradientSpan.LayerProjection);
                var (r, g, b) = gradient.Color(position, endpointSmoothing: false);
                return (r, g, b, gradient.Opacity(position, endpointSmoothing: false));
            };
        }

        if (overlay.Pattern is { } pattern)
        {
            if (!_document.Patterns.TryGetValue(pattern.PatternId, out var tile))
            {
                return static (_, _) => (0f, 0f, 0f, 0f);
            }

            var sampler = new PatternSampler(tile, pattern, styled.Layer);
            return sampler.Sample;
        }

        return null;
    }

    /// <summary>Per-pixel effect color (straight, 0..1) and an extra alpha factor.</summary>
    internal delegate (float R, float G, float B, float A) EffectPaint(int x, int y);

    /// <summary>
    /// Gradient strokes. Shape Burst follows the band's anchored distance field
    /// (position 0 at the outer band limit, 1 at the inner one, each limit reaching
    /// +1 px) with Photoshop's 1-2-1 tent across the pixel footprint; other types
    /// use the ordinary gradient placement over the layer's bounds.
    /// </summary>
    private static EffectPaint? StrokePaint(StyledLayer styled, StrokeEffect stroke, float bandOut, float bandIn, float[]? outside, float[]? inside)
    {
        if (stroke.Gradient is not { } gradient)
        {
            return null;
        }

        var domain = styled.Domain;
        if (gradient.Type == GradientType.ShapeBurst)
        {
            var outerReach = bandOut > 0 ? bandOut + 1f : 0f;
            var innerReach = bandIn > 0 ? bandIn + 1f : 0f;
            var span = Math.Max(1f, outerReach + innerReach);
            var step = 1f / span;
            return (x, y) =>
            {
                var i = ((y - domain.Top) * domain.Width) + (x - domain.Left);
                float along;
                if (styled.Matte[i] >= 0.5f)
                {
                    along = inside is null ? span : bandOut + inside[i];
                }
                else
                {
                    along = outside is null ? 0f : outerReach - outside[i];
                }

                var position = Math.Clamp(along / span, 0f, 1f);
                float At(float p) => Math.Clamp(gradient.Reverse ? 1f - p : p, 0f, 1f);
                var (r0, g0, b0) = gradient.Color(At(position - step), endpointSmoothing: true);
                var (r1, g1, b1) = gradient.Color(At(position), endpointSmoothing: true);
                var (r2, g2, b2) = gradient.Color(At(position + step), endpointSmoothing: true);
                var opacity = 0.25f * (gradient.Opacity(At(position - step), true) + (2f * gradient.Opacity(At(position), true)) + gradient.Opacity(At(position + step), true));
                return ((r0 + (2f * r1) + r2) * 0.25f, (g0 + (2f * g1) + g2) * 0.25f, (b0 + (2f * b1) + b2) * 0.25f, opacity);
            };
        }

        var bounds = gradient.AlignWithLayer ? MatteBounds(styled) : styled.Source.Bounds;
        return (x, y) =>
        {
            var position = gradient.Position(bounds, x, y, GradientSpan.LayerProjection);
            var (r, g, b) = gradient.Color(position, endpointSmoothing: false);
            return (r, g, b, gradient.Opacity(position, endpointSmoothing: false));
        };
    }

    /// <summary>
    /// A clipping base with effects: the base pixels plus its clipped members form the
    /// content, the base's effects key off its own matte, and its interior effects and
    /// overlays land over the members (reference <c>ClipRunContent</c>).
    /// </summary>
    private void CompositeStyledClipRun(PlanarImage target, PsdLayer layer, PlanarImage source, LayerEffects fx, IReadOnlyList<PsdLayer> members, MaskChain? masks)
    {
        var content = source.Clone();
        foreach (var member in members)
        {
            CompositeLayer(content, member, null, null, clipMode: true);
        }

        var styled = PrepareStyled(layer, source, fx);
        if (styled is null)
        {
            return;
        }

        var mode = layer.BlendMode is PsdBlendMode.PassThrough ? PsdBlendMode.Normal : layer.BlendMode;
        RenderExteriorEffects(target, styled, masks, clipMode: false);
        PrepareStrokes(styled);
        CompositeContent(target, styled, content, mode, masks, clipMode: false);
        RenderInteriorEffects(target, styled, masks, clipMode: false);
    }

    /// <summary>
    /// A styled group. Isolated groups route their flattened children through the
    /// ordinary styled pipeline (the group plays the layer). Pass Through groups do not
    /// isolate: exterior effects paint from the children's silhouette before the
    /// children, interior effects after them, and group opacity fades the result.
    /// </summary>
    private void CompositeStyledGroup(PlanarImage target, PsdLayer group, LayerEffects fx, MaskChain? masks, PsdBlendMode mode, bool clipMode, MaskSampler? groupMask)
    {
        var bounds = ContentBounds(group).Intersect(target.Bounds);
        if (bounds.IsEmpty)
        {
            return;
        }

        var silhouette = new PlanarImage(bounds);
        CompositeList(silhouette, group.Children, null);
        var fill = group.FillOpacity / 255f;
        if (mode != PsdBlendMode.PassThrough || fill < 1f)
        {
            CompositeStyledPixels(target, group, silhouette, fx, masks, mode == PsdBlendMode.PassThrough ? PsdBlendMode.Normal : mode, clipMode);
            return;
        }

        var opacity = group.Opacity / 255f;
        var reach = EffectReach(fx);
        var fadeRegion = new PsdRect(bounds.Left - reach, bounds.Top - reach, bounds.Right + reach, bounds.Bottom + reach).Intersect(target.Bounds);
        if (HasAdjustmentDescendant(group))
        {
            fadeRegion = target.Bounds;
        }

        var before = opacity < 1f ? Crop(target, fadeRegion) : null;
        var styled = PrepareStyled(group, silhouette, fx, opacityOverride: 1f);
        if (styled is not null)
        {
            RenderExteriorEffects(target, styled, masks, clipMode);
        }

        CompositeList(target, group.Children, Chain(masks, groupMask));
        if (styled is not null)
        {
            RenderOverlayPasses(target, styled, masks, clipMode);
            PrepareStrokes(styled);
            foreach (var (stroke, under, paint) in styled.StrokeUnderlays)
            {
                DrawEffect(target, styled, under, stroke.Opacity, stroke.Color, stroke.Mode, masks, clipMode, null, paint);
            }

            styled.Knockout = null;
            RenderInteriorEffects(target, styled, masks, clipMode);
        }

        if (before is not null)
        {
            FadeToward(target, before, fadeRegion, opacity);
        }
    }

    /// <summary>Overlays and satin as destination passes over the matte (pass-through groups cannot fold them).</summary>
    private void RenderOverlayPasses(PlanarImage target, StyledLayer styled, MaskChain? masks, bool clipMode)
    {
        var matte = new Plane(styled.Domain, styled.Matte);
        foreach (var overlay in styled.Effects.Overlays)
        {
            DrawEffect(target, styled, matte, overlay.Opacity * styled.Opacity, overlay.Color, overlay.Mode, masks, clipMode, null, OverlayPaint(styled, overlay));
        }

        foreach (var satin in styled.Effects.Satins)
        {
            var plane = SatinPlane(styled, satin);
            DrawEffect(target, styled, plane, satin.Opacity * styled.Opacity, satin.Color, satin.Mode, masks, clipMode, (x, y) => styled.MatteAt(x, y));
        }
    }

    private static void FadeToward(PlanarImage target, PlanarImage before, PsdRect region, float opacity)
    {
        for (var y = region.Top; y < region.Bottom; y++)
        {
            var t = target.RowOffset(y, region.Left);
            var b = before.RowOffset(y, region.Left);
            var w = region.Width;
            BlendKernels.FadeTowardRow(
                before.R.AsSpan(b, w), before.G.AsSpan(b, w), before.B.AsSpan(b, w), before.A.AsSpan(b, w),
                target.R.AsSpan(t, w), target.G.AsSpan(t, w), target.B.AsSpan(t, w), target.A.AsSpan(t, w),
                opacity);
        }
    }

    /// <summary>
    /// Photoshop's "knocks out" factor for exterior effects: the layer's shape
    /// removes its own shadow or glow, divided by what the content pass will
    /// cover anyway (reference <c>exterior_effect_knockout</c>).
    /// </summary>
    private static float ExteriorKnockout(float shape, float paintScale)
    {
        var paint = Math.Clamp(shape * paintScale, 0f, 1f);
        var remaining = 1f - paint;
        return remaining <= 0 ? 0f : Math.Min(1f, (1f - Math.Clamp(shape, 0f, 1f)) / remaining);
    }

    private static void ApplyRange(float[] plane, float range, bool center)
    {
        var gain = 100f / Math.Clamp(range, 1f, 100f);
        for (var i = 0; i < plane.Length; i++)
        {
            var value = Math.Min(1f, plane[i] * gain);
            plane[i] = center ? 1f - value : value;
        }
    }

    /// <summary>Photoshop's light offset: the effect moves away from the light angle.</summary>
    private static (int Dx, int Dy) Offset(float angleDegrees, float distance)
    {
        var radians = (180f - angleDegrees) * MathF.PI / 180f;
        return ((int)MathF.Round(MathF.Cos(radians) * distance, MidpointRounding.AwayFromZero), (int)MathF.Round(MathF.Sin(radians) * distance, MidpointRounding.AwayFromZero));
    }

    /// <summary>The matte moved by (dx, dy) over the same domain (samples from outside the domain are transparent).</summary>
    private static float[] Shifted(StyledLayer styled, int dx, int dy)
    {
        var domain = styled.Domain;
        var width = domain.Width;
        var height = domain.Height;
        var result = new float[styled.Matte.Length];
        for (var y = 0; y < height; y++)
        {
            var sy = y - dy;
            for (var x = 0; x < width; x++)
            {
                var sx = x - dx;
                result[(y * width) + x] = (uint)sx < (uint)width && (uint)sy < (uint)height ? styled.Matte[(sy * width) + sx] : 0f;
            }
        }

        return result;
    }

    /// <summary>Satin: |tent(A(x+v) − A(x−v))|, inverted by default (reference <c>satin_alpha_mask</c>).</summary>
    private static Plane SatinPlane(StyledLayer styled, SatinEffect satin)
    {
        var (dx, dy) = Offset(satin.AngleDegrees, Math.Max(1f, satin.Distance));
        var forward = Shifted(styled, -dx, -dy);
        var backward = Shifted(styled, dx, dy);
        for (var i = 0; i < forward.Length; i++)
        {
            forward[i] -= backward[i];
        }

        var size = satin.Size;
        if (size > 0)
        {
            EffectMasks.TentBlur(forward, styled.Domain.Width, styled.Domain.Height, Math.Max(2, (int)MathF.Round(size, MidpointRounding.AwayFromZero)));
        }

        for (var i = 0; i < forward.Length; i++)
        {
            var value = Math.Clamp(Math.Abs(forward[i]), 0f, 1f);
            forward[i] = satin.Invert ? 1f - value : value;
        }

        return new Plane(styled.Domain, forward);
    }

    /// <summary>Bounding box of the visible matte (gradient overlays aligned with the layer span it).</summary>
    private static PsdRect MatteBounds(StyledLayer styled)
    {
        var domain = styled.Domain;
        int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;
        for (var y = 0; y < domain.Height; y++)
        {
            for (var x = 0; x < domain.Width; x++)
            {
                if (styled.Matte[(y * domain.Width) + x] > 0)
                {
                    left = Math.Min(left, x);
                    right = Math.Max(right, x);
                    top = Math.Min(top, y);
                    bottom = Math.Max(bottom, y);
                }
            }
        }

        return left > right ? styled.Source.Bounds : new PsdRect(domain.Left + left, domain.Top + top, domain.Left + right + 1, domain.Top + bottom + 1);
    }
}
