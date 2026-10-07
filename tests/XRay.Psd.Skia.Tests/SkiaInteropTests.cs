using XRay.Psd.Imaging;
using XRay.Psd.Tests.Support;
using SkiaSharp;

namespace XRay.Psd.Skia.Tests;

public sealed class SkiaInteropTests
{
    [Fact]
    public void Bitmap_round_trip_preserves_straight_alpha()
    {
        var image = new RgbaImage(3, 2);
        image.SetPixel(0, 0, new PsdColor(255, 0, 0));
        image.SetPixel(1, 0, new PsdColor(10, 200, 30, 128));
        image.SetPixel(2, 1, new PsdColor(1, 2, 3, 255));

        using var bitmap = image.ToSKBitmap();
        Assert.Equal(new SKColor(255, 0, 0, 255), bitmap.GetPixel(0, 0));
        Assert.Equal(image.Pixels, bitmap.ToRgbaImage().Pixels);
    }

    [Fact]
    public void Built_in_png_decodes_identically_in_skia()
    {
        var rendered = Fixtures.Load("photoshop-shape-live-rect.psd").Render();

        using var decoded = SKBitmap.Decode(rendered.ToPng());

        Assert.Equal(rendered.Pixels, decoded.ToRgbaImage().Pixels);
    }

    [Theory]
    [InlineData(95, 3.0)]
    [InlineData(75, 6.0)]
    public void Built_in_jpeg_decodes_in_skia_close_to_the_source(int quality, double maxMeanError)
    {
        var rendered = Fixtures.Load("qual_rca_pinout.psd").Render();

        using var decoded = SKBitmap.Decode(rendered.ToJpeg(quality));

        Assert.NotNull(decoded);
        Assert.Equal(rendered.Width, decoded.Width);
        Assert.Equal(rendered.Height, decoded.Height);
        var pixels = decoded.ToRgbaImage().Pixels;
        double error = 0;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            error += Math.Abs(pixels[i] - rendered.Pixels[i]) + Math.Abs(pixels[i + 1] - rendered.Pixels[i + 1]) + Math.Abs(pixels[i + 2] - rendered.Pixels[i + 2]);
        }

        var mean = error / (pixels.Length / 4 * 3);
        Assert.True(mean < maxMeanError, $"mean error {mean:F2} at quality {quality}");
    }

    [Fact]
    public void Skia_encoders_are_available()
    {
        var document = Fixtures.Load("photoshop-shape-boolean.psd");
        var webp = document.RenderAndEncode(SKEncodedImageFormat.Webp, 90);
        Assert.Equal("RIFF"u8.ToArray(), webp[..4]);
        using var bitmap = document.RenderToSKBitmap();
        Assert.Equal(document.Width, bitmap.Width);
    }
}
