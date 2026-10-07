using XRay.Psd.Imaging;
using XRay.Psd.Layers;

namespace XRay.Psd.Rendering;

/// <summary>
/// Composites the layer tree in Photoshop order. Semantics follow the
/// calibrated reference compositor (.reference/src/render/layer_compositor.hpp):
/// <list type="bullet">
/// <item>Pass Through groups composite children against the live backdrop, then fade toward the pre-group backdrop by group opacity.</item>
/// <item>Other groups (and pass-through groups with Fill below 100%) isolate, then merge with their blend mode, opacity and mask.</item>
/// <item>A clipping run renders into an isolated buffer seeded by the base; members blend against the base color at full strength inside the base's coverage; the run then merges with the base's blend mode.</item>
/// <item>A hidden or fully transparent base hides its whole clipping run.</item>
/// </list>
/// Layer effects and most adjustment layers are not rendered yet (see TODO.md).
/// </summary>
internal sealed partial class LayerCompositor
{
    private readonly PsdDocument _document;
    private readonly RenderOptions _options;
    private readonly PsdRect _canvas;
    private readonly Dictionary<PsdLayer, PlanarImage?> _pixelCache = [];

    public LayerCompositor(PsdDocument document, RenderOptions options)
    {
        _document = document;
        _options = options;
        _canvas = document.Bounds;
    }

    private sealed record MaskChain(MaskSampler Sampler, MaskChain? Next);

    public PlanarImage Render()
    {
        var canvas = new PlanarImage(_canvas);
        CompositeList(canvas, _document.RootLayers, null);
        return canvas;
    }

    private bool IsVisible(PsdLayer layer) => _options.LayerVisibility?.Invoke(layer) ?? layer.IsVisible;

    private static bool IsClipBase(PsdLayer layer) => layer.Kind != PsdLayerKind.Adjustment;

    private static bool IsClipped(PsdLayer layer) => layer.IsClipped && layer.Kind != PsdLayerKind.Group;

    private void CompositeList(PlanarImage target, IReadOnlyList<PsdLayer> siblings, MaskChain? masks)
    {
        var index = 0;
        while (index < siblings.Count)
        {
            var layer = siblings[index];
            var runEnd = index + 1;
            if (!IsClipped(layer))
            {
                while (runEnd < siblings.Count && IsClipped(siblings[runEnd]))
                {
                    runEnd++;
                }
            }

            if (runEnd == index + 1 || !IsClipBase(layer))
            {
                CompositeLayer(target, layer, masks, null, clipMode: false);
                index++;
                continue;
            }

            // An all-channels-restricted base removes the whole run.
            var baseRestriction = RestrictionOf(layer);
            if (!IsVisible(layer) || layer.Opacity == 0 || baseRestriction == RestrictAll)
            {
                index = runEnd;
                continue;
            }

            // A restricted base gates the merged run against the original backdrop
            // (ps-compat.md "Advanced Blending Channels"); members restrict themselves.
            var baseGate = BlendIfOf(layer);
            if (layer.Kind != PsdLayerKind.Group && baseGate is null && EffectsOf(layer) is { } baseEffects && LayerPixels(layer) is { } baseSource)
            {
                var members = new List<PsdLayer>();
                for (var member = index + 1; member < runEnd; member++)
                {
                    members.Add(siblings[member]);
                }

                WithChannelRestriction(target, baseRestriction, AffectedBounds(layer), () => CompositeStyledClipRun(target, layer, baseSource, baseEffects, members, masks));
                index = runEnd;
                continue;
            }

            var runBounds = layer.Kind == PsdLayerKind.Group ? RenderBounds(layer) : AffectedBounds(layer);
            var bounds = runBounds.Intersect(target.Bounds);
            if (!bounds.IsEmpty)
            {
                // A Blend If base keeps the isolated path: its Underlying gate reads the
                // backdrop outside the buffer, and its ungated alpha stays the clip shape.
                var group = new PlanarImage(bounds);
                var gateBackdrop = baseGate is { HasUnderlying: true } ? Crop(target, bounds) : null;
                if (baseGate is not null)
                {
                    _clipCoverage[group] = new float[bounds.Width * bounds.Height];
                }

                CompositeLayer(group, layer, null, PsdBlendMode.Normal, clipMode: false, gateBackdrop, suppressRestriction: true);
                for (var member = index + 1; member < runEnd; member++)
                {
                    CompositeLayer(group, siblings[member], null, null, clipMode: true);
                }

                _clipCoverage.Remove(group);
                var mode = layer.BlendMode == PsdBlendMode.PassThrough ? PsdBlendMode.Normal : layer.BlendMode;
                WithChannelRestriction(target, baseRestriction, bounds, () => MergeImage(target, group, mode, 1f, masks, clipMode: false));
            }

            index = runEnd;
        }
    }

