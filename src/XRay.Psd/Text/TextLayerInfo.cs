using XRay.Psd.Descriptors;
using XRay.Psd.IO;

namespace XRay.Psd.Text;

/// <summary>Text direction of a type layer.</summary>
public enum TextOrientation
{
    Horizontal,
    Vertical,
}

/// <summary>Paragraph alignment from the text engine (<c>Justification</c>).</summary>
public enum TextJustification
{
    Left = 0,
    Right = 1,
    Center = 2,
    JustifyLastLeft = 3,
    JustifyLastRight = 4,
    JustifyLastCenter = 5,
    JustifyAll = 6,
}

/// <summary>A run of characters sharing one character style.</summary>
/// <param name="Start">First UTF-16 unit of the run in <see cref="TextLayerInfo.RawText"/>.</param>
/// <param name="Length">Run length in UTF-16 units (Photoshop counts the paragraph separators).</param>
/// <param name="Text">The run's text, separators normalized to <c>\n</c>.</param>
/// <param name="FontName">PostScript name of the font.</param>
/// <param name="FontSize">Size in engine units (document pixels before the layer transform).</param>
/// <param name="FillColor">Text color.</param>
/// <param name="FauxBold">Synthetic emboldening of the named face.</param>
/// <param name="FauxItalic">Synthetic slant of the named face.</param>
/// <param name="Tracking">Tracking in thousandths of an em.</param>
/// <param name="Leading">Fixed leading in engine units, or null for auto leading.</param>
public sealed record TextStyleRun(
    int Start,
    int Length,
    string Text,
    string? FontName,
    double? FontSize,
    PsdColor? FillColor,
    bool FauxBold,
    bool FauxItalic,
    double? Tracking,
    double? Leading)
{
    /// <summary>Photoshop auto leading: the paragraph's <see cref="TextParagraph.AutoLeadingFraction"/> times the size.</summary>
    public bool AutoLeading { get; init; } = true;

    /// <summary>Glyph width scale (1 = 100 percent).</summary>
    public double HorizontalScale { get; init; } = 1;

    /// <summary>Glyph height scale (1 = 100 percent).</summary>
    public double VerticalScale { get; init; } = 1;

    /// <summary>Baseline shift in engine units (positive moves the glyphs up).</summary>
    public double BaselineShift { get; init; }

    /// <summary>Metrics (automatic) kerning.</summary>
    public bool AutoKerning { get; init; } = true;

    /// <summary>Manual kerning in thousandths of an em.</summary>
    public double Kerning { get; init; }

    public TextCaps Caps { get; init; }

    public TextBaselinePosition BaselinePosition { get; init; }

    public bool Underline { get; init; }

    public bool Strikethrough { get; init; }

    /// <summary>Standard ligatures.</summary>
    public bool Ligatures { get; init; } = true;

    /// <summary>Discretionary ligatures.</summary>
    public bool DiscretionaryLigatures { get; init; }

    /// <summary>Vertical type only: 1 Roman glyphs upright, 2 rotated ("Standard Vertical Roman Alignment"), 0 unspecified.</summary>
    public int BaselineDirection { get; init; }

    /// <summary>Photoshop's language index, when stored.</summary>
    public int? Language { get; init; }

    /// <summary>"No Break": the run must not be split across lines.</summary>
    public bool NoBreak { get; init; }

    /// <summary>Outline (stroke) color of the glyphs.</summary>
    public PsdColor? StrokeColor { get; init; }

    /// <summary>Whether the glyphs are filled (<c>/FillFlag</c>).</summary>
    public bool FillEnabled { get; init; } = true;

    /// <summary>Whether the glyphs are stroked (<c>/StrokeFlag</c>).</summary>
    public bool StrokeEnabled { get; init; }

    /// <summary>Glyph outline width (<c>/OutlineWidth</c>), when stored.</summary>
    public double? StrokeWidth { get; init; }

    /// <summary>Display family name, when the record stores one (Photoshop 5 <c>tySh</c> faces).</summary>
    public string? FontFamily { get; init; }

    /// <summary>Display style name (<c>Bold</c>, <c>Italic</c>...), when the record stores one (Photoshop 5 <c>tySh</c> faces).</summary>
    public string? FontStyleName { get; init; }
}

