using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using XRay.Psd.Imaging;

namespace XRay.Psd.Codecs;

/// <summary>
/// Dependency-free baseline JPEG writer (JFIF, YCbCr). Quality 90 and above
/// keeps full-resolution chroma (4:4:4); lower qualities subsample 4:2:0.
/// Huffman tables are optimized per image (two passes), so the encoder needs
/// no transcribed standard tables. The forward DCT runs as two 8x8 matrix
/// products over <see cref="Vector256{T}"/> rows.
/// </summary>
public static class JpegEncoder
{
    private static readonly int[] LuminanceBase =
    [
        16, 11, 10, 16, 24, 40, 51, 61,
        12, 12, 14, 19, 26, 58, 60, 55,
        14, 13, 16, 24, 40, 57, 69, 56,
        14, 17, 22, 29, 51, 87, 80, 62,
        18, 22, 37, 56, 68, 109, 103, 77,
        24, 35, 55, 64, 81, 104, 113, 92,
        49, 64, 78, 87, 103, 121, 120, 101,
        72, 92, 95, 98, 112, 100, 103, 99,
    ];

    private static readonly int[] ChrominanceBase =
    [
        17, 18, 24, 47, 99, 99, 99, 99,
        18, 21, 26, 66, 99, 99, 99, 99,
        24, 26, 56, 99, 99, 99, 99, 99,
        47, 66, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
    ];

    /// <summary>ZigZag[k] is the natural (row-major) index of zigzag position k.</summary>
    private static readonly int[] ZigZag = BuildZigZag();

    private static readonly float[] DctMatrix = BuildDctMatrix();

    public static byte[] Encode(RgbaImage image, int quality = 90, PsdColor? background = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Width == 0 || image.Height == 0 || image.Width > 65535 || image.Height > 65535)
        {
            throw new ArgumentException("JPEG dimensions must be between 1 and 65535.", nameof(image));
        }

        quality = Math.Clamp(quality, 1, 100);
        var flat = image.IsOpaque ? image : image.Flatten(background ?? PsdColor.White);
        var subsample = quality < 90;
        var luminanceTable = ScaleTable(LuminanceBase, quality);
        var chrominanceTable = ScaleTable(ChrominanceBase, quality);

        var (y, cb, cr) = ToYCbCr(flat);
        var width = image.Width;
        var height = image.Height;
        var mcuSize = subsample ? 16 : 8;
        var mcuColumns = (width + mcuSize - 1) / mcuSize;
        var mcuRows = (height + mcuSize - 1) / mcuSize;
        var chromaWidth = width;
        var chromaHeight = height;
        if (subsample)
        {
            (cb, chromaWidth, chromaHeight) = Downsample(cb, width, height);
            (cr, _, _) = Downsample(cr, width, height);
        }

        // Pass 1: quantize every block in scan order. MCU rows are independent and
        // fill fixed slots, so they run in parallel.
        var blocksPerMcu = subsample ? 6 : 3;
        var blocksPerRow = mcuColumns * blocksPerMcu;
        var blocks = new short[mcuRows * blocksPerRow][];
        Parallelism.For(mcuRows, (long)mcuColumns * blocksPerMcu * 64 * 8, (first, last) =>
        {
            Span<float> samples = stackalloc float[64];
            for (var my = first; my < last; my++)
            {
                var blockIndex = my * blocksPerRow;
                for (var mx = 0; mx < mcuColumns; mx++)
                {
                    if (subsample)
                    {
                        for (var by = 0; by < 2; by++)
                        {
                            for (var bx = 0; bx < 2; bx++)
                            {
                                Extract(y, width, height, (mx * 16) + (bx * 8), (my * 16) + (by * 8), samples);
                                blocks[blockIndex++] = Quantize(samples, luminanceTable);
                            }
                        }

                        Extract(cb, chromaWidth, chromaHeight, mx * 8, my * 8, samples);
                        blocks[blockIndex++] = Quantize(samples, chrominanceTable);
                        Extract(cr, chromaWidth, chromaHeight, mx * 8, my * 8, samples);
                        blocks[blockIndex++] = Quantize(samples, chrominanceTable);
                    }
                    else
                    {
                        Extract(y, width, height, mx * 8, my * 8, samples);
                        blocks[blockIndex++] = Quantize(samples, luminanceTable);
                        Extract(cb, width, height, mx * 8, my * 8, samples);
                        blocks[blockIndex++] = Quantize(samples, chrominanceTable);
                        Extract(cr, width, height, mx * 8, my * 8, samples);
                        blocks[blockIndex++] = Quantize(samples, chrominanceTable);
                    }
                }
            }
        });

