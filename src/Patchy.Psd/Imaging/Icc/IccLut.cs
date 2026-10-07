using System.Buffers.Binary;

namespace Patchy.Psd.Imaging.Icc;

/// <summary>How a LUT encodes the PCS side of its values (normalized to [0,1]).</summary>
internal enum IccPcsEncoding
{
    /// <summary>v4 Lab: L = v*100, a/b = v*255 - 128 (also <c>mft1</c>).</summary>
    LabV4,

    /// <summary>v2 16-bit Lab in <c>mft2</c>: L 100 at 0xFF00, a/b 0 at 0x8000.</summary>
    LabV2,
}

/// <summary>
/// An ICC LUT-based transform (<c>mft1</c>, <c>mft2</c>, <c>mAB </c>, <c>mBA </c>)
/// as a pipeline of curve, matrix and CLUT stages over normalized [0,1] values.
/// It is evaluated in double precision only while building lookup tables, so
/// the stages are plain virtual calls.
/// </summary>
internal sealed class IccLut
{
    /// <summary>ICC caps a color space at 15 channels.</summary>
    public const int MaxChannels = 15;

    // Defensive cap on CLUT size (entries times outputs) for untrusted profiles.
    private const long MaxClutValues = 1L << 24;

    private readonly IccLutStage[] _stages;

    private IccLut(int inputs, int outputs, IccPcsEncoding labEncoding, IccLutStage[] stages)
    {
        InputChannels = inputs;
        OutputChannels = outputs;
        LabEncoding = labEncoding;
        _stages = stages;
    }

    public int InputChannels { get; }

    public int OutputChannels { get; }

    /// <summary>The Lab encoding of the PCS side, used when the profile PCS is Lab.</summary>
    public IccPcsEncoding LabEncoding { get; }

    /// <summary>The LUT type signature it was read from (for diagnostics).</summary>
    public string Type { get; private init; } = "";

    /// <summary>
    /// Recognizes a CLUT-free three-channel LUT that is just per-channel curves
    /// followed by a matrix (the v4 matrix/shaper form of <c>mAB</c>), so it can
    /// run as an exact matrix/shaper instead of a sampled grid. The matrix and
    /// offset act on normalized PCS values.
    /// </summary>
    public bool TryGetMatrixShaper(out IccCurve[] curves, out double[] matrix, out double[] offset)
    {
        curves = [IccCurve.Identity, IccCurve.Identity, IccCurve.Identity];
        matrix = [];
        offset = [0, 0, 0];
        if (InputChannels != 3 || OutputChannels != 3)
        {
            return false;
        }

        var stages = _stages;
        if (stages is [CurveStage shaper, MatrixStage last] && shaper.Curves.Length == 3)
        {
            curves = shaper.Curves;
            matrix = last.Matrix;
            offset = last.Offset ?? offset;
            return true;
        }

        if (stages is [MatrixStage only])
        {
            matrix = only.Matrix;
            offset = only.Offset ?? offset;
            return true;
        }

        return false;
    }

    public void Evaluate(ReadOnlySpan<double> input, Span<double> output)
    {
        Span<double> a = stackalloc double[MaxChannels];
        Span<double> b = stackalloc double[MaxChannels];
        var count = InputChannels;
        for (var i = 0; i < count; i++)
        {
            a[i] = Math.Clamp(input[i], 0.0, 1.0);
        }

        foreach (var stage in _stages)
        {
            stage.Apply(a[..stage.Inputs], b[..stage.Outputs]);
            count = stage.Outputs;
            var swap = a;
            a = b;
            b = swap;
        }

        for (var i = 0; i < OutputChannels; i++)
        {
            output[i] = i < count ? a[i] : 0;
        }
    }

