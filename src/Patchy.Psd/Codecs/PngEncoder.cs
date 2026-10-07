using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using Patchy.Psd.Imaging;

namespace Patchy.Psd.Codecs;

/// <summary>Compression effort for <see cref="PngEncoder"/>.</summary>
public enum PngCompressionLevel
{
    Fastest,
    Default,
    Smallest,
}

/// <summary>
/// Dependency-free PNG writer (8-bit RGB/RGBA, non-interlaced) with per-row
/// adaptive filtering (minimum sum of absolute differences).
/// </summary>
public static class PngEncoder
{
    private static readonly byte[] Signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    public static byte[] Encode(RgbaImage image, bool dropAlphaIfOpaque = true, PngCompressionLevel level = PngCompressionLevel.Default)
    {
        ArgumentNullException.ThrowIfNull(image);
        var withAlpha = !(dropAlphaIfOpaque && image.IsOpaque);
        var channels = withAlpha ? 4 : 3;
        var stride = image.Width * channels;

        using var output = new MemoryStream();
        output.Write(Signature);

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)image.Width);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], (uint)image.Height);
        header[8] = 8;
        header[9] = (byte)(withAlpha ? 6 : 2);
        header[10] = 0;
        header[11] = 0;
        header[12] = 0;
        WriteChunk(output, "IHDR", header);

        using (var compressed = new MemoryStream())
        {
            var compression = level switch
            {
                PngCompressionLevel.Fastest => CompressionLevel.Fastest,
                PngCompressionLevel.Smallest => CompressionLevel.SmallestSize,
                _ => CompressionLevel.Optimal,
            };
            using (var zlib = new ZLibStream(compressed, compression, leaveOpen: true))
            {
                var previous = new byte[stride];
                var current = new byte[stride];
                var candidate = new byte[stride + 1];
                var best = new byte[stride + 1];
                for (var y = 0; y < image.Height; y++)
                {
                    PackRow(image, y, withAlpha, current);
                    FilterRow(current, previous, channels, candidate, best);
                    zlib.Write(best);
                    (previous, current) = (current, previous);
                }
            }

            WriteChunk(output, "IDAT", compressed.GetBuffer().AsSpan(0, (int)compressed.Length));
        }

        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void PackRow(RgbaImage image, int y, bool withAlpha, Span<byte> row)
    {
        var source = image.Pixels.AsSpan(y * image.Width * 4, image.Width * 4);
        if (withAlpha)
        {
            source.CopyTo(row);
            return;
        }

        for (int x = 0, s = 0, d = 0; x < image.Width; x++, s += 4, d += 3)
        {
            row[d] = source[s];
            row[d + 1] = source[s + 1];
            row[d + 2] = source[s + 2];
        }
    }

    private static void FilterRow(ReadOnlySpan<byte> row, ReadOnlySpan<byte> previous, int bpp, Span<byte> candidate, Span<byte> best)
    {
        var bestScore = long.MaxValue;
        for (byte filter = 0; filter <= 4; filter++)
        {
            candidate[0] = filter;
            var body = candidate[1..];
            switch (filter)
            {
                case 0:
                    row.CopyTo(body);
                    break;
                case 1:
                    row[..bpp].CopyTo(body);
                    Subtract(row[bpp..], row, body[bpp..]);
                    break;
                case 2:
                    Subtract(row, previous, body);
                    break;
                case 3:
                    for (var i = 0; i < row.Length; i++)
                    {
                        var left = i >= bpp ? row[i - bpp] : 0;
                        body[i] = (byte)(row[i] - ((left + previous[i]) >> 1));
                    }

                    break;
                default:
                    for (var i = 0; i < row.Length; i++)
                    {
                        int a = i >= bpp ? row[i - bpp] : 0;
                        int b = previous[i];
                        int c = i >= bpp ? previous[i - bpp] : 0;
                        body[i] = (byte)(row[i] - Paeth(a, b, c));
                    }

                    break;
            }

            var score = Score(body);
            if (score < bestScore)
            {
                bestScore = score;
                candidate.CopyTo(best);
            }
        }
    }

    // destination[i] = a[i] - b[i], SIMD over bytes (wrapping).
    private static void Subtract(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, Span<byte> destination)
    {
        var i = 0;
        var width = Vector<byte>.Count;
        for (; i <= a.Length - width; i += width)
        {
            (new Vector<byte>(a[i..]) - new Vector<byte>(b[i..])).CopyTo(destination[i..]);
        }

        for (; i < a.Length; i++)
        {
            destination[i] = (byte)(a[i] - b[i]);
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    // Sum of filtered bytes interpreted as signed magnitudes (the libpng heuristic).
    private static long Score(ReadOnlySpan<byte> filtered)
    {
        long sum = 0;
        foreach (var value in filtered)
        {
            sum += value < 128 ? value : 256 - value;
        }

        return sum;
    }

    private static void WriteChunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, (uint)data.Length);
        for (var i = 0; i < 4; i++)
        {
            buffer[4 + i] = (byte)type[i];
        }

        output.Write(buffer);
        output.Write(data);
        var crc = Crc32.Update(Crc32.Update(0xFFFFFFFFu, buffer[4..]), data) ^ 0xFFFFFFFFu;
        BinaryPrimitives.WriteUInt32BigEndian(buffer, crc);
        output.Write(buffer[..4]);
    }

    private static class Crc32
    {
        private static readonly uint[] Table = CreateTable();

        private static uint[] CreateTable()
        {
            var table = new uint[256];
            for (var n = 0u; n < 256; n++)
            {
                var c = n;
                for (var k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                }

                table[n] = c;
            }

            return table;
        }

        public static uint Update(uint crc, ReadOnlySpan<byte> data)
        {
            foreach (var b in data)
            {
                crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            }

            return crc;
        }
    }
}
