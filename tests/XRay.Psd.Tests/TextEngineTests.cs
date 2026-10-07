using System.Text;
using XRay.Psd.Layers;
using XRay.Psd.Tests.Support;
using XRay.Psd.Text;

namespace XRay.Psd.Tests;

/// <summary>
/// The text model beyond plain extraction: the document-level Txt2 block, the
/// gap filling it feeds, Photoshop 5 <c>tySh</c> records, and the richer TySh
/// fields (warp, bounds, paragraph and character properties).
/// </summary>
public sealed class TextEngineTests
{
    // ---- Txt2 on committed fixtures ----

    [Fact]
    public void Txt2_objects_are_addressed_by_each_layers_text_index()
    {
        var document = Fixtures.Load("qual_rca_pinout.psd");
        var engine = document.TextEngine;

        Assert.NotNull(engine);
        Assert.Equal(13, engine.FormatVersion); // an older Photoshop than the 2026 captures (14)
        Assert.Equal(6, engine.Objects.Count);
        Assert.Contains(engine.Fonts, f => f.PostScriptName == "ArialMT" && f.FontType == 1);
        foreach (var layer in document.Layers.Where(l => l.Kind == PsdLayerKind.Text))
        {
            var info = layer.Text!;
            Assert.NotNull(info.TextIndex);
            Assert.Same(engine.GetObject(info.TextIndex.Value), info.TextEngineObject);
            Assert.Equal(info.Text, info.TextEngineObject!.Text);
            Assert.EndsWith("\r", info.TextEngineObject.RawText, StringComparison.Ordinal);
            Assert.Equal(TextAntiAlias.Strong, info.AntiAlias);
        }

        var digits = document.Layers.Single(l => l.Name == "12345678910").Text!.TextEngineObject!;
        Assert.Equal(["ArialMT"], digits.Fonts);
        Assert.Equal(new PsdColor(255, 255, 255), digits.StyleRuns[0].FillColor);
        Assert.Equal(new PsdColor(0x8E, 0x8E, 0x8E), digits.StyleRuns[1].FillColor);
        Assert.All(digits.StyleRuns, r => Assert.Equal(75, r.FontSize));
    }

    [Fact]
    public void Txt2_style_runs_carry_tracking_and_point_frames()
    {
        // Two layers, tracking 0 and 200 (the value .reference/docs/txt2.md pins for this capture).
        var document = Fixtures.Load("photoshop-text-tracking.psd");
        var engine = document.TextEngine!;

        Assert.Equal(14, engine.FormatVersion);
        Assert.Equal(2, engine.Objects.Count);
        Assert.Equal(200, engine.Objects[0].StyleRuns.Single().Tracking);
        Assert.Equal(0, engine.Objects[1].StyleRuns.Single().Tracking);
        Assert.All(engine.Objects, o =>
        {
            var run = o.StyleRuns.Single();
            Assert.Equal("ArialMT", run.FontName);
            Assert.Equal(24, run.FontSize);
            Assert.True(run.AutoLeading);
            Assert.Null(run.Leading);
            Assert.Equal(11, run.Length);
            Assert.Equal(TextShapeKind.Point, o.Frame!.ShapeKind);
        });
        Assert.Equal([1, 0], engine.Objects.Select(o => o.FrameIndex!.Value));

        var tracked = document.Layers.Single(l => l.Text?.TextIndex == 0).Text!;
        Assert.Equal(200, tracked.StyleRuns.Single().Tracking);
    }

    [Fact]
    public void Txt2_and_TySh_agree_on_every_text_fixture()
    {
        foreach (var name in Fixtures.AllDocuments().Where(n => n.StartsWith("photoshop-text-", StringComparison.Ordinal) || n is "qual_rca_pinout.psd" or "photoshop-warp-text.psd" or "photoshop-cmyk-style-colors.psd"))
        {
            var document = Fixtures.Load(name);
            Assert.NotNull(document.TextEngine);
            foreach (var layer in document.Layers.Where(l => l.Kind == PsdLayerKind.Text))
            {
                var info = layer.Text!;
                var engineObject = info.TextEngineObject;
                Assert.True(engineObject is not null, name);
                Assert.Equal(info.Text, engineObject.Text);
                Assert.Equal(info.StyleRuns.Count, engineObject.StyleRuns.Count);
                for (var i = 0; i < info.StyleRuns.Count; i++)
                {
                    var own = info.StyleRuns[i];
                    var other = engineObject.StyleRuns[i];
                    Assert.Equal((own.Start, own.Length, own.FontName), (other.Start, other.Length, other.FontName));
                    Assert.Equal(own.FontSize, other.FontSize);
                    Assert.Equal(own.FillColor, other.FillColor);
                    Assert.Equal(own.Tracking, other.Tracking);
                    Assert.Equal(own.Leading, other.Leading);
                    Assert.Equal(own.AutoLeading, other.AutoLeading);
                    Assert.Equal(own.HorizontalScale, other.HorizontalScale);
                    Assert.Equal(own.VerticalScale, other.VerticalScale);
                    Assert.Equal((own.FauxBold, own.FauxItalic), (other.FauxBold, other.FauxItalic));
                }

                Assert.Equal(info.Paragraphs.Select(p => (p.Start, p.Length, p.Justification)), engineObject.Paragraphs.Select(p => (p.Start, p.Length, p.Justification)));
                Assert.Equal(info.Orientation, engineObject.Frame!.Orientation);
                Assert.Equal(info.ShapeKind, engineObject.Frame.ShapeKind);
            }
        }
    }

