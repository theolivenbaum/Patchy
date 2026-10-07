using XRay.Psd.Layers;
using XRay.Psd.Rendering;
using XRay.Psd.Tests.Support;

namespace XRay.Psd.Tests;

/// <summary>Unit tests for the vector stroker: caps, joins, miter limit, dashes, alignment and band union.</summary>
public sealed class VectorStrokerTests
{
    private static readonly PsdRect Canvas = new(0, 0, 100, 100);

    private static VectorPath Path(bool closed, params (double X, double Y)[] points)
    {
        var subpath = new PathSubpath { Closed = closed };
        foreach (var (x, y) in points)
        {
            subpath.Knots.Add(new PathKnot(x, y, x, y, x, y, false));
        }

        return new VectorPath { Subpaths = [subpath] };
    }

    private sealed class Coverage((float[] Plane, PsdRect Rect) raster)
    {
        public float this[int x, int y] => raster.Rect.Contains(x, y) ? raster.Plane[((y - raster.Rect.Top) * raster.Rect.Width) + (x - raster.Rect.Left)] : 0f;

        public double Total => raster.Plane.Sum(v => (double)v);
    }

    private static Coverage Stroke(VectorPath path, VectorStrokeStyle style) => new(VectorStroker.Rasterize(path, style, Canvas));

    private static VectorStrokeStyle Style(double width, VectorStrokeCap cap = VectorStrokeCap.Butt, VectorStrokeJoin join = VectorStrokeJoin.Miter, VectorStrokeAlignment alignment = VectorStrokeAlignment.Center, double[]? dashes = null, double offset = 0, double miterLimit = 100) =>
        new() { Enabled = true, Width = width, Cap = cap, Join = join, Alignment = alignment, Dashes = dashes ?? [], DashOffset = offset, MiterLimit = miterLimit };

    [Fact]
    public void Butt_cap_line_covers_exactly_its_rectangle()
    {
        var coverage = Stroke(Path(false, (10, 20), (30, 20)), Style(4));

        for (var x = 10; x < 30; x++)
        {
            for (var y = 18; y < 22; y++)
            {
                Assert.Equal(1f, coverage[x, y], 3);
            }

            Assert.Equal(0f, coverage[x, 17]);
            Assert.Equal(0f, coverage[x, 22]);
        }

        Assert.Equal(0f, coverage[9, 20]);
        Assert.Equal(0f, coverage[30, 20]);
        Assert.Equal(80, coverage.Total, 2);
    }

    [Fact]
    public void Square_cap_extends_by_half_the_width()
    {
        var coverage = Stroke(Path(false, (10, 20), (30, 20)), Style(4, VectorStrokeCap.Square));

        Assert.Equal(1f, coverage[8, 20], 3);
        Assert.Equal(1f, coverage[31, 20], 3);
        Assert.Equal(0f, coverage[7, 20]);
        Assert.Equal(0f, coverage[32, 20]);
        Assert.Equal(96, coverage.Total, 2);
    }

    [Fact]
    public void Round_cap_adds_a_half_disc_at_each_end()
    {
        var coverage = Stroke(Path(false, (10, 20), (30, 20)), Style(4, VectorStrokeCap.Round));

        // 20 x 4 band plus two half discs of radius 2 (the fan is a polygon inscribed in the arc).
        Assert.Equal(80 + (Math.PI * 4), coverage.Total, 0);
        Assert.True(coverage[8, 20] > 0.9f);
        Assert.True(coverage[8, 18] < 0.5f);
        Assert.Equal(0f, coverage[7, 20]);
    }