    /// <summary>Parses a LUT tag. Returns null for unsupported or malformed data.</summary>
    public static IccLut? Parse(ReadOnlySpan<byte> tag)
    {
        if (tag.Length < 12)
        {
            return null;
        }

        var type = System.Text.Encoding.ASCII.GetString(tag[..4]);
        try
        {
            return type switch
            {
                "mft1" => ParseLut8Or16(tag, sixteenBit: false),
                "mft2" => ParseLut8Or16(tag, sixteenBit: true),
                "mAB " => ParseLutAb(tag, aToB: true),
                "mBA " => ParseLutAb(tag, aToB: false),
                _ => null,
            };
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    private static IccLut? ParseLut8Or16(ReadOnlySpan<byte> tag, bool sixteenBit)
    {
        if (tag.Length < 48)
        {
            return null;
        }

        int inputs = tag[8], outputs = tag[9], grid = tag[10];
        if (inputs is < 1 or > MaxChannels || outputs is < 1 or > MaxChannels || grid < 2)
        {
            return null;
        }

        var matrix = new double[9];
        for (var i = 0; i < 9; i++)
        {
            matrix[i] = ReadS15Fixed16(tag, 12 + (i * 4));
        }

        int inputEntries, outputEntries, offset;
        if (sixteenBit)
        {
            if (tag.Length < 52)
            {
                return null;
            }

            inputEntries = BinaryPrimitives.ReadUInt16BigEndian(tag[48..]);
            outputEntries = BinaryPrimitives.ReadUInt16BigEndian(tag[50..]);
            offset = 52;
            if (inputEntries is < 2 or > 4096 || outputEntries is < 2 or > 4096)
            {
                return null;
            }
        }
        else
        {
            inputEntries = outputEntries = 256;
            offset = 48;
        }

        var sampleBytes = sixteenBit ? 2 : 1;
        var clutEntries = ClutEntryCount(grid, inputs);
        if (clutEntries < 0 || clutEntries * outputs > MaxClutValues)
        {
            return null;
        }

        var needed = offset + ((long)inputs * inputEntries * sampleBytes) + (clutEntries * outputs * sampleBytes) + ((long)outputs * outputEntries * sampleBytes);
        if (needed > tag.Length)
        {
            return null;
        }

        var stages = new List<IccLutStage>();

        // lcms applies the matrix only to three-channel (XYZ) input, and only when it is not the identity.
        if (inputs == 3 && !IsIdentity3x3(matrix))
        {
            stages.Add(new MatrixStage(matrix, null));
        }

        var inputCurves = new IccCurve[inputs];
        for (var c = 0; c < inputs; c++)
        {
            inputCurves[c] = IccCurve.FromTable(ReadSamples(tag, ref offset, inputEntries, sixteenBit));
        }

        stages.Add(new CurveStage(inputCurves));
        var gridPoints = new int[inputs];
        Array.Fill(gridPoints, grid);
        var clut = ReadSamples(tag, ref offset, (int)(clutEntries * outputs), sixteenBit);
        stages.Add(new ClutStage(gridPoints, outputs, clut));
        var outputCurves = new IccCurve[outputs];
        for (var c = 0; c < outputs; c++)
        {
            outputCurves[c] = IccCurve.FromTable(ReadSamples(tag, ref offset, outputEntries, sixteenBit));
        }

        stages.Add(new CurveStage(outputCurves));
        return new IccLut(inputs, outputs, sixteenBit ? IccPcsEncoding.LabV2 : IccPcsEncoding.LabV4, [.. stages]) { Type = sixteenBit ? "mft2" : "mft1" };
    }

    /// <summary>
    /// lutAToBType applies A curves, CLUT, M curves, matrix, B curves; lutBToAType
    /// runs the same elements in reverse. Any element may be absent.
    /// </summary>
    private static IccLut? ParseLutAb(ReadOnlySpan<byte> tag, bool aToB)
    {
        if (tag.Length < 32)
        {
            return null;
        }

        int inputs = tag[8], outputs = tag[9];
        if (inputs is < 1 or > MaxChannels || outputs is < 1 or > MaxChannels)
        {
            return null;
        }

        var offsetB = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(tag[12..]), int.MaxValue);
        var offsetMatrix = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(tag[16..]), int.MaxValue);
        var offsetM = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(tag[20..]), int.MaxValue);
        var offsetClut = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(tag[24..]), int.MaxValue);
        var offsetA = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(tag[28..]), int.MaxValue);
        foreach (var offset in (ReadOnlySpan<int>)[offsetB, offsetMatrix, offsetM, offsetClut, offsetA])
        {
            if (offset > tag.Length)
            {
                return null;
            }
        }

        // PCS-side channel count: outputs for AToB, inputs for BToA.
        var pcsChannels = aToB ? outputs : inputs;
        var deviceChannels = aToB ? inputs : outputs;
        var b = offsetB != 0 ? ReadCurveSet(tag, offsetB, pcsChannels) : null;
        var m = offsetM != 0 ? ReadCurveSet(tag, offsetM, pcsChannels) : null;
        var a = offsetA != 0 ? ReadCurveSet(tag, offsetA, deviceChannels) : null;
        if ((offsetB != 0 && b is null) || (offsetM != 0 && m is null) || (offsetA != 0 && a is null))
        {
            return null;
        }

        MatrixStage? matrix = null;
        if (offsetMatrix != 0)
        {
            if (pcsChannels != 3 || offsetMatrix + 48 > tag.Length)
            {
                return null;
            }

            var values = new double[9];
            var offsets = new double[3];
            for (var i = 0; i < 9; i++)
            {
                values[i] = ReadS15Fixed16(tag, offsetMatrix + (i * 4));
            }

            for (var i = 0; i < 3; i++)
            {
                offsets[i] = ReadS15Fixed16(tag, offsetMatrix + 36 + (i * 4));
            }

            matrix = new MatrixStage(values, offsets);
        }

        ClutStage? clut = null;
        if (offsetClut != 0)
        {
            var clutInputs = inputs;
            var clutOutputs = outputs;
            if (offsetClut + 20 > tag.Length)
            {
                return null;
            }

            var gridPoints = new int[clutInputs];
            long entries = 1;
            for (var i = 0; i < clutInputs; i++)
            {
                gridPoints[i] = tag[offsetClut + i];
                if (gridPoints[i] < 2)
                {
                    return null;
                }

                entries *= gridPoints[i];
                if (entries * clutOutputs > MaxClutValues)
                {
                    return null;
                }
            }

            var precision = tag[offsetClut + 16];
            if (precision is not (1 or 2))
            {
                return null;
            }

            var dataOffset = offsetClut + 20;
            if (dataOffset + (entries * clutOutputs * precision) > tag.Length)
            {
                return null;
            }

            clut = new ClutStage(gridPoints, clutOutputs, ReadSamples(tag, ref dataOffset, (int)(entries * clutOutputs), precision == 2));
        }
        else if (inputs != outputs)
        {
            return null;
        }

        var stages = new List<IccLutStage>();
        if (aToB)
        {
            Add(stages, a);
            if (clut is not null)
            {
                stages.Add(clut);
            }

            Add(stages, m);
            if (matrix is not null)
            {
                stages.Add(matrix);
            }

            Add(stages, b);
        }
        else
        {
            Add(stages, b);
            if (matrix is not null)
            {
                stages.Add(matrix);
            }

            Add(stages, m);
            if (clut is not null)
            {
                stages.Add(clut);
            }

            Add(stages, a);
        }

        return new IccLut(inputs, outputs, IccPcsEncoding.LabV4, [.. stages]) { Type = aToB ? "mAB" : "mBA" };

        static void Add(List<IccLutStage> list, IccCurve[]? curves)
        {
            if (curves is not null && !curves.All(c => c.IsIdentity))
            {
                list.Add(new CurveStage(curves));
            }
        }
    }

    private static IccCurve[]? ReadCurveSet(ReadOnlySpan<byte> tag, int offset, int count)
    {
        var curves = new IccCurve[count];
        for (var i = 0; i < count; i++)
        {
            if (offset < 0 || offset + 12 > tag.Length)
            {
                return null;
            }

            var curve = IccProfile.ReadCurve(tag[offset..], out var consumed);
            if (curve is null)
            {
                return null;
            }

            curves[i] = curve;
            offset += (consumed + 3) & ~3;
        }

        return curves;
    }

    private static float[] ReadSamples(ReadOnlySpan<byte> tag, ref int offset, int count, bool sixteenBit)
    {
        var values = new float[count];
        if (sixteenBit)
        {
            var span = tag.Slice(offset, count * 2);
            for (var i = 0; i < count; i++)
            {
                values[i] = BinaryPrimitives.ReadUInt16BigEndian(span[(i * 2)..]) / 65535f;
            }

            offset += count * 2;
        }
        else
        {
            var span = tag.Slice(offset, count);
            for (var i = 0; i < count; i++)
            {
                values[i] = span[i] / 255f;
            }

            offset += count;
        }

        return values;
    }

    private static long ClutEntryCount(int grid, int inputs)
    {
        long entries = 1;
        for (var i = 0; i < inputs; i++)
        {
            entries *= grid;
            if (entries > MaxClutValues)
            {
                return -1;
            }
        }

        return entries;
    }

    private static bool IsIdentity3x3(double[] m) =>
        m[0] == 1 && m[1] == 0 && m[2] == 0 && m[3] == 0 && m[4] == 1 && m[5] == 0 && m[6] == 0 && m[7] == 0 && m[8] == 1;

    internal static double ReadS15Fixed16(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadInt32BigEndian(data[offset..]) / 65536.0;
}

