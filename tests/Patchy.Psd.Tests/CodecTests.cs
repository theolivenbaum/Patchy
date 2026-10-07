using System.Buffers.Binary;
using Patchy.Psd.Codecs;
using Patchy.Psd.Imaging;
using Patchy.Psd.IO;
using Patchy.Psd.Tests.Support;

namespace Patchy.Psd.Tests;

public sealed class CodecTests
{
    private static RgbaImage Pattern(int width, int height, bool alpha)
    {
        var image = new RgbaImage(width, height);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                image.SetPixel(x, y, new PsdColor((byte)(x * 255 / Math.Max(1, width - 1)), (byte)(y * 3), (byte)((x ^ y) & 0xFF), alpha ? (byte)((x + y) % 256) : (byte)255));
            }
        }

        return image;
    }

    [Theory]
    [InlineData(1, 1, false)]
    [InlineData(37, 13, false)]
    [InlineData(64, 33, true)]
    public void Png_round_trips_exactly(int width, int height, bool alpha)
    {
        var image = Pattern(width, height, alpha);

        var png = image.ToPng();
        var decoded = PngDecoder.Decode(png);

        Assert.Equal(image.Pixels, decoded.Pixels);
        Assert.Equal(alpha ? 6 : 2, png[25]); // IHDR color type
    }

    [Fact]
    public void Png_of_a_rendered_fixture_round_trips()
    {
        var image = Fixtures.Load("photoshop-shape-boolean.psd").Render();
        Assert.Equal(image.Pixels, PngDecoder.Decode(image.ToPng()).Pixels);
    }

    [Theory]
    [InlineData(95)]
    [InlineData(75)]
    [InlineData(10)]
    public void Jpeg_has_valid_structure(int quality)
    {
        var image = Pattern(70, 45, alpha: false);

        var jpeg = image.ToJpeg(quality);

        Assert.Equal(0xFF, jpeg[0]);
        Assert.Equal(0xD8, jpeg[1]);
        Assert.Equal(0xFF, jpeg[^2]);
        Assert.Equal(0xD9, jpeg[^1]);
        var sof = jpeg.AsSpan().IndexOf(new byte[] { 0xFF, 0xC0 });
        Assert.True(sof > 0);
        Assert.Equal(45, BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(sof + 5)));
        Assert.Equal(70, BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(sof + 7)));
        Assert.Equal(quality >= 90 ? 0x11 : 0x22, jpeg[sof + 11]);
        Assert.Equal(4, CountMarkers(jpeg, 0xC4));
    }

    [Fact]
    public void Jpeg_quality_trades_size()
    {
        var image = Fixtures.Load("qual_rca_pinout.psd").Render();
        var high = image.ToJpeg(95).Length;
        var low = image.ToJpeg(40).Length;
        Assert.True(low < high, $"q40 {low} bytes vs q95 {high} bytes");
    }

    [Fact]
    public void Save_picks_format_by_extension()
    {
        var directory = Path.Combine(Path.GetTempPath(), "patchy-psd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var image = Pattern(8, 8, alpha: false);
            image.Save(Path.Combine(directory, "a.png"));
            image.Save(Path.Combine(directory, "a.jpg"));
            Assert.Equal(0x89, File.ReadAllBytes(Path.Combine(directory, "a.png"))[0]);
            Assert.Equal(0xFF, File.ReadAllBytes(Path.Combine(directory, "a.jpg"))[0]);
            Assert.Throws<NotSupportedException>(() => image.Save(Path.Combine(directory, "a.gif")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void PackBits_round_trips()
    {
        var random = new Random(1234);
        for (var trial = 0; trial < 200; trial++)
        {
            var row = new byte[random.Next(1, 400)];
            for (var i = 0; i < row.Length; i++)
            {
                row[i] = random.Next(4) == 0 ? (byte)random.Next(256) : (byte)(i / 17);
            }

            var packed = PsdBuilder.PackBits(row);
            var unpacked = new byte[row.Length];
            Assert.True(ChannelCodec.UnpackBits(packed, unpacked));
            Assert.Equal(row, unpacked);
        }
    }

    [Fact]
    public void UnpackBits_reports_damaged_input()
    {
        var destination = new byte[4];
        Assert.False(ChannelCodec.UnpackBits([0x05, 1, 2], destination));
        Assert.False(ChannelCodec.UnpackBits([0xFE], destination));
    }

    [Fact]
    public void Simd_conversions_match_scalar()
    {
        var bytes = Enumerable.Range(0, 1000).Select(i => (byte)(i * 7)).ToArray();
        var floats = new float[bytes.Length];
        ChannelCodec.BytesToUnitFloat(bytes, floats);
        for (var i = 0; i < bytes.Length; i++)
        {
            Assert.Equal(bytes[i] / 255f, floats[i], 1e-6f);
        }

        var back = new byte[bytes.Length];
        PlanarImage.UnitFloatToBytes(floats, back);
        Assert.Equal(bytes, back);

        var shorts = Enumerable.Range(0, 999).Select(i => (ushort)(i * 65)).ToArray();
        var wide = new float[shorts.Length];
        ChannelCodec.UShortsToUnitFloat(shorts, wide);
        for (var i = 0; i < shorts.Length; i++)
        {
            Assert.Equal(shorts[i] / 65535f, wide[i], 1e-6f);
        }
    }

    private static int CountMarkers(byte[] data, byte marker)
    {
        var count = 0;
        for (var i = 0; i + 1 < data.Length; i++)
        {
            if (data[i] == 0xFF && data[i + 1] == marker)
            {
                count++;
            }
        }

        return count;
    }
}
