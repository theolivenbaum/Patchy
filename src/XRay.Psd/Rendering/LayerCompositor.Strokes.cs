using XRay.Psd.Descriptors;
using XRay.Psd.Imaging;
using XRay.Psd.Layers;

namespace XRay.Psd.Rendering;

/// <summary>
/// Shape layers with a vector stroke (<c>vstk</c>), following
/// <c>rasterize_vector_shape</c> in <c>.reference/src/core/vector_raster.cpp</c>:
/// the fill paints under the path's coverage (unless <c>fillEnabled</c> is off),
/// the stroke paints its own band on top with its opacity and blend mode, and
/// the result becomes the layer's pixels. The path is baked in here, so
/// <see cref="LayerMasks"/> skips the vector mask for these layers.
/// </summary>
internal sealed partial class LayerCompositor
{
    private const int MaxFeatherMargin = 256;

    private readonly HashSet<PsdLayer> _strokedShapes = [];

    /// <summary>Whether the layer's pixels already carry its path coverage (a stroked or fill-disabled shape).</summary>
    private bool ShapeOwnsVectorMask(PsdLayer layer) =>
        layer.VectorStroke is not null && LayerPixels(layer) is not null && _strokedShapes.Contains(layer);

    /// <summary>
    /// Renders a shape layer whose stroke is on or whose fill is off, or returns null so the
    /// plain fill-plus-vector-mask path handles it.
    /// </summary>
    private PlanarImage? StrokedShapePixels(PsdLayer layer)
    {
        if (layer.Kind != PsdLayerKind.Fill || layer.VectorStroke is not { } stroke || layer.VectorMask is not { } path)
        {
            return null;
        }

        var strokePaint = StrokePaint(layer, stroke);
        var strokeOn = stroke.Enabled && stroke.Width > 0 && stroke.Opacity > 0 && strokePaint.Key is not null
            && path.Subpaths.Count > 0 && !path.Disabled;
        var fillOn = stroke.FillEnabled;
        if (fillOn && !strokeOn)
        {
            return null;
        }

        // A feathered shape blurs fill and stroke together, rasterized past the canvas by
        // the blur reach so the canvas edge does not clamp it (reference feather_shape_raster).
        var radii = layer.VectorMaskFeather > 0 ? MaskSampler.BoxRadii(layer.VectorMaskFeather) : [0, 0, 0];
        var reach = radii[0] + radii[1] + radii[2];

        // The margin is capped so a huge feather cannot allocate an unbounded plane; past
        // it the blur clamps at the domain edge.
        var margin = Math.Min(reach, MaxFeatherMargin);
        var domain = new PsdRect(_canvas.Left - margin, _canvas.Top - margin, _canvas.Right + margin, _canvas.Bottom + margin);
        var image = new PlanarImage(domain);
        var width = domain.Width;
        var shapeBounds = path.Subpaths.Count > 0 ? path.Bounds.Intersect(_canvas) : _canvas;

        if (fillOn && PaintContent(layer, layer.ContentKey, layer.FillDescriptor, domain, shapeBounds) is { } fill)
        {
            // Density shows the fill everywhere at (255 - density) / 255, the vector-mask rule.
            var coverage = MaskSampler.FromVectorMask(path, domain, layer.VectorMaskDensity, 0, domain);
            for (var y = domain.Top; y < domain.Bottom; y++)
            {
                var o = fill.RowOffset(y, domain.Left);
                coverage?.MultiplyRow(y, domain.Left, fill.A.AsSpan(o, width));
            }

            fill.R.AsSpan().CopyTo(image.R);
            fill.G.AsSpan().CopyTo(image.G);
            fill.B.AsSpan().CopyTo(image.B);
            fill.A.AsSpan().CopyTo(image.A);
        }

        if (strokeOn)
        {
            CompositeStroke(image, layer, path, stroke, strokePaint);
        }

        if (reach > 0)
        {
            image = FeatherShape(image, radii, _canvas);
        }

        _strokedShapes.Add(layer);
        return image;
    }

