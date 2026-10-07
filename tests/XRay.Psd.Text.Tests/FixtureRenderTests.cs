using XRay.Psd.Layers;
using XRay.Psd.Rendering;
using XRay.Psd.Tests.Support;
using XRay.Psd.Text.Tests.Support;

namespace XRay.Psd.Text.Tests;

/// <summary>
/// Re-renders the committed Photoshop type fixtures with the bundled Liberation Sans (metric
/// compatible with Arial, so advances, line breaks and placement should match while glyph
/// outlines differ slightly) and compares with the pixels Photoshop stored for each layer.
/// Measured values are in docs/text-rendering.md; the bounds here leave a small margin.
/// </summary>
public sealed class FixtureRenderTests : IDisposable
{
    private readonly TextLayerRenderer _renderer = new(TestFonts.Settings());

    public void Dispose() => _renderer.Dispose();

    public static TheoryData<string, int, double, int> ArialLayers => new()
    {
        // fixture, type layer index (top-down), minimum ink IoU, maximum ink-box edge delta (px).
        // The warp fixtures use Smooth anti-aliasing, which Photoshop draws with heavier stems
        // (coverage 0.77 to 0.84 against 0.92 to 0.97 for the others); the coverage check allows it.
        { "photoshop-text-anchor-whole.psd", 0, 0.75, 2 },
        { "photoshop-text-anchor-half.psd", 0, 0.75, 2 },
        { "photoshop-text-anchor-center-whole.psd", 0, 0.75, 2 },
        { "photoshop-text-anchor-center-third.psd", 0, 0.75, 2 },
        { "photoshop-text-anchor-center90-whole.psd", 0, 0.70, 1 },
        { "photoshop-text-anchor-center90-third.psd", 0, 0.70, 1 },
        { "photoshop-text-box-auto-leading.psd", 0, 0.75, 1 },
        { "photoshop-text-point-auto-leading.psd", 0, 0.75, 1 },
        { "photoshop-text-point-fixed-leading.psd", 0, 0.75, 1 },
        { "photoshop-text-point-transformed.psd", 0, 0.72, 1 },
        { "photoshop-text-hv-scale.psd", 0, 0.55, 1 },
        { "photoshop-text-tracking.psd", 0, 0.75, 1 },
        { "photoshop-text-tracking.psd", 1, 0.72, 1 },
        { "photoshop-warp-text.psd", 0, 0.65, 1 },
        { "photoshop-warp-text.psd", 1, 0.70, 1 },
        { "qual_rca_pinout.psd", 0, 0.62, 2 },
        { "qual_rca_pinout.psd", 1, 0.70, 1 },
        { "qual_rca_pinout.psd", 2, 0.68, 2 },
        { "qual_rca_pinout.psd", 3, 0.74, 1 },
        { "qual_rca_pinout.psd", 4, 0.60, 3 },
        { "qual_rca_pinout.psd", 5, 0.52, 3 },
    };

    [Theory]
    [MemberData(nameof(ArialLayers))]
    public void Arial_layers_match_the_Photoshop_raster(string fixture, int index, double minIou, int maxEdge)
    {
        var layer = TypeLayers(fixture)[index];
        Assert.Empty(_renderer.FindMissingFonts(layer.Text!));

        var raster = _renderer.Render(layer);

        Assert.NotNull(raster);
        var match = TextComparison.Compare(layer, raster);
        Assert.True(match.Iou >= minIou, $"{fixture}[{index}] {match}");
        Assert.True(match.MaxEdgeDelta <= maxEdge, $"{fixture}[{index}] {match}");
        Assert.InRange(match.CoverageRatio, layer.Text!.AntiAlias == TextAntiAlias.Smooth ? 0.72 : 0.88, 1.1);
    }

    [Fact]
    public void Fill_color_comes_from_the_style_run()
    {
        var layer = TypeLayers("qual_rca_pinout.psd")[1];
        var raster = _renderer.Render(layer)!;
        var expected = layer.Text!.StyleRuns[0].FillColor!.Value;

        var solid = OpaquePixels(raster).ToList();

        Assert.NotEmpty(solid);
        Assert.All(solid, c => Assert.Equal((expected.R, expected.G, expected.B), (c.R, c.G, c.B)));
    }