    [Fact]
    public void Joins_fill_the_outer_corner_by_type()
    {
        var corner = Path(false, (20, 40), (20, 20), (40, 20));
        var miter = Stroke(corner, Style(6, join: VectorStrokeJoin.Miter));
        var round = Stroke(corner, Style(6, join: VectorStrokeJoin.Round));
        var bevel = Stroke(corner, Style(6, join: VectorStrokeJoin.Bevel));

        // The outer corner square spans (17..20, 17..20); its far pixel decides the join.
        Assert.Equal(1f, miter[17, 17], 3);
        Assert.Equal(0f, bevel[17, 17], 3);
        Assert.InRange(round[17, 17], 0f, 0.2f);
        Assert.Equal(1f, round[19, 19], 3);

        // Two 20 x 6 quads overlapping in a 3 x 3 square; the outer 3 x 3 corner adds 9 px
        // for a miter, a quarter disc for a round join and a half square for a bevel.
        var straight = (2 * 20 * 6) - 9;
        Assert.Equal(straight + 9, miter.Total, 1);
        Assert.Equal(straight + (Math.PI * 9 / 4), round.Total, 0);
        Assert.Equal(straight + 4.5, bevel.Total, 1);
    }

    [Fact]
    public void Miter_beyond_the_limit_falls_back_to_a_bevel()
    {
        // A 30 degree turn back: the miter ratio 1/sin(15 deg) is about 3.86.
        var angle = 15 * Math.PI / 180;
        var path = Path(false, (60, 50 - (30 * Math.Tan(angle))), (30, 50), (60, 50 + (30 * Math.Tan(angle))));
        var mitered = Stroke(path, Style(4, miterLimit: 4));
        var limited = Stroke(path, Style(4, miterLimit: 3));

        // The miter tip reaches 2 * 3.86 = 7.7 px left of the vertex.
        Assert.True(mitered[24, 50] > 0.5f);
        Assert.Equal(0f, limited[26, 50]);
        Assert.True(mitered.Total > limited.Total + 5);
    }

    [Fact]
    public void Dashes_alternate_in_stroke_width_multiples()
    {
        // Width 2, dash 2 (4 px on), gap 1 (2 px off), butt caps.
        var path = Path(false, (10, 30), (40, 30));
        var coverage = Stroke(path, Style(2, dashes: [2, 1]));

        var expected = new[] { 1, 1, 1, 1, 0, 0 };
        for (var x = 10; x < 40; x++)
        {
            Assert.Equal(expected[(x - 10) % 6], coverage[x, 30], 3);
        }

        // An offset of one width shifts the pattern back by 2 px.
        var shifted = Stroke(path, Style(2, dashes: [2, 1], offset: 1));
        for (var x = 10; x < 40; x++)
        {
            Assert.Equal(expected[(x - 10 + 2) % 6], shifted[x, 30], 3);
        }
    }

    [Fact]
    public void Zero_length_dashes_draw_round_dots()
    {
        // Photoshop's dotted preset {0, 2}: dots every two widths, kept round by the path tangent.
        var coverage = Stroke(Path(false, (10, 30), (50, 30)), Style(4, VectorStrokeCap.Round, dashes: [0, 2]));

        // Dots at x = 10, 18, 26, 34, 42 (the walk ends inside the last gap): five discs of radius 2.
        Assert.Equal(5 * Math.PI * 4, coverage.Total, 0);
        Assert.True(coverage[17, 29] > 0.9f && coverage[18, 30] > 0.9f);
        Assert.Equal(0f, coverage[22, 30]);
    }

    [Fact]
    public void Alignment_places_the_band_inside_centered_or_outside()
    {
        var square = Path(true, (20, 20), (60, 20), (60, 60), (20, 60));
        var center = Stroke(square, Style(4));
        var inside = Stroke(square, Style(4, alignment: VectorStrokeAlignment.Inside));
        var outside = Stroke(square, Style(4, alignment: VectorStrokeAlignment.Outside));

        // Along the left edge (row 40): center covers 18..21, inside 20..23, outside 16..19.
        int[] Covered(Coverage c) => [.. Enumerable.Range(10, 20).Where(x => c[x, 40] > 0.5f)];
        Assert.Equal([18, 19, 20, 21], Covered(center));
        Assert.Equal([20, 21, 22, 23], Covered(inside));
        Assert.Equal([16, 17, 18, 19], Covered(outside));

        // Inside: a 40 x 40 square minus a 32 x 32 hole; outside: 48 x 48 minus 40 x 40 (mitered corners).
        Assert.Equal((40 * 40) - (32 * 32), inside.Total, 1);
        Assert.Equal((48 * 48) - (40 * 40), outside.Total, 1);
    }

