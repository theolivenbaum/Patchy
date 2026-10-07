using XRay.Psd.Rendering;
using XRay.Psd.Text.Tests.Support;

namespace XRay.Psd.Text.Tests;

/// <summary>Layout rules on synthetic text models, rendered with the bundled Liberation Sans.</summary>
public sealed class LayoutTests : IDisposable
{
    private readonly TextLayerRenderer _renderer = new(TestFonts.Settings());

    public void Dispose() => _renderer.Dispose();

    [Theory]
    [InlineData(TextJustification.Left)]
    [InlineData(TextJustification.Center)]
    [InlineData(TextJustification.Right)]
    public void Point_text_aligns_around_the_anchor(TextJustification justification)
    {
        var ink = Ink(Model("HHHH", justification: justification, anchor: (100, 50)));

        // Liberation Sans H: side bearings of about 1.9 px at 24 px.
        switch (justification)
        {
            case TextJustification.Left:
                Assert.InRange(ink.Left, 101, 103);
                break;
            case TextJustification.Right:
                Assert.InRange(ink.Right, 97, 99);
                break;
            default:
                Assert.InRange((ink.Left + ink.Right) / 2.0, 99, 101);
                break;
        }

        // The first baseline sits on the anchor: H stands on it.
        Assert.InRange(ink.Bottom, 50, 51);
    }

    [Fact]
    public void Lines_advance_by_the_largest_leading_of_the_entered_line()
    {
        // Line 1 at 24 px, line 2 mixes 24 px and 48 px: the second baseline drops 1.2 x 48.
        var runs = new[]
        {
            Run(0, 5, 24),
            Run(5, 2, 24),
            Run(7, 2, 48),
        };
        var info = Model("abcd\nefgh", runs: runs);

        var baselines = _renderer.Layout(info).Glyphs.Select(g => g.F).Distinct().Order().ToList();

        Assert.Equal([0, 57.6], baselines.Select(b => Math.Round(b, 6)));
    }

    [Fact]
    public void Paragraph_spacing_adds_to_the_baseline_advance()
    {
        var paragraphs = new[]
        {
            new TextParagraph(0, 3, "ab", TextJustification.Left) { SpaceAfter = 5 },
            new TextParagraph(3, 3, "cd", TextJustification.Left) { SpaceBefore = 7 },
        };
        var info = Model("ab\ncd", paragraphs: paragraphs);

        var baselines = _renderer.Layout(info).Glyphs.Select(g => g.F).Distinct().Order().ToList();

        Assert.Equal(28.8 + 5 + 7, baselines[1], 1e-9);
    }

    [Fact]
    public void Faux_bold_widens_glyphs_and_advances_by_three_hundredths_em()
    {
        var plain = Ink(Model("HHHH"));
        var bold = Ink(Model("HHHH", runs: [Run(0, 5, 24) with { FauxBold = true }]));

        // Three advances grow by 0.72 px and the outline by 0.36 px per side.
        Assert.InRange(bold.Width - plain.Width, 2, 4);
    }

    [Fact]
    public void Faux_italic_shears_to_the_right()
    {
        var plain = Ink(Model("HHHH"));
        var italic = Ink(Model("HHHH", runs: [Run(0, 5, 24) with { FauxItalic = true }]));

        // tan 12 degrees over a 16.5 px cap height.
        Assert.InRange(italic.Right - plain.Right, 2.5, 5);
        Assert.InRange(italic.Left - plain.Left, -1, 1);
    }

    [Fact]
    public void Box_text_wraps_words_and_breaks_overlong_ones()
    {
        var box = new TextBounds(0, 0, 60, 300);
        var words = _renderer.Layout(Model("aa bb cc dd ee ff", box: box));
        var longWord = _renderer.Layout(Model("WWWWWWWWWW", box: box));

        Assert.True(words.Glyphs.Select(g => g.F).Distinct().Count() >= 2);
        Assert.All(words.Glyphs, g => Assert.InRange(g.E, 0, 60));
        Assert.True(longWord.Glyphs.Select(g => g.F).Distinct().Count() >= 3);
    }

