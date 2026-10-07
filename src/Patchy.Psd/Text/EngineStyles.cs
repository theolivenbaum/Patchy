namespace Patchy.Psd.Text;

/// <summary>
/// Key names of one character style sheet flavor. TySh EngineData names its
/// keys (<c>/FontSize</c>); the document-level Txt2 block numbers them
/// (<c>/1</c>). A null key is a property the flavor is not known to store.
/// Txt2 numbers follow the single-setting captures in
/// <c>.reference/docs/txt2.md</c> ("Key map").
/// </summary>
internal sealed class StyleKeys
{
    public static readonly StyleKeys TypeTool = new()
    {
        Font = "Font",
        FontSize = "FontSize",
        FauxBold = "FauxBold",
        FauxItalic = "FauxItalic",
        AutoLeading = "AutoLeading",
        Leading = "Leading",
        HorizontalScale = "HorizontalScale",
        VerticalScale = "VerticalScale",
        Tracking = "Tracking",
        BaselineShift = "BaselineShift",
        AutoKerning = "AutoKerning",
        Kerning = "Kerning",
        FontCaps = "FontCaps",
        FontBaseline = "FontBaseline",
        Underline = "Underline",
        Strikethrough = "Strikethrough",
        Ligatures = "Ligatures",
        DLigatures = "DLigatures",
        BaselineDirection = "BaselineDirection",
        Language = "Language",
        NoBreak = "NoBreak",
        FillColor = "FillColor",
        StrokeColor = "StrokeColor",
        FillFlag = "FillFlag",
        StrokeFlag = "StrokeFlag",
        OutlineWidth = "OutlineWidth",
    };

    public static readonly StyleKeys TextEngine = new()
    {
        Font = "0",
        FontSize = "1",
        FauxBold = "2",
        FauxItalic = "3",
        AutoLeading = "4",
        Leading = "5",
        HorizontalScale = "6",
        VerticalScale = "7",
        Tracking = "8",
        BaselineShift = "9",
        AutoKerning = "11",
        Ligatures = "18",
        Language = "38",
        FillColor = "53",
        StrokeColor = "54",
    };

    public string? Font { get; private init; }

    public string? FontSize { get; private init; }

    public string? FauxBold { get; private init; }

    public string? FauxItalic { get; private init; }

    public string? AutoLeading { get; private init; }

    public string? Leading { get; private init; }

    public string? HorizontalScale { get; private init; }

    public string? VerticalScale { get; private init; }

    public string? Tracking { get; private init; }

    public string? BaselineShift { get; private init; }

    public string? AutoKerning { get; private init; }

    public string? Kerning { get; private init; }

    public string? FontCaps { get; private init; }

    public string? FontBaseline { get; private init; }

    public string? Underline { get; private init; }

    public string? Strikethrough { get; private init; }

    public string? Ligatures { get; private init; }

    public string? DLigatures { get; private init; }

    public string? BaselineDirection { get; private init; }

    public string? Language { get; private init; }

    public string? NoBreak { get; private init; }

    public string? FillColor { get; private init; }

    public string? StrokeColor { get; private init; }

    public string? FillFlag { get; private init; }

    public string? StrokeFlag { get; private init; }

    public string? OutlineWidth { get; private init; }
}

/// <summary>Key names of one paragraph sheet flavor (see <see cref="StyleKeys"/>).</summary>
internal sealed class ParagraphKeys
{
    public static readonly ParagraphKeys TypeTool = new()
    {
        Justification = "Justification",
        FirstLineIndent = "FirstLineIndent",
        StartIndent = "StartIndent",
        EndIndent = "EndIndent",
        SpaceBefore = "SpaceBefore",
        SpaceAfter = "SpaceAfter",
        AutoLeading = "AutoLeading",
        AutoHyphenate = "AutoHyphenate",
        Direction = "ParagraphDirection",
    };

    public static readonly ParagraphKeys TextEngine = new()
    {
        Justification = "0",
        FirstLineIndent = "1",
        StartIndent = "2",
        EndIndent = "3",
        SpaceBefore = "4",
        SpaceAfter = "5",
        AutoLeading = "7",
        AutoHyphenate = "9",
        Direction = "33",
        ListStyle = "36",
        ListTier = "37",
    };

    public string? Justification { get; private init; }

    public string? FirstLineIndent { get; private init; }

    public string? StartIndent { get; private init; }

    public string? EndIndent { get; private init; }

    public string? SpaceBefore { get; private init; }

    public string? SpaceAfter { get; private init; }

    public string? AutoLeading { get; private init; }

    public string? AutoHyphenate { get; private init; }

    public string? Direction { get; private init; }

    public string? ListStyle { get; private init; }

    public string? ListTier { get; private init; }
}

