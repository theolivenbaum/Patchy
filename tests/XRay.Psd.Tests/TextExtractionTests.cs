using XRay.Psd.Tests.Support;
using XRay.Psd.Text;

namespace XRay.Psd.Tests;

public sealed class TextExtractionTests
{
    [Fact]
    public void Extracts_type_layer_content_with_fonts()
    {
        var content = Fixtures.Load("photoshop-text-rtl-hebrew.psd").ExtractText();

        var text = Assert.Single(content.OfKind(PsdTextKind.TextLayer));
        Assert.Equal("שלום עולם", text.Text);
        Assert.Equal("rtl_hebrew_dir_rtl", text.Source);
        Assert.NotNull(text.Layer);
        Assert.Contains("ArialMT", text.TextInfo!.Fonts);
        Assert.Equal(["rtl_hebrew_dir_rtl", "Background"], content.LayerNames);
    }

    [Fact]
    public void Layer_names_come_in_panel_order()
    {
        var content = Fixtures.Load("qual_rca_pinout.psd").ExtractText();

        Assert.Equal(
            ["Layer 2", "12345678910", "5=Audio (W)", "4=Audio (R)", "9=Video", "10=G", "1=G", "Layer 1"],
            content.LayerNames);
        Assert.Equal(
            ["12345678910", "5=Audio (W)", "4=Audio (R)", "9=Video", "10=G", "1=G"],
            content.TextLayers);
    }

    [Fact]
    public void Group_names_are_tagged_and_paths_nest()
    {
        var content = Fixtures.Load("arrows.psd").ExtractText();

        Assert.Contains(content.Items, i => i.Kind == PsdTextKind.GroupName && i.Text == "Turn arrows");
        Assert.Contains(content.Items, i => i.Kind == PsdTextKind.LayerName && i.Source == "Move arrows/Shape 3 copy");
    }

    [Fact]
    public void Hidden_layers_can_be_excluded()
    {
        var document = Fixtures.Load("arrows.psd");
        var visibleOnly = document.ExtractText(new TextExtractionOptions { IncludeHiddenLayers = false });

        Assert.DoesNotContain(visibleOnly.Items, i => i.Text == "Turn arrows" || i.Text == "Shape 5");
        Assert.Contains(visibleOnly.Items, i => i.Text == "Move arrows");
    }

    [Fact]
    public void Channel_and_path_names_are_extracted()
    {
        var channels = Fixtures.Load("photoshop-saved-channels.psd").ExtractText();
        Assert.Equal(["Duplicate", "Duplicate", "特色チャンネル"], channels.OfKind(PsdTextKind.ChannelName).Select(i => i.Text));

        var paths = Fixtures.Load("photoshop-saved-paths.psd").ExtractText();
        Assert.Equal(["Alpha Path", "Beta Path"], paths.OfKind(PsdTextKind.PathName).Select(i => i.Text));
    }

    [Fact]
    public void Paragraph_text_keeps_line_breaks_and_style_runs()
    {
        var document = Fixtures.Load("photoshop-text-box-auto-leading.psd");
        var layer = document.Layers.Single(l => l.Text is not null);
        var info = layer.Text!;

        Assert.Equal("HHHH HHHH HHHH xxxx", info.Text);
        Assert.True(info.IsParagraphText);
        Assert.NotEmpty(info.Paragraphs);
        var run = info.StyleRuns[0];
        Assert.Equal("ArialMT", run.FontName);
        Assert.True(run.FontSize > 0);
        Assert.NotNull(run.FillColor);
    }

    [Fact]
    public void Every_text_fixture_yields_its_type_layers()
    {
        foreach (var name in Fixtures.AllDocuments().Where(n => n.StartsWith("photoshop-text-", StringComparison.Ordinal)))
        {
            var document = Fixtures.Load(name);
            var textLayers = document.Layers.Where(l => l.Kind == PsdLayerKind.Text).ToList();
            Assert.NotEmpty(textLayers);
            var content = document.ExtractText();
            Assert.Equal(textLayers.Count, content.OfKind(PsdTextKind.TextLayer).Count());
            Assert.All(content.OfKind(PsdTextKind.TextLayer), i => Assert.False(string.IsNullOrWhiteSpace(i.Text)));
        }
    }

    [Fact]
    public void Vertical_text_reports_orientation()
    {
        var document = Fixtures.Load("photoshop-text-vertical-point.psd");
        var text = document.Layers.Single(l => l.Text is not null).Text!;
        Assert.Equal(TextOrientation.Vertical, text.Orientation);
    }

    [Fact]
    public void ToString_joins_everything_for_indexing()
    {
        var all = Fixtures.Load("photoshop-text-rtl-hebrew.psd").ExtractText().ToString();
        Assert.Contains("שלום עולם", all, StringComparison.Ordinal);
        Assert.Contains("Background", all, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("a\rb", "a\nb")]
    [InlineData("a\r\nb\r", "a\nb")]
    [InlineData("line\u0003break", "line\nbreak")]
    [InlineData("", "")]
    public void Paragraph_separators_normalize(string raw, string expected)
    {
        Assert.Equal(expected, TextLayerInfo.NormalizeText(raw));
    }
}
