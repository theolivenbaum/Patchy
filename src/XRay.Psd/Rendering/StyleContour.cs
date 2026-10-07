using XRay.Psd.Descriptors;

namespace XRay.Psd.Rendering;

/// <summary>
/// A layer-style contour curve (<c>ShpC</c>: points 0..255 with corner flags),
/// evaluated as natural cubic runs split at corners, like
/// .reference/src/core/style_contour.cpp.
/// </summary>
internal sealed class StyleContour
{
    private readonly List<(double X, double Y, bool Corner)> _points;

    private StyleContour(List<(double X, double Y, bool Corner)> points)
    {
        _points = points;
    }

    public static StyleContour Linear { get; } = new([(0, 0, false), (255, 255, false)]);

    public static StyleContour FromDescriptor(Descriptor? shape)
    {
        var points = new List<(double, double, bool)>();
        if (shape?.GetList("Crv ") is { } curve)
        {
            foreach (var item in curve)
            {
                if (item.Object is { } point)
                {
                    points.Add((point.GetNumber("Hrzn", 0), point.GetNumber("Vrtc", 0), !point.GetBoolean("Cnty", true)));
                }
            }
        }

        return points.Count == 0 ? Linear : new StyleContour(points);
    }

    public bool IsLinear =>
        _points.Count == 0 ||
        (_points.Count == 2 && Math.Abs(_points[0].X) < 1e-4 && Math.Abs(_points[0].Y) < 1e-4 && Math.Abs(_points[1].X - 255) < 1e-4 && Math.Abs(_points[1].Y - 255) < 1e-4);

    public byte[] BuildLut()
    {
        var lut = new byte[256];
        var nodes = _points
            .Select(p => (X: Math.Clamp(p.X, 0, 255), Y: Math.Clamp(p.Y, 0, 255), p.Corner))
            .OrderBy(p => p.X)
            .ToList();
        var unique = new List<(double X, double Y, bool Corner)>();
        foreach (var node in nodes)
        {
            if (unique.Count > 0 && Math.Abs(unique[^1].X - node.X) < 1e-9)
            {
                unique[^1] = node;
            }
            else
            {
                unique.Add(node);
            }
        }

        if (unique.Count < 2)
        {
            for (var i = 0; i < 256; i++)
            {
                lut[i] = (byte)i;
            }

            return lut;
        }

        var front = (byte)Math.Clamp(Math.Round(unique[0].Y, MidpointRounding.AwayFromZero), 0, 255);
        var back = (byte)Math.Clamp(Math.Round(unique[^1].Y, MidpointRounding.AwayFromZero), 0, 255);
        for (var i = 0; i < 256; i++)
        {
            if (i <= unique[0].X)
            {
                lut[i] = front;
            }
            else if (i >= unique[^1].X)
            {
                lut[i] = back;
            }
        }

        var runStart = 0;
        for (var index = 1; index < unique.Count; index++)
        {
            if (unique[index].Corner || index + 1 == unique.Count)
            {
                FillRun(lut, unique, runStart, index);
                runStart = index;
            }
        }

        return lut;
    }

    private static void FillRun(byte[] lut, List<(double X, double Y, bool Corner)> nodes, int first, int last)
    {
        var count = last - first + 1;
        var second = new double[count];
        var work = new double[count];
        for (var i = 1; i + 1 < count; i++)
        {
            var previous = nodes[first + i - 1];
            var current = nodes[first + i];
            var next = nodes[first + i + 1];
            var previousSpan = current.X - previous.X;
            var nextSpan = next.X - current.X;
            var combined = previousSpan + nextSpan;
            var sigma = previousSpan / combined;
            var pivot = (sigma * second[i - 1]) + 2.0;
            second[i] = (sigma - 1.0) / pivot;
            var previousSlope = (current.Y - previous.Y) / previousSpan;
            var nextSlope = (next.Y - current.Y) / nextSpan;
            work[i] = ((6.0 * (nextSlope - previousSlope) / combined) - (sigma * work[i - 1])) / pivot;
        }

        for (var upper = count - 1; upper > 0; upper--)
        {
            var index = upper - 1;
            second[index] = (second[index] * second[upper]) + work[index];
        }

        var begin = (int)Math.Ceiling(nodes[first].X - 1e-9);
        var end = (int)Math.Floor(nodes[last].X + 1e-9);
        var up = 1;
        for (var input = Math.Max(0, begin); input <= Math.Min(255, end); input++)
        {
            while (up + 1 < count && input > nodes[first + up].X)
            {
                up++;
            }

            var left = nodes[first + up - 1];
            var right = nodes[first + up];
            var span = right.X - left.X;
            var output = left.Y;
            if (span > 1e-9)
            {
                var lw = (right.X - input) / span;
                var rw = (input - left.X) / span;
                output = (lw * left.Y) + (rw * right.Y) + ((((lw * lw * lw) - lw) * second[up - 1]) + (((rw * rw * rw) - rw) * second[up])) * span * span / 6.0;
            }

            lut[input] = (byte)Math.Clamp(Math.Round(output, MidpointRounding.AwayFromZero), 0, 255);
        }
    }

    public static float Sample(byte[] lut, float t, bool antiAliased)
    {
        var clamped = Math.Clamp(t, 0f, 1f);
        if (!antiAliased)
        {
            return lut[(int)MathF.Round(clamped * 255f, MidpointRounding.AwayFromZero)] / 255f;
        }

        var scaled = clamped * 255f;
        var low = (int)scaled;
        var high = Math.Min(low + 1, 255);
        var fraction = scaled - low;
        return ((lut[low] * (1f - fraction)) + (lut[high] * fraction)) / 255f;
    }
}