    [Fact]
    public void Paragraph_direction_comes_from_Txt2()
    {
        // Photoshop keeps the paragraph direction outside the TySh; Txt2 paragraph key 33 has it.
        var info = Fixtures.Load("photoshop-text-rtl-hebrew.psd").Layers.Single(l => l.Text is not null).Text!;

        Assert.DoesNotContain("ParagraphDirection", Encoding.Latin1.GetString(info.Descriptor!["EngineData"]!.Raw.Span), StringComparison.Ordinal);
        Assert.Equal(TextDirection.RightToLeft, Assert.Single(info.Paragraphs).Direction);
        Assert.True(info.Origin.HasFlag(TextDataOrigin.TextEngineBlock));
        Assert.True(info.Origin.HasFlag(TextDataOrigin.TypeTool | TextDataOrigin.EngineData));

        var ltr = Fixtures.Load("photoshop-text-tracking.psd").Layers.First(l => l.Text is not null).Text!;
        Assert.Equal(TextDirection.LeftToRight, ltr.Paragraphs.Single().Direction);
    }

    [Fact]
    public void Cmyk_document_text_colors_convert_from_ink()
    {
        // FillColor /Type 2 [1 0 1 1 0]: no cyan, full magenta and yellow, no black.
        var info = Fixtures.Load("photoshop-cmyk-style-colors.psd").Layers.Single(l => l.Text is not null).Text!;
        var run = Assert.Single(info.StyleRuns);

        Assert.Equal("Menu", info.Text);
        Assert.Equal("MyriadPro-Regular", run.FontName);
        Assert.Equal(new PsdColor(255, 0, 0), run.FillColor);
        Assert.Equal(new PsdColor(255, 0, 0), info.TextEngineObject!.StyleRuns.Single().FillColor);
        Assert.NotNull(run.StrokeColor);
    }

    [Fact]
    public void Vertical_box_text_keeps_its_frame()
    {
        var info = Fixtures.Load("photoshop-text-vertical-box.psd").Layers.Single(l => l.Text is not null).Text!;

        Assert.Equal(TextOrientation.Vertical, info.Orientation);
        Assert.Equal(TextShapeKind.Box, info.ShapeKind);
        Assert.True(info.IsParagraphText);
        Assert.Equal(new TextBounds(0, 0, 150, 250), info.BoxBounds);
        var frame = info.TextEngineObject!.Frame!;
        Assert.Equal(TextOrientation.Vertical, frame.Orientation);
        Assert.Equal(new TextBounds(0, 0, 150, 250), frame.Bounds);
        Assert.Equal(32, frame.Points.Count);
        Assert.Equal(["MS-Gothic"], info.Fonts);
    }

    [Fact]
    public void Rotated_roman_alignment_reads_from_both_records()
    {
        var info = Fixtures.Load("photoshop-text-vertical-rotated-roman.psd").Layers.Single(l => l.Text is not null).Text!;

        Assert.Equal(TextOrientation.Vertical, info.Orientation);
        Assert.Equal(2, info.StyleRuns.Single().BaselineDirection);
        Assert.True(info.TextEngineObject!.RotatedRoman);
        Assert.Equal(2, info.TextEngineObject.StyleRuns.Single().BaselineDirection);

        var upright = Fixtures.Load("photoshop-text-vertical-point.psd").Layers.Single(l => l.Text is not null).Text!;
        Assert.All(upright.StyleRuns, r => Assert.Equal(1, r.BaselineDirection));
        Assert.False(upright.TextEngineObject!.RotatedRoman);
    }

    // ---- Richer TySh fields on committed fixtures ----

    [Fact]
    public void Warp_settings_decode_as_a_typed_record()
    {
        var document = Fixtures.Load("photoshop-warp-text.psd");
        var arc = document.Layers.Single(l => l.Name == "Arc50").Text!;
        var squeeze = document.Layers.Single(l => l.Name == "Sqz").Text!;

        Assert.Equal(new TextWarp(TextWarpStyle.Arc, "warpArc", 50, 0, 0, TextOrientation.Horizontal), arc.WarpSettings);
        Assert.Equal(new TextWarp(TextWarpStyle.Squeeze, "warpSqueeze", -70, 25, -10, TextOrientation.Vertical), squeeze.WarpSettings);
        Assert.False(arc.WarpSettings!.IsIdentity);
        Assert.Equal(TextAntiAlias.Smooth, arc.AntiAlias);

        var plain = Fixtures.Load("photoshop-text-tracking.psd").Layers.First(l => l.Text is not null).Text!;
        Assert.Equal(TextWarpStyle.None, plain.WarpSettings!.Style);
        Assert.True(plain.WarpSettings.IsIdentity);
    }

    [Fact]
    public void Bounds_and_bounding_box_are_typed_rectangles()
    {
        var arc = Fixtures.Load("photoshop-warp-text.psd").Layers.Single(l => l.Name == "Arc50").Text!;

        Assert.Equal(new TextBounds(-0.03662109375, -20.595703125, 62.6953125, 7.79296875), arc.Bounds);
        Assert.Equal(new TextBounds(-0.1376953125, -17.25, 61.53515625, 0.1875), arc.BoundingBox);
        Assert.Equal(TextShapeKind.Point, arc.ShapeKind);
        Assert.Null(arc.BoxBounds);

        var box = Fixtures.Load("photoshop-text-box-auto-leading.psd").Layers.Single(l => l.Text is not null).Text!;
        Assert.Equal(new TextBounds(0, 0, 200, 200), box.BoxBounds);
        Assert.Equal(200, box.Bounds!.Value.Width);
        Assert.Equal(TextAntiAlias.Crisp, box.AntiAlias);
    }

