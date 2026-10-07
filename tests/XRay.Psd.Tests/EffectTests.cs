using XRay.Psd.Imaging;
using XRay.Psd.Rendering;
using XRay.Psd.Tests.Support;

namespace XRay.Psd.Tests;

/// <summary>
/// Layer-effect features no Photoshop fixture exercises yet: Stroke Emboss, the
/// "Precise" glow technique, dithered gradients and the stroke knockout of
/// destination-pass overlays. Each follows the reference renderer
/// (.reference/src/render/layer_compositor.hpp, layer_style_mask_ops.cpp).
/// </summary>
public sealed class EffectTests
{
    private const int Canvas = 20;
    private static readonly PsdRect Square = new(6, 6, 14, 14);

    [Fact]
    public void Stroke_emboss_shades_only_the_stroke_band()
    {
        var strokeOnly = Render(Stroke(size: 3));
        var embossed = Render(Stroke(size: 3), StrokeEmboss());

        // Interior and exterior away from the band keep their stroke-only colors.
        Assert.Equal(strokeOnly.GetPixel(10, 10), embossed.GetPixel(10, 10));
        Assert.Equal(strokeOnly.GetPixel(0, 0), embossed.GetPixel(0, 0));

        // The band picks up both a highlight and a shadow side.
        var lighter = false;
        var darker = false;
        for (var y = 0; y < Canvas; y++)
        {
            for (var x = 0; x < Canvas; x++)
            {
                if (Square.Contains(x, y))
                {
                    Assert.Equal(strokeOnly.GetPixel(x, y), embossed.GetPixel(x, y));
                    continue;
                }

                var before = strokeOnly.GetPixel(x, y);
                var after = embossed.GetPixel(x, y);
                lighter |= after.G > before.G + 20;
                darker |= after.R + 20 < before.R;
            }
        }

        Assert.True(lighter, "no highlight on the stroke band");
        Assert.True(darker, "no shadow on the stroke band");
    }

    [Fact]
    public void Stroke_emboss_without_a_visible_stroke_renders_nothing()
    {
        var plain = Render();
        Assert.Equal(Pixels(plain), Pixels(Render(StrokeEmboss())));
        Assert.Equal(Pixels(plain), Pixels(Render(Stroke(size: 3, opacity: 0), StrokeEmboss())));
    }

    [Fact]
    public void Precise_outer_glow_follows_the_smoothstep_distance_ramp()
    {
        const int size = 41;
        var mask = new float[size * size];
        mask[(20 * size) + 20] = 1f;
        EffectMasks.PreciseExterior(mask, size, size, 10f, 0f);
        Assert.Equal(1f, mask[(20 * size) + 20]);
        Assert.Equal(0.5f, mask[(20 * size) + 25], 4);
        Assert.Equal(0f, mask[(20 * size) + 31]);

        // Spread keeps the inner share solid; a faint component carries its own strength.
        var spread = new float[size * size];
        spread[(20 * size) + 20] = 0.5f;
        EffectMasks.PreciseExterior(spread, size, size, 10f, 50f);
        Assert.Equal(0.5f, spread[(20 * size) + 25], 4);
        Assert.Equal(0.5f * (1f - (0.36f * 1.8f)), spread[(20 * size) + 28], 4);
        Assert.Equal(0f, spread[(20 * size) + 31]);
    }

    [Fact]
    public void Precise_inner_glow_center_is_the_complement_of_edge_without_choke()
    {
        const int size = 30;
        float[] Matte()
        {
            var matte = new float[size * size];
            for (var y = 5; y < 25; y++)
            {
                for (var x = 5; x < 25; x++)
                {
                    matte[(y * size) + x] = 1f;
                }
            }

            return matte;
        }

        var edge = Matte();
        var center = Matte();
        EffectMasks.PreciseInterior(edge, size, size, 8f, 0f, center: false);
        EffectMasks.PreciseInterior(center, size, size, 8f, 0f, center: true);
        for (var i = 0; i < edge.Length; i++)
        {
            Assert.Equal(1f, edge[i] + center[i], 4);
        }

        // Choke 100 leaves a hard band of the full size along the contour.
        var choked = Matte();
        EffectMasks.PreciseInterior(choked, size, size, 4f, 100f, center: false);
        Assert.Equal(1f, choked[(15 * size) + 8], 4);
        Assert.Equal(0f, choked[(15 * size) + 15], 4);
    }

    [Fact]
    public void Glow_technique_selects_the_precise_falloff()
    {
        var softer = Render(Glow("OrGl", precise: false), Glow("IrGl", precise: false));
        var precise = Render(Glow("OrGl", precise: true), Glow("IrGl", precise: true));
        Assert.NotEqual(Pixels(softer), Pixels(precise));
        Assert.Equal(softer.GetPixel(0, 0), precise.GetPixel(0, 0));
    }

