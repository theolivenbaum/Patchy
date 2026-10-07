using XRay.Psd.Imaging;
using XRay.Psd.Layers;
using XRay.Psd.Rendering;
using XRay.Psd.Tests.Support;
using XRay.Psd.Text.Tests.Support;

namespace XRay.Psd.Text.Tests;

/// <summary><see cref="RenderOptions.TextRasterizer"/> plugged into the core compositor.</summary>
public sealed class CompositorIntegrationTests : IDisposable
{
    private readonly TextLayerRasterizer _rasterizer = new(TestFonts.Settings());

    public void Dispose() => _rasterizer.Dispose();

    [Fact]
    public void Type_layer_without_pixels_renders_from_its_text()
    {
        var document = PsdDocument.Load(TypeLayerPsd.Build("Hello", "ArialMT", 24, (20, 50), color: (200, 10, 30)));
        var layer = Assert.Single(document.Layers);
        Assert.Equal(PsdLayerKind.Text, layer.Kind);
        Assert.Null(layer.GetPixels());

        var plain = document.Render(new RenderOptions { Source = RenderSource.Layers });
        var rerendered = document.Render(new RenderOptions { Source = RenderSource.Layers, TextRasterizer = _rasterizer });

        Assert.Equal(0, CountInk(plain));
        var ink = InkBounds(rerendered);
        Assert.InRange(ink.Left, 20, 23);
        Assert.InRange(ink.Bottom, 50, 51);
        Assert.Contains(Opaque(rerendered), c => c == new PsdColor(200, 10, 30));
    }

    [Fact]
    public void Stored_pixels_win_unless_asked_to_rerender()
    {
        var document = Fixtures.Load("photoshop-text-tracking.psd");
        var counting = new CountingRasterizer(_rasterizer);

        var plain = document.Render(new RenderOptions { Source = RenderSource.Layers });
        var missingOnly = document.Render(new RenderOptions { Source = RenderSource.Layers, TextRasterizer = counting });

        Assert.Equal(0, counting.Calls);
        Assert.Equal(plain.Pixels, missingOnly.Pixels);
    }

    [Fact]
    public void Always_mode_rerenders_every_type_layer_and_skips_the_merged_image()
    {
        var document = Fixtures.Load("photoshop-text-point-auto-leading.psd");
        var counting = new CountingRasterizer(_rasterizer);

        var layers = document.Render(new RenderOptions { Source = RenderSource.Layers });
        var rerendered = document.Render(new RenderOptions { TextRasterizer = counting, TextRasterMode = TextRasterMode.Always });

        Assert.Equal(1, counting.Calls);
        Assert.NotEqual(layers.Pixels, rerendered.Pixels);
        Assert.True(MeanDifference(layers, rerendered) < 3, MeanDifference(layers, rerendered).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Rerendered_text_keeps_the_layer_effects()
    {
        // The qual_rca labels carry layer effects; the compositor applies them to the re-rendered glyphs.
        var document = Fixtures.Load("qual_rca_pinout.psd");

        var layers = document.Render(new RenderOptions { Source = RenderSource.Layers });
        var rerendered = document.Render(new RenderOptions { Source = RenderSource.Layers, TextRasterizer = _rasterizer, TextRasterMode = TextRasterMode.Always });

        Assert.True(MeanDifference(layers, rerendered) < 2);
    }

    [Fact]
    public void Every_fixture_renders_with_text_rerendering()
    {
        var options = new RenderOptions { Source = RenderSource.Layers, TextRasterizer = _rasterizer, TextRasterMode = TextRasterMode.Always };
        foreach (var name in Fixtures.AllDocuments())
        {
            var document = Fixtures.Load(name);
            var image = document.Render(options);
            Assert.Equal((document.Width, document.Height), (image.Width, image.Height));
        }
    }

    [Fact]
    public void Truncated_type_fixtures_still_render()
    {
        var options = new RenderOptions { Source = RenderSource.Layers, TextRasterizer = _rasterizer, TextRasterMode = TextRasterMode.Always };
        foreach (var name in new[] { "photoshop-text-box-auto-leading.psd", "photoshop-warp-text.psd", "photoshop-text-vertical-box.psd" })
        {
            var bytes = File.ReadAllBytes(Fixtures.PathOf(name));
            for (var length = bytes.Length / 8; length < bytes.Length; length += bytes.Length / 8)
            {
                PsdDocument document;
                try
                {
                    document = PsdDocument.Load(bytes.AsSpan(0, length).ToArray());
                }
                catch (PsdFormatException)
                {
                    continue;
                }

                try
                {
                    _ = document.Render(options);
                }
                catch (PsdFormatException)
                {
                }
            }
        }
    }

    private static double MeanDifference(RgbaImage a, RgbaImage b)
    {
        long sum = 0;
        for (var i = 0; i < a.Pixels.Length; i++)
        {
            sum += Math.Abs(a.Pixels[i] - b.Pixels[i]);
        }

        return (double)sum / a.Pixels.Length;
    }

    private static int CountInk(RgbaImage image)
    {
        var count = 0;
        for (var i = 3; i < image.Pixels.Length; i += 4)
        {
            if (image.Pixels[i] != 0)
            {
                count++;
            }
        }

        return count;
    }

    private static PsdRect InkBounds(RgbaImage image)
    {
        var ink = default(PsdRect);
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                if (image.GetPixel(x, y).A >= 128)
                {
                    ink = ink.Union(new PsdRect(x, y, x + 1, y + 1));
                }
            }
        }

        return ink;
    }

    private static IEnumerable<PsdColor> Opaque(RgbaImage image)
    {
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                var c = image.GetPixel(x, y);
                if (c.A == 255)
                {
                    yield return c;
                }
            }
        }
    }

    private sealed class CountingRasterizer(ITextLayerRasterizer inner) : ITextLayerRasterizer
    {
        public int Calls { get; private set; }

        public TextLayerRaster? Rasterize(PsdLayer layer)
        {
            Calls++;
            return inner.Rasterize(layer);
        }
    }
}
