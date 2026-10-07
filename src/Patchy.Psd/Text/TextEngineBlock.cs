namespace Patchy.Psd.Text;

/// <summary>A font entry of the Txt2 font set (<c>/0/1/0[i]</c>, class <c>CoolTypeFont</c>).</summary>
/// <param name="PostScriptName">PostScript name (<c>/0</c>).</param>
/// <param name="FontType">Font type (<c>/2</c>): 0 for Photoshop's defaults (Myriad, AdobeInvisFont), 1 for ordinary fonts.</param>
/// <param name="Version">The font version string (<c>/5</c>), when stored.</param>
public sealed record TextEngineFont(string PostScriptName, int FontType, string? Version);

/// <summary>A text frame of the Txt2 frame set (<c>/0/8/0[i]</c>): the shape a text object is laid out in.</summary>
/// <param name="Index">Position in the frame set.</param>
/// <param name="ShapeKind">Point text, a box, or another frame type (settings key <c>/0</c>: absent or 0 point, 1 box).</param>
/// <param name="Orientation">Settings key <c>/1</c>: 2 is vertical.</param>
/// <param name="Points">The frame path as x, y pairs in text space (a box stores each corner four times).</param>
/// <param name="Bounds">The extent of <paramref name="Points"/>, when the frame has a path.</param>
public sealed record TextEngineFrame(
    int Index,
    TextShapeKind ShapeKind,
    TextOrientation Orientation,
    IReadOnlyList<double> Points,
    TextBounds? Bounds);

/// <summary>
/// One text object of the Txt2 block (<c>/1/1[i]</c>), addressed by a type
/// layer's <c>TextIndex</c>. Lengths are converted to document pixels (the
/// TySh engine units) using the unit flag each run stores.
/// </summary>
public sealed class TextEngineObject
{
    /// <summary>Position in the object list; the <c>TextIndex</c> a type layer stores.</summary>
    public int Index { get; init; }

    /// <summary>Text with paragraph breaks normalized to <c>\n</c>.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>The engine text as stored (<c>\r</c> separators and a trailing <c>\r</c>).</summary>
    public string RawText { get; init; } = string.Empty;

    public IReadOnlyList<TextStyleRun> StyleRuns { get; init; } = [];

    public IReadOnlyList<TextParagraph> Paragraphs { get; init; } = [];

    /// <summary>Distinct font PostScript names referenced by the style runs, in first-use order.</summary>
    public IReadOnlyList<string> Fonts { get; init; } = [];

    /// <summary>Index into <see cref="TextEngineBlock.Frames"/> the object's view references.</summary>
    public int? FrameIndex { get; init; }

    /// <summary>The frame the object is laid out in, when it resolves.</summary>
    public TextEngineFrame? Frame { get; init; }

    /// <summary>Vertical Roman glyphs lie rotated ("Standard Vertical Roman Alignment", model key <c>/10/0</c> = 4).</summary>
    public bool RotatedRoman { get; init; }

    /// <summary>The object's raw tree.</summary>
    public EngineValue? Node { get; init; }
}

/// <summary>
/// The document-level text engine block (<c>Txt2</c>): one text object per type
/// layer plus the shared fonts, style sheets and frames. Photoshop trusts it over
/// the per-layer TySh EngineData. Format: <c>.reference/docs/txt2.md</c>.
/// </summary>
public sealed class TextEngineBlock
{
    private TextEngineBlock(EngineValue root)
    {
        Root = root;
    }

    /// <summary>The serialization version (<c>/98/0</c>; Photoshop 2026 writes 14), when stored.</summary>
    public int? FormatVersion { get; private init; }

    /// <summary>The font set (<c>/0/1/0</c>); style sheets reference fonts by index.</summary>
    public IReadOnlyList<TextEngineFont> Fonts { get; private init; } = [];

    /// <summary>The text frame set (<c>/0/8/0</c>).</summary>
    public IReadOnlyList<TextEngineFrame> Frames { get; private init; } = [];

    /// <summary>The text objects (<c>/1/1</c>), indexed by <c>TextIndex</c>.</summary>
    public IReadOnlyList<TextEngineObject> Objects { get; private init; } = [];

    /// <summary>The whole parsed block.</summary>
    public EngineValue Root { get; }

    /// <summary>The object a type layer's <c>TextIndex</c> addresses, or null.</summary>
    public TextEngineObject? GetObject(int textIndex) => textIndex >= 0 && textIndex < Objects.Count ? Objects[textIndex] : null;

    /// <summary>
    /// Parses a Txt2 payload. <paramref name="pixelsPerPoint"/> converts sheet
    /// values stored in points (unit flag 1) to document pixels: the document
    /// resolution divided by 72. Returns null when the block does not decode.
    /// </summary>
    public static TextEngineBlock? Parse(ReadOnlySpan<byte> payload, double pixelsPerPoint = 1)
    {
        if (!double.IsFinite(pixelsPerPoint) || pixelsPerPoint <= 0)
        {
            pixelsPerPoint = 1;
        }

        var root = EngineDataParser.Parse(payload);
        if (root is not { Kind: EngineValueKind.Dictionary } || root.Get("1", "1") is not { Kind: EngineValueKind.List } objectList)
        {
            return null;
        }

        var fonts = new List<TextEngineFont>();
        foreach (var entry in root.Get("0", "1", "0")?.Items ?? [])
        {
            var font = entry.Get("0", "0");
            fonts.Add(new TextEngineFont(
                font?["0"] is { Kind: EngineValueKind.String } name ? name.Text : string.Empty,
                EngineStyles.Integer(font?["2"]) ?? 1,
                font?["5"] is { Kind: EngineValueKind.String } version ? version.Text : null));
        }

        var frames = new List<TextEngineFrame>();
        var frameList = root.Get("0", "8", "0")?.Items ?? [];
        for (var i = 0; i < frameList.Count; i++)
        {
            frames.Add(ReadFrame(i, frameList[i]["0"]));
        }

        var fontNames = fonts.Select(f => f.PostScriptName).ToList();

        // Sparse run sheets fall back to the normal style sheet, then to the document defaults.
        var normalStyle = root.Get("0", "5", "0", 0, "0", "6");
        var defaultStyle = root.Get("1", "2");
        var normalParagraph = root.Get("0", "6", "0", 0, "0", "5");
        var defaultParagraph = root.Get("1", "3");
        var objects = new List<TextEngineObject>(objectList.Items.Count);
        for (var i = 0; i < objectList.Items.Count; i++)
        {
            objects.Add(ReadObject(i, objectList.Items[i], fontNames, frames, normalStyle, defaultStyle, normalParagraph, defaultParagraph, pixelsPerPoint));
        }

        return new TextEngineBlock(root)
        {
            FormatVersion = EngineStyles.Integer(root.Get("98", "0")),
            Fonts = fonts,
            Frames = frames,
            Objects = objects,
        };
    }