        // Gather symbol statistics with the same traversal the writer uses, in parallel
        // MCU-row strips (integer counts, so the sum does not depend on the split).
        var stripCount = Parallelism.StripCount(mcuRows, (long)blocksPerRow * 64);
        var stripCounts = new SymbolCounter[stripCount];
        Parallelism.For(stripCount, (long)blocks.Length * 64 / stripCount, (first, last) =>
        {
            for (var strip = first; strip < last; strip++)
            {
                var counter = new SymbolCounter(new long[4 * 257]);
                var startBlock = (int)((long)mcuRows * strip / stripCount) * blocksPerRow;
                var endBlock = (int)((long)mcuRows * (strip + 1) / stripCount) * blocksPerRow;
                Traverse(blocks, blocksPerMcu, startBlock, endBlock, ref counter);
                stripCounts[strip] = counter;
            }
        });

        var dcFrequency = new[] { new long[257], new long[257] };
        var acFrequency = new[] { new long[257], new long[257] };
        foreach (var counter in stripCounts)
        {
            for (var symbol = 0; symbol < 257; symbol++)
            {
                dcFrequency[0][symbol] += counter.Counts[symbol];
                dcFrequency[1][symbol] += counter.Counts[257 + symbol];
                acFrequency[0][symbol] += counter.Counts[514 + symbol];
                acFrequency[1][symbol] += counter.Counts[771 + symbol];
            }
        }

        var dcTables = new[] { HuffmanTable.Build(dcFrequency[0]), HuffmanTable.Build(dcFrequency[1]) };
        var acTables = new[] { HuffmanTable.Build(acFrequency[0]), HuffmanTable.Build(acFrequency[1]) };

