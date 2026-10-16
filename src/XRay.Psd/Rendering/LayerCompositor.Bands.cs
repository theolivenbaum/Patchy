using XRay.Psd.Imaging;
using XRay.Psd.IO;
using XRay.Psd.Layers;

namespace XRay.Psd.Rendering;

/// <summary>
/// Band rendering: the document composites in horizontal bands of full width, and every
/// band comes out bit-identical to the same rows of a whole-canvas render.
/// <para>
/// The canvas, isolated groups, clipping runs and the layers that only take part in
/// row-local work (blending, adjustments, Blend If, unfeathered masks, fill synthesis)
/// hold just the band's rows. Everything that reads across rows runs at the extent a
/// whole render gives it, independent of the band: a layer with effects decodes whole and
/// computes its effect planes over its whole effect domain, a group with effects
/// composites its children into a whole silhouette, feathered masks blur whole, stroked
/// shapes and re-rendered text rasterize whole, and vector masks rasterize over the
/// whole-render region (keeping only the band's rows, which the rasterizer computes
/// independently). A <see cref="PlanarImage"/> carries its whole-render bounds in
/// <see cref="PlanarImage.Logical"/>, and every decision that depends on a buffer's
/// extent reads those. No halo is needed, because no band-clipped buffer is ever blurred,
/// dilated or distance-transformed.
/// </para>
/// <para>
/// Memory is bounded by the band for documents built from plain layers; layers with
/// effects, styled groups, feathered masks, stroked shapes and re-rendered type keep
/// their whole-extent planes (cached across bands), as a whole render does.
/// </para>
/// </summary>
internal sealed partial class LayerCompositor : IDisposable
{
    private PsdRect? _window;
    private HashSet<PsdLayer>? _wholeLayers;
    private ChannelReaders? _readers;
    private readonly Dictionary<PsdLayer, PlanarImage?> _bandPixels = [];
    private readonly Dictionary<PsdLayer, PlanarImage?> _bandPrefetched = [];
    private readonly Dictionary<PsdLayer, bool> _storedCoverageEmpty = [];
    private readonly Dictionary<PsdLayer, PsdRect> _reachBounds = [];
    private readonly Dictionary<(PsdLayer Layer, LayerMask Mask), MaskSampler?> _wholeMasks = [];
    private readonly Dictionary<(PsdLayer Layer, PsdRect Region), MaskSampler?> _wholeVectorMasks = [];

    /// <summary>
    /// Prepares band rendering: finds the layers that must decode whole (see the class
    /// remarks) and decodes them, once for every band.
    /// </summary>
    public void BeginBands()
    {
        _wholeLayers = [];
        _readers = new ChannelReaders();
        CollectWholeLayers(_document.RootLayers, insideStyledGroup: false);
        PrefetchPixels(_wholeLayers);
    }

    /// <summary>Composites the canvas rows of <paramref name="band"/>; the result holds just those rows.</summary>
    public PlanarImage RenderBand(PsdRect band)
    {
        band = band.Intersect(_canvas);
        _window = band;
        _bandPixels.Clear();
        _bandPrefetched.Clear();
        PrefetchBand(band);
        var canvas = new PlanarImage(band) { Logical = _canvas };
        CompositeList(canvas, _document.RootLayers, null);
        _bandPixels.Clear();
        return canvas;
    }

    public void Dispose()
    {
        _readers?.Dispose();
        _readers = null;
    }

    /// <summary>
    /// Layers whose cross-row work needs their whole pixels: anything with effects, every
    /// layer under a group with effects (they composite into the group's whole silhouette),
    /// shapes with a vector stroke, and type layers when a text rasterizer may draw them.
    /// </summary>
    private void CollectWholeLayers(IReadOnlyList<PsdLayer> layers, bool insideStyledGroup)
    {
        foreach (var layer in layers)
        {
            var styled = EffectsOf(layer) is not null;
            if (insideStyledGroup || styled
                || (layer.Kind == PsdLayerKind.Text && _options.TextRasterizer is not null)
                || (layer.Kind == PsdLayerKind.Fill && layer.VectorStroke is not null))
            {
                _wholeLayers!.Add(layer);
            }

            if (layer.Kind == PsdLayerKind.Group)
            {
                CollectWholeLayers(layer.Children, insideStyledGroup || styled);
            }
        }
    }