    [Fact]
    public void Character_scales_and_leading_modes_decode()
    {
        var scaled = Fixtures.Load("photoshop-text-hv-scale.psd").Layers.Single(l => l.Text is not null).Text!.StyleRuns.Single();
        Assert.Equal(0.8, scaled.HorizontalScale);
        Assert.Equal(1.5, scaled.VerticalScale);

        var fixedLeading = Fixtures.Load("photoshop-text-point-fixed-leading.psd").Layers.Single(l => l.Text is not null).Text!.StyleRuns.Single();
        Assert.False(fixedLeading.AutoLeading);
        Assert.Equal(40, fixedLeading.Leading);
        Assert.Equal(24, fixedLeading.FontSize);

        var auto = Fixtures.Load("photoshop-text-point-auto-leading.psd").Layers.Single(l => l.Text is not null).Text!;
        Assert.True(auto.StyleRuns.Single().AutoLeading);
        Assert.Null(auto.StyleRuns.Single().Leading);
        Assert.Equal(3, auto.Paragraphs.Count);
        Assert.All(auto.Paragraphs, p => Assert.Equal(1.2, p.AutoLeadingFraction));

        var anchor = Fixtures.Load("photoshop-text-anchor-whole.psd").Layers.Single(l => l.Text is not null).Text!;
        Assert.Equal(TextAntiAlias.Sharp, anchor.AntiAlias);
        Assert.Equal(100.0, anchor.Transform[4], 9);
    }

    // ---- Synthetic TySh: character and paragraph properties ----

    [Fact]
    public void EngineData_paragraph_and_character_properties_decode()
    {
        var engine = EngineDataText(
            "Ab\rCd\r",
            styleRuns:
            [
                ("/Font 0 /FontSize 30 /BaselineShift 4.5 /Underline true /Strikethrough true /FontCaps 2 /FontBaseline 1 /Ligatures false /DLigatures true /AutoKerning false /Kerning -20 /NoBreak true /Language 3 /StrokeFlag true /OutlineWidth 2.5", 3),
                ("/Font 1 /FontCaps 1 /FontBaseline 2", 3),
            ],
            paragraphRuns:
            [
                ("/Justification 2 /FirstLineIndent -10 /StartIndent 20 /EndIndent 5 /SpaceBefore 6 /SpaceAfter 7 /AutoLeading 1.5 /AutoHyphenate true /ParagraphDirection 1", 3),
                ("/Justification 6", 3),
            ],
            fonts: ["ArialMT", "Georgia-Bold"]);
        var layer = TypeLayer("styled", TypeToolBlock("Ab\rCd", textIndex: null, engine));
        var info = PsdDocument.Load(Build(layer)).Layers.Single().Text!;

        Assert.Equal("Ab\nCd", info.Text);
        var first = info.StyleRuns[0];
        Assert.Equal(("ArialMT", 30.0), (first.FontName, first.FontSize!.Value));
        Assert.Equal(4.5, first.BaselineShift);
        Assert.True(first.Underline && first.Strikethrough && first.NoBreak && first.DiscretionaryLigatures && first.StrokeEnabled);
        Assert.False(first.Ligatures);
        Assert.False(first.AutoKerning);
        Assert.Equal(-20, first.Kerning);
        Assert.Equal(TextCaps.AllCaps, first.Caps);
        Assert.Equal(TextBaselinePosition.Superscript, first.BaselinePosition);
        Assert.Equal(3, first.Language);
        Assert.Equal(2.5, first.StrokeWidth);

        // The second run falls back to the normal style sheet (FontSize 12) for what it omits.
        var second = info.StyleRuns[1];
        Assert.Equal("Georgia-Bold", second.FontName);
        Assert.Equal(12, second.FontSize);
        Assert.Equal(TextCaps.SmallCaps, second.Caps);
        Assert.Equal(TextBaselinePosition.Subscript, second.BaselinePosition);
        Assert.Equal(["ArialMT", "Georgia-Bold"], info.Fonts);

        var paragraph = info.Paragraphs[0];
        Assert.Equal(TextJustification.Center, paragraph.Justification);
        Assert.Equal((-10.0, 20.0, 5.0, 6.0, 7.0), (paragraph.FirstLineIndent, paragraph.StartIndent, paragraph.EndIndent, paragraph.SpaceBefore, paragraph.SpaceAfter));
        Assert.Equal(1.5, paragraph.AutoLeadingFraction);
        Assert.True(paragraph.AutoHyphenate);
        Assert.Equal(TextDirection.RightToLeft, paragraph.Direction);
        Assert.Equal("Ab", paragraph.Text);

        // The second paragraph inherits the normal paragraph sheet's indent.
        Assert.Equal(TextJustification.JustifyAll, info.Paragraphs[1].Justification);
        Assert.Equal(3, info.Paragraphs[1].StartIndent);
        Assert.Equal(TextDirection.Auto, info.Paragraphs[1].Direction);
    }

