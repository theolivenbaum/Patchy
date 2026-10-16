using XRay.Psd.Imaging;
using XRay.Psd.Layers;

namespace XRay.Psd.Rendering;

/// <summary>
/// Knockout (Advanced Blending, <c>knko</c>: 1 shallow, 2 deep), which the reference
/// does not model. Calibrated against the Photoshop merged images of psd-tools'
/// <c>transparency/knockout-*.psd</c> (see docs/rendering.md, Knockout):
/// <list type="bullet">
/// <item>A knockout element (layer, group or clipping run) composites against a
/// knockout backdrop K instead of the stack below it. Shallow takes K at the start
/// of the containing group: transparent inside an isolated group, the backdrop under
/// a Pass Through group, the clipping base in a clipping run. Deep passes through
/// Pass Through groups to the Background layer (transparent without one) and stops
/// at isolated groups. At the root both are the Background state.</item>
/// <item>The knockout shape m is the element's coverage times masks and opacity,
/// without Fill: Fill fades only the content drawn over K. The result is
/// <c>(1 - m) B + m (content at Fill over K)</c> in premultiplied color. The
/// composite is linear in its coverage, so with X the element composited over K
/// at coverage <c>m x Fill</c>, this equals <c>X + (1 - m)(B - K)</c>.</item>
/// </list>
/// </summary>
internal sealed partial class LayerCompositor
{
    private const byte KnockoutNone = 0;
    private const byte KnockoutShallow = 1;
    private const byte KnockoutDeep = 2;

    private bool? _hasKnockout;

    /// <summary>The buffer the current deep backdrop belongs to; a list on any other buffer is isolated.</summary>
    private PlanarImage? _knockoutTarget;

    /// <summary>The deep knockout backdrop of the current list (null is transparent).</summary>
    private PlanarImage? _deepBackdrop;

    private sealed class KnockoutScope
    {
        public PlanarImage? SavedTarget { get; init; }

        public PlanarImage? SavedDeep { get; init; }

        /// <summary>Knockout backdrops for the elements of this list (null is transparent).</summary>
        public PlanarImage? Shallow { get; set; }

        public PlanarImage? Deep { get; set; }

        public bool IsRoot { get; init; }
    }

    private byte KnockoutOf(PsdLayer layer)
    {
        if (layer.Kind == PsdLayerKind.Adjustment || layer.GetTaggedBlock("knko") is not { Data.Length: > 0 } block)
        {
            return KnockoutNone;
        }

        return block.Data.Span[0] switch
        {
            KnockoutShallow => KnockoutShallow,
            KnockoutDeep => KnockoutDeep,
            _ => KnockoutNone,
        };
    }

    private bool HasKnockout => _hasKnockout ??= _document.Layers.Any(l => KnockoutOf(l) != KnockoutNone);

    /// <summary>
    /// Photoshop's Background layer: the bottom root layer of a document whose layer
    /// count was positive (the merged image has no transparency channel).
    /// </summary>
    private bool IsBackgroundLayer(PsdLayer layer) =>
        !_document.MergedImageHasTransparency && _document.RootLayers.Count > 0 &&
        ReferenceEquals(_document.RootLayers[0], layer) && layer.Kind == PsdLayerKind.Pixel;

    private KnockoutScope? EnterKnockoutScope(PlanarImage target, IReadOnlyList<PsdLayer> siblings)
    {
        if (!HasKnockout)
        {
            return null;
        }

        var root = _knockoutTarget is null && ReferenceEquals(siblings, _document.RootLayers);
        var passThrough = !root && ReferenceEquals(_knockoutTarget, target);
        var scope = new KnockoutScope { SavedTarget = _knockoutTarget, SavedDeep = _deepBackdrop, IsRoot = root };
        if (passThrough)
        {
            scope.Deep = _deepBackdrop;
            if (siblings.Any(s => KnockoutOf(s) == KnockoutShallow && IsVisible(s)))
            {
                scope.Shallow = Crop(target, target.Bounds);
            }
        }

        // Isolated lists (and the root before its Background) knock out to transparency.
        _knockoutTarget = target;
        _deepBackdrop = scope.Deep;
        return scope;
    }

    private void ExitKnockoutScope(KnockoutScope? scope)
    {
        if (scope is not null)
        {
            _knockoutTarget = scope.SavedTarget;
            _deepBackdrop = scope.SavedDeep;
        }
    }

    /// <summary>After the root Background layer composites, both backdrops become its state.</summary>
    private void NoteKnockoutBackground(KnockoutScope? scope, PlanarImage target, PsdLayer layer)
    {
        if (scope is { IsRoot: true } && IsBackgroundLayer(layer))
        {
            scope.Shallow = scope.Deep = Crop(target, target.Bounds);
            _deepBackdrop = scope.Deep;
        }
    }

    /// <summary>Composites one element of a list, through its knockout when it has one.</summary>
    private void CompositeKnockoutElement(PlanarImage target, PsdLayer layer, KnockoutScope? scope, MaskChain? masks, Action draw)
    {
        var kind = scope is null ? KnockoutNone : KnockoutOf(layer);
        if (kind == KnockoutNone || !IsVisible(layer) || layer.Opacity == 0)
        {
            draw();
            return;
        }

        WithKnockout(target, layer, kind == KnockoutDeep ? scope!.Deep : scope!.Shallow, masks, draw);
    }

