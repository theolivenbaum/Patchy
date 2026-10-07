using HarfBuzzSharp;

namespace XRay.Psd.Text;

/// <summary>The resolved character style of one run, in engine units.</summary>
internal sealed class LayoutStyle
{
    public required FontFace Face { get; init; }

    public double Size { get; init; } = 12;

    public double HorizontalScale { get; init; } = 1;

    public double VerticalScale { get; init; } = 1;

    public bool FauxBold { get; init; }

    public bool FauxItalic { get; init; }

    /// <summary>Tracking plus manual kerning, in thousandths of an em.</summary>
    public double Spacing { get; init; }

    public double? FixedLeading { get; init; }

    public double BaselineShift { get; init; }

    public TextCaps Caps { get; init; }

    public TextBaselinePosition Position { get; init; }

    public bool Underline { get; init; }

    public bool Strikethrough { get; init; }

    public PsdColor Fill { get; init; } = PsdColor.Black;

    public bool FillEnabled { get; init; } = true;

    public PsdColor? Stroke { get; init; }

    public double StrokeWidth { get; init; }

    public bool NoBreak { get; init; }

    /// <summary>Vertical type: Roman glyphs lie rotated (BaselineDirection 2, or unspecified).</summary>
    public bool RotateRoman { get; init; }

    public required Feature[] Features { get; init; }

    public double Leading(double autoFraction) => FixedLeading ?? (autoFraction * Size);
}

/// <summary>One glyph placed in text space: its outline (1 px per em, y down) maps through the matrix <c>(A B C D E F)</c>: <c>X = A x + C y + E</c>, <c>Y = B x + D y + F</c>.</summary>
internal readonly record struct PlacedGlyph(FontFace Face, ushort Glyph, int Style, double A, double B, double C, double D, double E, double F);

/// <summary>An underline or strikethrough bar in text space (horizontal text only).</summary>
internal readonly record struct PlacedBar(int Style, double Left, double Right, double Top, double Bottom);

/// <summary>The laid-out text of one layer in text space.</summary>
internal sealed class LaidOutText
{
    public List<LayoutStyle> Styles { get; } = [];

    public List<PlacedGlyph> Glyphs { get; } = [];

    public List<PlacedBar> Bars { get; } = [];

