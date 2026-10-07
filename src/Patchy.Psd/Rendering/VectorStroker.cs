using Patchy.Psd.Layers;

namespace Patchy.Psd.Rendering;

/// <summary>
/// Vector stroke geometry and coverage, ported from the stroker in
/// <c>.reference/src/core/vector_raster.cpp</c>: subpaths flatten to polylines
/// on the 1/256 pixel lattice, dashes split them into runs, and each run emits
/// closed outline loops (segment quads plus join and cap fans) whose union is
/// rasterized under the non-zero rule. Inside and outside strokes rasterize the
/// centered band at double width and keep the half inside (or outside) the
/// path's fill coverage.
/// <para>
/// One deliberate difference: the reference accumulates exact cell areas, which
/// adds overlapping loops inside a pixel (an inner miter corner of a 3 px stroke
/// came out fully covered where Photoshop shows about 0.78). Sampling the union
/// on 16 sub-scanlines with exact horizontal coverage matches the Photoshop
/// capture (<c>photoshop-shape-strokes</c>) much more closely.
/// </para>
/// </summary>
internal static class VectorStroker
{
    // 24.8 fixed point: one pixel is 256 subpixels.
    private const int Sub = 256;

    // Keeps 24.8 fixed-point coordinates inside 32-bit range.
    private const double CoordinateLimit = 1 << 22;

    private const int MaxDashBoundaries = 262144;

    // Sub-scanlines per pixel row of the band rasterizer.
    private const int SampleRows = 16;

    internal readonly record struct Point(double X, double Y);

    private readonly record struct FixedPoint(int X, int Y);

    private readonly record struct Edge(FixedPoint From, FixedPoint To);

    /// <summary>One continuous piece of a stroke: a whole subpath, or one dash.</summary>
    internal sealed class StrokeRun
    {
        public List<Point> Points { get; init; } = [];

        public bool Closed { get; init; }

        /// <summary>A zero-length dash still needs its path tangent to orient square and round caps.</summary>
        public Point DotDirection { get; init; }
    }

    /// <summary>
    /// Rasterizes the stroke of <paramref name="path"/> clipped to <paramref name="clip"/>.
    /// Returns the coverage plane and its rectangle (the outline's hull within the clip), or
    /// an empty result when nothing is stroked.
    /// </summary>
    public static (float[] Coverage, PsdRect Rect) Rasterize(VectorPath path, VectorStrokeStyle stroke, PsdRect clip)
    {
        if (clip.IsEmpty || path.Subpaths.Count == 0 || !(stroke.Width > 0))
        {
            return ([], default);
        }

        // Inside/outside strokes rasterize the centered band at double width, then clip
        // by the fill region (or its complement): the kept half is exactly `width` deep
        // and its path-side edge keeps the fill coverage's anti-aliasing.
        var centered = stroke.Alignment == VectorStrokeAlignment.Center;
        var half = (centered ? stroke.Width : stroke.Width * 2) / 2;

        // Doubling the band must not double dash caps: that would fill the gaps of the
        // dotted presets and clip round dots into oversized semicircles.
        var capHalfWidth = !centered && stroke.Dashes.Count > 0 ? stroke.Width / 2 : half;
        var dashes = new double[stroke.Dashes.Count];
        for (var i = 0; i < dashes.Length; i++)
        {
            dashes[i] = stroke.Dashes[i] * stroke.Width;
        }

        var offset = stroke.DashOffset * stroke.Width;

        // The band is sized from the emitted outline's hull: miter tips can reach
        // miter-limit times the half width past the path.
        var edges = new List<Edge>();
        foreach (var subpath in path.Subpaths)
        {
            var polyline = SubpathPolyline(subpath);
            if (polyline.Count < 2)
            {
                continue;
            }

            foreach (var run in ApplyDashes(polyline, subpath.Closed, dashes, offset))
            {
                AppendRunOutline(run, half, capHalfWidth, stroke.Cap, stroke.Join, stroke.MiterLimit, edges);
            }
        }

        if (edges.Count == 0)
        {
            return ([], default);
        }

        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        foreach (var edge in edges)
        {
            minX = Math.Min(minX, Math.Min(edge.From.X, edge.To.X));
            maxX = Math.Max(maxX, Math.Max(edge.From.X, edge.To.X));
            minY = Math.Min(minY, Math.Min(edge.From.Y, edge.To.Y));
            maxY = Math.Max(maxY, Math.Max(edge.From.Y, edge.To.Y));
        }

        var band = new PsdRect(FloorDiv(minX), FloorDiv(minY), FloorDiv(maxX) + 1, FloorDiv(maxY) + 1).Intersect(clip);
        if (band.IsEmpty)
        {
            return ([], default);
        }

        var shiftX = band.Left * Sub;
        var shiftY = band.Top * Sub;
        for (var i = 0; i < edges.Count; i++)
        {
            var edge = edges[i];
            edges[i] = new Edge(
                new FixedPoint(edge.From.X - shiftX, edge.From.Y - shiftY),
                new FixedPoint(edge.To.X - shiftX, edge.To.Y - shiftY));
        }

        var coverage = RasterizeUnion(edges, band.Width, band.Height);
        if (!centered)
        {
            var region = PathRasterizer.RasterizePath(path, band);
            var inside = stroke.Alignment == VectorStrokeAlignment.Inside;
            for (var i = 0; i < coverage.Length; i++)
            {
                coverage[i] *= inside ? region[i] : 1f - region[i];
            }
        }

        return (coverage, band);
    }