    [Fact]
    public void Justified_box_lines_fill_the_frame_except_the_last()
    {
        var box = new TextBounds(10, 0, 160, 300);
        var info = Model("aaa bbb ccc ddd eee fff ggg hhh", box: box, justification: TextJustification.JustifyLastLeft, anchor: (0, 0));

        var raster = _renderer.Render(info)!;
        var rows = RowInk(raster);
        var firstLine = rows.First();
        var lastLine = rows.Last();

        Assert.InRange(firstLine.Right, 157, 161);
        Assert.True(lastLine.Right < 150);
    }

    [Fact]
    public void Underline_and_all_caps_change_the_ink()
    {
        // No ascenders or descenders in "ace": the underline and the capitals stand out.
        var plain = Ink(Model("ace"));
        var underlined = Ink(Model("ace", runs: [Run(0, 4, 24) with { Underline = true }]));
        var caps = Ink(Model("ace", runs: [Run(0, 4, 24) with { Caps = TextCaps.AllCaps }]));

        Assert.True(underlined.Bottom > plain.Bottom);
        Assert.True(caps.Top < plain.Top);
    }

    [Fact]
    public void Superscript_is_smaller_and_raised()
    {
        var plain = Ink(Model("H"));
        var super = Ink(Model("H", runs: [Run(0, 2, 24) with { BaselinePosition = TextBaselinePosition.Superscript }]));

        Assert.True(super.Height < plain.Height * 0.7);
        Assert.True(super.Bottom < plain.Bottom - 5);
    }

    [Fact]
    public void Anti_alias_none_draws_hard_edges()
    {
        var info = Model("Hg", antiAlias: TextAntiAlias.None);

        var raster = _renderer.Render(info)!;

        Assert.All(Alphas(raster), a => Assert.True(a is 0 or 255));
    }

    [Fact]
    public void Rotation_in_the_transform_turns_the_text()
    {
        var info = Model("HHHHHH", transform: [0, 1, -1, 0, 50, 20]);

        var ink = Ink(info);

        Assert.True(ink.Height > ink.Width * 3, ink.ToString());
        Assert.InRange(ink.Top, 19, 23);
    }

    [Fact]
    public void Vertical_text_stacks_upright_or_rotated_cells()
    {
        var upright = Model("HHH", orientation: TextOrientation.Vertical, runs: [Run(0, 4, 24) with { BaselineDirection = 1 }], anchor: (100, 10));
        var rotated = Model("HHH", orientation: TextOrientation.Vertical, runs: [Run(0, 4, 24) with { BaselineDirection = 2 }], anchor: (100, 10));

        var uprightInk = Ink(upright);
        var rotatedInk = Ink(rotated);

        // Upright: three em cells (72 px) centered on x 100; rotated: three H advances (52 px) along the column.
        Assert.InRange((uprightInk.Left + uprightInk.Right) / 2.0, 99, 101);
        Assert.InRange(uprightInk.Height, 60, 72);
        Assert.InRange(rotatedInk.Height, 45, 53);
        Assert.True(rotatedInk.Width < uprightInk.Height);
    }

    [Fact]
    public void Missing_fonts_fall_back_and_are_reported()
    {
        var info = Model("Hi", runs: [Run(0, 3, 24) with { FontName = "NoSuchFont-Bold" }]);

        Assert.Equal(["NoSuchFont-Bold"], _renderer.FindMissingFonts(info));
        Assert.NotNull(_renderer.Render(info));
    }

    [Fact]
    public void Degenerate_models_do_not_throw()
    {
        Assert.Null(_renderer.Render(Model(string.Empty)));
        Assert.NotNull(_renderer.Render(Model("Hi", runs: [Run(0, 3, double.NaN)])));
        Assert.Null(_renderer.Render(Model("Hi", transform: [0, 0, 0, 0, 0, 0]), new PsdRect(0, 0, 1, 1)));
        Assert.NotNull(_renderer.Render(Model("Hi", runs: [Run(5, 90, 24)])));
        Assert.Null(_renderer.Render(Model("Hi", transform: [1e9, 0, 0, 1e9, 0, 0])));
    }