    /// <summary>
    /// Composites one layer. <paramref name="gateBackdrop"/> overrides the backdrop the
    /// Underlying Blend If gate reads (a clipping-run base renders into a transparent
    /// buffer); <paramref name="suppressRestriction"/> skips the layer's own channel
    /// restriction when the caller applies it at a merge instead.
    /// </summary>
    private void CompositeLayer(PlanarImage target, PsdLayer layer, MaskChain? masks, PsdBlendMode? modeOverride, bool clipMode, PlanarImage? gateBackdrop = null, bool suppressRestriction = false)
    {
        if (!IsVisible(layer) || layer.Opacity == 0)
        {
            return;
        }

        // All three channels excluded removes the layer, effects included.
        var restriction = RestrictionOf(layer);
        if (restriction == RestrictAll)
        {
            return;
        }

        if (restriction != 0 && !suppressRestriction)
        {
            WithChannelRestriction(target, restriction, AffectedBounds(layer), () => CompositeLayer(target, layer, masks, modeOverride, clipMode, gateBackdrop, suppressRestriction: true));
            return;
        }

        switch (layer.Kind)
        {
            case PsdLayerKind.Group:
                CompositeGroup(target, layer, masks, modeOverride, clipMode, gateBackdrop);
                break;
            case PsdLayerKind.Adjustment:
                ApplyAdjustment(target, layer, masks);
                break;
            default:
                CompositePixels(target, layer, masks, modeOverride, clipMode, gateBackdrop);
                break;
        }
    }

    private void CompositeGroup(PlanarImage target, PsdLayer group, MaskChain? masks, PsdBlendMode? modeOverride, bool clipMode, PlanarImage? gateBackdrop)
    {
        var mode = modeOverride ?? group.BlendMode;
        var opacity = group.Opacity / 255f;
        var fill = group.FillOpacity / 255f;
        var groupMask = LayerMasks(group, ContentBounds(group).Intersect(target.Bounds));
        if (EffectsOf(group) is { } effects)
        {
            CompositeStyledGroup(target, group, effects, masks, modeOverride ?? mode, clipMode, groupMask, gateBackdrop);
            return;
        }

        // Blend If groups always isolate, Pass Through included: the gates test the
        // merged children against the outside backdrop.
        var gate = BlendIfOf(group);
        if (mode != PsdBlendMode.PassThrough || fill < 1f || modeOverride is not null || gate is not null)
        {
            var bounds = ContentBounds(group).Intersect(target.Bounds);
            if (bounds.IsEmpty)
            {
                return;
            }

            var isolated = new PlanarImage(bounds);
            CompositeList(isolated, group.Children, null);
            var merged = mode == PsdBlendMode.PassThrough ? PsdBlendMode.Normal : mode;
            MergeImage(target, isolated, merged, opacity * fill, Chain(masks, groupMask), clipMode, gate, gateBackdrop);
            return;
        }

        PlanarImage? before = null;
        var fadeRegion = HasAdjustmentDescendant(group) ? target.Bounds : ContentBounds(group).Intersect(target.Bounds);
        if (opacity < 1f && !fadeRegion.IsEmpty)
        {
            before = Crop(target, fadeRegion);
        }

        CompositeList(target, group.Children, Chain(masks, groupMask));
        if (before is not null)
        {
            for (var y = fadeRegion.Top; y < fadeRegion.Bottom; y++)
            {
                var t = target.RowOffset(y, fadeRegion.Left);
                var b = before.RowOffset(y, fadeRegion.Left);
                var w = fadeRegion.Width;
                BlendKernels.FadeTowardRow(
                    before.R.AsSpan(b, w), before.G.AsSpan(b, w), before.B.AsSpan(b, w), before.A.AsSpan(b, w),
                    target.R.AsSpan(t, w), target.G.AsSpan(t, w), target.B.AsSpan(t, w), target.A.AsSpan(t, w),
                    opacity);
            }
        }
    }

