using Patchy.Psd.Layers;
using Patchy.Psd.Rendering;
using Patchy.Psd.Text;

namespace Patchy.Psd.Tests;

/// <summary>Unit tests for the SIMD blend kernels, the path rasterizer and the format parsers.</summary>
public sealed class KernelTests
{
    // Photoshop's 8-bit kernels (from the reference compositor's blend_math.cpp).
    private static int Reference(PsdBlendMode mode, int s, int d) => mode switch
    {
        PsdBlendMode.Normal => s,
        PsdBlendMode.Multiply => s * d / 255,
        PsdBlendMode.Screen => 255 - ((255 - s) * (255 - d) / 255),
        PsdBlendMode.Overlay => d < 128 ? 2 * s * d / 255 : 255 - (2 * (255 - s) * (255 - d) / 255),
        PsdBlendMode.HardLight => s < 128 ? 2 * s * d / 255 : 255 - (2 * (255 - s) * (255 - d) / 255),
        PsdBlendMode.Darken => Math.Min(s, d),
        PsdBlendMode.Lighten => Math.Max(s, d),
        PsdBlendMode.Difference => Math.Abs(d - s),
        PsdBlendMode.Exclusion => s + d - (2 * ((s * d) + 127) / 255),
        PsdBlendMode.LinearBurn => Math.Clamp(s + d - 255, 0, 255),
        PsdBlendMode.LinearDodge => Math.Min(255, s + d),
        PsdBlendMode.Subtract => Math.Max(0, d - s),
        PsdBlendMode.ColorDodge => d == 0 ? 0 : s == 255 ? 255 : Math.Min(255, ((2 * d * 255) + (255 - s)) / (2 * (255 - s))),
        PsdBlendMode.ColorBurn => d == 255 ? 255 : s == 0 ? 0 : 255 - Math.Min(255, ((2 * (255 - d) * 255) + s) / (2 * s)),
        PsdBlendMode.LinearLight => Math.Clamp(d + (2 * s) - 256, 0, 255),
        PsdBlendMode.PinLight => s < 128 ? Math.Min(d, 2 * s) : Math.Max(d, 2 * (s - 128)),
        PsdBlendMode.Divide => d == 0 ? 0 : s == 0 ? 255 : Math.Min(255, ((d * 255) + (s / 2)) / s),
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    [Theory]
    [InlineData(PsdBlendMode.Normal, 0)]
    [InlineData(PsdBlendMode.Multiply, 1)]
    [InlineData(PsdBlendMode.Screen, 1)]
    [InlineData(PsdBlendMode.Overlay, 2)]
    [InlineData(PsdBlendMode.HardLight, 2)]
    [InlineData(PsdBlendMode.Darken, 0)]
    [InlineData(PsdBlendMode.Lighten, 0)]
    [InlineData(PsdBlendMode.Difference, 0)]
    [InlineData(PsdBlendMode.Exclusion, 1)]
    [InlineData(PsdBlendMode.LinearBurn, 0)]
    [InlineData(PsdBlendMode.LinearDodge, 0)]
    [InlineData(PsdBlendMode.Subtract, 0)]
    [InlineData(PsdBlendMode.ColorDodge, 1)]
    [InlineData(PsdBlendMode.ColorBurn, 1)]
    [InlineData(PsdBlendMode.LinearLight, 1)]
    [InlineData(PsdBlendMode.PinLight, 1)]
    [InlineData(PsdBlendMode.Divide, 1)]
    public void Separable_kernels_match_photoshop_formulas(PsdBlendMode mode, int tolerance)
    {
        // An opaque source over an opaque backdrop yields exactly B(s, d).
        const int step = 5;
        var values = Enumerable.Range(0, (255 / step) + 1).Select(v => v * step).ToArray();
        var count = values.Length * values.Length;
        var s = new float[count];
        var d = new float[count];
        var index = 0;
        foreach (var sv in values)
        {
            foreach (var dv in values)
            {
                s[index] = sv / 255f;
                d[index] = dv / 255f;
                index++;
            }
        }

        var alpha = Enumerable.Repeat(1f, count).ToArray();
        var dr = (float[])d.Clone();
        var dg = (float[])d.Clone();
        var db = (float[])d.Clone();
        var da = Enumerable.Repeat(1f, count).ToArray();
        BlendKernels.CompositeRow(mode, s, s, s, alpha, dr, dg, db, da, clipMode: false);

        for (var i = 0; i < count; i++)
        {
            var sv = (int)Math.Round(s[i] * 255);
            var dv = (int)Math.Round(d[i] * 255);
            var expected = Reference(mode, sv, dv);
            var actual = (int)Math.Round(dr[i] * 255);
            Assert.True(Math.Abs(expected - actual) <= tolerance, $"{mode}({sv}, {dv}) = {actual}, expected {expected}");
            Assert.Equal(dr[i], dg[i]);
            Assert.Equal(1f, da[i], 6);
        }
    }

    [Fact]
    public void Source_over_matches_porter_duff()
    {
        float[] sr = [1f, 0.2f];
        float[] sa = [0.5f, 0.25f];
        float[] dr = [0f, 0.8f];
        float[] dg = [0f, 0f];
        float[] db = [0f, 0f];
        float[] da = [1f, 0f];
        BlendKernels.CompositeRow(PsdBlendMode.Normal, sr, sr, sr, sa, dr, dg, db, da, clipMode: false);

        Assert.Equal(0.5f, dr[0], 5);
        Assert.Equal(1f, da[0], 5);
        Assert.Equal(0.2f, dr[1], 5); // over transparency the source color is kept straight
        Assert.Equal(0.25f, da[1], 5);
    }

    [Fact]
    public void Non_separable_modes_keep_luminosity_rules()
    {
        // Luminosity of a gray source onto a saturated red keeps the source luma.
        float[] s = [0.5f];
        float[] one = [1f];
        float[] r = [1f];
        float[] g = [0f];
        float[] b = [0f];
        float[] a = [1f];
        BlendKernels.CompositeRow(PsdBlendMode.Luminosity, s, s, s, one, r, g, b, a, clipMode: false);
        var luma = (0.3f * r[0]) + (0.59f * g[0]) + (0.11f * b[0]);
        Assert.Equal(0.5f, luma, 2);

        // Color of red onto mid gray keeps the backdrop luma (0.5) with red's hue.
        float[] red = [1f];
        float[] zero = [0f];
        float[] gr = [0.5f];
        float[] gg = [0.5f];
        float[] gb = [0.5f];
        BlendKernels.CompositeRow(PsdBlendMode.Color, red, zero, zero, one, gr, gg, gb, a, clipMode: false);
        Assert.Equal(0.5f, (0.3f * gr[0]) + (0.59f * gg[0]) + (0.11f * gb[0]), 2);
        Assert.True(gr[0] > gg[0] && gg[0] == gb[0]);
    }

    [Fact]
    public void Rasterizer_covers_axis_aligned_rectangles_exactly()
    {
        var polygon = new List<(double, double)> { (1, 1), (3, 1), (3, 2.5), (1, 2.5) };
        var coverage = PathRasterizer.Rasterize([polygon], new PsdRect(0, 0, 4, 4), evenOdd: true);

        Assert.Equal(0f, coverage[0]);
        Assert.Equal(1f, coverage[(1 * 4) + 1], 5);
        Assert.Equal(1f, coverage[(1 * 4) + 2], 5);
        Assert.Equal(0.5f, coverage[(2 * 4) + 1], 5);
        Assert.Equal(0f, coverage[(1 * 4) + 3]);
    }

    [Fact]
    public void Rasterizer_area_of_a_circle()
    {
        var circle = new List<(double, double)>();
        for (var i = 0; i < 720; i++)
        {
            var angle = i * Math.PI / 360;
            circle.Add((50 + (30 * Math.Cos(angle)), 50 + (30 * Math.Sin(angle))));
        }

        var coverage = PathRasterizer.Rasterize([circle], new PsdRect(0, 0, 100, 100), evenOdd: false);
        Assert.Equal(Math.PI * 900, coverage.Sum(), 0.005 * Math.PI * 900);
    }

    [Fact]
    public void Even_odd_groups_cut_holes_and_operations_combine()
    {
        var path = new VectorPath
        {
            Subpaths =
            [
                Rectangle(0, 0, 10, 10, PathCombineOperation.Add, group: 1),
                Rectangle(2, 2, 8, 8, PathCombineOperation.Add, group: 1), // same group: hole
                Rectangle(4, 4, 6, 6, PathCombineOperation.Add, group: 2), // new group: added back
                Rectangle(0, 0, 1, 10, PathCombineOperation.Subtract, group: 3),
            ],
        };

        var coverage = PathRasterizer.RasterizePath(path, new PsdRect(0, 0, 10, 10));
        float At(int x, int y) => coverage[(y * 10) + x];
        Assert.Equal(0f, At(0, 5));
        Assert.Equal(1f, At(1, 1));
        Assert.Equal(0f, At(3, 3));
        Assert.Equal(1f, At(5, 5));
    }

    private static PathSubpath Rectangle(double left, double top, double right, double bottom, PathCombineOperation op, int group)
    {
        var subpath = new PathSubpath { Closed = true, Operation = op, ShapeGroup = group };
        foreach (var (x, y) in new[] { (left, top), (right, top), (right, bottom), (left, bottom) })
        {
            subpath.Knots.Add(new PathKnot(x, y, x, y, x, y, false));
        }

        return subpath;
    }

    [Fact]
    public void EngineData_parses_nested_structures()
    {
        // Strings are UTF-16BE with a byte-order mark; a ")" inside is escaped.
        var text = System.Text.Encoding.Latin1.GetBytes("\n<< /EngineDict << /Editor << /Text (")
            .Concat(new byte[] { 0xFE, 0xFF, 0, (byte)'H', 0, (byte)'i', 0, (byte)'\\', (byte)')', 0, (byte)'\r' })
            .Concat(System.Text.Encoding.Latin1.GetBytes(") >> /Numbers [ 1 -2.5 .75 ] /Flag true /Name /Roman >> >>"))
            .ToArray();

        var root = EngineDataParser.Parse(text);

        Assert.NotNull(root);
        Assert.Equal("Hi)\r", root.Get("EngineDict", "Editor", "Text")!.Text);
        var numbers = root.Get("EngineDict", "Numbers")!;
        Assert.Equal([1, -2.5, 0.75], numbers.Items.Select(i => i.Number));
        Assert.True(root.Get("EngineDict", "Flag")!.Boolean);
        Assert.Equal("Roman", root.Get("EngineDict", "Name")!.Text);
        Assert.Null(EngineDataParser.Parse("<< /Broken (unterminated"u8));
    }

    [Fact]
    public void Feather_box_radii_follow_the_gauss_split()
    {
        Assert.Equal([0, 0, 0], MaskSampler.BoxRadii(0.1));
        var radii = MaskSampler.BoxRadii(5);
        var variance = radii.Sum(r => (((2 * r) + 1) * ((2 * r) + 1)) - 1) / 12.0;
        Assert.Equal(25, variance, 4.0);
    }
}
