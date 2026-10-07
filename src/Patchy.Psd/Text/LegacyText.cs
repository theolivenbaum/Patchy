using Patchy.Psd.IO;

namespace Patchy.Psd.Text;

/// <summary>
/// Reader for Photoshop 5.x type records (<c>tySh</c>): a fixed binary layout
/// with no EngineData. Recovers the text, faces, style runs (font, size,
/// tracking, leading, kerning, baseline shift), the layer color, per-line
/// alignment and anti-aliasing. Layout, units and the validation and retry
/// rules follow <c>.reference/src/psd/psd_text_legacy.cpp</c> and
/// <c>.reference/docs/psd-legacy-text.md</c>.
/// </summary>
internal static class LegacyText
{
    private const ushort Version = 1;
    private const int StyleRecordBytes = 26;
    private const int ColorBytes = 10;
    private const char CarriageReturn = '\r';

    private sealed record Face(ushort Mark, string PostScriptName, string Family, string Style);

    private sealed record Style(ushort Mark, ushort FaceMark, double Size, double TrackingEm, double KerningEm, double Leading, double BaselineShift, bool AutoKern);

    private sealed record Line(ushort Orientation, short Alignment, List<(char Unit, ushort Mark)> Units);

    private sealed class Record
    {
        public double[] Transform { get; init; } = [1, 0, 0, 1, 0, 0];

        public List<Face> Faces { get; } = [];

        public List<Style> Styles { get; set; } = [];

        public List<Line> Lines { get; set; } = [];

        public PsdColor Color { get; set; } = PsdColor.Black;

        public byte AntiAlias { get; set; }
    }

    public static TextLayerInfo? Parse(ReadOnlyMemory<byte> payload)
    {
        var record = ParseRecord(payload);
        if (record is null || record.Styles.Count == 0 || record.Faces.Count == 0)
        {
            return null;
        }

        // Flatten the lines into one unit stream. A non-final line without its '\r' gets a
        // synthetic separator in the line's last style, and the last line's '\r' is dropped,
        // so run and paragraph indices match the text.
        var units = new List<(char Unit, ushort Mark)>();
        var lineSpans = new List<(int Start, int Length, short Alignment)>();
        var vertical = false;
        for (var i = 0; i < record.Lines.Count; i++)
        {
            var line = record.Lines[i];
            vertical |= line.Orientation != 0;
            var start = units.Count;
            units.AddRange(line.Units);
            var last = i + 1 == record.Lines.Count;
            if (!last && (line.Units.Count == 0 || line.Units[^1].Unit != CarriageReturn))
            {
                var mark = line.Units.Count == 0 ? record.Styles[0].Mark : line.Units[^1].Mark;
                units.Add((CarriageReturn, mark));
            }

            if (last && units.Count > 0 && units[^1].Unit == CarriageReturn)
            {
                units.RemoveAt(units.Count - 1);
            }

            lineSpans.Add((start, units.Count - start, line.Alignment));
        }

        if (units.Count == 0)
        {
            return null;
        }

        var raw = new string(units.Select(u => u.Unit).ToArray());
        var runs = new List<TextStyleRun>();
        var runStart = 0;
        while (runStart < units.Count)
        {
            var mark = units[runStart].Mark;
            var runEnd = runStart + 1;
            while (runEnd < units.Count && units[runEnd].Mark == mark)
            {
                runEnd++;
            }

            var style = record.Styles.FirstOrDefault(s => s.Mark == mark) ?? record.Styles[0];
            var face = record.Faces.FirstOrDefault(f => f.Mark == style.FaceMark) ?? record.Faces[0];

            // PS 5 tracking and kerning are em fractions; the run model uses thousandths of an em.
            runs.Add(new TextStyleRun(
                runStart,
                runEnd - runStart,
                TextLayerInfo.NormalizeText(raw[runStart..runEnd]),
                face.PostScriptName.Length > 0 ? face.PostScriptName : null,
                Math.Clamp(style.Size, 1, EngineStyles.MaxTextSize),
                record.Color,
                FauxBold: false,
                FauxItalic: false,
                Math.Round(style.TrackingEm * 1000),
                style.Leading > 0 ? style.Leading : null)
            {
                AutoLeading = style.Leading <= 0,
                AutoKerning = style.AutoKern,
                Kerning = Math.Round(style.KerningEm * 1000),
                BaselineShift = style.BaselineShift,
                FontFamily = face.Family.Length > 0 ? face.Family : null,
                FontStyleName = face.Style.Length > 0 ? face.Style : null,
            });
            runStart = runEnd;
        }

        var paragraphs = new List<TextParagraph>();
        foreach (var (start, length, alignment) in lineSpans)
        {
            if (length <= 0)
            {
                continue;
            }

            var justification = alignment switch
            {
                1 => TextJustification.Center,
                -1 => TextJustification.Right,
                _ => TextJustification.Left,
            };
            paragraphs.Add(new TextParagraph(start, length, TextLayerInfo.NormalizeText(raw.Substring(start, length)), justification));
        }

        // Fonts the runs use first, then the record's other faces.
        var fonts = TextLayerInfo.DistinctFonts(runs).ToList();
        foreach (var face in record.Faces)
        {
            if (face.PostScriptName.Length > 0 && !fonts.Contains(face.PostScriptName))
            {
                fonts.Add(face.PostScriptName);
            }
        }

        return new TextLayerInfo
        {
            Text = TextLayerInfo.NormalizeText(raw),
            RawText = raw,
            Transform = record.Transform,
            Orientation = vertical ? TextOrientation.Vertical : TextOrientation.Horizontal,
            StyleRuns = runs,
            Paragraphs = paragraphs,
            Fonts = fonts,

            // Byte 1 reads as Photoshop 2026's Sharp (legacy_type_tool_anti_alias in the reference).
            AntiAlias = record.AntiAlias == 0 ? TextAntiAlias.None : TextAntiAlias.Sharp,
            Origin = TextDataOrigin.LegacyTypeTool,
        };
    }