/// <summary>
/// Builds <see cref="TextStyleRun"/> and <see cref="TextParagraph"/> values from
/// EngineData sheets of either flavor. Lookups fall back from the run's own
/// (often sparse) sheet to the document's normal sheet.
/// </summary>
internal static class EngineStyles
{
    /// <summary>Photoshop's ceiling for a type size in pixels; larger values mark a misread record.</summary>
    public const double MaxTextSize = 8192;

    public delegate EngineValue? Lookup(string key);

    public static double? Number(EngineValue? value) =>
        value is { Kind: EngineValueKind.Number } && double.IsFinite(value.Number) ? value.Number : null;

    public static bool? Flag(EngineValue? value) => value?.Kind switch
    {
        EngineValueKind.Boolean => value.Boolean,
        EngineValueKind.Number when double.IsFinite(value.Number) => value.Number != 0,
        _ => null,
    };

    /// <summary>A finite number as an int, or null when it is missing or out of range.</summary>
    public static int? Integer(EngineValue? value)
    {
        var number = Number(value);
        return number is >= int.MinValue and <= int.MaxValue ? (int)Math.Round(number.Value) : null;
    }

    /// <summary>Run lengths as non-negative ints (malformed entries count as 0).</summary>
    public static int Length(EngineValue? value) => Math.Max(0, Integer(value) ?? 0);

    /// <summary>
    /// Decodes a text-engine color: TySh <c>&lt;&lt; /Type t /Values [...] &gt;&gt;</c> or
    /// Txt2 <c>&lt;&lt; /99 /SimplePaint /0 &lt;&lt; /0 t /1 [...] &gt;&gt; &gt;&gt;</c>.
    /// Type 1 is [alpha, red, green, blue], 2 is [alpha, cyan, magenta, yellow, black]
    /// ink fractions, 0 is [alpha, level] with 0 black
    /// (<c>rgb_color_from_engine_values</c>, .reference/src/psd/psd_text_read.cpp).
    /// </summary>
    public static PsdColor? Color(EngineValue? node)
    {
        if (node is not { Kind: EngineValueKind.Dictionary })
        {
            return null;
        }

        EngineValue? values;
        int type;
        if (node["Values"] is { Kind: EngineValueKind.List } named)
        {
            values = named;
            type = Integer(node["Type"]) ?? 1;
        }
        else if (node["0"] is { Kind: EngineValueKind.Dictionary } paint && paint["1"] is { Kind: EngineValueKind.List } numbered)
        {
            values = numbered;
            type = Integer(paint["0"]) ?? 1;
        }
        else
        {
            return null;
        }

        var v = new double[values.Items.Count];
        for (var i = 0; i < v.Length; i++)
        {
            if (Number(values.Items[i]) is not double component)
            {
                return null;
            }

            v[i] = component;
        }

        static byte Unit(double value) => (byte)Math.Clamp(Math.Round(value * 255), 0, 255);
        switch (type)
        {
            case 0 when v.Length >= 2:
                var level = Unit(v[1]);
                return new PsdColor(level, level, level, Unit(v[0]));
            case 2 when v.Length >= 5:
                var k = 1 - Math.Clamp(v[4], 0, 1);
                return new PsdColor(
                    Unit((1 - Math.Clamp(v[1], 0, 1)) * k),
                    Unit((1 - Math.Clamp(v[2], 0, 1)) * k),
                    Unit((1 - Math.Clamp(v[3], 0, 1)) * k),
                    Unit(v[0]));
            case 0 or 2:
                return null;
            default:
                if (v.Length < 4)
                {
                    return null;
                }

                // Old writers stored 0..255 components; a value above 1 marks that scale.
                var normalized = v[1] <= 1 && v[2] <= 1 && v[3] <= 1;
                byte C(double value) => normalized ? Unit(value) : (byte)Math.Clamp(Math.Round(value), 0, 255);
                return new PsdColor(C(v[1]), C(v[2]), C(v[3]), Unit(Math.Min(1, v[0])));
        }
    }

