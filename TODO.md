# XRay.Psd port backlog

Ordered by impact within each section. The reference for every rendering item is the calibrated C++ code in `.reference/` (paths given). Check an item off by deleting it and noting anything non-obvious in `docs/`.

Use the survey (`XRAY_PSD_SURVEY=1`, see CLAUDE.md) to measure progress: each item below names the fixtures it should fix. When a fixture starts matching Photoshop, add it to `RenderingTests`.

## Done in the first pass

- PSD/PSB parsing: header, color mode data, image resources, layer records, mask data (all three layouts, mask parameters), tagged blocks (including PSB wide lengths and 4-byte padded global blocks), group tree, 16/32-bit layers in `Lr16`/`Lr32`, `lnk2`/`lnkD`/`lnk3`/`lnkE` linked files.
- Channel decoding: raw, PackBits, ZIP, ZIP with prediction at 1/8/16/32 bits; SIMD sample conversion.
- Color modes: RGB, grayscale, bitmap, indexed, CMYK, Lab (D50 to sRGB), duotone (ink curves from the specification), multichannel (first three planes as CMY inks, as in the reference).
- Descriptor and EngineData parsers; TySh text with style runs (character properties), paragraphs (indents, spacing, direction), fonts, orientation, warp, bounds; the global `Txt2` block as a typed model that fills TySh gaps; PS 5 `tySh` text with style runs, alignment and color. See `docs/text.md`.
- ICC color management: embedded profiles (resource 1039) convert gray, RGB, indexed and CMYK pixels to sRGB with relative colorimetric intent and black point compensation, matching the reference's lcms2 setup; descriptor, text, legacy and pattern colors go through the same transform; absolute colorimetric, float `D2Bx` LUTs and 32-bit RGB with LUT-only profiles are supported (`docs/color.md`).
- Text extraction: layer/group names, type layers, channel names, path names, slices, XMP and IPTC metadata, recursion into embedded PSD/PSB smart objects.
- Compositor: all 27 blend modes (SIMD), opacity and fill (special Fill on the eight modes), pass-through and isolated groups, clipping runs, raster masks (density, feather), vector masks (baked plane or rasterized, density, unclamped feather), solid fill layers, Dissolve, Blend If, channel restrictions.
- Adjustment layers at the reference's 8-bit LUT semantics: Levels, Curves (ACV body and `Crv ` extension), Hue/Saturation (`hue2` and `hue `: master, bands, colorize), Brightness/Contrast (legacy `brit` and modern `CgEd`), Exposure, Invert, Threshold, Posterize.
- Layer effects: drop shadow, outer/inner glow, inner shadow, color/gradient/pattern overlay, satin, strokes (solid, gradient, Shape Burst, Overprint knockout), bevel and emboss (all styles including Stroke Emboss, techniques, contour, texture, gloss), Softer and Precise glows, dithered gradient overlays and strokes, on layers, groups and clipping bases.
- Gradient (`GdFl`) and pattern (`PtFl`) fill layers; the `Patt` pattern store.
- Vector strokes on shape layers (`vstk`): width, alignment, caps, joins, miter limit, dashes, solid/gradient/pattern paint, opacity, blend mode, `fillEnabled`.
- Output: built-in PNG and JPEG encoders; optional SkiaSharp package.
- Type-layer re-rendering in the optional `XRay.Psd.Text` package (HarfBuzz shaping, Skia outlines): point, box and vertical text, runs, leading, tracking, glyph scales, faux styles, alignment, transform, pixel-grid rounding, Warp Text, pluggable font resolution; plugged into the compositor through `RenderOptions.TextRasterizer`. See `docs/text-rendering.md`.

## Rendering gaps (highest impact first)

