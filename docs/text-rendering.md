# Text rendering

How the optional `XRay.Psd.Text` package draws type layers from the parsed text model instead of the pixels Photoshop stored. Code lives in `src/XRay.Psd.Text/`; tests in `tests/XRay.Psd.Text.Tests/`. The text model itself (TySh, `Txt2`, PS 5 `tySh`) is described in [text.md](text.md). Read both before changing the layout.

## Why a separate package

The core library has no font engine and must stay dependency-free. Re-rendering needs HarfBuzz (shaping: kerning, ligatures, marks, Arabic joining) and a glyph rasterizer, so it lives in its own package on `SkiaSharp`, `SkiaSharp.HarfBuzz` and `HarfBuzzSharp` (same SkiaSharp version as `XRay.Psd.Skia`). The core only exposes a small hook.

## Core hook

`src/XRay.Psd/Rendering/TextLayerRasterizer.cs` defines:

- `ITextLayerRasterizer.Rasterize(PsdLayer)`: returns a `TextLayerRaster` (straight-alpha sRGB `RgbaImage` plus its document position) or null to keep the stored pixels. The raster is the glyph coverage in the fill color only: the compositor applies masks, opacity, Fill, blend mode, clipping and layer effects to it exactly as it does to stored pixels.
- `RenderOptions.TextRasterizer` and `RenderOptions.TextRasterMode`. `MissingPixels` (default) asks the rasterizer only for type layers whose stored pixels are missing or fully transparent, which is what files from tools that do not rasterize text look like. `Always` replaces every type layer and forces the layer compositor under `RenderSource.Auto`, since the merged image would otherwise hide the change.

The compositor change is one line in `LayerPixels` plus `LayerCompositor.Text.cs`. With no rasterizer set, rendering is byte-identical to before (the survey output did not change).

## Usage

```csharp
using XRay.Psd;
using XRay.Psd.Rendering;
using XRay.Psd.Text;

using var rasterizer = new TextLayerRasterizer(new TextRenderSettings
{
    FontResolver = new CompositeFontResolver(myFonts, SystemFontResolver.Default),
});
var image = document.Render(new RenderOptions { TextRasterizer = rasterizer, TextRasterMode = TextRasterMode.Always });

using var renderer = new TextLayerRenderer();
TextLayerRaster? raster = renderer.Render(layer);              // one layer, clipped to the document
IReadOnlyList<string> missing = renderer.FindMissingFonts(layer.Text!);
```

`TextLayerRenderer` also renders a bare `TextLayerInfo` (`Render(info, clip)`), which the tests use to build synthetic models. It caches faces and outlines and is used by one thread at a time (calls are serialized by a lock). `TextLayerRasterizer` turns layout failures on damaged text (argument, range, arithmetic and invalid-operation exceptions) into "keep the stored pixels", in line with the core's malformed-input rule.

## Font resolution

`IFontResolver.Resolve(FontRequest)` maps a run's font to an `SKTypeface`. `FontRequest.FromRun` takes the PostScript name and derives a family and bold/italic flags with the reference's suffix heuristic (`heuristic_resolved_photoshop_font` in `.reference/src/psd/psd_text_read.cpp`: strips `-BoldItalicMT`, `-Bold`, `-It`..., then `MT`/`PS`, and splits case changes, so `TimesNewRomanPSMT` becomes `Times New Roman`). Photoshop 5 faces use their stored family and style strings instead, as the reference does.

Resolvers:

- `FontCollection`: fonts the application registers (file, stream or typeface). Matches the PostScript name first (compact key: lowercase letters and digits), then family plus closest style, then `FontAliases`.
- `SystemFontResolver`: the platform font manager (fontconfig, DirectWrite, CoreText). A match is accepted only when the returned family really is the requested one (or an alias), so a missing font is reported instead of silently replaced by the platform default. Note that the `SkiaSharp.NativeAssets.Linux.NoDependencies` native build has an empty font manager; use the fontconfig build or a `FontCollection` on Linux.
- `CompositeFontResolver`: first match wins.
- `FontAliases`: the metric-compatible table from `.reference/docs/fonts.md` (Arial and Helvetica to Liberation Sans or Arimo, Times New Roman to Liberation Serif or Tinos, Courier New to Liberation Mono or Cousine, Calibri to Carlito, Cambria to Caladea, and so on). Metric compatibility keeps advances, and with them line breaks and placement, identical.

