using XRay.Psd.Rendering;
using XRay.Psd.Tests.Support;

namespace XRay.Psd.Tests;

/// <summary>
/// Advanced Blending: Blend If gates, the eight special-Fill modes and channel
/// restrictions. Expected values come from the reference tests
/// (.reference/tests/core/compositor_blend_if_tests.cpp), which pin them against
/// Photoshop 2026 captures.
/// </summary>
public sealed class BlendingTests
{
    private static readonly byte[] IdentityEntry = [0, 0, 255, 255, 0, 0, 255, 255];

    // ---- Blend If math --------------------------------------------------------

    [Theory]
    [InlineData(63, 0)]
    [InlineData(64, 255)]
    [InlineData(192, 255)]
    [InlineData(193, 0)]
    public void Joined_handles_cut_off_hard(byte value, byte expected) =>
        Assert.Equal(expected, BlendIf.ThresholdByte(64, 64, 192, 192, value));

    [Theory]
    [InlineData(9, 0)]
    [InlineData(10, 63)]
    [InlineData(11, 127)]
    [InlineData(12, 191)]
    [InlineData(13, 255)]
    [InlineData(20, 255)]
    [InlineData(21, 191)]
    [InlineData(22, 127)]
    [InlineData(23, 63)]
    [InlineData(24, 0)]
    public void Split_handles_feather_both_byte_endpoints(byte value, byte expected) =>
        Assert.Equal(expected, BlendIf.ThresholdByte(10, 13, 20, 23, value));

    [Fact]
    public void Composite_gray_uses_fixed_thousandths()
    {
        Assert.Equal(76, BlendIf.GrayValue(255, 0, 0));
        Assert.Equal(150, BlendIf.GrayValue(0, 255, 0));
        Assert.Equal(28, BlendIf.GrayValue(0, 0, 255));
        Assert.Equal(11, BlendIf.GrayValue(11, 11, 11));
    }

    [Fact]
    public void Channel_gates_multiply_with_truncation()
    {
        // Gray This {9,12} gives 191 at 11, Red This {10,13} 127, Blue This {0,0,9,12} 127:
        // 255 -> 191 -> 95 -> 95 -> 47. Underlying Gray {10,13} 127, Green {0,0,10,13} 191,
        // Blue {9,12} 191: 255 -> 127 -> 127 -> 95 -> 71.
        var gate = BlendIf.Parse(Ranges(
            gray: ([9, 12, 255, 255], [10, 13, 255, 255]),
            red: ([10, 13, 255, 255], [0, 0, 255, 255]),
            green: ([0, 0, 255, 255], [0, 0, 10, 13]),
            blue: ([0, 0, 9, 12], [9, 12, 255, 255])));

        Assert.NotNull(gate);
        Assert.True(gate.HasSource);
        Assert.True(gate.HasUnderlying);
        Assert.Equal(47, gate.SourceByte(11, 11, 11));
        Assert.Equal(71, gate.UnderlyingByte(11, 11, 11));
    }

    [Fact]
    public void Identity_and_unsupported_payloads_render_no_gate()
    {
        Assert.Null(BlendIf.Parse(Ranges()));
        Assert.Null(BlendIf.Parse([]));
        Assert.Null(BlendIf.Parse(IdentityEntry));

        var odd = Ranges(gray: ([0, 0, 200, 255], [0, 0, 255, 255]))[..39];
        Assert.Null(BlendIf.Parse(odd));

        var unordered = Ranges(gray: ([20, 10, 200, 240], [0, 0, 255, 255]));
        Assert.Null(BlendIf.Parse(unordered));

        var tail = Ranges(gray: ([0, 0, 200, 255], [0, 0, 255, 255]));
        tail[33] = 1;
        Assert.Null(BlendIf.Parse(tail));
    }

    // ---- Blend If in the compositor -------------------------------------------