    private void CompositeStroke(PlanarImage image, PsdLayer layer, VectorPath path, VectorStrokeStyle stroke, (string? Key, Descriptor? Content) paint)
    {
        var (coverage, rect) = VectorStroker.Rasterize(path, stroke, image.Bounds);
        var painted = TrimmedBounds(coverage, rect);
        if (painted.IsEmpty)
        {
            return;
        }

        // Aligned gradients span the stroke's own painted bounds.
        if (PaintContent(layer, paint.Key, paint.Content, painted, painted) is not { } content)
        {
            return;
        }

        var mode = stroke.BlendMode is PsdBlendMode.PassThrough or PsdBlendMode.Dissolve ? PsdBlendMode.Normal : stroke.BlendMode;
        var opacity = (float)stroke.Opacity;
        var width = painted.Width;
        var alpha = new float[width];
        for (var y = painted.Top; y < painted.Bottom; y++)
        {
            var s = content.RowOffset(y, painted.Left);
            var c = ((y - rect.Top) * rect.Width) + (painted.Left - rect.Left);
            var t = image.RowOffset(y, painted.Left);
            content.A.AsSpan(s, width).CopyTo(alpha);
            BlendKernels.MultiplyInPlace(alpha, coverage.AsSpan(c, width));
            BlendKernels.Scale(alpha, opacity);

            // Non-Normal stroke modes blend against the fill within the same raster.
            BlendKernels.CompositeRow(
                mode,
                content.R.AsSpan(s, width), content.G.AsSpan(s, width), content.B.AsSpan(s, width), alpha,
                image.R.AsSpan(t, width), image.G.AsSpan(t, width), image.B.AsSpan(t, width), image.A.AsSpan(t, width),
                clipMode: false);
        }
    }

    /// <summary>
    /// The stroke paint: <c>strokeStyleContent</c>, or the legacy <c>vscg</c> block (content
    /// key, then a versioned descriptor) for stroke-only shapes that omit it.
    /// </summary>
    private static (string? Key, Descriptor? Content) StrokePaint(PsdLayer layer, VectorStrokeStyle stroke)
    {
        if (stroke.ContentKey is not null)
        {
            return (stroke.ContentKey, stroke.Content);
        }

        if (layer.GetTaggedBlock("vscg") is { Data.Length: > 8 } legacy)
        {
            var key = PsdParser.DecodeLatin1(legacy.Data.Span[..4]);
            if (key is "SoCo" or "GdFl" or "PtFl" && PsdParser.TryReadDescriptor(legacy.Data, skip: 4) is { } descriptor)
            {
                return (key, descriptor);
            }
        }

        return (null, null);
    }

