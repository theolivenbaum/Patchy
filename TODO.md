# Patchy.Psd port backlog

Ordered by impact within each section. The reference for every rendering item is the calibrated C++ code in `.reference/` (paths given). Check an item off by deleting it and noting anything non-obvious in `docs/`.

Use the survey (`PATCHY_PSD_SURVEY=1`, see CLAUDE.md) to measure progress: each item below names the fixtures it should fix. When a fixture starts matching Photoshop, add it to `RenderingTests`.

## Done in the first pass

- PSD/PSB parsing: header, color mode data, image resources, layer records, mask data (all three layouts, mask parameters), tagged blocks (including PSB wide lengths and 4-byte padded global blocks), group tree, 16/32-bit layers in `Lr16`/`Lr32`, `lnk2`/`lnkD`/`lnk3`/`lnkE` linked files.
- Channel decoding: raw, PackBits, ZIP, ZIP with prediction at 1/8/16/32 bits; SIMD sample conversion.
- Color modes: RGB, grayscale, bitmap, indexed, CMYK (uncalibrated), Lab (D50 to sRGB), duotone (as gray), multichannel (first channel).
- Descriptor and EngineData parsers; TySh text with style runs (character properties), paragraphs (indents, spacing, direction), fonts, orientation, warp, bounds; the global `Txt2` block as a typed model that fills TySh gaps; PS 5 `tySh` text with style runs, alignment and color. See `docs/text.md`.
- Text extraction: layer/group names, type layers, channel names, path names, slices, XMP and IPTC metadata, recursion into embedded PSD/PSB smart objects.
- Compositor: all 27 blend modes (SIMD), opacity and fill, pass-through and isolated groups, clipping runs, raster masks (density, feather), vector masks (baked plane or rasterized), solid fill layers, Dissolve, Invert/Threshold/Posterize adjustments.
- Layer effects: drop shadow, outer/inner glow, inner shadow, color/gradient/pattern overlay, satin, strokes (solid, gradient, Shape Burst, Overprint knockout), bevel and emboss (all styles but Stroke Emboss, techniques, contour, texture, gloss), on layers, groups and clipping bases.
- Gradient (`GdFl`) and pattern (`PtFl`) fill layers; the `Patt` pattern store.
- Output: built-in PNG and JPEG encoders; optional SkiaSharp package.

## Rendering gaps (highest impact first)

1. **Remaining layer effects**: Stroke Emboss, non-linear contours on glows and shadows (`TrnS`), gradient glows, effect noise and jitter, the outer glow "Precise" technique, dithered gradients, and the stroke knockout for destination-pass overlays. Known residuals (also in the reference): spread-100 glow corner arcs, chisel pillow lit rims (`photoshop-gloss-contour`), bevel contour plus texture combinations (`photoshop-bevel-subs`, mean 6), gloss on chisel bevels (`photoshop-bevel-gloss`, mean 3.7). Reference: `.reference/src/render/layer_compositor.hpp`, `.reference/docs/layer-effects-render.md`.
2. **Adjustment layers**: Levels, Curves, Hue/Saturation (master, bands, colorize), Color Balance, Brightness/Contrast (legacy and modern), Exposure, Vibrance, Black and White, Channel Mixer, Selective Color, Gradient Map, Photo Filter, Color Lookup. Parsing reference: `.reference/src/psd/psd_adjustments.cpp`; math: `.reference/src/core/adjustment_layer.cpp`; calibration: `.reference/docs/adjustments-calibration.md`. Fixtures: `photoshop-curves-*`, `photoshop-hue-saturation-*` (bands/master have real merged images and BMPs), `photoshop-color-balance*`, `photoshop-brightness-contrast-*`, `photoshop-clipping-mask` (Levels).
3. **Noise gradients** (`ClNs`) in fills and overlays; they render as a gray ramp today. Reference: `gradient_noise_channel` in `.reference/src/core/blend_math.cpp`.
4. **Vector strokes** (`vstk` on shape layers: width, alignment, caps, joins, dashes). Reference: `.reference/src/core/vector_raster.cpp`, `.reference/src/psd/psd_vector.cpp`, `.reference/docs/vector-tools.md`. Fixtures: `photoshop-shape-strokes`, `patchy-open-path-strokes`.
5. **Blend If** (layer blending ranges) and **channel restrictions** (`brst`). Reference: `blend_if_*` in `.reference/src/core/blend_math.cpp`, `ChannelRestrictedTarget` in the compositor. Fixtures: `photoshop-blend-if-4b-roundtrip`, `photoshop-channel-restrictions`.
6. **Special Fill blend modes**: Color Burn, Linear Burn, Color Dodge, Linear Dodge, Difference, Vivid Light, Linear Light and Hard Mix treat Fill differently from Opacity (`composite_special_fill_rgb`, `.reference/docs/blend-modes.md`).
7. **Feather calibration**: raster and vector feather use the reference's three-box gaussian, but `photoshop-user-mask-params`, `photoshop-vector-mask-feather` and `photoshop-shape-feather` still differ from the BMPs (max 126 to 169). Compare against `.reference/src/core/layer_render_utils.cpp` and the vector feather path in `.reference/src/core/vector_raster.cpp`.
8. **Knockout** (shallow/deep), which the reference does not model either.
9. **Photoshop 8-bit rounding**: the float pipeline is within 1/255 of Photoshop's integer kernels for single layers; deep stacks can drift by a few levels. An integer path for 8-bit documents would make results bit-exact where the reference is.
10. **Merged image matte**: `MergedImageDecoder` removes a white matte from partially transparent merged pixels (psd-tools convention). Confirm against a Photoshop-saved transparent document with soft edges; Patchy-written files store straight color.