    [Fact]
    public void Dithered_gradient_overlay_moves_colors_by_at_most_two_levels()
    {
        var smooth = Render(GradientOverlay(dither: false));
        var dithered = Render(GradientOverlay(dither: true));
        var changed = 0;
        for (var y = Square.Top; y < Square.Bottom; y++)
        {
            for (var x = Square.Left; x < Square.Right; x++)
            {
                var a = smooth.GetPixel(x, y);
                var b = dithered.GetPixel(x, y);
                Assert.InRange(b.R - a.R, -2, 3);
                changed += a == b ? 0 : 1;
            }
        }

        Assert.True(changed > 0);
        Assert.Equal(Pixels(dithered), Pixels(Render(GradientOverlay(dither: true))));
    }

    [Fact]
    public void Dither_offsets_come_from_the_document_coordinate()
    {
        for (var y = 0; y < 16; y++)
        {
            for (var x = 0; x < 16; x++)
            {
                var (r, g, b) = LayerCompositor.DitherGradientColor(100 / 255f, 0f, 1f, x, y);
                var delta = (int)MathF.Round(r * 255f) - 100;
                Assert.InRange(delta, -1, 2);
                Assert.Equal(Math.Max(0, delta) / 255f, g, 5);
                Assert.Equal(Math.Min(255, 255 + delta) / 255f, b, 5);
            }
        }
    }

    [Fact]
    public void Stroke_knocks_destination_pass_overlays_out_of_its_band()
    {
        // At Fill 50% the overlay cannot fold into the layer color and paints as its own
        // pass; a 50% Inside stroke without Overprint must show only the stroke over the
        // white backdrop in its band, with no green from the overlay.
        var image = Render(fill: 128, Stroke(size: 2, opacity: 50, position: "InsF"), ColorOverlay(0, 255, 0));
        var band = image.GetPixel(Square.Left, 10);
        Assert.InRange(band.R, 254, 255);
        Assert.InRange(band.G, 126, 129);
        Assert.InRange(band.B, 126, 129);

        // Away from the band the overlay still covers the content.
        var center = image.GetPixel(10, 10);
        Assert.True(center.G > 200 && center.R < 10, center.ToString());
    }

    // ---- documents ------------------------------------------------------------

    private static RgbaImage Render(params Desc[] effects) => Render(255, effects);

    private static RgbaImage Render(byte fill, params Desc[] effects)
    {
        var builder = new PsdBuilder { Width = Canvas, Height = Canvas };
        builder.Layers.Add(Solid("backdrop", new PsdRect(0, 0, Canvas, Canvas), 255, 255, 255));
        var layer = Solid("styled", Square, 40, 40, 200);
        if (fill != 255)
        {
            layer.Blocks.Add(("iOpa", [fill, 0, 0, 0]));
        }

        if (effects.Length > 0)
        {
            var root = new Desc("null", [("masterFXSwitch", true), .. effects.Select(e => (e.ClassId, (object)e))]);
            var writer = new PsdBuilder.Writer();
            writer.U32(0);
            writer.U32(16);
            WriteDescriptor(writer, root);
            layer.Blocks.Add(("lfx2", writer.ToArray()));
        }

        builder.Layers.Add(layer);
        return PsdDocument.Load(builder.Build()).Render(new RenderOptions { Source = RenderSource.Layers });
    }

    private static BuilderLayer Solid(string name, PsdRect rect, byte r, byte g, byte b)
    {
        var layer = new BuilderLayer { Name = name, Rect = rect };
        layer.Channels[-1] = PsdBuilder.Plane8(rect.Width, rect.Height, 255);
        layer.Channels[0] = PsdBuilder.Plane8(rect.Width, rect.Height, r);
        layer.Channels[1] = PsdBuilder.Plane8(rect.Width, rect.Height, g);
        layer.Channels[2] = PsdBuilder.Plane8(rect.Width, rect.Height, b);
        return layer;
    }

