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

Inputs are committed fixtures (`qual_rca_pinout.psd` 1745x1158 with styled type layers, `photoshop-bevel-texture-clouds.psd`, `arrows.psd`) and a synthetic 4000x3000 document (`synthetic-4000x3000.psd`, about 270 MB): six full-canvas RLE layers in Normal, Multiply, Screen, Overlay, Soft Light and Hard Light with partial alpha, one raster mask, and an RLE merged image, all filled with gradients plus hashed noise. It is built in memory by the test suite's `PsdBuilder` (linked into the project), so nothing large is committed. Set `XRAY_PSD_FIXTURES` when running from outside the repository.

## Parallelism

All parallel loops go through `Parallelism.For(count, costPerItem, body)` (`src/XRay.Psd/Parallelism.cs`); nothing calls `Parallel.For` directly. The rules that keep results bit-identical to a sequential run:

- Items are independent: rows of a plane, lines of a separable blur, channels of a layer, layers to decode, MCU rows, deflate segments. A body writes only its own items and reads nothing another strip writes.
- The math per item is the same code whatever the split; strips only decide which thread runs it.
- Strip boundaries depend on the item count and cost, never on the thread count (at least 64 K work units per strip, at most 256 strips). Anything derived from the partition (the JPEG statistics strips) is the same on every machine.
- Reductions are integer (JPEG symbol counts) or absent.
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
| `LayerCompositor` | rows of `CompositePixels`, `MergeImage` and `ApplyAdjustment` |
| `MaskSampler` | feather box passes by line |
| `EffectMasks` | dilation rows, tent blur lines, box blur lines, distance transform columns and rows |
| `PlanarImage.ToRgba`, `RgbaImage.Flatten` | rows |
| `PngEncoder` | row filtering, then deflate segments |
| `JpegEncoder` | color conversion, chroma downsampling, DCT and quantization by MCU row, symbol statistics |

Still sequential: zlib inflate of one channel (one stream), the layer tree walk, effect passes in `LayerCompositor.Effects.cs`/`.Bevel.cs`/`.Strokes.cs` (only the `EffectMasks` helpers they call are parallel), path rasterization, and JPEG entropy coding.

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

Limits of the windowed path: a single channel larger than 2 GB reads as empty, the merged image decodes from at most 2 GB, and a 16/32-bit document whose `Lr16`/`Lr32` global block exceeds 2 GB loses its layers (the block is skipped). Rendering is limited separately by the canvas: `PlanarImage` holds whole-document float planes with an `int` pixel count, so documents above about 46000 x 46000 pixels cannot render, and memory use is 16 bytes per pixel per buffer. Tiling would lift both; see `TODO.md`.

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