    private static Record? ParseRecord(ReadOnlyMemory<byte> payload)
    {
        try
        {
            var reader = new BigEndianReader(payload);
            if (reader.ReadUInt16() != Version)
            {
                return null;
            }

            var transform = TextLayerInfo.ReadTransform(reader);
            if (!transform.All(double.IsFinite))
            {
                return null;
            }

            var record = new Record { Transform = transform };
            _ = reader.ReadUInt16(); // font info version (6)
            var faceCount = reader.ReadUInt16();
            if (faceCount == 0 || faceCount * 15L > reader.Remaining)
            {
                return null;
            }

            for (var i = 0; i < faceCount; i++)
            {
                var mark = reader.ReadUInt16();
                _ = reader.ReadUInt32(); // font type
                var postScriptName = reader.ReadPascalString(1);
                var family = reader.ReadPascalString(1);
                var style = reader.ReadPascalString(1);
                _ = reader.ReadUInt16(); // script
                var axes = reader.ReadUInt32();
                if (axes > (uint)(reader.Remaining / 4))
                {
                    return null;
                }

                reader.Skip(axes * 4L); // design vector
                record.Faces.Add(new Face(mark, postScriptName, family, style));
            }

            // Adobe's specification puts a u16 version before the style count, but the PS 5.x
            // files seen so far go straight from the last face to the count. Read count-first
            // with known face marks; retry with a version word skipped; finally accept the
            // count-first layout with unknown face marks (they fall back to the first face).
            var stylesAt = reader.Position;
            if (TryReadStylesTextAndColor(reader, strict: true, record))
            {
                return record;
            }

            if (payload.Length - stylesAt >= 2)
            {
                reader.Position = stylesAt + 2;
                if (TryReadStylesTextAndColor(reader, strict: true, record))
                {
                    return record;
                }
            }

            reader.Position = stylesAt;
            return TryReadStylesTextAndColor(reader, strict: false, record) ? record : null;
        }
        catch (PsdFormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the style records, the text section and the color from the style count on.
    /// False when the bytes do not describe a sane record from here; <paramref name="strict"/>
    /// also requires every style to name a known face, the check that tells the two
    /// style-section layouts apart.
    /// </summary>
    private static bool TryReadStylesTextAndColor(BigEndianReader reader, bool strict, Record record)
    {
        try
        {
            var styleCount = reader.ReadUInt16();
            if (styleCount == 0 || styleCount * (long)StyleRecordBytes > reader.Remaining)
            {
                return false;
            }

            var styles = new List<Style>(styleCount);
            for (var i = 0; i < styleCount; i++)
            {
                var mark = reader.ReadUInt16();
                var faceMark = reader.ReadUInt16();
                var size = reader.ReadFixed16();
                var tracking = reader.ReadFixed16();
                var kerning = reader.ReadFixed16();
                var leading = reader.ReadFixed16();
                var baselineShift = reader.ReadFixed16();
                var autoKern = reader.ReadByte() != 0;
                _ = reader.ReadByte(); // rotate (vertical type)
                if (size <= 0 || size > EngineStyles.MaxTextSize || Math.Abs(tracking) > 10 || leading < 0 || leading > EngineStyles.MaxTextSize * 4)
                {
                    return false;
                }

                if (strict && !record.Faces.Any(f => f.Mark == faceMark))
                {
                    return false;
                }

                styles.Add(new Style(mark, faceMark, size, tracking, kerning, leading, baselineShift, autoKern));
            }

            _ = reader.ReadUInt16(); // text type: 0, point text (the only kind PS 5 had)
            _ = reader.ReadUInt32(); // scaling factor (16.16)
            var characterCount = reader.ReadUInt32();
            reader.Skip(16); // horizontal and vertical placement, selection start and end
            if (characterCount > (uint)(reader.Remaining / 4))
            {
                return false;
            }

            var lineCount = reader.ReadUInt16();
            var lines = new List<Line>(lineCount);
            long unitsSeen = 0;
            for (var i = 0; i < lineCount; i++)
            {
                var count = reader.ReadUInt32();
                var orientation = reader.ReadUInt16();
                var alignment = reader.ReadInt16();
                if (count > (uint)(reader.Remaining / 4) || unitsSeen + count > characterCount)
                {
                    return false;
                }

                var units = new List<(char, ushort)>((int)count);
                for (var u = 0u; u < count; u++)
                {
                    var unit = (char)reader.ReadUInt16();
                    units.Add((unit, reader.ReadUInt16()));
                }

                unitsSeen += count;
                lines.Add(new Line(orientation, alignment, units));
            }

            if (reader.Remaining < ColorBytes + 1)
            {
                return false;
            }

            record.Color = ReadColor(reader);
            record.AntiAlias = reader.ReadByte();
            record.Styles = styles;
            record.Lines = lines;
            return true;
        }
        catch (PsdFormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// Photoshop's 10-byte color: u16 space and four u16 components. 0 RGB, 1 HSB
    /// (hue over 0..65535 for 0..360 degrees), 2 CMYK stored inverted (65535 is no
    /// ink), 8 grayscale with the level on 0..10000. Mirrors
    /// <c>read_legacy_effect_color</c> in .reference/src/psd/psd_layer_styles.cpp,
    /// with the plain inverse-ink CMYK conversion.
    /// </summary>
    internal static PsdColor ReadColor(BigEndianReader reader)
    {
        var space = reader.ReadUInt16();
        Span<ushort> c = [reader.ReadUInt16(), reader.ReadUInt16(), reader.ReadUInt16(), reader.ReadUInt16()];
        static byte Level(double value) => (byte)Math.Clamp(Math.Round(value * 255), 0, 255);
        switch (space)
        {
            case 1:
                {
                    var hue = c[0] * 360.0 / 65535.0;
                    var saturation = c[1] / 65535.0;
                    var brightness = c[2] / 65535.0;
                    var position = (hue / 60.0) % 6.0;
                    var sector = (int)Math.Floor(position);
                    var fraction = position - sector;
                    var p = brightness * (1 - saturation);
                    var q = brightness * (1 - (saturation * fraction));
                    var t = brightness * (1 - (saturation * (1 - fraction)));
                    var (r, g, b) = sector switch
                    {
                        1 => (q, brightness, p),
                        2 => (p, brightness, t),
                        3 => (p, q, brightness),
                        4 => (t, p, brightness),
                        5 => (brightness, p, q),
                        _ => (brightness, t, p),
                    };
                    return new PsdColor(Level(r), Level(g), Level(b));
                }

            case 2:
                {
                    // Stored inverted: the component is 1 - ink, so RGB = component x (1 - black ink).
                    var k = c[3] / 65535.0;
                    return new PsdColor(Level(c[0] / 65535.0 * k), Level(c[1] / 65535.0 * k), Level(c[2] / 65535.0 * k));
                }

            case 8:
                {
                    var level = (byte)Math.Clamp(Math.Round(c[0] * 255.0 / 10000.0), 0, 255);
                    return new PsdColor(level, level, level);
                }

            default:
                return new PsdColor((byte)(c[0] / 257), (byte)(c[1] / 257), (byte)(c[2] / 257));
        }
    }
}