    [Fact]
    public void Half_pixel_anchor_moves_the_raster_like_Photoshop()
    {
        // Photoshop rounds the line start (halves up): x 100.0 and 100.5 render one column apart.
        var whole = TypeLayers("photoshop-text-anchor-whole.psd")[0];
        var half = TypeLayers("photoshop-text-anchor-half.psd")[0];
        var wholeMatch = TextComparison.Compare(whole, _renderer.Render(whole)!);
        var halfMatch = TextComparison.Compare(half, _renderer.Render(half)!);

        Assert.Equal(halfMatch.StoredInk.Left - wholeMatch.StoredInk.Left, halfMatch.RenderedInk.Left - wholeMatch.RenderedInk.Left);
        Assert.Equal(1, halfMatch.RenderedInk.Left - wholeMatch.RenderedInk.Left);
    }

    [Fact]
    public void Centered_anchor_rounds_the_line_start_not_the_anchor()
    {
        // x 100.3 centered: the line start 69.32 -> 69 and 69.62 -> 70 (text-render-calibration.md).
        var whole = TypeLayers("photoshop-text-anchor-center-whole.psd")[0];
        var third = TypeLayers("photoshop-text-anchor-center-third.psd")[0];
        var wholeInk = TextComparison.Compare(whole, _renderer.Render(whole)!);
        var thirdInk = TextComparison.Compare(third, _renderer.Render(third)!);

        Assert.Equal(thirdInk.StoredInk.Left - wholeInk.StoredInk.Left, thirdInk.RenderedInk.Left - wholeInk.RenderedInk.Left);
    }

    [Fact]
    public void Tracking_adds_size_times_tracking_per_gap()
    {
        // 10 glyphs at 24 px with tracking 200: 9 gaps of 4.8 px.
        var layers = TypeLayers("photoshop-text-tracking.psd");
        var tracked = layers.Single(l => l.Text!.StyleRuns[0].Tracking == 200);
        var plain = layers.Single(l => l.Text!.StyleRuns[0].Tracking == 0);
        var trackedInk = TextComparison.Compare(tracked, _renderer.Render(tracked)!);
        var plainInk = TextComparison.Compare(plain, _renderer.Render(plain)!);

        var renderedGrowth = trackedInk.RenderedInk.Width - plainInk.RenderedInk.Width;
        var storedGrowth = trackedInk.StoredInk.Width - plainInk.StoredInk.Width;
        Assert.InRange(renderedGrowth, 43.2 - 1, 43.2 + 1);
        Assert.InRange(renderedGrowth, storedGrowth - 1, storedGrowth + 1);
    }

    [Fact]
    public void Fixed_leading_spaces_lines_by_the_leading()
    {
        // Three lines: auto leading 1.2 x 24 = 28.8 per line, fixed leading 40.
        var auto = TypeLayers("photoshop-text-point-auto-leading.psd")[0];
        var fixedLayer = TypeLayers("photoshop-text-point-fixed-leading.psd")[0];
        var autoInk = TextComparison.Compare(auto, _renderer.Render(auto)!).RenderedInk;
        var fixedInk = TextComparison.Compare(fixedLayer, _renderer.Render(fixedLayer)!).RenderedInk;

        Assert.Equal(autoInk.Top, fixedInk.Top);
        Assert.InRange(fixedInk.Bottom - autoInk.Bottom, (2 * (40 - 28.8)) - 1, (2 * (40 - 28.8)) + 1);
    }

    [Fact]
    public void Box_text_wraps_at_the_frame_and_starts_at_the_typographic_ascender()
    {
        // "HHHH HHHH HHHH xxxx" at 24 px in a 200 px frame wraps to two lines (the stored raster's height).
        var layer = TypeLayers("photoshop-text-box-auto-leading.psd")[0];
        var layout = _renderer.Layout(layer.Text!);
        var baselines = layout.Glyphs.Select(g => Math.Round(g.F, 3)).Distinct().Order().ToList();

        Assert.Equal(2, baselines.Count);
        Assert.Equal(0.728 * 24, baselines[0], 0.05); // OS/2 sTypoAscender of Arial and Liberation Sans: 1491/2048
        Assert.Equal(28.8, baselines[1] - baselines[0], 1e-6);
    }

