using Patchy.Psd.Layers;

namespace Patchy.Psd.Rendering;

/// <summary>
/// Scanline polygon rasterizer: 16 sub-scanlines per pixel row with exact
/// horizontal span coverage, even-odd or non-zero winding. Used for vector
/// masks; Photoshop pixel <c>i</c> covers the continuous interval <c>[i, i+1)</c>.
/// </summary>
internal static class PathRasterizer
{
    private const int SubSamples = 16;
    private const double FlattenTolerance = 0.05;

    private readonly record struct Edge(double X0, double Y0, double X1, double Y1, int Direction)
    {
        public double MinY => Math.Min(Y0, Y1);

        public double MaxY => Math.Max(Y0, Y1);

        public double XAt(double y) => X0 + ((X1 - X0) * (y - Y0) / (Y1 - Y0));
    }

    /// <summary>
    /// Rasterizes a vector path into coverage over <paramref name="rect"/>, combining
    /// subpath groups with their Photoshop operations (Add, Subtract, Intersect, Xor).
    /// </summary>
    public static float[] RasterizePath(VectorPath path, PsdRect rect)
    {
        var coverage = new float[rect.Width * rect.Height];
        if (rect.IsEmpty)
        {
            return coverage;
        }

        if (path.Subpaths.Count == 0)
        {
            // An empty path covers everything (fill layers without a mask path).
            coverage.AsSpan().Fill(1f);
            return coverage;
        }

        var first = true;
        var index = 0;
        var subpaths = path.Subpaths;
        while (index < subpaths.Count)
        {
            var group = subpaths[index].ShapeGroup;
            var op = subpaths[index].Operation;
            var end = index + 1;
            while (end < subpaths.Count && subpaths[end].ShapeGroup == group)
            {
                end++;
            }

            var polygons = new List<List<(double X, double Y)>>();
            for (var i = index; i < end; i++)
            {
                var polygon = Flatten(subpaths[i]);
                if (polygon.Count >= 3)
                {
                    polygons.Add(polygon);
                }
            }

            var groupCoverage = Rasterize(polygons, rect, evenOdd: true);
            if (first)
            {
                first = false;
                if (op == PathCombineOperation.Subtract)
                {
                    for (var i = 0; i < coverage.Length; i++)
                    {
                        coverage[i] = 1f - groupCoverage[i];
                    }
                }
                else
                {
                    groupCoverage.AsSpan().CopyTo(coverage);
                }
            }
            else
            {
                for (var i = 0; i < coverage.Length; i++)
                {
                    var a = coverage[i];
                    var b = groupCoverage[i];
                    coverage[i] = op switch
                    {
                        PathCombineOperation.Add => a + b - (a * b),
                        PathCombineOperation.Subtract => a - (a * b),
                        PathCombineOperation.Intersect => a * b,
                        _ => a + b - (2 * a * b),
                    };
                }
            }

            index = end;
        }

        return coverage;
    }

    /// <summary>Flattens a bezier subpath to a closed polygon in document coordinates.</summary>
    public static List<(double X, double Y)> Flatten(PathSubpath subpath)
    {
        var knots = subpath.Knots;
        var points = new List<(double X, double Y)>();
        if (knots.Count == 0)
        {
            return points;
        }

        points.Add((knots[0].X, knots[0].Y));
        // Photoshop fills open subpaths as if closed.
        var segments = knots.Count;
        for (var i = 0; i < segments; i++)
        {
            var a = knots[i];
            var b = knots[(i + 1) % knots.Count];
            if (i == segments - 1 && knots.Count == 1)
            {
                break;
            }

            AddCubic(points, a.X, a.Y, a.OutX, a.OutY, b.InX, b.InY, b.X, b.Y);
        }

        return points;
    }

    private static void AddCubic(List<(double X, double Y)> points, double x0, double y0, double x1, double y1, double x2, double y2, double x3, double y3)
    {
        // Wang's formula: subdivisions needed for the flattening tolerance.
        var ddx = Math.Max(Math.Abs(x0 - (2 * x1) + x2), Math.Abs(x1 - (2 * x2) + x3));
        var ddy = Math.Max(Math.Abs(y0 - (2 * y1) + y2), Math.Abs(y1 - (2 * y2) + y3));
        var dd = Math.Sqrt((ddx * ddx) + (ddy * ddy));
        var steps = (int)Math.Clamp(Math.Ceiling(Math.Sqrt(dd * 3.0 / (4.0 * FlattenTolerance))), 1, 512);
        for (var s = 1; s <= steps; s++)
        {
            var t = (double)s / steps;
            var u = 1 - t;
            var b0 = u * u * u;
            var b1 = 3 * u * u * t;
            var b2 = 3 * u * t * t;
            var b3 = t * t * t;
            points.Add(((b0 * x0) + (b1 * x1) + (b2 * x2) + (b3 * x3), (b0 * y0) + (b1 * y1) + (b2 * y2) + (b3 * y3)));
        }
    }

