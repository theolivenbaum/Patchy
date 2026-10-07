using XRay.Psd.Rendering;
using XRay.Psd.Tests.Support;

namespace XRay.Psd.Tests;

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
        var reference = ImageTools.ReadBmp(Fixtures.ReferenceBmpOf(name));

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
    [InlineData("photoshop-shape-strokes.psd", 40, 0.2)]

    // Mask density and feather, Advanced Blending channel restrictions with Fill on a
    // special mode.
    [InlineData("photoshop-user-mask-params.psd", 6, 0.3)]
    [InlineData("photoshop-vector-mask-feather.psd", 5, 0.6)]
    [InlineData("photoshop-channel-restrictions.psd", 1, 0.01)]
    [InlineData("photoshop-blend-if-4b-roundtrip.psd", 2, 0.6)]
    [InlineData("photoshop-shape-feather.psd", 4, 0.5)]

    // Layer effects (drop shadow, glows, inner shadow, overlays, strokes, satin,
    // effects on groups and clipping bases).
    [InlineData("photoshop-size-zero-effects.psd", 1, 0.01)]
    [InlineData("photoshop-inner-shadow.psd", 2, 0.01)]
    [InlineData("photoshop-outer-glow-range.psd", 2, 0.1)]
    [InlineData("photoshop-shadow-conceals.psd", 2, 0.05)]
    [InlineData("photoshop-inner-glow-range.psd", 5, 0.1)]
    [InlineData("photoshop-gradient-overlay-geometry.psd", 6, 0.1)]
    [InlineData("photoshop-group-fx-blend-fill.psd", 1, 0.01)]
    [InlineData("photoshop-group-fx-interior.psd", 1, 0.01)]
    [InlineData("photoshop-group-fx-passthrough.psd", 1, 0.01)]
    [InlineData("photoshop-group-fx-mask-stroke.psd", 30, 0.05)]
    [InlineData("photoshop-overlay-zorder.psd", 14, 0.8)]
    [InlineData("photoshop-outer-glow.psd", 72, 0.5)]
    [InlineData("photoshop-inner-glow.psd", 70, 0.25)]
    [InlineData("photoshop-stroke-overprint.psd", 52, 0.1)]
    [InlineData("photoshop-stroke-shapeburst.psd", 125, 0.1)]
    [InlineData("photoshop-stroke-aa-matte.psd", 90, 0.4)]
    [InlineData("photoshop-clip-base-effects.psd", 130, 1.2)]

    // Patterns (overlay effect and fill layers), gradient fill layers, bevel and emboss.
    [InlineData("photoshop-pattern-overlay.psd", 1, 0.01)]
    [InlineData("photoshop-pattern-anchor.psd", 1, 0.01)]
    [InlineData("photoshop-pattern-transparent.psd", 1, 0.01)]
    [InlineData("photoshop-pattern-scale.psd", 34, 2.0)]
    [InlineData("photoshop-shape-pattern.psd", 8, 0.1)]
    [InlineData("photoshop-shape-gradient.psd", 10, 1.3)]
    [InlineData("photoshop-bevel-smooth.psd", 4, 0.05)]
    [InlineData("photoshop-emboss-styles.psd", 30, 0.15)]
    [InlineData("photoshop-pillow-emboss.psd", 75, 0.1)]
    [InlineData("photoshop-pillow-emboss2.psd", 70, 0.12)]
    [InlineData("photoshop-bevel-texture-ramp.psd", 30, 0.5)]
    [InlineData("photoshop-bevel-texture-clouds.psd", 96, 1.3)]
    [InlineData("photoshop-gloss-contour.psd", 192, 0.9)]
    public void Layer_compositor_matches_photoshop_capture(string name, int maxDelta, double meanDelta)
    {
        var document = Fixtures.Load(name);
        var reference = ImageTools.ReadBmp(Fixtures.ReferenceBmpOf(name));

        var rendered = document.Render(new RenderOptions { Source = RenderSource.Layers });
        var diff = ImageTools.Compare(rendered, reference);

        Assert.True(diff.MaxDelta <= maxDelta && diff.MeanDelta <= meanDelta, $"{name}: {diff}");
    }

    /// <summary>Fixtures without a capture whose real merged image the compositor reproduces.</summary>
    [Theory]
    [InlineData("photoshop-pattern-deep.psd", 1, 0.01)]
    [InlineData("patchy-gradient-empty-transparency.psd", 1, 0.01)]
    [InlineData("photoshop-bevel-default.psd", 3, 0.1)]
    [InlineData("arrows.psd", 120, 0.4)]
    [InlineData("patchy-open-path-strokes.psd", 1, 0.01)]
    public void Layer_compositor_matches_real_merged_image(string name, int maxDelta, double meanDelta)
    {
        var document = Fixtures.Load(name);
        Assert.True(document.HasRealMergedImage);

        var diff = ImageTools.Compare(document.Render(new RenderOptions { Source = RenderSource.Layers }), document.GetMergedImage());

        Assert.True(diff.MaxDelta <= maxDelta && diff.MeanDelta <= meanDelta, $"{name}: {diff}");
    }

    /// <summary>
    /// Photoshop (CS4 here) stores the merged image of a transparent document matted
    /// against white. Unmatted, its soft edges match the layer compositor's straight
    /// color wherever the two agree on alpha; read as straight color they are up to
    /// 188 levels too light.
    /// </summary>
    [Fact]
    public void Merged_image_of_a_transparent_document_is_unmatted_from_white()
    {
        var document = Fixtures.Load("arrows.psd");
        Assert.True(document.MergedImageHasTransparency);
        var merged = document.GetMergedImage();
        var layers = document.Render(new RenderOptions { Source = RenderSource.Layers });

        var compared = 0;
        var worst = 0;
        for (var y = 0; y < document.Height; y++)
        {
            for (var x = 0; x < document.Width; x++)
            {
                var m = merged.GetPixel(x, y);
                var l = layers.GetPixel(x, y);
                if (m.A < 64 || m.A == 255 || Math.Abs(m.A - l.A) > 2)
                {
                    continue;
                }

                compared++;
                worst = Math.Max(worst, Math.Max(Math.Abs(m.R - l.R), Math.Max(Math.Abs(m.G - l.G), Math.Abs(m.B - l.B))));
            }
        }

        Assert.True(compared > 400, $"only {compared} soft-edge pixels");
        Assert.True(worst <= 12, $"worst soft-edge color delta {worst}");
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
