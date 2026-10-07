using XRay.Psd.Rendering;
using XRay.Psd.Tests.Support;
using static XRay.Psd.Tests.Support.DescriptorWriter;

namespace XRay.Psd.Tests;

/// <summary>
/// Layer effects on shape layers (vector stroke split planes, the coverage silhouette
/// of gradient fills, legacy <c>vscg</c> paint) and burn/dodge effects over a partially
/// transparent backdrop. Rules: .reference/docs/layer-effects-render.md and
/// <c>composite_effect_color</c> in .reference/src/render/layer_compositor.hpp.
/// </summary>
public sealed class ShapeEffectTests
{
    private const int Size = 20;

    [Fact]
    public void Interior_overlay_covers_the_fill_and_the_vector_stroke_stays_above_it()
    {
        var shape = Shape("Stroked", Rectangle(5, 5, 15, 15));
        shape.Blocks.Add(("SoCo", SolidContent(255, 0, 0)));
        shape.Blocks.Add(("vstk", StrokeStyle(width: 2, fillEnabled: true, SolidContent(0, 0, 255, "solidColorLayer"))));
        shape.Blocks.Add(("lfx2", Effects(("SoFi", Overlay("Nrml", 100, 0, 255, 0)))));
        var image = Render(shape);

        Assert.Equal(new PsdColor(0, 255, 0), image.GetPixel(10, 10));
        // The centered 2 px stroke covers columns 4 and 5 on the left edge.
        Assert.Equal(new PsdColor(0, 0, 255), image.GetPixel(4, 10));
        Assert.Equal(new PsdColor(0, 0, 255), image.GetPixel(5, 10));
        Assert.Equal(new PsdColor(0, 255, 0), image.GetPixel(6, 10));
    }

    [Fact]
    public void Overlay_on_a_stroke_only_shape_covers_the_stroke()
    {
        var shape = Shape("Stroke only", Rectangle(5, 5, 15, 15));
        shape.Blocks.Add(("SoCo", SolidContent(255, 0, 0)));
        shape.Blocks.Add(("vstk", StrokeStyle(width: 2, fillEnabled: false, SolidContent(0, 0, 255, "solidColorLayer"))));
        shape.Blocks.Add(("lfx2", Effects(("SoFi", Overlay("Nrml", 100, 0, 255, 0)))));
        var image = Render(shape);

        Assert.Equal(new PsdColor(0, 255, 0), image.GetPixel(5, 10));
        Assert.Equal(0, image.GetPixel(10, 10).A);
    }

    [Fact]
    public void Gradient_fill_effects_follow_the_shape_coverage()
    {
        // A gradient from opaque black to fully transparent across the shape: a 100%
        // Color Overlay turns the whole shape opaque (the shape is the effect silhouette).
        var plain = Shape("Gradient", Rectangle(2, 2, 18, 18));
        plain.Blocks.Add(("GdFl", FadingGradient()));
        plain.Blocks.Add(("lfx2", Effects(("SoFi", Overlay("Nrml", 100, 0, 128, 255)))));
        var image = Render(plain);
        Assert.Equal(new PsdColor(0, 128, 255), image.GetPixel(16, 10));
        Assert.Equal(new PsdColor(0, 128, 255), image.GetPixel(3, 10));
        Assert.Equal(0, image.GetPixel(1, 10).A);

        // The same through the stroked-shape renderer (the matte also takes the stroke band).
        var stroked = Shape("Gradient stroked", Rectangle(2, 2, 18, 18));
        stroked.Blocks.Add(("GdFl", FadingGradient()));
        stroked.Blocks.Add(("vstk", StrokeStyle(width: 2, fillEnabled: true, SolidContent(255, 0, 0, "solidColorLayer"), opacity: 0.5)));
        stroked.Blocks.Add(("lfx2", Effects(("SoFi", Overlay("Nrml", 100, 0, 128, 255)))));
        image = Render(stroked);
        Assert.Equal(new PsdColor(0, 128, 255), image.GetPixel(15, 10));

        // The stroke re-composites above the overlay at its own 50% opacity.
        var edge = image.GetPixel(2, 10);
        Assert.InRange(edge.R, 126, 129);
        Assert.InRange(edge.G, 63, 65);
        Assert.InRange(edge.B, 126, 129);
    }

