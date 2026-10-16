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
    private readonly Dictionary<PsdLayer, PlanarImage?> _prefetched = [];

    public LayerCompositor(PsdDocument document, RenderOptions options)
    {
        _document = document;
        _options = options;
        _canvas = document.Bounds;
    }

    private sealed record MaskChain(MaskSampler Sampler, MaskChain? Next);

    public PlanarImage Render()
    {
        PrefetchPixels();
        var canvas = new PlanarImage(_canvas);
        CompositeList(canvas, _document.RootLayers, null);
        return canvas;
    }

    private bool IsVisible(PsdLayer layer) => _options.LayerVisibility?.Invoke(layer) ?? layer.IsVisible;

    private static bool IsClipBase(PsdLayer layer) => layer.Kind != PsdLayerKind.Adjustment;

    private static bool IsClipped(PsdLayer layer) => layer.IsClipped && layer.Kind != PsdLayerKind.Group;

    private void CompositeList(PlanarImage target, IReadOnlyList<PsdLayer> siblings, MaskChain? masks)
    {
        var knockout = EnterKnockoutScope(target, siblings);
        CompositeSiblings(target, siblings, masks, knockout);
        ExitKnockoutScope(knockout);
    }

    private void CompositeSiblings(PlanarImage target, IReadOnlyList<PsdLayer> siblings, MaskChain? masks, KnockoutScope? knockout)
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
                CompositeKnockoutElement(target, layer, knockout, masks, () => CompositeLayer(target, layer, masks, null, clipMode: false));
                NoteKnockoutBackground(knockout, target, layer);
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

                if (!OutsideBand(target, AffectedBounds(layer)))
                {
                    CompositeKnockoutElement(target, layer, knockout, masks, () => WithChannelRestriction(target, baseRestriction, AffectedBounds(layer), () => CompositeStyledClipRun(target, layer, baseSource, baseEffects, members, masks)));
                }

                index = runEnd;
                continue;
            }

            var runBounds = layer.Kind == PsdLayerKind.Group ? RenderBounds(layer) : AffectedBounds(layer);
            var logical = runBounds.Intersect(target.Logical);
            var bounds = logical.Intersect(target.Bounds);
            if (!bounds.IsEmpty)
            {
                // A Blend If base keeps the isolated path: its Underlying gate reads the
                // backdrop outside the buffer, and its ungated alpha stays the clip shape.
                var group = new PlanarImage(bounds) { Logical = logical };
                var gateBackdrop = baseGate is { HasUnderlying: true } ? Crop(target, bounds) : null;
                if (baseGate is not null)
                {
                    _clipCoverage[group] = new float[bounds.Width * bounds.Height];
                }

                CompositeLayer(group, layer, null, PsdBlendMode.Normal, clipMode: false, gateBackdrop, suppressRestriction: true);
                CompositeClipMembers(group, siblings.Skip(index + 1).Take(runEnd - index - 1));
                _clipCoverage.Remove(group);
                var mode = layer.BlendMode == PsdBlendMode.PassThrough ? PsdBlendMode.Normal : layer.BlendMode;
                CompositeKnockoutElement(target, layer, knockout, masks, () => WithChannelRestriction(target, baseRestriction, bounds, () => MergeImage(target, group, mode, 1f, masks, clipMode: false)));
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
        if (restriction == RestrictAll || OutsideBand(target, ReachBounds(layer)))
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
        var groupMask = LayerMasks(group, ContentBounds(group).Intersect(target.Logical), target.Bounds);
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
            var logical = ContentBounds(group).Intersect(target.Logical);
            var bounds = logical.Intersect(target.Bounds);
            if (bounds.IsEmpty)
            {
                return;
            }

            var isolated = new PlanarImage(bounds) { Logical = logical };
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
            FadeToward(target, before, fadeRegion, opacity);
        }
    }

    private void CompositePixels(PlanarImage target, PsdLayer layer, MaskChain? masks, PsdBlendMode? modeOverride, bool clipMode, PlanarImage? gateBackdrop)
    {
        var source = LayerPixels(layer);
        if (source is null)
        {
            return;
        }

        // A band render keeps only some rows of the source and target; the region a whole
        // render would composite decides what runs (styled layers draw effects past it).
        var logicalRegion = source.Logical.Intersect(target.Logical);
        if (logicalRegion.IsEmpty)
        {
            return;
        }

        var region = source.Bounds.Intersect(target.Bounds);
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

        if (region.IsEmpty)
        {
            return;
        }

        var layerMask = LayerMasks(layer, logicalRegion, region);
        var opacity = layer.Opacity / 255f;
        var fill = layer.FillOpacity / 255f;
        var gate = BlendIfOf(layer);
        var width = region.Width;
        Parallelism.For(region.Height, width, (start, end) =>
        {
            var alpha = new float[width];
            for (var y = region.Top + start; y < region.Top + end; y++)
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
        });
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
        Parallelism.For(region.Height, width, (start, end) =>
        {
            var alpha = new float[width];
            for (var y = region.Top + start; y < region.Top + end; y++)
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
        });
    }

    private void ApplyAdjustment(PlanarImage target, PsdLayer layer, MaskChain? masks)
    {
        var adjustment = Adjustments.Create(layer, _options.RenderColorLookup);
        if (adjustment is null)
        {
            return;
        }

        var region = target.Bounds;
        var layerMask = LayerMasks(layer, target.Logical, region);
        var opacity = layer.Opacity / 255f * (layer.FillOpacity / 255f);
        var mode = layer.BlendMode is PsdBlendMode.PassThrough or PsdBlendMode.Dissolve ? PsdBlendMode.Normal : layer.BlendMode;
        var gate = BlendIfOf(layer);
        var width = region.Width;
        Parallelism.For(region.Height, width, (start, end) =>
        {
            var alpha = new float[width];
            var r = new float[width];
            var g = new float[width];
            var b = new float[width];
            for (var y = region.Top + start; y < region.Top + end; y++)
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
        });
    }

    private static MaskChain? Chain(MaskChain? chain, MaskSampler? sampler) => sampler is null ? chain : new MaskChain(sampler, chain);

    /// <summary>
    /// Combined raster and vector mask of a layer over <paramref name="region"/>, or null when
    /// none applies. <paramref name="rows"/> (default: all of them) bounds the rows a caller
    /// will sample; a band render passes its band so unfeathered masks decode and rasterize
    /// only those rows. The raster always follows <paramref name="region"/>, the region of a
    /// whole render, so every row comes out bit-identical to that render.
    /// </summary>
    private MaskSampler? LayerMasks(PsdLayer layer, PsdRect region, PsdRect? rows = null)
    {
        var (top, bottom) = rows is { } window ? (window.Top, window.Bottom) : (int.MinValue, int.MaxValue);
        MaskSampler? result = null;
        if (layer.Mask is { } mask)
        {
            result = BandMask(layer, mask, top, bottom, () => MaskSampler.FromLayerMask(layer, mask, _canvas, top, bottom, _readers));
        }

        MaskSampler? vector = null;
        if (ShapeOwnsVectorMask(layer))
        {
            // A stroked shape's pixels already carry its path (LayerCompositor.Strokes.cs).
        }
        else if (layer.RenderedVectorMask is { } rendered)
        {
            vector = BandMask(layer, rendered, top, bottom, () => MaskSampler.FromRenderedVectorMask(layer, rendered, _canvas, top, bottom, _readers));
        }
        else if (layer.VectorMask is { } path && !region.IsEmpty)
        {
            var reach = layer.VectorMaskFeather > 0 ? (int)Math.Ceiling(layer.VectorMaskFeather * 3) + 1 : 0;
            // The vector feather does not clamp at the canvas: rasterize the path
            // past the edge so the blur sees real coverage there.
            var limit = new PsdRect(_canvas.Left - reach, _canvas.Top - reach, _canvas.Right + reach, _canvas.Bottom + reach);
            var vectorRegion = new PsdRect(region.Left - reach, region.Top - reach, region.Right + reach, region.Bottom + reach).Intersect(limit);
            vector = BandVectorMask(layer, vectorRegion, top, bottom, () => MaskSampler.FromVectorMask(path, vectorRegion, layer.VectorMaskDensity, layer.VectorMaskFeather, _canvas, top, bottom));
        }

        if (vector is null)
        {
            return result;
        }

        return result is null ? vector : MaskSampler.Multiply(result, vector);
    }

    /// <summary>
    /// Inflates the stored pixels of every layer the render can reach (visible, with
    /// visible ancestors and nonzero opacity) in parallel before compositing starts.
    /// The compositor keeps every decoded layer for the whole render anyway, so this
    /// changes when decoding happens, not how much memory it holds.
    /// </summary>
    private void PrefetchPixels(HashSet<PsdLayer>? only = null)
    {
        var layers = new List<PsdLayer>();
        foreach (var layer in ReachableLayers())
        {
            if (only is null || only.Contains(layer))
            {
                layers.Add(layer);
            }
        }

        if (layers.Count < 2)
        {
            return;
        }

        var decoded = new PlanarImage?[layers.Count];
        var succeeded = new bool[layers.Count];
        var area = 0L;
        foreach (var layer in layers)
        {
            area += (long)layer.Bounds.Width * layer.Bounds.Height;
        }

        Parallelism.For(layers.Count, area / layers.Count, (start, end) =>
        {
            for (var i = start; i < end; i++)
            {
                try
                {
                    decoded[i] = layers[i].DecodePixels();
                    succeeded[i] = true;
                }
                catch (PsdFormatException)
                {
                    // Left to LayerPixels: a damaged layer fails the render only if it is drawn.
                }
            }
        });

        for (var i = 0; i < layers.Count; i++)
        {
            if (succeeded[i])
            {
                _prefetched[layers[i]] = decoded[i];
            }
        }
    }

    /// <summary>Content layers with stored channels that the render can reach: visible with visible ancestors, nonzero opacity.</summary>
    private IEnumerable<PsdLayer> ReachableLayers()
    {
        foreach (var layer in _document.Layers)
        {
            if (layer.Kind is PsdLayerKind.Group or PsdLayerKind.Adjustment || layer.Bounds.IsEmpty || layer.Opacity == 0 || layer.Channels.Count == 0)
            {
                continue;
            }

            var reachable = true;
            for (var node = layer; node is not null && reachable; node = node.Parent)
            {
                reachable = IsVisible(node);
            }

            if (reachable)
            {
                yield return layer;
            }
        }
    }

    /// <summary>Decoded pixels for a content layer, synthesizing solid fills when the layer stores none.</summary>
    private PlanarImage? LayerPixels(PsdLayer layer)
    {
        if (_window is { } window && !_wholeLayers!.Contains(layer))
        {
            return BandPixels(layer, window);
        }

        if (_pixelCache.TryGetValue(layer, out var cached))
        {
            return cached;
        }

        var hasPrefetched = _prefetched.Remove(layer, out var prefetched);
        PlanarImage? Decode() => hasPrefetched ? prefetched : layer.DecodePixels();
        var pixels = layer.Kind == PsdLayerKind.Text && _options.TextRasterizer is { } textRasterizer ? TextLayerPixels(layer, textRasterizer, Decode) : Decode();
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
    /// aligned (the canvas otherwise), with Photoshop's eased two-stop ramp and
    /// dither (reference <c>vector_raster.cpp</c>, GdFl branch, <c>gradient_color_dithered</c>).
    /// The vector mask shapes it later.
    /// </summary>
    private PlanarImage? GradientFillPixels(PsdLayer layer, Descriptors.Descriptor descriptor) => GradientFillPixels(layer, descriptor, _canvas);

    /// <summary>The gradient fill over the canvas rows of <paramref name="rows"/> (logical bounds: the canvas).</summary>
    private PlanarImage? GradientFillPixels(PsdLayer layer, Descriptors.Descriptor descriptor, PsdRect rows)
    {
        var gradient = Gradient.FromDescriptor(descriptor, defaultAngle: 0);
        if (gradient is null)
        {
            return null;
        }

        var placement = gradient.AlignWithLayer && (PathGradientBounds(layer.VectorMask) ?? MaskGradientBounds(layer.Mask)) is { } shape ? shape : GradientBox.From(_canvas);
        var frame = gradient.Frame(placement, GradientSpan.CenterChord);
        var dither = descriptor.GetBoolean("Dthr");
        var pixels = new PlanarImage(_canvas.Intersect(rows)) { Logical = _canvas };
        var area = pixels.Bounds;
        Parallelism.For(area.Height, area.Width * 4L, (start, end) =>
        {
            for (var y = area.Top + start; y < area.Top + end; y++)
            {
                for (var x = area.Left; x < area.Right; x++)
                {
                    var position = frame.Position(x, y);
                    var (r, g, b) = gradient.Color(position, endpointSmoothing: true);
                    if (dither)
                    {
                        (r, g, b) = DitherGradientColor(r, g, b, x, y);
                    }

                    var i = pixels.RowOffset(y, x);
                    pixels.R[i] = r;
                    pixels.G[i] = g;
                    pixels.B[i] = b;
                    pixels.A[i] = gradient.Opacity(position, endpointSmoothing: true);
                }
            }
        });

        return pixels;
    }

    /// <summary>
    /// The bounds an aligned gradient fill spans: its path's exact bounds (anchors and
    /// control points, clipped to the canvas); null without a path. Photoshop measures
    /// the path itself, not its pixel hull: a 64 px rectangle spans 64 px although its
    /// edge is stored as 63.99999 (psd-tools' gradient-sizes.psd).
    /// </summary>
    private GradientBox? PathGradientBounds(Layers.VectorPath? path)
    {
        if (path is not { Subpaths.Count: > 0 })
        {
            return null;
        }

        double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
        foreach (var subpath in path.Subpaths)
        {
            foreach (var k in subpath.Knots)
            {
                left = Math.Min(left, Math.Min(k.X, Math.Min(k.InX, k.OutX)));
                top = Math.Min(top, Math.Min(k.Y, Math.Min(k.InY, k.OutY)));
                right = Math.Max(right, Math.Max(k.X, Math.Max(k.InX, k.OutX)));
                bottom = Math.Max(bottom, Math.Max(k.Y, Math.Max(k.InY, k.OutY)));
            }
        }

        left = Math.Max(left, _canvas.Left);
        top = Math.Max(top, _canvas.Top);
        right = Math.Min(right, _canvas.Right);
        bottom = Math.Min(bottom, _canvas.Bottom);
        return right > left && bottom > top ? new GradientBox((float)left, (float)top, (float)(right - left), (float)(bottom - top)) : null;
    }

    /// <summary>
    /// Without a path, an aligned gradient fill spans its raster mask's rectangle when the
    /// mask hides everything outside it (default color 0): psd-tools' 32-bit
    /// gradient-fill.psd runs its 90 degree ramp over the mask's 94 rows, not the canvas's 150.
    /// </summary>
    private GradientBox? MaskGradientBounds(LayerMask? mask)
    {
        if (mask is not { Disabled: false, DefaultColor: 0 } || mask.Bounds.Intersect(_canvas) is not { IsEmpty: false } bounds)
        {
            return null;
        }

        return GradientBox.From(bounds);
    }

    /// <summary>Pattern fill layers tile their pattern over the canvas; the vector mask shapes it later.</summary>
    private PlanarImage? PatternFillPixels(PsdLayer layer, Descriptors.Descriptor descriptor) => PatternFillPixels(layer, descriptor, _canvas);

    /// <summary>The pattern fill over the canvas rows of <paramref name="rows"/> (logical bounds: the canvas).</summary>
    private PlanarImage? PatternFillPixels(PsdLayer layer, Descriptors.Descriptor descriptor, PsdRect rows)
    {
        if (PatternPlacement.FromDescriptor(descriptor) is not { } placement || !_document.Patterns.TryGetValue(placement.PatternId, out var tile))
        {
            return null;
        }

        var sampler = new PatternSampler(tile, placement, layer);
        var pixels = new PlanarImage(_canvas.Intersect(rows)) { Logical = _canvas };
        var area = pixels.Bounds;
        Parallelism.For(area.Height, area.Width * 4L, (start, end) =>
        {
            for (var y = area.Top + start; y < area.Top + end; y++)
            {
                for (var x = area.Left; x < area.Right; x++)
            {
                var i = pixels.RowOffset(y, x);
                (pixels.R[i], pixels.G[i], pixels.B[i], pixels.A[i]) = sampler.Sample(x, y);
            }
        }
        });

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

        return LayerPixels(layer)?.Logical ?? default;
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
        Parallelism.For(region.Height, width, (start, end) =>
        {
            for (var y = region.Top + start; y < region.Top + end; y++)
        {
            var s = source.RowOffset(y, region.Left);
            var d = copy.RowOffset(y, region.Left);
            source.R.AsSpan(s, width).CopyTo(copy.R.AsSpan(d, width));
            source.G.AsSpan(s, width).CopyTo(copy.G.AsSpan(d, width));
            source.B.AsSpan(s, width).CopyTo(copy.B.AsSpan(d, width));
            source.A.AsSpan(s, width).CopyTo(copy.A.AsSpan(d, width));
        }
        });

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
