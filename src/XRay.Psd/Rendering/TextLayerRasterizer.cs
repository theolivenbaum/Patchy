using XRay.Psd.Imaging;
using XRay.Psd.Layers;

namespace XRay.Psd.Rendering;

/// <summary>
/// Draws a type layer from its text model (<see cref="PsdLayer.Text"/>) instead of
/// the pixels Photoshop stored. The core library has no font engine; the optional
/// <c>XRay.Psd.Text</c> package implements this with SkiaSharp and HarfBuzz.
/// Plug an implementation into <see cref="RenderOptions.TextRasterizer"/>.
/// </summary>
public interface ITextLayerRasterizer
{
    /// <summary>
    /// Rasterizes the layer's text in document pixels: glyph coverage in the fill color,
    /// without the layer's masks, opacity, blend mode or effects (the compositor applies
    /// those, as it does for stored pixels). Returns null to keep the stored pixels.
    /// </summary>
    TextLayerRaster? Rasterize(PsdLayer layer);
}

/// <summary>Which type layers <see cref="RenderOptions.TextRasterizer"/> draws.</summary>
public enum TextRasterMode
{
    /// <summary>Only type layers whose stored pixels are missing or fully transparent.</summary>
    MissingPixels,

    /// <summary>Every type layer; the stored pixels are ignored. Forces the layer compositor under <see cref="RenderSource.Auto"/>.</summary>
    Always,
}

/// <summary>A re-rendered type layer: straight-alpha sRGB pixels placed at (<see cref="Left"/>, <see cref="Top"/>) in document coordinates.</summary>
public sealed class TextLayerRaster
{
    public TextLayerRaster(int left, int top, RgbaImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        Left = left;
        Top = top;
        Image = image;
    }

    public int Left { get; }

    public int Top { get; }

    public RgbaImage Image { get; }

    /// <summary>The raster's rectangle in document coordinates.</summary>
    public PsdRect Bounds => new(Left, Top, Left + Image.Width, Top + Image.Height);
}
