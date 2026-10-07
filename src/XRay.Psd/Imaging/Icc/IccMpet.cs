using System.Buffers.Binary;

namespace XRay.Psd.Imaging.Icc;

/// <summary>
/// Reads the ICC v4 multiProcessElementsType (<c>mpet</c>) used by the float
/// <c>D2Bx</c> tags, following Little CMS (<c>Type_MPE_Read</c> and its element
/// readers in cmstypes.c): curve sets of segmented curves (<c>cvst</c>, with
/// <c>parf</c> formula and <c>samf</c> sampled segments), matrices
/// (<c>matf</c>) and float CLUTs (<c>clut</c>); <c>bACS</c>/<c>eACS</c> are
/// skipped. Outputs are real PCS values (XYZ with Y = 1 for white, or L* a* b*).
/// Every offset and count is checked; malformed data returns null.
/// </summary>
internal static class IccMpet
{
    private const int MaxElements = 64;
    private const int MaxSegments = 1024;
    private const int MaxSamples = 1 << 20;
    private const long MaxClutValues = 1L << 24;

    // Little CMS's MINUS_INF and PLUS_INF segment bounds.
    private const double Infinity = 1e22;

    public static IccLut? Parse(ReadOnlySpan<byte> tag)
    {
        if (tag.Length < 16)
        {
            return null;
        }

        int inputs = U16(tag, 8), outputs = U16(tag, 10);
        var count = U32(tag, 12);
        if (inputs is < 1 or > IccLut.MaxChannels || outputs is < 1 or > IccLut.MaxChannels || count is 0 or > MaxElements)
        {
            return null;
        }

        var stages = new List<IccLutStage>();
        var channels = inputs;
        for (var i = 0; i < (int)count; i++)
        {
            var element = Slice(tag, U32(tag, 16 + (i * 8)), U32(tag, 20 + (i * 8)));
            if (element.Length < 12)
            {
                return null;
            }

            var signature = System.Text.Encoding.ASCII.GetString(element[..4]);
            if (signature is "bACS" or "eACS")
            {
                continue;
            }

            int elementInputs = U16(element, 8), elementOutputs = U16(element, 10);
            if (elementInputs != channels || elementOutputs is < 1 or > IccLut.MaxChannels)
            {
                return null;
            }

            IccLutStage? stage = signature switch
            {
                "cvst" => ReadCurveSet(element, elementInputs, elementOutputs),
                "matf" => ReadMatrix(element, elementInputs, elementOutputs),
                "clut" => ReadClut(element, elementInputs, elementOutputs),
                _ => null,
            };
            if (stage is null)
            {
                return null;
            }

            stages.Add(stage);
            channels = elementOutputs;
        }

        return channels == outputs ? IccLut.FromStages(inputs, outputs, IccPcsEncoding.Float, [.. stages], "mpet") : null;
    }

    private static SegmentedCurveStage? ReadCurveSet(ReadOnlySpan<byte> element, int inputs, int outputs)
    {
        if (inputs != outputs || element.Length < 12 + (inputs * 8))
        {
            return null;
        }

        var curves = new SegmentedCurve[inputs];
        for (var i = 0; i < inputs; i++)
        {
            var curve = SegmentedCurve.Read(Slice(element, U32(element, 12 + (i * 8)), U32(element, 16 + (i * 8))));
            if (curve is null)
            {
                return null;
            }

            curves[i] = curve;
        }

        return new SegmentedCurveStage(curves);
    }

    private static FloatMatrixStage? ReadMatrix(ReadOnlySpan<byte> element, int inputs, int outputs)
    {
        var values = (inputs * outputs) + outputs;
        if (element.Length < 12 + (values * 4))
        {
            return null;
        }

        var matrix = new double[inputs * outputs];
        for (var i = 0; i < matrix.Length; i++)
        {
            matrix[i] = Float(element, 12 + (i * 4));
        }

        var offset = new double[outputs];
        for (var i = 0; i < outputs; i++)
        {
            offset[i] = Float(element, 12 + ((matrix.Length + i) * 4));
        }

        return matrix.Concat(offset).All(double.IsFinite) ? new FloatMatrixStage(inputs, outputs, matrix, offset) : null;
    }

    private static ClutStage? ReadClut(ReadOnlySpan<byte> element, int inputs, int outputs)
    {
        if (element.Length < 28)
        {
            return null;
        }

        var grid = new int[inputs];
        long entries = 1;
        for (var i = 0; i < inputs; i++)
        {
            grid[i] = element[12 + i];
            if (grid[i] < 2)
            {
                return null;
            }

            entries *= grid[i];
        }

        var total = entries * outputs;
        if (total > MaxClutValues || element.Length < 28 + (total * 4))
        {
            return null;
        }

        var values = new float[total];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = BinaryPrimitives.ReadSingleBigEndian(element[(28 + (i * 4))..]);
            if (!float.IsFinite(values[i]))
            {
                return null;
            }
        }

