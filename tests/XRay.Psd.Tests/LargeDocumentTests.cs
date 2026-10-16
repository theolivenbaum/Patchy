using System.Text;
using XRay.Psd.Rendering;

namespace XRay.Psd.Tests;

/// <summary>
/// A real PSB past every 32-bit limit: 50000 x 43000 pixels (more than one float plane
/// holds) with raw channels of 2.15 GB each and a raw merged image of 6.45 GB. The file is
/// sparse (only a few stripes are written), so it takes little disk, but rendering reads
/// all of it, which takes a minute or two. Set XRAY_PSD_LARGE=1 to run it; the file goes to
/// the temporary directory and is deleted afterwards.
/// </summary>
public sealed class LargeDocumentTests
{
    private const int Width = 50000;
    private const int Height = 43000;
    private const int StripeEvery = 4096;
    private const int StripeRow = 100;

    [Fact]
    public void Psb_past_2_gb_channels_renders_in_bands()
    {
        if (Environment.GetEnvironmentVariable("XRAY_PSD_LARGE") != "1")
        {
            return;
        }

        var path = Path.Combine(Path.GetTempPath(), $"xray-psd-large-{Environment.ProcessId}.psb");
        try
        {
            WriteSparsePsb(path);
            using var document = PsdDocument.Load(path);
            Assert.Equal(Width, document.Width);
            var layer = Assert.Single(document.Layers);
            Assert.All(layer.Channels, channel => Assert.True(channel.Data.IsEmpty && channel.LargeData is { Length: > int.MaxValue }));
            Assert.False(document.MergedImage.IsContiguous);

            // The whole-canvas path cannot hold this canvas and says so.
            Assert.Throws<PsdFormatException>(() => document.Render(new RenderOptions { Source = RenderSource.Layers }));

            foreach (var source in new[] { RenderSource.Layers, RenderSource.MergedImage })
            {
                var expected = source == RenderSource.Layers ? new PsdColor(200, 50, 25) : new PsdColor(30, 160, 90);
                var rows = 0;
                var peak = 0L;
                foreach (var band in document.RenderBands(new RenderOptions { Source = source, BandHeight = 256 }))
                {
                    for (var y = 0; y < band.Image.Height; y++)
                    {
                        var documentRow = band.Top + y;
                        var color = documentRow % StripeEvery == StripeRow ? expected : new PsdColor(0, 0, 0);
                        Assert.Equal(color, band.Image.GetPixel(0, y));
                        Assert.Equal(color, band.Image.GetPixel(Width / 2, y));
                        Assert.Equal(color, band.Image.GetPixel(Width - 1, y));
                    }

                    rows += band.Image.Height;
                    if (band.Top % (256 * 16) == 0)
                    {
                        // Live memory while a band is held (the band render allocates and drops planes freely).
                        peak = Math.Max(peak, GC.GetTotalMemory(forceFullCollection: true));
                    }
                }

                Assert.Equal(Height, rows);

                // One band of 256 x 50000 float planes is about 205 MB; nothing near the canvas size is held.
                Assert.True(peak < 1L << 30, $"live managed heap {peak >> 20} MB");
            }

            using var png = new CountingStream();
            document.RenderToPng(png, new RenderOptions { Source = RenderSource.MergedImage });
            Assert.True(png.Length > 1000);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Writes an 8-bit RGB PSB with one full-canvas layer and a merged image, every channel
    /// raw. Only the stripe rows are written; the rest of the file is a hole (zeros).
    /// </summary>
    private static void WriteSparsePsb(string path)
    {
        const long plane = (long)Width * Height;
        using var file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        var header = new List<byte>();
        void Bytes(params byte[] values) => header.AddRange(values);
        void U16(int value) => Bytes((byte)(value >> 8), (byte)value);
        void U32(long value) => Bytes((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value);
        void U64(long value)
        {
            U32(value >> 32);
            U32(value & 0xFFFFFFFF);
        }

        Bytes(Encoding.ASCII.GetBytes("8BPS"));
        U16(2);
        Bytes(0, 0, 0, 0, 0, 0);
        U16(3);
        U32(Height);
        U32(Width);
        U16(8);
        U16(3);
        U32(0);
        U32(0);

        // Layer record: rect, three raw channels, Normal, name "Large" padded to 4.
        var record = new List<byte>();
        void R(params byte[] values) => record.AddRange(values);
        void R32(long value) => R((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value);
        R(0, 1);
        R32(0);
        R32(0);
        R32(Height);
        R32(Width);
        R(0, 3);
        for (short c = 0; c < 3; c++)
        {
            R((byte)(c >> 8), (byte)c);
            R32(0);
            R32(plane + 2);
        }

        R(Encoding.ASCII.GetBytes("8BIMnorm"));
        R(255, 0, 0, 0);
        R32(4 + 4 + 8);
        R32(0);
        R32(0);
        R(5);
        R(Encoding.ASCII.GetBytes("Large"));
        R(0, 0);

        var layerData = 3 * (plane + 2);
        var layerInfoLength = record.Count + layerData;
        var padding = (4 - (layerInfoLength % 4)) % 4;
        layerInfoLength += padding;
        U64(8 + layerInfoLength + 4);
        U64(layerInfoLength);
        header.AddRange(record);
        file.Write(header.ToArray());

        var layerStart = header.Count;
        var mergedStart = layerStart + layerData + padding + 4;
        file.SetLength(mergedStart + 2 + (3 * plane));

        // Channel compression words (raw) and the stripes.
        var stripe = new byte[Width];
        byte[] layerColor = [200, 50, 25];
        byte[] mergedColor = [30, 160, 90];
        for (var c = 0; c < 3; c++)
        {
            var channelStart = layerStart + (c * (plane + 2));
            WriteAt(file, channelStart, [0, 0]);
            Array.Fill(stripe, layerColor[c]);
            for (var y = StripeRow; y < Height; y += StripeEvery)
            {
                WriteAt(file, channelStart + 2 + ((long)y * Width), stripe);
            }
        }

        WriteAt(file, mergedStart, [0, 0]);
        for (var c = 0; c < 3; c++)
        {
            Array.Fill(stripe, mergedColor[c]);
            for (var y = StripeRow; y < Height; y += StripeEvery)
            {
                WriteAt(file, mergedStart + 2 + (c * plane) + ((long)y * Width), stripe);
            }
        }

        // Layer info padding and the empty global mask info stay zero (part of the hole).
    }

    private static void WriteAt(FileStream file, long offset, byte[] data)
    {
        file.Position = offset;
        file.Write(data);
    }

    /// <summary>A write-only stream that only counts bytes.</summary>
    private sealed class CountingStream : Stream
    {
        private long _length;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => _length;

        public override long Position
        {
            get => _length;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => _length += count;

        public override void Write(ReadOnlySpan<byte> buffer) => _length += buffer.Length;
    }
}
