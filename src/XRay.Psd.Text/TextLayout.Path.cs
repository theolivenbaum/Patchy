namespace XRay.Psd.Text;

/// <summary>Type on a path: one line of glyphs along <see cref="TextLayerInfo.Path"/>.</summary>
internal sealed partial class TextLayoutEngine
{
    /// <summary>
    /// Lays the text out as one line along the path, between the start and end markers
    /// (<see cref="TextPath.RangeStart"/>, <see cref="TextPath.RangeEnd"/>), measured by arc
    /// length. Each glyph's baseline midpoint sits on the path and the glyph turns to the
    /// tangent there (Illustrator's default "Rainbow" effect, the only one Photoshop offers).
    /// Left, center and right justification place the line at the start marker, the middle
    /// of the range and the end marker. A glyph whose midpoint falls outside the range is
    /// overset and not drawn, which is what Photoshop shows for <c>text-path.psd</c> (only
    /// "so" of "some text" fits before its end marker). Flipped text runs the other way along
    /// the path, which puts it on the other side. Paragraph separators add nothing: path
    /// text is a single line. Underline and strikethrough are not drawn on paths.
    /// </summary>
    private sealed class PathPass(TextLayoutEngine engine, TextLayerInfo info, string text, int[] styleOf, LaidOutText output)
    {
        public void Run(List<ParagraphSpan> paragraphs)
        {
            if (PathCurve.Create(info.Path!) is not { } curve || paragraphs.Count == 0)
            {
                return;
            }

            // Shape every paragraph and join the glyphs into one visual line.
            var glyphs = new List<(ShapedGlyph Glyph, double Gap)>();
            foreach (var span in paragraphs)
            {
                var shaped = engine.Shape(text, styleOf, output.Styles, span, vertical: false);
                var visual = VisualOrder([.. Enumerable.Range(0, shaped.Glyphs.Length)], g => shaped.Levels[shaped.Glyphs[g].Char]);
                for (var k = 0; k < visual.Length; k++)
                {
                    var glyph = shaped.Glyphs[visual[k]];
                    var endsCluster = k == visual.Length - 1 || shaped.Glyphs[visual[k + 1]].Char != glyph.Char;
                    glyphs.Add((glyph, endsCluster ? shaped.CharGap[glyph.Char] : 0));
                }
            }

            if (glyphs.Count == 0)
            {
                return;
            }

            // Tracking applies between glyphs, not after the last one.
            glyphs[^1] = (glyphs[^1].Glyph, 0);
            double width = 0;
            foreach (var (glyph, gap) in glyphs)
            {
                width += glyph.Advance + gap;
            }

            var (start, end) = curve.Range(info.Path!);
            var properties = paragraphs[0].Properties;
            var startIndent = (properties?.StartIndent ?? 0) + (properties?.FirstLineIndent ?? 0);
            var endIndent = properties?.EndIndent ?? 0;
            var pen = (properties?.Justification ?? TextJustification.Left) switch
            {
                TextJustification.Right or TextJustification.JustifyLastRight => end - endIndent - width,
                TextJustification.Center or TextJustification.JustifyLastCenter => ((start + startIndent + end - endIndent) / 2) - (width / 2),
                _ => start + startIndent,
            };

            const double Tolerance = 1e-6;
            foreach (var (glyph, gap) in glyphs)
            {
                var middle = pen + glyph.OffsetX + (glyph.Advance / 2);
                if (middle > end + Tolerance)
                {
                    break;
                }

                if (middle >= start - Tolerance)
                {
                    var style = output.Styles[glyph.Style];
                    var (point, tangent) = curve.At(middle);
                    var down = new TextPoint(-tangent.Y, tangent.X);
                    var drop = glyph.OffsetY - glyph.Rise;
                    var originX = point.X - (tangent.X * glyph.Advance / 2) + (down.X * drop);
                    var originY = point.Y - (tangent.Y * glyph.Advance / 2) + (down.Y * drop);
                    engine.AddPathGlyph(output, glyph, style, originX, originY, tangent);
                }

                pen += glyph.Advance + gap;
            }
        }
    }

    /// <summary>Adds a glyph turned to a path tangent: the upright glyph matrix, then the rotation to <paramref name="tangent"/>.</summary>
    private void AddPathGlyph(LaidOutText output, ShapedGlyph glyph, LayoutStyle style, double x, double y, TextPoint tangent)
    {
        var em = style.Size * glyph.Scale;
        var sx = em * style.HorizontalScale;
        var sy = em * style.VerticalScale;
        var shear = style.FauxItalic ? -FauxItalicSlant * sy : 0;
        var (cos, sin) = (tangent.X, tangent.Y);
        output.Glyphs.Add(new PlacedGlyph(
            glyph.Face,
            glyph.Id,
            glyph.Style,
            cos * sx,
            sin * sx,
            (cos * shear) - (sin * sy),
            (sin * shear) + (cos * sy),
            x,
            y));
    }

    /// <summary>A type path sampled by arc length: positions, tangents and the range markers in arc units.</summary>
    internal sealed class PathCurve
    {
        private const int SamplesPerSegment = 64;

        private readonly IReadOnlyList<TextPathSegment> _segments;
        private readonly double[] _arc; // arc length at each sample; sample i is segment i / N, parameter (i % N) / N
        private readonly bool _flipped;

