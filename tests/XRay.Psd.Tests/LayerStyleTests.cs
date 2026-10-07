using XRay.Psd.Layers;
using XRay.Psd.Tests.Support;

namespace XRay.Psd.Tests;

public sealed class LayerStyleTests
{
    [Fact]
    public void Multi_instance_effects_project_in_file_order_with_their_switches()
    {
        var layer = Fixtures.Load("photoshop-lmfx-multi-stroke.psd").Layers.Single(l => l.Name == "Comic Pow");

        var style = layer.Style;
        Assert.NotNull(style);
        Assert.True(style.Visible);
        Assert.Same(layer.Effects, style.Descriptor);
        Assert.Equal(10, style.Effects.Count);
        Assert.Equal(
            [PsdLayerEffectKind.DropShadow, PsdLayerEffectKind.ColorOverlay, PsdLayerEffectKind.Stroke, PsdLayerEffectKind.Stroke],
            style.ActiveEffects.Select(e => e.Kind));

        var shadow = style.Effects[0];
        Assert.Equal((PsdLayerEffectKind.DropShadow, true, PsdBlendMode.Multiply, PsdColor.Black), (shadow.Kind, shadow.Enabled, shadow.BlendMode, shadow.Color));
        Assert.Equal(0.9f, shadow.Opacity, 3);
        Assert.Equal((0f, 7f, 128f), (shadow.Size, shadow.Distance, shadow.Angle));

        var strokes = style.Effects.Where(e => e.Kind == PsdLayerEffectKind.Stroke).ToList();
        Assert.Equal([PsdStrokePosition.Inside, PsdStrokePosition.Outside], strokes.Select(s => s.StrokePosition));
        Assert.Equal([3f, 6f], strokes.Select(s => s.Size));
        Assert.Equal([new PsdColor(0xE2, 0x06, 0x2C), PsdColor.White], strokes.Select(s => s.Color));

        // Disabled effects are listed with the defaults the renderer would use.
        var innerShadow = style.Effects.Single(e => e.Kind == PsdLayerEffectKind.InnerShadow);
        Assert.False(innerShadow.Enabled);
        Assert.Equal(PsdBlendMode.Multiply, innerShadow.BlendMode);
        Assert.NotNull(innerShadow.Distance);
    }

    [Fact]
    public void Global_light_resolves_into_the_angle()
    {
        var document = Fixtures.Load("photoshop-global-light-shadow.psd");

        var shadow = document.Layers.Single(l => l.Style is not null).Style!.ActiveEffects.Single();

        Assert.Equal(PsdLayerEffectKind.DropShadow, shadow.Kind);
        Assert.True(shadow.Descriptor!.GetBoolean("uglg"));
        Assert.Equal(document.GlobalLightAngle, shadow.Angle);
        Assert.Equal((4f, 10f), (shadow.Size, shadow.Distance));
    }

    [Fact]
    public void Bevel_and_overlay_details_are_exposed()
    {
        var bevel = Fixtures.Load("photoshop-bevel-default.psd").Layers.Single(l => l.Name == "bevelled").Style!.ActiveEffects.Single();
        Assert.Equal((PsdLayerEffectKind.BevelEmboss, PsdBevelStyle.OuterBevel, PsdBevelTechnique.Smooth), (bevel.Kind, bevel.BevelStyle, bevel.BevelTechnique));
        Assert.Equal((PsdBlendMode.Screen, PsdColor.White, PsdBlendMode.Multiply, PsdColor.Black), (bevel.BlendMode, bevel.Color, bevel.ShadowBlendMode, bevel.ShadowColor));
        Assert.NotNull(bevel.Altitude);
        Assert.NotNull(bevel.Depth);

        var arrows = Fixtures.Load("arrows.psd").Layers.First(l => l.Name == "Shape 5").Style!;
        var gradient = arrows.ActiveEffects.Single(e => e.Kind == PsdLayerEffectKind.GradientOverlay);
        Assert.True(gradient.HasGradient);
        Assert.Null(gradient.Color);
        Assert.Equal(90f, gradient.Angle);
        Assert.Contains(arrows.Effects, e => e is { Kind: PsdLayerEffectKind.ColorOverlay, Enabled: false });
    }

    [Fact]
    public void Layers_without_effects_have_no_style()
    {
        var document = Fixtures.Load("photoshop-group-opacity.psd");

        Assert.All(document.Layers.Where(l => l.Effects is null), l => Assert.Null(l.Style));
    }

    [Fact]
    public void Master_switch_off_leaves_effects_listed_but_inactive()
    {
        var builder = new PsdBuilder();
        var layer = new BuilderLayer { Name = "styled", Rect = new PsdRect(0, 0, 2, 2) };
        layer.Channels[0] = PsdBuilder.Plane8(2, 2, 255);
        layer.Channels[1] = PsdBuilder.Plane8(2, 2, 0);
        layer.Channels[2] = PsdBuilder.Plane8(2, 2, 0);
        layer.Blocks.Add(("lfx2", ColorOverlayEffects(masterSwitch: false)));
        builder.Layers.Add(layer);

        var style = PsdDocument.Load(builder.Build()).Layers.Single().Style;

        Assert.NotNull(style);
        Assert.False(style.Visible);
        var overlay = Assert.Single(style.Effects);
        Assert.Equal((PsdLayerEffectKind.ColorOverlay, true, new PsdColor(0, 128, 255)), (overlay.Kind, overlay.Enabled, overlay.Color));
        Assert.Empty(style.ActiveEffects);
    }

    private static byte[] ColorOverlayEffects(bool masterSwitch)
    {
        var writer = new PsdBuilder.Writer();
        writer.U32(0);
        writer.U32(16);
        writer.UnicodeString(string.Empty);
        writer.DescriptorId("null");
        writer.U32(2);
        writer.DescriptorId("masterFXSwitch");
        writer.Ascii("bool");
        writer.U8(masterSwitch ? (byte)1 : (byte)0);
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
        foreach (var (key, value) in new[] { ("Rd  ", 0.0), ("Grn ", 128.0), ("Bl  ", 255.0) })
        {
            writer.DescriptorId(key);
            writer.Ascii("doub");
            writer.F64(value);
        }

        return writer.ToArray();
    }
}