    // ---- Synthetic Txt2: gap filling and extraction ----

    [Fact]
    public void Empty_TySh_text_is_filled_from_Txt2()
    {
        var layer = TypeLayer("Label", TypeToolBlock(string.Empty, textIndex: 0, engineData: null));
        var builder = new PsdBuilder { Width = 4, Height = 4 };
        builder.Layers.Add(layer);
        builder.GlobalBlocks.Add(("Txt2", Txt2Block(("Hello\rWorld\r", 1))));

        var document = PsdDocument.Load(builder.Build());
        var info = document.Layers.Single().Text!;

        Assert.Equal("Hello\nWorld", info.Text);
        Assert.Equal(TextDataOrigin.TypeTool | TextDataOrigin.TextEngineBlock, info.Origin);
        var run = Assert.Single(info.StyleRuns);
        Assert.Equal(("ArialMT", 18.0, 100.0), (run.FontName, run.FontSize!.Value, run.Tracking!.Value));
        Assert.Equal(new PsdColor(255, 0, 0), run.FillColor);
        Assert.Equal(2, info.Paragraphs.Count);
        Assert.Equal(TextJustification.Center, info.Paragraphs[0].Justification);
        Assert.Equal(TextShapeKind.Box, info.TextEngineObject!.Frame!.ShapeKind);
        Assert.Equal(new TextBounds(0, 0, 100, 50), info.TextEngineObject.Frame.Bounds);

        var content = document.ExtractText();
        Assert.Equal(["Hello\nWorld"], content.TextLayers);
        Assert.DoesNotContain(content.Items, i => i.Kind == PsdTextKind.TextEngineObject);
    }

    [Fact]
    public void Damaged_TySh_is_rebuilt_from_its_Txt2_object()
    {
        // A descriptor version Photoshop never wrote: the descriptor does not decode, but the
        // transform and the TextIndex are still in the bytes.
        var payload = TypeToolBlock("ignored", textIndex: 1, engineData: null, descriptorVersion: 99);
        var builder = new PsdBuilder { Width = 4, Height = 4 };
        builder.Layers.Add(TypeLayer("Broken", payload));
        builder.GlobalBlocks.Add(("Txt2", Txt2Block(("unused\r", 0), ("Recovered\r", 0))));

        var document = PsdDocument.Load(builder.Build());
        var layer = document.Layers.Single();

        Assert.Equal(PsdLayerKind.Text, layer.Kind);
        var info = layer.Text!;
        Assert.Equal("Recovered", info.Text);
        Assert.Equal(TextDataOrigin.TextEngineBlock, info.Origin);
        Assert.Equal(1, info.TextIndex);
        Assert.Equal([2.0, 0, 0, 2, 10, 20], info.Transform);
        Assert.Equal(TextShapeKind.Box, info.ShapeKind);
        Assert.True(info.IsParagraphText);
        Assert.Equal(new TextBounds(0, 0, 100, 50), info.BoxBounds);
        Assert.Contains("Recovered", document.ExtractText().TextLayers);
    }

    [Fact]
    public void Txt2_objects_no_layer_reports_are_extracted()
    {
        var builder = new PsdBuilder { Width = 4, Height = 4 };
        builder.Layers.Add(TypeLayer("Title", TypeToolBlock("Old title", textIndex: 0, engineData: null)));
        builder.GlobalBlocks.Add(("Txt2", Txt2Block(("New title\r", 0), ("Orphan\r", 0))));

        var document = PsdDocument.Load(builder.Build());
        var content = document.ExtractText();

        Assert.Equal(["Old title"], content.TextLayers);
        var extra = content.OfKind(PsdTextKind.TextEngineObject).ToList();
        Assert.Equal(2, extra.Count);
        Assert.Equal(("New title", "Title"), (extra[0].Text, extra[0].Source));
        Assert.Same(document.Layers[0], extra[0].Layer);
        Assert.Equal(("Orphan", "Txt2/1"), (extra[1].Text, extra[1].Source));
        Assert.Null(extra[1].Layer);
        Assert.Equal(TextDataOrigin.TextEngineBlock, extra[1].TextInfo!.Origin);

        var without = document.ExtractText(new TextExtractionOptions { IncludeTextEngineObjects = false });
        Assert.DoesNotContain(without.Items, i => i.Kind == PsdTextKind.TextEngineObject);
    }

    [Fact]
    public void Txt2_point_units_scale_by_document_resolution()
    {
        // Unit flag 1 means points at the document resolution: 18 pt at 300 ppi is 75 px.
        var builder = new PsdBuilder { Width = 4, Height = 4 };
        builder.Resources.Add((1005, string.Empty, ResolutionResource(300)));
        builder.Layers.Add(TypeLayer("T", TypeToolBlock(string.Empty, textIndex: 0, engineData: null)));
        builder.GlobalBlocks.Add(("Txt2", Txt2Block(("Size\r", 1))));

        var run = PsdDocument.Load(builder.Build()).Layers.Single().Text!.StyleRuns.Single();

        Assert.Equal(18 * 300 / 72.0, run.FontSize!.Value, 6);
        Assert.Equal(100, run.Tracking);
    }

