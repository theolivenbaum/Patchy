using XRay.Psd.Imaging;
using XRay.Psd.Layers;

namespace XRay.Psd.Rendering;

internal sealed partial class LayerCompositor
{
    /// <summary>
    /// Type-layer content when <see cref="RenderOptions.TextRasterizer"/> is set: the
    /// rasterizer's output for <see cref="TextRasterMode.Always"/>, or when the stored
    /// pixels are missing or empty; otherwise the stored pixels.
    /// </summary>
    private PlanarImage? TextLayerPixels(PsdLayer layer, ITextLayerRasterizer rasterizer)
    {
        PlanarImage? stored = null;
        if (_options.TextRasterMode != TextRasterMode.Always)
        {
            stored = layer.DecodePixels();
            if (stored is not null && !IsEmptyCoverage(stored))
            {
                return stored;
            }
        }

        return rasterizer.Rasterize(layer) is { } raster ? ToPlanar(raster) : stored;
    }

    private static PlanarImage? ToPlanar(TextLayerRaster raster)
    {
        var bounds = raster.Bounds;
        if (bounds.IsEmpty)
        {
            return null;
        }

        var image = new PlanarImage(bounds);
        var pixels = raster.Image.Pixels;
        const float scale = 1f / 255f;
        for (int i = 0, p = 0; i < image.A.Length; i++, p += 4)
        {
            image.R[i] = pixels[p] * scale;
            image.G[i] = pixels[p + 1] * scale;
            image.B[i] = pixels[p + 2] * scale;
            image.A[i] = pixels[p + 3] * scale;
        }

        return image;
    }
}
