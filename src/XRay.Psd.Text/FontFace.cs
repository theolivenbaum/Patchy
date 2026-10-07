using System.Buffers.Binary;
using HarfBuzzSharp;
using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace XRay.Psd.Text;

/// <summary>
/// One typeface prepared for layout: a HarfBuzz font at design units, the metrics the
/// Photoshop model needs (OS/2 typographic ascender, hhea ascent and descent, underline
/// and strikeout), and a cache of unit-size glyph outlines.
/// </summary>
internal sealed class FontFace : IDisposable
{
    private readonly Blob _blob;
    private readonly Face _face;
    private readonly Font _font;
    private readonly SKFont _outlineFont;
    private readonly Dictionary<ushort, SKPath?> _paths = [];
    private readonly Dictionary<int, bool> _coverage = [];

    public FontFace(SKTypeface typeface)
    {
        Typeface = typeface;
        using (var stream = typeface.OpenStream(out var index))
        {
            _blob = stream.ToHarfBuzzBlob();
            _face = new Face(_blob, index);
        }

        UnitsPerEm = Math.Max(16, _face.UnitsPerEm);
        _font = new Font(_face);
        _font.SetFunctionsOpenType();
        _font.SetScale(UnitsPerEm, UnitsPerEm);

        // Outlines at OutlineSize px per em (FreeType's 26.6 outlines lose precision at small
        // sizes); the renderer scales them per glyph. No hinting: Photoshop never hints type
        // (.reference/docs/text-render-calibration.md, "Text renders UNHINTED").
        _outlineFont = new SKFont(typeface, OutlineSize) { Hinting = SKFontHinting.None, Subpixel = true, LinearMetrics = true };
        var metrics = default(SKFontMetrics);
        using (var big = new SKFont(typeface, UnitsPerEm) { Hinting = SKFontHinting.None, LinearMetrics = true })
        {
            big.GetFontMetrics(out metrics);
        }

        Ascent = Positive(-metrics.Ascent / UnitsPerEm, 0.8);
        Descent = Positive(metrics.Descent / UnitsPerEm, 0.2);
        UnderlinePosition = (metrics.UnderlinePosition ?? (UnitsPerEm * 0.1f)) / UnitsPerEm;
        UnderlineThickness = Positive((metrics.UnderlineThickness ?? (UnitsPerEm * 0.05f)) / UnitsPerEm, 0.05);
        StrikeoutPosition = (metrics.StrikeoutPosition ?? (-UnitsPerEm * 0.3f)) / UnitsPerEm;
        StrikeoutThickness = Positive((metrics.StrikeoutThickness ?? (UnitsPerEm * 0.05f)) / UnitsPerEm, 0.05);
        TypoAscender = ReadTypoAscender(typeface, UnitsPerEm) ?? Ascent;
    }

    /// <summary>The em size glyph outlines are cached at.</summary>
    public const float OutlineSize = 512f;

    public SKTypeface Typeface { get; }

    public int UnitsPerEm { get; }

    /// <summary>OS/2 sTypoAscender per em: Photoshop's first-baseline offset in box text.</summary>
    public double TypoAscender { get; }

    /// <summary>Ascent per em (positive, Skia's hhea-based value).</summary>
    public double Ascent { get; }

    /// <summary>Descent per em (positive).</summary>
    public double Descent { get; }

    /// <summary>Underline offset below the baseline per em (positive is down).</summary>
    public double UnderlinePosition { get; }

    public double UnderlineThickness { get; }

    /// <summary>Strikeout offset per em (negative is above the baseline).</summary>
    public double StrikeoutPosition { get; }

    public double StrikeoutThickness { get; }

    /// <summary>Whether the face has a glyph for the code point.</summary>
    public bool Covers(int codePoint)
    {
        lock (_coverage)
        {
            if (!_coverage.TryGetValue(codePoint, out var covered))
            {
                covered = _font.TryGetGlyph((uint)codePoint, out var glyph) && glyph != 0;
                _coverage[codePoint] = covered;
            }

            return covered;
        }
    }

    /// <summary>Shapes <paramref name="length"/> UTF-16 units of <paramref name="text"/> at <paramref name="start"/> (the rest is context). Positions are in design units.</summary>
    public (GlyphInfo[] Infos, GlyphPosition[] Positions) Shape(string text, int start, int length, bool rightToLeft, IReadOnlyList<Feature> features)
    {
        using var buffer = new HarfBuzzSharp.Buffer();
        buffer.AddUtf16(text, start, length);
        buffer.GuessSegmentProperties();
        buffer.Direction = rightToLeft ? Direction.RightToLeft : Direction.LeftToRight;
        buffer.ClusterLevel = ClusterLevel.MonotoneCharacters;
        lock (_font)
        {
            _font.Shape(buffer, [.. features]);
        }

        return (buffer.GlyphInfos, buffer.GlyphPositions);
    }

    /// <summary>The glyph outline at <see cref="OutlineSize"/> px per em (y down, baseline at 0); null for empty glyphs.</summary>
    public SKPath? GlyphPath(ushort glyph)
    {
        lock (_paths)
        {
            if (!_paths.TryGetValue(glyph, out var path))
            {
                path = _outlineFont.GetGlyphPath(glyph);
                if (path is { IsEmpty: true })
                {
                    path.Dispose();
                    path = null;
                }

                _paths[glyph] = path;
            }

            return path;
        }
    }

    public void Dispose()
    {
        foreach (var path in _paths.Values)
        {
            path?.Dispose();
        }

        _paths.Clear();
        _outlineFont.Dispose();
        _font.Dispose();
        _face.Dispose();
        _blob.Dispose();
    }

    private static double Positive(double value, double fallback) => double.IsFinite(value) && value > 0 ? value : fallback;

    private static double? ReadTypoAscender(SKTypeface typeface, int unitsPerEm)
    {
        const uint os2 = ('O' << 24) | ('S' << 16) | ('/' << 8) | '2';
        if (!typeface.TryGetTableData(os2, out var table) || table is null || table.Length < 70)
        {
            return null;
        }

        var ascender = BinaryPrimitives.ReadInt16BigEndian(table.AsSpan(68, 2));
        return ascender > 0 ? (double)ascender / unitsPerEm : null;
    }
}