    /// <summary>
    /// The band's rows of a layer that decodes per band: its stored pixels, or the solid,
    /// pattern or gradient fill a fill layer synthesizes when it stores no coverage
    /// (decided on the whole stored layer, as a whole render decides it).
    /// </summary>
    private PlanarImage? BandPixels(PsdLayer layer, PsdRect window)
    {
        if (_bandPixels.TryGetValue(layer, out var cached))
        {
            return cached;
        }

        var pixels = _bandPrefetched.Remove(layer, out var prefetched) ? prefetched : layer.DecodePixels(window.Top, window.Bottom, _readers);
        if (layer.Kind == PsdLayerKind.Fill && StoredCoverageEmpty(layer))
        {
            if (layer.FillColor is { } color)
            {
                pixels = new PlanarImage(_canvas.Intersect(window)) { Logical = _canvas };
                pixels.Fill(color.R / 255f, color.G / 255f, color.B / 255f, 1f);
            }
            else if (layer.ContentKey == "PtFl" && layer.FillDescriptor is { } patternFill)
            {
                pixels = PatternFillPixels(layer, patternFill, window);
            }
            else if (layer.ContentKey == "GdFl" && layer.FillDescriptor is { } gradientFill)
            {
                pixels = GradientFillPixels(layer, gradientFill, window);
            }
        }

        _bandPixels[layer] = pixels;
        return pixels;
    }

    /// <summary>
    /// Whether a whole decode of the layer would be null or fully transparent (the
    /// <see cref="IsEmptyCoverage"/> test), found by scanning its alpha channel in strips
    /// so that no whole plane is held.
    /// </summary>
    private bool StoredCoverageEmpty(PsdLayer layer)
    {
        if (_storedCoverageEmpty.TryGetValue(layer, out var empty))
        {
            return empty;
        }

        empty = true;
        if (layer.HasStoredPixels)
        {
            var bounds = layer.Bounds;
            if (layer.GetChannel(-1) is null)
            {
                // Without an alpha channel the decoded alpha is 1 everywhere.
                empty = false;
            }
            else
            {
                using var readers = new ChannelReaders();
                var strip = Math.Max(1, (1 << 24) / Math.Max(1, bounds.Width));
                for (var top = bounds.Top; top < bounds.Bottom && empty; top += strip)
                {
                    var alpha = layer.DecodeChannelPlane(-1, bounds, top, Math.Min(bounds.Bottom, top + strip), readers);
                    empty = alpha is null || alpha.AsSpan().IndexOfAnyExceptInRange(float.NegativeInfinity, 0f) < 0;
                }
            }
        }

        _storedCoverageEmpty[layer] = empty;
        return empty;
    }

    /// <summary>A copy of <paramref name="source"/> that a band render limits to the band's rows (whole bounds stay its logical bounds).</summary>
    private PlanarImage BandCopy(PlanarImage source)
    {
        if (_window is not { } window)
        {
            return source.Clone();
        }

        var rows = source.Bounds.Intersect(window);
        var copy = new PlanarImage(rows) { Logical = source.Logical };
        var width = rows.Width;
        for (var y = rows.Top; y < rows.Bottom; y++)
        {
            var s = source.RowOffset(y, rows.Left);
            var d = copy.RowOffset(y, rows.Left);
            source.R.AsSpan(s, width).CopyTo(copy.R.AsSpan(d, width));
            source.G.AsSpan(s, width).CopyTo(copy.G.AsSpan(d, width));
            source.B.AsSpan(s, width).CopyTo(copy.B.AsSpan(d, width));
            source.A.AsSpan(s, width).CopyTo(copy.A.AsSpan(d, width));
        }

        return copy;
    }