    /// <summary>
    /// Paints fill-layer content (<c>SoCo</c> solid, <c>GdFl</c> gradient, <c>PtFl</c> pattern)
    /// over <paramref name="rect"/>. Aligned gradients span <paramref name="alignBounds"/>.
    /// </summary>
    private PlanarImage? PaintContent(PsdLayer layer, string? key, Descriptor? descriptor, PsdRect rect, PsdRect alignBounds)
    {
        if (descriptor is null || rect.IsEmpty)
        {
            return null;
        }

        var pixels = new PlanarImage(rect);
        switch (key)
        {
            case "SoCo":
                {
                    if (descriptor.GetColor("Clr ") is not { } color)
                    {
                        return null;
                    }

                    pixels.Fill(color.R / 255f, color.G / 255f, color.B / 255f, 1f);
                    return pixels;
                }

            case "GdFl":
                {
                    if (Gradient.FromDescriptor(descriptor, defaultAngle: 0) is not { } gradient)
                    {
                        return null;
                    }

                    var placement = gradient.AlignWithLayer && !alignBounds.IsEmpty ? alignBounds : _canvas;
                    for (var y = rect.Top; y < rect.Bottom; y++)
                    {
                        for (var x = rect.Left; x < rect.Right; x++)
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

            case "PtFl":
                {
                    if (PatternPlacement.FromDescriptor(descriptor) is not { } placement || !_document.Patterns.TryGetValue(placement.PatternId, out var tile))
                    {
                        return null;
                    }

                    var sampler = new PatternSampler(tile, placement, layer);
                    for (var y = rect.Top; y < rect.Bottom; y++)
                    {
                        for (var x = rect.Left; x < rect.Right; x++)
                        {
                            var i = pixels.RowOffset(y, x);
                            (pixels.R[i], pixels.G[i], pixels.B[i], pixels.A[i]) = sampler.Sample(x, y);
                        }
                    }

                    return pixels;
                }

            default:
                return null;
        }
    }

    /// <summary>Bounds of the non-zero pixels of a coverage plane.</summary>
    private static PsdRect TrimmedBounds(float[] coverage, PsdRect rect)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (var y = 0; y < rect.Height; y++)
        {
            var row = coverage.AsSpan(y * rect.Width, rect.Width);
            var first = row.IndexOfAnyExcept(0f);
            if (first < 0)
            {
                continue;
            }

            minX = Math.Min(minX, first);
            maxX = Math.Max(maxX, row.LastIndexOfAnyExcept(0f));
            minY = Math.Min(minY, y);
            maxY = y;
        }

        return maxX < 0 ? default : new PsdRect(rect.Left + minX, rect.Top + minY, rect.Left + maxX + 1, rect.Top + maxY + 1);
    }

    /// <summary>
    /// Shape feather: the vector-mask gaussian (three box passes per axis) over the
    /// premultiplied shape raster, then cropped to <paramref name="clip"/>.
    /// </summary>
    private static PlanarImage FeatherShape(PlanarImage image, int[] radii, PsdRect clip)
    {
        var width = image.Width;
        var height = image.Height;
        for (var i = 0; i < image.A.Length; i++)
        {
            var a = image.A[i];
            image.R[i] *= a;
            image.G[i] *= a;
            image.B[i] *= a;
        }

        var scratch = new float[image.A.Length];
        foreach (var plane in new[] { image.R, image.G, image.B, image.A })
        {
            foreach (var radius in radii)
            {
                if (radius > 0)
                {
                    BoxBlur(plane, scratch, width, height, radius, horizontal: true);
                    BoxBlur(scratch, plane, width, height, radius, horizontal: false);
                }
            }
        }

        var cropped = new PlanarImage(clip);
        var cropWidth = clip.Width;
        for (var y = clip.Top; y < clip.Bottom; y++)
        {
            var s = image.RowOffset(y, clip.Left);
            var d = cropped.RowOffset(y, clip.Left);
            for (var x = 0; x < cropWidth; x++)
            {
                var a = Math.Clamp(image.A[s + x], 0f, 1f);
                var inverse = a > 1e-6f ? 1f / a : 0f;
                cropped.R[d + x] = Math.Clamp(image.R[s + x] * inverse, 0f, 1f);
                cropped.G[d + x] = Math.Clamp(image.G[s + x] * inverse, 0f, 1f);
                cropped.B[d + x] = Math.Clamp(image.B[s + x] * inverse, 0f, 1f);
                cropped.A[d + x] = a;
            }
        }

        return cropped;
    }

    // Edge-clamped running box filter from source into destination.
    private static void BoxBlur(float[] source, float[] destination, int width, int height, int radius, bool horizontal)
    {
        var length = horizontal ? width : height;
        var lines = horizontal ? height : width;
        var step = horizontal ? 1 : width;
        var inverse = 1f / ((2 * radius) + 1);
        for (var line = 0; line < lines; line++)
        {
            var baseIndex = horizontal ? line * width : line;
            float At(int index) => source[baseIndex + (Math.Clamp(index, 0, length - 1) * step)];
            var sum = 0f;
            for (var i = -radius; i <= radius; i++)
            {
                sum += At(i);
            }

            for (var i = 0; i < length; i++)
            {
                destination[baseIndex + (i * step)] = sum * inverse;
                sum += At(i + radius + 1) - At(i - radius);
            }
        }
    }
}