    [Fact]
    public void Txt2_lists_and_sparse_sheets_parse()
    {
        var engine = TextEngineBlock.Parse(Txt2Block(("A\rB\r", 0)));

        Assert.NotNull(engine);
        var paragraphs = engine.Objects.Single().Paragraphs;
        Assert.Equal(5, paragraphs[0].ListStyleIndex);
        Assert.Equal(1, paragraphs[0].ListTier);
        Assert.Null(paragraphs[1].ListStyleIndex);
        Assert.Equal(TextDirection.LeftToRight, paragraphs[1].Direction);

        // The second paragraph omits indents and falls back to the default paragraph sheet (/1/3).
        Assert.Equal(12, paragraphs[1].StartIndent);
        Assert.Equal(TextJustification.Right, paragraphs[1].Justification);
        Assert.Equal(["AdobeInvisFont", "ArialMT"], engine.Fonts.Select(f => f.PostScriptName));
    }

    [Fact]
    public void Truncated_Txt2_blocks_never_throw()
    {
        var bytes = Fixtures.Load("photoshop-text-tracking.psd").GetGlobalTaggedBlock("Txt2")!.Data.ToArray();
        for (var length = 0; length < bytes.Length; length += Math.Max(1, length / 16))
        {
            _ = TextEngineBlock.Parse(bytes.AsSpan(0, length));
        }

        Assert.NotNull(TextEngineBlock.Parse(bytes));
        Assert.Null(TextEngineBlock.Parse("/0 << >>"u8));
        Assert.Null(TextEngineBlock.Parse("/1 << /1 ["u8));
    }

    // ---- Photoshop 5 tySh ----

    [Fact]
    public void Legacy_type_record_decodes_runs_alignment_and_color()
    {
        var spec = new LegacySpec
        {
            Lines =
            [
                (0, 1, "WWW.\r", [4, 4, 4, 4, 4]),
                (0, -1, "MENU", [3, 3, 3, 3]),
            ],
        };
        var document = PsdDocument.Load(Build(TypeLayer("legacy", LegacyPayload(spec), key: "tySh")));
        var layer = document.Layers.Single();
        var info = layer.Text!;

        Assert.Equal(PsdLayerKind.Text, layer.Kind);
        Assert.Equal(TextDataOrigin.LegacyTypeTool, info.Origin);
        Assert.Equal("WWW.\nMENU", info.Text);
        Assert.Equal("WWW.\rMENU", info.RawText);
        Assert.Equal([0.7722, 0, 0, 0.7722, 20, 40], info.Transform);
        Assert.Equal(TextAntiAlias.Sharp, info.AntiAlias);

        Assert.Equal(2, info.StyleRuns.Count);
        var title = info.StyleRuns[0];
        Assert.Equal((0, 5, "WWW."), (title.Start, title.Length, title.Text));
        Assert.Equal(("FuturaBT-BoldCondensed", "Futura BdCn BT", "Bold"), (title.FontName, title.FontFamily, title.FontStyleName));
        Assert.Equal(23.3125, title.FontSize);
        Assert.Equal(100, title.Tracking);
        Assert.True(title.AutoLeading);
        Assert.Null(title.Leading);
        Assert.Equal(new PsdColor(0x00, 0x2A, 0x77), title.FillColor);

        var menu = info.StyleRuns[1];
        Assert.Equal((5, 4), (menu.Start, menu.Length));
        Assert.Equal("ArialMT", menu.FontName);
        Assert.Equal(32, menu.FontSize);
        Assert.Equal(400, menu.Tracking);
        Assert.False(menu.AutoLeading);
        Assert.Equal(34, menu.Leading);
        Assert.Equal(-50, menu.Kerning);
        Assert.Equal(2, menu.BaselineShift);
        Assert.False(menu.AutoKerning);

        Assert.Equal([TextJustification.Center, TextJustification.Right], info.Paragraphs.Select(p => p.Justification));
        Assert.Equal([(0, 5), (5, 4)], info.Paragraphs.Select(p => (p.Start, p.Length)));
        Assert.Equal(["FuturaBT-BoldCondensed", "ArialMT"], info.Fonts);
        Assert.Equal(["WWW.\nMENU"], document.ExtractText().TextLayers);
    }

    [Fact]
    public void Legacy_lines_without_separators_get_one_and_the_last_loses_its_own()
    {
        var spec = new LegacySpec
        {
            Lines =
            [
                (0, 0, "AB", [3, 4]),
                (0, 0, "C\r", [4, 4]),
            ],
        };
        var info = PsdDocument.Load(Build(TypeLayer("legacy", LegacyPayload(spec), key: "tySh"))).Layers.Single().Text!;

        Assert.Equal("AB\rC", info.RawText);
        Assert.Equal([(0, 1), (1, 3)], info.StyleRuns.Select(r => (r.Start, r.Length)));
        Assert.Equal([(0, 3), (3, 1)], info.Paragraphs.Select(p => (p.Start, p.Length)));
    }

    [Fact]
    public void Legacy_style_section_with_a_version_word_is_retried()
    {
        var spec = new LegacySpec { StyleVersionWord = true };
        var info = PsdDocument.Load(Build(TypeLayer("legacy", LegacyPayload(spec), key: "tySh"))).Layers.Single().Text!;

        Assert.Equal("WWW.\nMENU", info.Text);
        Assert.Equal(32, info.StyleRuns[1].FontSize);
        Assert.Equal("ArialMT", info.StyleRuns[1].FontName);
    }

