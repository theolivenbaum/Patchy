using XRay.Psd.Imaging;
using XRay.Psd.Layers;
using XRay.Psd.Rendering;
using XRay.Psd.Tests.Support;
using static XRay.Psd.Tests.Support.DescriptorWriter;

namespace XRay.Psd.Tests;

/// <summary>
/// Photoshop 5 layer effects (<c>lrFX</c>). No Photoshop file in reach carries them
/// without a modern <c>lfx2</c> beside them, so the records are synthetic, built in the
/// layout the translation was checked against (the lfx2 mirrors of 208 psd-tools
/// layers), plus a render-parity check on psd-tools' <c>effects/blend-modes.psd</c>.
/// </summary>
public sealed class LegacyEffectTests
{
    private const int Canvas = 24;
    private static readonly PsdRect Square = new(6, 6, 14, 14);

    [Fact]
    public void Photoshop_5_drop_shadow_renders_like_its_lfx2_equivalent()
    {
        var legacy = Load(LrFx(Common(), DropShadow(size: 4, distance: 3, angle: 120, opacity: 191, mode: "mul ")));
        var modern = Load(lfx2: new DescriptorWriter("null")
            .Add("masterFXSwitch", true)
            .Add("DrSh", new DescriptorWriter("DrSh")
                .Add("enab", true).Add("Md  ", BlendMode("Mltp")).Add("Clr ", Rgb(0, 0, 0)).Add("Opct", Percent(191 * 100.0 / 255))
                .Add("uglg", false).Add("lagl", new Unit("#Ang", 120)).Add("Dstn", Pixels(3)).Add("Ckmt", Pixels(0)).Add("blur", Pixels(4))));

        var style = legacy.Layers[^1].Style;
        Assert.NotNull(style);
        Assert.True(style.IsLegacy);
        var shadow = Assert.Single(style.ActiveEffects);
        Assert.Equal(PsdLayerEffectKind.DropShadow, shadow.Kind);
        Assert.Equal(PsdBlendMode.Multiply, shadow.BlendMode);
        Assert.Equal(4f, shadow.Size);
        Assert.Equal(3f, shadow.Distance);
        Assert.Equal(120f, shadow.Angle);
        Assert.Equal(PsdColor.Black, shadow.Color);

        var a = Render(legacy);
        var b = Render(modern);
        Assert.True(ImageTools.Compare(a, b).MaxDelta <= 1, ImageTools.Compare(a, b).ToString());
        Assert.NotEqual(PsdColor.White, a.GetPixel(Square.Right + 1, Square.Bottom + 1));
    }

    [Fact]
    public void Legacy_effects_are_ignored_beside_lfx2()
    {
        // Photoshop writes lrFX as a lossy mirror; a disabled lfx2 shadow must stay off.
        var lfx2 = new DescriptorWriter("null").Add("masterFXSwitch", true)
            .Add("DrSh", new DescriptorWriter("DrSh").Add("enab", false).Add("blur", Pixels(4)));
        var document = Load(LrFx(Common(), DropShadow(size: 4, distance: 3, angle: 120, opacity: 255, mode: "norm")), lfx2);

        Assert.False(document.Layers[^1].Style!.IsLegacy);
        Assert.Empty(document.Layers[^1].Style!.ActiveEffects);
        Assert.Equal(PsdColor.White, Render(document).GetPixel(Square.Right + 1, Square.Bottom + 1));
    }

    [Fact]
    public void Glow_bevel_and_fill_records_translate()
    {
        var document = Load(LrFx(
            Common(),
            Glow("oglw", size: 6, color: (0xFFFF, 0xFFFF, 0, 0), opacity: 128, edge: null),
            Glow("iglw", size: 3, color: (0, 0, 0, 0xFFFF), opacity: 255, edge: false),
            Bevel(style: 3, size: 5, depth: 5, down: true),
            Fill(color: (2, 0xFFFF, 0xFFFF, 0xFFFF, 0), opacity: 255)));
        var effects = document.Layers[^1].Style!.ActiveEffects.ToList();

        var outer = effects.Single(e => e.Kind == PsdLayerEffectKind.OuterGlow);
        Assert.Equal(new PsdColor(255, 255, 0), outer.Color);
        Assert.Equal(6f, outer.Size);
        Assert.Equal(128 / 255f, outer.Opacity, 3);
        Assert.Equal("SrcC", effects.Single(e => e.Kind == PsdLayerEffectKind.InnerGlow).Descriptor!.GetEnum("glwS"));

        // Photoshop 5 stores half the modern size for Emboss and Pillow Emboss, and the
        // depth in pixels (5 of a 5 px bevel is 100%).
        var bevel = effects.Single(e => e.Kind == PsdLayerEffectKind.BevelEmboss);
        Assert.Equal(PsdBevelStyle.Emboss, bevel.BevelStyle);
        Assert.Equal(10f, bevel.Size);
        Assert.Equal(100f, bevel.Depth!.Value, 3);
        Assert.Equal("Out ", bevel.Descriptor!.GetEnum("bvlD"));

        // CMYK is stored inverted: 0xFFFF on every ink but black is 100% K.
        var fill = effects.Single(e => e.Kind == PsdLayerEffectKind.ColorOverlay);
        Assert.Equal(PsdColor.Black, fill.Color);
        Render(document);
    }

