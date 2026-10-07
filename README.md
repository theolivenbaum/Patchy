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
- Optional type-layer re-rendering (`XRay.Psd.Text`, SkiaSharp and HarfBuzz): draws type layers from their text, for files that store no text pixels or when the text should be redrawn with other fonts. It follows Photoshop's layout model for point, box and vertical text, leading, tracking, glyph scales, alignment, transforms and Warp Text. See `docs/text-rendering.md`.
- Typed views of the common image resources (resolution, guides and grid, thumbnail, slices, layer comps, print scale, pixel aspect ratio, version info, ICC profile, XMP, IPTC) and of each layer's effects.

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

    // Layer effects as a typed view (the raw descriptor stays in layer.Effects).
    foreach (var effect in layer.Style?.ActiveEffects ?? [])
    {
        Console.WriteLine($"  {effect.Kind} {effect.BlendMode} {effect.Opacity:P0} {effect.Color} size {effect.Size}");
    }
}

// Typed image resources, parsed on first access (null when absent or damaged).
var resources = document.Resources;
Console.WriteLine($"{resources.Resolution?.HorizontalPpi} ppi, written by {resources.VersionInfo?.ReaderName}");
foreach (var guide in resources.GridAndGuides?.Guides ?? [])
{
    Console.WriteLine($"{guide.Orientation} guide at {guide.Position} px");
}

foreach (var comp in resources.LayerComps?.Comps ?? [])
{
    Console.WriteLine($"layer comp \"{comp.Name}\"");
}

var thumbnailJpeg = resources.Thumbnail?.Data; // decode with XRay.Psd.Skia: resources.Thumbnail.Decode()
```

Re-rendering type layers with the optional `XRay.Psd.Text` package:

```csharp
using XRay.Psd.Text;

var fonts = new FontCollection();
fonts.AddFile("fonts/LiberationSans-Regular.ttf"); // ArialMT resolves to it through the metric-compatible aliases

using var rasterizer = new TextLayerRasterizer(new TextRenderSettings
{
    FontResolver = new CompositeFontResolver(fonts, SystemFontResolver.Default),
});

// Type layers without stored pixels are drawn from their text; Always redraws every type layer.
document.Render(new RenderOptions { TextRasterizer = rasterizer }).SavePng("filled-in.png");
document.Render(new RenderOptions { TextRasterizer = rasterizer, TextRasterMode = TextRasterMode.Always }).SavePng("redrawn.png");

// Fonts the resolver cannot find (those runs use the fallback face).
foreach (var layer in document.EnumerateLayersTopDown())
{
    if (layer.Text is { } text && rasterizer.Renderer.FindMissingFonts(text) is { Count: > 0 } missing)
    {
        Console.WriteLine($"{layer.Path}: missing {string.Join(", ", missing)}");
    }
}
```

The package needs the Skia and HarfBuzz native assets for the platform (for example `SkiaSharp.NativeAssets.Linux` and `HarfBuzzSharp.NativeAssets.Linux`).

`docs/api.md` lists every resource view and effect property.

Loading and threading:

```csharp
// Read asynchronously, or map a large file instead of reading it (dispose to release the mapping).
var fromStream = await PsdDocument.LoadAsync(stream, cancellationToken: token);
using var mapped = PsdDocument.Load("huge.psb", new PsdLoadOptions { MemoryMap = true });

// Rendering uses every processor by default; the result is the same at any degree.
var image = mapped.Render(new RenderOptions { MaxDegreeOfParallelism = 1 });
```

Files of 2 GB and more are always memory-mapped and parsed with 64-bit offsets. See `docs/performance.md` for the limits, the parallel model and the benchmarks.

Command line:

```bash
dotnet run --project tools/XRay.Psd.Cli -- info file.psd --resources --effects
dotnet run --project tools/XRay.Psd.Cli -- text file.psd
dotnet run --project tools/XRay.Psd.Cli -- render file.psd out.png --layers
```

## Building and testing

```bash
dotnet build XRay.Psd.slnx -c Release
dotnet test --solution XRay.Psd.slnx -c Release
```

Benchmarks (BenchmarkDotNet): `dotnet run -c Release --project benchmarks/XRay.Psd.Benchmarks -- --filter "*" --job short`.

Tests run every committed fixture in `tests/fixtures/psd` (parse, decode, truncation and seeded mutation robustness), compare renders with Photoshop's own captures, and cover the formats without fixtures through a synthetic PSD writer. Real 16/32-bit, grayscale, indexed, Lab, duotone, multichannel, bitmap and embedded PSB files come from the psd-tools collection (MIT), committed under `tests/fixtures/psd-tools/` (see `NOTICE`).

NuGet packages (`XRay.Psd`, `XRay.Psd.Skia`, `XRay.Psd.Text`, with `.snupkg` symbols and Source Link):

```bash
dotnet pack XRay.Psd.slnx -c Release -o artifacts
```

## Fuzzing

`fuzz/XRay.Psd.Fuzz` is a [SharpFuzz](https://github.com/Metalnem/sharpfuzz) harness over `PsdDocument.Load`, `Render` and `ExtractText`; any exception other than `PsdFormatException` counts as a crash. Build it, instrument the library, then run it under libFuzzer or AFL:

```bash
dotnet tool install --global SharpFuzz.CommandLine
dotnet build fuzz/XRay.Psd.Fuzz -c Release
sharpfuzz fuzz/XRay.Psd.Fuzz/bin/Release/net10.0/XRay.Psd.dll

# libFuzzer, with libfuzzer-dotnet from https://github.com/Metalnem/libfuzzer-dotnet/releases
./libfuzzer-dotnet --target_path=fuzz/XRay.Psd.Fuzz/bin/Release/net10.0/XRay.Psd.Fuzz -timeout=20 corpus/

# AFL++
afl-fuzz -i corpus/ -o findings/ -t 20000 -m none dotnet fuzz/XRay.Psd.Fuzz/bin/Release/net10.0/XRay.Psd.Fuzz.dll --afl

# Replay a saved input without a fuzzer
dotnet fuzz/XRay.Psd.Fuzz/bin/Release/net10.0/XRay.Psd.Fuzz.dll crash-file
```

Seed `corpus/` with a few small files from `tests/fixtures/psd`. `docs/testing.md` has the details.

## License

MIT, see `LICENSE`. Third-party test files and fonts are listed in `NOTICE`.
