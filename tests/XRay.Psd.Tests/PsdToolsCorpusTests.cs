using XRay.Psd.Layers;
using XRay.Psd.Rendering;
using XRay.Psd.Resources;
using XRay.Psd.Tests.Support;
using XRay.Psd.Text;

namespace XRay.Psd.Tests;

/// <summary>
/// Real Photoshop files for the color modes and depths the committed fixtures
/// lack (16/32-bit, grayscale, indexed, Lab, duotone, multichannel, bitmap) and
/// a real embedded PSB smart object, from the psd-tools collection (MIT,
/// tests/fixtures/psd-tools with its LICENSE).
/// </summary>
public sealed class PsdToolsCorpusTests
{
    public static TheoryData<string, PsdColorMode, int, int> ColorModes() => new()
    {
        { "4x4_8bit_grayscale.psd", PsdColorMode.Grayscale, 8, 1 },
        { "4x4_16bit_grayscale.psd", PsdColorMode.Grayscale, 16, 1 },
        { "4x4_32bit_grayscale.psd", PsdColorMode.Grayscale, 32, 1 },
        { "4x4_8bit_rgb.psd", PsdColorMode.Rgb, 8, 1 },
        { "4x4_8bit_rgba.psd", PsdColorMode.Rgb, 8, 1 },
        { "4x4_16bit_rgb.psd", PsdColorMode.Rgb, 16, 1 },
        { "4x4_32bit_rgb.psd", PsdColorMode.Rgb, 32, 1 },
        { "4x4_8bit_cmyk.psd", PsdColorMode.Cmyk, 8, 1 },
        { "4x4_16bit_cmyk.psd", PsdColorMode.Cmyk, 16, 1 },
        { "4x4_8bit_lab.psd", PsdColorMode.Lab, 8, 1 },
        { "4x4_16bit_lab.psd", PsdColorMode.Lab, 16, 1 },
        { "4x4_8bit_duotone.psd", PsdColorMode.Duotone, 8, 1 },
        { "4x4_8bit_index_color.psd", PsdColorMode.Indexed, 8, 0 },
        { "4x4_16bit_multichannel.psd", PsdColorMode.Multichannel, 16, 0 },
        { "4x4_1bit_bitmap.psd", PsdColorMode.Bitmap, 1, 0 },
        { "20x5_1bit_bitmap.psd", PsdColorMode.Bitmap, 1, 0 },
        { "100x20_1bit_bitmap_rle.psd", PsdColorMode.Bitmap, 1, 0 },
    };

    [Theory]
    [MemberData(nameof(ColorModes))]
    public void Color_mode_fixtures_load_render_and_match_their_merged_image(string name, PsdColorMode mode, int depth, int minimumLayers)
    {
        var document = PsdDocument.Load(Fixtures.PsdTools(Path.Combine("colormodes", name)));

        Assert.Equal(mode, document.ColorMode);
        Assert.Equal(depth, document.Depth);
        Assert.True(document.Layers.Count >= minimumLayers);
        var text = document.ExtractText();
        foreach (var layer in document.Layers)
        {
            Assert.Contains(text.Items, item => item.Kind is PsdTextKind.LayerName or PsdTextKind.GroupName && item.Text == layer.Name);
        }

        var merged = document.Render(new RenderOptions { Source = RenderSource.MergedImage });
        Assert.Equal(document.Width, merged.Width);
        Assert.Equal(document.Height, merged.Height);
        Assert.True(merged.Pixels.Any(b => b is not (0 or 255)) || mode == PsdColorMode.Bitmap || merged.Pixels.Distinct().Count() > 1, "merged image decoded to a flat buffer");

        // In the 8-bit files the gradient fill layer carries baked pixels and the
        // compositor must reproduce Photoshop's composite. The 16 and 32-bit files
        // store the fill without pixels, so it is regenerated from its GdFl
        // descriptor, which does not match yet (TODO.md, Rendering gaps).
        if (document.Layers.Count > 0 && depth == 8)
        {
            var layers = document.Render(new RenderOptions { Source = RenderSource.Layers });
            var diff = ImageTools.Compare(layers, merged);
            Assert.True(diff.MaxDelta <= 2, $"{name}: layers vs merged {diff}");
        }
    }

