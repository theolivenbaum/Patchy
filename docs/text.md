# Text model

How type layers and document text are read. Code lives in `src/XRay.Psd/Text/`. Read this before changing text parsing or extraction.

## Records

A Photoshop document stores type in up to three places:

| Record | Where | Reader |
|---|---|---|
| `TySh` (Photoshop 6+) | Layer tagged block: version, transform, text descriptor (`Txt `, `Ornt`, `AntA`, `bounds`, `boundingBox`, `TextIndex`, `EngineData`), warp descriptor | `TextLayerInfo.Parse` |
| `Txt2` | Global tagged block: one text object per type layer, addressed by the layer's `TextIndex`, plus shared fonts, sheets and frames | `TextEngineBlock.Parse` |
| `tySh` (Photoshop 5.x) | Layer tagged block with a fixed binary layout and no EngineData | `LegacyText.Parse` |

`EngineDataParser` reads the PostScript-like syntax used by both `EngineData` (named keys such as `/FontSize`) and `Txt2` (integer keys such as `/1`, a bare root without `<< >>`).

## TextLayerInfo

`PsdLayer.Text` holds a `TextLayerInfo`:

- `Text` (separators normalized to `\n`, trailing break trimmed) and `RawText` (as stored, `\r` separators). Run and paragraph `Start`/`Length` index `RawText` in UTF-16 units and count the separators, as Photoshop does. The text comes from the descriptor `Txt `, then `EngineDict/Editor/Text`, then the `Txt2` object.
- `Transform` (`xx xy yx yy tx ty`, text space to document pixels), `Orientation`, `ShapeKind` (point, box, other) with the raw `ShapeType`, `IsParagraphText`.
- `Bounds` and `BoundingBox` from the descriptor, `BoxBounds` from `Rendered/Shapes/Children[0]/Cookie/Photoshop/BoxBounds`, all `TextBounds` in text space.
- `WarpSettings`: a `TextWarp` record (style, stored token, bend, horizontal and vertical distortion, axis) decoded from the `warp` descriptor. `IsIdentity` is true for `warpNone` or all-zero parameters. The raw descriptor stays in `Warp`.
- `AntiAlias`: descriptor `AntA` first (`Anno`, `AnCr`, `AnSt`, `AnSm`, `AnSh`), else EngineData `/AntiAlias` (0 none, 1 crisp, 2 strong, 3 smooth, 4 sharp, matching the fixtures).
- `TextIndex` and `TextEngineObject` (the layer's `Txt2` object), `Origin` (flags: `TypeTool`, `EngineData`, `TextEngineBlock`, `LegacyTypeTool`).
- `StyleRuns`, `Paragraphs`, `Fonts` (PostScript names used by the runs, first-use order).

`TextStyleRun` keeps its original positional members (font, size, fill color, faux bold and italic, tracking, fixed leading) and adds init-only properties: `AutoLeading`, `HorizontalScale`, `VerticalScale`, `BaselineShift`, `AutoKerning`, `Kerning`, `Caps`, `BaselinePosition`, `Underline`, `Strikethrough`, `Ligatures`, `DiscretionaryLigatures`, `BaselineDirection`, `Language`, `NoBreak`, `StrokeColor`, `FillEnabled`, `StrokeEnabled`, `StrokeWidth`, and for PS 5 faces `FontFamily` and `FontStyleName`. Sizes, leading and baseline shift are engine units (document pixels before the transform); tracking and kerning are thousandths of an em. `Leading` is null for auto leading, because Photoshop stores a stale `/Leading` even then.

`TextParagraph` adds `FirstLineIndent`, `StartIndent`, `EndIndent`, `SpaceBefore`, `SpaceAfter` (engine units), `AutoLeadingFraction` (default 1.2), `AutoHyphenate`, `Direction`, and the `Txt2`-only list settings `ListStyleIndex` and `ListTier`.

Sheets are sparse. A run's lookup falls back to the document's normal sheet: `ResourceDict/StyleSheetSet[TheNormalStyleSheet]/StyleSheetData` and `ParagraphSheetSet[TheNormalParagraphSheet]/Properties` in EngineData; the first entry of the style sheet set (`/0/5/0`) or paragraph sheet set (`/0/6/0`), then the document defaults (`/1/2`, `/1/3`) in `Txt2`.

Colors: `/Type 1` is `[alpha r g b]`, `/Type 2` (CMYK documents) is `[alpha c m y k]` ink fractions converted with the plain inverse-ink formula, `/Type 0` is `[alpha level]`. `Txt2` wraps the same values in `<< /99 /SimplePaint /0 << /0 type /1 [...] >> >>`.

## Txt2 (`PsdDocument.TextEngine`)

Parsed on first use (and at load when the document has type layers). `TextEngineBlock` exposes `FormatVersion` (`/98/0`: 14 for Photoshop 2026, 13 in `qual_rca_pinout.psd`), `Fonts` (`/0/1/0`), `Frames` (`/0/8/0`: settings `/2/0` 1 for a box, `/2/1` 2 for vertical, path points in `/1/0`), `Objects` (`/1/1`) and the raw `Root`. Each `TextEngineObject` has its text (`/0/0`), style runs (`/0/6/0`), paragraph runs (`/0/5/0`), frame (`/1/0/0/0`) and `RotatedRoman` (`/0/10/0` = 4, which also sets the runs' `BaselineDirection` to 2).

Only the numeric keys pinned in `.reference/docs/txt2.md` are mapped. Style: 0 font, 1 size, 2 and 3 faux bold and italic, 4 auto leading, 5 leading, 6 and 7 scales, 8 tracking, 9 baseline shift, 11 auto kerning, 18 ligatures, 38 language, 53 and 54 fill and stroke paint. Paragraph: 0 justification, 1 to 5 indents and spacing, 7 auto leading fraction, 9 hyphenation, 33 direction, 36 list style, 37 list tier. Underline, strikethrough, caps and manual kerning have no confirmed `Txt2` key and keep their defaults on `Txt2`-only runs.

Unit flags: a style run's `/0/0/5` and a paragraph run's `/0/0/6` set the meaning of its sheet lengths. 0 is document pixels; 1 is points at the document resolution, converted with resource 1005 (`ppi / 72`). Fallback sheet values are scaled with the run's flag.

## Gap filling (`TextEngineResolver`)

At the end of `PsdParser.Parse`, every type layer with a `TextIndex` that addresses a `Txt2` object is linked to it, and:

- an empty layer text takes the object's text;
- when the texts agree, missing style or paragraph runs come from the object, and paragraph direction and list settings (which TySh does not store) are merged by paragraph start;
- a `TySh` whose descriptor does not decode is salvaged: the transform comes from the header and the `TextIndex` from the raw `TextIndexlong` bytes, and the layer's info is built from the object alone (`Origin == TextEngineBlock`).

The layer's own values win wherever both records have them. Photoshop itself trusts `Txt2` over `TySh`, so a mismatch means the file was edited by another tool; both versions are kept (see below).

## Extraction

`PsdDocument.ExtractText` reports type layers as `PsdTextKind.TextLayer`. With `IncludeTextEngineObjects` (default true), `Txt2` objects whose text no extracted layer reports become `PsdTextKind.TextEngineObject` items: objects no layer references (source `Txt2/<index>`), and objects whose text differs from their layer's own record (source is the layer path, `Layer` is set). Objects owned by hidden layers follow `IncludeHiddenLayers`. Embedded PSD/PSB smart objects go through the same path.

## Photoshop 5 `tySh`

Layout per `.reference/docs/psd-legacy-text.md`. Faces carry a PostScript name, family and style; 26-byte style records reference faces by mark; characters reference styles by mark. Style sizes, leading and base shift are 16.16 fixed engine units; tracking and kerning are em fractions, reported as thousandths. Leading 0 is auto. Runs coalesce consecutive units with the same style mark; each line becomes a paragraph (alignment 0 left, 1 center, -1 right). A non-final line without its `\r` gets one; the last line's `\r` is dropped. The record's single color (RGB, HSB, inverted CMYK or gray on 0 to 10000) fills every run. Anti-alias byte 0 is none, anything else Sharp. A line orientation other than 0 marks the layer vertical.

The style section is resolved by validation: count-first with every style naming a known face and a consistent text section, then the specification's version-word form, then count-first with unknown face marks falling back to the first face. Sizes must be in (0, 8192], tracking within 10 em, leading in [0, 32768]; line unit sums must stay within the character count, and the color plus anti-alias byte must fit. Anything else returns null and the layer stays a text layer without `Text`.

## Rendering

The core never draws text itself: type layers render from their stored pixels. The optional `XRay.Psd.Text` package lays out and rasterizes this model (runs, paragraphs, transform, warp) and plugs into the compositor; see [text-rendering.md](text-rendering.md).

## Gaps

- Type on a path: `ShapeType` values other than 0 and 1 are reported as `TextShapeKind.Other`, and `Txt2` frame paths are exposed as raw points, but the path geometry is not decoded into a typed path (no fixture yet).
- `Txt2` keys beyond the pinned map, the cached layout tree (`/1/1[i]/1/2`), and the list style set (`/0/9`) are reachable only through `Root`/`Node`.
- Color conversion of engine CMYK and gray is uncalibrated (no ICC).
- PS 5 vertical type and resolution scaling are untested against real files (the reference has no sample either).