internal abstract class IccLutStage
{
    public abstract int Inputs { get; }

    public abstract int Outputs { get; }

    public abstract void Apply(ReadOnlySpan<double> input, Span<double> output);
}

internal sealed class CurveStage(IccCurve[] curves) : IccLutStage
{
    public IccCurve[] Curves => curves;

    public override int Inputs => curves.Length;

    public override int Outputs => curves.Length;

    public override void Apply(ReadOnlySpan<double> input, Span<double> output)
    {
        for (var i = 0; i < curves.Length; i++)
        {
            output[i] = curves[i].Evaluate(input[i]);
        }
    }
}

/// <summary>A 3x3 matrix (row major) with an optional offset, on normalized values.</summary>
internal sealed class MatrixStage(double[] matrix, double[]? offset) : IccLutStage
{
    public double[] Matrix => matrix;

    public double[]? Offset => offset;

    public override int Inputs => 3;

    public override int Outputs => 3;

    public override void Apply(ReadOnlySpan<double> input, Span<double> output)
    {
        double x = input[0], y = input[1], z = input[2];
        for (var row = 0; row < 3; row++)
        {
            var value = (matrix[row * 3] * x) + (matrix[(row * 3) + 1] * y) + (matrix[(row * 3) + 2] * z) + (offset?[row] ?? 0);
            output[row] = Math.Clamp(value, 0.0, 1.0);
        }
    }
}

