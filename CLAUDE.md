# Patchy.Psd: C# PSD/PSB library

This repository is a .NET 10 port of the PSD/PSB reading and compositing parts of Patchy, a C++/Qt image editor. The original source tree lives, unmodified apart from disabled workflows, in `.reference/`. It is the behavioral reference for this port: read it, never build or edit it.

Read this file before any task. Keep it current and under 20,000 bytes; put detailed topic knowledge in `docs/<topic>.md` and link it here. `TODO.md` is the backlog; update it when you finish or discover work.

## Goals and scope

- Read PSD (version 1) and PSB (version 2) files: header, color mode data, image resources, layer tree, masks, vector masks, tagged blocks, linked/embedded files, merged image.
- Render a document to 8-bit sRGB RGBA and save it as PNG or JPEG.
- Extract all text: layer and group names, type-layer content with style runs and fonts, channel names, path names, slices, XMP/IPTC metadata, and text inside embedded PSD/PSB smart objects.
- Keep the core library dependency-free. SkiaSharp lives only in the optional `Patchy.Psd.Skia` package; HarfBuzz is not used yet (see TODO.md for when it would be).

## Layout

| Path | What |
|---|---|
| `src/Patchy.Psd/` | Core library, no package dependencies |
| `src/Patchy.Psd/IO/` | `BigEndianReader` (bounds-checked cursor), `ChannelCodec` (raw, PackBits RLE, ZIP, ZIP-with-prediction; SIMD sample conversion) |
| `src/Patchy.Psd/PsdParser.cs` | The five file sections, layer records, mask data, tagged blocks, group tree, `lnk2` linked files |
| `src/Patchy.Psd/Layers/` | `PsdLayer`, `LayerMask`, `VectorPath` (path records to pixel coordinates) |
| `src/Patchy.Psd/Descriptors/` | Action Manager descriptor reader (`Objc`, `VlLs`, `UntF`, `tdta`, `obj `...) |
| `src/Patchy.Psd/Text/` | `EngineData` parser, `TextLayerInfo` (TySh), `TextEngineBlock` (`Txt2`) and `TextEngineResolver`, `LegacyText` (PS 5 tySh), `TextExtractor`; see `docs/text.md` |
| `src/Patchy.Psd/Imaging/` | `RgbaImage` (public output), `PlanarImage` (internal float planes), `ColorSpaces` |
| `src/Patchy.Psd/Rendering/` | `LayerCompositor` (+ `.Effects.cs`), SIMD `BlendKernels`/`BlendOps`, `MaskSampler`, `PathRasterizer`, `EffectMasks`, `LayerEffects`, `Gradient`, `Patterns`, `StyleContour`, `Adjustments`, `MergedImageDecoder`, `PsdRenderer` |
| `src/Patchy.Psd/Codecs/` | Built-in `PngEncoder` and baseline `JpegEncoder` |
| `src/Patchy.Psd.Skia/` | Optional SkiaSharp interop (SKBitmap/SKImage, Skia encoders such as WebP) |
| `tools/Patchy.Psd.Cli/` | `psdtool info|text|render|layers` for inspection and manual checks |
| `tests/Patchy.Psd.Tests/` | xUnit v3 tests; `Support/PsdBuilder.cs` writes synthetic PSD/PSB files |
| `tests/Patchy.Psd.Skia.Tests/` | Skia interop tests; also decodes the built-in PNG/JPEG output with Skia |
| `tests/fixtures/psd/` | Committed PSD/PSB fixtures and Photoshop reference renders (`.bmp`) copied from `.reference/test-fixtures/psd` |
| `docs/porting-map.md` | Which reference files each C# area came from, and what was deliberately left out |
| `docs/rendering.md` | Compositor model, effect pipeline, calibration status |
| `docs/text.md` | Text model: TySh, `Txt2`, PS 5 `tySh`, gap filling and extraction |

## Commands

```bash
dotnet build Patchy.Psd.slnx -c Release                 # must report 0 warnings
dotnet test --solution Patchy.Psd.slnx -c Release        # Microsoft.Testing.Platform runner (global.json)
dotnet test --project tests/Patchy.Psd.Tests -c Release -- --filter-class "*RenderingTests"
PATCHY_PSD_SURVEY=1 dotnet test --project tests/Patchy.Psd.Tests -c Release -- --filter-method "*Survey"
dotnet run --project tools/Patchy.Psd.Cli -c Release -- info tests/fixtures/psd/arrows.psd
```

The survey writes `test-output/survey.txt` (layer compositor vs merged image vs Photoshop BMP for every fixture) plus `*.layers.png`/`*.merged.png`. Run it before and after any rendering change and compare. `test-output/` is gitignored.

## Rules

- Never modify `.reference/`. It is the record of the calibrated C++ behavior. When a rendering rule is unclear, find it there (start with `.reference/src/render/layer_compositor.hpp`, `.reference/src/core/blend_math.cpp`, `.reference/docs/blend-modes.md`, `.reference/docs/ps-compat.md`) and cite the source in a code comment.
- The core library takes no NuGet dependencies. System.IO.Compression (zlib) and System.Xml are in the BCL and allowed. Anything that needs SkiaSharp or HarfBuzz goes in a separate package.
- Zero build warnings across the solution. Fix the cause; scope any suppression to one line.
- Untrusted input: every length, count and offset from a file goes through `BigEndianReader` or an explicit check. Malformed input must throw `PsdFormatException` or degrade (skip the block, zero the damaged rows); any other exception type escaping `PsdDocument.Load`, `Render` or `ExtractText` is a bug. `ParsingTests.Every_fixture_survives_truncation` guards this.
- Pixel data stays compressed until rendering asks for it (`PsdLayer.DecodePixels`). Text extraction and inspection must not decode pixels.
- Hot loops are planar float and SIMD: use `System.Numerics.Vector<T>` (or `Vector128/256` where a fixed width fits, as in the JPEG DCT). Blend modes are `IBlendOp`/`IChannelBlend` structs with static abstract members so the JIT emits one specialized loop per mode; do not add per-pixel virtual dispatch or delegates.
- Determinism: no `Random` or `std`-style distributions in rendering. Dissolve uses the splitmix64 hash of the document coordinate, as in the reference.
- Public API: keep `PsdDocument`, `PsdLayer`, `RgbaImage`, `RenderOptions`, `PsdTextContent` stable; mark new internals `internal`. `PsdBlendMode` is append-only.
- Tests that need files outside the repository copy them into `local-test-fixtures/` (gitignored) first. Never hardcode machine paths.
- Commit only verified, finished work with a short subject line. No AI attribution or co-author trailers in commits or pull requests.
- User-facing docs (README, docs/) use plain prose: no em dashes, no hype words.

## Rendering model (summary)

`PsdDocument.Render()` defaults to `RenderSource.Auto`: the saved merged image when the version-info resource (1057) says it is real, else the layer compositor. The compositor works on straight-alpha float planes and follows the calibrated reference semantics for groups, clipping, masks, fills, adjustments and layer effects. The full model, effect order and calibration status are in [docs/rendering.md](docs/rendering.md); read it before touching `src/Patchy.Psd/Rendering/`.

Accuracy status per fixture is in the survey output; `RenderingTests` pins the fixtures that already match Photoshop.