    [Fact]
    public void Legacy_intensity_is_a_transfer_contour()
    {
        // Intensity scales the shadow's coverage by 1 + intensity / 100 (clamped), so a
        // 100% shadow is denser everywhere the plain one is partial.
        var plain = Render(Load(LrFx(Common(), DropShadow(size: 6, distance: 0, angle: 120, opacity: 255, mode: "norm"))));
        var dense = Render(Load(LrFx(Common(), DropShadow(size: 6, distance: 0, angle: 120, opacity: 255, mode: "norm", intensity: 100))));
        var edge = (X: Square.Right + 2, Y: 10);
        Assert.True(dense.GetPixel(edge.X, edge.Y).R < plain.GetPixel(edge.X, edge.Y).R - 20, $"{dense.GetPixel(edge.X, edge.Y)} vs {plain.GetPixel(edge.X, edge.Y)}");
    }

    [Fact]
    public void Damaged_legacy_records_degrade()
    {
        var shadow = DropShadow(size: 4, distance: 3, angle: 120, opacity: 255, mode: "norm");
        var block = LrFx(Common(), shadow);
        for (var cut = 4; cut < block.Length; cut += 7)
        {
            var document = Load(block[..cut]);
            Render(document);
            _ = document.Layers[^1].Style;
        }

        // A record whose size runs past the block ends the list.
        var lying = (byte[])block.Clone();
        lying[4 + 12 + 7 + 8 + 3] = 0xFF;
        Render(Load(lying));
    }

    [Fact]
    public void Corpus_lrfx_mirrors_render_like_their_lfx2()
    {
        // psd-tools' effects/blend-modes.psd: 27 drop shadows and 27 color overlays, one per
        // blend mode, each mirrored in lrFX. Rendering from the translated lrFX instead of
        // lfx2 gives the same image within one level.
        var document = PsdDocument.Load(Fixtures.PsdTools(Path.Combine("effects", "blend-modes.psd")));
        var modern = document.Render(new RenderOptions { Source = RenderSource.Layers });
        var swapped = 0;
        foreach (var layer in document.Layers)
        {
            if (layer.GetTaggedBlock("lrFX") is { } block && LegacyEffects.ToDescriptor(block.Data, document.Colors) is { } legacy)
            {
                layer.Effects = legacy;
                layer.EffectsVisible = legacy.GetBoolean("masterFXSwitch", true);
                swapped++;
            }
        }

        var translated = document.Render(new RenderOptions { Source = RenderSource.Layers });
        Assert.True(swapped >= 54, $"{swapped} layers");
        var diff = ImageTools.Compare(translated, modern);
        Assert.True(diff.MaxDelta <= 1, diff.ToString());
    }

    // ---- documents ------------------------------------------------------------

    private static PsdDocument Load(byte[]? lrfx = null, DescriptorWriter? lfx2 = null)
    {
        var builder = new PsdBuilder { Width = Canvas, Height = Canvas };
        builder.Layers.Add(Solid("backdrop", new PsdRect(0, 0, Canvas, Canvas), 255, 255, 255));
        var layer = Solid("styled", Square, 40, 40, 200);
        if (lfx2 is not null)
        {
            layer.Blocks.Add(("lfx2", lfx2.Versioned(0)));
        }

        if (lrfx is not null)
        {
            layer.Blocks.Add(("lrFX", lrfx));
        }

        builder.Layers.Add(layer);
        return PsdDocument.Load(builder.Build());
    }

