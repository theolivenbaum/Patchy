using System.Buffers.Binary;
using System.Text;

namespace XRay.Psd.Tests.Support;

/// <summary>
/// Writes small ICC profiles for tests: header, tag table and the tag types
/// the color management code reads (XYZ, curv, para, desc, mluc, sf32, mft1,
/// mft2, mAB). Values are given as doubles and quantized like real profiles.
/// </summary>
internal sealed class IccBuilder
{
    public uint Version { get; init; } = 0x02100000;

    public string DeviceClass { get; init; } = "mntr";

    public string ColorSpace { get; init; } = "RGB ";

    public string Pcs { get; init; } = "XYZ ";

    public List<(string Signature, byte[] Data)> Tags { get; } = [];

    public IccBuilder Add(string signature, byte[] data)
    {
        Tags.Add((signature, data));
        return this;
    }

    public byte[] Build()
    {
        var tableSize = 4 + (Tags.Count * 12);
        var offset = 128 + tableSize;
        var body = new MemoryStream();
        var entries = new List<(string, int, int)>();
        foreach (var (signature, data) in Tags)
        {
            entries.Add((signature, offset + (int)body.Length, data.Length));
            body.Write(data);
            while (body.Length % 4 != 0)
            {
                body.WriteByte(0);
            }
        }

        var total = offset + (int)body.Length;
        var bytes = new byte[total];
        var span = bytes.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(span, (uint)total);
        BinaryPrimitives.WriteUInt32BigEndian(span[8..], Version);
        Ascii(DeviceClass).CopyTo(span[12..]);
        Ascii(ColorSpace).CopyTo(span[16..]);
        Ascii(Pcs).CopyTo(span[20..]);
        Ascii("acsp").CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32BigEndian(span[68..], Fixed(0.9642));
        BinaryPrimitives.WriteInt32BigEndian(span[72..], Fixed(1.0));
        BinaryPrimitives.WriteInt32BigEndian(span[76..], Fixed(0.8249));
        BinaryPrimitives.WriteUInt32BigEndian(span[128..], (uint)Tags.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            var (signature, at, size) = entries[i];
            var entry = span[(132 + (i * 12))..];
            Ascii(signature).CopyTo(entry);
            BinaryPrimitives.WriteUInt32BigEndian(entry[4..], (uint)at);
            BinaryPrimitives.WriteUInt32BigEndian(entry[8..], (uint)size);
        }

        body.ToArray().CopyTo(span[offset..]);
        return bytes;
    }