    /// <summary>In a band render, whether nothing <paramref name="reach"/> covers lies in the rows <paramref name="target"/> holds.</summary>
    private bool OutsideBand(PlanarImage target, PsdRect reach) => _window is not null && reach.Intersect(target.Bounds).IsEmpty;

    /// <summary>
    /// Every pixel a layer can write, its effects and (for groups) its children's included:
    /// a superset used only to skip layers that cannot touch a band.
    /// </summary>
    private PsdRect ReachBounds(PsdLayer layer)
    {
        if (_window is null)
        {
            return _canvas;
        }

        if (_reachBounds.TryGetValue(layer, out var reach))
        {
            return reach;
        }

        if (layer.Kind == PsdLayerKind.Group)
        {
            reach = default;
            foreach (var child in layer.Children)
            {
                if (IsVisible(child))
                {
                    reach = reach.Union(ReachBounds(child));
                }
            }

            if (EffectsOf(layer) is { } fx && !reach.IsEmpty)
            {
                var r = EffectReach(fx);
                reach = reach.Union(new PsdRect(reach.Left - r, reach.Top - r, reach.Right + r, reach.Bottom + r));
            }
        }
        else
        {
            reach = AffectedBounds(layer);
        }

        _reachBounds[layer] = reach;
        return reach;
    }

    /// <summary>
    /// A raster or baked vector mask sampler. A band render builds unfeathered ones for the
    /// band's rows and keeps feathered ones (built whole) for every band.
    /// </summary>
    private MaskSampler? BandMask(PsdLayer layer, LayerMask mask, int top, int bottom, Func<MaskSampler?> build)
    {
        if (_window is null || MaskSampler.IsRowLocal(mask) || (top == int.MinValue && bottom == int.MaxValue))
        {
            return build();
        }

        if (!_wholeMasks.TryGetValue((layer, mask), out var sampler))
        {
            sampler = build();
            _wholeMasks[(layer, mask)] = sampler;
        }

        return sampler;
    }

    /// <summary>A vector mask sampler; feathered ones (built whole) are kept for every band.</summary>
    private MaskSampler? BandVectorMask(PsdLayer layer, PsdRect region, int top, int bottom, Func<MaskSampler?> build)
    {
        if (_window is null || layer.VectorMaskFeather <= 0 || (top == int.MinValue && bottom == int.MaxValue))
        {
            return build();
        }

        if (!_wholeVectorMasks.TryGetValue((layer, region), out var sampler))
        {
            sampler = build();
            _wholeVectorMasks[(layer, region)] = sampler;
        }

        return sampler;
    }

    /// <summary>Decodes the band's rows of every reachable per-band layer that crosses the band, in parallel.</summary>
    private void PrefetchBand(PsdRect band)
    {
        var layers = new List<PsdLayer>();
        foreach (var layer in ReachableLayers())
        {
            if (!_wholeLayers!.Contains(layer) && layer.Bounds.Top < band.Bottom && layer.Bounds.Bottom > band.Top)
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
        Parallelism.For(layers.Count, (long)band.Width * band.Height, (start, end) =>
        {
            for (var i = start; i < end; i++)
            {
                try
                {
                    decoded[i] = layers[i].DecodePixels(band.Top, band.Bottom, _readers);
                    succeeded[i] = true;
                }
                catch (PsdFormatException)
                {
                    // Left to BandPixels: a damaged layer fails the render only if it is drawn.
                }
            }
        });

        for (var i = 0; i < layers.Count; i++)
        {
            if (succeeded[i])
            {
                _bandPrefetched[layers[i]] = decoded[i];
            }
        }
    }
}
