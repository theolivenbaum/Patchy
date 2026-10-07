using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Runtime.InteropServices;

namespace XRay.Psd.IO;

/// <summary>
/// Decompresses PSD channel image data (raw, PackBits RLE, ZIP, ZIP with
/// prediction) into big-endian sample bytes. Damaged rows decode as zeros
/// (or partially) rather than failing the whole file.
/// </summary>
internal static class ChannelCodec
{
    public static int RowBytes(int width, int depth) => depth switch
    {
        1 => (width + 7) / 8,
        8 => width,
        16 => width * 2,
        32 => width * 4,
        _ => throw new PsdFormatException($"Unsupported bit depth {depth}."),
    };

    /// <summary>
    /// Decodes a layer channel payload whose first two bytes are the compression
    /// method. Returns <c>height * RowBytes(width, depth)</c> big-endian bytes.
    /// </summary>
    public static byte[] DecodeLayerChannel(ReadOnlySpan<byte> payload, int width, int height, int depth, bool large)
    {
        var rowBytes = RowBytes(width, depth);
        var output = new byte[checked(rowBytes * height)];
        if (width <= 0 || height <= 0 || payload.Length < 2)
        {
            return output;
        }

        var compression = (PsdCompression)BinaryPrimitives.ReadUInt16BigEndian(payload);
        var data = payload[2..];
        switch (compression)
        {
            case PsdCompression.Raw:
                data[..Math.Min(data.Length, output.Length)].CopyTo(output);
                break;
            case PsdCompression.Rle:
                {
                    var countSize = large ? 4 : 2;
                    var tableBytes = (long)height * countSize;
                    if (tableBytes > data.Length)
                    {
                        break;
                    }

                    DecodeRle(data[(int)tableBytes..], data[..(int)tableBytes], countSize, height, rowBytes, output);
                    break;
                }

            case PsdCompression.Zip:
            case PsdCompression.ZipPrediction:
                Inflate(data, output);
                if (compression == PsdCompression.ZipPrediction)
                {
                    UndoPrediction(output, width, height, depth);
                }

                break;
            default:
                throw new PsdFormatException($"Unknown channel compression {(int)compression}.");
        }

        return output;
    }

    /// <summary>
    /// Decodes PackBits rows. <paramref name="counts"/> holds one big-endian byte
    /// count per row (u16, or u32 in PSB files).
    /// </summary>
    public static void DecodeRle(ReadOnlySpan<byte> data, ReadOnlySpan<byte> counts, int countSize, int rows, int rowBytes, Span<byte> output)
    {
        var offset = 0;
        for (var row = 0; row < rows; row++)
        {
            var count = countSize == 4
                ? (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(counts[(row * 4)..]), int.MaxValue)
                : BinaryPrimitives.ReadUInt16BigEndian(counts[(row * 2)..]);
            var destination = output.Slice(row * rowBytes, rowBytes);
            count = Math.Min(count, Math.Max(0, data.Length - offset));
            _ = UnpackBits(data.Slice(offset, count), destination);

            offset += count;
        }
    }

    /// <summary>PackBits decoder. Returns false when the input under- or overflows the row.</summary>
    public static bool UnpackBits(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        var s = 0;
        var d = 0;
        while (s < source.Length && d < destination.Length)
        {
            var header = (sbyte)source[s++];
            if (header >= 0)
            {
                var literal = header + 1;
                if (literal > source.Length - s || literal > destination.Length - d)
                {
                    var n = Math.Min(Math.Min(literal, source.Length - s), destination.Length - d);
                    source.Slice(s, n).CopyTo(destination[d..]);
                    return false;
                }

                source.Slice(s, literal).CopyTo(destination[d..]);
                s += literal;
                d += literal;
            }
            else if (header != -128)
            {
                var run = 1 - header;
                if (s >= source.Length)
                {
                    return false;
                }

                var value = source[s++];
                var n = Math.Min(run, destination.Length - d);
                destination.Slice(d, n).Fill(value);
                d += n;
                if (n < run)
                {
                    return false;
                }
            }
        }

        return d == destination.Length;
    }

    public static unsafe void Inflate(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        fixed (byte* pointer = source)
        {
            using var input = new UnmanagedMemoryStream(pointer, source.Length);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            var total = 0;
            try
            {
                while (total < destination.Length)
                {
                    var read = zlib.Read(destination[total..]);
                    if (read == 0)
                    {
                        break;
                    }

                    total += read;
                }
            }
            catch (InvalidDataException)
            {
                // Keep the rows that inflated before the damage.
            }
        }
    }