    private static int FloorDiv(int value) => value >= 0 ? value / Sub : -((-value + Sub - 1) / Sub);

    private static int ToFixed(double value)
    {
        if (double.IsNaN(value))
        {
            return 0;
        }

        return (int)Math.Round(Math.Clamp(value, -CoordinateLimit, CoordinateLimit) * Sub, MidpointRounding.AwayFromZero);
    }

    // Integer de Casteljau halving, so the flattening is identical on every platform.
    private static void FlattenCubicRecursive(FixedPoint p0, FixedPoint p1, FixedPoint p2, FixedPoint p3, int depth, List<Edge> edges)
    {
        if (depth <= 0)
        {
            if (p0 != p3)
            {
                edges.Add(new Edge(p0, p3));
            }

            return;
        }

        static int Mid(int a, int b) => (int)(((long)a + b) >> 1);
        var m01 = new FixedPoint(Mid(p0.X, p1.X), Mid(p0.Y, p1.Y));
        var m12 = new FixedPoint(Mid(p1.X, p2.X), Mid(p1.Y, p2.Y));
        var m23 = new FixedPoint(Mid(p2.X, p3.X), Mid(p2.Y, p3.Y));
        var m012 = new FixedPoint(Mid(m01.X, m12.X), Mid(m01.Y, m12.Y));
        var m123 = new FixedPoint(Mid(m12.X, m23.X), Mid(m12.Y, m23.Y));
        var m = new FixedPoint(Mid(m012.X, m123.X), Mid(m012.Y, m123.Y));
        FlattenCubicRecursive(p0, m01, m012, m, depth - 1, edges);
        FlattenCubicRecursive(m, m123, m23, p3, depth - 1, edges);
    }

    private static void FlattenCubic(FixedPoint from, FixedPoint c1, FixedPoint c2, FixedPoint to, List<Edge> edges)
    {
        var deviation = Math.Max(
            Math.Max(Math.Abs((long)from.X - (2L * c1.X) + c2.X), Math.Abs((long)from.Y - (2L * c1.Y) + c2.Y)),
            Math.Max(Math.Abs((long)c1.X - (2L * c2.X) + to.X), Math.Abs((long)c1.Y - (2L * c2.Y) + to.Y)));
        if (deviation <= 8)
        {
            // Within 1/32 px of straight.
            if (from != to)
            {
                edges.Add(new Edge(from, to));
            }

            return;
        }

        // Chord error after n segments is at most 3 * deviation / (4 n^2); each halving
        // quarters it, so search the depth that reaches 8 subpixels (1/32 px).
        var depth = 1;
        var error = deviation * 3 / 4;
        while (error > 8 && depth < 8)
        {
            error /= 4;
            depth++;
        }

        FlattenCubicRecursive(from, c1, c2, to, depth, edges);
    }