    private void CompositePixels(PlanarImage target, PsdLayer layer, MaskChain? masks, PsdBlendMode? modeOverride, bool clipMode, PlanarImage? gateBackdrop)
    {
        var source = LayerPixels(layer);
        if (source is null)
        {
            return;
        }

        var region = source.Bounds.Intersect(target.Bounds);
        if (region.IsEmpty)
        {
            return;
        }

        var mode = modeOverride ?? layer.BlendMode;
        if (mode == PsdBlendMode.PassThrough)
        {
            mode = PsdBlendMode.Normal;
        }

        if (EffectsOf(layer) is { } effects)
        {
            CompositeStyledPixels(target, layer, source, effects, masks, mode, clipMode, gateBackdrop);
            return;
        }

        var layerMask = LayerMasks(layer, region);
        var opacity = layer.Opacity / 255f;
        var fill = layer.FillOpacity / 255f;
        var gate = BlendIfOf(layer);
        var width = region.Width;
        var alpha = new float[width];
        for (var y = region.Top; y < region.Bottom; y++)
        {
            var s = source.RowOffset(y, region.Left);
            source.A.AsSpan(s, width).CopyTo(alpha);
            layerMask?.MultiplyRow(y, region.Left, alpha);
            for (var chain = masks; chain is not null; chain = chain.Next)
            {
                chain.Sampler.MultiplyRow(y, region.Left, alpha);
            }

            CompositeLayerRow(
                target, y, region.Left, mode,
                source.R.AsSpan(s, width), source.G.AsSpan(s, width), source.B.AsSpan(s, width), alpha,
                opacity, fill, gate, gateBackdrop, clipMode);
        }
    }

    /// <summary>
    /// Merges an isolated buffer (a group or clipping run) into the target with a blend
    /// mode, opacity, masks and, for groups, the group's Blend If gate.
    /// </summary>
    private void MergeImage(PlanarImage target, PlanarImage source, PsdBlendMode mode, float opacity, MaskChain? masks, bool clipMode, BlendIf? gate = null, PlanarImage? gateBackdrop = null)
    {
        var region = source.Bounds.Intersect(target.Bounds);
        if (region.IsEmpty || opacity <= 0)
        {
            return;
        }

        var width = region.Width;
        var alpha = new float[width];
        for (var y = region.Top; y < region.Bottom; y++)
        {
            var s = source.RowOffset(y, region.Left);
            source.A.AsSpan(s, width).CopyTo(alpha);
            for (var chain = masks; chain is not null; chain = chain.Next)
            {
                chain.Sampler.MultiplyRow(y, region.Left, alpha);
            }

            CompositeLayerRow(
                target, y, region.Left, mode,
                source.R.AsSpan(s, width), source.G.AsSpan(s, width), source.B.AsSpan(s, width), alpha,
                opacity, 1f, gate, gateBackdrop, clipMode);
        }
    }

    private void ApplyAdjustment(PlanarImage target, PsdLayer layer, MaskChain? masks)
    {
        var adjustment = Adjustments.Create(layer);
        if (adjustment is null)
        {
            return;
        }

        var region = target.Bounds;
        var layerMask = LayerMasks(layer, region);
        var opacity = layer.Opacity / 255f * (layer.FillOpacity / 255f);
        var mode = layer.BlendMode is PsdBlendMode.PassThrough or PsdBlendMode.Dissolve ? PsdBlendMode.Normal : layer.BlendMode;
        var gate = BlendIfOf(layer);
        var width = region.Width;
        var alpha = new float[width];
        var r = new float[width];
        var g = new float[width];
        var b = new float[width];
        for (var y = region.Top; y < region.Bottom; y++)
        {
            var t = target.RowOffset(y, region.Left);
            var tr = target.R.AsSpan(t, width);
            var tg = target.G.AsSpan(t, width);
            var tb = target.B.AsSpan(t, width);
            tr.CopyTo(r);
            tg.CopyTo(g);
            tb.CopyTo(b);
            adjustment.Apply(r, g, b);
            alpha.AsSpan().Fill(opacity);
            layerMask?.MultiplyRow(y, region.Left, alpha);
            for (var chain = masks; chain is not null; chain = chain.Next)
            {
                chain.Sampler.MultiplyRow(y, region.Left, alpha);
            }

            // Blend If: This Layer tests the adjusted color, Underlying the backdrop before it.
            if (gate is not null)
            {
                ApplyAdjustmentGate(gate, r, g, b, tr, tg, tb, target.A.AsSpan(t, width), alpha);
            }

            // Adjustments recolor existing coverage only: the clip-mode kernel
            // blends at full strength where the backdrop has alpha and never grows it.
            BlendKernels.CompositeRow(mode, r, g, b, alpha, tr, tg, tb, target.A.AsSpan(t, width), clipMode: true, ClipCoverageRow(target, t, width));
        }
    }