    /// <summary>
    /// Composites the members of a clipping run into <paramref name="run"/>: a knockout
    /// member knocks out to the run as it stood after the base (the end of the clipping group).
    /// </summary>
    private void CompositeClipMembers(PlanarImage run, IEnumerable<PsdLayer> members)
    {
        PlanarImage? baseState = null;
        foreach (var member in members)
        {
            if (HasKnockout && KnockoutOf(member) != KnockoutNone && IsVisible(member) && member.Opacity > 0)
            {
                baseState ??= run.Clone();
                WithKnockout(run, member, baseState, null, () => CompositeLayer(run, member, null, null, clipMode: true));
            }
            else
            {
                CompositeLayer(run, member, null, null, clipMode: true);
            }
        }
    }

    private void WithKnockout(PlanarImage target, PsdLayer layer, PlanarImage? backdrop, MaskChain? masks, Action draw)
    {
        var region = (layer.Kind == PsdLayerKind.Group ? target.Bounds : AffectedBounds(layer)).Intersect(target.Bounds);
        if (region.IsEmpty)
        {
            draw();
            return;
        }

        var shape = KnockoutShape(layer, region, masks);
        var before = Crop(target, region);
        var width = region.Width;
        for (var y = region.Top; y < region.Bottom; y++)
        {
            var t = target.RowOffset(y, region.Left);
            if (backdrop is null)
            {
                target.R.AsSpan(t, width).Clear();
                target.G.AsSpan(t, width).Clear();
                target.B.AsSpan(t, width).Clear();
                target.A.AsSpan(t, width).Clear();
            }
            else
            {
                var k = backdrop.RowOffset(y, region.Left);
                backdrop.R.AsSpan(k, width).CopyTo(target.R.AsSpan(t, width));
                backdrop.G.AsSpan(k, width).CopyTo(target.G.AsSpan(t, width));
                backdrop.B.AsSpan(k, width).CopyTo(target.B.AsSpan(t, width));
                backdrop.A.AsSpan(k, width).CopyTo(target.A.AsSpan(t, width));
            }
        }

        draw();

        for (var y = region.Top; y < region.Bottom; y++)
        {
            var t = target.RowOffset(y, region.Left);
            var b = before.RowOffset(y, region.Left);
            var k = backdrop?.RowOffset(y, region.Left) ?? 0;
            var m = (y - region.Top) * width;
            for (var i = 0; i < width; i++)
            {
                var rest = 1f - Math.Clamp(shape[m + i], 0f, 1f);
                if (rest <= 0f)
                {
                    continue;
                }

                float kr = 0f, kg = 0f, kb = 0f, ka = 0f;
                if (backdrop is not null)
                {
                    ka = backdrop.A[k + i];
                    kr = backdrop.R[k + i] * ka;
                    kg = backdrop.G[k + i] * ka;
                    kb = backdrop.B[k + i] * ka;
                }

                var ba = before.A[b + i];
                var xa = target.A[t + i];
                var a = Math.Clamp(xa + (rest * (ba - ka)), 0f, 1f);
                if (a <= 0f)
                {
                    target.R[t + i] = target.G[t + i] = target.B[t + i] = target.A[t + i] = 0f;
                    continue;
                }

                target.R[t + i] = Math.Clamp(((target.R[t + i] * xa) + (rest * ((before.R[b + i] * ba) - kr))) / a, 0f, 1f);
                target.G[t + i] = Math.Clamp(((target.G[t + i] * xa) + (rest * ((before.G[b + i] * ba) - kg))) / a, 0f, 1f);
                target.B[t + i] = Math.Clamp(((target.B[t + i] * xa) + (rest * ((before.B[b + i] * ba) - kb))) / a, 0f, 1f);
                target.A[t + i] = a;
            }
        }
    }

    /// <summary>
    /// The knockout shape over <paramref name="region"/>: the element's alpha (a group's
    /// isolated content) times its masks, the parent mask chain and opacity, not Fill.
    /// </summary>
    private float[] KnockoutShape(PsdLayer layer, PsdRect region, MaskChain? masks)
    {
        var shape = new float[region.Width * region.Height];
        PlanarImage? content;
        if (layer.Kind == PsdLayerKind.Group)
        {
            var bounds = ContentBounds(layer).Intersect(region);
            if (bounds.IsEmpty)
            {
                return shape;
            }

            content = new PlanarImage(bounds);
            CompositeList(content, layer.Children, null);
        }
        else
        {
            content = LayerPixels(layer);
        }

        var inner = content is null ? default : content.Bounds.Intersect(region);
        if (inner.IsEmpty)
        {
            return shape;
        }

        var layerMask = LayerMasks(layer, inner);
        var opacity = layer.Opacity / 255f;
        var row = new float[inner.Width];
        for (var y = inner.Top; y < inner.Bottom; y++)
        {
            content!.A.AsSpan(content.RowOffset(y, inner.Left), inner.Width).CopyTo(row);
            layerMask?.MultiplyRow(y, inner.Left, row);
            for (var chain = masks; chain is not null; chain = chain.Next)
            {
                chain.Sampler.MultiplyRow(y, inner.Left, row);
            }

            var offset = ((y - region.Top) * region.Width) + (inner.Left - region.Left);
            for (var i = 0; i < row.Length; i++)
            {
                shape[offset + i] = row[i] * opacity;
            }
        }

        return shape;
    }
}