    /// <summary>PostScript names (or families) that did not resolve and were drawn with the fallback face.</summary>
    public SortedSet<string> MissingFonts { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Photoshop's type layout over the parsed text model, following the reference's
/// calibrated rules (<c>.reference/docs/text-render-calibration.md</c>,
/// <c>.reference/src/ui/text_layout.cpp</c>):
/// <list type="bullet">
/// <item>Engine units are document pixels before the layer transform.</item>
/// <item>Point text puts the first baseline at the origin; box text at the frame top plus the largest OS/2 typographic ascender on line 1.</item>
/// <item>Each following baseline advances by the max effective leading of the entered line (auto = paragraph fraction x size), plus paragraph spacing.</item>
/// <item>Tracking adds size x tracking / 1000 per inter-glyph gap, not after the last glyph; faux bold adds 0.03 em to every advance.</item>
/// <item>Horizontal and vertical glyph scales stretch glyphs only; leading stays size based.</item>
/// <item>Vertical type stacks em cells (whitespace advances by its width), columns advance left by leading.</item>
/// </list>
/// </summary>
internal sealed class TextLayoutEngine
{
    /// <summary>Faux bold widens each glyph and its advance by this fraction of the em (reference <c>kFauxBoldEmFraction</c>).</summary>
    public const double FauxBoldEm = 0.03;

    /// <summary>Faux italic shear, tan 12 degrees (reference <c>kFauxItalicSlant</c>).</summary>
    public const double FauxItalicSlant = 0.2126;

    // Photoshop's default superscript/subscript size and position (Type preferences: 58.3% and 33.3%).
    private const double ScriptSize = 0.583;
    private const double ScriptOffset = 0.333;

    // Synthesized small caps scale.
    private const double SmallCapsSize = 0.7;

    private readonly Func<TextStyleRun?, (FontFace Face, bool Substituted)> _faceForRun;
    private readonly Func<int, FontFace?> _fallbackForCodePoint;

    public TextLayoutEngine(Func<TextStyleRun?, (FontFace Face, bool Substituted)> faceForRun, Func<int, FontFace?> fallbackForCodePoint)
    {
        _faceForRun = faceForRun;
        _fallbackForCodePoint = fallbackForCodePoint;
    }

    public LaidOutText Layout(TextLayerInfo info)
    {
        var output = new LaidOutText();
        var text = info.RawText.Length > 0 ? info.RawText : info.Text;
        if (text.Length == 0)
        {
            return output;
        }

        var styleOf = ResolveStyles(info, text, output);
        var paragraphs = SplitParagraphs(info, text);
        if (info.Orientation == TextOrientation.Vertical)
        {
            new VerticalPass(this, info, text, styleOf, output).Run(paragraphs);
        }
        else
        {
            new HorizontalPass(this, info, text, styleOf, output).Run(paragraphs);
        }

        return output;
    }

    // ---- Styles ----

    private int[] ResolveStyles(TextLayerInfo info, string text, LaidOutText output)
    {
        var styleOf = new int[text.Length];
        Array.Fill(styleOf, -1);
        var runs = info.StyleRuns;
        // Runs are consecutive; a run reaching back over earlier ones only claims what is left,
        // which keeps hostile run tables linear in the text length.
        var assigned = 0;
        foreach (var run in runs)
        {
            var index = output.Styles.Count;
            output.Styles.Add(CreateStyle(run, info, output));
            var start = Math.Clamp(Math.Max(run.Start, assigned), 0, text.Length);
            var end = (int)Math.Clamp((long)run.Start + Math.Max(0, run.Length), start, text.Length);
            assigned = Math.Max(assigned, end);
            for (var i = start; i < end; i++)
            {
                styleOf[i] = index;
            }
        }

        if (output.Styles.Count == 0)
        {
            output.Styles.Add(CreateStyle(null, info, output));
        }

        // Characters no run covers take the previous character's style (the first run before any).
        var current = 0;
        for (var i = 0; i < styleOf.Length; i++)
        {
            if (styleOf[i] < 0)
            {
                styleOf[i] = current;
            }
            else
            {
                current = styleOf[i];
            }
        }

        return styleOf;
    }

    private LayoutStyle CreateStyle(TextStyleRun? run, TextLayerInfo info, LaidOutText output)
    {
        var (face, substituted) = _faceForRun(run);
        if (substituted)
        {
            output.MissingFonts.Add(run?.FontName ?? run?.FontFamily ?? "(none)");
        }

        var request = run is null ? null : FontRequest.FromRun(run);
        var size = Sane(run?.FontSize, 12, 0.01, EngineStyleLimits.MaxSize);
        var features = new List<Feature>
        {
            new(new Tag('k', 'e', 'r', 'n'), run?.AutoKerning == false ? 0u : 1u),
            new(new Tag('l', 'i', 'g', 'a'), run?.Ligatures == false ? 0u : 1u),
            new(new Tag('c', 'l', 'i', 'g'), run?.Ligatures == false ? 0u : 1u),
            new(new Tag('d', 'l', 'i', 'g'), run?.DiscretionaryLigatures == true ? 1u : 0u),
        };
        if (info.Orientation == TextOrientation.Vertical)
        {
            features.Add(new Feature(new Tag('v', 'e', 'r', 't'), 1u));
        }

        var tracking = run?.Tracking is { } t && double.IsFinite(t) ? Math.Clamp(t, -1000, 10000) : 0;
        var kerning = run is { AutoKerning: false } && double.IsFinite(run.Kerning) ? Math.Clamp(run.Kerning, -1000, 10000) : 0;
        // The model reports Leading only for fixed leading (null means auto).
        double? leading = run?.Leading is { } fixedLeading && double.IsFinite(fixedLeading) && fixedLeading >= 0 ? Math.Min(fixedLeading, EngineStyleLimits.MaxSize * 4) : null;
        return new LayoutStyle
        {
            Face = face,
            Size = size,
            HorizontalScale = Sane(run?.HorizontalScale, 1, 0.01, 100),
            VerticalScale = Sane(run?.VerticalScale, 1, 0.01, 100),
            // A face that cannot be bold or italic (missing, or a family without that face) is synthesized like Photoshop's faux styles.
            FauxBold = (run?.FauxBold ?? false) || (request is { Bold: true } && !face.Typeface.IsBold),
            FauxItalic = (run?.FauxItalic ?? false) || (request is { Italic: true } && !face.Typeface.IsItalic),
            Spacing = tracking + kerning,
            FixedLeading = leading,
            BaselineShift = run is not null && double.IsFinite(run.BaselineShift) ? Math.Clamp(run.BaselineShift, -EngineStyleLimits.MaxSize, EngineStyleLimits.MaxSize) : 0,
            Caps = run?.Caps ?? TextCaps.Normal,
            Position = run?.BaselinePosition ?? TextBaselinePosition.Normal,
            Underline = run?.Underline ?? false,
            Strikethrough = run?.Strikethrough ?? false,
            Fill = run?.FillColor ?? PsdColor.Black,
            FillEnabled = run?.FillEnabled ?? true,
            Stroke = run is { StrokeEnabled: true } ? run.StrokeColor : null,
            StrokeWidth = Sane(run?.StrokeWidth, 1, 0, 1000),
            NoBreak = run?.NoBreak ?? false,
            RotateRoman = run is null || run.BaselineDirection != 1,
            Features = [.. features],
        };
    }

    private static double Sane(double? value, double fallback, double min, double max) =>
        value is { } v && double.IsFinite(v) && v > 0 ? Math.Clamp(v, min, max) : fallback;

    // ---- Paragraphs ----

    private sealed record ParagraphSpan(int Start, int End, TextParagraph? Properties, bool RightToLeft);

    private static List<ParagraphSpan> SplitParagraphs(TextLayerInfo info, string text)
    {
        var spans = new List<ParagraphSpan>();
        var start = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            if (i < text.Length && text[i] is not ('\r' or '\n'))
            {
                continue;
            }

            var properties = ParagraphAt(info, start);
            spans.Add(new ParagraphSpan(start, i, properties, IsRightToLeft(text, start, i, properties)));
            if (i < text.Length - 1 && text[i] == '\r' && text[i + 1] == '\n')
            {
                i++;
            }

            start = i + 1;
        }

        // Photoshop stores a trailing paragraph separator; it starts no visible line.
        if (spans.Count > 1 && spans[^1].Start >= text.Length)
        {
            spans.RemoveAt(spans.Count - 1);
        }

        return spans;
    }

    private static TextParagraph? ParagraphAt(TextLayerInfo info, int index)
    {
        TextParagraph? last = null;
        foreach (var paragraph in info.Paragraphs)
        {
            if (index >= paragraph.Start && index < paragraph.Start + Math.Max(1, paragraph.Length))
            {
                return paragraph;
            }

            if (paragraph.Start <= index)
            {
                last = paragraph;
            }
        }

        return last ?? (info.Paragraphs.Count > 0 ? info.Paragraphs[0] : null);
    }

    private static bool IsRightToLeft(string text, int start, int end, TextParagraph? properties)
    {
        switch (properties?.Direction)
        {
            case TextDirection.RightToLeft:
                return true;
            case TextDirection.LeftToRight:
                return false;
        }

        for (var i = start; i < end; i++)
        {
            var kind = Classify(CodePointAt(text, i));
            if (kind != BidiClass.Neutral)
            {
                return kind == BidiClass.Right;
            }
        }

        return false;
    }

    // ---- Shaping ----

    /// <summary>One shaped glyph in logical order. Advances and offsets are text-space units (y down).</summary>
    private struct ShapedGlyph
    {
        public int Char;
        public ushort Id;
        public FontFace Face;
        public int Style;
        public double Advance;
        public double OffsetX;
        public double OffsetY;
        public double Scale;
        public double Rise;
    }

    private sealed class ShapedParagraph
    {
        public required ParagraphSpan Span { get; init; }

        public required ShapedGlyph[] Glyphs { get; init; }

        /// <summary>Per character: the advance of its cluster (on the cluster's first character, 0 on the others).</summary>
        public required double[] CharAdvance { get; init; }

        /// <summary>Per character: the tracking gap after it when it ends a cluster.</summary>
        public required double[] CharGap { get; init; }

        /// <summary>Per character: bidi embedding level (0 or 1 for LTR paragraphs, 1 or 2 for RTL).</summary>
        public required byte[] Levels { get; init; }

        /// <summary>Per character: em size scale (small caps, superscript, subscript).</summary>
        public required double[] CharScale { get; init; }
    }

    private ShapedParagraph Shape(string text, int[] styleOf, List<LayoutStyle> styles, ParagraphSpan span, bool vertical)
    {
        var length = span.End - span.Start;
        var shapedText = text.ToCharArray(span.Start, length);
        var scale = new double[length];
        var rise = new double[length];
        var faces = new FontFace[length];
        var levels = ResolveLevels(text, span);
        for (var i = 0; i < length; i++)
        {
            var style = styles[styleOf[span.Start + i]];
            var factor = 1.0;
            var ch = shapedText[i];
            if (style.Caps == TextCaps.AllCaps)
            {
                shapedText[i] = char.ToUpperInvariant(ch);
            }
            else if (style.Caps == TextCaps.SmallCaps && char.IsLower(ch))
            {
                shapedText[i] = char.ToUpperInvariant(ch);
                factor = SmallCapsSize;
            }

            var shift = style.BaselineShift;
            if (style.Position == TextBaselinePosition.Superscript)
            {
                shift += style.Size * ScriptOffset;
                factor *= ScriptSize;
            }
            else if (style.Position == TextBaselinePosition.Subscript)
            {
                shift -= style.Size * ScriptOffset;
                factor *= ScriptSize;
            }

            scale[i] = factor;
            rise[i] = shift;
        }

        var working = new string(shapedText);
        for (var i = 0; i < length; i++)
        {
            if (char.IsLowSurrogate(working[i]) && i > 0)
            {
                faces[i] = faces[i - 1];
                continue;
            }

            var style = styles[styleOf[span.Start + i]];
            var codePoint = CodePointAt(working, i);
            faces[i] = style.Face.Covers(codePoint) || IsControlOrSpace(codePoint) ? style.Face : _fallbackForCodePoint(codePoint) ?? style.Face;
        }

        var glyphs = new List<ShapedGlyph>(length);
        var itemStart = 0;
        while (itemStart < length)
        {
            var styleIndex = styleOf[span.Start + itemStart];
            var itemEnd = itemStart + 1;
            while (itemEnd < length && styleOf[span.Start + itemEnd] == styleIndex && faces[itemEnd] == faces[itemStart]
                && scale[itemEnd] == scale[itemStart] && rise[itemEnd] == rise[itemStart] && levels[itemEnd] == levels[itemStart])
            {
                itemEnd++;
            }

            var style = styles[styleIndex];
            var face = faces[itemStart];
            var rtl = !vertical && (levels[itemStart] & 1) == 1;
            var (infos, positions) = face.Shape(working, itemStart, itemEnd - itemStart, rtl, style.Features);
            var em = style.Size * scale[itemStart];
            var unit = em / face.UnitsPerEm;
            var bold = style.FauxBold ? FauxBoldEm * em : 0;
            var itemGlyphs = new ShapedGlyph[infos.Length];
            for (var g = 0; g < infos.Length; g++)
            {
                var cluster = (int)Math.Clamp(infos[g].Cluster, (uint)itemStart, (uint)(itemEnd - 1));
                itemGlyphs[g] = new ShapedGlyph
                {
                    Char = cluster,
                    Id = (ushort)infos[g].Codepoint,
                    Face = face,
                    Style = styleIndex,
                    Advance = (positions[g].XAdvance * unit * style.HorizontalScale) + bold,
                    OffsetX = positions[g].XOffset * unit * style.HorizontalScale,
                    OffsetY = -positions[g].YOffset * unit * style.VerticalScale,
                    Scale = scale[itemStart],
                    Rise = rise[itemStart],
                };
            }

            if (rtl)
            {
                Array.Reverse(itemGlyphs);
            }

            glyphs.AddRange(itemGlyphs);
            itemStart = itemEnd;
        }

        var charAdvance = new double[length];
        var charGap = new double[length];
        var clusterEnds = new bool[length];
        foreach (var glyph in glyphs)
        {
            charAdvance[glyph.Char] += glyph.Advance;
        }

        // Glyphs are in logical order, so a cluster ends where the next glyph starts a later
        // character; its last character carries the tracking gap.
        for (var g = 0; g < glyphs.Count; g++)
        {
            var c = glyphs[g].Char;
            var next = g + 1 < glyphs.Count ? glyphs[g + 1].Char : length;
            if (next != c)
            {
                clusterEnds[Math.Clamp(next - 1, c, length - 1)] = true;
            }
        }

        for (var i = 0; i < length; i++)
        {
            if (clusterEnds[i])
            {
                var style = styles[styleOf[span.Start + i]];
                charGap[i] = style.Size * style.Spacing / 1000;
            }
        }

        return new ShapedParagraph
        {
            Span = span,
            Glyphs = [.. glyphs],
            CharAdvance = charAdvance,
            CharGap = charGap,
            Levels = levels,
            CharScale = scale,
        };
    }

    // ---- Bidi (two-level simplification of UAX 9) ----

    private enum BidiClass
    {
        Neutral,
        Left,
        Right,
    }

    private static BidiClass Classify(int codePoint)
    {
        if (IsRtlCodePoint(codePoint))
        {
            return BidiClass.Right;
        }

        return codePoint < 0x10000 ? (char.IsLetterOrDigit((char)codePoint) ? BidiClass.Left : BidiClass.Neutral) : BidiClass.Left;
    }

    private static bool IsRtlCodePoint(int cp) =>
        cp is (>= 0x0590 and <= 0x08FF) or (>= 0xFB1D and <= 0xFDFF) or (>= 0xFE70 and <= 0xFEFF) or (>= 0x10800 and <= 0x10FFF) or (>= 0x1E800 and <= 0x1EFFF);

    /// <summary>
    /// Embedding levels per character: strong right-to-left letters odd, everything else even,
    /// neutrals taking the direction of matching neighbors and the paragraph direction otherwise.
    /// Numbers count as left-to-right.
    /// </summary>
    private static byte[] ResolveLevels(string text, ParagraphSpan span)
    {
        var length = span.End - span.Start;
        var classes = new BidiClass[length];
        for (var i = 0; i < length; i++)
        {
            classes[i] = char.IsLowSurrogate(text[span.Start + i]) && i > 0 ? classes[i - 1] : Classify(CodePointAt(text, span.Start + i));
        }

        var baseClass = span.RightToLeft ? BidiClass.Right : BidiClass.Left;
        var resolved = new BidiClass[length];
        var previousStrong = baseClass;
        for (var i = 0; i < length; i++)
        {
            if (classes[i] != BidiClass.Neutral)
            {
                resolved[i] = previousStrong = classes[i];
                continue;
            }

            var j = i;
            while (j < length && classes[j] == BidiClass.Neutral)
            {
                j++;
            }

            var nextStrong = j < length ? classes[j] : baseClass;
            var direction = previousStrong == nextStrong ? nextStrong : baseClass;
            for (var k = i; k < j; k++)
            {
                resolved[k] = direction;
            }

            i = j - 1;
        }

        var levels = new byte[length];
        for (var i = 0; i < length; i++)
        {
            levels[i] = span.RightToLeft
                ? (byte)(resolved[i] == BidiClass.Right ? 1 : 2)
                : (byte)(resolved[i] == BidiClass.Right ? 1 : 0);
        }

        return levels;
    }

    /// <summary>Reorders a line's glyph indices from logical to visual order (UAX 9 rule L2).</summary>
    private static int[] VisualOrder(List<int> logical, Func<int, byte> levelOf)
    {
        var order = logical.ToArray();
        if (order.Length == 0)
        {
            return order;
        }

        byte highest = 0;
        byte lowestOdd = byte.MaxValue;
        foreach (var index in order)
        {
            var level = levelOf(index);
            highest = Math.Max(highest, level);
            if ((level & 1) == 1)
            {
                lowestOdd = Math.Min(lowestOdd, level);
            }
        }

        for (var level = highest; level >= lowestOdd && level > 0; level--)
        {
            var i = 0;
            while (i < order.Length)
            {
                if (levelOf(order[i]) < level)
                {
                    i++;
                    continue;
                }

                var j = i;
                while (j < order.Length && levelOf(order[j]) >= level)
                {
                    j++;
                }

                Array.Reverse(order, i, j - i);
                i = j;
            }
        }

        return order;
    }

    // ---- Character helpers ----

    private static int CodePointAt(string text, int index)
    {
        var ch = text[index];
        if (char.IsHighSurrogate(ch) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
        {
            return char.ConvertToUtf32(ch, text[index + 1]);
        }

        return ch;
    }

    private static bool IsControlOrSpace(int cp) => cp < 0x20 || cp is 0x7F or 0x20 or 0xA0 or 0x3000 or (>= 0x2000 and <= 0x200F) or 0x2028 or 0x2029;

    /// <summary>Whitespace that hangs past the line end and may break.</summary>
    private static bool IsBreakingSpace(char ch) => ch is ' ' or '\t' || ch == 0x3000 || (ch >= 0x2000 && ch <= 0x200B && ch != 0x2007);

    /// <summary>A line break inside a paragraph: ETX (Photoshop's Shift+Enter), line separator, vertical tab.</summary>
    private static bool IsForcedBreak(char ch) => ch == 0x0003 || ch == 0x2028 || ch == 0x000B;

    private static bool IsCjk(int cp) =>
        cp is (>= 0x1100 and <= 0x11FF) or (>= 0x2E80 and <= 0x2FFF) or (>= 0x3000 and <= 0x30FF) or (>= 0x3100 and <= 0x4DBF)
        or (>= 0x4E00 and <= 0x9FFF) or (>= 0xA000 and <= 0xA4CF) or (>= 0xAC00 and <= 0xD7AF) or (>= 0xF900 and <= 0xFAFF)
        or (>= 0xFE30 and <= 0xFE4F) or (>= 0xFF00 and <= 0xFFEF) or (>= 0x20000 and <= 0x3FFFF);

    /// <summary>A break is allowed after <paramref name="i"/> (paragraph-relative) unless a No Break run spans it.</summary>
    private static bool CanBreakAfter(string text, int[] styleOf, List<LayoutStyle> styles, ParagraphSpan span, int i)
    {
        var index = span.Start + i;
        if (index + 1 >= span.End)
        {
            return true;
        }

        var ch = text[index];
        var next = text[index + 1];
        if (char.IsHighSurrogate(ch))
        {
            return false;
        }

        if (styles[styleOf[index]].NoBreak && styles[styleOf[index + 1]].NoBreak)
        {
            return false;
        }

        if (IsBreakingSpace(next))
        {
            return false;
        }

        return IsBreakingSpace(ch) || ch is '-' or '/' || ch == 0x00AD || ch == 0x2010 || ch == 0x2013
            || IsCjk(CodePointAt(text, index)) || IsCjk(CodePointAt(text, index + 1));
    }

    // ---- Horizontal text ----

    private sealed record Line(ShapedParagraph Paragraph, int Start, int End, bool FirstInParagraph, bool LastInParagraph, bool EndsAtForcedBreak);

    private sealed class HorizontalPass(TextLayoutEngine engine, TextLayerInfo info, string text, int[] styleOf, LaidOutText output)
    {
        private readonly bool _boxed = info.ShapeKind == TextShapeKind.Box && info.BoxBounds is { IsEmpty: false };

        public void Run(List<ParagraphSpan> paragraphs)
        {
            var box = info.BoxBounds ?? default;
            var lines = new List<Line>();
            foreach (var span in paragraphs)
            {
                var shaped = engine.Shape(text, styleOf, output.Styles, span, vertical: false);
                BreakLines(shaped, box, lines);
            }

            double baseline = 0;
            double previousSpaceAfter = 0;
            for (var index = 0; index < lines.Count; index++)
            {
                var line = lines[index];
                var properties = line.Paragraph.Span.Properties;
                var spaceBefore = line.FirstInParagraph ? Math.Max(0, properties?.SpaceBefore ?? 0) : 0;
                if (index == 0)
                {
                    // Point text: the first baseline sits on the origin. Box text: frame top + space
                    // before + the largest typographic ascender on the line (reference photoshop_text_layout_plan).
                    baseline = _boxed ? box.Top + spaceBefore + FirstBaselineOffset(line) : 0;
                }
                else
                {
                    baseline += Math.Max(0.01, LineLeading(line)) + spaceBefore + previousSpaceAfter;
                }

                previousSpaceAfter = line.LastInParagraph ? Math.Max(0, properties?.SpaceAfter ?? 0) : 0;
                if (_boxed && baseline - LineAscent(line) >= box.Bottom)
                {
                    // Lines below the frame are overset text: Photoshop does not draw them.
                    break;
                }

                PlaceLine(line, baseline, box);
            }
        }

        private void BreakLines(ShapedParagraph shaped, TextBounds box, List<Line> lines)
        {
            var span = shaped.Span;
            var length = span.End - span.Start;
            var properties = span.Properties;
            var startIndent = properties?.StartIndent ?? 0;
            var endIndent = properties?.EndIndent ?? 0;
            var firstIndent = properties?.FirstLineIndent ?? 0;
            var segmentStart = 0;
            var first = true;
            for (var i = 0; i <= length; i++)
            {
                if (i < length && !IsForcedBreak(text[span.Start + i]))
                {
                    continue;
                }

                var forced = i < length;
                var start = segmentStart;
                while (true)
                {
                    var end = i;
                    if (_boxed)
                    {
                        var available = box.Width - startIndent - endIndent - (first ? firstIndent : 0);
                        end = WrapEnd(shaped, start, i, available);
                    }

                    var last = end >= i;
                    lines.Add(new Line(shaped, start, end, first, last && !forced, last && forced));
                    first = false;
                    if (last)
                    {
                        break;
                    }

                    start = end;
                }

                segmentStart = i + 1;
            }
        }

        /// <summary>Greedy wrap: the end of the line that starts at <paramref name="start"/>, breaking at opportunities; trailing spaces hang.</summary>
        private int WrapEnd(ShapedParagraph shaped, int start, int limit, double available)
        {
            var span = shaped.Span;
            double width = 0;
            var lastBreak = -1;
            for (var i = start; i < limit; i++)
            {
                var ch = text[span.Start + i];
                var advance = shaped.CharAdvance[i];
                if (!IsBreakingSpace(ch) && i > start && width + advance > available + 1e-6)
                {
                    if (lastBreak >= start)
                    {
                        return lastBreak + 1;
                    }

                    // No opportunity: break before this character (an over-long word).
                    return char.IsLowSurrogate(ch) ? Math.Max(start + 1, i - 1) : i;
                }

                width += advance + shaped.CharGap[i];
                if (CanBreakAfter(text, styleOf, output.Styles, span, i))
                {
                    lastBreak = i;
                }
            }

            return limit;
        }

        private IEnumerable<int> LineChars(Line line)
        {
            if (line.End > line.Start)
            {
                for (var i = line.Start; i < line.End; i++)
                {
                    yield return i;
                }
            }
            else
            {
                // An empty line takes the format at its position (the separator's run).
                var span = line.Paragraph.Span;
                var index = Math.Clamp(span.Start + line.Start, 0, text.Length - 1);
                yield return index - span.Start;
            }
        }

        private LayoutStyle StyleAtSafe(Line line, int i)
        {
            var index = Math.Clamp(line.Paragraph.Span.Start + i, 0, styleOf.Length - 1);
            return output.Styles[styleOf[index]];
        }

        private double LineLeading(Line line)
        {
            var fraction = line.Paragraph.Span.Properties?.AutoLeadingFraction ?? 1.2;
            if (!double.IsFinite(fraction) || fraction <= 0.01 || fraction >= 10)
            {
                fraction = 1.2;
            }

            double leading = 0;
            foreach (var i in LineChars(line))
            {
                leading = Math.Max(leading, StyleAtSafe(line, i).Leading(fraction));
            }

            return leading;
        }

        private double FirstBaselineOffset(Line line)
        {
            double ascent = 0;
            foreach (var i in LineChars(line))
            {
                var style = StyleAtSafe(line, i);
                ascent = Math.Max(ascent, style.Face.TypoAscender * style.Size);
            }

            return ascent;
        }

        private double LineAscent(Line line)
        {
            double ascent = 0;
            foreach (var i in LineChars(line))
            {
                var style = StyleAtSafe(line, i);
                ascent = Math.Max(ascent, style.Face.Ascent * style.Size * style.VerticalScale);
            }

            return ascent;
        }

        private void PlaceLine(Line line, double baseline, TextBounds box)
        {
            var shaped = line.Paragraph;
            var span = shaped.Span;
            var properties = span.Properties;

            // Trailing whitespace hangs: it neither counts toward alignment nor draws.
            var visibleEnd = line.End;
            while (visibleEnd > line.Start && IsBreakingSpace(text[span.Start + visibleEnd - 1]))
            {
                visibleEnd--;
            }

            // Glyphs are sorted by character: find the line's first glyph by binary search.
            var indices = new List<int>();
            int low = 0, high = shaped.Glyphs.Length;
            while (low < high)
            {
                var mid = (low + high) / 2;
                if (shaped.Glyphs[mid].Char < line.Start)
                {
                    low = mid + 1;
                }
                else
                {
                    high = mid;
                }
            }

            for (var g = low; g < shaped.Glyphs.Length && shaped.Glyphs[g].Char < visibleEnd; g++)
            {
                indices.Add(g);
            }

            var visual = VisualOrder(indices, g => shaped.Levels[shaped.Glyphs[g].Char]);
            double width = 0;
            var spaces = 0;
            for (var k = 0; k < visual.Length; k++)
            {
                var glyph = shaped.Glyphs[visual[k]];
                width += glyph.Advance;
                if (k < visual.Length - 1 && EndsCluster(shaped, visual, k))
                {
                    width += shaped.CharGap[glyph.Char];
                }

                if (IsBreakingSpace(text[span.Start + glyph.Char]))
                {
                    spaces++;
                }
            }

            var rtl = span.RightToLeft;
            var startIndent = properties?.StartIndent ?? 0;
            var endIndent = properties?.EndIndent ?? 0;
            var firstIndent = line.FirstInParagraph ? properties?.FirstLineIndent ?? 0 : 0;
            var leftIndent = rtl ? endIndent : startIndent + firstIndent;
            var rightIndent = rtl ? startIndent + firstIndent : endIndent;
            var left = (_boxed ? box.Left : 0) + leftIndent;
            var right = (_boxed ? box.Right : 0) - rightIndent;

            var justification = properties?.Justification ?? TextJustification.Left;
            var fullJustify = _boxed && !line.EndsAtForcedBreak && spaces > 0
                && (justification == TextJustification.JustifyAll || (!line.LastInParagraph && justification is TextJustification.JustifyLastLeft or TextJustification.JustifyLastRight or TextJustification.JustifyLastCenter));
            var alignment = justification switch
            {
                TextJustification.Right or TextJustification.JustifyLastRight => TextJustification.Right,
                TextJustification.Center or TextJustification.JustifyLastCenter => TextJustification.Center,
                _ => TextJustification.Left,
            };

            // Right-to-left paragraphs align to their start edge: Left means the right side.
            if (rtl && alignment != TextJustification.Center)
            {
                alignment = alignment == TextJustification.Left ? TextJustification.Right : TextJustification.Left;
            }

            var spaceExtra = fullJustify ? Math.Max(0, (right - left - width) / spaces) : 0;
            var x = fullJustify ? left : alignment switch
            {
                TextJustification.Right => right - width,
                TextJustification.Center => ((left + right) / 2) - (width / 2),
                _ => left,
            };

            var pen = x;
            var barStyle = -1;
            double barStart = 0, barEnd = 0;
            for (var k = 0; k < visual.Length; k++)
            {
                var glyph = shaped.Glyphs[visual[k]];
                var style = output.Styles[glyph.Style];
                var em = style.Size * glyph.Scale;
                var originX = pen + glyph.OffsetX;
                var originY = baseline + glyph.OffsetY - glyph.Rise;
                engine.AddGlyph(output, glyph.Face, glyph.Id, glyph.Style, style, em, originX, originY, rotated: false);

                var decorated = style.Underline || style.Strikethrough ? glyph.Style : -1;
                if (decorated != barStyle)
                {
                    FlushBars(barStyle, barStart, barEnd, baseline);
                    barStyle = decorated;
                    barStart = pen;
                }

                pen += glyph.Advance;
                if (IsBreakingSpace(text[span.Start + glyph.Char]))
                {
                    pen += spaceExtra;
                }

                barEnd = pen;
                if (k < visual.Length - 1 && EndsCluster(shaped, visual, k))
                {
                    pen += shaped.CharGap[glyph.Char];
                }
            }

            FlushBars(barStyle, barStart, barEnd, baseline);
        }

        private static bool EndsCluster(ShapedParagraph shaped, int[] visual, int k) =>
            shaped.Glyphs[visual[k + 1]].Char != shaped.Glyphs[visual[k]].Char;

        private void FlushBars(int styleIndex, double left, double right, double baseline)
        {
            if (styleIndex < 0 || right <= left)
            {
                return;
            }

            var style = output.Styles[styleIndex];
            var size = style.Size * style.VerticalScale;
            if (style.Underline)
            {
                var center = baseline + (style.Face.UnderlinePosition * size);
                var half = Math.Max(0.5, style.Face.UnderlineThickness * size) / 2;
                output.Bars.Add(new PlacedBar(styleIndex, left, right, center - half, center + half));
            }

            if (style.Strikethrough)
            {
                var center = baseline + (style.Face.StrikeoutPosition * size);
                var half = Math.Max(0.5, style.Face.StrikeoutThickness * size) / 2;
                output.Bars.Add(new PlacedBar(styleIndex, left, right, center - half, center + half));
            }
        }
    }

    /// <summary>Adds a glyph whose outline scales by the run's em and glyph scales, with faux italic shear and an optional 90 degree clockwise turn.</summary>
    private void AddGlyph(LaidOutText output, FontFace face, ushort id, int styleIndex, LayoutStyle style, double em, double x, double y, bool rotated)
    {
        var sx = em * style.HorizontalScale;
        var sy = em * style.VerticalScale;
        var shear = style.FauxItalic ? -FauxItalicSlant * sy : 0;

        // Upright: X = sx*x + shear*y, Y = sy*y.
        double a = sx, b = 0, c = shear, d = sy;
        if (rotated)
        {
            // Turned 90 degrees clockwise: (X, Y) -> (-Y, X).
            (a, b, c, d) = (-b, a, -d, c);
        }

        output.Glyphs.Add(new PlacedGlyph(face, id, styleIndex, a, b, c, d, x, y));
    }

    // ---- Vertical text ----

    private sealed class VerticalPass(TextLayoutEngine engine, TextLayerInfo info, string text, int[] styleOf, LaidOutText output)
    {
        private readonly bool _boxed = info.ShapeKind == TextShapeKind.Box && info.BoxBounds is { IsEmpty: false };

        private sealed class Cell
        {
            public int Char;
            public int Style;
            public bool Rotated;
            public double Advance;
            public double Gap;
            public double Em;
            public double HorizontalWidth;
            public readonly List<ShapedGlyph> Glyphs = [];
        }

        private sealed class Column
        {
            public required ParagraphSpan Span;
            public readonly List<Cell> Cells = [];
            public bool FirstInParagraph;
            public bool LastInParagraph;
            public int EmptyChar;
        }

        public void Run(List<ParagraphSpan> paragraphs)
        {
            var box = info.BoxBounds ?? default;
            var columns = new List<Column>();
            foreach (var span in paragraphs)
            {
                var shaped = engine.Shape(text, styleOf, output.Styles, span, vertical: true);
                BuildColumns(shaped, box, columns);
            }

            double axis = 0;
            double previousSpaceAfter = 0;
            for (var index = 0; index < columns.Count; index++)
            {
                var column = columns[index];
                var properties = column.Span.Properties;
                var em = ColumnEm(column);
                var spaceBefore = column.FirstInParagraph ? Math.Max(0, properties?.SpaceBefore ?? 0) : 0;
                if (index == 0)
                {
                    // Point text: the first column's axis is the origin. Box text: the first column
                    // stands against the frame's right edge (reference vertical_text_layout_plan), but
                    // Photoshop's own layout box (the descriptor bounds, the union of the column em
                    // boxes) puts it a few pixels further right on the vt_box capture (153.69 for a
                    // 150 px frame); trust the stored bounds when they are plausible.
                    var right = box.Right;
                    if (info.Bounds is { IsEmpty: false } stored && stored.Right >= box.Right && stored.Right <= box.Right + em)
                    {
                        right = stored.Right;
                    }

                    axis = _boxed ? right - spaceBefore - (em / 2) : 0;
                }
                else
                {
                    axis -= Math.Max(0.01, ColumnLeading(column)) + spaceBefore + previousSpaceAfter;
                }

                previousSpaceAfter = column.LastInParagraph ? Math.Max(0, properties?.SpaceAfter ?? 0) : 0;
                if (_boxed && axis - (em / 2) < box.Left - 0.5)
                {
                    // A column whose em box leaves the frame is overset (vt_box capture: the final
                    // column of one cell is hidden).
                    break;
                }

                PlaceColumn(column, axis, box);
            }
        }

        private void BuildColumns(ShapedParagraph shaped, TextBounds box, List<Column> columns)
        {
            var span = shaped.Span;
            var cells = new List<Cell>();
            var forcedAfter = new HashSet<int>();
            Cell? current = null;
            foreach (var glyph in shaped.Glyphs)
            {
                if (current is null || current.Char != glyph.Char)
                {
                    var style = output.Styles[glyph.Style];
                    var cp = CodePointAt(text, span.Start + glyph.Char);
                    current = new Cell
                    {
                        Char = glyph.Char,
                        Style = glyph.Style,
                        Rotated = style.RotateRoman && !IsCjk(cp),
                        Em = style.Size * glyph.Scale * style.VerticalScale,
                        Gap = shaped.CharGap[glyph.Char],
                    };
                    cells.Add(current);
                }

                current.Glyphs.Add(glyph);
                current.HorizontalWidth += glyph.Advance;
            }

            foreach (var cell in cells)
            {
                var ch = text[span.Start + cell.Char];
                cell.Advance = cell.Rotated || IsBreakingSpace(ch) || IsForcedBreak(ch) ? cell.HorizontalWidth : cell.Em;
            }

            var properties = span.Properties;
            var height = box.Height - (properties?.StartIndent ?? 0) - (properties?.EndIndent ?? 0);
            var column = new Column { Span = span, FirstInParagraph = true, EmptyChar = 0 };
            double used = 0;
            foreach (var cell in cells)
            {
                if (IsForcedBreak(text[span.Start + cell.Char]))
                {
                    columns.Add(column);
                    column = new Column { Span = span, EmptyChar = cell.Char };
                    used = 0;
                    continue;
                }

                var firstIndent = column.FirstInParagraph ? properties?.FirstLineIndent ?? 0 : 0;
                if (_boxed && column.Cells.Count > 0 && !IsBreakingSpace(text[span.Start + cell.Char]) && used + cell.Advance > height - firstIndent + 1e-6)
                {
                    columns.Add(column);
                    column = new Column { Span = span, EmptyChar = cell.Char };
                    used = 0;
                }

                column.Cells.Add(cell);
                used += cell.Advance + cell.Gap;
            }

            column.LastInParagraph = true;
            columns.Add(column);
        }

        private double ColumnEm(Column column)
        {
            double em = 0;
            foreach (var cell in column.Cells)
            {
                em = Math.Max(em, cell.Em);
            }

            if (em <= 0)
            {
                var style = StyleAt(column.Span, column.EmptyChar);
                em = style.Size * style.VerticalScale;
            }

            return em;
        }

        private LayoutStyle StyleAt(ParagraphSpan span, int i) => output.Styles[styleOf[Math.Clamp(span.Start + i, 0, styleOf.Length - 1)]];

        private double ColumnLeading(Column column)
        {
            var fraction = column.Span.Properties?.AutoLeadingFraction ?? 1.2;
            if (!double.IsFinite(fraction) || fraction <= 0.01 || fraction >= 10)
            {
                fraction = 1.2;
            }

            if (column.Cells.Count == 0)
            {
                return StyleAt(column.Span, column.EmptyChar).Leading(fraction);
            }

            double leading = 0;
            foreach (var cell in column.Cells)
            {
                leading = Math.Max(leading, output.Styles[cell.Style].Leading(fraction));
            }

            return leading;
        }

        private void PlaceColumn(Column column, double axis, TextBounds box)
        {
            // Trailing whitespace hangs.
            var count = column.Cells.Count;
            while (count > 0 && IsBreakingSpace(text[column.Span.Start + column.Cells[count - 1].Char]))
            {
                count--;
            }

            double length = 0;
            for (var k = 0; k < count; k++)
            {
                length += column.Cells[k].Advance + (k < count - 1 ? column.Cells[k].Gap : 0);
            }

            var properties = column.Span.Properties;
            var startIndent = (properties?.StartIndent ?? 0) + (column.FirstInParagraph ? properties?.FirstLineIndent ?? 0 : 0);
            var endIndent = properties?.EndIndent ?? 0;
            var top = (_boxed ? box.Top : 0) + startIndent;
            var bottom = (_boxed ? box.Bottom : 0) - endIndent;
            var y = (properties?.Justification ?? TextJustification.Left) switch
            {
                TextJustification.Right or TextJustification.JustifyLastRight => bottom - length,
                TextJustification.Center or TextJustification.JustifyLastCenter => ((top + bottom) / 2) - (length / 2),
                _ => top,
            };

            for (var k = 0; k < count; k++)
            {
                var cell = column.Cells[k];
                var style = output.Styles[cell.Style];
                var face = cell.Glyphs[0].Face;
                var em = style.Size * cell.Glyphs[0].Scale;
                var ascent = face.Ascent * em * style.VerticalScale;
                var descent = face.Descent * em * style.VerticalScale;
                double pen = 0;
                foreach (var glyph in cell.Glyphs)
                {
                    if (cell.Rotated)
                    {
                        // Along the column, the ascent+descent box centered on the axis (reference vertical_text_layout_plan).
                        var originX = axis - ((ascent - descent) / 2) - glyph.OffsetY + glyph.Rise;
                        var originY = y + pen + glyph.OffsetX;
                        engine.AddGlyph(output, glyph.Face, glyph.Id, cell.Style, style, em, originX, originY, rotated: true);
                    }
                    else
                    {
                        // Upright in an em cell: centered on the axis, the ascent+descent box centered vertically.
                        var originX = axis - (cell.HorizontalWidth / 2) + pen + glyph.OffsetX;
                        var originY = y + ((cell.Em - (ascent + descent)) / 2) + ascent + glyph.OffsetY - glyph.Rise;
                        engine.AddGlyph(output, glyph.Face, glyph.Id, cell.Style, style, em, originX, originY, rotated: false);
                    }

                    pen += glyph.Advance;
                }

                y += cell.Advance + (k < count - 1 ? cell.Gap : 0);
            }
        }
    }
}

/// <summary>Size limits shared with the core reader (<c>EngineStyles.MaxTextSize</c>).</summary>
internal static class EngineStyleLimits
{
    public const double MaxSize = 8192;
}