    private static MaskChain? Chain(MaskChain? chain, MaskSampler? sampler) => sampler is null ? chain : new MaskChain(sampler, chain);

    /// <summary>Combined raster and vector mask of a layer, or null when none applies.</summary>
    private MaskSampler? LayerMasks(PsdLayer layer, PsdRect region)
    {
        MaskSampler? result = null;
        if (layer.Mask is { } mask)
        {
            result = MaskSampler.FromLayerMask(layer, mask, _canvas);
        }

        MaskSampler? vector = null;
        if (ShapeOwnsVectorMask(layer))
        {
            // A stroked shape's pixels already carry its path (LayerCompositor.Strokes.cs).
        }
        else if (layer.RenderedVectorMask is { } rendered)
        {
            vector = MaskSampler.FromRenderedVectorMask(layer, rendered, _canvas);
        }
        else if (layer.VectorMask is { } path && !region.IsEmpty)
        {
            var reach = layer.VectorMaskFeather > 0 ? (int)Math.Ceiling(layer.VectorMaskFeather * 3) + 1 : 0;
            // The vector feather does not clamp at the canvas: rasterize the path
            // past the edge so the blur sees real coverage there.
            var limit = new PsdRect(_canvas.Left - reach, _canvas.Top - reach, _canvas.Right + reach, _canvas.Bottom + reach);
            var vectorRegion = new PsdRect(region.Left - reach, region.Top - reach, region.Right + reach, region.Bottom + reach).Intersect(limit);
            vector = MaskSampler.FromVectorMask(path, vectorRegion, layer.VectorMaskDensity, layer.VectorMaskFeather, _canvas);
        }

        if (vector is null)
        {
            return result;
        }

        return result is null ? vector : MaskSampler.Multiply(result, vector);
    }

    /// <summary>Decoded pixels for a content layer, synthesizing solid fills when the layer stores none.</summary>
    private PlanarImage? LayerPixels(PsdLayer layer)
    {
        if (_pixelCache.TryGetValue(layer, out var cached))
        {
            return cached;
        }

        var pixels = layer.DecodePixels();
        if (layer.Kind == PsdLayerKind.Fill && (pixels is null || IsEmptyCoverage(pixels)) && StrokedShapePixels(layer) is { } shape)
        {
            pixels = shape;
        }
        else if (layer.Kind == PsdLayerKind.Fill && layer.FillColor is { } color && (pixels is null || IsEmptyCoverage(pixels)))
        {
            // Fill layers render their content from the fill settings; files that
            // carry no channel data get a canvas-wide solid fill that the vector
            // mask then shapes.
            pixels = new PlanarImage(_canvas);
            pixels.Fill(color.R / 255f, color.G / 255f, color.B / 255f, 1f);
        }

        else if (layer.Kind == PsdLayerKind.Fill && layer.ContentKey == "PtFl" && layer.FillDescriptor is { } patternFill && (pixels is null || IsEmptyCoverage(pixels)))
        {
            pixels = PatternFillPixels(layer, patternFill);
        }
        else if (layer.Kind == PsdLayerKind.Fill && layer.ContentKey == "GdFl" && layer.FillDescriptor is { } gradientFill && (pixels is null || IsEmptyCoverage(pixels)))
        {
            pixels = GradientFillPixels(layer, gradientFill);
        }

        _pixelCache[layer] = pixels;
        return pixels;
    }

    /// <summary>
    /// Gradient fill layers span the center chord of their shape's bounds when
    /// aligned (the canvas otherwise), with Photoshop's eased two-stop ramp
    /// (reference <c>vector_raster.cpp</c>, GdFl branch). The vector mask shapes it later.
    /// </summary>
    private PlanarImage? GradientFillPixels(PsdLayer layer, Descriptors.Descriptor descriptor)
    {
        var gradient = Gradient.FromDescriptor(descriptor, defaultAngle: 0);
        if (gradient is null)
        {
            return null;
        }

        var shape = layer.VectorMask is { Subpaths.Count: > 0 } path ? path.Bounds.Intersect(_canvas) : _canvas;
        var placement = gradient.AlignWithLayer && !shape.IsEmpty ? shape : _canvas;
        var pixels = new PlanarImage(_canvas);
        for (var y = _canvas.Top; y < _canvas.Bottom; y++)
        {
            for (var x = _canvas.Left; x < _canvas.Right; x++)
            {
                var position = gradient.Position(placement, x, y, GradientSpan.CenterChord);
                var (r, g, b) = gradient.Color(position, endpointSmoothing: true);
                var i = pixels.RowOffset(y, x);
                pixels.R[i] = r;
                pixels.G[i] = g;
                pixels.B[i] = b;
                pixels.A[i] = gradient.Opacity(position, endpointSmoothing: true);
            }
        }

        return pixels;
    }