    [Fact]
    public void Legacy_vscg_paints_the_fill_of_a_shape_without_a_fill_block()
    {
        var shape = Shape("Legacy fill", Rectangle(5, 5, 15, 15));
        shape.Blocks.Add(("vstk", StrokeStyle(width: 2, fillEnabled: true, null, strokeEnabled: false)));
        shape.Blocks.Add(("vscg", new DescriptorWriter("null").Add("Clr ", Rgb(255, 0, 0)).KeyedVersioned("SoCo")));
        var document = PsdDocument.Load(Build(shape));
        var layer = document.Layers.Single(l => l.Name == "Legacy fill");
        Assert.Equal(PsdLayerKind.Fill, layer.Kind);
        Assert.Equal("SoCo", layer.ContentKey);
        Assert.Equal(new PsdColor(255, 0, 0), layer.FillColor);

        var image = document.Render(new RenderOptions { Source = RenderSource.Layers });
        Assert.Equal(new PsdColor(255, 0, 0), image.GetPixel(10, 10));
        Assert.Equal(0, image.GetPixel(2, 10).A);
    }

    [Fact]
    public void Legacy_vscg_paints_the_stroke_of_a_stroke_only_shape()
    {
        var shape = Shape("Legacy stroke", Rectangle(5, 5, 15, 15));
        shape.Blocks.Add(("vstk", StrokeStyle(width: 2, fillEnabled: false, null)));
        shape.Blocks.Add(("vscg", new DescriptorWriter("null").Add("Clr ", Rgb(0, 0, 255)).KeyedVersioned("SoCo")));
        var document = PsdDocument.Load(Build(shape));
        Assert.Equal(PsdLayerKind.Fill, document.Layers.Single(l => l.Name == "Legacy stroke").Kind);

        var image = document.Render(new RenderOptions { Source = RenderSource.Layers });
        Assert.Equal(new PsdColor(0, 0, 255), image.GetPixel(5, 10));
        Assert.Equal(0, image.GetPixel(10, 10).A);
    }

    [Fact]
    public void Burn_effect_over_partial_transparency_takes_the_special_fill_split()
    {
        // A 50% Linear Burn overlay (painted as a pass because the layer's Fill is 0) over
        // a backdrop at alpha 128 (left) and 255 (right).
        var builder = new PsdBuilder { Width = 2, Height = 1 };
        var backdrop = PixelLayer("Backdrop", 200, 100, 50, x => x == 0 ? (byte)128 : (byte)255);
        builder.Layers.Add(backdrop);
        var top = PixelLayer("Burn", 0, 0, 0, _ => 255);
        top.Blocks.Add(("iOpa", [0, 0, 0, 0]));
        top.Blocks.Add(("lfx2", Effects(("SoFi", Overlay("linearBurn", 50, 100, 100, 100)))));
        builder.Layers.Add(top);
        var image = PsdDocument.Load(builder.Build()).Render(new RenderOptions { Source = RenderSource.Layers });

        // Special Fill with the effect alpha as Fill: the source fades toward white by
        // 128/255 (178), the overlap takes Linear Burn (123, 23, 0), and the uncovered half
        // of the pixel keeps the original color at the effect's alpha.
        AssertNear(new PsdColor(115, 49, 33, 192), image.GetPixel(0, 0));

        // Over the opaque pixel the alpha folds into the color: 178 + d - 255.
        AssertNear(new PsdColor(123, 23, 0, 255), image.GetPixel(1, 0));
    }

    // ---- helpers --------------------------------------------------------------

    private static Imaging.RgbaImage Render(BuilderLayer layer) =>
        PsdDocument.Load(Build(layer)).Render(new RenderOptions { Source = RenderSource.Layers });

    private static byte[] Build(BuilderLayer layer)
    {
        var builder = new PsdBuilder { Width = Size, Height = Size };
        builder.Layers.Add(layer);
        return builder.Build();
    }

    private static BuilderLayer Shape(string name, byte[] path)
    {
        var layer = new BuilderLayer { Name = name };
        layer.Blocks.Add(("vmsk", path));
        return layer;
    }

    private static BuilderLayer PixelLayer(string name, byte r, byte g, byte b, Func<int, byte> alpha)
    {
        var layer = new BuilderLayer { Name = name, Rect = new PsdRect(0, 0, 2, 1) };
        layer.Channels[-1] = PsdBuilder.Plane8(2, 1, (x, _) => alpha(x));
        layer.Channels[0] = PsdBuilder.Plane8(2, 1, r);
        layer.Channels[1] = PsdBuilder.Plane8(2, 1, g);
        layer.Channels[2] = PsdBuilder.Plane8(2, 1, b);
        return layer;
    }