    /// <summary>Reverses the ZIP-with-prediction row delta filter in place.</summary>
    public static void UndoPrediction(Span<byte> data, int width, int height, int depth)
    {
        var rowBytes = RowBytes(width, depth);
        switch (depth)
        {
            case 8:
                for (var y = 0; y < height; y++)
                {
                    var row = data.Slice(y * rowBytes, rowBytes);
                    byte acc = 0;
                    for (var x = 0; x < row.Length; x++)
                    {
                        acc += row[x];
                        row[x] = acc;
                    }
                }

                break;
            case 16:
                for (var y = 0; y < height; y++)
                {
                    var row = data.Slice(y * rowBytes, rowBytes);
                    ushort acc = 0;
                    for (var x = 0; x < width; x++)
                    {
                        acc += BinaryPrimitives.ReadUInt16BigEndian(row[(x * 2)..]);
                        BinaryPrimitives.WriteUInt16BigEndian(row[(x * 2)..], acc);
                    }
                }

                break;
            case 32:
                {
                    // 32-bit rows are delta-coded bytewise after splitting each float
                    // into four byte planes (all high bytes first, then the next...).
                    var planes = new byte[rowBytes];
                    for (var y = 0; y < height; y++)
                    {
                        var row = data.Slice(y * rowBytes, rowBytes);
                        byte acc = 0;
                        for (var i = 0; i < rowBytes; i++)
                        {
                            acc += row[i];
                            planes[i] = acc;
                        }

                        for (var x = 0; x < width; x++)
                        {
                            row[(x * 4) + 0] = planes[x];
                            row[(x * 4) + 1] = planes[width + x];
                            row[(x * 4) + 2] = planes[(2 * width) + x];
                            row[(x * 4) + 3] = planes[(3 * width) + x];
                        }
                    }

                    break;
                }
        }
    }

    /// <summary>
    /// Converts big-endian samples to normalized floats. 8- and 16-bit samples map
    /// to [0, 1]; 32-bit samples are copied as-is (linear light, possibly above 1);
    /// 1-bit samples map set bits to 0 (black) and clear bits to 1.
    /// </summary>
    public static void ToFloat(ReadOnlySpan<byte> samples, int width, int height, int depth, Span<float> destination)
    {
        var pixels = width * height;
        switch (depth)
        {
            case 8:
                BytesToUnitFloat(samples[..pixels], destination[..pixels]);
                break;
            case 16:
                {
                    var words = MemoryMarshal.Cast<byte, ushort>(samples[..(pixels * 2)]);
                    var swapped = new ushort[pixels];
                    if (BitConverter.IsLittleEndian)
                    {
                        BinaryPrimitives.ReverseEndianness(words, swapped);
                    }
                    else
                    {
                        words.CopyTo(swapped);
                    }

                    UShortsToUnitFloat(swapped, destination[..pixels]);
                    break;
                }

            case 32:
                {
                    var words = MemoryMarshal.Cast<byte, uint>(samples[..(pixels * 4)]);
                    var target = MemoryMarshal.Cast<float, uint>(destination[..pixels]);
                    if (BitConverter.IsLittleEndian)
                    {
                        BinaryPrimitives.ReverseEndianness(words, target);
                    }
                    else
                    {
                        words.CopyTo(target);
                    }

                    // Replace NaN/inf from damaged data so they cannot poison blends.
                    for (var i = 0; i < pixels; i++)
                    {
                        if (!float.IsFinite(destination[i]))
                        {
                            destination[i] = 0;
                        }
                    }

                    break;
                }

            case 1:
                {
                    var rowBytes = RowBytes(width, 1);
                    for (var y = 0; y < height; y++)
                    {
                        var row = samples.Slice(y * rowBytes, rowBytes);
                        var output = destination.Slice(y * width, width);
                        for (var x = 0; x < width; x++)
                        {
                            output[x] = (row[x >> 3] & (0x80 >> (x & 7))) != 0 ? 0f : 1f;
                        }
                    }

                    break;
                }

            default:
                throw new PsdFormatException($"Unsupported bit depth {depth}.");
        }
    }

    /// <summary>SIMD byte to [0,1] float conversion.</summary>
    public static void BytesToUnitFloat(ReadOnlySpan<byte> source, Span<float> destination)
    {
        var i = 0;
        var scale = new Vector<float>(1f / 255f);
        var bytesPerBlock = Vector<byte>.Count;
        if (Vector.IsHardwareAccelerated)
        {
            var floatsPerVector = Vector<float>.Count;
            for (; i <= source.Length - bytesPerBlock; i += bytesPerBlock)
            {
                var bytes = new Vector<byte>(source[i..]);
                Vector.Widen(bytes, out var lowShorts, out var highShorts);
                Vector.Widen(lowShorts, out var a, out var b);
                Vector.Widen(highShorts, out var c, out var d);
                (Vector.ConvertToSingle(a) * scale).CopyTo(destination[i..]);
                (Vector.ConvertToSingle(b) * scale).CopyTo(destination[(i + floatsPerVector)..]);
                (Vector.ConvertToSingle(c) * scale).CopyTo(destination[(i + (2 * floatsPerVector))..]);
                (Vector.ConvertToSingle(d) * scale).CopyTo(destination[(i + (3 * floatsPerVector))..]);
            }
        }

        for (; i < source.Length; i++)
        {
            destination[i] = source[i] * (1f / 255f);
        }
    }

    /// <summary>SIMD ushort to [0,1] float conversion.</summary>
    public static void UShortsToUnitFloat(ReadOnlySpan<ushort> source, Span<float> destination)
    {
        var i = 0;
        var scale = new Vector<float>(1f / 65535f);
        if (Vector.IsHardwareAccelerated)
        {
            var count = Vector<ushort>.Count;
            var floatsPerVector = Vector<float>.Count;
            for (; i <= source.Length - count; i += count)
            {
                var words = new Vector<ushort>(source[i..]);
                Vector.Widen(words, out var low, out var high);
                (Vector.ConvertToSingle(low) * scale).CopyTo(destination[i..]);
                (Vector.ConvertToSingle(high) * scale).CopyTo(destination[(i + floatsPerVector)..]);
            }
        }

        for (; i < source.Length; i++)
        {
            destination[i] = source[i] * (1f / 65535f);
        }
    }
}