    [Fact]
    public void This_layer_gate_scales_alpha_not_color()
    {
        // A 128-alpha gray-100 pixel at a 127/255 gate exports with alpha 64 and its own RGB.
        var builder = new PsdBuilder { Width = 1, Height = 1 };
        var layer = Layer("this", 1, 100, 100, 100, 128);
        layer.BlendingRanges = Ranges(gray: ([99, 102, 255, 255], [0, 0, 255, 255]));
        builder.Layers.Add(layer);

        Assert.Equal(new PsdColor(100, 100, 100, 64), Render(builder).GetPixel(0, 0));
    }

    [Fact]
    public void Underlying_gate_tests_only_the_covered_backdrop()
    {
        // A black backdrop at alpha 0, 128 and 255 under a red layer with Underlying
        // Gray {128,128}: transparency passes, so the gate is 1 - backdrop alpha.
        var builder = new PsdBuilder { Width = 3, Height = 1 };
        var backdrop = Layer("backdrop", 3, 0, 0, 0, 255);
        backdrop.Channels[-1] = [0, 128, 255];
        builder.Layers.Add(backdrop);
        var top = Layer("top", 3, 255, 0, 0, 255);
        top.BlendingRanges = Ranges(gray: ([0, 0, 255, 255], [128, 128, 255, 255]));
        builder.Layers.Add(top);

        var image = Render(builder);
        Assert.Equal(new PsdColor(255, 0, 0, 255), image.GetPixel(0, 0));
        AssertNear(new PsdColor(169, 0, 0, 191), image.GetPixel(1, 0));
        Assert.Equal(new PsdColor(0, 0, 0, 255), image.GetPixel(2, 0));
    }

    [Fact]
    public void Blend_if_does_not_gate_layer_effects()
    {
        // The blue pixel fails This Layer Gray {128,128}, but its green Color Overlay still paints.
        var builder = new PsdBuilder { Width = 1, Height = 1 };
        var layer = Layer("hidden blue", 1, 0, 0, 255, 255);
        layer.BlendingRanges = Ranges(gray: ([128, 128, 255, 255], [0, 0, 255, 255]));
        layer.Blocks.Add(("lfx2", ColorOverlayEffects(0, 255, 0)));
        builder.Layers.Add(layer);

        Assert.Equal(new PsdColor(0, 255, 0, 255), Render(builder).GetPixel(0, 0));
    }

    [Fact]
    public void Adjustment_gates_test_adjusted_this_and_original_underlying()
    {
        // Invert maps the backdrop's 50 to 205. This Layer {128,128} tests 205 and passes;
        // Underlying {128,128} tests the original 50 and blocks the adjustment.
        PsdColor Run(byte[] ranges)
        {
            var builder = new PsdBuilder { Width = 1, Height = 1 };
            builder.Layers.Add(Layer("backdrop", 1, 50, 50, 50, 255));
            builder.Layers.Add(Invert("invert", ranges));
            return Render(builder).GetPixel(0, 0);
        }

        Assert.Equal(new PsdColor(205, 205, 205), Run(Ranges(gray: ([128, 128, 255, 255], [0, 0, 255, 255]))));
        Assert.Equal(new PsdColor(50, 50, 50), Run(Ranges(gray: ([0, 0, 255, 255], [128, 128, 255, 255]))));
    }

    [Fact]
    public void Group_gate_tests_the_merged_children()
    {
        var builder = new PsdBuilder { Width = 2, Height = 1 };
        builder.Layers.Add(Layer("backdrop", 2, 20, 20, 20, 255));
        builder.Layers.Add(new BuilderLayer { Name = "</Layer group>", SectionType = 3 });
        var child = Layer("child", 2, 100, 100, 100, 255);
        child.Channels[0] = child.Channels[1] = child.Channels[2] = [100, 200];
        builder.Layers.Add(child);
        builder.Layers.Add(new BuilderLayer
        {
            Name = "group", SectionType = 1, SectionBlendKey = "norm",
            BlendingRanges = Ranges(gray: ([128, 128, 255, 255], [0, 0, 255, 255])),
        });

        var image = Render(builder);
        Assert.Equal(new PsdColor(20, 20, 20), image.GetPixel(0, 0));
        Assert.Equal(new PsdColor(200, 200, 200), image.GetPixel(1, 0));
    }

