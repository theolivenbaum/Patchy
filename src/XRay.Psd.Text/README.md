# XRay.Psd.Text

Type-layer re-rendering for [XRay.Psd](https://www.nuget.org/packages/XRay.Psd), the dependency-free PSD/PSB reader and renderer for .NET. It draws Photoshop type layers from their parsed text with HarfBuzz shaping and SkiaSharp glyph rasterization, following Photoshop's layout model for point, box and vertical text, leading, tracking, alignment, the layer transform and Warp Text.

- `TextLayerRasterizer` plugs into `RenderOptions.TextRasterizer`. By default only type layers without stored pixels are drawn; `TextRasterMode.Always` redraws every type layer. Masks, effects, clipping and blend modes apply as usual.
- `TextLayerRenderer.Render(layer)` draws one layer on its own.
- `IFontResolver` maps PostScript font names to faces: `FontCollection` for font files you supply, `SystemFontResolver` for installed fonts, `CompositeFontResolver` to chain them. Metric-compatible aliases (Arial to Liberation Sans, Calibri to Carlito) are built in, and `FindMissingFonts` reports names that fall back.

```csharp
using XRay.Psd;
using XRay.Psd.Rendering;
using XRay.Psd.Text;

var fonts = new FontCollection();
fonts.AddFile("fonts/LiberationSans-Regular.ttf");

using var rasterizer = new TextLayerRasterizer(new TextRenderSettings
{
    FontResolver = new CompositeFontResolver(fonts, SystemFontResolver.Default),
});

var document = PsdDocument.Load("poster.psd");
document.Render(new RenderOptions { TextRasterizer = rasterizer }).SavePng("poster.png");
```

Applications add the Skia and HarfBuzz native asset packages for their platform (for example `SkiaSharp.NativeAssets.Linux` and `HarfBuzzSharp.NativeAssets.Linux`).

License: MIT.