/// <summary>
/// A multidimensional color lookup table. The first input varies slowest.
/// Interpolation follows Little CMS: tetrahedral over the last three inputs,
/// linear across any leading inputs (so CMYK is linear in C between two
/// tetrahedral MYK lookups), bilinear for two inputs and linear for one.
/// </summary>
internal sealed class ClutStage : IccLutStage
{
    private readonly int[] _grid;
    private readonly int[] _strides;
    private readonly int _outputs;
    private readonly float[] _values;

    public ClutStage(int[] grid, int outputs, float[] values)
    {
        _grid = grid;
        _outputs = outputs;
        _values = values;
        _strides = new int[grid.Length];
        var stride = outputs;
        for (var i = grid.Length - 1; i >= 0; i--)
        {
            _strides[i] = stride;
            stride *= grid[i];
        }
    }

    public override int Inputs => _grid.Length;

    public override int Outputs => _outputs;

    public override void Apply(ReadOnlySpan<double> input, Span<double> output)
    {
        Span<int> index = stackalloc int[_grid.Length];
        Span<double> fraction = stackalloc double[_grid.Length];
        for (var d = 0; d < _grid.Length; d++)
        {
            var position = Math.Clamp(input[d], 0.0, 1.0) * (_grid[d] - 1);
            var i = Math.Min((int)position, _grid[d] - 2);
            index[d] = i;
            fraction[d] = position - i;
        }

        Interpolate(0, 0, index, fraction, output);
    }