    [Fact]
    public void Hostile_models_lay_out_in_linear_time()
    {
        // 20000 one-character lines and 2000 runs that each claim the whole text.
        var text = new string('a', 20000);
        var runs = Enumerable.Range(0, 2000).Select(_ => Run(0, 20001, 24)).ToArray();
        var info = Model(text, runs: runs, box: new TextBounds(0, 0, 1, 1e6));
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var layout = _renderer.Layout(info);

        Assert.Equal(20000, layout.Glyphs.Count);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), watch.Elapsed.ToString());
    }

    [Fact]
    public void Type_on_a_path_is_not_drawn()
    {
        var info = Model("On a path");
        var onPath = new TextLayerInfo { Text = info.Text, RawText = info.RawText, StyleRuns = info.StyleRuns, Paragraphs = info.Paragraphs, Transform = info.Transform, ShapeKind = TextShapeKind.Other, ShapeType = 2 };

        Assert.Null(_renderer.Render(onPath));
    }

    // ---- helpers ----

    internal static TextStyleRun Run(int start, int length, double size) =>
        new(start, length, string.Empty, "ArialMT", size, PsdColor.Black, false, false, 0, null);

    internal static TextLayerInfo Model(
        string text,
        TextJustification justification = TextJustification.Left,
        (double X, double Y)? anchor = null,
        TextStyleRun[]? runs = null,
        TextParagraph[]? paragraphs = null,
        TextBounds? box = null,
        double[]? transform = null,
        TextOrientation orientation = TextOrientation.Horizontal,
        TextAntiAlias antiAlias = TextAntiAlias.Sharp)
    {
        var raw = text.Replace('\n', '\r') + "\r";
        if (text.Length == 0)
        {
            raw = string.Empty;
        }

        paragraphs ??= [new TextParagraph(0, raw.Length, text, justification)];
        return new TextLayerInfo
        {
            Text = text,
            RawText = raw,
            Transform = transform ?? [1, 0, 0, 1, anchor?.X ?? 20, anchor?.Y ?? 40],
            StyleRuns = runs ?? [Run(0, raw.Length, 24)],
            Paragraphs = paragraphs,
            ShapeKind = box is null ? TextShapeKind.Point : TextShapeKind.Box,
            IsParagraphText = box is not null,
            BoxBounds = box,
            Orientation = orientation,
            AntiAlias = antiAlias,
        };
    }

    private PsdRect Ink(TextLayerInfo info)
    {
        var raster = _renderer.Render(info);
        Assert.NotNull(raster);
        var ink = default(PsdRect);
        var image = raster.Image;
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                if (image.Pixels[(((y * image.Width) + x) * 4) + 3] >= 128)
                {
                    ink = ink.Union(new PsdRect(raster.Left + x, raster.Top + y, raster.Left + x + 1, raster.Top + y + 1));
                }
            }
        }

        return ink;
    }

    /// <summary>Ink extents of each band of rows separated by empty rows (one per line of text).</summary>
    private static List<PsdRect> RowInk(TextLayerRaster raster)
    {
        var bands = new List<PsdRect>();
        var image = raster.Image;
        var current = default(PsdRect);
        for (var y = 0; y < image.Height; y++)
        {
            var row = default(PsdRect);
            for (var x = 0; x < image.Width; x++)
            {
                if (image.Pixels[(((y * image.Width) + x) * 4) + 3] >= 128)
                {
                    row = row.Union(new PsdRect(raster.Left + x, raster.Top + y, raster.Left + x + 1, raster.Top + y + 1));
                }
            }

            if (row.IsEmpty)
            {
                if (!current.IsEmpty)
                {
                    bands.Add(current);
                    current = default;
                }
            }
            else
            {
                current = current.Union(row);
            }
        }

        if (!current.IsEmpty)
        {
            bands.Add(current);
        }

        return bands;
    }

    private static IEnumerable<byte> Alphas(TextLayerRaster raster)
    {
        for (var i = 3; i < raster.Image.Pixels.Length; i += 4)
        {
            yield return raster.Image.Pixels[i];
        }
    }
}