    [Fact]
    public void Legacy_styles_naming_unknown_faces_fall_back_to_the_first_face()
    {
        var spec = new LegacySpec { StyleFaceMarks = [9, 8] };
        var info = PsdDocument.Load(Build(TypeLayer("legacy", LegacyPayload(spec), key: "tySh"))).Layers.Single().Text!;

        Assert.Equal("WWW.\nMENU", info.Text);
        Assert.All(info.StyleRuns, r => Assert.Equal("FuturaBT-BoldCondensed", r.FontName));
    }

    [Theory]
    [InlineData((ushort)0, (ushort)65535, (ushort)32896, (ushort)0, (ushort)0, 255, 128, 0)]
    [InlineData((ushort)8, (ushort)5000, (ushort)0, (ushort)0, (ushort)0, 128, 128, 128)]
    [InlineData((ushort)2, (ushort)65535, (ushort)0, (ushort)0, (ushort)65535, 255, 0, 0)]
    [InlineData((ushort)2, (ushort)65535, (ushort)65535, (ushort)65535, (ushort)0, 0, 0, 0)]
    [InlineData((ushort)1, (ushort)39835, (ushort)65535, (ushort)30583, (ushort)0, 0x00, 0x2A, 0x77)]
    public void Legacy_colors_decode_every_space(ushort space, ushort c0, ushort c1, ushort c2, ushort c3, int r, int g, int b)
    {
        var spec = new LegacySpec { Color = (space, c0, c1, c2, c3), AntiAlias = 0 };
        var info = PsdDocument.Load(Build(TypeLayer("legacy", LegacyPayload(spec), key: "tySh"))).Layers.Single().Text!;

        Assert.Equal(new PsdColor((byte)r, (byte)g, (byte)b), info.StyleRuns[0].FillColor);
        Assert.Equal(TextAntiAlias.None, info.AntiAlias);
    }

    [Fact]
    public void Legacy_vertical_lines_report_vertical_orientation()
    {
        var spec = new LegacySpec { Lines = [(1, 0, "TATE", [4, 4, 4, 4])] };
        var info = PsdDocument.Load(Build(TypeLayer("legacy", LegacyPayload(spec), key: "tySh"))).Layers.Single().Text!;

        Assert.Equal(TextOrientation.Vertical, info.Orientation);
        Assert.Equal("TATE", info.Text);
    }

    [Fact]
    public void Damaged_legacy_records_degrade_without_throwing()
    {
        var payload = LegacyPayload(new LegacySpec());
        for (var length = 0; length < payload.Length; length++)
        {
            var info = PsdDocument.Load(Build(TypeLayer("legacy", payload[..length], key: "tySh"))).Layers.Single().Text;
            Assert.True(info is null || info.Text.Length > 0);
        }

        // A character count far beyond the payload is rejected rather than allocated.
        var absurd = LegacyPayload(new LegacySpec { CharacterCountOverride = 0x7FFFFFFF });
        Assert.Null(PsdDocument.Load(Build(TypeLayer("legacy", absurd, key: "tySh"))).Layers.Single().Text);

        // Zero styles cannot describe any text.
        var noStyles = LegacyPayload(new LegacySpec { Styles = [] });
        Assert.Null(PsdDocument.Load(Build(TypeLayer("legacy", noStyles, key: "tySh"))).Layers.Single().Text);
    }

    // ---- Builders ----

    private static byte[] Build(BuilderLayer layer)
    {
        var builder = new PsdBuilder { Width = 4, Height = 4 };
        builder.Layers.Add(layer);
        return builder.Build();
    }

    private static BuilderLayer TypeLayer(string name, byte[] payload, string key = "TySh")
    {
        var layer = new BuilderLayer { Name = name, Rect = new PsdRect(0, 0, 2, 2) };
        layer.Channels[-1] = PsdBuilder.Plane8(2, 2, 255);
        layer.Channels[0] = PsdBuilder.Plane8(2, 2, 0);
        layer.Channels[1] = PsdBuilder.Plane8(2, 2, 0);
        layer.Channels[2] = PsdBuilder.Plane8(2, 2, 0);
        layer.Blocks.Add((key, payload));
        return layer;
    }

    private static byte[] ResolutionResource(double ppi)
    {
        var writer = new PsdBuilder.Writer();
        writer.U32((uint)(ppi * 65536));
        writer.U16(1);
        writer.U16(1);
        writer.U32((uint)(ppi * 65536));
        writer.U16(1);
        writer.U16(1);
        return writer.ToArray();
    }

    /// <summary>A TySh payload: version, transform, text descriptor (Txt, TextIndex, EngineData), warp descriptor.</summary>
    private static byte[] TypeToolBlock(string text, int? textIndex, byte[]? engineData, uint descriptorVersion = 16)
    {
        var writer = new PsdBuilder.Writer();
        writer.U16(1);
        foreach (var value in new double[] { 2, 0, 0, 2, 10, 20 })
        {
            writer.F64(value);
        }

        writer.U16(50);
        writer.U32(descriptorVersion);
        writer.UnicodeString(string.Empty);
        writer.DescriptorId("TxLr");
        var items = 2 + (textIndex is null ? 0 : 1) + (engineData is null ? 0 : 1);
        writer.U32((uint)items);
        writer.DescriptorId("Txt ");
        writer.Ascii("TEXT");
        writer.UnicodeString(text);
        writer.DescriptorId("Ornt");
        writer.Ascii("enum");
        writer.DescriptorId("Ornt");
        writer.DescriptorId("Hrzn");
        if (textIndex is int index)
        {
            writer.DescriptorId("TextIndex");
            writer.Ascii("long");
            writer.I32(index);
        }

        if (engineData is not null)
        {
            writer.DescriptorId("EngineData");
            writer.Ascii("tdta");
            writer.U32((uint)engineData.Length);
            writer.Bytes(engineData);
        }

        writer.U16(1);
        writer.U32(16);
        writer.UnicodeString(string.Empty);
        writer.DescriptorId("warp");
        writer.U32(1);
        writer.DescriptorId("warpStyle");
        writer.Ascii("enum");
        writer.DescriptorId("warpStyle");
        writer.DescriptorId("warpNone");
        writer.Zeros(16); // left, top, right, bottom
        return writer.ToArray();
    }