    [Theory]
    [InlineData(false, null, 205, 105)]
    [InlineData(false, "underlying", 50, 150)]
    [InlineData(false, "this", 50, 150)]
    [InlineData(true, null, 205, 105)]
    [InlineData(true, "underlying", 20, 105)]
    [InlineData(true, "this", 205, 220)]
    [InlineData(true, "white", 20, 105)]
    public void Pass_through_group_with_blend_if_isolates(bool withPixels, string? ranges, int left, int right)
    {
        // Identity Pass Through lets the Invert reach the outside backdrop. Any Blend If
        // isolates the children first; Underlying then samples the outside backdrop and
        // This samples the isolated result (reference
        // compositor_pass_through_group_blend_if_isolates_adjustment_child, with Invert
        // standing in for its inverting Curves).
        var builder = new PsdBuilder { Width = 2, Height = 1 };
        var background = Layer("background", 2, 0, 0, 0, 255);
        background.Channels[0] = background.Channels[1] = background.Channels[2] = withPixels ? [20, 220] : [50, 150];
        builder.Layers.Add(background);
        builder.Layers.Add(new BuilderLayer { Name = "</Layer group>", SectionType = 3 });
        if (withPixels)
        {
            var pixels = Layer("group pixels", 2, 0, 0, 0, 255);
            pixels.Channels[0] = pixels.Channels[1] = pixels.Channels[2] = [50, 150];
            builder.Layers.Add(pixels);
        }

        builder.Layers.Add(Invert("invert", null));
        builder.Layers.Add(new BuilderLayer
        {
            Name = "group", SectionType = 1, SectionBlendKey = "pass", BlendKey = "pass",
            BlendingRanges = ranges switch
            {
                "underlying" => Ranges(gray: ([0, 0, 255, 255], [100, 100, 255, 255])),
                "this" => Ranges(gray: ([150, 150, 255, 255], [0, 0, 255, 255])),
                "white" => Ranges(gray: ([0, 0, 130, 130], [0, 0, 255, 255])),
                _ => null,
            },
        });

        var image = Render(builder);
        Assert.Equal(new PsdColor((byte)left, (byte)left, (byte)left), image.GetPixel(0, 0));
        Assert.Equal(new PsdColor((byte)right, (byte)right, (byte)right), image.GetPixel(1, 0));
    }

    [Fact]
    public void Clip_base_gate_keeps_the_original_clip_shape()
    {
        // Blend If hides the red base, but its alpha still defines the clip, so the green member shows.
        var builder = new PsdBuilder { Width = 1, Height = 1 };
        builder.Layers.Add(Layer("blue backdrop", 1, 0, 0, 255, 255));
        var clipBase = Layer("hidden red base", 1, 255, 0, 0, 255);
        clipBase.BlendingRanges = Ranges(gray: ([128, 128, 255, 255], [0, 0, 255, 255]));
        builder.Layers.Add(clipBase);
        var member = Layer("green clip", 1, 0, 255, 0, 255);
        member.Clipped = true;
        builder.Layers.Add(member);

        Assert.Equal(new PsdColor(0, 255, 0), Render(builder).GetPixel(0, 0));
    }

    // ---- Special Fill ---------------------------------------------------------

    [Theory]
    [InlineData("norm", 120, 80, 150)]
    [InlineData("idiv", 13, 3, 153)]
    [InlineData("lbrn", 12, 2, 112)]
    [InlineData("div ", 66, 113, 235)]
    [InlineData("lddg", 140, 130, 240)]
    [InlineData("diff", 60, 70, 120)]
    [InlineData("vLit", 56, 44, 178)]
    [InlineData("lLit", 112, 31, 171)]
    [InlineData("hMix", 26, 6, 225)]
    public void Fill_opacity_matches_photoshop_modes(string blendKey, int r, int g, int b)
    {
        // Reference compositor_fill_opacity_matches_photoshop_modes: (200,60,120) at Fill 128
        // over an opaque (40,100,180).
        var builder = new PsdBuilder { Width = 1, Height = 1 };
        builder.Layers.Add(Layer("base", 1, 40, 100, 180, 255));
        var top = Layer("fill", 1, 200, 60, 120, 255);
        top.BlendKey = blendKey;
        top.Blocks.Add(("iOpa", [128, 0, 0, 0]));
        builder.Layers.Add(top);

        AssertNear(new PsdColor((byte)r, (byte)g, (byte)b), Render(builder).GetPixel(0, 0), tolerance: blendKey == "norm" ? 1 : 0);
    }