    /// <summary>Pattern fill layers tile their pattern over the canvas; the vector mask shapes it later.</summary>
    private PlanarImage? PatternFillPixels(PsdLayer layer, Descriptors.Descriptor descriptor)
    {
        if (PatternPlacement.FromDescriptor(descriptor) is not { } placement || !_document.Patterns.TryGetValue(placement.PatternId, out var tile))
        {
            return null;
        }

        var sampler = new PatternSampler(tile, placement, layer);
        var pixels = new PlanarImage(_canvas);
        for (var y = _canvas.Top; y < _canvas.Bottom; y++)
        {
            for (var x = _canvas.Left; x < _canvas.Right; x++)
            {
                var i = pixels.RowOffset(y, x);
                (pixels.R[i], pixels.G[i], pixels.B[i], pixels.A[i]) = sampler.Sample(x, y);
            }
        }

        return pixels;
    }

    private static bool IsEmptyCoverage(PlanarImage image)
    {
        foreach (var a in image.A)
        {
            if (a > 0)
            {
                return false;
            }
        }

        return true;
    }

    private PsdRect RenderBounds(PsdLayer layer)
    {
        if (layer.Kind == PsdLayerKind.Group)
        {
            return ContentBounds(layer);
        }

        if (layer.Kind == PsdLayerKind.Adjustment)
        {
            return _canvas;
        }

        return LayerPixels(layer)?.Bounds ?? default;
    }

    private PsdRect ContentBounds(PsdLayer group)
    {
        var bounds = default(PsdRect);
        foreach (var child in group.Children)
        {
            if (!IsVisible(child))
            {
                continue;
            }

            if (child.Kind == PsdLayerKind.Adjustment)
            {
                return _canvas;
            }

            bounds = bounds.Union(RenderBounds(child));
        }

        return bounds;
    }

    private bool HasAdjustmentDescendant(PsdLayer group)
    {
        foreach (var child in group.Children)
        {
            if (!IsVisible(child))
            {
                continue;
            }

            if (child.Kind == PsdLayerKind.Adjustment || (child.Kind == PsdLayerKind.Group && HasAdjustmentDescendant(child)))
            {
                return true;
            }
        }

        return false;
    }

    private static PlanarImage Crop(PlanarImage source, PsdRect region)
    {
        var copy = new PlanarImage(region);
        var width = region.Width;
        for (var y = region.Top; y < region.Bottom; y++)
        {
            var s = source.RowOffset(y, region.Left);
            var d = copy.RowOffset(y, region.Left);
            source.R.AsSpan(s, width).CopyTo(copy.R.AsSpan(d, width));
            source.G.AsSpan(s, width).CopyTo(copy.G.AsSpan(d, width));
            source.B.AsSpan(s, width).CopyTo(copy.B.AsSpan(d, width));
            source.A.AsSpan(s, width).CopyTo(copy.A.AsSpan(d, width));
        }

        return copy;
    }

    /// <summary>
    /// Photoshop's Dissolve: each pixel paints fully or not at all, with a
    /// deterministic per-document-pixel threshold (splitmix64 over x, y).
    /// </summary>
    private static void Dissolve(Span<float> alpha, int x0, int y)
    {
        for (var i = 0; i < alpha.Length; i++)
        {
            var a = alpha[i];
            if (a <= 0f || a >= 1f)
            {
                continue;
            }

            var key = (ulong)(uint)(x0 + i) | ((ulong)(uint)y << 32);
            var threshold = (uint)(SplitMix64(key) >> 40);
            var probability = (uint)Math.Round(a * (1 << 24));
            alpha[i] = probability > threshold ? 1f : 0f;
        }
    }

    private static ulong SplitMix64(ulong value)
    {
        value += 0x9e3779b97f4a7c15UL;
        value = (value ^ (value >> 30)) * 0xbf58476d1ce4e5b9UL;
        value = (value ^ (value >> 27)) * 0x94d049bb133111ebUL;
        return value ^ (value >> 31);
    }
}
