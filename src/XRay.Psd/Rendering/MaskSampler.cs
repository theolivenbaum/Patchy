using XRay.Psd.Layers;

namespace XRay.Psd.Rendering;

/// <summary>
/// A coverage plane positioned in document space with a constant value
/// outside it: raster layer masks (default color outside the stored rect)
/// and rasterized vector masks. Density and feather are baked in on creation.
/// </summary>
internal sealed class MaskSampler
{
    private readonly float[] _plane;
    private readonly PsdRect _rect;
    private readonly float _outside;

    private MaskSampler(float[] plane, PsdRect rect, float outside)
    {
        _plane = plane;
        _rect = rect;
        _outside = outside;
    }

    public static MaskSampler Constant(float value) => new([], default, value);

    /// <summary>Builds the sampler for a raster mask, applying feather (gaussian sigma in px) and density.</summary>
    public static MaskSampler? FromLayerMask(PsdLayer layer, LayerMask mask, PsdRect canvas) => FromPlane(layer, mask, canvas, clampFeather: true);

    private static MaskSampler? FromPlane(PsdLayer layer, LayerMask mask, PsdRect canvas, bool clampFeather)
    {
        if (mask.Disabled)
        {
            return null;
        }

        var outside = mask.DefaultColor / 255f;
        var rect = mask.Bounds;
        var plane = rect.IsEmpty ? [] : layer.DecodeMask(mask) ?? [];
        if (plane.Length == 0)
        {
            rect = default;
        }

        if (mask.Feather > 0)
        {
            (plane, rect) = Feather(plane, rect, outside, mask.Feather, canvas, clampFeather);
        }

        if (mask.Density < 255)
        {
            var density = mask.Density / 255f;
            for (var i = 0; i < plane.Length; i++)
            {
                plane[i] = (plane[i] * density) + (1f - density);
            }

            outside = (outside * density) + (1f - density);
        }

        return new MaskSampler(plane, rect, outside);
    }

    /// <summary>Rasterizes a vector mask over <paramref name="region"/>; pixels outside the region are uncovered (or covered when inverted).</summary>
    public static MaskSampler? FromVectorMask(VectorPath path, PsdRect region, byte density, double feather, PsdRect canvas)
    {
        if (path.Disabled)
        {
            return null;
        }

        var coverage = PathRasterizer.RasterizePath(path, region);
        var outside = 0f;
        if (path.Inverted)
        {
            for (var i = 0; i < coverage.Length; i++)
            {
                coverage[i] = 1f - coverage[i];
            }

            outside = 1f;
        }

        var rect = region;
        if (feather > 0)
        {
            (coverage, rect) = Feather(coverage, rect, outside, feather, canvas, clampToCanvas: false);
        }

        if (density < 255)
        {
            var d = density / 255f;
            for (var i = 0; i < coverage.Length; i++)
            {
                coverage[i] = (coverage[i] * d) + (1f - d);
            }

            outside = (outside * d) + (1f - d);
        }

        return new MaskSampler(coverage, rect, outside);
    }

    /// <summary>Wraps Photoshop's baked vector-mask plane (unfeathered coverage), applying the vector feather and density.</summary>
    public static MaskSampler? FromRenderedVectorMask(PsdLayer layer, LayerMask mask, PsdRect canvas)
    {
        if (layer.VectorMask is { Disabled: true })
        {
            return null;
        }

        return FromPlane(layer, mask with { Disabled = false }, canvas, clampFeather: false);
    }

    /// <summary>Value at one document pixel.</summary>
    public float Sample(int x, int y) => _rect.Contains(x, y) ? _plane[((y - _rect.Top) * _rect.Width) + (x - _rect.Left)] : _outside;

    /// <summary>Multiplies <paramref name="alpha"/> (document pixels <paramref name="x0"/>.. on row <paramref name="y"/>) by the mask.</summary>
    public void MultiplyRow(int y, int x0, Span<float> alpha)
    {
        var x1 = x0 + alpha.Length;
        if (y < _rect.Top || y >= _rect.Bottom || x1 <= _rect.Left || x0 >= _rect.Right)
        {
            BlendKernels.Scale(alpha, _outside);
            return;
        }

        var insideStart = Math.Max(x0, _rect.Left);
        var insideEnd = Math.Min(x1, _rect.Right);
        if (insideStart > x0)
        {
            BlendKernels.Scale(alpha[..(insideStart - x0)], _outside);
        }

        var row = _plane.AsSpan(((y - _rect.Top) * _rect.Width) + (insideStart - _rect.Left), insideEnd - insideStart);
        BlendKernels.MultiplyInPlace(alpha.Slice(insideStart - x0, insideEnd - insideStart), row);
        if (insideEnd < x1)
        {
            BlendKernels.Scale(alpha[(insideEnd - x0)..], _outside);
        }
    }

