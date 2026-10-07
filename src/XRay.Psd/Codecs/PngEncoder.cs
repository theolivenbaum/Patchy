using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using XRay.Psd.Imaging;

namespace XRay.Psd.Codecs;

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
    /// <summary>Filtered data above twice this size deflates in parallel segments of about this size.</summary>
    private const int DeflateSegmentBytes = 1 << 20;

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

        // Filter every row in parallel (a row's filter depends only on the raw row and
        // the raw row above), then deflate.
        var height = image.Height;
        var filteredStride = stride + 1;
        var filtered = new byte[checked(filteredStride * height)];
        Parallelism.For(height, stride * 5L, (start, end) =>
        {
            var previous = new byte[stride];
            var current = new byte[stride];
            var candidate = new byte[filteredStride];
            if (start > 0)
            {
                PackRow(image, start - 1, withAlpha, previous);
            }

            for (var y = start; y < end; y++)
            {
                PackRow(image, y, withAlpha, current);
                FilterRow(current, previous, channels, candidate, filtered.AsSpan(y * filteredStride, filteredStride));
                (previous, current) = (current, previous);
            }
        });

        WriteChunk(output, "IDAT", Compress(filtered, filteredStride, level));
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
                    for (var i = 0; i < bpp; i++)
                    {
                        body[i] = (byte)(row[i] - (previous[i] >> 1));
                    }

                    AverageFilter(row[bpp..], row, previous[bpp..], body[bpp..]);
                    break;
                default:
                    for (var i = 0; i < bpp; i++)
                    {
                        body[i] = (byte)(row[i] - previous[i]);
                    }

                    PaethFilter(row[bpp..], row, previous[bpp..], previous, body[bpp..]);
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

    // destination[i] = x[i] - floor((left[i] + up[i]) / 2). The encoder knows every raw
    // byte, so the filter has no serial dependency; floor((a + b) / 2) = (a & b) + ((a ^ b) >> 1).
    private static void AverageFilter(ReadOnlySpan<byte> x, ReadOnlySpan<byte> left, ReadOnlySpan<byte> up, Span<byte> destination)
    {
        var i = 0;
        var width = Vector<byte>.Count;
        if (Vector.IsHardwareAccelerated)
        {
            for (; i <= x.Length - width; i += width)
            {
                var a = new Vector<byte>(left[i..]);
                var b = new Vector<byte>(up[i..]);
                var average = (a & b) + Vector.ShiftRightLogical(a ^ b, 1);
                (new Vector<byte>(x[i..]) - average).CopyTo(destination[i..]);
            }
        }

        for (; i < x.Length; i++)
        {
            destination[i] = (byte)(x[i] - ((left[i] + up[i]) >> 1));
        }
    }

    // destination[i] = x[i] - Paeth(left[i], up[i], upLeft[i]), on 16-bit lanes.
    private static void PaethFilter(ReadOnlySpan<byte> x, ReadOnlySpan<byte> left, ReadOnlySpan<byte> up, ReadOnlySpan<byte> upLeft, Span<byte> destination)
    {
        var i = 0;
        var width = Vector<byte>.Count;
        if (Vector.IsHardwareAccelerated)
        {
            for (; i <= x.Length - width; i += width)
            {
                Vector.Widen(new Vector<byte>(left[i..]), out var a0, out var a1);
                Vector.Widen(new Vector<byte>(up[i..]), out var b0, out var b1);
                Vector.Widen(new Vector<byte>(upLeft[i..]), out var c0, out var c1);
                var predicted = Vector.Narrow(
                    PaethLanes(Vector.AsVectorInt16(a0), Vector.AsVectorInt16(b0), Vector.AsVectorInt16(c0)),
                    PaethLanes(Vector.AsVectorInt16(a1), Vector.AsVectorInt16(b1), Vector.AsVectorInt16(c1)));
                (new Vector<byte>(x[i..]) - predicted).CopyTo(destination[i..]);
            }
        }

        for (; i < x.Length; i++)
        {
            destination[i] = (byte)(x[i] - Paeth(left[i], up[i], upLeft[i]));
        }
    }

    // The Paeth predictor per lane: pa = |b - c|, pb = |a - c|, pc = |a + b - 2c|; ties prefer a, then b.
    private static Vector<ushort> PaethLanes(Vector<short> a, Vector<short> b, Vector<short> c)
    {
        var pa = Vector.Abs(b - c);
        var pb = Vector.Abs(a - c);
        var pc = Vector.Abs(a + b - c - c);
        var useA = Vector.LessThanOrEqual(pa, pb) & Vector.LessThanOrEqual(pa, pc);
        var useB = Vector.LessThanOrEqual(pb, pc);
        return Vector.AsVectorUInt16(Vector.ConditionalSelect(useA, a, Vector.ConditionalSelect(useB, b, c)));
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    // Sum of filtered bytes interpreted as signed magnitudes (the libpng heuristic):
    // v < 128 ? v : 256 - v, which is |(sbyte)v| on widened lanes.
    private static long Score(ReadOnlySpan<byte> filtered)
    {
        long sum = 0;
        var i = 0;
        var width = Vector<byte>.Count;
        if (Vector.IsHardwareAccelerated)
        {
            var total = Vector<int>.Zero;
            for (; i <= filtered.Length - width; i += width)
            {
                Vector.Widen(Vector.AsVectorSByte(new Vector<byte>(filtered[i..])), out var low, out var high);
                Vector.Widen(Vector.Abs(low) + Vector.Abs(high), out var low32, out var high32);
                total += low32 + high32;
            }

            sum = Vector.Sum(total);
        }

        for (; i < filtered.Length; i++)
        {
            var value = filtered[i];
            sum += value < 128 ? value : 256 - value;
        }

        return sum;
    }

    /// <summary>
    /// Wraps the filtered rows in one zlib stream. Large images split into segments of
    /// whole rows (about 1 MiB each, a size fixed by the image, not the thread count)
    /// that deflate in parallel: every segment but the last ends with a sync flush, so
    /// the concatenation is one valid deflate stream (the pigz layout). The Adler-32 of
    /// the whole input is combined from the per-segment checksums. The output bytes are
    /// the same for every degree of parallelism.
    /// </summary>
    private static byte[] Compress(byte[] filtered, int filteredStride, PngCompressionLevel level)
    {
        var compression = level switch
        {
            PngCompressionLevel.Fastest => CompressionLevel.Fastest,
            PngCompressionLevel.Smallest => CompressionLevel.SmallestSize,
            _ => CompressionLevel.Optimal,
        };

        if (filtered.Length <= 2 * DeflateSegmentBytes)
        {
            using var single = new MemoryStream();
            using (var zlib = new ZLibStream(single, compression, leaveOpen: true))
            {
                zlib.Write(filtered);
            }

            return single.ToArray();
        }

        var segmentBytes = Math.Max(1, DeflateSegmentBytes / filteredStride) * filteredStride;
        var segmentCount = (filtered.Length + segmentBytes - 1) / segmentBytes;
        var segments = new byte[segmentCount][];
        var checksums = new uint[segmentCount];
        Parallelism.For(segmentCount, segmentBytes, (start, end) =>
        {
            for (var segment = start; segment < end; segment++)
            {
                var offset = segment * segmentBytes;
                var data = filtered.AsSpan(offset, Math.Min(segmentBytes, filtered.Length - offset));
                checksums[segment] = Adler32.Compute(data);
                var last = segment == segmentCount - 1;
                using var buffer = new MemoryStream();
                var keep = 0L;
                using (var deflate = new DeflateStream(buffer, compression, leaveOpen: true))
                {
                    deflate.Write(data);
                    if (!last)
                    {
                        // Sync flush: byte-aligned output without the final-block bit.
                        // Disposing then appends a final block, which is cut off below.
                        deflate.Flush();
                        keep = buffer.Length;
                    }
                }

                segments[segment] = last ? buffer.ToArray() : buffer.GetBuffer().AsSpan(0, (int)keep).ToArray();
            }
        });

        var adler = checksums[0];
        for (var segment = 1; segment < segmentCount; segment++)
        {
            var length = Math.Min(segmentBytes, filtered.Length - (segment * segmentBytes));
            adler = Adler32.Combine(adler, checksums[segment], length);
        }

        var total = 2 + 4;
        foreach (var segment in segments)
        {
            total += segment.Length;
        }

        var result = new byte[total];
        result[0] = 0x78;
        result[1] = level switch
        {
            PngCompressionLevel.Fastest => 0x01,
            PngCompressionLevel.Smallest => 0xDA,
            _ => 0x9C,
        };
        var position = 2;
        foreach (var segment in segments)
        {
            segment.CopyTo(result, position);
            position += segment.Length;
        }

        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(position), adler);
        return result;
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

    private static class Adler32
    {
        private const uint Modulus = 65521;

        // zlib's NMAX: the longest run whose sums cannot overflow 32 bits before the modulo.
        private const int Block = 5552;

        public static uint Compute(ReadOnlySpan<byte> data)
        {
            uint a = 1;
            uint b = 0;
            while (data.Length > 0)
            {
                var chunk = data[..Math.Min(Block, data.Length)];
                foreach (var value in chunk)
                {
                    a += value;
                    b += a;
                }

                a %= Modulus;
                b %= Modulus;
                data = data[chunk.Length..];
            }

            return (b << 16) | a;
        }

        /// <summary>The checksum of two concatenated inputs from their checksums (zlib <c>adler32_combine</c>).</summary>
        public static uint Combine(uint first, uint second, long secondLength)
        {
            var remainder = (uint)(secondLength % Modulus);
            var sum1 = first & 0xFFFF;
            var sum2 = (uint)((ulong)remainder * sum1 % Modulus);
            sum1 += (second & 0xFFFF) + Modulus - 1;
            sum2 += ((first >> 16) & 0xFFFF) + ((second >> 16) & 0xFFFF) + Modulus - remainder;
            if (sum1 >= Modulus)
            {
                sum1 -= Modulus;
            }

            if (sum1 >= Modulus)
            {
                sum1 -= Modulus;
            }

            if (sum2 >= Modulus << 1)
            {
                sum2 -= Modulus << 1;
            }

            if (sum2 >= Modulus)
            {
                sum2 -= Modulus;
            }

            return sum1 | (sum2 << 16);
        }
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
