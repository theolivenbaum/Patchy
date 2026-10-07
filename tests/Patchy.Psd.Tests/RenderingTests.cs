using Patchy.Psd.Rendering;
using Patchy.Psd.Tests.Support;

namespace Patchy.Psd.Tests;

/// <summary>
/// Compares renders with Photoshop's own output: the merged image stored in the
/// file and the Photoshop-captured reference BMPs next to the fixtures.
/// </summary>
public sealed class RenderingTests
{
    /// <summary>Fixtures whose real merged image matches Photoshop's reference capture exactly.</summary>
    [Theory]
    [InlineData("photoshop-group-opacity.psd")]
    [InlineData("photoshop-bevel-subs.psd")]
    [InlineData("photoshop-gloss-contour.psd")]
    [InlineData("photoshop-inner-shadow.psd")]
    [InlineData("photoshop-overlay-zorder.psd")]
    [InlineData("photoshop-pattern-overlay.psd")]
    [InlineData("photoshop-saved-paths.psd")]
    public void Merged_image_matches_photoshop_capture(string name)
    {
        var document = Fixtures.Load(name);
        Assert.True(document.HasRealMergedImage);
        var reference = ImageTools.ReadBmp(Path.ChangeExtension(Fixtures.PathOf(name), ".bmp"));

        var diff = ImageTools.Compare(document.GetMergedImage(), reference);

        Assert.Equal(0, diff.MaxDelta);
    }

    /// <summary>
    /// Fixtures exercising features the layer compositor implements: groups and
    /// group opacity, raster and vector masks, boolean shape paths, solid fills,
    /// text rasters. The tolerance covers anti-aliasing differences at path edges.
    /// </summary>
    [Theory]
    [InlineData("photoshop-group-opacity.psd", 1, 0.01)]
    [InlineData("photoshop-both-masks.psd", 3, 0.2)]
    [InlineData("photoshop-both-masks-params.psd", 3, 0.2)]
    [InlineData("photoshop-shape-boolean.psd", 1, 0.01)]
    [InlineData("photoshop-shape-first-ops.psd", 1, 0.01)]
    [InlineData("photoshop-shape-solid.psd", 12, 0.2)]
    [InlineData("photoshop-shape-live-rect.psd", 8, 0.2)]
    [InlineData("photoshop-vector-mask-on-pixel.psd", 12, 0.2)]
    [InlineData("photoshop-compound-text.psd", 10, 0.3)]
    [InlineData("patchy-compound-group.psd", 8, 0.2)]
    public void Layer_compositor_matches_photoshop_capture(string name, int maxDelta, double meanDelta)
    {
        var document = Fixtures.Load(name);
        var reference = ImageTools.ReadBmp(Path.ChangeExtension(Fixtures.PathOf(name), ".bmp"));

        var rendered = document.Render(new RenderOptions { Source = RenderSource.Layers });
        var diff = ImageTools.Compare(rendered, reference);

        Assert.True(diff.MaxDelta <= maxDelta && diff.MeanDelta <= meanDelta, $"{name}: {diff}");
    }

    /// <summary>Adjustment layers the compositor implements, against the merged image.</summary>
    [Theory]
    [InlineData("photoshop-invert.psd")]
    [InlineData("photoshop-threshold.psd")]
    [InlineData("photoshop-posterize.psd")]
    public void Supported_adjustments_match_merged_image(string name)
    {
        var document = Fixtures.Load(name);
        var merged = document.GetMergedImage();

        var rendered = document.Render(new RenderOptions { Source = RenderSource.Layers });

        Assert.Equal(0, ImageTools.Compare(rendered, merged).MaxDelta);
    }

    [Fact]
    public void Auto_source_prefers_real_merged_image_and_falls_back_to_layers()
    {
        var real = Fixtures.Load("photoshop-gloss-contour.psd");
        Assert.Equal(0, ImageTools.Compare(real.Render(), real.GetMergedImage()).MaxDelta);

        var placeholder = Fixtures.Load("photoshop-both-masks.psd");
        Assert.False(placeholder.HasRealMergedImage);
        var auto = placeholder.Render();
        var layers = placeholder.Render(new RenderOptions { Source = RenderSource.Layers });
        Assert.Equal(0, ImageTools.Compare(auto, layers).MaxDelta);
    }

    [Fact]
    public void Background_option_flattens_to_opaque()
    {
        var document = Fixtures.Load("photoshop-place-embedded-png.psd");
        var image = document.Render(new RenderOptions { Background = new PsdColor(10, 20, 30) });
        Assert.True(image.IsOpaque);
    }

    [Fact]
    public void Layer_visibility_override_renders_a_subset()
    {
        var document = Fixtures.Load("photoshop-both-masks.psd");
        var onlyBackground = document.Render(new RenderOptions { LayerVisibility = l => l.Name == "Background" });
        var background = document.Layers.Single(l => l.Name == "Background").GetPixels()!;
        Assert.Equal(0, ImageTools.Compare(onlyBackground, background).MaxDelta);

        var single = document.Layers.Single(l => l.Name == "Layer 2").Render();
        Assert.Equal(document.Width, single.Width);
        Assert.False(single.IsOpaque);
    }

    [Fact]
    public void Real_world_document_renders_from_layers()
    {
        var document = Fixtures.Load("qual_rca_pinout.psd");
        var image = document.Render();
        Assert.Equal(1745, image.Width);
        Assert.True(image.IsOpaque);

        // The white "12345678910" type layer is drawn over the dark connector.
        var text = document.Layers.Single(l => l.Name == "12345678910");
        Assert.NotNull(text.Text);
        var bright = 0;
        for (var y = text.Bounds.Top; y < text.Bounds.Bottom; y++)
        {
            for (var x = text.Bounds.Left; x < text.Bounds.Right; x++)
            {
                var pixel = image.GetPixel(x, y);
                bright += pixel.R > 200 && pixel.G > 200 && pixel.B > 200 ? 1 : 0;
            }
        }

        Assert.InRange(bright, text.Bounds.Width * text.Bounds.Height / 20, text.Bounds.Width * text.Bounds.Height);
    }
}