    /// <summary>
    /// Gaussian feather approximated by three box passes per axis (the "boxes for
    /// gauss" split). The plane grows by the blur reach with the outside value as
    /// padding. Raster masks clamp the blur at the canvas edge (no fade there,
    /// reference <c>compute_feathered_layer_mask</c>); vector masks do not: the
    /// coverage beyond the canvas is whatever the path says, so a path ending on
    /// the canvas edge fades there (reference <c>update_vector_mask_raster</c>).
    /// </summary>
    private static (float[] Plane, PsdRect Rect) Feather(float[] plane, PsdRect rect, float outside, double sigma, PsdRect canvas, bool clampToCanvas)
    {
        var radii = BoxRadii(sigma);
        var reach = radii[0] + radii[1] + radii[2];
        if (reach <= 0)
        {
            return (plane, rect);
        }

        var limit = clampToCanvas ? canvas : new PsdRect(canvas.Left - reach, canvas.Top - reach, canvas.Right + reach, canvas.Bottom + reach);
        var domain = rect.IsEmpty ? limit : new PsdRect(rect.Left - reach, rect.Top - reach, rect.Right + reach, rect.Bottom + reach).Intersect(limit);
        if (domain.IsEmpty)
        {
            return (plane, rect);
        }

        var width = domain.Width;
        var height = domain.Height;
        var grown = new float[width * height];
        grown.AsSpan().Fill(outside);
        var inside = rect.Intersect(domain);
        for (var y = inside.Top; y < inside.Bottom; y++)
        {
            plane.AsSpan(((y - rect.Top) * rect.Width) + (inside.Left - rect.Left), inside.Width)
                .CopyTo(grown.AsSpan(((y - domain.Top) * width) + (inside.Left - domain.Left)));
        }

        var scratch = new float[grown.Length];
        foreach (var radius in radii)
        {
            if (radius <= 0)
            {
                continue;
            }

            BoxPass(grown, scratch, width, height, radius, horizontal: true);
            BoxPass(scratch, grown, width, height, radius, horizontal: false);
        }

        return (grown, domain);
    }

    internal static int[] BoxRadii(double sigma)
    {
        const double passes = 3.0;
        var idealWidth = Math.Sqrt((12.0 * sigma * sigma / passes) + 1.0);
        var lower = (int)Math.Floor(idealWidth);
        if (lower % 2 == 0)
        {
            lower--;
        }

        lower = Math.Max(lower, 1);
        double l = lower;
        var narrow = ((12.0 * sigma * sigma) - (passes * l * l) - (4.0 * passes * l) - (3.0 * passes)) / ((-4.0 * l) - 4.0);
        var narrowPasses = Math.Clamp((int)Math.Floor(narrow + 0.5), 0, 3);
        var radii = new int[3];
        for (var pass = 0; pass < 3; pass++)
        {
            radii[pass] = ((pass < narrowPasses ? lower : lower + 2) - 1) / 2;
        }

        return radii;
    }

    // Edge-clamped running box filter from source into destination; lines are independent and run in parallel.
    private static void BoxPass(float[] source, float[] destination, int width, int height, int radius, bool horizontal)
    {
        var length = horizontal ? width : height;
        var lines = horizontal ? height : width;
        var step = horizontal ? 1 : width;
        var window = (2 * radius) + 1;
        var inverse = 1f / window;
        Parallelism.For(lines, length + (2 * radius), (first, last) =>
        {
            for (var line = first; line < last; line++)
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
        });
    }

    /// <summary>Combines this sampler with another by multiplication over the union of their planes.</summary>
    public static MaskSampler Multiply(MaskSampler a, MaskSampler b)
    {
        var rect = a._rect.Union(b._rect);
        if (rect.IsEmpty)
        {
            return Constant(a._outside * b._outside);
        }

        var plane = new float[rect.Width * rect.Height];
        for (var y = rect.Top; y < rect.Bottom; y++)
        {
            var row = plane.AsSpan((y - rect.Top) * rect.Width, rect.Width);
            row.Fill(1f);
            a.MultiplyRow(y, rect.Left, row);
            b.MultiplyRow(y, rect.Left, row);
        }

        return new MaskSampler(plane, rect, a._outside * b._outside);
    }
}