    /// <summary>A <c>vmsk</c> payload holding one closed rectangle (document pixels).</summary>
    private static byte[] Rectangle(double left, double top, double right, double bottom)
    {
        var writer = new PsdBuilder.Writer();
        writer.U32(3);
        writer.U32(0);
        Record(writer, 6, []);
        Record(writer, 8, [0, 0]);
        Record(writer, 0, [0, 4, 0, 1]);
        foreach (var (x, y) in new[] { (left, top), (right, top), (right, bottom), (left, bottom) })
        {
            var fy = (int)(y / Size * 16777216);
            var fx = (int)(x / Size * 16777216);
            var knot = new PsdBuilder.Writer();
            for (var i = 0; i < 3; i++)
            {
                knot.I32(fy);
                knot.I32(fx);
            }

            Record(writer, 2, knot.ToArray());
        }

        return writer.ToArray();
    }

    private static void Record(PsdBuilder.Writer writer, ushort selector, byte[] body)
    {
        writer.U16(selector);
        writer.Bytes(body);
        writer.Zeros(24 - body.Length);
    }

    /// <summary>A solid paint descriptor (a <c>SoCo</c> block, or a <c>strokeStyleContent</c> object when a class is given).</summary>
    private static byte[] SolidContent(byte r, byte g, byte b) => new DescriptorWriter("null").Add("Clr ", Rgb(r, g, b)).Versioned();

    private static DescriptorWriter SolidContent(byte r, byte g, byte b, string classId) => new DescriptorWriter(classId).Add("Clr ", Rgb(r, g, b));

    private static byte[] StrokeStyle(double width, bool fillEnabled, DescriptorWriter? content, double opacity = 1, bool strokeEnabled = true)
    {
        var style = new DescriptorWriter("strokeStyle")
            .Add("strokeStyleVersion", 2)
            .Add("strokeEnabled", strokeEnabled)
            .Add("fillEnabled", fillEnabled)
            .Add("strokeStyleLineWidth", Pixels(width))
            .Add("strokeStyleLineAlignment", new EnumValue("strokeStyleLineAlignment", "strokeStyleAlignCenter"))
            .Add("strokeStyleBlendMode", BlendMode("Nrml"))
            .Add("strokeStyleOpacity", Percent(opacity * 100));
        if (content is not null)
        {
            style.Add("strokeStyleContent", content);
        }

        return style.Versioned(2);
    }

    private static DescriptorWriter Overlay(string mode, double opacity, byte r, byte g, byte b) =>
        new DescriptorWriter("SoFi")
            .Add("enab", true)
            .Add("Md  ", BlendMode(mode))
            .Add("Opct", Percent(opacity))
            .Add("Clr ", Rgb(r, g, b));

    private static byte[] Effects(params (string Key, DescriptorWriter Effect)[] effects)
    {
        var root = new DescriptorWriter("null").Add("Scl ", Percent(100)).Add("masterFXSwitch", true);
        foreach (var (key, effect) in effects)
        {
            root.Add(key, effect);
        }

        return root.Versioned(0);
    }

    /// <summary>A left-to-right linear gradient fill from opaque black to fully transparent.</summary>
    private static byte[] FadingGradient()
    {
        DescriptorWriter ColorStop(int location) => new DescriptorWriter("Clrt")
            .Add("Clr ", Rgb(0, 0, 0)).Add("Type", new EnumValue("Clry", "UsrS")).Add("Lctn", location).Add("Mdpn", 50);
        DescriptorWriter AlphaStop(int location, double opacity) => new DescriptorWriter("TrnS")
            .Add("Opct", Percent(opacity)).Add("Lctn", location).Add("Mdpn", 50);
        var gradient = new DescriptorWriter("Grdn")
            .Add("Nm  ", "Fade")
            .Add("GrdF", new EnumValue("GrdF", "CstS"))
            .Add("Intr", 4096.0)
            .Add("Clrs", new object[] { ColorStop(0), ColorStop(4096) })
            .Add("Trns", new object[] { AlphaStop(0, 100), AlphaStop(4096, 0) });
        return new DescriptorWriter("null")
            .Add("Grad", gradient)
            .Add("Type", new EnumValue("GrdT", "Lnr "))
            .Add("Angl", new Unit("#Ang", 0))
            .Add("Algn", true)
            .Versioned();
    }

    private static void AssertNear(PsdColor expected, PsdColor actual, int tolerance = 1)
    {
        Assert.True(
            Math.Abs(expected.R - actual.R) <= tolerance && Math.Abs(expected.G - actual.G) <= tolerance
            && Math.Abs(expected.B - actual.B) <= tolerance && Math.Abs(expected.A - actual.A) <= tolerance,
            $"expected {expected}, got {actual}");
    }
}