1. **Remaining layer effects**: non-linear contours on drop shadows, inner shadows and glows (`TrnS` and its `AntA`), gradient glows, and effect Noise (`Nose`) and Jitter (`ShdN`). The reference parses none of them (`parse_drop_shadow`, `parse_outer_glow` in `.reference/src/psd/psd_layer_styles.cpp`) and no fixture uses them, so each needs Photoshop captures before it can be modeled; today they render as Linear, solid color and 0. The "Precise" glow technique follows the reference's uncalibrated legacy falloff and needs captures too. Gradient fill layers (`GdFl`) and vector shape gradient fills ignore `Dthr`; the reference dithers them (`vector_raster.cpp`, `gradient_color_dithered`). Known residuals (also in the reference): spread-100 glow corner arcs, chisel pillow lit rims (`photoshop-gloss-contour`), bevel contour plus texture combinations (`photoshop-bevel-subs`, mean 6), gloss on chisel bevels (`photoshop-bevel-gloss`, mean 3.7). Reference: `.reference/src/render/layer_compositor.hpp`, `.reference/docs/layer-effects-render.md`.
2. **Remaining adjustment layers**: Color Balance is parsed (`Adjustments.ParseColorBalance`) but not rendered: the reference models the midtones only, as a flat `round(slider * 2.55)` offset, which is far from Photoshop (max 98/255 on `photoshop-color-balance.psd`, against 29 for leaving the layer out). Photoshop's midtones offset is a per-channel bell curve: `v + slider * 0.64 * 4x(1 - x)` with `x = (v/255)^0.631` fits all 24 fixture samples within 1/255, but shadows, highlights and Preserve Luminosity (`photoshop-color-balance-full.psd`) need captures. Vibrance, Black and White, Channel Mixer, Selective Color, Gradient Map, Photo Filter and Color Lookup are not modeled in the reference either and render as no-ops. Adjustments in CMYK and gray documents run on RGB; the reference evaluates the channel-wise kinds on the inks through the document's ICC profile (see Color below).
3. **Noise gradients** (`ClNs`) in fills and overlays; they render as a gray ramp today. Reference: `gradient_noise_channel` in `.reference/src/core/blend_math.cpp`.
4. **Vector stroke residuals** (strokes, split planes for interior overlays, the coverage effect silhouette and legacy `vscg` paint are done, see `docs/rendering.md`): Photoshop's closing of open contours in multi-subpath stroked shapes (`.reference/docs/open-path-strokes.md`; the reference keeps them open too and no committed fixture shows it); the about 1/32 px bias of Photoshop's band edges on half pixels (the reference does not model it either); non-Normal stroke modes blend against the fill only, not the backdrop below the layer (as in the reference). No Photoshop fixture has effects on a stroked or gradient-filled shape yet; `ShapeEffectTests` pins the reference rules synthetically.
6. **Knockout** (shallow/deep), which the reference does not model either.
7. **Photoshop 8-bit rounding**: the float pipeline is within 1/255 of Photoshop's integer kernels for single layers. No committed fixture shows stack drift beyond anti-aliasing: rounding straight RGB to bytes after every composite write, as the reference does, moved nine fixtures by at most 0.13 mean in both directions (see `docs/rendering.md`, Pixel model). Bit-exact output would need Photoshop's own integer kernels per mode, which the reference does not have either; revisit with a deep-stack Photoshop capture.
8. **Merged image matte of other writers**: Photoshop's white matte is confirmed and removed (`docs/rendering.md`, Sources), but Patchy writes straight color, so its transparent documents decode slightly light at soft edges. Patchy writes no version-info resource (1057) while Photoshop always does, which could select the convention; check other writers (GIMP, Krita) before relying on it.

## Color

- Legacy `lrFX` effects (PS 5 drop shadows, used by the reference only when a layer has no `lfx2`/`lmfx`) are not parsed. Their 10-byte colors would go through `LegacyText.ReadColor` with the document's `DocumentColors`, which already converts CMYK through the profile; the parsing belongs with the layer effects (`parse_lrfx_layer_style` in `.reference/src/psd/psd_layer_styles.cpp`).
- Real Photoshop fixtures: a non-sRGB RGB profile (Adobe RGB, Display P3), Dot Gain 20% gray (the reference pins its local `gray-ramp-dotgain20.psd`, 128 to 149; the test uses a stand-in profile fitted to those pins), and a duotone document with a Photoshop render to calibrate the ink model (overprint colors, dot gain field and color-book inks are not modeled).
- 32-bit documents use the reference's plain transfer (clamp and sRGB-encode); Photoshop's HDR toning options are not modeled, and the reference itself does not model them either. 32-bit gray with a LUT-only gray profile is read as linear luminance.
- Named-color and device-link profiles are not supported (they do not describe document pixels).
- Adjustment layers on inks: the reference runs the channel-wise adjustments of CMYK and gray documents on the inks through an `InkSpace` sampled from the profile in both directions (`build_cmyk_ink_space`, `.reference/src/core/ink_space.cpp`). That needs the PCS-to-device direction (`B2Ax`) evaluated to tables, which the port does not build yet.

## Text

- Type on a path: decode the path geometry (the TySh EngineData shape and the `Txt2` frame path) into a typed path once a Photoshop fixture with on-path text exists. Today such layers report `TextShapeKind.Other` and raw frame points, and `XRay.Psd.Text` does not draw them (it keeps the stored pixels); once the path is typed, lay glyphs along it.
- `Txt2` style keys without a pinned meaning (underline, strikethrough, caps, manual kerning) need single-setting captures before they can be mapped (`.reference/docs/txt2.md`, "Key map"). No committed fixture sets them, so they stay unmapped.
- A real Photoshop 5 `tySh` fixture (the reference's Title02.psd is not public); the tests build synthetic records.
- Text re-rendering (`XRay.Psd.Text`, see [docs/text-rendering.md](docs/text-rendering.md)) residuals, each needing Photoshop captures: Crisp/Strong stem fitting and Smooth's heavier stems; glyph x positions under a horizontal scale (`photoshop-text-hv-scale` looks fractional); horizontal overset (intersect or fit); manual kerning placement; Small Caps and super/subscript sizes from the document's preferences; the glyph stroke (`StrokeFlag`); vertical box text beyond the one capture; mixed-direction bidi lines. Not modeled yet: hyphenation, kinsoku, tab stops, justification spacing limits, list bullets, optical kerning.
- A fixture with Photoshop's own text in a font the tests can bundle (Liberation, Noto) would make the re-render comparable glyph for glyph; today Liberation Sans stands in for Arial (same advances, different outlines, IoU ceiling about 0.8).

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