    private static RgbaImage Render(PsdDocument document) => document.Render(new RenderOptions { Source = RenderSource.Layers });

    private static BuilderLayer Solid(string name, PsdRect rect, byte r, byte g, byte b)
    {
        var layer = new BuilderLayer { Name = name, Rect = rect };
        layer.Channels[-1] = PsdBuilder.Plane8(rect.Width, rect.Height, 255);
        layer.Channels[0] = PsdBuilder.Plane8(rect.Width, rect.Height, r);
        layer.Channels[1] = PsdBuilder.Plane8(rect.Width, rect.Height, g);
        layer.Channels[2] = PsdBuilder.Plane8(rect.Width, rect.Height, b);
        return layer;
    }

    // ---- lrFX records -----------------------------------------------------------

    private static byte[] LrFx(params (string Key, byte[] Payload)[] records)
    {
        var writer = new PsdBuilder.Writer();
        writer.U16(0);
        writer.U16((ushort)records.Length);
        foreach (var (key, payload) in records)
        {
            writer.Ascii("8BIM");
            writer.Ascii(key);
            writer.U32((uint)payload.Length);
            writer.Bytes(payload);
        }

        return writer.ToArray();
    }

    private static (string, byte[]) Common()
    {
        var writer = new PsdBuilder.Writer();
        writer.U32(0);
        writer.U8(1);
        writer.U16(0);
        return ("cmnS", writer.ToArray());
    }

    /// <summary>A version 0 shadow (41 bytes, what Photoshop 5 writes).</summary>
    private static (string, byte[]) DropShadow(double size, double distance, double angle, byte opacity, string mode, double intensity = 0)
    {
        var writer = new PsdBuilder.Writer();
        writer.U32(0);
        Fixed(writer, size);
        Fixed(writer, intensity);
        Fixed(writer, angle);
        Fixed(writer, distance);
        Color(writer, (0, 0, 0, 0, 0));
        writer.Ascii("8BIM");
        writer.Ascii(mode);
        writer.U8(1);
        writer.U8(0);
        writer.U8(opacity);
        return ("dsdw", writer.ToArray());
    }

    private static (string, byte[]) Glow(string key, double size, (ushort R, ushort G, ushort B, ushort X) color, byte opacity, bool? edge)
    {
        var writer = new PsdBuilder.Writer();
        writer.U32(2);
        Fixed(writer, size);
        Fixed(writer, 0);
        Color(writer, (0, color.R, color.G, color.B, color.X));
        writer.Ascii("8BIM");
        writer.Ascii("norm");
        writer.U8(1);
        writer.U8(opacity);
        if (edge is { } value)
        {
            writer.U8((byte)(value ? 1 : 0));
        }

        Color(writer, (0, color.R, color.G, color.B, color.X));
        return (key, writer.ToArray());
    }

    private static (string, byte[]) Bevel(byte style, double size, double depth, bool down)
    {
        var writer = new PsdBuilder.Writer();
        writer.U32(0);
        Fixed(writer, 120);
        Fixed(writer, depth);
        Fixed(writer, size);
        writer.Ascii("8BIMscrn8BIMmul ");
        Color(writer, (0, 0xFFFF, 0xFFFF, 0xFFFF, 0));
        Color(writer, (0, 0, 0, 0, 0));
        writer.U8(style);
        writer.U8(191);
        writer.U8(191);
        writer.U8(1);
        writer.U8(0);
        writer.U8((byte)(down ? 1 : 0));
        return ("bevl", writer.ToArray());
    }

    private static (string, byte[]) Fill((ushort Space, ushort A, ushort B, ushort C, ushort D) color, byte opacity)
    {
        var writer = new PsdBuilder.Writer();
        writer.U32(2);
        writer.Ascii("8BIMnorm");
        Color(writer, color);
        writer.U8(opacity);
        writer.U8(1);
        Color(writer, color);
        return ("sofi", writer.ToArray());
    }

    private static void Fixed(PsdBuilder.Writer writer, double value) => writer.I32((int)Math.Round(value * 65536));

    private static void Color(PsdBuilder.Writer writer, (ushort Space, ushort A, ushort B, ushort C, ushort D) color)
    {
        writer.U16(color.Space);
        writer.U16(color.A);
        writer.U16(color.B);
        writer.U16(color.C);
        writer.U16(color.D);
    }
}