A run whose font does not resolve uses `TextRenderSettings.FallbackTypeface` (Skia's default when null) and is listed by `FindMissingFonts`. Per character, glyphs the run's face lacks come from the fallback face, then `GlyphFallbacks` in order, then (when `UseSystemGlyphFallback`) the platform's character match. A face that is not bold or italic while the name asks for it gets the faux styles below.

## Layout model

`TextLayoutEngine` (`TextLayout.cs`) follows the calibrated Photoshop rules of `.reference/docs/text-render-calibration.md` and `photoshop_text_layout_plan` / `vertical_text_layout_plan` in `.reference/src/ui/text_layout.cpp`:

- Engine units are document pixels before the layer transform. The transform (`xx xy yx yy tx ty`, `x' = xx x + yx y + tx`) maps text space to the document; sizes are never averaged over the axes.
- Paragraphs split at `\r` (and `\n`); ETX (`\u0003`, Shift+Enter), U+2028 and vertical tab break lines inside a paragraph. A trailing separator starts no line.
- Shaping: HarfBuzz per item (same style run, same face after glyph fallback, same caps/script scale and same bidi level), with `kern` from `AutoKerning`, `liga`/`clig` from `Ligatures`, `dlig` from `DiscretionaryLigatures`, and `vert` for vertical type. Positions are design units scaled by size times `HorizontalScale` (x) and `VerticalScale` (y).
- Point text: the first baseline is the origin. Justification decides whether the origin is the line start, middle or end; indents shift it.
- Box text: the first baseline is frame top plus space before plus the largest OS/2 `sTypoAscender` x size on line 1. Lines wrap greedily at spaces, after hyphens and slashes, and around CJK characters; No Break runs do not break; an overlong word breaks between characters; trailing spaces hang. Lines whose top falls below the frame are overset and not drawn (the reference gates on intersection, so a straddling line draws).
- Each following baseline advances by the largest effective leading among the entered line's characters (fixed `Leading`, else paragraph `AutoLeadingFraction` x `FontSize`, glyph scales excluded), plus space before and the previous paragraph's space after.
- Tracking and manual kerning add `FontSize x value / 1000` after each cluster except the line's last. Faux bold adds 0.03 em to every advance and strokes the outline with a 0.03 em pen; faux italic shears by tan 12 degrees (0.2126) about the baseline. Both are also used when the resolved face lacks the requested bold or italic.
- All Caps uppercases; Small Caps uppercases lowercase letters at 70%; superscript and subscript use Photoshop's default 58.3% size and 33.3% offset; baseline shift moves up. Underline and strikethrough use the face's own positions and thickness.
- Justify (JustifyLast*, JustifyAll) in box text stretches the spaces of every wrapped line to the frame width; the last line (and lines ending at a forced break) align as the variant says.
- Bidi: a two-level simplification of UAX 9. Hebrew, Arabic and related ranges are right-to-left, letters and digits left-to-right, neutrals take matching neighbors or the paragraph direction (`TextParagraph.Direction`, else the first strong character). Lines reorder by rule L2. In a right-to-left paragraph, Left and Right justification mean its start and end edges (the Hebrew capture's "Left" text ends at the anchor).
- Vertical type: em cells (`FontSize x VerticalScale`) stack downward; whitespace and rotated Roman glyphs advance by their horizontal width. Roman glyphs lie rotated 90 degrees clockwise unless the run's `BaselineDirection` is 1 (upright); CJK always stands. Upright glyphs center on the column axis with the ascent plus descent box centered in the cell; rotated glyphs center that box on the axis. Columns advance left by the entered column's largest leading. Point text puts the first axis on the origin, with left, center and right justification at the top, middle and bottom. Box text starts at the frame's right edge, wraps by whole cells at the frame height, and hides columns whose em box leaves the frame. On the `vt_box` capture Photoshop's first column sits 3.7 px right of the frame edge, exactly where its stored layout bounds put it, so the stored `Bounds.Right` wins when it lies between the frame edge and one em past it.

## Rasterization

- Glyph outlines come from `SKFont.GetGlyphPath` at 512 px per em with hinting off (Photoshop never hints) and are cached per face. Each glyph maps through its own matrix (size, glyph scales, shear, rotation), the optional warp, then the layer transform.
- Pixel grid: on axis-aligned transforms each glyph's document origin rounds to a whole pixel, halves up (`floor(v + 0.5)`), which reproduces Photoshop's line-start and per-glyph rounding (`photoshop-text-anchor-*`). Rotated, sheared and warped text keeps exact positions.
- Anti-aliasing `None` draws hard edges; every other mode draws Skia's analytic coverage.
- Warp Text: `TextWarpMesh` ports `generate_style_warp_mesh` and `apply_warp_distortion` from `.reference/src/core/warp_mesh.cpp` (all fifteen styles, bend, both distortions, vertical orientation). It acts over the descriptor `Bounds` (the frame for box text). Warped outlines are flattened and every point is mapped through the Bezier patch.
- The raster covers the ink bounds plus one pixel, clipped to the document, and is unpremultiplied to straight alpha.

## Validation

`TextRenderSurvey` (opt-in: `XRAY_PSD_TEXT_SURVEY=1 dotnet test --project tests/XRay.Psd.Text.Tests -c Release`) re-renders every type layer of every committed fixture with the bundled Liberation Sans and writes `test-output/text-survey.txt` plus side-by-side PNGs (stored, re-rendered, alpha difference). `XRAY_PSD_TEXT_PROFILE=<fixture>` dumps per-column and per-row alpha sums. Metrics: IoU of the two ink masks (alpha at least 128), mean alpha error over the union of both ink boxes, total alpha ratio, and ink-box edge deltas (re-rendered minus stored).

October 2026 results with Liberation Sans for ArialMT:

| Fixture | IoU | Edge deltas (L T R B) | Notes |
|---|---|---|---|
| `photoshop-text-anchor-{whole,half,center-whole,center-third}` | 0.81 | +1 +2 0 0 | the half and third variants move exactly as Photoshop's |
| `photoshop-text-anchor-center90-*` | 0.76 | 0 +1 0 0 | 90% transform scale |
| `photoshop-text-box-auto-leading` | 0.82 | 0 +1 0 0 | wraps to the same two lines |
| `photoshop-text-point-{auto,fixed}-leading` | 0.82 | 0 +1 0 0 | |
| `photoshop-text-point-transformed` | 0.79 | +1 +1 0 0 | 2 x 1.5 transform |
| `photoshop-text-tracking` (two layers) | 0.86, 0.80 | 0 +1 0 0 | tracked width grows by 43 px as in Photoshop |
| `photoshop-text-hv-scale` | 0.62 | 0 +1 +1 0 | see residuals |
| `photoshop-warp-text` (Squeeze, Arc 50) | 0.73, 0.77 | within 1 | |
| `qual_rca_pinout` (six layers) | 0.59 to 0.80 | within 3 | 75 px, Strong anti-aliasing |
| `photoshop-text-rtl-hebrew` | 0.23 | -12 -3 0 -1 | right edge exact; Liberation's Hebrew is wider than Arial's |
| `photoshop-text-vertical-rotated-roman` | 0.28 | 0 +1 0 +5 | Myriad Pro missing, fallback is wider |
| `photoshop-text-vertical-{point,box}` | 0.39, 0.42 | within 3 | with a local CJK face instead of MS Gothic |
| `photoshop-cmyk-style-colors` | 0.35 | +1 0 +2 0 | Myriad Pro missing |

Placement (edge deltas) is within one pixel on 15 of the 21 Arial layers and within three on all. The IoU ceiling is set by the font: Liberation Sans shares Arial's advances but not its outlines (its capitals are 0.69 em tall against Arial's 0.716, and figures such as 1 differ), so even a perfect layout leaves IoU near 0.8 at these sizes.

`FixtureRenderTests` pins these with a small margin, plus relational checks that do not depend on glyph shapes: the half-pixel and third-pixel anchors move the raster exactly as Photoshop's does, tracking adds 9 x 4.8 px, fixed leading 40 moves the third line by 2 x 11.2 px, box text wraps to two lines with the first baseline at 0.728 em, and the right-to-left line ends at the anchor with its first letter rightmost. `LayoutTests` covers alignment, mixed-size leading, paragraph spacing, faux styles, wrapping, justification, underline, caps, superscript, anti-alias none, rotation, vertical cells, missing fonts, degenerate models and linear-time layout. `CompositorIntegrationTests` covers the hook: a type layer without pixels renders from its text, stored pixels win by default, `Always` re-renders and keeps layer effects, and every fixture (and truncated type fixtures) renders.

The tests use only the bundled `Fonts/LiberationSans-Regular.ttf` (SIL Open Font License, copied with its license from `.reference/third_party/fonts-web/liberation`). The vertical CJK test runs when a CJK font sits in `local-test-fixtures/fonts/` (gitignored) and skips otherwise.

## Residuals and gaps

- Glyph outlines differ wherever the substitute is not the real font; metric-compatible aliases keep the layout.
- Photoshop's anti-aliasing modes: Crisp and Strong fit stems to the pixel grid and Smooth draws heavier stems (the warp fixtures carry 16 to 23% more ink than the re-render). Not modeled.
- With a horizontal scale (`photoshop-text-hv-scale`, 80%) Photoshop's glyph x positions look fractional rather than rounded; the per-glyph rounding above is calibrated on unscaled Sharp text only.
- Bidi is the two-level simplification above (no explicit embeddings, numbers are left-to-right runs). Mixed-direction lines are untested against Photoshop.
- Not modeled: hyphenation (`AutoHyphenate`), Japanese line-breaking rules (kinsoku), tab stops (a tab advances by its glyph), the justification settings (word and letter spacing limits, glyph scaling), list bullets and numbering (`ListStyleIndex`), OpenType features other than kerning, ligatures and `vert`, optical kerning, and composite (Every-line) composer differences.
- Uncalibrated: where manual kerning sits (after every character of its run), Small Caps at 70%, super and subscript at Photoshop's default preferences (a document can change them), the glyph stroke (`StrokeFlag`, `OutlineWidth` in engine units, centered), the horizontal overset rule, and vertical box text beyond the one capture.
- Type on a path (`TextShapeKind.Other`) is not drawn: the renderer returns null and the stored pixels stay. The path geometry still needs decoding (TODO.md).
- Text colors in CMYK and gray documents use the model's naive conversion (see [color.md](color.md)).