        return new ClutStage(grid, outputs, values);
    }

    private static ReadOnlySpan<byte> Slice(ReadOnlySpan<byte> data, uint offset, uint size) =>
        offset <= (uint)data.Length && size <= (uint)data.Length - offset ? data.Slice((int)offset, (int)size) : [];

    private static int U16(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);

    private static uint U32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);

    private static double Float(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadSingleBigEndian(data[offset..]);

    /// <summary>A <c>curf</c> segmented curve.</summary>
    private sealed class SegmentedCurve
    {
        private readonly Segment[] _segments;

        private SegmentedCurve(Segment[] segments) => _segments = segments;

        public static SegmentedCurve? Read(ReadOnlySpan<byte> data)
        {
            if (data.Length < 12 || System.Text.Encoding.ASCII.GetString(data[..4]) != "curf")
            {
                return null;
            }

            var count = U16(data, 8);
            if (count is 0 or > MaxSegments || data.Length < 12 + ((count - 1) * 4))
            {
                return null;
            }

            var bounds = new double[count + 1];
            bounds[0] = -Infinity;
            bounds[count] = Infinity;
            for (var i = 1; i < count; i++)
            {
                bounds[i] = Float(data, 12 + ((i - 1) * 4));
                if (!double.IsFinite(bounds[i]) || bounds[i] < bounds[i - 1])
                {
                    return null;
                }
            }

            var segments = new Segment[count];
            var at = 12 + ((count - 1) * 4);
            for (var i = 0; i < count; i++)
            {
                if (data.Length < at + 12)
                {
                    return null;
                }

                var signature = System.Text.Encoding.ASCII.GetString(data.Slice(at, 4));
                if (signature == "parf")
                {
                    var type = U16(data, at + 8);
                    var parameterCount = type switch { 0 => 4, 1 or 2 => 5, _ => 0 };
                    if (parameterCount == 0 || data.Length < at + 12 + (parameterCount * 4))
                    {
                        return null;
                    }

                    var parameters = new double[parameterCount];
                    for (var p = 0; p < parameterCount; p++)
                    {
                        parameters[p] = Float(data, at + 12 + (p * 4));
                    }

                    segments[i] = new Segment(bounds[i], bounds[i + 1], type, parameters, null);
                    at += 12 + (parameterCount * 4);
                }
                else if (signature == "samf" && i > 0)
                {
                    // The first point is implicit: the previous segment's value at the breakpoint.
                    var samples = U32(data, at + 8);
                    if (samples is 0 or > MaxSamples || data.Length < at + 12 + (samples * 4L))
                    {
                        return null;
                    }

                    var points = new double[samples + 1];
                    points[0] = segments[i - 1].Evaluate(bounds[i]);
                    for (var p = 1; p <= samples; p++)
                    {
                        points[p] = Float(data, at + 12 + ((p - 1) * 4));
                    }

                    segments[i] = new Segment(bounds[i], bounds[i + 1], -1, [], points);
                    at += 12 + ((int)samples * 4);
                }
                else
                {
                    return null;
                }
            }

            return new SegmentedCurve(segments);
        }

        /// <summary>Little CMS <c>EvalSegmentedFn</c>: the last segment whose (x0, x1] holds x.</summary>
        public double Evaluate(double x)
        {
            for (var i = _segments.Length - 1; i >= 0; i--)
            {
                var segment = _segments[i];
                if (x > segment.Start && x <= segment.End)
                {
                    return segment.Evaluate(x);
                }
            }

            return -Infinity;
        }
    }

    private sealed record Segment(double Start, double End, int Type, double[] Parameters, double[]? Samples)
    {
        public double Evaluate(double x)
        {
            if (Samples is { } samples)
            {
                var position = Math.Clamp((x - Start) / (End - Start), 0, 1) * (samples.Length - 1);
                var index = Math.Min((int)position, samples.Length - 2);
                var t = position - index;
                return samples[index] + ((samples[index + 1] - samples[index]) * t);
            }

            var p = Parameters;
            switch (Type)
            {
                case 0:
                    {
                        // Y = (a X + b)^gamma + c
                        var e = (p[1] * x) + p[2];
                        return p[0] == 1.0 ? e + p[3] : e < 0 ? p[3] : Math.Pow(e, p[0]) + p[3];
                    }

                case 1:
                    {
                        // Y = a log10(b X^gamma + c) + d
                        var e = (p[2] * Math.Pow(x, p[0])) + p[3];
                        return e <= 0 ? p[4] : (p[1] * Math.Log10(e)) + p[4];
                    }

                default:
                    // Y = a b^(c X + d) + e
                    return (p[0] * Math.Pow(p[1], (p[2] * x) + p[3])) + p[4];
            }
        }
    }

    private sealed class SegmentedCurveStage(SegmentedCurve[] curves) : IccLutStage
    {
        public override int Inputs => curves.Length;

        public override int Outputs => curves.Length;

        public override void Apply(ReadOnlySpan<double> input, Span<double> output)
        {
            for (var i = 0; i < curves.Length; i++)
            {
                var value = curves[i].Evaluate(input[i]);
                output[i] = double.IsFinite(value) ? value : 0;
            }
        }
    }

    /// <summary>An outputs x inputs matrix plus offset on real values, without clamping.</summary>
    private sealed class FloatMatrixStage(int inputs, int outputs, double[] matrix, double[] offset) : IccLutStage
    {
        public override int Inputs => inputs;

        public override int Outputs => outputs;

        public override void Apply(ReadOnlySpan<double> input, Span<double> output)
        {
            for (var row = 0; row < outputs; row++)
            {
                var value = offset[row];
                for (var column = 0; column < inputs; column++)
                {
                    value += matrix[(row * inputs) + column] * input[column];
                }

                output[row] = value;
            }
        }
    }
}
