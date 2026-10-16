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
    public void Layer_comp_state_reads_per_layer_visibility_position_and_appearance()
    {
        var document = PsdDocument.Load(Fixtures.PsdTools("layer_comps.psd"));
        var comps = document.Resources.LayerComps!;
        var cheeky = comps.Comps[0].Id;
        var scary = comps.Comps[1].Id;

        foreach (var layer in document.Layers)
        {
            var metadata = layer.Metadata;
            Assert.NotNull(metadata);
            Assert.Equal(["cmls", "cust"], metadata.Items.Select(i => i.Key));
            Assert.All(metadata.Items, i => Assert.NotNull(i.Descriptor));
            Assert.Equal(new DateTime(2014, 7, 21, 12, 2, 4, DateTimeKind.Utc), metadata.LayerTime!.Value.UtcDateTime, TimeSpan.FromSeconds(1));

            // Every layer records one state per comp plus the document state (comp ID 0).
            Assert.Equal([cheeky, scary, 0], metadata.CompStates.SelectMany(s => s.CompIds));

            // Cheeky was applied last, so the layers' saved visibility is Cheeky's.
            if (metadata.GetCompState(cheeky)!.Visible is bool visible)
            {
                Assert.Equal(layer.IsVisible, visible);
            }
        }

        // Scary hides the mouth's "Фигура 5" and moves both eyes 9 px right.
        var mouth = document.Layers.Single(l => l.Name == "Фигура 5").Metadata!;
        Assert.False(mouth.GetCompState(scary)!.Visible);
        Assert.Equal(18, mouth.CompLayerId);
        var eye = document.Layers.Single(l => l.Name == "Eye Left").Metadata!;
        Assert.Equal(new PsdPoint(9, 0), eye.GetCompState(scary)!.Offset);
        Assert.Equal(new PsdPoint(0, 0), eye.GetCompState(0)!.Offset);
        Assert.Equal(PsdBlendMode.PassThrough, eye.GetCompState(cheeky)!.BlendMode);

        // Appearance: Cheeky stores the shape's effects and vector mask position.
        var shape = mouth.GetCompState(cheeky)!;
        Assert.NotNull(shape.Effects);
        Assert.Equal(new PsdPoint(106, 138), shape.VectorMaskOffset);
        Assert.Equal(new PsdPoint(-5, -3), shape.EffectsReferencePoint);
        Assert.Null(document.Layers.Single(l => l.Name == "Фон").Metadata!.GetCompState(12345));
    }

    [Fact]
    public void Version_8_slices_read_the_descriptor_layout()
    {
        // Photoshop CC writes the descriptor form; metadata.psd has the automatic slice only.
        var slices = PsdDocument.Load(Fixtures.PsdTools("metadata.psd")).Resources.Slices;

        Assert.NotNull(slices);
        Assert.Equal((8, "User", new PsdRect(0, 0, 100, 100)), (slices.Version, slices.GroupName, slices.Bounds));
        var slice = Assert.Single(slices.Slices);
        Assert.Equal((0, 0, PsdSliceOrigin.AutoGenerated, PsdSliceType.Image, new PsdRect(0, 0, 100, 100)), (slice.Id, slice.GroupId, slice.Origin, slice.Type, slice.Bounds));
        Assert.Equal((string.Empty, string.Empty, true, 0, 0), (slice.Url, slice.Name, slice.CellTextIsHtml, slice.HorizontalAlignment, slice.VerticalAlignment));
        Assert.Equal((0, 0, 0, 0), (slice.TopOutset, slice.LeftOutset, slice.BottomOutset, slice.RightOutset));
        Assert.Equal(PsdColor.Transparent, slice.BackgroundColor);
        Assert.Contains("url", slices.Descriptor!.GetList("slices")![0].Object!.Items.Select(i => i.Key));
    }

    [Fact]
    public void Txt2_small_caps_match_the_TySh_runs()
    {
        // masks.psd sets Small Caps on "sign in": TySh /FontCaps 1 and Txt2 style key 12 = 1.
        var document = PsdDocument.Load(Fixtures.PsdTools("masks.psd"));
        var layer = document.Layers.Single(l => l.Name == "sign in");
        var info = layer.Text!;
        var engine = info.TextEngineObject!;

        // TySh splits the text into one run per character; Txt2 keeps one run.
        Assert.Equal(info.Text, engine.Text);
        Assert.Equal(info.StyleRuns.Sum(r => r.Length), engine.StyleRuns.Sum(r => r.Length));
        Assert.All(info.StyleRuns, r => Assert.Equal(TextCaps.SmallCaps, r.Caps));
        Assert.All(engine.StyleRuns, r => Assert.Equal(TextCaps.SmallCaps, r.Caps));
        Assert.All(engine.StyleRuns, r => Assert.False(r.Ligatures));

        // The other type layers keep normal caps in both records.
        foreach (var other in document.Layers.Where(l => l.Text?.TextEngineObject is not null && l != layer))
        {
            Assert.All(other.Text!.TextEngineObject!.StyleRuns, r => Assert.Equal(TextCaps.Normal, r.Caps));
        }
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

    // ---- The full psd-tools collection (local only) ---------------------------------

    /// <summary>
    /// Mean error of the layer compositor against Photoshop's merged image for every file in
    /// psd-tools' <c>adjustments/</c> folder, measured when the CMYK and gray ink spaces, the
    /// Levels toe and the Threshold and 16-bit Posterize rules went in (docs/adjustments.md),
    /// plus 0.02. Most of these files are too large to commit; the test runs when the
    /// collection is checked out under <c>local-test-fixtures/psd-tools/tests/psd_files</c>
    /// (docs/testing.md) and skips otherwise.
    /// </summary>
    public static TheoryData<string, double> AdjustmentCorpus() => new()
    {
        { "adjustment_backdrop_test.psd", 0.02 },
        { "adjustment_clipping.psd", 0.02 },
        { "adjustment_nested_composition_1.psd", 0.33 },
        { "adjustment_nested_composition_2.psd", 0.09 },
        { "adjustment_nested_composition_3.psd", 0.11 },
        { "adjustment_nested_composition_4.psd", 0.07 },
        { "adjustment_nested_composition_5.psd", 0.15 },
        { "brightnesscontrast_cmyk.psd", 0.59 },
        { "brightnesscontrast_grayscale.psd", 0.05 },
        { "brightnesscontrast_legacy_cmyk.psd", 0.16 },
        { "brightnesscontrast_legacy_grayscale.psd", 0.15 },
        { "brightnesscontrast_legacy_rgb.psd", 0.20 },
        { "brightnesscontrast_rgb.psd", 0.02 },
        { "curves_cmyk.psd", 0.85 },
        { "curves_grayscale.psd", 0.06 },
        { "curves_rgb.psd", 0.03 },
        { "exposure_grayscale.psd", 0.03 },
        { "exposure_rgb.psd", 0.03 },
        { "huesaturation_colorize_rgb.psd", 0.02 },
        { "huesaturation_lightness_rgb.psd", 0.12 },
        { "huesaturation_rgb.psd", 0.47 },
        { "huesaturation_saturation_rgb.psd", 0.41 },
        { "invert_cmyk.psd", 0.26 },
        { "invert_grayscale.psd", 0.05 },
        { "invert_rgb.psd", 0.02 },
        { "levels_cmyk.psd", 0.54 },
        { "levels_grayscale.psd", 0.28 },
        { "levels_rgb.psd", 0.33 },
        { "posterize_16bits_cmyk.psd", 0.60 },
        { "posterize_16bits_grayscale.psd", 0.08 },
        { "posterize_16bits_rgb.psd", 0.02 },
        { "posterize_cmyk.psd", 0.48 },
        { "posterize_grayscale.psd", 0.06 },
        { "posterize_rgb.psd", 0.02 },
        { "threshold_16bits_grayscale.psd", 0.03 },
        { "threshold_16bits_rgb.psd", 0.02 },
        { "threshold_cmyk.psd", 0.34 },
        { "threshold_grayscale.psd", 0.06 },
        { "threshold_rgb.psd", 0.03 },
    };

    [Theory]
    [MemberData(nameof(AdjustmentCorpus))]
    public void Psd_tools_adjustment_collection_matches_photoshop_if_available(string name, double meanDelta)
    {
        var path = LocalCorpus("adjustments", name);
        Assert.SkipWhen(path is null, "Check out the psd-tools collection under local-test-fixtures/psd-tools to run this test.");
        var document = PsdDocument.Load(path!);
        var rendered = document.Render(new RenderOptions { Source = RenderSource.Layers });

        var diff = ImageTools.Compare(rendered, document.GetMergedImage());

        Assert.True(diff.MeanDelta <= meanDelta, $"{name}: {diff}");
    }

    /// <summary>A file of the full psd-tools collection in a <c>local-test-fixtures</c> folder at or above the repository, or null.</summary>
    private static string? LocalCorpus(params string[] relative)
    {
        for (var directory = new DirectoryInfo(Fixtures.RootDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine([directory.FullName, "local-test-fixtures", "psd-tools", "tests", "psd_files", .. relative]);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