    public static TheoryData<PsdBlendMode, int, int, int, int> LightModeQuads()
    {
        var data = new TheoryData<PsdBlendMode, int, int, int, int>();
        int[][] linearLight =
        [
            [0, 64, 26, 37], [32, 64, 26, 44], [128, 128, 26, 127], [192, 0, 26, 12], [255, 128, 26, 153], [128, 64, 64, 63],
            [128, 200, 125, 199], [0, 200, 128, 71], [32, 128, 128, 31], [128, 0, 128, 0], [128, 128, 128, 128], [192, 0, 128, 64],
            [255, 64, 128, 191], [192, 0, 191, 96], [255, 64, 191, 254], [64, 128, 230, 12], [255, 0, 230, 229], [0, 128, 3, 124],
            [255, 128, 3, 130], [0, 254, 252, 1], [255, 1, 252, 252],
        ];
        int[][] vividLight =
        [
            [136, 64, 26, 65], [135, 64, 26, 64], [130, 192, 26, 192], [131, 192, 26, 193], [129, 64, 64, 64], [131, 64, 64, 65],
            [0, 129, 128, 2], [0, 192, 128, 129], [32, 128, 128, 51], [96, 64, 128, 37], [128, 64, 128, 64], [160, 64, 128, 73],
            [160, 128, 128, 146], [192, 128, 128, 172], [255, 64, 128, 129], [129, 192, 191, 193], [1, 254, 191, 251], [64, 128, 230, 24],
            [255, 32, 230, 255], [0, 128, 3, 126], [255, 128, 3, 130], [127, 254, 252, 254], [128, 1, 252, 1],
        ];
        int[][] hardMix =
        [
            [0, 32, 26, 7], [128, 128, 26, 128], [255, 224, 26, 249], [0, 100, 64, 48], [127, 64, 64, 43], [255, 32, 64, 43],
            [0, 128, 125, 6], [127, 128, 125, 128], [128, 128, 125, 129], [255, 128, 125, 251], [0, 128, 128, 2], [127, 64, 128, 0],
            [128, 64, 128, 2], [192, 32, 128, 2], [255, 64, 128, 128], [128, 128, 130, 130], [128, 128, 153, 129], [1, 190, 191, 4],
            [254, 2, 230, 10], [0, 128, 3, 126], [255, 128, 3, 130], [100, 154, 252, 64], [100, 156, 252, 191],
        ];
        foreach (var q in linearLight)
        {
            data.Add(PsdBlendMode.LinearLight, q[0], q[1], q[2], q[3]);
        }

        foreach (var q in vividLight)
        {
            data.Add(PsdBlendMode.VividLight, q[0], q[1], q[2], q[3]);
        }

        foreach (var q in hardMix)
        {
            data.Add(PsdBlendMode.HardMix, q[0], q[1], q[2], q[3]);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(LightModeQuads))]
    public void Light_mode_special_fill_matches_photoshop_captures(PsdBlendMode mode, int source, int destination, int fillByte, int expected)
    {
        // Full coverage over an opaque backdrop isolates the kernel: the composite is its output.
        Assert.Equal(expected, SpecialFill.Blend(mode, (byte)source, (byte)destination, fillByte));

        float[] r = [source / 255f];
        float[] dr = [destination / 255f];
        float[] dg = [destination / 255f];
        float[] db = [destination / 255f];
        float[] da = [1f];
        SpecialFill.CompositeRow(mode, r, r, r, [1f], fillByte / 255f, 1f, dr, dg, db, da, clipMode: false);
        Assert.Equal(expected, BlendIf.ToByte(dr[0]));
        Assert.Equal(1f, da[0]);
    }