/// <summary>A paragraph with its alignment and metrics.</summary>
/// <param name="Start">First UTF-16 unit of the paragraph in <see cref="TextLayerInfo.RawText"/>.</param>
/// <param name="Length">Length in UTF-16 units, separator included.</param>
/// <param name="Text">The paragraph text without its separator.</param>
/// <param name="Justification">Alignment.</param>
public sealed record TextParagraph(int Start, int Length, string Text, TextJustification Justification)
{
    /// <summary>First-line indent in engine units; negative with a positive <see cref="StartIndent"/> is a hanging indent.</summary>
    public double FirstLineIndent { get; init; }

    /// <summary>Indent at the line start (left for horizontal left-to-right text), engine units.</summary>
    public double StartIndent { get; init; }

    /// <summary>Indent at the line end, engine units.</summary>
    public double EndIndent { get; init; }

    /// <summary>Space before the paragraph, engine units.</summary>
    public double SpaceBefore { get; init; }

    /// <summary>Space after the paragraph, engine units.</summary>
    public double SpaceAfter { get; init; }

    /// <summary>Auto leading as a fraction of the font size (Photoshop default 1.2).</summary>
    public double AutoLeadingFraction { get; init; } = 1.2;

    public bool AutoHyphenate { get; init; }

    /// <summary>Paragraph base direction. Photoshop stores it only in the document's Txt2 block.</summary>
    public TextDirection Direction { get; init; }

    /// <summary>Bulleted or numbered list style (an index into the Txt2 list style set), or null for none. Txt2 only.</summary>
    public int? ListStyleIndex { get; init; }

    /// <summary>List nesting level (0 is the outermost). Txt2 only.</summary>
    public int ListTier { get; init; }
}

/// <summary>
/// The type-tool data of a text layer (<c>TySh</c>, or a Photoshop 5 <c>tySh</c>):
/// the plain text, its transform and geometry, and the character/paragraph
/// styling decoded from EngineData. Gaps in the layer's own record are filled
/// from the document's <c>Txt2</c> text engine block when it has the layer's object.
/// </summary>
public sealed class TextLayerInfo
{
    /// <summary>Text with paragraph breaks normalized to <c>\n</c>.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>The text exactly as stored (Photoshop uses <c>\r</c> between paragraphs).</summary>
    public string RawText { get; init; } = string.Empty;

    /// <summary>Affine transform <c>xx, xy, yx, yy, tx, ty</c> from text space to document pixels.</summary>
    public IReadOnlyList<double> Transform { get; init; } = [1, 0, 0, 1, 0, 0];

    public TextOrientation Orientation { get; init; }

    /// <summary>True for paragraph (box) text, false for point text.</summary>
    public bool IsParagraphText { get; init; }

    /// <summary>Point, box or other (such as type on a path).</summary>
    public TextShapeKind ShapeKind { get; init; }

    /// <summary>The raw EngineData <c>ShapeType</c>, when stored.</summary>
    public int? ShapeType { get; init; }

    public IReadOnlyList<TextStyleRun> StyleRuns { get; init; } = [];

    public IReadOnlyList<TextParagraph> Paragraphs { get; init; } = [];

    /// <summary>Distinct font PostScript names referenced by the style runs, in first-use order.</summary>
    public IReadOnlyList<string> Fonts { get; init; } = [];

    /// <summary>The <c>TxLr</c> text descriptor.</summary>
    public Descriptor? Descriptor { get; init; }

    /// <summary>The <c>warp</c> descriptor, when present.</summary>
    public Descriptor? Warp { get; init; }

    /// <summary>Warp Text settings decoded from <see cref="Warp"/>; check <see cref="TextWarp.IsIdentity"/>.</summary>
    public TextWarp? WarpSettings { get; init; }

    /// <summary>The parsed text-engine tree, when it decoded.</summary>
    public EngineValue? EngineData { get; init; }

    /// <summary>Index of the layer's object in the document's Txt2 block (<c>TextIndex</c>).</summary>
    public int? TextIndex { get; init; }

    /// <summary>The layer's object in the document's Txt2 block, when the document has one.</summary>
    public TextEngineObject? TextEngineObject { get; init; }

    /// <summary>Anti-aliasing method, when stored.</summary>
    public TextAntiAlias? AntiAlias { get; init; }

    /// <summary>The descriptor's <c>bounds</c>: the layout box in text space (for point text, the line box around the baseline).</summary>
    public TextBounds? Bounds { get; init; }

    /// <summary>The descriptor's <c>boundingBox</c>: the tight ink box in text space.</summary>
    public TextBounds? BoundingBox { get; init; }

    /// <summary>The paragraph-text frame (EngineData <c>BoxBounds</c>) in text space.</summary>
    public TextBounds? BoxBounds { get; init; }