    private void Interpolate(int dimension, int baseOffset, ReadOnlySpan<int> index, ReadOnlySpan<double> fraction, Span<double> output)
    {
        var remaining = _grid.Length - dimension;
        var offset = baseOffset + (index[dimension] * _strides[dimension]);
        if (remaining == 3)
        {
            var origin = offset + (index[dimension + 1] * _strides[dimension + 1]) + (index[dimension + 2] * _strides[dimension + 2]);
            Tetrahedral(origin, _strides[dimension], _strides[dimension + 1], _strides[dimension + 2], fraction[dimension], fraction[dimension + 1], fraction[dimension + 2], output);
            return;
        }

        var t = fraction[dimension];
        if (remaining == 1)
        {
            for (var o = 0; o < _outputs; o++)
            {
                double a = _values[offset + o], b = _values[offset + _strides[dimension] + o];
                output[o] = a + ((b - a) * t);
            }

            return;
        }

        Span<double> low = stackalloc double[_outputs];
        Span<double> high = stackalloc double[_outputs];
        Interpolate(dimension + 1, offset, index, fraction, low);
        Interpolate(dimension + 1, offset + _strides[dimension], index, fraction, high);
        for (var o = 0; o < _outputs; o++)
        {
            output[o] = low[o] + ((high[o] - low[o]) * t);
        }
    }

    private void Tetrahedral(int origin, int sx, int sy, int sz, double rx, double ry, double rz, Span<double> output)
    {
        var v = _values;
        for (var o = 0; o < _outputs; o++)
        {
            var c0 = (double)v[origin + o];
            double c1, c2, c3;
            if (rx >= ry && ry >= rz)
            {
                c1 = v[origin + sx + o] - c0;
                c2 = v[origin + sx + sy + o] - v[origin + sx + o];
                c3 = v[origin + sx + sy + sz + o] - v[origin + sx + sy + o];
            }
            else if (rx >= rz && rz >= ry)
            {
                c1 = v[origin + sx + o] - c0;
                c2 = v[origin + sx + sy + sz + o] - v[origin + sx + sz + o];
                c3 = v[origin + sx + sz + o] - v[origin + sx + o];
            }
            else if (rz >= rx && rx >= ry)
            {
                c1 = v[origin + sx + sz + o] - v[origin + sz + o];
                c2 = v[origin + sx + sy + sz + o] - v[origin + sx + sz + o];
                c3 = v[origin + sz + o] - c0;
            }
            else if (ry >= rx && rx >= rz)
            {
                c1 = v[origin + sx + sy + o] - v[origin + sy + o];
                c2 = v[origin + sy + o] - c0;
                c3 = v[origin + sx + sy + sz + o] - v[origin + sx + sy + o];
            }
            else if (ry >= rz && rz >= rx)
            {
                c1 = v[origin + sx + sy + sz + o] - v[origin + sy + sz + o];
                c2 = v[origin + sy + o] - c0;
                c3 = v[origin + sy + sz + o] - v[origin + sy + o];
            }
            else
            {
                c1 = v[origin + sx + sy + sz + o] - v[origin + sy + sz + o];
                c2 = v[origin + sy + sz + o] - v[origin + sz + o];
                c3 = v[origin + sz + o] - c0;
            }

            output[o] = c0 + (c1 * rx) + (c2 * ry) + (c3 * rz);
        }
    }
}