    [Theory]
    [InlineData(PsdBlendMode.VividLight)]
    [InlineData(PsdBlendMode.LinearLight)]
    [InlineData(PsdBlendMode.HardMix)]
    public void Light_modes_at_fill_zero_are_identity(PsdBlendMode mode) =>
        Assert.Equal(77, SpecialFill.Blend(mode, 200, 77, 0));

    [Fact]
    public void Special_fill_alpha_split_scales_only_alpha_growth_by_fill()
    {
        // Over a transparent backdrop the layer shows its own color at coverage x Fill x opacity.
        float[] dr = [0f];
        float[] dg = [0f];
        float[] db = [0f];
        float[] da = [0f];
        SpecialFill.CompositeRow(PsdBlendMode.LinearDodge, [0.8f], [0.4f], [0.2f], [1f], 0.5f, 1f, dr, dg, db, da, clipMode: false);
        Assert.Equal(0.5f, da[0], 5);
        Assert.Equal(0.8f, dr[0], 5);
        Assert.Equal(0.4f, dg[0], 5);
    }

    // ---- Channel restrictions -------------------------------------------------

    [Fact]
    public void Restricted_channel_keeps_the_premultiplied_backdrop()
    {
        // Over a half-alpha gray-60 backdrop the excluded green reads 60 * 0.5 / out_alpha;
        // over a transparent backdrop it reads 0.
        var builder = new PsdBuilder { Width = 2, Height = 1 };
        var backdrop = Layer("backdrop", 2, 60, 60, 60, 128);
        backdrop.Channels[-1] = [128, 0];
        builder.Layers.Add(backdrop);
        var top = Layer("red no G", 2, 255, 200, 0, 255);
        top.Blocks.Add(("brst", [0, 0, 0, 1]));
        builder.Layers.Add(top);

        var image = Render(builder);
        AssertNear(new PsdColor(255, 30, 0, 255), image.GetPixel(0, 0));
        Assert.Equal(new PsdColor(255, 0, 0, 255), image.GetPixel(1, 0));
    }

    [Fact]
    public void All_channels_restricted_removes_the_layer_and_its_effects()
    {
        var builder = new PsdBuilder { Width = 1, Height = 1 };
        builder.Layers.Add(Layer("backdrop", 1, 100, 120, 140, 255));
        var top = Layer("all restricted", 1, 255, 255, 255, 255);
        top.Blocks.Add(("brst", [0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 2]));
        top.Blocks.Add(("lfx2", ColorOverlayEffects(0, 255, 0)));
        builder.Layers.Add(top);

        Assert.Equal(new PsdColor(100, 120, 140), Render(builder).GetPixel(0, 0));
    }

    [Fact]
    public void Restricted_clip_base_gates_the_run_and_members_keep_the_base_channel()
    {
        // A green-restricted base: its member's green never reaches the result, which keeps
        // the original backdrop's green. A green-restricted member over a red base keeps the base's.
        var builder = new PsdBuilder { Width = 1, Height = 1 };
        builder.Layers.Add(Layer("backdrop", 1, 10, 20, 30, 255));
        var clipBase = Layer("base no G", 1, 200, 200, 200, 255);
        clipBase.Blocks.Add(("brst", [0, 0, 0, 1]));
        builder.Layers.Add(clipBase);
        var member = Layer("member", 1, 0, 255, 0, 255);
        member.Clipped = true;
        builder.Layers.Add(member);
        Assert.Equal(new PsdColor(0, 20, 0), Render(builder).GetPixel(0, 0));

        builder = new PsdBuilder { Width = 1, Height = 1 };
        builder.Layers.Add(Layer("backdrop", 1, 10, 20, 30, 255));
        builder.Layers.Add(Layer("base", 1, 200, 100, 50, 255));
        member = Layer("member no G", 1, 0, 255, 0, 255);
        member.Clipped = true;
        member.Blocks.Add(("brst", [0, 0, 0, 1]));
        builder.Layers.Add(member);
        Assert.Equal(new PsdColor(0, 100, 0), Render(builder).GetPixel(0, 0));
    }