    /// <summary>
    /// Flattens one subpath to a polyline in document pixels. Every vertex snaps to the
    /// 1/256 lattice so sub-quantum micro-segments cannot seed miter spikes; a closed
    /// subpath drops its duplicated closing point.
    /// </summary>
    internal static List<Point> SubpathPolyline(PathSubpath subpath)
    {
        var points = new List<Point>();
        var knots = subpath.Knots;
        var count = knots.Count;
        if (count == 0)
        {
            return points;
        }

        void Push(double x, double y)
        {
            var point = new Point((double)ToFixed(x) / Sub, (double)ToFixed(y) / Sub);
            if (points.Count > 0 && points[^1] == point)
            {
                return;
            }

            points.Add(point);
        }

        Push(knots[0].X, knots[0].Y);
        var segments = subpath.Closed ? count : count - 1;
        var flattened = new List<Edge>();
        for (var i = 0; i < segments; i++)
        {
            var a = knots[i];
            var b = knots[(i + 1) % count];
            var from = new FixedPoint(ToFixed(a.X), ToFixed(a.Y));
            var c1 = new FixedPoint(ToFixed(a.OutX), ToFixed(a.OutY));
            var c2 = new FixedPoint(ToFixed(b.InX), ToFixed(b.InY));
            var to = new FixedPoint(ToFixed(b.X), ToFixed(b.Y));
            if (c1 != from || c2 != to)
            {
                flattened.Clear();
                FlattenCubic(from, c1, c2, to, flattened);
                foreach (var edge in flattened)
                {
                    Push((double)edge.To.X / Sub, (double)edge.To.Y / Sub);
                }
            }

            // Lands the exact endpoint (the flattener skips zero-length tails).
            Push(b.X, b.Y);
        }

        if (subpath.Closed && points.Count > 1 && points[0] == points[^1])
        {
            points.RemoveAt(points.Count - 1);
        }

        return points;
    }

