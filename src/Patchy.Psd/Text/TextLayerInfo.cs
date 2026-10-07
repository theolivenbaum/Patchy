using Patchy.Psd.Descriptors;
using Patchy.Psd.IO;

namespace Patchy.Psd.Text;

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
    double? Leading);

/// <summary>A paragraph with its alignment.</summary>
public sealed record TextParagraph(int Start, int Length, string Text, TextJustification Justification);

/// <summary>
/// The type-tool data of a text layer (<c>TySh</c>): the plain text, its
/// transform, and the character/paragraph styling decoded from EngineData.
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

    public IReadOnlyList<TextStyleRun> StyleRuns { get; init; } = [];

    public IReadOnlyList<TextParagraph> Paragraphs { get; init; } = [];

    /// <summary>Distinct font PostScript names referenced by the style runs, in first-use order.</summary>
    public IReadOnlyList<string> Fonts { get; init; } = [];

    /// <summary>The <c>TxLr</c> text descriptor.</summary>
    public Descriptor? Descriptor { get; init; }

    /// <summary>The <c>warp</c> descriptor, when present.</summary>
    public Descriptor? Warp { get; init; }

    /// <summary>The parsed text-engine tree, when it decoded.</summary>
    public EngineValue? EngineData { get; init; }

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

            var transform = new double[6];
            for (var i = 0; i < 6; i++)
            {
                transform[i] = reader.ReadDouble();
            }

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

    private static TextLayerInfo FromDescriptor(Descriptor text, Descriptor? warp, double[] transform)
    {
        var raw = text.GetString("Txt ") ?? string.Empty;
        EngineValue? engine = null;
        if (text.TryGet("EngineData", out var engineValue) && engineValue.Type == DescriptorValueType.RawData)
        {
            engine = EngineDataParser.Parse(engineValue.Raw.Span);
        }

        // The descriptor text is authoritative; EngineData repeats it with a trailing \r.
        var engineText = engine?.Get("EngineDict", "Editor", "Text")?.Text;
        if (string.IsNullOrEmpty(raw) && !string.IsNullOrEmpty(engineText))
        {
            raw = engineText;
        }

        var orientation = text.GetEnum("Ornt") == "Vrtc" ? TextOrientation.Vertical : TextOrientation.Horizontal;
        var runs = new List<TextStyleRun>();
        var paragraphs = new List<TextParagraph>();
        var fonts = new List<string>();
        var isBox = false;
        if (engine is not null)
        {
            var source = engineText ?? raw;
            ReadStyleRuns(engine, source, runs, fonts);
            ReadParagraphs(engine, source, paragraphs);
            var shapeType = engine.Get("EngineDict", "Rendered", "Shapes", "Children", 0, "ShapeType")?.Number;
            isBox = shapeType == 1;
        }

        return new TextLayerInfo
        {
            Text = NormalizeText(raw),
            RawText = raw,
            Transform = transform,
            Orientation = orientation,
            IsParagraphText = isBox,
            StyleRuns = runs,
            Paragraphs = paragraphs,
            Fonts = fonts,
            Descriptor = text,
            Warp = warp,
            EngineData = engine,
        };
    }

    private static void ReadStyleRuns(EngineValue engine, string text, List<TextStyleRun> runs, List<string> fonts)
    {
        var fontSet = engine.Get("ResourceDict", "FontSet") ?? engine.Get("DocumentResources", "FontSet");
        var defaultSheetIndex = (int?)engine.Get("ResourceDict", "TheNormalStyleSheet")?.Number;
        EngineValue? defaultStyle = null;
        if (defaultSheetIndex is int sheetIndex)
        {
            defaultStyle = engine.Get("ResourceDict", "StyleSheetSet", sheetIndex, "StyleSheetData");
        }

        var runArray = engine.Get("EngineDict", "StyleRun", "RunArray");
        var lengths = engine.Get("EngineDict", "StyleRun", "RunLengthArray");
        if (runArray is null || lengths is null)
        {
            return;
        }

        var start = 0;
        for (var i = 0; i < runArray.Items.Count && i < lengths.Items.Count; i++)
        {
            var length = (int)lengths.Items[i].Number;
            var style = runArray.Items[i]["StyleSheet"]?["StyleSheetData"];
            EngineValue? Lookup(string key) => style?[key] ?? defaultStyle?[key];

            string? fontName = null;
            if (Lookup("Font") is { Kind: EngineValueKind.Number } fontIndex && fontSet is not null)
            {
                fontName = fontSet[(int)fontIndex.Number]?["Name"]?.Text;
            }

            if (!string.IsNullOrEmpty(fontName) && !fonts.Contains(fontName))
            {
                fonts.Add(fontName);
            }

            var fontSize = Lookup("FontSize")?.Number;
            PsdColor? color = null;
            if (Lookup("FillColor")?["Values"] is { Items.Count: >= 4 } values)
            {
                static byte C(double v) => (byte)Math.Clamp(Math.Round(v * 255), 0, 255);
                color = new PsdColor(C(values.Items[1].Number), C(values.Items[2].Number), C(values.Items[3].Number), C(values.Items[0].Number));
            }

            var autoLeading = Lookup("AutoLeading")?.Boolean ?? true;
            var segment = Segment(text, start, length);
            runs.Add(new TextStyleRun(
                start,
                length,
                NormalizeText(segment),
                fontName,
                fontSize,
                color,
                Lookup("FauxBold")?.Boolean ?? false,
                Lookup("FauxItalic")?.Boolean ?? false,
                Lookup("Tracking")?.Number,
                autoLeading ? null : Lookup("Leading")?.Number));
            start += length;
        }
    }

    private static void ReadParagraphs(EngineValue engine, string text, List<TextParagraph> paragraphs)
    {
        var runArray = engine.Get("EngineDict", "ParagraphRun", "RunArray");
        var lengths = engine.Get("EngineDict", "ParagraphRun", "RunLengthArray");
        if (runArray is null || lengths is null)
        {
            return;
        }

        var start = 0;
        for (var i = 0; i < runArray.Items.Count && i < lengths.Items.Count; i++)
        {
            var length = (int)lengths.Items[i].Number;
            var justification = (int?)runArray.Items[i].Get("ParagraphSheet", "Properties", "Justification")?.Number ?? 0;
            paragraphs.Add(new TextParagraph(start, length, NormalizeText(Segment(text, start, length)), (TextJustification)Math.Clamp(justification, 0, 6)));
            start += length;
        }
    }

    private static string Segment(string text, int start, int length)
    {
        if (start >= text.Length || length <= 0)
        {
            return string.Empty;
        }

        return text.Substring(start, Math.Min(length, text.Length - start));
    }
}