    /// <summary>Which stored records the values came from.</summary>
    public TextDataOrigin Origin { get; init; }

    /// <summary>Normalizes Photoshop paragraph separators (<c>\r</c>, <c>\r\n</c>, ETX line breaks) to <c>\n</c> and trims the trailing break.</summary>
    public static string NormalizeText(string text)
    {
        if (text.Length == 0)
        {
            return text;
        }

        var builder = new System.Text.StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r')
            {
                builder.Append('\n');
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }
            }
            else if (c == '\u0003')
            {
                builder.Append('\n');
            }
            else
            {
                builder.Append(c);
            }
        }

        while (builder.Length > 0 && builder[^1] == '\n')
        {
            builder.Length--;
        }

        return builder.ToString();
    }

    /// <summary>Parses a <c>TySh</c> tagged-block payload. Returns null when it does not decode.</summary>
    public static TextLayerInfo? Parse(ReadOnlyMemory<byte> payload)
    {
        try
        {
            var reader = new BigEndianReader(payload);
            var version = reader.ReadUInt16();
            if (version != 1)
            {
                return null;
            }

            var transform = ReadTransform(reader);
            _ = reader.ReadUInt16(); // text version (50)
            var text = Descriptor.ReadVersioned(reader);
            Descriptor? warp = null;
            if (reader.Remaining >= 6)
            {
                try
                {
                    _ = reader.ReadUInt16(); // warp version
                    warp = Descriptor.ReadVersioned(reader);
                }
                catch (PsdFormatException)
                {
                    warp = null;
                }
            }

            return FromDescriptor(text, warp, transform);
        }
        catch (PsdFormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Recovers the transform and <c>TextIndex</c> from a <c>TySh</c> payload whose
    /// descriptor does not decode, so the layer can still be matched to its Txt2 object.
    /// </summary>
    internal static (double[]? Transform, int? TextIndex) Salvage(ReadOnlySpan<byte> payload)
    {
        double[]? transform = null;
        if (payload.Length >= 50 && System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(payload) == 1)
        {
            transform = new double[6];
            for (var i = 0; i < 6; i++)
            {
                var value = System.Buffers.Binary.BinaryPrimitives.ReadDoubleBigEndian(payload[(2 + (i * 8))..]);
                transform[i] = double.IsFinite(value) ? value : (i is 0 or 3 ? 1 : 0);
            }
        }

        int? textIndex = null;
        var key = "TextIndexlong"u8;
        var at = payload.IndexOf(key);
        if (at >= 0 && at + key.Length + 4 <= payload.Length)
        {
            textIndex = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(payload[(at + key.Length)..]);
        }

        return (transform, textIndex);
    }

    /// <summary>A copy with the given members replaced (the Txt2 merge in <see cref="TextEngineResolver"/>).</summary>
    internal TextLayerInfo With(
        string? rawText = null,
        IReadOnlyList<TextStyleRun>? styleRuns = null,
        IReadOnlyList<TextParagraph>? paragraphs = null,
        IReadOnlyList<string>? fonts = null,
        TextEngineObject? textEngineObject = null,
        TextDataOrigin origin = TextDataOrigin.None) => new()
        {
            Text = rawText is null ? Text : NormalizeText(rawText),
            RawText = rawText ?? RawText,
            Transform = Transform,
            Orientation = Orientation,
            IsParagraphText = IsParagraphText,
            ShapeKind = ShapeKind,
            ShapeType = ShapeType,
            StyleRuns = styleRuns ?? StyleRuns,
            Paragraphs = paragraphs ?? Paragraphs,
            Fonts = fonts ?? Fonts,
            Descriptor = Descriptor,
            Warp = Warp,
            WarpSettings = WarpSettings,
            EngineData = EngineData,
            TextIndex = TextIndex,
            TextEngineObject = textEngineObject ?? TextEngineObject,
            AntiAlias = AntiAlias,
            Bounds = Bounds,
            BoundingBox = BoundingBox,
            BoxBounds = BoxBounds,
            Origin = Origin | origin,
        };

    internal static double[] ReadTransform(BigEndianReader reader)
    {
        var transform = new double[6];
        for (var i = 0; i < 6; i++)
        {
            transform[i] = reader.ReadDouble();
        }

        return transform;
    }

    internal static IReadOnlyList<string> DistinctFonts(IEnumerable<TextStyleRun> runs)
    {
        var fonts = new List<string>();
        foreach (var run in runs)
        {
            if (!string.IsNullOrEmpty(run.FontName) && !fonts.Contains(run.FontName))
            {
                fonts.Add(run.FontName);
            }
        }

        return fonts;
    }

    private static TextLayerInfo FromDescriptor(Descriptor text, Descriptor? warp, double[] transform)
    {
        var raw = text.GetString("Txt ") ?? string.Empty;
        EngineValue? engine = null;
        if (text.TryGet("EngineData", out var engineValue) && engineValue.Type == DescriptorValueType.RawData)
        {
            engine = EngineDataParser.Parse(engineValue.Raw.Span);
        }

        var origin = TextDataOrigin.TypeTool | (engine is null ? TextDataOrigin.None : TextDataOrigin.EngineData);

        // The descriptor text is authoritative; EngineData repeats it with a trailing \r.
        var engineText = engine?.Get("EngineDict", "Editor", "Text") is { Kind: EngineValueKind.String } editor ? editor.Text : null;
        if (string.IsNullOrEmpty(raw) && !string.IsNullOrEmpty(engineText))
        {
            raw = engineText;
        }

        var orientation = text.GetEnum("Ornt") == "Vrtc" ? TextOrientation.Vertical : TextOrientation.Horizontal;
        IReadOnlyList<TextStyleRun> runs = [];
        IReadOnlyList<TextParagraph> paragraphs = [];
        int? shapeType = null;
        TextBounds? boxBounds = null;
        TextAntiAlias? antiAlias = AntiAliasFromDescriptor(text.GetEnum("AntA"));
        if (engine is not null)
        {
            var source = string.IsNullOrEmpty(engineText) ? raw : engineText;
            runs = ReadStyleRuns(engine, source);
            paragraphs = ReadParagraphs(engine, source);
            var shape = engine.Get("EngineDict", "Rendered", "Shapes", "Children", 0);
            shapeType = EngineStyles.Integer(shape?["ShapeType"]) ?? EngineStyles.Integer(shape?.Get("Cookie", "Photoshop", "ShapeType"));
            boxBounds = BoundsFromList(shape?.Get("Cookie", "Photoshop", "BoxBounds"));
            if (antiAlias is null && EngineStyles.Integer(engine.Get("EngineDict", "AntiAlias")) is int engineAntiAlias and >= 0 and <= 4)
            {
                antiAlias = (TextAntiAlias)engineAntiAlias;
            }
        }

        var textIndex = text.TryGet("TextIndex", out var index) && index.Type == DescriptorValueType.Integer && index.Integer is >= int.MinValue and <= int.MaxValue
            ? (int?)index.Integer
            : null;

        return new TextLayerInfo
        {
            Text = NormalizeText(raw),
            RawText = raw,
            Transform = transform,
            Orientation = orientation,
            IsParagraphText = shapeType == 1,
            ShapeKind = shapeType switch
            {
                null or 0 => TextShapeKind.Point,
                1 => TextShapeKind.Box,
                _ => TextShapeKind.Other,
            },
            ShapeType = shapeType,
            StyleRuns = runs,
            Paragraphs = paragraphs,
            Fonts = DistinctFonts(runs),
            Descriptor = text,
            Warp = warp,
            WarpSettings = warp is null ? null : WarpFromDescriptor(warp),
            EngineData = engine,
            TextIndex = textIndex,
            AntiAlias = antiAlias,
            Bounds = BoundsFromDescriptor(text.GetObject("bounds")),
            BoundingBox = BoundsFromDescriptor(text.GetObject("boundingBox")),
            BoxBounds = boxBounds,
            Origin = origin,
        };
    }

    private static TextWarp WarpFromDescriptor(Descriptor warp)
    {
        static double Finite(double value) => double.IsFinite(value) ? value : 0;
        var key = warp.GetEnum("warpStyle") ?? "warpNone";
        return new TextWarp(
            TextWarp.StyleFromKey(key),
            key,
            Finite(warp.GetNumber("warpValue", 0)),
            Finite(warp.GetNumber("warpPerspective", 0)),
            Finite(warp.GetNumber("warpPerspectiveOther", 0)),
            warp.GetEnum("warpRotate") == "Vrtc" ? TextOrientation.Vertical : TextOrientation.Horizontal);
    }

    private static TextBounds? BoundsFromDescriptor(Descriptor? bounds)
    {
        if (bounds is null)
        {
            return null;
        }

        var left = bounds.GetNumber("Left", 0);
        var top = bounds.GetNumber("Top ", 0);
        var right = bounds.GetNumber("Rght", left);
        var bottom = bounds.GetNumber("Btom", top);
        return double.IsFinite(left) && double.IsFinite(top) && double.IsFinite(right) && double.IsFinite(bottom)
            ? new TextBounds(left, top, right, bottom)
            : null;
    }

    private static TextBounds? BoundsFromList(EngineValue? list)
    {
        if (list is not { Kind: EngineValueKind.List, Items.Count: >= 4 })
        {
            return null;
        }

        var values = new double[4];
        for (var i = 0; i < 4; i++)
        {
            if (EngineStyles.Number(list.Items[i]) is not double value)
            {
                return null;
            }

            values[i] = value;
        }

        return new TextBounds(values[0], values[1], values[2], values[3]);
    }

    /// <summary>Descriptor <c>AntA</c> tokens; the EngineData <c>/AntiAlias</c> numbers in parentheses.</summary>
    private static TextAntiAlias? AntiAliasFromDescriptor(string? value) => value switch
    {
        "Anno" or "antiAliasNone" => TextAntiAlias.None, // 0
        "AnCr" or "antiAliasCrisp" => TextAntiAlias.Crisp, // 1
        "AnSt" or "antiAliasStrong" => TextAntiAlias.Strong, // 2
        "AnSm" or "antiAliasSmooth" => TextAntiAlias.Smooth, // 3
        "AnSh" or "antiAliasSharp" => TextAntiAlias.Sharp, // 4
        _ => null,
    };

    private static List<TextStyleRun> ReadStyleRuns(EngineValue engine, string text)
    {
        var runs = new List<TextStyleRun>();
        var fontNames = new List<string>();
        var fontSet = engine.Get("ResourceDict", "FontSet") ?? engine.Get("DocumentResources", "FontSet");
        foreach (var font in fontSet?.Items ?? [])
        {
            fontNames.Add(font["Name"] is { Kind: EngineValueKind.String } name ? name.Text : string.Empty);
        }

        EngineValue? defaultStyle = null;
        if (EngineStyles.Integer(engine.Get("ResourceDict", "TheNormalStyleSheet")) is int sheetIndex)
        {
            defaultStyle = engine.Get("ResourceDict", "StyleSheetSet", sheetIndex, "StyleSheetData");
        }

        var runArray = engine.Get("EngineDict", "StyleRun", "RunArray");
        var lengths = engine.Get("EngineDict", "StyleRun", "RunLengthArray");
        if (runArray is null || lengths is null)
        {
            return runs;
        }

        var start = 0;
        for (var i = 0; i < runArray.Items.Count && i < lengths.Items.Count && start < text.Length; i++)
        {
            var length = Math.Min(EngineStyles.Length(lengths.Items[i]), text.Length - start);
            if (length <= 0)
            {
                continue;
            }

            var style = runArray.Items[i].Get("StyleSheet", "StyleSheetData");
            runs.Add(EngineStyles.Run(
                start,
                length,
                EngineStyles.Segment(text, start, length),
                key => style?[key] ?? defaultStyle?[key],
                StyleKeys.TypeTool,
                fontNames,
                1));
            start += length;
        }

        return runs;
    }

    private static List<TextParagraph> ReadParagraphs(EngineValue engine, string text)
    {
        var paragraphs = new List<TextParagraph>();
        EngineValue? defaultSheet = null;
        if (EngineStyles.Integer(engine.Get("ResourceDict", "TheNormalParagraphSheet")) is int sheetIndex)
        {
            defaultSheet = engine.Get("ResourceDict", "ParagraphSheetSet", sheetIndex, "Properties");
        }

        var runArray = engine.Get("EngineDict", "ParagraphRun", "RunArray");
        var lengths = engine.Get("EngineDict", "ParagraphRun", "RunLengthArray");
        if (runArray is null || lengths is null)
        {
            return paragraphs;
        }

        var start = 0;
        for (var i = 0; i < runArray.Items.Count && i < lengths.Items.Count && start < text.Length; i++)
        {
            var length = Math.Min(EngineStyles.Length(lengths.Items[i]), text.Length - start);
            if (length <= 0)
            {
                continue;
            }

            var properties = runArray.Items[i].Get("ParagraphSheet", "Properties");
            paragraphs.Add(EngineStyles.Paragraph(
                start,
                length,
                EngineStyles.Segment(text, start, length),
                key => properties?[key] ?? defaultSheet?[key],
                ParagraphKeys.TypeTool,
                1));
            start += length;
        }

        return paragraphs;
    }
}