    private static TextEngineFrame ReadFrame(int index, EngineValue? frame)
    {
        var settings = frame?["2"];
        var kind = EngineStyles.Integer(settings?["0"]) switch
        {
            null or 0 => TextShapeKind.Point,
            1 => TextShapeKind.Box,
            _ => TextShapeKind.Other,
        };
        var orientation = EngineStyles.Integer(settings?["1"]) == 2 ? TextOrientation.Vertical : TextOrientation.Horizontal;
        var points = new List<double>();
        foreach (var item in frame?.Get("1", "0")?.Items ?? [])
        {
            if (EngineStyles.Number(item) is double value)
            {
                points.Add(value);
            }
        }

        TextBounds? bounds = null;
        if (points.Count >= 2)
        {
            double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
            for (var p = 0; p + 1 < points.Count; p += 2)
            {
                left = Math.Min(left, points[p]);
                right = Math.Max(right, points[p]);
                top = Math.Min(top, points[p + 1]);
                bottom = Math.Max(bottom, points[p + 1]);
            }

            bounds = new TextBounds(left, top, right, bottom);
        }

        return new TextEngineFrame(index, kind, orientation, points, bounds);
    }

    private static TextEngineObject ReadObject(
        int index,
        EngineValue node,
        IReadOnlyList<string> fontNames,
        IReadOnlyList<TextEngineFrame> frames,
        EngineValue? normalStyle,
        EngineValue? defaultStyle,
        EngineValue? normalParagraph,
        EngineValue? defaultParagraph,
        double pixelsPerPoint)
    {
        var model = node["0"];
        var raw = model?["0"] is { Kind: EngineValueKind.String } text ? text.Text : string.Empty;

        // Paragraph runs: /0/5/0[j] = << /0 << /0 << /5 sheet /6 unit >> >> /1 length >>.
        var paragraphs = new List<TextParagraph>();
        var start = 0;
        foreach (var run in model?.Get("5", "0")?.Items ?? [])
        {
            if (start >= raw.Length)
            {
                break;
            }

            var length = Math.Min(EngineStyles.Length(run["1"]), raw.Length - start);
            if (length <= 0)
            {
                continue;
            }

            var holder = run.Get("0", "0");
            var sheet = holder?["5"];
            var scale = EngineStyles.Integer(holder?["6"]) == 1 ? pixelsPerPoint : 1;
            paragraphs.Add(EngineStyles.Paragraph(
                start,
                length,
                EngineStyles.Segment(raw, start, length),
                key => sheet?[key] ?? normalParagraph?[key] ?? defaultParagraph?[key],
                ParagraphKeys.TextEngine,
                scale));
            start += length;
        }

        // Style runs: /0/6/0[j] = << /0 << /0 << /6 sheet /5 unit >> >> /1 length >>.
        var runs = new List<TextStyleRun>();
        start = 0;
        foreach (var run in model?.Get("6", "0")?.Items ?? [])
        {
            if (start >= raw.Length)
            {
                break;
            }

            var length = Math.Min(EngineStyles.Length(run["1"]), raw.Length - start);
            if (length <= 0)
            {
                continue;
            }

            var holder = run.Get("0", "0");
            var sheet = holder?["6"];
            var scale = EngineStyles.Integer(holder?["5"]) == 1 ? pixelsPerPoint : 1;
            runs.Add(EngineStyles.Run(
                start,
                length,
                EngineStyles.Segment(raw, start, length),
                key => sheet?[key] ?? normalStyle?[key] ?? defaultStyle?[key],
                StyleKeys.TextEngine,
                fontNames,
                scale));
            start += length;
        }

        // Standard Vertical Roman Alignment is a model setting here and a run setting in TySh.
        var rotatedRoman = EngineStyles.Integer(model?.Get("10", "0")) == 4;
        if (rotatedRoman)
        {
            for (var i = 0; i < runs.Count; i++)
            {
                runs[i] = runs[i] with { BaselineDirection = 2 };
            }
        }

        var frameIndex = EngineStyles.Integer(node.Get("1", "0", 0, "0"));
        return new TextEngineObject
        {
            Index = index,
            Text = TextLayerInfo.NormalizeText(raw),
            RawText = raw,
            StyleRuns = runs,
            Paragraphs = paragraphs,
            Fonts = TextLayerInfo.DistinctFonts(runs),
            FrameIndex = frameIndex,
            Frame = frameIndex is int f && f >= 0 && f < frames.Count ? frames[f] : null,
            RotatedRoman = rotatedRoman,
            Node = node,
        };
    }
}