    private static double Distance(Point a, Point b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    /// <summary>
    /// Splits a polyline into dash runs. <paramref name="dashes"/> and
    /// <paramref name="offset"/> are in pixels; an empty or all-zero pattern keeps
    /// the whole polyline as one run.
    /// </summary>
    internal static List<StrokeRun> ApplyDashes(List<Point> points, bool closed, IReadOnlyList<double> dashes, double offset)
    {
        var runs = new List<StrokeRun>();
        if (points.Count < 2)
        {
            return runs;
        }

        var lengths = new double[dashes.Count];
        var total = 0.0;
        for (var i = 0; i < lengths.Length; i++)
        {
            var dash = dashes[i];
            if (!double.IsFinite(dash))
            {
                return [new StrokeRun { Points = points, Closed = closed }];
            }

            // Sub-lattice dash lengths clamp to the raster lattice.
            lengths[i] = dash > 0 ? Math.Max(dash, 1.0 / Sub) : Math.Max(dash, 0);
            total += lengths[i];
        }

        if (lengths.Length == 0 || !double.IsFinite(total) || total <= 0)
        {
            return [new StrokeRun { Points = points, Closed = closed }];
        }

        if (!double.IsFinite(offset))
        {
            offset = 0;
        }

        var walk = new List<Point>(points);
        if (closed)
        {
            walk.Add(points[0]);
        }

        var phase = offset % total;
        if (phase < 0)
        {
            phase += total;
        }

        var index = 0;
        while (phase > 0 && phase >= lengths[index])
        {
            phase -= lengths[index];
            index = (index + 1) % lengths.Length;
            if (phase <= 0)
            {
                break;
            }
        }

        var on = index % 2 == 0;
        var remaining = lengths[index] - phase;
        var current = new List<Point>();
        var boundaries = 0;

        void FinishRun(Point direction)
        {
            if (current.Count >= 2)
            {
                runs.Add(new StrokeRun { Points = [.. current], DotDirection = direction });
            }

            current.Clear();
        }

        if (on)
        {
            current.Add(walk[0]);
        }

        for (var i = 0; i + 1 < walk.Count; i++)
        {
            var a = walk[i];
            var b = walk[i + 1];
            var segmentLeft = Distance(a, b);
            while (segmentLeft > remaining && remaining >= 0)
            {
                if (++boundaries > MaxDashBoundaries)
                {
                    return [new StrokeRun { Points = points, Closed = closed }];
                }

                var t = remaining / segmentLeft;
                var cut = new Point(a.X + ((b.X - a.X) * t), a.Y + ((b.Y - a.Y) * t));
                if (on)
                {
                    current.Add(cut);
                    FinishRun(new Point((b.X - a.X) / segmentLeft, (b.Y - a.Y) / segmentLeft));
                }
                else
                {
                    current.Clear();
                    current.Add(cut);
                }

                on = !on;
                segmentLeft -= remaining;
                a = cut;
                index = (index + 1) % lengths.Length;

                // A zero entry advances on the next iteration, emitting a real dot for an on-entry.
                remaining = lengths[index];
            }

            remaining -= segmentLeft;
            if (on)
            {
                current.Add(b);
            }
        }

        FinishRun(default);
        return runs;
    }

    // Emits one closed loop. The band unions its loops under non-zero winding, so every
    // loop must carry the same orientation or an opposite wedge cancels the quads it overlaps.
    private static void AppendLoop(ReadOnlySpan<Point> loop, List<Edge> edges)
    {
        if (loop.Length < 3)
        {
            return;
        }

        var doubledArea = 0.0;
        for (var i = 0; i < loop.Length; i++)
        {
            var a = loop[i];
            var b = loop[(i + 1) % loop.Length];
            doubledArea += (a.X * b.Y) - (b.X * a.Y);
        }

        var reverse = doubledArea > 0;
        FixedPoint At(ReadOnlySpan<Point> l, int index)
        {
            var p = reverse ? l[l.Length - 1 - index] : l[index];
            return new FixedPoint(ToFixed(p.X), ToFixed(p.Y));
        }

        var previous = At(loop, 0);
        var first = previous;
        for (var i = 1; i < loop.Length; i++)
        {
            var point = At(loop, i);
            if (point != previous)
            {
                edges.Add(new Edge(previous, point));
            }

            previous = point;
        }

        if (previous != first)
        {
            edges.Add(new Edge(previous, first));
        }
    }

    private static void AppendTriangle(Point a, Point b, Point c, List<Edge> edges) => AppendLoop([a, b, c], edges);

    // Arc fan between two unit vectors around center, by normalized-midpoint halving (no trig).
    private static void AppendArcFan(Point center, Point from, Point to, double radius, List<Edge> edges, int depth = 0)
    {
        var chordX = to.X - from.X;
        var chordY = to.Y - from.Y;
        var chord = Math.Sqrt((chordX * chordX) + (chordY * chordY));
        if (depth >= 6 || chord * radius <= 0.25)
        {
            AppendTriangle(
                center,
                new Point(center.X + (from.X * radius), center.Y + (from.Y * radius)),
                new Point(center.X + (to.X * radius), center.Y + (to.Y * radius)),
                edges);
            return;
        }

        var midX = from.X + to.X;
        var midY = from.Y + to.Y;
        var midLength = Math.Sqrt((midX * midX) + (midY * midY));
        if (midLength <= 1e-12)
        {
            // Opposite vectors (a half circle): split through the perpendicular.
            midX = -from.Y;
            midY = from.X;
        }
        else
        {
            midX /= midLength;
            midY /= midLength;
        }

        var mid = new Point(midX, midY);
        AppendArcFan(center, from, mid, radius, edges, depth + 1);
        AppendArcFan(center, mid, to, radius, edges, depth + 1);
    }

    /// <summary>Builds the outline loops of one run at half width <paramref name="h"/>.</summary>
    private static void AppendRunOutline(StrokeRun run, double h, double capHalfWidth, VectorStrokeCap cap, VectorStrokeJoin join, double miterLimit, List<Edge> edges)
    {
        var points = run.Points;
        if (points.Count < 2 || h <= 0)
        {
            return;
        }

        var segmentCount = run.Closed ? points.Count : points.Count - 1;
        var directions = new Point[segmentCount];
        for (var i = 0; i < segmentCount; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Count];
            var length = Distance(a, b);
            if (length <= 1e-12)
            {
                continue;
            }

            var d = new Point((b.X - a.X) / length, (b.Y - a.Y) / length);
            directions[i] = d;
            var nx = -d.Y * h;
            var ny = d.X * h;
            AppendLoop(
                [new Point(a.X + nx, a.Y + ny), new Point(b.X + nx, b.Y + ny), new Point(b.X - nx, b.Y - ny), new Point(a.X - nx, a.Y - ny)],
                edges);
        }

        // Joins at interior vertices (every vertex of a closed run).
        var firstJoin = run.Closed ? 0 : 1;
        var joinCount = run.Closed ? points.Count : points.Count - 2;
        for (var j = 0; j < joinCount; j++)
        {
            var vertex = (firstJoin + j) % points.Count;
            var du = directions[(vertex + segmentCount - 1) % segmentCount];
            var dv = directions[vertex % segmentCount];
            if ((du.X == 0 && du.Y == 0) || (dv.X == 0 && dv.Y == 0))
            {
                continue;
            }

            var cross = (du.X * dv.Y) - (du.Y * dv.X);
            if (Math.Abs(cross) <= 1e-12)
            {
                // Straight or a reversal: the quads already overlap.
                continue;
            }

            // Normals on the outer side of the turn.
            var side = cross > 0 ? -1.0 : 1.0;
            var nIn = new Point(-du.Y * side, du.X * side);
            var nOut = new Point(-dv.Y * side, dv.X * side);
            var v = points[vertex];
            var inner = new Point(v.X + (nIn.X * h), v.Y + (nIn.Y * h));
            var outer = new Point(v.X + (nOut.X * h), v.Y + (nOut.Y * h));
            switch (join)
            {
                case VectorStrokeJoin.Bevel:
                    AppendTriangle(v, inner, outer, edges);
                    break;
                case VectorStrokeJoin.Round:
                    AppendArcFan(v, nIn, nOut, h, edges);
                    break;
                default:
                    {
                        // Miter: the tip lies along the normal bisector at h / cos(alpha / 2); a
                        // ratio past the limit falls back to a bevel.
                        var bisX = nIn.X + nOut.X;
                        var bisY = nIn.Y + nOut.Y;
                        var bisLength = Math.Sqrt((bisX * bisX) + (bisY * bisY));
                        if (bisLength <= 1e-12)
                        {
                            AppendTriangle(v, inner, outer, edges);
                            break;
                        }

                        bisX /= bisLength;
                        bisY /= bisLength;
                        var cosHalf = (nIn.X * bisX) + (nIn.Y * bisY);
                        var ratio = cosHalf > 1e-9 ? 1.0 / cosHalf : 1e9;
                        if (ratio > Math.Max(miterLimit, 1.0))
                        {
                            AppendTriangle(v, inner, outer, edges);
                        }
                        else
                        {
                            var tip = new Point(v.X + (bisX * h * ratio), v.Y + (bisY * h * ratio));
                            AppendLoop([v, inner, tip, outer], edges);
                        }

                        break;
                    }
            }
        }

        if (run.Closed || cap == VectorStrokeCap.Butt)
        {
            return;
        }

        var firstDirection = run.DotDirection;
        foreach (var d in directions)
        {
            if (d.X != 0 || d.Y != 0)
            {
                firstDirection = d;
                break;
            }
        }

        var lastDirection = run.DotDirection;
        for (var i = directions.Length - 1; i >= 0; i--)
        {
            if (directions[i].X != 0 || directions[i].Y != 0)
            {
                lastDirection = directions[i];
                break;
            }
        }

        AppendCap(points[0], new Point(-firstDirection.X, -firstDirection.Y), h, capHalfWidth, cap, edges);
        AppendCap(points[^1], lastDirection, h, capHalfWidth, cap, edges);
    }

