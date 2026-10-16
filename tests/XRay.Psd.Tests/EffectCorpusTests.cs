using XRay.Psd.Imaging;
using XRay.Psd.Rendering;
using XRay.Psd.Tests.Support;

namespace XRay.Psd.Tests;

/// <summary>
/// Blending and layer-effect behavior the reference does not model, calibrated
/// against Photoshop's merged images in psd-tools' test collection (MIT,
/// tests/fixtures/psd-tools): knockout, shadow and glow contours, gradient glows
/// and the Photoshop 5 <c>lrFX</c> effects. See docs/rendering.md.
/// </summary>
public sealed class EffectCorpusTests
{
    // Each file is a 32x32 stack: an opaque bottom layer (a Background layer unless
    // "nobg"), opaque red, then a group with Fill 50% holding opaque blue (nested files:
    // Outer { green, Inner { blue } }, Inner carrying the knockout). Before knockout was
    // modeled every one of them was off by 127 levels.
    [Theory]
    [InlineData("knockout-deep-normal.psd")]
    [InlineData("knockout-deep-passthrough.psd")]
    [InlineData("knockout-deep-cyanbg.psd")]
    [InlineData("knockout-deep-nobg.psd")]
    [InlineData("knockout-deep-nested.psd")]
    [InlineData("knockout-deep-nested-pt.psd")]
    [InlineData("knockout-shallow-nested.psd")]
    [InlineData("knockout-shallow-nested-pt.psd")]
    [InlineData("knockout-none-nested.psd")]
    public void Knockout_groups_match_photoshop(string name)
    {
        var document = PsdDocument.Load(Fixtures.PsdTools(Path.Combine("transparency", name)));
        var merged = document.Render(new RenderOptions { Source = RenderSource.MergedImage });
        var layers = document.Render(new RenderOptions { Source = RenderSource.Layers });

        var diff = ImageTools.Compare(layers, merged);
        Assert.True(diff.MaxDelta <= 1, $"{name}: layers vs merged {diff}");
        // Transparency too: deep knockout without a Background layer cuts to transparent.
        Assert.Equal(merged.GetPixel(16, 16), layers.GetPixel(16, 16));
    }

    [Fact]
    public void Shallow_knockout_layers_cut_through_their_isolated_or_pass_through_group()
    {
        // Four quadrants of Multiply ellipses (#CCCCCC) over a gradient rectangle:
        // isolated and pass-through groups, each with and without shallow knockout on
        // the ellipses. Knockout in an isolated group shows the ellipse color alone;
        // in a pass-through group each ellipse multiplies the rectangle only.
        var document = PsdDocument.Load(Fixtures.PsdTools(Path.Combine("transparency", "knockout-isolated-groups.psd")));
        var merged = document.Render(new RenderOptions { Source = RenderSource.MergedImage });
        var layers = document.Render(new RenderOptions { Source = RenderSource.Layers });

        foreach (var (x, y) in new[] { (64, 30), (64, 64), (192, 64), (64, 158), (64, 192), (192, 192) })
        {
            var expected = merged.GetPixel(x, y);
            var actual = layers.GetPixel(x, y);
            Assert.True(Math.Abs(expected.R - actual.R) <= 1 && Math.Abs(expected.G - actual.G) <= 1 && Math.Abs(expected.B - actual.B) <= 1, $"({x},{y}): {actual} vs {expected}");
        }

        // The rest is the ellipses' anti-aliased rims, as large in the quadrants without
        // knockout (mean 4.4 before knockout was modeled).
        var diff = ImageTools.Compare(layers, merged);
        Assert.True(diff.MeanDelta < 0.5, $"layers vs merged {diff}");
    }

    [Fact]
    public void Gradient_glow_with_a_custom_contour_matches_photoshop()
    {
        // layer_params.psd: a circle with a Hard Light outer glow (size 40, Range 43%)
        // painted with a three-stop gradient whose opacity falls from 100% to 10%,
        // through the custom contour (0,0) (164,104) (255,255) with anti-aliasing.
        // Compared in document space (Adobe RGB) on the glow's left side, away from
        // the drop shadow: mean 3.75 with a solid Linear glow, 3.05 with the gradient
        // but no contour, 0.43 with both.
        var document = PsdDocument.Load(Fixtures.PsdTools("layer_params.psd"), new PsdLoadOptions { ColorManagement = false });
        var merged = Crop(document.Render(new RenderOptions { Source = RenderSource.MergedImage }), 135, 280, 200, 490);
        var layers = Crop(document.Render(new RenderOptions { Source = RenderSource.Layers }), 135, 280, 200, 490);

        var diff = ImageTools.Compare(layers, merged);
        Assert.True(diff.MeanDelta < 0.6 && diff.MaxDelta <= 25, $"glow region {diff}");
    }

    [Fact]
    public void Layer_params_composite_improves_with_contours_and_gradient_glows()
    {
        // The whole document (a Gaussian-contour drop shadow on the caption as well),
        // color managed: mean 2.05 before contours and gradient glows, 0.95 after. The
        // rest is the caption's gradient stroke and pillow emboss, and Hard Light
        // blending in sRGB instead of the document's Adobe RGB.
        var document = PsdDocument.Load(Fixtures.PsdTools("layer_params.psd"));
        var merged = document.Render(new RenderOptions { Source = RenderSource.MergedImage });
        var layers = document.Render(new RenderOptions { Source = RenderSource.Layers });

        var diff = ImageTools.Compare(layers, merged);
        Assert.True(diff.MeanDelta < 1.1, $"layers vs merged {diff}");
    }

    private static RgbaImage Crop(RgbaImage image, int left, int top, int right, int bottom)
    {
        var crop = new RgbaImage(right - left, bottom - top);
        for (var y = top; y < bottom; y++)
        {
            for (var x = left; x < right; x++)
            {
                crop.SetPixel(x - left, y - top, image.GetPixel(x, y));
            }
        }

        return crop;
    }
}