    private static PsdColor[] Pixels(RgbaImage image)
    {
        var pixels = new PsdColor[image.Width * image.Height];
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                pixels[(y * image.Width) + x] = image.GetPixel(x, y);
            }
        }

        return pixels;
    }

    // ---- effect descriptors ---------------------------------------------------

    private static Desc Color(byte r, byte g, byte b) => new("RGBC", [("Rd  ", (double)r), ("Grn ", (double)g), ("Bl  ", (double)b)]);

    private static Desc Stroke(float size, double opacity = 100, string position = "OutF") => new("FrFX",
    [
        ("enab", true), ("Styl", new Enum("FStl", position)), ("PntT", new Enum("FrFl", "SClr")),
        ("Md  ", new Enum("BlnM", "Nrml")), ("Opct", new Unit("#Prc", opacity)), ("Sz  ", new Unit("#Pxl", size)),
        ("Clr ", Color(255, 0, 0)), ("overprint", false),
    ]);

    private static Desc StrokeEmboss() => new("ebbl",
    [
        ("enab", true), ("hglM", new Enum("BlnM", "Nrml")), ("hglC", Color(255, 255, 255)), ("hglO", new Unit("#Prc", 100.0)),
        ("sdwM", new Enum("BlnM", "Nrml")), ("sdwC", Color(0, 0, 0)), ("sdwO", new Unit("#Prc", 100.0)),
        ("bvlT", new Enum("bvlT", "SfBL")), ("bvlS", new Enum("BESl", "strokeEmboss")), ("uglg", false),
        ("lagl", new Unit("#Ang", 120.0)), ("Lald", new Unit("#Ang", 30.0)), ("srgR", new Unit("#Prc", 200.0)),
        ("blur", new Unit("#Pxl", 3.0)), ("bvlD", new Enum("BESs", "In  ")),
    ]);

    private static Desc Glow(string kind, bool precise) => new(kind,
    [
        ("enab", true), ("Md  ", new Enum("BlnM", "Nrml")), ("Clr ", Color(255, 255, 0)), ("Opct", new Unit("#Prc", 100.0)),
        ("GlwT", new Enum("BETE", precise ? "PrBL" : "SfBL")), ("Ckmt", new Unit("#Pxl", 0.0)), ("blur", new Unit("#Pxl", 4.0)),
        ("Inpr", new Unit("#Prc", 50.0)),
    ]);

    private static Desc ColorOverlay(byte r, byte g, byte b) => new("SoFi",
    [
        ("enab", true), ("Md  ", new Enum("BlnM", "Nrml")), ("Clr ", Color(r, g, b)), ("Opct", new Unit("#Prc", 100.0)),
    ]);

    private static Desc GradientOverlay(bool dither) => new("GrFl",
    [
        ("enab", true), ("Md  ", new Enum("BlnM", "Nrml")), ("Opct", new Unit("#Prc", 100.0)),
        ("Grad", new Desc("Grdn",
        [
            ("Intr", 4096.0),
            ("Clrs", new object[]
            {
                new Desc("Clrt", [("Clr ", Color(0, 0, 0)), ("Type", new Enum("Clry", "UsrS")), ("Lctn", 0), ("Mdpn", 50)]),
                new Desc("Clrt", [("Clr ", Color(255, 255, 255)), ("Type", new Enum("Clry", "UsrS")), ("Lctn", 4096), ("Mdpn", 50)]),
            }),
            ("Trns", new object[]
            {
                new Desc("TrnS", [("Opct", new Unit("#Prc", 100.0)), ("Lctn", 0), ("Mdpn", 50)]),
                new Desc("TrnS", [("Opct", new Unit("#Prc", 100.0)), ("Lctn", 4096), ("Mdpn", 50)]),
            }),
        ])),
        ("Angl", new Unit("#Ang", 0.0)), ("Type", new Enum("GrdT", "Lnr ")), ("Dthr", dither), ("Scl ", new Unit("#Prc", 100.0)),
    ]);

    private sealed record Desc(string ClassId, (string Key, object Value)[] Items);

    private sealed record Enum(string Type, string Value);

    private sealed record Unit(string Code, double Value);

    private static void WriteDescriptor(PsdBuilder.Writer writer, Desc descriptor)
    {
        writer.UnicodeString(string.Empty);
        writer.DescriptorId(descriptor.ClassId);
        writer.U32((uint)descriptor.Items.Length);
        foreach (var (key, value) in descriptor.Items)
        {
            writer.DescriptorId(key);
            WriteValue(writer, value);
        }
    }

    private static void WriteValue(PsdBuilder.Writer writer, object value)
    {
        switch (value)
        {
            case bool flag:
                writer.Ascii("bool");
                writer.U8(flag ? (byte)1 : (byte)0);
                break;
            case double number:
                writer.Ascii("doub");
                writer.F64(number);
                break;
            case int integer:
                writer.Ascii("long");
                writer.I32(integer);
                break;
            case Unit unit:
                writer.Ascii("UntF");
                writer.Ascii(unit.Code);
                writer.F64(unit.Value);
                break;
            case Enum enumerated:
                writer.Ascii("enum");
                writer.DescriptorId(enumerated.Type);
                writer.DescriptorId(enumerated.Value);
                break;
            case Desc nested:
                writer.Ascii("Objc");
                WriteDescriptor(writer, nested);
                break;
            case object[] list:
                writer.Ascii("VlLs");
                writer.U32((uint)list.Length);
                foreach (var item in list)
                {
                    WriteValue(writer, item);
                }

                break;
            default:
                throw new ArgumentException($"Unsupported descriptor value {value}");
        }
    }
}