    /// <summary>Encodes an EngineData string body: UTF-16BE with a BOM, parentheses and backslashes escaped.</summary>
    private static byte[] EngineString(string text)
    {
        var output = new List<byte> { (byte)'(' };
        foreach (var b in new byte[] { 0xFE, 0xFF }.Concat(Encoding.BigEndianUnicode.GetBytes(text)))
        {
            if (b is (byte)'(' or (byte)')' or (byte)'\\')
            {
                output.Add((byte)'\\');
            }

            output.Add(b);
        }

        output.Add((byte)')');
        return [.. output];
    }

    private static byte[] EngineDataText(string text, (string Sheet, int Length)[] styleRuns, (string Sheet, int Length)[] paragraphRuns, string[] fonts)
    {
        var strings = new List<string> { text };
        var fontSet = new StringBuilder();
        foreach (var font in fonts)
        {
            fontSet.Append(CultureInvariant($" << /Name ${strings.Count} /FontType 1 >>"));
            strings.Add(font);
        }

        var styles = string.Join(" ", styleRuns.Select(r => $"<< /StyleSheet << /StyleSheetData << {r.Sheet} >> >> >>"));
        var styleLengths = string.Join(" ", styleRuns.Select(r => r.Length));
        var paragraphs = string.Join(" ", paragraphRuns.Select(r => $"<< /ParagraphSheet << /Properties << {r.Sheet} >> >> >>"));
        var paragraphLengths = string.Join(" ", paragraphRuns.Select(r => r.Length));
        var template =
            "\n\n<<\n\t/EngineDict << /Editor << /Text $0 >>" +
            $" /ParagraphRun << /RunArray [ {paragraphs} ] /RunLengthArray [ {paragraphLengths} ] >>" +
            $" /StyleRun << /RunArray [ {styles} ] /RunLengthArray [ {styleLengths} ] >> /AntiAlias 3 >>" +
            $" /ResourceDict << /FontSet [{fontSet} ] /TheNormalStyleSheet 0 /TheNormalParagraphSheet 0" +
            " /StyleSheetSet [ << /StyleSheetData << /Font 0 /FontSize 12 /FillColor << /Type 1 /Values [ 1.0 0.0 0.0 0.0 ] >> >> >> ]" +
            " /ParagraphSheetSet [ << /Properties << /Justification 0 /StartIndent 3 >> >> ] >> >>\n";
        return EngineMany(template, strings);
    }

    private static string CultureInvariant(FormattableString value) => FormattableString.Invariant(value);

    /// <summary>
    /// A Txt2 block with one object per (text, unit flag) entry: font set (AdobeInvisFont, ArialMT),
    /// one box frame, default sheets, a two-key style run (ArialMT 18, tracking 100, red) and
    /// paragraph runs per line (the first centered with a bullet list, the rest sparse).
    /// </summary>
    private static byte[] Txt2Block(params (string Text, int Unit)[] objects)
    {
        var strings = new List<string>();
        var body = new StringBuilder();
        body.Append(" /98 << /0 14 >> /0 << /1 << /0 [ << /0 << /99 /CoolTypeFont /0 << /0 $0 /2 0 >> >> >> << /0 << /99 /CoolTypeFont /0 << /0 $1 /2 1 /5 $2 >> >> >> ] >>");
        strings.AddRange(["AdobeInvisFont", "ArialMT", "Version 7.06"]);
        body.Append(" /8 << /0 [ << /0 << /1 << /0 [ 0.0 0.0 100.0 0.0 100.0 50.0 0.0 50.0 ] >> /2 << /0 1 /6 [ -2.0 -2.0 ] >> >> >> ] >> >>");
        body.Append(" /1 << /2 << /0 1 /1 12.0 /8 0 >> /3 << /0 1 /2 12.0 /33 0 >> /1 [");
        foreach (var (text, unit) in objects)
        {
            var lines = text.Split('\r', StringSplitOptions.RemoveEmptyEntries);
            var paragraphRuns = new StringBuilder();
            for (var i = 0; i < lines.Length; i++)
            {
                var sheet = i == 0 ? "/0 2 /36 5 /37 1" : "/1 4.0";
                paragraphRuns.Append(CultureInvariant($" << /0 << /0 << /0 ( ) /5 << {sheet} >> /6 {unit} >> >> /1 {lines[i].Length + 1} >>"));
            }

            var index = strings.Count;
            strings.Add(text);
            body.Append(CultureInvariant($" << /0 << /0 ${index} /5 << /0 [{paragraphRuns} ] >>"));
            body.Append(CultureInvariant($" /6 << /0 [ << /0 << /0 << /0 ( ) /5 {unit} /6 << /0 1 /1 18.0 /8 100 /53 << /99 /SimplePaint /0 << /0 1 /1 [ 1.0 1.0 0.0 0.0 ] >> >> >> >> >> /1 {text.Length} >> ] >>"));
            body.Append(" /10 << /0 1 /2 true >> >> /1 << /0 [ << /0 0 >> ] >> >>");
        }

        body.Append(" ] >>");
        return EngineMany(body.ToString(), strings);
    }