        private PathCurve(IReadOnlyList<TextPathSegment> segments, double[] arc, bool flipped, bool closed)
        {
            _segments = segments;
            _arc = arc;
            _flipped = flipped;
            IsClosed = closed;
        }

        /// <summary>Total arc length in text-space units.</summary>
        public double Length => _arc[^1];

        public bool IsClosed { get; }

        public static PathCurve? Create(TextPath path)
        {
            var segments = path.Segments;
            // Photoshop paths have a handful of segments; a crafted frame could list millions.
            if (segments.Count == 0 || segments.Count > 10_000)
            {
                return null;
            }

            var arc = new double[(segments.Count * SamplesPerSegment) + 1];
            var previous = segments[0].Start;
            if (!double.IsFinite(previous.X) || !double.IsFinite(previous.Y))
            {
                return null;
            }

            double total = 0;
            for (var s = 0; s < segments.Count; s++)
            {
                for (var i = 1; i <= SamplesPerSegment; i++)
                {
                    var point = segments[s].PointAt((double)i / SamplesPerSegment);
                    if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
                    {
                        return null;
                    }

                    total += Math.Sqrt(((point.X - previous.X) * (point.X - previous.X)) + ((point.Y - previous.Y) * (point.Y - previous.Y)));
                    arc[(s * SamplesPerSegment) + i] = total;
                    previous = point;
                }
            }

            return total > 1e-9 && double.IsFinite(total) ? new PathCurve(segments, arc, path.Flipped, path.IsClosed) : null;
        }

        /// <summary>
        /// The text range in arc units along the reading direction. An empty or inverted range
        /// wraps around a closed path and falls back to the whole of an open one.
        /// </summary>
        public (double Start, double End) Range(TextPath path)
        {
            var start = ArcAtPosition(path.RangeStart);
            var end = ArcAtPosition(path.RangeEnd);
            if (_flipped)
            {
                (start, end) = (Length - end, Length - start);
            }

            if (end <= start)
            {
                return IsClosed ? (start, end + Length) : (0, Length);
            }

            return (start, end);
        }

        /// <summary>The point and unit tangent at an arc length along the reading direction.</summary>
        public (TextPoint Point, TextPoint Tangent) At(double distance)
        {
            if (IsClosed)
            {
                distance %= Length;
                if (distance < 0)
                {
                    distance += Length;
                }
            }

            var along = _flipped ? Length - distance : distance;
            var (segment, t) = ParameterAt(along);
            var point = _segments[segment].PointAt(t);
            var tangent = Derivative(_segments[segment], t);
            var length = Math.Sqrt((tangent.X * tangent.X) + (tangent.Y * tangent.Y));
            if (length < 1e-12)
            {
                // A control point on its anchor: use the chord around the sample.
                var before = _segments[segment].PointAt(Math.Max(0, t - 1e-3));
                var after = _segments[segment].PointAt(Math.Min(1, t + 1e-3));
                tangent = new TextPoint(after.X - before.X, after.Y - before.Y);
                length = Math.Sqrt((tangent.X * tangent.X) + (tangent.Y * tangent.Y));
                if (length < 1e-12)
                {
                    tangent = new TextPoint(1, 0);
                    length = 1;
                }
            }

            tangent = new TextPoint(tangent.X / length, tangent.Y / length);
            if (_flipped)
            {
                tangent = new TextPoint(-tangent.X, -tangent.Y);
            }

            return (point, tangent);
        }

        /// <summary>Arc length at a path position (segment index plus parameter), clamped to the path.</summary>
        public double ArcAtPosition(double position)
        {
            if (!double.IsFinite(position))
            {
                return 0;
            }

            var clamped = Math.Clamp(position, 0, _segments.Count);
            var sample = clamped * SamplesPerSegment;
            var index = Math.Min((int)sample, _arc.Length - 2);
            var fraction = sample - index;
            return _arc[index] + ((_arc[index + 1] - _arc[index]) * fraction);
        }

        private (int Segment, double T) ParameterAt(double distance)
        {
            distance = Math.Clamp(distance, 0, Length);
            var index = Array.BinarySearch(_arc, distance);
            if (index < 0)
            {
                index = Math.Max(0, ~index - 1);
            }

            index = Math.Min(index, _arc.Length - 2);
            var span = _arc[index + 1] - _arc[index];
            var fraction = span > 1e-12 ? (distance - _arc[index]) / span : 0;
            var segment = Math.Min(index / SamplesPerSegment, _segments.Count - 1);
            var t = (((index - (segment * SamplesPerSegment)) + fraction) / SamplesPerSegment);
            return (segment, Math.Clamp(t, 0, 1));
        }

        private static TextPoint Derivative(TextPathSegment s, double t)
        {
            var u = 1 - t;
            var a = 3 * u * u;
            var b = 6 * u * t;
            var c = 3 * t * t;
            return new TextPoint(
                (a * (s.Control1.X - s.Start.X)) + (b * (s.Control2.X - s.Control1.X)) + (c * (s.End.X - s.Control2.X)),
                (a * (s.Control1.Y - s.Start.Y)) + (b * (s.Control2.Y - s.Control1.Y)) + (c * (s.End.Y - s.Control2.Y)));
        }
    }
}