    private static void AppendCap(Point end, Point direction, double h, double radius, VectorStrokeCap cap, List<Edge> edges)
    {
        if (direction.X == 0 && direction.Y == 0)
        {
            return;
        }

        var n = new Point(-direction.Y, direction.X);
        void Emit(Point center)
        {
            if (cap == VectorStrokeCap.Square)
            {
                AppendLoop(
                    [
                        new Point(center.X + (n.X * radius), center.Y + (n.Y * radius)),
                        new Point(center.X + (n.X * radius) + (direction.X * radius), center.Y + (n.Y * radius) + (direction.Y * radius)),
                        new Point(center.X - (n.X * radius) + (direction.X * radius), center.Y - (n.Y * radius) + (direction.Y * radius)),
                        new Point(center.X - (n.X * radius), center.Y - (n.Y * radius)),
                    ],
                    edges);
            }
            else
            {
                AppendArcFan(center, n, direction, radius, edges);
                AppendArcFan(center, direction, new Point(-n.X, -n.Y), radius, edges);
            }
        }

        var shift = h - radius;
        if (shift > 0)
        {
            // An aligned dash has the original width on each side of the path: each
            // half band gets its own normal-sized cap, and the fill clip picks the side.
            Emit(new Point(end.X + (n.X * shift), end.Y + (n.Y * shift)));
            Emit(new Point(end.X - (n.X * shift), end.Y - (n.Y * shift)));
        }
        else
        {
            Emit(end);
        }
    }