    public static byte[] Xyz(double x, double y, double z)
    {
        var data = new byte[20];
        Ascii("XYZ ").CopyTo(data, 0);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(8), Fixed(x));
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(12), Fixed(y));
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(16), Fixed(z));
        return data;
    }

    /// <summary>A <c>curv</c> with a single u8Fixed8 gamma.</summary>
    public static byte[] Gamma(double gamma)
    {
        var data = new byte[14];
        Ascii("curv").CopyTo(data, 0);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(12), (ushort)Math.Round(gamma * 256));
        return data;
    }

    /// <summary>A sampled <c>curv</c> (or the identity when <paramref name="samples"/> is empty).</summary>
    public static byte[] Table(IReadOnlyList<double> samples)
    {
        var data = new byte[12 + (samples.Count * 2)];
        Ascii("curv").CopyTo(data, 0);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), (uint)samples.Count);
        for (var i = 0; i < samples.Count; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(12 + (i * 2)), Word(samples[i]));
        }

        return data;
    }

    public static byte[] Parametric(int function, params double[] parameters)
    {
        var data = new byte[12 + (parameters.Length * 4)];
        Ascii("para").CopyTo(data, 0);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(8), (ushort)function);
        for (var i = 0; i < parameters.Length; i++)
        {
            BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(12 + (i * 4)), Fixed(parameters[i]));
        }

        return data;
    }

    /// <summary>A v2 <c>desc</c> (ASCII part only, empty Unicode and ScriptCode parts).</summary>
    public static byte[] Description(string text)
    {
        var ascii = Encoding.ASCII.GetBytes(text + "\0");
        var data = new byte[12 + ascii.Length + 4 + 4 + 3 + 67];
        Ascii("desc").CopyTo(data, 0);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), (uint)ascii.Length);
        ascii.CopyTo(data, 12);
        return data;
    }

    /// <summary>A v4 <c>mluc</c> with one en-US record.</summary>
    public static byte[] MultiLocalized(string text)
    {
        var utf16 = Encoding.BigEndianUnicode.GetBytes(text);
        var data = new byte[28 + utf16.Length];
        Ascii("mluc").CopyTo(data, 0);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(12), 12);
        Ascii("enUS").CopyTo(data, 16);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(20), (uint)utf16.Length);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(24), 28);
        utf16.CopyTo(data, 28);
        return data;
    }

    public static byte[] Sf32(params double[] values)
    {
        var data = new byte[8 + (values.Length * 4)];
        Ascii("sf32").CopyTo(data, 0);
        for (var i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(8 + (i * 4)), Fixed(values[i]));
        }

        return data;
    }

    /// <summary>
    /// An <c>mft2</c> (16-bit) or <c>mft1</c> (8-bit) LUT with identity matrix and
    /// identity input/output tables; <paramref name="clut"/> maps a grid node
    /// (normalized inputs, first varying slowest) to normalized outputs.
    /// </summary>
    public static byte[] Lut(bool sixteenBit, int inputs, int outputs, int grid, Func<double[], double[]> clut)
    {
        var stream = new MemoryStream();
        var header = new byte[sixteenBit ? 52 : 48];
        Ascii(sixteenBit ? "mft2" : "mft1").CopyTo(header, 0);
        header[8] = (byte)inputs;
        header[9] = (byte)outputs;
        header[10] = (byte)grid;
        for (var i = 0; i < 3; i++)
        {
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(12 + (i * 16)), Fixed(1.0));
        }

        var entries = sixteenBit ? 2 : 256;
        if (sixteenBit)
        {
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(48), (ushort)entries);
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(50), (ushort)entries);
        }

        stream.Write(header);
        var word = new byte[2];
        void Sample(double value)
        {
            if (sixteenBit)
            {
                BinaryPrimitives.WriteUInt16BigEndian(word, Word(value));
                stream.Write(word);
            }
            else
            {
                stream.WriteByte((byte)Math.Clamp(Math.Round(value * 255), 0, 255));
            }
        }

        for (var c = 0; c < inputs; c++)
        {
            for (var i = 0; i < entries; i++)
            {
                Sample(i / (double)(entries - 1));
            }
        }

        var total = (int)Math.Pow(grid, inputs);
        var node = new double[inputs];
        for (var n = 0; n < total; n++)
        {
            var rest = n;
            for (var d = inputs - 1; d >= 0; d--)
            {
                node[d] = (rest % grid) / (double)(grid - 1);
                rest /= grid;
            }

            foreach (var value in clut(node))
            {
                Sample(value);
            }
        }

        for (var c = 0; c < outputs; c++)
        {
            for (var i = 0; i < entries; i++)
            {
                Sample(i / (double)(entries - 1));
            }
        }

        return stream.ToArray();
    }

    /// <summary>
    /// A v4 <c>mAB</c> in the matrix/shaper form (no CLUT, no A curves): M curves,
    /// a 3x3 matrix with offsets on normalized XYZ, then B curves.
    /// </summary>
    public static byte[] MatrixShaperAToB(byte[][] mCurves, double[] matrix, double[] offsets, byte[][] bCurves)
    {
        var stream = new MemoryStream();
        stream.Write(new byte[32]);
        int Pad()
        {
            while (stream.Length % 4 != 0)
            {
                stream.WriteByte(0);
            }

            return (int)stream.Length;
        }

        var offsetB = Pad();
        foreach (var curve in bCurves)
        {
            stream.Write(curve);
            Pad();
        }

        var offsetMatrix = Pad();
        var word = new byte[4];
        foreach (var value in matrix.Concat(offsets))
        {
            BinaryPrimitives.WriteInt32BigEndian(word, Fixed(value));
            stream.Write(word);
        }

        var offsetM = Pad();
        foreach (var curve in mCurves)
        {
            stream.Write(curve);
            Pad();
        }

        var data = stream.ToArray();
        Ascii("mAB ").CopyTo(data, 0);
        data[8] = 3;
        data[9] = 3;
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(12), (uint)offsetB);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(16), (uint)offsetMatrix);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(20), (uint)offsetM);
        return data;
    }

    /// <summary>A <c>parf</c> formula segment (type 0: gamma, a, b, c; types 1 and 2: five parameters).</summary>
    public static byte[] FormulaSegment(int type, params float[] parameters) =>
        [.. Ascii("parf"), 0, 0, 0, 0, .. U16(type), 0, 0, .. Floats(parameters)];

    /// <summary>A <c>samf</c> sampled segment (the first point is implicit).</summary>
    public static byte[] SampledSegment(params float[] samples) =>
        [.. Ascii("samf"), 0, 0, 0, 0, .. U32(samples.Length), .. Floats(samples)];

    /// <summary>A <c>curf</c> segmented curve: breakpoints between the segments.</summary>
    public static byte[] SegmentedCurve(float[] breakpoints, params byte[][] segments) =>
        [.. Ascii("curf"), 0, 0, 0, 0, .. U16(segments.Length), 0, 0, .. Floats(breakpoints), .. segments.SelectMany(s => s)];

    /// <summary>A <c>cvst</c> curve set element.</summary>
    public static byte[] CurveSetElement(params byte[][] curves) =>
        [.. Ascii("cvst"), 0, 0, 0, 0, .. U16(curves.Length), .. U16(curves.Length), .. PositionTable(12 + (8 * curves.Length), curves)];

    /// <summary>A <c>matf</c> element: an outputs x inputs matrix (row major), then the offsets.</summary>
    public static byte[] MatrixElement(int inputs, int outputs, float[] matrix, float[] offsets) =>
        [.. Ascii("matf"), 0, 0, 0, 0, .. U16(inputs), .. U16(outputs), .. Floats(matrix), .. Floats(offsets)];

    /// <summary>A float <c>clut</c> element (first input varies slowest).</summary>
    public static byte[] ClutElement(int inputs, int outputs, byte[] grid, float[] values)
    {
        var points = new byte[16];
        grid.CopyTo(points, 0);
        return [.. Ascii("clut"), 0, 0, 0, 0, .. U16(inputs), .. U16(outputs), .. points, .. Floats(values)];
    }

    /// <summary>A multiProcessElementsType (<c>mpet</c>) tag, as used by <c>D2Bx</c>.</summary>
    public static byte[] MultiProcess(int inputs, int outputs, params byte[][] elements) =>
        [.. Ascii("mpet"), 0, 0, 0, 0, .. U16(inputs), .. U16(outputs), .. U32(elements.Length), .. PositionTable(16 + (8 * elements.Length), elements)];

    private static byte[] PositionTable(int start, byte[][] items)
    {
        var table = new List<byte>();
        var body = new List<byte>();
        foreach (var item in items)
        {
            table.AddRange(U32(start + body.Count));
            table.AddRange(U32(item.Length));
            body.AddRange(item);
            while (body.Count % 4 != 0)
            {
                body.Add(0);
            }
        }

        return [.. table, .. body];
    }

    private static byte[] U16(int value) => [(byte)(value >> 8), (byte)value];

    private static byte[] U32(int value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    private static byte[] Floats(IEnumerable<float> values) => [.. values.SelectMany(v =>
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteSingleBigEndian(bytes, v);
        return bytes;
    })];

    public static int Fixed(double value) => (int)Math.Round(value * 65536);

    public static ushort Word(double value) => (ushort)Math.Clamp(Math.Round(value * 65535), 0, 65535);

    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);
}