    [Theory]
    [InlineData("4x4_16bit_rgb.psd", "4x4_8bit_rgb.psd", 2)]
    [InlineData("4x4_16bit_grayscale.psd", "4x4_8bit_grayscale.psd", 2)]
    [InlineData("4x4_16bit_cmyk.psd", "4x4_8bit_cmyk.psd", 4)]
    [InlineData("4x4_16bit_lab.psd", "4x4_8bit_lab.psd", 4)]
    public void Sixteen_bit_merged_images_match_the_8_bit_saves(string deep, string shallow, int maxDelta)
    {
        // The same document saved at both depths: the 16-bit merged image must
        // decode to the 8-bit one within rounding (the ink and Lab conversions
        // round once more, hence the looser bound there).
        var a = PsdDocument.Load(Fixtures.PsdTools(Path.Combine("colormodes", deep))).Render(new RenderOptions { Source = RenderSource.MergedImage });
        var b = PsdDocument.Load(Fixtures.PsdTools(Path.Combine("colormodes", shallow))).Render(new RenderOptions { Source = RenderSource.MergedImage });

        var diff = ImageTools.Compare(a, b);
        Assert.True(diff.MaxDelta <= maxDelta, $"{deep} vs {shallow}: {diff}");
    }

    [Fact]
    public void Embedded_psb_smart_object_is_parsed_and_its_text_extracted()
    {
        var document = PsdDocument.Load(Fixtures.PsdTools("smart-object-slice.psd"));

        var layer = Assert.Single(document.Layers, l => l.Kind == PsdLayerKind.SmartObject);
        var file = Assert.Single(document.LinkedFiles);
        Assert.Equal(layer.SmartObjectFileId, file.Id);
        Assert.True(file.IsPhotoshopDocument);

        var inner = PsdDocument.Load(file.Data);
        Assert.True(inner.IsLargeDocument);
        Assert.NotEmpty(inner.Layers);
        Assert.Equal(inner.Width, inner.Render().Width);

        // Text extraction recurses into the embedded PSB.
        var text = document.ExtractText();
        foreach (var innerLayer in inner.Layers)
        {
            Assert.Contains(text.Items, item => item.Kind is PsdTextKind.LayerName or PsdTextKind.GroupName && item.Text == innerLayer.Name && item.Depth > 0);
        }
    }

    [Fact]
    public void Layer_comps_resource_reads_names_comments_and_capture_flags()
    {
        var comps = PsdDocument.Load(Fixtures.PsdTools("layer_comps.psd")).Resources.LayerComps;

        Assert.NotNull(comps);
        Assert.Collection(
            comps.Comps,
            c => Assert.Equal(("Cheeky", "Emoticon with tongue", true, true, true), (c.Name, c.Comment, c.CapturesVisibility, c.CapturesPosition, c.CapturesAppearance)),
            c => Assert.Equal(("Scary", "Scary emoticon"), (c.Name, c.Comment)));
        Assert.Equal(comps.Comps[0].Id, comps.LastAppliedCompId);
    }

    [Fact]
    public void Guides_read_in_pixels_with_their_orientation()
    {
        var grid = PsdDocument.Load(Fixtures.PsdTools("metadata.psd")).Resources.GridAndGuides;

        Assert.NotNull(grid);
        Assert.Equal(18, grid.HorizontalGridCycle);
        Assert.Equal(
            [new(18, PsdGuideOrientation.Horizontal), new(114, PsdGuideOrientation.Horizontal), new(50, PsdGuideOrientation.Vertical), new(50, PsdGuideOrientation.Horizontal), new(1887 / 32.0, PsdGuideOrientation.Horizontal)],
            grid.Guides);
    }

    [Fact]
    public void Version_6_slices_read_every_record()
    {
        var slices = PsdDocument.Load(Fixtures.PsdTools("slices.psd")).Resources.Slices;

        Assert.NotNull(slices);
        Assert.Equal(10, slices.Slices.Count);
        var user = slices.Slices.Where(s => s.Origin == PsdSliceOrigin.UserGenerated).ToList();
        Assert.Equal(["slices_06", "Slice 1", "slices_04"], user.Select(s => s.Name));
        Assert.Equal(new PsdRect(133, 70, 201, 138), user[0].Bounds);
        Assert.Equal(("http://example.com", "target", "message", "alt_tag"), (user[0].Url, user[0].Target, user[0].Message, user[0].AltTag));
    }
}