    /// <summary>
    /// Non-zero union of the outline loops (buffer-relative 24.8 edges): per
    /// sub-scanline, the spans where the winding is non-zero get exact horizontal
    /// coverage, so overlapping loops never add up past their union inside a pixel.
    /// </summary>
    private static float[] RasterizeUnion(List<Edge> edges, int width, int height)
    {
        var coverage = new float[width * height];
        var order = new List<int>(edges.Count);
        for (var i = 0; i < edges.Count; i++)
        {
            if (edges[i].From.Y != edges[i].To.Y)
            {
                order.Add(i);
            }
        }

        order.Sort((a, b) => Math.Min(edges[a].From.Y, edges[a].To.Y).CompareTo(Math.Min(edges[b].From.Y, edges[b].To.Y)));
        var active = new List<int>();
        var crossings = new List<(double X, int Direction)>();
        var accumulation = new float[width + 1];
        var next = 0;
        var samples = SampleRows;
        var weight = 1f / samples;
        for (var y = 0; y < height; y++)
        {
            var rowTop = (long)y * Sub;
            var rowBottom = rowTop + Sub;
            while (next < order.Count && Math.Min(edges[order[next]].From.Y, edges[order[next]].To.Y) < rowBottom)
            {
                active.Add(order[next++]);
            }

            active.RemoveAll(i => Math.Max(edges[i].From.Y, edges[i].To.Y) <= rowTop);
            if (active.Count == 0)
            {
                continue;
            }

            Array.Clear(accumulation);
            var row = coverage.AsSpan(y * width, width);
            for (var s = 0; s < samples; s++)
            {
                var sy = y + ((s + 0.5) / samples);
                crossings.Clear();
                foreach (var index in active)
                {
                    var edge = edges[index];
                    double y0 = edge.From.Y / (double)Sub, y1 = edge.To.Y / (double)Sub;
                    if (sy >= Math.Min(y0, y1) && sy < Math.Max(y0, y1))
                    {
                        double x0 = edge.From.X / (double)Sub, x1 = edge.To.X / (double)Sub;
                        crossings.Add((x0 + ((x1 - x0) * (sy - y0) / (y1 - y0)), y1 > y0 ? 1 : -1));
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
                    winding += crossings[c].Direction;
                    if (winding == 0)
                    {
                        continue;
                    }

                    var start = Math.Clamp(crossings[c].X, 0, width);
                    var end = Math.Clamp(crossings[c + 1].X, 0, width);
                    if (end <= start)
                    {
                        continue;
                    }

                    var i0 = (int)start;
                    var i1 = (int)end;
                    if (i0 == i1)
                    {
                        row[Math.Min(i0, width - 1)] += (float)(end - start) * weight;
                        continue;
                    }

                    row[i0] += (float)(i0 + 1 - start) * weight;
                    if (i1 < width)
                    {
                        row[i1] += (float)(end - i1) * weight;
                    }

                    if (i0 + 1 < i1)
                    {
                        accumulation[i0 + 1] += weight;
                        accumulation[i1] -= weight;
                    }
                }
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