## Color

- ICC-based conversion for CMYK, Lab and gray (resource 1039). Today CMYK uses the naive inverse-ink formula. The reference uses lcms2 (`.reference/src/color/`); a managed ICC transform (matrix/TRC and LUT-based profiles) would keep the core dependency-free.
- Duotone rendering from the duotone specification in the color mode data.
- 32-bit documents: Photoshop's HDR toning for 8-bit conversion differs from the plain sRGB transfer used now.

## Text

- Type on a path: decode the path geometry (the TySh EngineData shape and the `Txt2` frame path) into a typed path once a Photoshop fixture with on-path text exists. Today such layers report `TextShapeKind.Other` and raw frame points.
- `Txt2` style keys without a pinned meaning (underline, strikethrough, caps, manual kerning) need single-setting captures before they can be mapped (`.reference/docs/txt2.md`, "Key map").
- A real Photoshop 5 `tySh` fixture (the reference's Title02.psd is not public); the tests build synthetic records.
- Optional text re-rendering for type layers without pixel data. This is the one place HarfBuzz (shaping) plus SkiaSharp (glyph rasterization) would be needed; keep it in a separate package.

## Performance and scale

- Parallelize the compositor by row strips (`Parallel.For`) once effects exist; the per-row kernels are already independent.
- Tile the canvas instead of whole-document float planes (16 bytes per pixel today) for very large PSBs.
- PSB files over 2 GB: memory-map instead of `File.ReadAllBytes`.
- Vectorize the remaining scalar loops: RGBA interleave in `PlanarImage.ToRgba`, Lab conversion, 16-bit prediction, PNG Paeth filter.
- Benchmarks (BenchmarkDotNet in a separate project) for decode, composite and encode.

## API and packaging

- Typed parsers for common image resources (resolution, guides, thumbnail, slices, layer comps) and for effect descriptors.
- `PsdDocument.LoadAsync` and a stream-based loader that avoids the extra copy.
- PSD writing (the reference writer is `.reference/src/psd/psd_document_io.cpp` plus `.reference/docs/ps-compat.md`; every written file must open in Photoshop without warnings).
- NuGet packaging (README, icon, source link) and a new CI workflow. The reference repository's workflows were disabled on purpose; add new ones under `.github/workflows/` only when asked.

## Testing

- Real Photoshop fixtures for 16-bit, 32-bit, grayscale, indexed, Lab, duotone and multichannel documents (only synthetic `PsdBuilder` files cover them now).
- A fuzzing harness (SharpFuzz) over `PsdDocument.Load`, `Render` and `ExtractText`.
- A real embedded PSB smart-object fixture (the recursion test uses a synthetic one).