    /// <summary>
    /// Builds one style run. <paramref name="lengthScale"/> converts the sheet's
    /// length values (size, leading, baseline shift) to document pixels; it is 1
    /// for TySh and for Txt2 runs whose unit flag says pixels.
    /// </summary>
    public static TextStyleRun Run(
        int start,
        int length,
        string segment,
        Lookup lookup,
        StyleKeys keys,
        IReadOnlyList<string> fontNames,
        double lengthScale)
    {
        EngineValue? Get(string? key) => key is null ? null : lookup(key);
        double? Scaled(string? key) => Number(Get(key)) is double value ? value * lengthScale : null;

        string? fontName = null;
        if (Integer(Get(keys.Font)) is int fontIndex && fontIndex >= 0 && fontIndex < fontNames.Count)
        {
            fontName = fontNames[fontIndex];
        }

        if (string.IsNullOrEmpty(fontName))
        {
            fontName = null;
        }

        var fontSize = Scaled(keys.FontSize);
        if (fontSize is <= 0 or > MaxTextSize)
        {
            fontSize = null;
        }

        var autoLeading = Flag(Get(keys.AutoLeading)) ?? true;

        // Photoshop records a stale /Leading even for auto-leading runs; only fixed leading counts.
        var leading = autoLeading ? null : Scaled(keys.Leading);
        if (leading is <= 0)
        {
            leading = null;
        }

        var tracking = Number(Get(keys.Tracking));
        if (tracking is double t && Math.Abs(t) >= 10000)
        {
            tracking = null;
        }

        return new TextStyleRun(
            start,
            length,
            TextLayerInfo.NormalizeText(segment),
            fontName,
            fontSize,
            Color(Get(keys.FillColor)),
            Flag(Get(keys.FauxBold)) ?? false,
            Flag(Get(keys.FauxItalic)) ?? false,
            tracking,
            leading)
        {
            AutoLeading = autoLeading,
            HorizontalScale = Scale(Number(Get(keys.HorizontalScale))),
            VerticalScale = Scale(Number(Get(keys.VerticalScale))),
            BaselineShift = Scaled(keys.BaselineShift) ?? 0,
            AutoKerning = Flag(Get(keys.AutoKerning)) ?? true,
            Kerning = Number(Get(keys.Kerning)) ?? 0,
            Caps = Integer(Get(keys.FontCaps)) is int caps and >= 0 and <= 2 ? (TextCaps)caps : TextCaps.Normal,
            BaselinePosition = Integer(Get(keys.FontBaseline)) is int position and >= 0 and <= 2 ? (TextBaselinePosition)position : TextBaselinePosition.Normal,
            Underline = Flag(Get(keys.Underline)) ?? false,
            Strikethrough = Flag(Get(keys.Strikethrough)) ?? false,
            Ligatures = Flag(Get(keys.Ligatures)) ?? true,
            DiscretionaryLigatures = Flag(Get(keys.DLigatures)) ?? false,
            BaselineDirection = Integer(Get(keys.BaselineDirection)) ?? 0,
            Language = Integer(Get(keys.Language)),
            NoBreak = Flag(Get(keys.NoBreak)) ?? false,
            StrokeColor = Color(Get(keys.StrokeColor)),
            FillEnabled = Flag(Get(keys.FillFlag)) ?? true,
            StrokeEnabled = Flag(Get(keys.StrokeFlag)) ?? false,
            StrokeWidth = Number(Get(keys.OutlineWidth)),
        };
    }

    /// <summary>Builds one paragraph run (see <see cref="Run"/> for <paramref name="lengthScale"/>).</summary>
    public static TextParagraph Paragraph(int start, int length, string segment, Lookup lookup, ParagraphKeys keys, double lengthScale)
    {
        EngineValue? Get(string? key) => key is null ? null : lookup(key);
        double Scaled(string? key) => Number(Get(key)) is double value ? value * lengthScale : 0;

        var justification = Integer(Get(keys.Justification)) ?? 0;
        var fraction = Number(Get(keys.AutoLeading));
        var direction = Integer(Get(keys.Direction)) switch
        {
            0 => TextDirection.LeftToRight,
            1 => TextDirection.RightToLeft,
            _ => TextDirection.Auto,
        };
        int? listStyle = Get(keys.ListStyle) is { Kind: EngineValueKind.Number } list ? Integer(list) : null;
        return new TextParagraph(start, length, TextLayerInfo.NormalizeText(segment), (TextJustification)Math.Clamp(justification, 0, 6))
        {
            FirstLineIndent = Scaled(keys.FirstLineIndent),
            StartIndent = Scaled(keys.StartIndent),
            EndIndent = Scaled(keys.EndIndent),
            SpaceBefore = Scaled(keys.SpaceBefore),
            SpaceAfter = Scaled(keys.SpaceAfter),
            AutoLeadingFraction = fraction is > 0.01 and < 10 ? fraction.Value : 1.2,
            AutoHyphenate = Flag(Get(keys.AutoHyphenate)) ?? false,
            Direction = direction,
            ListStyleIndex = listStyle is >= 0 ? listStyle : null,
            ListTier = Math.Max(0, Integer(Get(keys.ListTier)) ?? 0),
        };
    }

    /// <summary>The part of <paramref name="text"/> a run covers, clipped to the text.</summary>
    public static string Segment(string text, int start, int length)
    {
        if (start >= text.Length || length <= 0)
        {
            return string.Empty;
        }

        return text.Substring(start, Math.Min(length, text.Length - start));
    }

    private static double Scale(double? value) => value is > 0.01 and < 100 ? value.Value : 1;
}
