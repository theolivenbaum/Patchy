using Patchy.Psd.Layers;
using Patchy.Psd.Tests.Support;

namespace Patchy.Psd.Tests;

public sealed class ParsingTests
{
    public static TheoryData<string> AllFixtures()
    {
        var data = new TheoryData<string>();
        foreach (var name in Fixtures.AllDocuments())
        {
            data.Add(name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void Every_fixture_parses_and_decodes_its_layers(string name)
    {
        var document = Fixtures.Load(name);

        Assert.InRange(document.Width, 1, 30000);
        Assert.InRange(document.Height, 1, 30000);
        Assert.Equal(name.EndsWith(".psb", StringComparison.Ordinal), document.IsLargeDocument);
        foreach (var layer in document.Layers)
        {
            Assert.False(string.IsNullOrEmpty(layer.Name));
            if (!layer.IsGroup && !layer.Bounds.IsEmpty)
            {
                var pixels = layer.GetPixels();
                Assert.NotNull(pixels);
                Assert.Equal(layer.Bounds.Width, pixels.Width);
            }
        }

        var merged = document.GetMergedImage();
        Assert.Equal(document.Width, merged.Width);
        Assert.Equal(document.Height, merged.Height);
    }

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void Every_fixture_survives_truncation(string name)
    {
        var bytes = File.ReadAllBytes(Fixtures.PathOf(name));
        foreach (var fraction in new[] { 0.1, 0.5, 0.9 })
        {
            var cut = bytes.AsMemory(0, (int)(bytes.Length * fraction));
            try
            {
                var document = PsdDocument.Load(cut);
                _ = document.Render();
                _ = document.ExtractText();
            }
            catch (PsdFormatException)
            {
                // Rejecting a damaged file is fine; any other exception type is a bug.
            }
        }
    }

    [Fact]
    public void Rejects_non_psd_data()
    {
        Assert.Throws<PsdFormatException>(() => PsdDocument.Load(new byte[64]));
        Assert.False(PsdDocument.IsPsd("GIF89a"u8));
        Assert.True(PsdDocument.IsPsd("8BPS"u8));
    }

    [Fact]
    public void Group_tree_follows_section_dividers()
    {
        var document = Fixtures.Load("photoshop-group-opacity.psd");

        Assert.Equal(10, document.Layers.Count);
        var groups = document.Layers.Where(l => l.IsGroup).ToList();
        Assert.NotEmpty(groups);
        foreach (var group in groups)
        {
            Assert.NotEmpty(group.Children);
            Assert.All(group.Children, child => Assert.Same(group, child.Parent));
        }

        // Every non-root layer is reachable from the roots exactly once.
        var reachable = new List<PsdLayer>();
        void Walk(IEnumerable<PsdLayer> layers)
        {
            foreach (var layer in layers)
            {
                reachable.Add(layer);
                Walk(layer.Children);
            }
        }

        Walk(document.RootLayers);
        Assert.Equal(document.Layers.Count, reachable.Distinct().Count());
        Assert.Equal(document.Layers.Count, document.EnumerateLayersTopDown().Count());
    }

    [Fact]
    public void Psb_documents_parse_wide_lengths()
    {
        var document = Fixtures.Load("photoshop-basic.psb");
        Assert.True(document.IsLargeDocument);
        Assert.Equal(40, document.Width);
        Assert.Equal(30, document.Height);
        Assert.Equal(2, document.Layers.Count);
        Assert.NotNull(document.Layers[^1].GetPixels());
    }

    [Fact]
    public void Layer_records_expose_kinds_masks_and_blocks()
    {
        var shape = Fixtures.Load("photoshop-shape-solid.psd");
        var fill = shape.Layers.Single(l => l.Kind == PsdLayerKind.Fill);
        Assert.Equal("SoCo", fill.ContentKey);
        Assert.NotNull(fill.FillColor);
        Assert.NotNull(fill.VectorMask);
        Assert.NotEmpty(fill.VectorMask.Subpaths);

        var masks = Fixtures.Load("photoshop-both-masks.psd");
        var masked = masks.Layers.Single(l => l.Name == "Layer 1");
        Assert.NotNull(masked.Mask);
        Assert.NotNull(masked.VectorMask);

        var adjustments = Fixtures.Load("photoshop-curves-masked.psd");
        Assert.Contains(adjustments.Layers, l => l.Kind == PsdLayerKind.Adjustment && l.ContentKey == "curv");

        var clipping = Fixtures.Load("photoshop-clipping-mask.psd");
        Assert.Equal(2, clipping.Layers.Count(l => l.IsClipped));
        Assert.Equal(PsdBlendMode.Multiply, clipping.Layers.Single(l => l.Name == "Clip Multiply").BlendMode);
    }

    [Fact]
    public void Smart_object_layers_link_to_embedded_files()
    {
        var document = Fixtures.Load("photoshop-place-embedded-png.psd");
        var layer = document.Layers.Single(l => l.Kind == PsdLayerKind.SmartObject);
        var file = Assert.Single(document.LinkedFiles);
        Assert.Equal("small.png", file.FileName);
        Assert.Equal(layer.SmartObjectFileId, file.Id);
        Assert.Equal("png", file.FileType);
        Assert.True(file.Data.Span.StartsWith(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }));
    }

    [Fact]
    public void Text_layers_carry_type_tool_data()
    {
        var document = Fixtures.Load("photoshop-text-tracking.psd");
        var texts = document.Layers.Where(l => l.Kind == PsdLayerKind.Text).ToList();
        Assert.Equal(2, texts.Count);
        foreach (var layer in texts)
        {
            Assert.NotNull(layer.Text);
            Assert.Equal("HHHHHHHHHH", layer.Text.Text);
            Assert.Contains("ArialMT", layer.Text.Fonts);
            Assert.NotEmpty(layer.Text.StyleRuns);
            Assert.NotNull(layer.Text.EngineData);
            Assert.Equal(6, layer.Text.Transform.Count);
        }

        // The two layers differ only in tracking.
        var tracking = texts.Select(l => l.Text!.StyleRuns[0].Tracking ?? 0).Distinct().ToList();
        Assert.Equal(2, tracking.Count);
    }

    [Fact]
    public void Cmyk_documents_decode()
    {
        var document = Fixtures.Load("photoshop-cmyk-style-colors.psd");
        Assert.Equal(PsdColorMode.Cmyk, document.ColorMode);
        var image = document.Render(new Rendering.RenderOptions { Source = Rendering.RenderSource.Layers });
        Assert.Equal(document.Width, image.Width);
        Assert.NotNull(document.Layers[0].GetPixels());
    }

    [Fact]
    public void Image_resources_are_exposed()
    {
        var document = Fixtures.Load("photoshop-saved-paths.psd");
        Assert.NotNull(document.GetImageResource(ImageResourceIds.VersionInfo));
        Assert.Equal(2, document.ImageResources.Count(r => r.Id is >= ImageResourceIds.PathFirst and <= ImageResourceIds.PathLast));
    }
}