    [Fact]
    public void Right_to_left_paragraph_ends_at_the_anchor()
    {
        // Hebrew in a right-to-left paragraph, justification "left" (its start side): the line ends at x 340.
        // Liberation's Hebrew glyphs are wider than Arial's, so only the right edge is pinned.
        var layer = TypeLayers("photoshop-text-rtl-hebrew.psd")[0];
        var match = TextComparison.Compare(layer, _renderer.Render(layer)!);

        Assert.InRange(match.Right, -1, 1);
        Assert.InRange(match.Bottom, -2, 2);

        // Visual order: the first logical letter (shin) is drawn rightmost, left of the anchor.
        var layout = _renderer.Layout(layer.Text!);
        using var font = new SkiaSharp.SKFont(TestFonts.LiberationSans);
        var rightmost = layout.Glyphs.MaxBy(g => g.E);
        Assert.Equal(font.GetGlyph(0x05E9), rightmost.Glyph);
        Assert.True(rightmost.E < 0);
    }

    [Fact]
    public void Rotated_roman_vertical_text_stays_on_its_column()
    {
        // MyriadPro is not bundled: the fallback face is wider, so the column runs longer, but the
        // column axis and the first cell are placed by the vertical model.
        var layer = TypeLayers("photoshop-text-vertical-rotated-roman.psd")[0];
        Assert.Equal(["MyriadPro-Regular"], _renderer.FindMissingFonts(layer.Text!));

        var match = TextComparison.Compare(layer, _renderer.Render(layer)!);

        Assert.InRange(match.Left, -1, 1);
        Assert.InRange(match.Right, -1, 1);
        Assert.InRange(match.Top, -1, 1);
    }

    [Theory]
    [InlineData("photoshop-text-vertical-point.psd", 3)]
    [InlineData("photoshop-text-vertical-box.psd", 3)]
    public void Vertical_cjk_columns_match_the_Photoshop_raster(string fixture, int maxEdge)
    {
        // Needs a CJK face in local-test-fixtures/fonts (MS Gothic itself, or any CJK font: cells are em sized).
        Assert.SkipUnless(TestFonts.HasLocalGlyph(0x7E26), "Copy a CJK font into local-test-fixtures/fonts to run this test.");
        using var renderer = new TextLayerRenderer(TestFonts.Settings(localGlyphs: true));
        var layer = TypeLayers(fixture)[0];

        var match = TextComparison.Compare(layer, renderer.Render(layer)!);

        Assert.True(match.MaxEdgeDelta <= maxEdge, match.ToString());
        Assert.True(match.Iou >= 0.3, match.ToString());
    }

    [Fact]
    public void Every_type_layer_renders_with_the_fallback_face()
    {
        foreach (var name in Fixtures.AllDocuments())
        {
            foreach (var layer in Fixtures.Load(name).EnumerateLayersTopDown().Where(l => l.Text is not null))
            {
                var raster = _renderer.Render(layer);
                var hasLatin = layer.Text!.Text.Any(char.IsAsciiLetterOrDigit);
                if (hasLatin)
                {
                    Assert.NotNull(raster);
                    Assert.True(raster.Bounds.Intersect(layer.Document.Bounds) == raster.Bounds, $"{name} [{layer.Name}] raster leaves the canvas");
                }
            }
        }
    }

    private static List<PsdLayer> TypeLayers(string fixture) =>
        [.. Fixtures.Load(fixture).EnumerateLayersTopDown().Where(l => l.Text is not null)];

    private static IEnumerable<PsdColor> OpaquePixels(TextLayerRaster raster)
    {
        var pixels = raster.Image.Pixels;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i + 3] == 255)
            {
                yield return new PsdColor(pixels[i], pixels[i + 1], pixels[i + 2]);
            }
        }
    }
}