        using var output = new MemoryStream();
        WriteHeaders(output, width, height, subsample, luminanceTable, chrominanceTable, dcTables, acTables);
        var writer = new BitWriter(output);
        var symbolWriter = new SymbolWriter(writer, dcTables, acTables);
        Traverse(blocks, blocksPerMcu, 0, blocks.Length, ref symbolWriter);
        writer.Flush();
        output.WriteByte(0xFF);
        output.WriteByte(0xD9);
        return output.ToArray();
    }

    /// <summary>Receives the entropy-coding symbols of <see cref="Traverse{TSink}"/>; a struct so the calls inline.</summary>
    private interface ISymbolSink
    {
        void Symbol(int table, int symbol, int extraBits, int extraLength, bool isDc);
    }

    /// <summary>Counts symbols: DC luma, DC chroma, AC luma, AC chroma, 257 slots each.</summary>
    private readonly struct SymbolCounter(long[] counts) : ISymbolSink
    {
        public long[] Counts { get; } = counts;

        public void Symbol(int table, int symbol, int extraBits, int extraLength, bool isDc) =>
            Counts[((isDc ? 0 : 2) + table) * 257 + symbol]++;
    }

    private readonly struct SymbolWriter(BitWriter writer, HuffmanTable[] dcTables, HuffmanTable[] acTables) : ISymbolSink
    {
        public void Symbol(int table, int symbol, int extraBits, int extraLength, bool isDc)
        {
            var huffman = isDc ? dcTables[table] : acTables[table];
            writer.Write(huffman.Codes[symbol], huffman.Lengths[symbol]);
            if (extraLength > 0)
            {
                writer.Write(extraBits, extraLength);
            }
        }
    }

    /// <summary>
    /// Walks blocks [<paramref name="startBlock"/>, <paramref name="endBlock"/>), which must
    /// start on an MCU boundary. The DC predictors start from the last block of each
    /// component before the range, so any MCU-aligned split yields the same symbols.
    /// </summary>
    private static void Traverse<TSink>(short[][] blocks, int blocksPerMcu, int startBlock, int endBlock, ref TSink sink)
        where TSink : struct, ISymbolSink
    {
        var previousDc = new int[3];
        if (startBlock >= blocksPerMcu)
        {
            var previous = startBlock - blocksPerMcu;
            previousDc[0] = blocks[previous + (blocksPerMcu == 6 ? 3 : 0)][0];
            previousDc[1] = blocks[previous + blocksPerMcu - 2][0];
            previousDc[2] = blocks[previous + blocksPerMcu - 1][0];
        }

        for (var i = startBlock; i < endBlock; i++)
        {
            var position = i % blocksPerMcu;
            int component = blocksPerMcu == 6 ? (position < 4 ? 0 : position - 3) : position;
            var table = component == 0 ? 0 : 1;
            var block = blocks[i];

            var diff = block[0] - previousDc[component];
            previousDc[component] = block[0];
            var (dcBits, dcLength) = Magnitude(diff);
            sink.Symbol(table, dcLength, dcBits, dcLength, true);

            var run = 0;
            for (var k = 1; k < 64; k++)
            {
                int value = block[k];
                if (value == 0)
                {
                    run++;
                    continue;
                }

                while (run > 15)
                {
                    sink.Symbol(table, 0xF0, 0, 0, false);
                    run -= 16;
                }

                var (bits, length) = Magnitude(value);
                sink.Symbol(table, (run << 4) | length, bits, length, false);
                run = 0;
            }

            if (run > 0)
            {
                sink.Symbol(table, 0x00, 0, 0, false);
            }
        }
    }

    // JPEG magnitude category and the extra bits (one's complement for negatives).
    private static (int Bits, int Length) Magnitude(int value)
    {
        var magnitude = Math.Abs(value);
        var length = 0;
        while (magnitude != 0)
        {
            length++;
            magnitude >>= 1;
        }

        var bits = value < 0 ? value + (1 << length) - 1 : value;
        return (bits & ((1 << length) - 1), length);
    }

    private static int[] ScaleTable(int[] baseTable, int quality)
    {
        var scale = quality < 50 ? 5000 / quality : 200 - (quality * 2);
        var table = new int[64];
        for (var i = 0; i < 64; i++)
        {
            table[i] = Math.Clamp(((baseTable[i] * scale) + 50) / 100, 1, 255);
        }

        return table;
    }

    private static (float[] Y, float[] Cb, float[] Cr) ToYCbCr(RgbaImage image)
    {
        var count = image.Width * image.Height;
        var y = new float[count];
        var cb = new float[count];
        var cr = new float[count];
        var pixels = image.Pixels;
        Parallelism.For(count, 8, (start, end) => ToYCbCr(pixels, y, cb, cr, start, end));
        return (y, cb, cr);
    }

    // Pixels [start, end). The vector lanes evaluate the scalar expressions in the same
    // order (no fused multiply-add), so both paths produce identical floats.
    private static void ToYCbCr(byte[] pixels, float[] y, float[] cb, float[] cr, int start, int end)
    {
        var i = start;
        if (Vector.IsHardwareAccelerated && BitConverter.IsLittleEndian)
        {
            var words = MemoryMarshal.Cast<byte, uint>(pixels.AsSpan());
            var mask = new Vector<uint>(0xFF);
            var offset = new Vector<float>(128f);
            for (; i <= end - Vector<uint>.Count; i += Vector<uint>.Count)
            {
                var packed = new Vector<uint>(words[i..]);
                var r = Vector.ConvertToSingle(Vector.AsVectorInt32(packed & mask));
                var g = Vector.ConvertToSingle(Vector.AsVectorInt32(Vector.ShiftRightLogical(packed, 8) & mask));
                var b = Vector.ConvertToSingle(Vector.AsVectorInt32(Vector.ShiftRightLogical(packed, 16) & mask));
                ((0.299f * r) + (0.587f * g) + (0.114f * b) - offset).CopyTo(y, i);
                ((-0.168736f * r) - (0.331264f * g) + (0.5f * b)).CopyTo(cb, i);
                ((0.5f * r) - (0.418688f * g) - (0.081312f * b)).CopyTo(cr, i);
            }
        }

        for (var p = i * 4; i < end; i++, p += 4)
        {
            float r = pixels[p];
            float g = pixels[p + 1];
            float b = pixels[p + 2];
            y[i] = (0.299f * r) + (0.587f * g) + (0.114f * b) - 128f;
            cb[i] = (-0.168736f * r) - (0.331264f * g) + (0.5f * b);
            cr[i] = (0.5f * r) - (0.418688f * g) - (0.081312f * b);
        }
    }

    private static (float[] Plane, int Width, int Height) Downsample(float[] plane, int width, int height)
    {
        var w = (width + 1) / 2;
        var h = (height + 1) / 2;
        var result = new float[w * h];
        Parallelism.For(h, w * 4L, (first, last) =>
        {
            for (var y = first; y < last; y++)
            {
                var y0 = y * 2;
                var y1 = Math.Min(y0 + 1, height - 1);
                for (var x = 0; x < w; x++)
                {
                    var x0 = x * 2;
                    var x1 = Math.Min(x0 + 1, width - 1);
                    result[(y * w) + x] = 0.25f * (plane[(y0 * width) + x0] + plane[(y0 * width) + x1] + plane[(y1 * width) + x0] + plane[(y1 * width) + x1]);
                }
            }
        });

        return (result, w, h);
    }

    // Copies an 8x8 block, replicating edge pixels past the plane bounds.
    private static void Extract(float[] plane, int width, int height, int x0, int y0, Span<float> block)
    {
        for (var y = 0; y < 8; y++)
        {
            var sy = Math.Min(y0 + y, height - 1);
            for (var x = 0; x < 8; x++)
            {
                var sx = Math.Min(x0 + x, width - 1);
                block[(y * 8) + x] = plane[(sy * width) + sx];
            }
        }
    }

    private static short[] Quantize(Span<float> samples, int[] table)
    {
        Span<float> coefficients = stackalloc float[64];
        ForwardDct(samples, coefficients);
        var result = new short[64];
        for (var k = 0; k < 64; k++)
        {
            var natural = ZigZag[k];
            // Baseline JPEG codes DC magnitudes up to 11 bits and AC up to 10 bits.
            var limit = k == 0 ? 2047 : 1023;
            result[k] = (short)Math.Clamp(Math.Round(coefficients[natural] / table[natural], MidpointRounding.AwayFromZero), -limit, limit);
        }

        return result;
    }

    /// <summary>Orthonormal 2-D DCT-II: F = M f Mᵀ, computed as two row-vector matrix products.</summary>
    private static void ForwardDct(ReadOnlySpan<float> input, Span<float> output)
    {
        Span<float> temp = stackalloc float[64];
        MultiplyColumns(input, temp);
        Span<float> transposed = stackalloc float[64];
        Transpose(temp, transposed);
        MultiplyColumns(transposed, temp);
        Transpose(temp, output);
    }

    // result row u = sum_x M[u][x] * source row x (one Vector256 per row).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void MultiplyColumns(ReadOnlySpan<float> source, Span<float> result)
    {
        Span<Vector256<float>> rows = stackalloc Vector256<float>[8];
        for (var x = 0; x < 8; x++)
        {
            rows[x] = Vector256.Create(source.Slice(x * 8, 8));
        }

        for (var u = 0; u < 8; u++)
        {
            var sum = Vector256<float>.Zero;
            for (var x = 0; x < 8; x++)
            {
                sum += rows[x] * DctMatrix[(u * 8) + x];
            }

            sum.CopyTo(result.Slice(u * 8, 8));
        }
    }

    private static void Transpose(ReadOnlySpan<float> source, Span<float> destination)
    {
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 8; x++)
            {
                destination[(x * 8) + y] = source[(y * 8) + x];
            }
        }
    }

    private static float[] BuildDctMatrix()
    {
        var matrix = new float[64];
        for (var u = 0; u < 8; u++)
        {
            var c = u == 0 ? Math.Sqrt(0.125) : 0.5;
            for (var x = 0; x < 8; x++)
            {
                matrix[(u * 8) + x] = (float)(c * Math.Cos(((2 * x) + 1) * u * Math.PI / 16));
            }
        }

        return matrix;
    }

    private static int[] BuildZigZag()
    {
        var order = new int[64];
        var index = 0;
        for (var diagonal = 0; diagonal < 15; diagonal++)
        {
            var start = Math.Max(0, diagonal - 7);
            var end = Math.Min(diagonal, 7);
            for (var i = start; i <= end; i++)
            {
                // Even diagonals run bottom-left to top-right, odd ones the other way.
                var row = diagonal % 2 == 0 ? diagonal - i : i;
                var column = diagonal - row;
                order[index++] = (row * 8) + column;
            }
        }

        return order;
    }

    private static void WriteHeaders(Stream output, int width, int height, bool subsample, int[] luminance, int[] chrominance, HuffmanTable[] dc, HuffmanTable[] ac)
    {
        void Marker(byte code) { output.WriteByte(0xFF); output.WriteByte(code); }
        void Word(int value) { output.WriteByte((byte)(value >> 8)); output.WriteByte((byte)value); }

        Marker(0xD8);

        // APP0 JFIF 1.01, 1:1 aspect.
        Marker(0xE0);
        Word(16);
        output.Write("JFIF\0"u8);
        output.WriteByte(1);
        output.WriteByte(1);
        output.WriteByte(0);
        Word(1);
        Word(1);
        output.WriteByte(0);
        output.WriteByte(0);

        Marker(0xDB);
        Word(2 + (65 * 2));
        output.WriteByte(0);
        for (var k = 0; k < 64; k++)
        {
            output.WriteByte((byte)luminance[ZigZag[k]]);
        }

        output.WriteByte(1);
        for (var k = 0; k < 64; k++)
        {
            output.WriteByte((byte)chrominance[ZigZag[k]]);
        }

        Marker(0xC0);
        Word(17);
        output.WriteByte(8);
        Word(height);
        Word(width);
        output.WriteByte(3);
        output.WriteByte(1);
        output.WriteByte((byte)(subsample ? 0x22 : 0x11));
        output.WriteByte(0);
        output.WriteByte(2);
        output.WriteByte(0x11);
        output.WriteByte(1);
        output.WriteByte(3);
        output.WriteByte(0x11);
        output.WriteByte(1);

        var tables = new (int Class, HuffmanTable Table)[] { (0x00, dc[0]), (0x10, ac[0]), (0x01, dc[1]), (0x11, ac[1]) };
        foreach (var (tableClass, table) in tables)
        {
            Marker(0xC4);
            Word(2 + 1 + 16 + table.Values.Length);
            output.WriteByte((byte)tableClass);
            for (var length = 1; length <= 16; length++)
            {
                output.WriteByte((byte)table.Counts[length]);
            }

            output.Write(table.Values);
        }

        Marker(0xDA);
        Word(12);
        output.WriteByte(3);
        output.WriteByte(1);
        output.WriteByte(0x00);
        output.WriteByte(2);
        output.WriteByte(0x11);
        output.WriteByte(3);
        output.WriteByte(0x11);
        output.WriteByte(0);
        output.WriteByte(63);
        output.WriteByte(0);
    }

    /// <summary>Canonical Huffman table built from symbol frequencies (the libjpeg optimal-table algorithm, 16-bit limited).</summary>
    private sealed class HuffmanTable
    {
        public int[] Codes { get; } = new int[256];

        public int[] Lengths { get; } = new int[256];

        public int[] Counts { get; } = new int[17];

        public byte[] Values { get; private set; } = [];

        public static HuffmanTable Build(long[] frequencies)
        {
            var frequency = (long[])frequencies.Clone();
            var codeSize = new int[257];
            var others = new int[257];
            Array.Fill(others, -1);
            var any = false;
            for (var i = 0; i < 256; i++)
            {
                any |= frequency[i] > 0;
            }

            if (!any)
            {
                // Tables must not be empty; give symbol 0 a nominal count.
                frequency[0] = 1;
            }

            // Reserve one code point so no real symbol gets the all-ones code.
            frequency[256] = 1;
            while (true)
            {
                var c1 = -1;
                var v = long.MaxValue;
                for (var i = 0; i <= 256; i++)
                {
                    if (frequency[i] > 0 && frequency[i] <= v)
                    {
                        v = frequency[i];
                        c1 = i;
                    }
                }

                var c2 = -1;
                v = long.MaxValue;
                for (var i = 0; i <= 256; i++)
                {
                    if (frequency[i] > 0 && frequency[i] <= v && i != c1)
                    {
                        v = frequency[i];
                        c2 = i;
                    }
                }

                if (c2 < 0)
                {
                    break;
                }

                frequency[c1] += frequency[c2];
                frequency[c2] = 0;
                codeSize[c1]++;
                while (others[c1] >= 0)
                {
                    c1 = others[c1];
                    codeSize[c1]++;
                }

                others[c1] = c2;
                codeSize[c2]++;
                while (others[c2] >= 0)
                {
                    c2 = others[c2];
                    codeSize[c2]++;
                }
            }

            var bits = new int[33];
            for (var i = 0; i <= 256; i++)
            {
                if (codeSize[i] > 0)
                {
                    bits[codeSize[i]]++;
                }
            }

            // Limit code lengths to 16 bits.
            for (var i = 32; i > 16; i--)
            {
                while (bits[i] > 0)
                {
                    var j = i - 2;
                    while (bits[j] == 0)
                    {
                        j--;
                    }

                    bits[i] -= 2;
                    bits[i - 1]++;
                    bits[j + 1] += 2;
                    bits[j]--;
                }
            }

            // Drop the reserved code from the longest length.
            var longest = 16;
            while (bits[longest] == 0)
            {
                longest--;
            }

            bits[longest]--;

            var table = new HuffmanTable();
            var values = new List<byte>();
            for (var length = 1; length <= 32; length++)
            {
                for (var symbol = 0; symbol < 256; symbol++)
                {
                    if (codeSize[symbol] == length)
                    {
                        values.Add((byte)symbol);
                    }
                }
            }

            for (var length = 1; length <= 16; length++)
            {
                table.Counts[length] = bits[length];
            }

            table.Values = values.ToArray();

            // Canonical codes: symbols in Values order receive increasing codes per length.
            var code = 0;
            var k = 0;
            for (var length = 1; length <= 16; length++)
            {
                for (var n = 0; n < bits[length]; n++)
                {
                    var symbol = table.Values[k++];
                    table.Codes[symbol] = code++;
                    table.Lengths[symbol] = length;
                }

                code <<= 1;
            }

            return table;
        }
    }

    private sealed class BitWriter(Stream output)
    {
        private uint _buffer;
        private int _count;

        public void Write(int bits, int length)
        {
            _buffer = (_buffer << length) | ((uint)bits & ((1u << length) - 1));
            _count += length;
            while (_count >= 8)
            {
                var b = (byte)(_buffer >> (_count - 8));
                output.WriteByte(b);
                if (b == 0xFF)
                {
                    output.WriteByte(0);
                }

                _count -= 8;
            }
        }

        public void Flush()
        {
            if (_count > 0)
            {
                Write(0x7F, 8 - _count);
            }
        }
    }
}
