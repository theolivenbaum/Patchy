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
- `Transform` (`xx xy yx yy tx ty`, text space to document pixels), `Orientation`, `ShapeKind` (point, box, path, other) with the raw `ShapeType`, `IsParagraphText`, and `Path` for type on a path (see below).
- `Bounds` and `BoundingBox` from the descriptor, `BoxBounds` from `Rendered/Shapes/Children[0]/Cookie/Photoshop/BoxBounds`, all `TextBounds` in text space.
- `WarpSettings`: a `TextWarp` record (style, stored token, bend, horizontal and vertical distortion, axis) decoded from the `warp` descriptor. `IsIdentity` is true for `warpNone` or all-zero parameters. The raw descriptor stays in `Warp`.
- `AntiAlias`: descriptor `AntA` first (`Anno`, `AnCr`, `AnSt`, `AnSm`, `AnSh`), else EngineData `/AntiAlias` (0 none, 1 crisp, 2 strong, 3 smooth, 4 sharp, matching the fixtures).
- `TextIndex` and `TextEngineObject` (the layer's `Txt2` object), `Origin` (flags: `TypeTool`, `EngineData`, `TextEngineBlock`, `LegacyTypeTool`).
- `StyleRuns`, `Paragraphs`, `Fonts` (PostScript names used by the runs, first-use order).

`TextStyleRun` keeps its original positional members (font, size, fill color, faux bold and italic, tracking, fixed leading) and adds init-only properties: `AutoLeading`, `HorizontalScale`, `VerticalScale`, `BaselineShift`, `AutoKerning`, `Kerning`, `Caps`, `BaselinePosition`, `Underline`, `Strikethrough`, `Ligatures`, `DiscretionaryLigatures`, `BaselineDirection`, `Language`, `NoBreak`, `StrokeColor`, `FillEnabled`, `StrokeEnabled`, `StrokeWidth`, and for PS 5 faces `FontFamily` and `FontStyleName`. Sizes, leading and baseline shift are engine units (document pixels before the transform); tracking and kerning are thousandths of an em. `Leading` is null for auto leading, because Photoshop stores a stale `/Leading` even then.

`TextParagraph` adds `FirstLineIndent`, `StartIndent`, `EndIndent`, `SpaceBefore`, `SpaceAfter` (engine units), `AutoLeadingFraction` (default 1.2), `AutoHyphenate`, `Direction`, and the `Txt2`-only list settings `ListStyleIndex` and `ListTier`.

Sheets are sparse. A run's lookup falls back to the document's normal sheet: `ResourceDict/StyleSheetSet[TheNormalStyleSheet]/StyleSheetData` and `ParagraphSheetSet[TheNormalParagraphSheet]/Properties` in EngineData; the first entry of the style sheet set (`/0/5/0`) or paragraph sheet set (`/0/6/0`), then the document defaults (`/1/2`, `/1/3`) in `Txt2`.

Colors: `/Type 1` is `[alpha r g b]`, `/Type 2` (CMYK documents) is `[alpha c m y k]` ink fractions, `/Type 0` is `[alpha level]`. All three convert through the document's ICC profile like its pixels (`DocumentColors`, see [color.md](color.md)); without one, CMYK uses the plain inverse-ink formula and gray copies neutrally. `Txt2` wraps the same values in `<< /99 /SimplePaint /0 << /0 type /1 [...] >> >>`.

## Txt2 (`PsdDocument.TextEngine`)

Parsed on first use (and at load when the document has type layers). `TextEngineBlock` exposes `FormatVersion` (`/98/0`: 14 for Photoshop 2026, 13 in `qual_rca_pinout.psd`), `Fonts` (`/0/1/0`), `Frames` (`/0/8/0[i]/0`: settings `/2/0` 0 point, 1 box, 2 path; `/2/1` 2 for vertical; Bezier control points in `/1/0`, four per cubic segment, so a box stores each side as a straight segment), `Objects` (`/1/1`) and the raw `Root`. Each `TextEngineObject` has its text (`/0/0`), style runs (`/0/6/0`), paragraph runs (`/0/5/0`), frame (`/1/0/0/0`) and `RotatedRoman`: model key `/0/10/0` = 4 on a vertical frame. That key is also 4 on the horizontal anchor captures and on ag-psd's `text-path.psd`, so it only counts for vertical frames, and the runs take their `BaselineDirection` from style key 35.

Style keys: 0 font, 1 size, 2 and 3 faux bold and italic, 4 auto leading, 5 leading, 6 and 7 scales, 8 tracking, 9 baseline shift, 11 kerning method (0 manual, 1 metrics, 2 optical; nonzero reads as `AutoKerning`), 12 caps, 13 baseline position (superscript, subscript), 15 strikethrough and 16 underline (position numbers, nonzero is on), 18 ligatures, 19 discretionary ligatures, 35 baseline direction, 38 language, 52 no break, 53 and 54 fill and stroke paint. Paragraph keys: 0 justification, 1 to 5 indents and spacing, 7 auto leading fraction, 9 hyphenation, 33 direction, 36 list style, 37 list tier.

Evidence. The pinned keys come from the single-setting captures in `.reference/docs/txt2.md`. The others follow the Adobe text engine's style sheet order (Font, FontSize, FauxBold, FauxItalic, AutoLeading, Leading, HorizontalScale, VerticalScale, Tracking, BaselineShift, CharacterRotation, AutoKern, FontCaps, FontBaseline, FontOTPosition, StrikethroughPosition, UnderlinePosition, UnderlineOffset, Ligatures, DiscretionaryLigatures, ... BaselineDirection at 35, Tsume, StyleRunAlignment, Language at 38, ... NoBreak at 52, FillColor, StrokeColor), whose types and defaults match all 89 values of Photoshop's default sheet (`/1/2`) and agree with ag-psd's `engineData2.ts`. Comparing the TySh and `Txt2` runs of the same text in the corpus confirms caps (Small Caps 1 on `masks.psd` "sign in", pinned in `PsdToolsCorpusTests`) and baseline direction (1 on the upright vertical captures, 2 on the rotated-Roman one); baseline position, strikethrough, underline, discretionary ligatures and no break only show matching zero and false values, since no corpus file sets them. `TextEngineTests` pins the agreement on every committed type fixture.

Manual kerning has no `Txt2` style key: every candidate in the default sheet is typed otherwise (key 10, the character rotation, is a float), and no corpus file sets kerning, so `Txt2`-only runs keep 0. One more mismatch stays open: in psd-tools' `layer_effects.psd` the TySh runs say `AutoKerning true` while the `Txt2` runs store key 11 as 0 (manual), and in `text.psb` the reverse; TySh wins whenever both exist.

Unit flags: a style run's `/0/0/5` and a paragraph run's `/0/0/6` set the meaning of its sheet lengths. 0 is document pixels; 1 is points at the document resolution, converted with resource 1005 (`ppi / 72`). Fallback sheet values are scaled with the run's flag.

## Gap filling (`TextEngineResolver`)

At the end of `PsdParser.Parse`, every type layer with a `TextIndex` that addresses a `Txt2` object is linked to it, and:

- an empty layer text takes the object's text;
- when the texts agree, missing style or paragraph runs come from the object, and paragraph direction and list settings (which TySh does not store) are merged by paragraph start;
- a `TySh` whose descriptor does not decode is salvaged: the transform comes from the header and the `TextIndex` from the raw `TextIndexlong` bytes, and the layer's info is built from the object alone (`Origin == TextEngineBlock`);
- an object whose frame is a path turns the layer into type on a path (below).

## Type on a path

Photoshop's TySh record does not mark type on a path: ag-psd's `text-path.psd` (Photoshop, committed under `tests/fixtures/ag-psd/`) stores the layer as box text, `ShapeType 1`, with the path's extent as `BoxBounds` and an empty line list. Only the layer's `Txt2` frame says it follows a path (frame type 2). The resolver then sets `ShapeKind = Path`, `IsParagraphText = false` and `Path`, keeping the raw `ShapeType`.

`TextPath` (also on `TextEngineFrame.Path`) holds:

- `Segments`: cubic Bezier segments in text space (`TextPathSegment` with `Start`, `Control1`, `Control2`, `End`), read eight numbers at a time from the frame's `/1/0`. The fixture's path is two segments sharing their middle point.
- `RangeStart` and `RangeEnd` (frame `/2/6`): Photoshop's start and end markers as path positions, the segment index plus the curve parameter inside it. In the fixture the end marker is at 0.33 of the first segment (x 30 in text space); Photoshop draws only "so" of "some text" there, and its cached layout holds two glyphs. Read as a fraction of the arc length instead, the range would be long enough for "som", so the parameter reading is the one that matches.
- `Flipped` (`/2/11/0`, per ag-psd's key names; the fixture does not set it), `IsClosed`, `Bounds` (control polygon extent) and `PointAt(position)`.

Text extraction does not change for path text. Vertical type on a path is not modeled.

The layer's own values win wherever both records have them. Photoshop itself trusts `Txt2` over `TySh`, so a mismatch means the file was edited by another tool; both versions are kept (see below).

## Extraction

`PsdDocument.ExtractText` reports type layers as `PsdTextKind.TextLayer`. With `IncludeTextEngineObjects` (default true), `Txt2` objects whose text no extracted layer reports become `PsdTextKind.TextEngineObject` items: objects no layer references (source `Txt2/<index>`), and objects whose text differs from their layer's own record (source is the layer path, `Layer` is set). Objects owned by hidden layers follow `IncludeHiddenLayers`. Embedded PSD/PSB smart objects go through the same path.

## Photoshop 5 `tySh`

Layout per `.reference/docs/psd-legacy-text.md`. Faces carry a PostScript name, family and style; 26-byte style records reference faces by mark; characters reference styles by mark. Style sizes, leading and base shift are 16.16 fixed engine units; tracking and kerning are em fractions, reported as thousandths. Leading 0 is auto. Runs coalesce consecutive units with the same style mark; each line becomes a paragraph (alignment 0 left, 1 center, -1 right). A non-final line without its `\r` gets one; the last line's `\r` is dropped. The record's single color (RGB, HSB, inverted CMYK or gray on 0 to 10000) fills every run; CMYK and RGB convert through the document's profile. Anti-alias byte 0 is none, anything else Sharp. A line orientation other than 0 marks the layer vertical.

The style section is resolved by validation: count-first with every style naming a known face and a consistent text section, then the specification's version-word form, then count-first with unknown face marks falling back to the first face. Sizes must be in (0, 8192], tracking within 10 em, leading in [0, 32768]; line unit sums must stay within the character count, and the color plus anti-alias byte must fit. Anything else returns null and the layer stays a text layer without `Text`.

There is still no real Photoshop 5 file: none of the 356 psd-tools files, the committed fixtures or the reference tree holds a `tySh` block (embedded smart objects included), so the tests build synthetic records.

## Rendering

The core never draws text itself: type layers render from their stored pixels. The optional `XRay.Psd.Text` package lays out and rasterizes this model (runs, paragraphs, transform, warp) and plugs into the compositor; see [text-rendering.md](text-rendering.md).

## Gaps

- Type on a path: one fixture (open path, no flip). Closed paths, flipped text, the other path data keys (`/2/11/3`, `/5`, `/7`) and vertical path text are unconfirmed.
- `Txt2` keys beyond the map above (manual kerning has none), the cached layout tree (`/1/1[i]/1/2`), and the list style set (`/0/9`) are reachable only through `Root`/`Node`.
- PS 5 vertical type and resolution scaling are untested against real files (the reference has no sample either).