    [Fact]
    public void Overlapping_outline_loops_union_inside_a_pixel()
    {
        // A 3 px stroke on a rectangle: the inner corner pixel (39, 39) is three quarters covered,
        // not the sum of the two segment quads that meet there.
        var coverage = Stroke(Path(true, (38, 38), (58, 38), (58, 50), (38, 50)), Style(3));

        Assert.Equal(0.75f, coverage[39, 39], 2);
        Assert.Equal(0.5f, coverage[36, 44], 2);
        Assert.Equal(1f, coverage[37, 44], 3);
    }

    [Fact]
    public void Curves_flatten_onto_the_stroke()
    {
        // A circle of radius 20 from four kappa cubics, stroked 2 px wide: area 2 * pi * r * w.
        const double k = 0.5522847498 * 20;
        var subpath = new PathSubpath { Closed = true };
        subpath.Knots.Add(new PathKnot(50 - k, 30, 50, 30, 50 + k, 30, true));
        subpath.Knots.Add(new PathKnot(70, 50 - k, 70, 50, 70, 50 + k, true));
        subpath.Knots.Add(new PathKnot(50 + k, 70, 50, 70, 50 - k, 70, true));
        subpath.Knots.Add(new PathKnot(30, 50 + k, 30, 50, 30, 50 - k, true));
        var coverage = Stroke(new VectorPath { Subpaths = [subpath] }, Style(2, join: VectorStrokeJoin.Round));

        Assert.Equal(2 * Math.PI * 20 * 2, coverage.Total, 0);
        Assert.Equal(0f, coverage[50, 50]);
        Assert.Equal(1f, coverage[50, 30], 3);
    }

    [Fact]
    public void Vstk_blocks_parse_into_stroke_styles()
    {
        var document = Fixtures.Load("photoshop-shape-strokes.psd");
        VectorStrokeStyle StrokeOf(string name) => document.Layers.Single(l => l.Name == name).VectorStroke!;

        var center = StrokeOf("Color Fill 1");
        Assert.True(center.Enabled && center.FillEnabled);
        Assert.Equal(6, center.Width);
        Assert.Equal(VectorStrokeAlignment.Center, center.Alignment);
        Assert.Equal(VectorStrokeCap.Butt, center.Cap);
        Assert.Equal(VectorStrokeJoin.Miter, center.Join);
        Assert.Equal(100, center.MiterLimit);
        Assert.Equal(1, center.Opacity);
        Assert.Equal(PsdBlendMode.Normal, center.BlendMode);
        Assert.Equal("SoCo", center.ContentKey);
        Assert.Equal(VectorStrokeAlignment.Inside, StrokeOf("Color Fill 2").Alignment);
        Assert.Equal(VectorStrokeAlignment.Outside, StrokeOf("Color Fill 3").Alignment);

        var dashed = StrokeOf("Color Fill 4");
        Assert.False(dashed.FillEnabled);
        Assert.Equal(VectorStrokeCap.Round, dashed.Cap);
        Assert.Equal(VectorStrokeJoin.Round, dashed.Join);
        Assert.Equal([2.0, 1.0], dashed.Dashes);

        var beveled = StrokeOf("Color Fill 5");
        Assert.Equal(VectorStrokeCap.Square, beveled.Cap);
        Assert.Equal(VectorStrokeJoin.Bevel, beveled.Join);
        Assert.False(StrokeOf("Color Fill 6").FillEnabled);
    }
}
