# Performance

How decoding, rendering and encoding use threads and SIMD, how loading avoids copies, and how to measure. Read this before adding a hot loop or a parallel loop.

## Benchmarks

`benchmarks/XRay.Psd.Benchmarks` is a BenchmarkDotNet project (the only project that may reference BenchmarkDotNet). It is in the solution and builds with it.

```bash
cd benchmarks/XRay.Psd.Benchmarks
dotnet run -c Release -- --filter "*" --job short          # everything, quick pass
dotnet run -c Release -- --filter "*ScalingBenchmarks*"    # one thread against all processors
dotnet run -c Release -- --write-synthetic /tmp/big.psd    # save the generated document
```

| Class | Covers |
|---|---|
| `LoadBenchmarks` | `Load(byte[])`, `Load(Stream)`, `ExtractText` |
| `FileLoadBenchmarks` | `Load(path)` read into an array, memory-mapped, and `LoadAsync` |
| `RenderBenchmarks` | `GetMergedImage`, every layer's `GetPixels`, the layer compositor |
| `EncodeBenchmarks` | `ToPng`, `ToJpeg` on a rendered document |
| `ScalingBenchmarks` | compositor, merged decode, PNG and JPEG at one thread and at the processor count |
| `EffectBenchmarks` | the compositor on effect-heavy documents at one thread and at the processor count |
| `BandBenchmarks` | whole render plus `ToPng` against `RenderBands` and `RenderToPng` |

Inputs are committed fixtures (`qual_rca_pinout.psd` 1745x1158 with styled type layers, `photoshop-bevel-texture-clouds.psd`, `arrows.psd`) and a synthetic 4000x3000 document (`synthetic-4000x3000.psd`, about 270 MB): six full-canvas RLE layers in Normal, Multiply, Screen, Overlay, Soft Light and Hard Light with partial alpha, one raster mask, and an RLE merged image, all filled with gradients plus hashed noise. It is built in memory by the test suite's `PsdBuilder` (linked into the project), so nothing large is committed. `EffectBenchmarks` adds a synthetic 2400x1600 document (`synthetic-effects-2400x1600.psd`, `--write-synthetic-effects <path>` saves it) whose two large soft-edged layers carry drop shadow, outer and inner glow, inner shadow, satin, strokes, a color overlay and smooth and chisel bevels, plus `photoshop-lmfx-multi-stroke.psd` and `photoshop-shape-strokes.psd`. Set `XRAY_PSD_FIXTURES` when running from outside the repository.

## Parallelism

All parallel loops go through `Parallelism.For(count, costPerItem, body)` (`src/XRay.Psd/Parallelism.cs`); nothing calls `Parallel.For` directly. The rules that keep results bit-identical to a sequential run:

