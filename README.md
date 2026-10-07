# XRay.Psd

A .NET 10 library for reading Photoshop documents (PSD and PSB), rendering them to PNG or JPEG, and extracting their text. The core package has no dependencies outside the .NET base class library. Hot paths use `System.Numerics.Vector<T>` SIMD.

It is a port of the PSD engine in Patchy, a C++ image editor whose source is kept in `.reference/` as the behavioral reference.

## Features

- PSD and PSB, 1/8/16/32 bits per channel, RGB, grayscale, bitmap, indexed, CMYK, Lab, duotone and multichannel.
- Raw, RLE, ZIP and ZIP-with-prediction channel data.
- Layer tree with groups, blend modes, opacity, fill, clipping, raster masks and vector masks.
- Rendering from the merged image Photoshop saved, or from the layers with a compositor that follows Photoshop's blending rules.
- Built-in PNG and JPEG encoders. The optional `XRay.Psd.Skia` package adds `SKBitmap` interop and Skia encoders such as WebP.
- Text extraction: layer and group names, type-layer content with fonts and style runs, channel and path names, slices, XMP and IPTC metadata, and text inside embedded PSD/PSB smart objects.

The layer compositor renders layer effects (shadows, glows, overlays, satin, strokes, bevel and emboss), adjustment layers (Levels, Curves, Hue/Saturation, Brightness/Contrast, Exposure, Invert, Threshold, Posterize) and solid, gradient and pattern fills. Not rendered from layers yet: vector strokes on shape layers, Blend If, some adjustments (Color Balance, Vibrance, Black and White, Channel Mixer, Selective Color, Gradient Map, Photo Filter). Documents saved with "Maximize Compatibility" render exactly through the merged image. See `TODO.md`.

## Usage

```csharp
using XRay.Psd;
using XRay.Psd.Rendering;

var document = PsdDocument.Load("poster.psd");

// Render and save. Auto uses Photoshop's merged image when the file has one.
document.Render().SavePng("poster.png");
document.Render(new RenderOptions { Background = PsdColor.White }).SaveJpeg("poster.jpg", quality: 85);

// Force the layer compositor, or hide layers.
var withoutText = document.Render(new RenderOptions
{
    LayerVisibility = layer => layer.IsVisible && layer.Kind != PsdLayerKind.Text,
});

// All text, in Layers panel order.
foreach (var item in document.ExtractText().Items)
{
    Console.WriteLine($"{item.Kind}: {item.Source}: {item.Text}");
}

// Walk the layer tree.
foreach (var layer in document.EnumerateLayersTopDown())
{
    Console.WriteLine($"{layer.Path} {layer.Kind} {layer.BlendMode} {layer.Bounds}");
    if (layer.Text is { } text)
    {
        Console.WriteLine($"  \"{text.Text}\" in {string.Join(", ", text.Fonts)}");
    }

    layer.GetPixels()?.SavePng($"{layer.Index}.png"); // the layer's own pixels, no masks
}
```

Command line:

```bash
dotnet run --project tools/XRay.Psd.Cli -- info file.psd
dotnet run --project tools/XRay.Psd.Cli -- text file.psd
dotnet run --project tools/XRay.Psd.Cli -- render file.psd out.png --layers
```

## Building and testing

```bash
dotnet build XRay.Psd.slnx -c Release
dotnet test --solution XRay.Psd.slnx -c Release
```

Tests run every committed fixture in `tests/fixtures/psd` (parse, decode, truncation robustness), compare renders with Photoshop's own captures, and cover the formats without fixtures through a synthetic PSD writer.

## License

MIT, see `LICENSE`.
