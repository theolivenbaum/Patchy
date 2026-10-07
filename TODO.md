# XRay.Psd port backlog

Ordered by impact within each section. The reference for every rendering item is the calibrated C++ code in `.reference/` (paths given). Check an item off by deleting it and noting anything non-obvious in `docs/`.

Use the survey (`XRAY_PSD_SURVEY=1`, see CLAUDE.md) to measure progress: each item below names the fixtures it should fix. When a fixture starts matching Photoshop, add it to `RenderingTests`.

## Done in the first pass

- PSD/PSB parsing: header, color mode data, image resources, layer records, mask data (all three layouts, mask parameters), tagged blocks (including PSB wide lengths and 4-byte padded global blocks), group tree, 16/32-bit layers in `Lr16`/`Lr32`, `lnk2`/`lnkD`/`lnk3`/`lnkE` linked files.
- Channel decoding: raw, PackBits, ZIP, ZIP with prediction at 1/8/16/32 bits; SIMD sample conversion.
- Color modes: RGB, grayscale, bitmap, indexed, CMYK, Lab (D50 to sRGB), duotone (as gray), multichannel (first channel).
- Descriptor and EngineData parsers; TySh text with style runs (character properties), paragraphs (indents, spacing, direction), fonts, orientation, warp, bounds; the global `Txt2` block as a typed model that fills TySh gaps; PS 5 `tySh` text with style runs, alignment and color. See `docs/text.md`.
- ICC color management: embedded profiles (resource 1039) convert gray, RGB, indexed and CMYK pixels to sRGB with relative colorimetric intent and black point compensation, matching the reference's lcms2 setup (`docs/color.md`).
- Text extraction: layer/group names, type layers, channel names, path names, slices, XMP and IPTC metadata, recursion into embedded PSD/PSB smart objects.
- Compositor: all 27 blend modes (SIMD), opacity and fill (special Fill on the eight modes), pass-through and isolated groups, clipping runs, raster masks (density, feather), vector masks (baked plane or rasterized, density, unclamped feather), solid fill layers, Dissolve, Blend If, channel restrictions.
- Adjustment layers at the reference's 8-bit LUT semantics: Levels, Curves (ACV body and `Crv ` extension), Hue/Saturation (`hue2` and `hue `: master, bands, colorize), Brightness/Contrast (legacy `brit` and modern `CgEd`), Exposure, Invert, Threshold, Posterize.
- Layer effects: drop shadow, outer/inner glow, inner shadow, color/gradient/pattern overlay, satin, strokes (solid, gradient, Shape Burst, Overprint knockout), bevel and emboss (all styles but Stroke Emboss, techniques, contour, texture, gloss), on layers, groups and clipping bases.
- Gradient (`GdFl`) and pattern (`PtFl`) fill layers; the `Patt` pattern store.
- Vector strokes on shape layers (`vstk`): width, alignment, caps, joins, miter limit, dashes, solid/gradient/pattern paint, opacity, blend mode, `fillEnabled`.
- Output: built-in PNG and JPEG encoders; optional SkiaSharp package.

## Rendering gaps (highest impact first)

1. **Remaining layer effects**: Stroke Emboss, non-linear contours on glows and shadows (`TrnS`), gradient glows, effect noise and jitter, the outer glow "Precise" technique, dithered gradients, and the stroke knockout for destination-pass overlays. Known residuals (also in the reference): spread-100 glow corner arcs, chisel pillow lit rims (`photoshop-gloss-contour`), bevel contour plus texture combinations (`photoshop-bevel-subs`, mean 6), gloss on chisel bevels (`photoshop-bevel-gloss`, mean 3.7). Reference: `.reference/src/render/layer_compositor.hpp`, `.reference/docs/layer-effects-render.md`.
2. **Remaining adjustment layers**: Color Balance is parsed (`Adjustments.ParseColorBalance`) but not rendered: the reference models the midtones only, as a flat `round(slider * 2.55)` offset, which is far from Photoshop (max 98/255 on `photoshop-color-balance.psd`, against 29 for leaving the layer out). Photoshop's midtones offset is a per-channel bell curve: `v + slider * 0.64 * 4x(1 - x)` with `x = (v/255)^0.631` fits all 24 fixture samples within 1/255, but shadows, highlights and Preserve Luminosity (`photoshop-color-balance-full.psd`) need captures. Vibrance, Black and White, Channel Mixer, Selective Color, Gradient Map, Photo Filter and Color Lookup are not modeled in the reference either and render as no-ops. Adjustments in CMYK and gray documents run on RGB; the reference evaluates the channel-wise kinds on the inks through the document's ICC profile (see Color below).
3. **Noise gradients** (`ClNs`) in fills and overlays; they render as a gray ramp today. Reference: `gradient_noise_channel` in `.reference/src/core/blend_math.cpp`.
4. **Vector stroke residuals** (strokes themselves are done, see `docs/rendering.md`): interior overlays on a stroked shape should cover only the fill with the stroke composited above them, and a gradient or pattern fill's effect silhouette should be raised to its coverage (`.reference/docs/layer-effects-render.md`, the split planes in `rasterize_vector_shape`); a legacy `vscg` block used as the fill when no `SoCo`/`GdFl`/`PtFl` exists (the parser does not mark such layers as fills); Photoshop's closing of open contours in multi-subpath stroked shapes (`.reference/docs/open-path-strokes.md`; the reference keeps them open too); the about 1/32 px bias of Photoshop's band edges on half pixels; non-Normal stroke modes blend against the fill only, not the backdrop below the layer (as in the reference).
5. **Burn and dodge effects over transparency**: Color Burn, Linear Burn and Color Dodge effects over a partially transparent backdrop take the special-Fill split in the reference (`composite_effect_color` calls `composite_special_fill_color` with the effect alpha as Fill); `SpecialFill` has the kernels, `DrawEffect` still uses the plain kernel there.
6. **Knockout** (shallow/deep), which the reference does not model either.
7. **Photoshop 8-bit rounding**: the float pipeline is within 1/255 of Photoshop's integer kernels for single layers; deep stacks can drift by a few levels. An integer path for 8-bit documents would make results bit-exact where the reference is.
8. **Merged image matte**: `MergedImageDecoder` removes a white matte from partially transparent merged pixels (psd-tools convention). Confirm against a Photoshop-saved transparent document with soft edges; Patchy-written files store straight color.

## Color

- Descriptor and text colors in CMYK and gray documents (`CMYC`/`Grsc` effect colors, EngineData `/FillColor` types 2 and 0) still use the naive formulas; the reference converts them through the same ICC transform as the pixels (`photoshop-cmyk-style-colors.psd` pins the overlay at (143,123,92)). `IccSrgbTransform.Evaluate` can do it once descriptors can reach the document.
- CMYK patterns (`Patt` tiles in CMYK documents) and the legacy `lrFX` color-space ids are not color managed.
- ICC gaps: absolute colorimetric intent (treated as relative), float LUTs (`D2Bx`/`mpet`), named-color and device-link profiles, and LUT-only profiles for 32-bit gray or RGB. See [docs/color.md](docs/color.md).
- A real Photoshop fixture with a non-sRGB RGB profile (Adobe RGB, Display P3) and one with Dot Gain 20% gray; the reference pins its local `gray-ramp-dotgain20.psd` against Photoshop (128 to 149).
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