    /// <summary>Rasterizes closed polygons (document coordinates) to coverage over <paramref name="rect"/>.</summary>
    public static float[] Rasterize(List<List<(double X, double Y)>> polygons, PsdRect rect, bool evenOdd)
    {
        var width = rect.Width;
        var height = rect.Height;
        var coverage = new float[width * height];
        var edges = new List<Edge>();
        foreach (var polygon in polygons)
        {
            for (var i = 0; i < polygon.Count; i++)
            {
                var (ax, ay) = polygon[i];
                var (bx, by) = polygon[(i + 1) % polygon.Count];
                ax -= rect.Left;
                bx -= rect.Left;
                ay -= rect.Top;
                by -= rect.Top;
                if (ay == by)
                {
                    continue;
                }

                edges.Add(new Edge(ax, ay, bx, by, by > ay ? 1 : -1));
            }
        }

        if (edges.Count == 0)
        {
            return coverage;
        }

        edges.Sort(static (a, b) => a.MinY.CompareTo(b.MinY));
        var active = new List<Edge>();
        var crossings = new List<(double X, int Direction)>();
        var accumulation = new float[width + 1];
        var nextEdge = 0;
        const float sampleWeight = 1f / SubSamples;
        for (var y = 0; y < height; y++)
        {
            var rowTop = (double)y;
            var rowBottom = rowTop + 1;

            // Activate edges that start above the row bottom; drop edges that end above the row top.
            while (nextEdge < edges.Count && edges[nextEdge].MinY < rowBottom)
            {
                active.Add(edges[nextEdge++]);
            }

            active.RemoveAll(e => e.MaxY <= rowTop);
            if (active.Count == 0)
            {
                if (nextEdge >= edges.Count)
                {
                    break;
                }

                continue;
            }

            Array.Clear(accumulation);
            var row = coverage.AsSpan(y * width, width);
            var touched = false;
            for (var s = 0; s < SubSamples; s++)
            {
                var sy = rowTop + ((s + 0.5) / SubSamples);
                crossings.Clear();
                foreach (var edge in active)
                {
                    if (sy >= edge.MinY && sy < edge.MaxY)
                    {
                        crossings.Add((edge.XAt(sy), edge.Direction));
                    }
                }

                if (crossings.Count < 2)
                {
                    continue;
                }

                crossings.Sort(static (a, b) => a.X.CompareTo(b.X));
                var winding = 0;
                for (var c = 0; c < crossings.Count - 1; c++)
                {
                    winding += evenOdd ? 1 : crossings[c].Direction;
                    var inside = evenOdd ? (winding & 1) != 0 : winding != 0;
                    if (!inside)
                    {
                        continue;
                    }

                    var x0 = Math.Clamp(crossings[c].X, 0, width);
                    var x1 = Math.Clamp(crossings[c + 1].X, 0, width);
                    if (x1 <= x0)
                    {
                        continue;
                    }

                    touched = true;
                    var i0 = (int)x0;
                    var i1 = (int)x1;
                    if (i0 == i1)
                    {
                        row[Math.Min(i0, width - 1)] += (float)(x1 - x0) * sampleWeight;
                        continue;
                    }

                    row[i0] += (float)(i0 + 1 - x0) * sampleWeight;
                    if (i1 < width)
                    {
                        row[i1] += (float)(x1 - i1) * sampleWeight;
                    }

                    // Full cells between the partial ends go through a difference array.
                    if (i0 + 1 < i1)
                    {
                        accumulation[i0 + 1] += sampleWeight;
                        accumulation[i1] -= sampleWeight;
                    }
                }
            }

            if (!touched)
            {
                continue;
            }

            var running = 0f;
            for (var x = 0; x < width; x++)
            {
                running += accumulation[x];
                row[x] = Math.Clamp(row[x] + running, 0f, 1f);
            }
        }

        return coverage;
    }
}