- Items are independent: rows of a plane, lines of a separable blur, channels of a layer, layers to decode, MCU rows, deflate segments. A body writes only its own items and reads nothing another strip writes.
- The math per item is the same code whatever the split; strips only decide which thread runs it.
- Strip boundaries depend on the item count and cost, never on the thread count (at least 64 K work units per strip, at most 256 strips). Anything derived from the partition (the JPEG statistics strips) is the same on every machine.
- Reductions are integer or boolean (JPEG symbol counts, the bounding box of a matte, "some pixel changed" flags) or absent.
- A loop inside another loop's strip runs inline, so nesting never oversubscribes.
- An exception in a strip surfaces unwrapped (the lowest strip's), so `PsdFormatException` stays the only failure type for damaged input.

The degree comes from an ambient scope (`Parallelism.Use`, an `AsyncLocal`). `RenderOptions.MaxDegreeOfParallelism` sets it for `Render` (default: the processor count; 1 renders on the calling thread only). Encoders, `GetPixels` and `GetMergedImage` called directly use the processor count. Servers that render many documents at once may prefer `MaxDegreeOfParallelism = 1` and parallelism across documents.

What runs in parallel:

| Where | Unit |
|---|---|
| `LayerCompositor.PrefetchPixels` | every reachable layer's channel decode, before compositing (the compositor keeps every decoded layer for the whole render anyway, so peak memory does not grow) |
| `PsdLayer.DecodePixels` | the color and alpha channels of one layer |
| `ChannelCodec.DecodeRle` | rows (offsets from the row-count table first) |
| `ChannelCodec.UndoPrediction`, `ToFloat` | rows |
| `ColorSpaces` | Lab, indexed and 32-bit transfer conversions by pixel ranges; `IccSrgbTransform.Apply` by its existing chunks |
| `LayerCompositor` | rows of `CompositePixels`, `MergeImage`, `ApplyAdjustment`, group fades, crops, and gradient and pattern fill synthesis |
| `LayerCompositor.Effects.cs`, `.Bevel.cs` | rows of the effect matte, stroke bands and the Stroke Emboss surface, effect draws, the folded content pass, stroke restamps, shifted mattes, satin, bevel height fields, shading and textures |
| `LayerCompositor.Strokes.cs` | shape fill coverage, the stroke band composite, fill and stroke paint, the shape feather (premultiply, box lines, crop) |
| `PathRasterizer`, `VectorStroker` | bands of scanlines; each band rebuilds the active edge list it starts with, which is the list (in the same order) a walk from the first row holds there |
| `MaskSampler` | feather box passes by line |
| `EffectMasks` | dilation rows, tent blur lines, box blur lines, distance transform columns and rows, the stroke supersampling and read-back, element-wise passes |
| `PlanarImage.ToRgba`, `RgbaImage.Flatten` | rows |
| `PngEncoder` | row filtering, then deflate segments |
| `JpegEncoder` | color conversion, chroma downsampling, DCT and quantization by MCU row, symbol statistics |

Still sequential: zlib inflate of one channel (one stream), the layer tree walk, the Precise glow's chamfer distance and connected components (each pass depends on the previous pixel), `MaskSampler.Multiply`, and JPEG entropy coding.

Contributor rules: an `IAdjustment.Apply` implementation is called concurrently for different rows and must keep no mutable state outside the call. `CompositeLayerRow` and anything it calls may touch only the row it is given. A new parallel loop needs a test that compares it with `Parallelism.Use(1)`; `ParallelismTests` runs every fixture with the strip threshold lowered to one item, so even small fixtures split every loop.

## Vectorized loops

Every vector path below matches its scalar formula bit for bit (the JIT does not fuse multiply-adds, and integer scans wrap the same way in any order), which `ParallelismTests` checks against scalar references.

- `PlanarImage.ToRgba`: rounds the four planes and packs `r | g << 8 | b << 16 | a << 24` per 32-bit lane, zeroing fully transparent pixels.
- Lab to sRGB: the `f^-1`, white point and matrix steps run on `Vector<double>` in the scalar operation order; the sRGB transfer (`Math.Pow`) stays per lane.
- ZIP prediction: 8-bit and 16-bit rows (and the byte planes of 32-bit rows) undo the delta with a log-step prefix sum on `Vector128` (shuffles by 1, 2, 4, 8 lanes plus a broadcast carry); 16-bit words are byte-swapped in the same shuffle.
- PNG filters: Average uses `(a & b) + ((a ^ b) >> 1)`, Paeth computes `|b - c|`, `|a - c|`, `|a + b - 2c|` on 16-bit lanes, and the filter score sums `|(sbyte)v|` on widened lanes. The encoder knows every raw byte, so neither filter has a serial dependency.
- JPEG color conversion unpacks RGBA words into float lanes; `RgbaImage.IsOpaque` tests alpha four words at a time.

## PNG output

Rows are filtered in parallel into one buffer. When the filtered data exceeds 2 MiB it is deflated in segments of whole rows (about 1 MiB each, sized from the image alone) on separate threads; every segment but the last ends with a sync flush, so their concatenation is one valid deflate stream (the pigz layout), and the Adler-32 checksum is combined from the per-segment sums. Segments cannot reference data in the previous segment, which costs well under 1% in size. The bytes differ from the earlier single-stream output for large images but are identical for every degree of parallelism; smaller images still use one stream.

## Loading

- `Load(byte[])` and `Load(ReadOnlyMemory<byte>)` parse in place.
- `Load(Stream)` reads a seekable stream from its position straight into one array of the remaining length (one copy, none extra); other streams fill pooled 1 MiB chunks that are copied once into an exact array.
- `LoadAsync(path)` and `LoadAsync(stream)` read asynchronously (with cancellation) and then parse on the calling thread. Parsing touches only structure, so it is short.
- `PsdLoadOptions.MemoryMap` (path overloads) maps the file read-only (`IO/PsdSource.cs`). Pages load when parsing or decoding touches them, so opening a large document reads its records and little else, and channel data stays on disk until a render decodes it. The document then owns the mapping: `PsdDocument` implements `IDisposable`, and reading pixel data after `Dispose` throws `ObjectDisposedException` (every span access checks). Disposing is a no-op for documents in memory.
- Files larger than one array (2 GB and up) are always mapped, and the parser takes its windowed path (`PsdParser.ParseWindowed`): header, color mode data and resources parse from the first window, the layer and mask section is walked with 64-bit offsets, each channel's data becomes its own window at its file offset, and the global blocks after the layer info parse from one more window. `LoadingTests` runs every fixture through this path (threshold 0) and compares the result with the in-memory parse.

Payloads past one span (`IO/ByteRange.cs`): on the windowed path a layer channel longer than 2 GB keeps an empty `LayerChannel.Data` and an internal file range instead, and decoding reads it in windows: raw and RLE rows are addressable (RLE offsets come from the row-count table), ZIP streams inflate through a stream over the range. The merged image is a file range the same way (`PsdDocument.MergedImageData` keeps its first 2 GB only), and a 16/32-bit layer block (`Lr16`, `Lr32`, `Layr`) past 2 GB parses its records from a window at its start with channel data sliced from the file, as the layer info does; it is listed in `GlobalTaggedBlocks` without data. Other global blocks past 2 GB (an embedded smart object, for instance) are still skipped. `LoadingTests` runs every fixture and the psd-tools color-mode files with the span limit lowered to 64 bytes and compares channels, layer pixels, renders and text with the in-memory parse; `LargeDocumentTests` (`XRAY_PSD_LARGE=1`, about two minutes) writes a sparse 50000 x 43000 PSB with 2.15 GB raw channels and a 6.45 GB merged image, renders it from the layers and the merged image in bands and deletes it.

Decoding such a channel whole still needs one array for it, and `Render` needs one float plane and one `RgbaImage` for the canvas: `PlanarImage` counts pixels in an `int` (about 46000 x 46000) and `RgbaImage` holds 4 bytes per pixel in one array (about 23000 x 23000). Past those, `Render` throws `PsdFormatException` naming band rendering, which has no such limit (next section).

## Band rendering

`PsdDocument.RenderBands(options)` renders the document in horizontal bands of `RenderOptions.BandHeight` rows (default: about four million pixels per band) and yields each band as an `RgbaImage` with its first document row. `RenderToPng(stream or path)` streams those bands into a PNG. Each band is bit-identical to the same rows of `Render`, for every document, band height and degree of parallelism; `BandRenderingTests` checks every fixture with bands of 1 and 7 rows (small documents) or 13 and 97, from the layers and from the merged image, plus truncated and mutated fixtures and synthetic ZIP channels and feathered masks.

How it stays exact: no band-clipped buffer is ever blurred, dilated or distance-transformed, so no halo is needed. The canvas, isolated groups, clipping runs and every layer used only by row-local work (blending, adjustments, Blend If, unfeathered masks, solid, gradient and pattern fill synthesis) hold just the band's rows; decoding reads just those rows (`PsdLayer.DecodePixels(top, bottom)`, `ChannelCodec.DecodeLayerChannelRows`). Everything that reads across rows runs at the extent a whole render gives it, independent of the band: a layer with effects decodes whole and computes its effect planes over its whole effect domain; a group with effects composites its children into a whole silhouette (its descendants decode whole); feathered masks blur whole; stroked shapes and re-rendered type rasterize whole; vector masks rasterize over the region a whole render uses and keep the band's rows, which `PathRasterizer` computes independently with the same coordinates. Each `PlanarImage` carries its whole-render bounds (`Logical`), and every decision that depends on a buffer's extent reads those. Whether a fill layer stores coverage is decided on its whole alpha channel, scanned in strips. In a band, layers whose reach (pixels, effects and, for groups, children) misses the band are skipped.

Costs: memory is bounded by the band for documents built from plain layers, masks, adjustments and fills. Layers with effects, groups with effects, feathered masks, stroked shapes and re-rendered type keep their whole-extent planes, decoded once and cached across bands, as a whole render keeps them; effect planes are recomputed for every band the layer touches, so effect-heavy documents prefer tall bands (the default band of a 1745-pixel-wide document is the whole of `qual_rca_pinout`). ZIP channels keep their inflater from band to band (`ChannelReaders`), so a top-to-bottom render inflates each channel once; RLE and raw rows are read directly. The merged image path decodes the band's rows of every channel (RLE through precomputed channel offsets, ZIP through one stream per channel).

PNG output (`PngEncoder.RowWriter`): rows filter in parallel within a band (each strip packs the raw row above it), and the filtered bytes deflate in the segments `ToPng` uses (whole rows, about 1 MiB, fixed by the row size), several at a time in parallel, each written as its own IDAT chunk; the first starts with the zlib header and the last ends with the combined Adler-32. The concatenated IDAT data is the zlib stream `ToPng` writes for the same pixels, and the file does not depend on the band height or the degree. The writer must choose RGB or RGBA before the first row, so `RenderToPng` writes RGB only when the result is opaque by construction (a `Background`, or a merged image without transparency); `ToPng` drops alpha whenever every pixel is opaque.

## Measurements

October 2026, 4-core x64 container, .NET 10, BenchmarkDotNet `--job short --iterationCount 5`. The machine was shared with other builds (load average 14 to 26), so expect 10 to 30% noise; the baseline (commit `1edc804`) and the new code ran back to back. "All cores" is the default degree; "1 thread" is `MaxDegreeOfParallelism = 1` (encoders: `Parallelism.Use(1)`).

| Operation | Document | Before | After, 1 thread | After, all cores |
|---|---|---:|---:|---:|
| Layer composite | synthetic 4000x3000 | 1495 ms | 1227 ms | 470 ms |
| Layer composite | qual_rca_pinout | 87 ms | 65 ms | 78 ms |
| Merged image decode | synthetic 4000x3000 | 220 ms | 173 ms | 118 ms |
| Merged image decode | qual_rca_pinout | 37 ms | 28 ms | 22 ms |
| Every layer's `GetPixels` | synthetic 4000x3000 | 1534 ms | | 710 ms |
| Every layer's `GetPixels` | qual_rca_pinout | 63 ms | | 35 ms |
| PNG encode | synthetic 4000x3000 | 4627 ms | 3192 ms | 1012 ms |
| PNG encode | qual_rca_pinout | 805 ms | 648 ms | 199 ms |
| JPEG encode (q90) | synthetic 4000x3000 | 1316 ms | 1181 ms | 773 ms |
| JPEG encode (q90) | qual_rca_pinout | 142 ms | 129 ms | 78 ms |
| `Load(Stream)` | qual_rca_pinout (6 MB) | 11.3 ms, 13.0 MB | | 6.5 ms, 7.1 MB |
| `Load(Stream)` | synthetic (270 MB) | 120 ms, 530 MB | | 90 ms, 265 MB |
| `Load(path)` | synthetic (270 MB) | | 102 ms | |
| `Load(path)`, `MemoryMap` | synthetic (270 MB) | | 0.03 ms | |

`Load(byte[])` and `ExtractText` are unchanged (about 1 ms and 0.1 ms on qual_rca_pinout; they never touch pixels). qual_rca_pinout's composite is dominated by its styled type layers, whose effect passes stay sequential, so it gains little from more threads. The PNG encoder allocates one filtered copy of the image (36.7 MB instead of 18.6 MB on qual_rca_pinout); the segmented stream was 0.06% larger on the synthetic document. JPEG output is byte-identical to before.

### Effect loops and band rendering

October 2026, same container (load average 1 to 8 from other builds), BenchmarkDotNet `--job short --iterationCount 7`, the previous commit (`564fdca`) built from a copy and run back to back with this one. `EffectBenchmarks.LayerComposite`, all cores / 1 thread:

| Document | Before | After |
|---|---:|---:|
| synthetic effects 2400x1600 | 789 / 1282 ms | 791 / 1405 ms |
| qual_rca_pinout | 86 / 88 ms | 81 / 87 ms |
| photoshop-bevel-texture-clouds | 16.7 / 16.5 ms | 17.3 / 17.7 ms |
| photoshop-lmfx-multi-stroke | 31.3 / 35.8 ms | 32.3 / 37.7 ms |
| photoshop-shape-strokes | 4.9 / 4.9 ms | 5.9 / 5.2 ms |

The effect, stroke and fill loops now run in parallel, but on this machine the wall time did not move beyond noise (interleaved single-thread runs of the two builds on the synthetic document came out at 1100 to 1230 ms for both). The effect passes are memory-bound: the synthetic document allocates about 550 MB of planes per render, and at four threads the process spent about three times as much kernel time on page faults for those fresh planes as at one thread (2.0 s of CPU for 0.73 s of wall time, against 1.2 s for 1.2 s). Reusing effect planes instead of allocating them per pass is the next step (TODO.md).

`BandBenchmarks` (all cores, bands of 256 rows):

| Operation | synthetic 4000x3000 | qual_rca_pinout |
|---|---:|---:|
| `Render` + `ToPng` | 1788 ms, 3014 MB allocated | 377 ms, 159 MB |
| `RenderBands` (pixels only) | 846 ms, 2693 MB | 158 ms, 123 MB |
| `RenderToPng` | 1880 ms, 2991 MB | 452 ms, 161 MB |

Peak working set on the synthetic document (4 threads, process measurement): `Render` 6.4 GB, `RenderBands` with bands of 256 rows 1.8 GB. qual_rca_pinout pays for its styled type layers, whose effect planes are recomputed in each of the five bands they cross; with the default band height it renders in one band.