    /// <summary>ASCII EngineData with <c>$0</c>, <c>$1</c>... replaced by encoded strings.</summary>
    private static byte[] EngineMany(string template, List<string> strings)
    {
        var output = new List<byte>();
        for (var i = 0; i < template.Length; i++)
        {
            if (template[i] == '$' && i + 1 < template.Length && char.IsDigit(template[i + 1]))
            {
                var end = i + 1;
                while (end < template.Length && char.IsDigit(template[end]))
                {
                    end++;
                }

                output.AddRange(EngineString(strings[int.Parse(template[(i + 1)..end], System.Globalization.CultureInfo.InvariantCulture)]));
                i = end - 1;
            }
            else
            {
                output.Add((byte)template[i]);
            }
        }

        return [.. output];
    }

    /// <summary>The fields of a Photoshop 5 tySh record (.reference/docs/psd-legacy-text.md, "Record layout").</summary>
    private sealed class LegacySpec
    {
        public (ushort Mark, string PostScript, string Family, string Style)[] Faces { get; init; } =
        [
            (1, "FuturaBT-BoldCondensed", "Futura BdCn BT", "Bold"),
            (2, "ArialMT", "Arial", "Regular"),
        ];

        /// <summary>Mark, face mark, size, tracking (em), kerning (em), leading, base shift, auto kern.</summary>
        public (ushort Mark, ushort Face, double Size, double Tracking, double Kerning, double Leading, double Shift, bool AutoKern)[] Styles { get; init; } =
        [
            (4, 1, 23.3125, 0.1, 0, 0, 0, true),
            (3, 2, 32, 0.4, -0.05, 34, 2, false),
        ];

        /// <summary>Overrides the face marks of <see cref="Styles"/> in order.</summary>
        public ushort[]? StyleFaceMarks { get; init; }

        public bool StyleVersionWord { get; init; }

        public (ushort Orientation, short Alignment, string Text, ushort[] Marks)[] Lines { get; init; } =
        [
            (0, 1, "WWW.\r", [4, 4, 4, 4, 4]),
            (0, -1, "MENU", [3, 3, 3, 3]),
        ];

        public uint? CharacterCountOverride { get; init; }

        public (ushort Space, ushort C0, ushort C1, ushort C2, ushort C3) Color { get; init; } = (1, 39835, 65535, 30583, 0);

        public byte AntiAlias { get; init; } = 1;
    }

    private static int Fixed(double value) => (int)Math.Round(value * 65536);

    private static byte[] LegacyPayload(LegacySpec spec)
    {
        var writer = new PsdBuilder.Writer();
        writer.U16(1);
        foreach (var value in new[] { 0.7722, 0, 0, 0.7722, 20, 40 })
        {
            writer.F64(value);
        }

        writer.U16(6);
        writer.U16((ushort)spec.Faces.Length);
        foreach (var (mark, postScript, family, style) in spec.Faces)
        {
            writer.U16(mark);
            writer.U32(0);
            writer.Pascal(postScript, 1);
            writer.Pascal(family, 1);
            writer.Pascal(style, 1);
            writer.U16(0);
            writer.U32(1);
            writer.I32(0);
        }

        if (spec.StyleVersionWord)
        {
            writer.U16(0);
        }

        writer.U16((ushort)spec.Styles.Length);
        for (var i = 0; i < spec.Styles.Length; i++)
        {
            var style = spec.Styles[i];
            writer.U16(style.Mark);
            writer.U16(spec.StyleFaceMarks?[i] ?? style.Face);
            writer.I32(Fixed(style.Size));
            writer.I32(Fixed(style.Tracking));
            writer.I32(Fixed(style.Kerning));
            writer.I32(Fixed(style.Leading));
            writer.I32(Fixed(style.Shift));
            writer.U8(style.AutoKern ? (byte)1 : (byte)0);
            writer.U8(0);
        }

        var characters = (uint)spec.Lines.Sum(l => l.Text.Length);
        writer.U16(0);
        writer.U32(0x10000);
        writer.U32(spec.CharacterCountOverride ?? characters);
        writer.Zeros(16);
        writer.U16((ushort)spec.Lines.Length);
        foreach (var (orientation, alignment, text, marks) in spec.Lines)
        {
            writer.U32((uint)text.Length);
            writer.U16(orientation);
            writer.I16(alignment);
            for (var i = 0; i < text.Length; i++)
            {
                writer.U16(text[i]);
                writer.U16(marks[i]);
            }
        }

        writer.U16(spec.Color.Space);
        writer.U16(spec.Color.C0);
        writer.U16(spec.Color.C1);
        writer.U16(spec.Color.C2);
        writer.U16(spec.Color.C3);
        writer.U8(spec.AntiAlias);
        writer.Zeros(3);
        return writer.ToArray();
    }
}