    [Fact]
    public void Non_rgb_restriction_indices_are_ignored()
    {
        var builder = new PsdBuilder { Width = 1, Height = 1 };
        builder.Layers.Add(Layer("backdrop", 1, 100, 120, 140, 255));
        var top = Layer("index 3", 1, 0, 0, 0, 255);
        top.Blocks.Add(("brst", [0, 0, 0, 3]));
        builder.Layers.Add(top);

        Assert.Equal(new PsdColor(0, 0, 0), Render(builder).GetPixel(0, 0));
    }

    // ---- helpers --------------------------------------------------------------

    private static BuilderLayer Layer(string name, int width, byte r, byte g, byte b, byte a)
    {
        var rect = new PsdRect(0, 0, width, 1);
        var layer = new BuilderLayer { Name = name, Rect = rect };
        layer.Channels[-1] = PsdBuilder.Plane8(width, 1, a);
        layer.Channels[0] = PsdBuilder.Plane8(width, 1, r);
        layer.Channels[1] = PsdBuilder.Plane8(width, 1, g);
        layer.Channels[2] = PsdBuilder.Plane8(width, 1, b);
        return layer;
    }

    private static BuilderLayer Invert(string name, byte[]? ranges)
    {
        var layer = new BuilderLayer { Name = name, BlendingRanges = ranges };
        layer.Blocks.Add(("nvrt", []));
        return layer;
    }

    private static Imaging.RgbaImage Render(PsdBuilder builder) =>
        PsdDocument.Load(builder.Build()).Render(new RenderOptions { Source = RenderSource.Layers });

    /// <summary>A 40-byte RGB blending-ranges record: Gray, R, G, B (This, Underlying), then the identity transparency entry.</summary>
    private static byte[] Ranges(
        (byte[] This, byte[] Under)? gray = null,
        (byte[] This, byte[] Under)? red = null,
        (byte[] This, byte[] Under)? green = null,
        (byte[] This, byte[] Under)? blue = null)
    {
        var payload = new List<byte>();
        foreach (var entry in new[] { gray, red, green, blue })
        {
            if (entry is { } ranges)
            {
                payload.AddRange(ranges.This);
                payload.AddRange(ranges.Under);
            }
            else
            {
                payload.AddRange(IdentityEntry);
            }
        }

        payload.AddRange(IdentityEntry);
        return [.. payload];
    }

    /// <summary>An <c>lfx2</c> block holding one enabled Normal Color Overlay at 100%.</summary>
    private static byte[] ColorOverlayEffects(byte r, byte g, byte b)
    {
        var writer = new PsdBuilder.Writer();
        writer.U32(0);
        writer.U32(16);
        writer.UnicodeString(string.Empty);
        writer.DescriptorId("null");
        writer.U32(1);
        writer.DescriptorId("SoFi");
        writer.Ascii("Objc");
        writer.UnicodeString(string.Empty);
        writer.DescriptorId("SoFi");
        writer.U32(2);
        writer.DescriptorId("enab");
        writer.Ascii("bool");
        writer.U8(1);
        writer.DescriptorId("Clr ");
        writer.Ascii("Objc");
        writer.UnicodeString(string.Empty);
        writer.DescriptorId("RGBC");
        writer.U32(3);
        foreach (var (key, value) in new[] { ("Rd  ", r), ("Grn ", g), ("Bl  ", b) })
        {
            writer.DescriptorId(key);
            writer.Ascii("doub");
            writer.F64(value);
        }

        return writer.ToArray();
    }

    private static void AssertNear(PsdColor expected, PsdColor actual, int tolerance = 1)
    {
        Assert.True(
            Math.Abs(expected.R - actual.R) <= tolerance && Math.Abs(expected.G - actual.G) <= tolerance
            && Math.Abs(expected.B - actual.B) <= tolerance && Math.Abs(expected.A - actual.A) <= tolerance,
            $"expected {expected}, got {actual}");
    }
}
