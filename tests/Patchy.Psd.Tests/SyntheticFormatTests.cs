using Patchy.Psd.Imaging;
using Patchy.Psd.Layers;
using Patchy.Psd.Rendering;
using Patchy.Psd.Tests.Support;

namespace Patchy.Psd.Tests;

/// <summary>Formats the committed fixtures do not cover, built in memory with <see cref="PsdBuilder"/>.</summary>
public sealed class SyntheticFormatTests
{
    private static BuilderLayer RgbLayer(string name, PsdRect rect, byte r, byte g, byte b, byte a = 255)
    {
        var layer = new BuilderLayer { Name = name, Rect = rect };
        layer.Channels[-1] = PsdBuilder.Plane8(rect.Width, rect.Height, a);
        layer.Channels[0] = PsdBuilder.Plane8(rect.Width, rect.Height, r);
        layer.Channels[1] = PsdBuilder.Plane8(rect.Width, rect.Height, g);
        layer.Channels[2] = PsdBuilder.Plane8(rect.Width, rect.Height, b);
        return layer;
    }

    private static RgbaImage RenderLayers(PsdBuilder builder) =>
        PsdDocument.Load(builder.Build()).Render(new RenderOptions { Source = RenderSource.Layers });

    [Theory]
    [InlineData(PsdCompression.Raw)]
    [InlineData(PsdCompression.Rle)]
    [InlineData(PsdCompression.Zip)]
    [InlineData(PsdCompression.ZipPrediction)]
    public void Layer_channels_decode_with_every_compression(PsdCompression compression)
    {
        var builder = new PsdBuilder { Width = 37, Height = 5, LayerCompression = compression, MergedCompression = compression };
        var rect = new PsdRect(0, 0, 37, 5);
        var layer = new BuilderLayer { Name = "gradient", Rect = rect };
        layer.Channels[-1] = PsdBuilder.Plane8(37, 5, 255);
        layer.Channels[0] = PsdBuilder.Plane8(37, 5, (x, y) => (byte)(x * 7));
        layer.Channels[1] = PsdBuilder.Plane8(37, 5, (x, y) => (byte)(y * 50));
        layer.Channels[2] = PsdBuilder.Plane8(37, 5, (x, y) => (byte)((x + y) % 3 == 0 ? 200 : 10));
        builder.Layers.Add(layer);
        for (var c = 0; c < 3; c++)
        {
            builder.MergedChannels.Add(layer.Channels[(short)c]);
        }

        var document = PsdDocument.Load(builder.Build());
        var pixels = document.Layers[0].GetPixels()!;
        var merged = document.GetMergedImage();
        for (var y = 0; y < 5; y++)
        {
            for (var x = 0; x < 37; x++)
            {
                var expected = new PsdColor((byte)(x * 7), (byte)(y * 50), (byte)((x + y) % 3 == 0 ? 200 : 10));
                Assert.Equal(expected, pixels.GetPixel(x, y));
                Assert.Equal(expected, merged.GetPixel(x, y));
            }
        }
    }

    [Theory]
    [InlineData(PsdCompression.Rle, false)]
    [InlineData(PsdCompression.ZipPrediction, false)]
    [InlineData(PsdCompression.Raw, true)]
    [InlineData(PsdCompression.ZipPrediction, true)]
    public void Sixteen_bit_layers_decode(PsdCompression compression, bool large)
    {
        var builder = new PsdBuilder { Width = 9, Height = 3, Depth = 16, LayerCompression = compression, MergedCompression = compression, Large = large };
        var rect = new PsdRect(1, 0, 9, 3);
        var layer = new BuilderLayer { Name = "deep", Rect = rect };
        layer.Channels[-1] = PsdBuilder.Plane16(8, 3, (_, _) => 65535);
        layer.Channels[0] = PsdBuilder.Plane16(8, 3, (x, _) => (ushort)(x * 8000));
        layer.Channels[1] = PsdBuilder.Plane16(8, 3, (_, y) => (ushort)(y * 30000));
        layer.Channels[2] = PsdBuilder.Plane16(8, 3, (_, _) => 32768);
        builder.Layers.Add(layer);
        builder.MergedChannels.Add(PsdBuilder.Plane16(9, 3, (x, _) => (ushort)(x * 7000)));
        builder.MergedChannels.Add(PsdBuilder.Plane16(9, 3, (_, _) => 0));
        builder.MergedChannels.Add(PsdBuilder.Plane16(9, 3, (_, _) => 65535));

        var document = PsdDocument.Load(builder.Build());
        Assert.Equal(16, document.Depth);
        var pixels = document.Layers.Single().GetPixels()!;
        Assert.Equal(new PsdColor(0, 0, 128), pixels.GetPixel(0, 0));
        Assert.Equal(new PsdColor((byte)Math.Round(7 * 8000 / 257.0), (byte)Math.Round(60000 / 257.0), 128), pixels.GetPixel(7, 2));
        var merged = document.GetMergedImage();
        Assert.Equal(new PsdColor((byte)Math.Round(8 * 7000 / 257.0), 0, 255), merged.GetPixel(8, 1));
    }

    [Theory]
    [InlineData(PsdCompression.Raw)]
    [InlineData(PsdCompression.ZipPrediction)]
    public void Thirty_two_bit_samples_are_gamma_encoded(PsdCompression compression)
    {
        var builder = new PsdBuilder { Width = 3, Height = 1, Depth = 32, LayerCompression = compression, MergedCompression = compression };
        var layer = new BuilderLayer { Name = "hdr", Rect = new PsdRect(0, 0, 3, 1) };
        float[] linear = [0f, 0.214041f, 4f];
        layer.Channels[-1] = PsdBuilder.Plane32(3, 1, (_, _) => 1f);
        for (short c = 0; c < 3; c++)
        {
            layer.Channels[c] = PsdBuilder.Plane32(3, 1, (x, _) => linear[x]);
        }

        builder.Layers.Add(layer);
        for (short c = 0; c < 3; c++)
        {
            builder.MergedChannels.Add(layer.Channels[c]);
        }

        var document = PsdDocument.Load(builder.Build());
        var pixels = document.Layers.Single().GetPixels()!;
        Assert.Equal(0, pixels.GetPixel(0, 0).R);
        Assert.InRange(pixels.GetPixel(1, 0).R, 127, 129); // linear 0.214 is sRGB 0.5
        Assert.Equal(255, pixels.GetPixel(2, 0).R); // over-range values clip
        Assert.InRange(document.GetMergedImage().GetPixel(1, 0).G, 127, 129);
    }

    [Fact]
    public void Grayscale_documents_render_gray()
    {
        var builder = new PsdBuilder { Width = 4, Height = 2, Mode = PsdColorMode.Grayscale };
        var layer = new BuilderLayer { Name = "gray", Rect = new PsdRect(0, 0, 4, 2) };
        layer.Channels[-1] = PsdBuilder.Plane8(4, 2, 255);
        layer.Channels[0] = PsdBuilder.Plane8(4, 2, (x, _) => (byte)(x * 80));
        builder.Layers.Add(layer);
        builder.MergedChannels.Add(layer.Channels[0]);

        var image = RenderLayers(builder);
        Assert.Equal(new PsdColor(160, 160, 160), image.GetPixel(2, 1));
        Assert.Equal(new PsdColor(240, 240, 240), PsdDocument.Load(builder.Build()).GetMergedImage().GetPixel(3, 0));
    }

    [Fact]
    public void Indexed_documents_use_the_palette()
    {
        var palette = new byte[768];
        palette[1] = 255;           // index 1: red
        palette[256 + 2] = 255;     // index 2: green
        palette[512 + 3] = 200;     // index 3: blue 200
        var builder = new PsdBuilder { Width = 4, Height = 1, Mode = PsdColorMode.Indexed, ColorModeData = palette };
        builder.MergedChannels.Add([0, 1, 2, 3]);

        var document = PsdDocument.Load(builder.Build());
        Assert.NotNull(document.Palette);
        var image = document.Render();
        Assert.Equal(new PsdColor(0, 0, 0), image.GetPixel(0, 0));
        Assert.Equal(new PsdColor(255, 0, 0), image.GetPixel(1, 0));
        Assert.Equal(new PsdColor(0, 255, 0), image.GetPixel(2, 0));
        Assert.Equal(new PsdColor(0, 0, 200), image.GetPixel(3, 0));
    }

    [Fact]
    public void Cmyk_samples_are_stored_inverted()
    {
        var builder = new PsdBuilder { Width = 2, Height = 1, Mode = PsdColorMode.Cmyk };
        builder.MergedChannels.Add([255, 0]);   // C: none, full
        builder.MergedChannels.Add([255, 255]); // M: none
        builder.MergedChannels.Add([255, 255]); // Y: none
        builder.MergedChannels.Add([255, 255]); // K: none

        var image = PsdDocument.Load(builder.Build()).Render();
        Assert.Equal(new PsdColor(255, 255, 255), image.GetPixel(0, 0));
        Assert.Equal(new PsdColor(0, 255, 255), image.GetPixel(1, 0));
    }

    [Fact]
    public void Lab_documents_convert_to_srgb()
    {
        var builder = new PsdBuilder { Width = 3, Height = 1, Mode = PsdColorMode.Lab };
        builder.MergedChannels.Add([255, 0, 128]); // L 100, 0, ~50
        builder.MergedChannels.Add([128, 128, 128]); // a 0
        builder.MergedChannels.Add([128, 128, 128]); // b 0

        var image = PsdDocument.Load(builder.Build()).Render();
        var white = image.GetPixel(0, 0);
        Assert.InRange(white.R, 253, 255);
        Assert.InRange(white.G, 253, 255);
        Assert.InRange(white.B, 253, 255);
        Assert.Equal(new PsdColor(0, 0, 0), image.GetPixel(1, 0));
        var mid = image.GetPixel(2, 0);
        Assert.InRange(mid.G, 115, 122); // L* 50 is sRGB ~119
        Assert.InRange(Math.Abs(mid.R - mid.B), 0, 3);
    }

    [Fact]
    public void Bitmap_documents_decode_one_bit_rows()
    {
        var builder = new PsdBuilder { Width = 10, Height = 1, Depth = 1, Mode = PsdColorMode.Bitmap, MergedCompression = PsdCompression.Raw };
        builder.MergedChannels.Add([0b1010_0000, 0b0100_0000]);

        var image = PsdDocument.Load(builder.Build()).Render();
        Assert.Equal(PsdColor.Black, image.GetPixel(0, 0));
        Assert.Equal(PsdColor.White, image.GetPixel(1, 0));
        Assert.Equal(PsdColor.Black, image.GetPixel(2, 0));
        Assert.Equal(PsdColor.Black, image.GetPixel(9, 0));
        Assert.Equal(PsdColor.White, image.GetPixel(8, 0));
    }

    [Fact]
    public void Merged_transparency_channel_is_alpha()
    {
        var builder = new PsdBuilder { Width = 2, Height = 1, MergedTransparency = true };
        builder.Layers.Add(RgbLayer("dot", new PsdRect(0, 0, 1, 1), 255, 0, 0));
        builder.MergedChannels.Add([255, 255]);
        builder.MergedChannels.Add([0, 255]);
        builder.MergedChannels.Add([0, 255]);
        builder.MergedChannels.Add([255, 0]);

        var document = PsdDocument.Load(builder.Build());
        Assert.True(document.MergedImageHasTransparency);
        var merged = document.GetMergedImage();
        Assert.Equal(new PsdColor(255, 0, 0), merged.GetPixel(0, 0));
        Assert.Equal(0, merged.GetPixel(1, 0).A);
    }

    [Fact]
    public void Blend_modes_opacity_and_hidden_layers_composite()
    {
        var builder = new PsdBuilder { Width = 3, Height = 1 };
        builder.Layers.Add(RgbLayer("base", new PsdRect(0, 0, 3, 1), 200, 100, 50));
        var multiply = RgbLayer("multiply", new PsdRect(0, 0, 1, 1), 128, 128, 128);
        multiply.BlendKey = "mul ";
        builder.Layers.Add(multiply);
        var half = RgbLayer("half", new PsdRect(1, 0, 2, 1), 0, 0, 0);
        half.Opacity = 128;
        builder.Layers.Add(half);
        var hidden = RgbLayer("hidden", new PsdRect(2, 0, 3, 1), 0, 255, 0);
        hidden.Hidden = true;
        builder.Layers.Add(hidden);

        var image = RenderLayers(builder);
        AssertNear(new PsdColor(100, 50, 25), image.GetPixel(0, 0));
        AssertNear(new PsdColor(100, 50, 25), image.GetPixel(1, 0));
        Assert.Equal(new PsdColor(200, 100, 50), image.GetPixel(2, 0));
    }

    [Fact]
    public void Layer_masks_attenuate_with_default_color_outside()
    {
        var builder = new PsdBuilder { Width = 4, Height = 1 };
        var layer = RgbLayer("masked", new PsdRect(0, 0, 4, 1), 255, 255, 255);
        layer.MaskRect = new PsdRect(1, 0, 3, 1);
        layer.MaskDefault = 0;
        layer.Channels[-2] = [255, 64];
        builder.Layers.Add(layer);

        var image = RenderLayers(builder);
        Assert.Equal(0, image.GetPixel(0, 0).A);
        Assert.Equal(255, image.GetPixel(1, 0).A);
        Assert.InRange(image.GetPixel(2, 0).A, 63, 65);
        Assert.Equal(0, image.GetPixel(3, 0).A);
    }

    [Fact]
    public void Twenty_byte_mask_record_carries_parameters_in_place_of_padding()
    {
        // Flag bit 4 with a density-only parameter block fills the two pad bytes of the
        // short record (photoshop-user-mask-params.psd "density-only"): density 64 lifts
        // the hidden floor to 1 - 64/255.
        var builder = new PsdBuilder { Width = 2, Height = 1 };
        var layer = RgbLayer("density", new PsdRect(0, 0, 2, 1), 255, 255, 255);
        layer.MaskRect = new PsdRect(0, 0, 2, 1);
        layer.MaskFlags = 0x10;
        layer.MaskTail = [0x01, 64];
        layer.Channels[-2] = [0, 255];
        builder.Layers.Add(layer);

        var document = PsdDocument.Load(builder.Build());
        Assert.Equal(64, document.Layers[0].Mask!.Density);
        var image = document.Render(new RenderOptions { Source = RenderSource.Layers });
        Assert.InRange(image.GetPixel(0, 0).A, 190, 192);
        Assert.Equal(255, image.GetPixel(1, 0).A);
    }

    [Fact]
    public void Disabled_masks_are_ignored()
    {
        var builder = new PsdBuilder { Width = 2, Height = 1 };
        var layer = RgbLayer("masked", new PsdRect(0, 0, 2, 1), 255, 255, 255);
        layer.MaskRect = new PsdRect(0, 0, 2, 1);
        layer.MaskFlags = 0x02;
        layer.Channels[-2] = [0, 0];
        builder.Layers.Add(layer);

        Assert.Equal(255, RenderLayers(builder).GetPixel(0, 0).A);
    }

    [Fact]
    public void Clipped_layers_paint_only_inside_the_base()
    {
        var builder = new PsdBuilder { Width = 4, Height = 1 };
        builder.Layers.Add(RgbLayer("base", new PsdRect(1, 0, 3, 1), 255, 0, 0));
        var clipped = RgbLayer("clipped", new PsdRect(0, 0, 4, 1), 0, 0, 255);
        clipped.Clipped = true;
        builder.Layers.Add(clipped);

        var image = RenderLayers(builder);
        Assert.Equal(0, image.GetPixel(0, 0).A);
        Assert.Equal(new PsdColor(0, 0, 255), image.GetPixel(1, 0));
        Assert.Equal(new PsdColor(0, 0, 255), image.GetPixel(2, 0));
        Assert.Equal(0, image.GetPixel(3, 0).A);
    }

    [Fact]
    public void Hidden_clip_base_hides_its_members()
    {
        var builder = new PsdBuilder { Width = 1, Height = 1 };
        var clipBase = RgbLayer("base", new PsdRect(0, 0, 1, 1), 255, 0, 0);
        clipBase.Hidden = true;
        builder.Layers.Add(clipBase);
        var clipped = RgbLayer("clipped", new PsdRect(0, 0, 1, 1), 0, 0, 255);
        clipped.Clipped = true;
        builder.Layers.Add(clipped);

        Assert.Equal(0, RenderLayers(builder).GetPixel(0, 0).A);
    }

    [Fact]
    public void Pass_through_group_opacity_fades_the_group_result()
    {
        // Background white, group (pass through, 50%) holding an opaque black layer: 50% gray.
        var builder = new PsdBuilder { Width = 1, Height = 1 };
        builder.Layers.Add(RgbLayer("bg", new PsdRect(0, 0, 1, 1), 255, 255, 255));
        builder.Layers.Add(new BuilderLayer { Name = "</Layer group>", SectionType = 3 });
        builder.Layers.Add(RgbLayer("ink", new PsdRect(0, 0, 1, 1), 0, 0, 0));
        builder.Layers.Add(new BuilderLayer { Name = "group", SectionType = 1, SectionBlendKey = "pass", BlendKey = "pass", Opacity = 128 });

        var document = PsdDocument.Load(builder.Build());
        var group = Assert.Single(document.RootLayers, l => l.IsGroup);
        Assert.Equal("ink", Assert.Single(group.Children).Name);
        Assert.Equal(PsdBlendMode.PassThrough, group.BlendMode);
        AssertNear(new PsdColor(127, 127, 127), document.Render(new RenderOptions { Source = RenderSource.Layers }).GetPixel(0, 0));
    }

    [Fact]
    public void Isolated_groups_keep_child_blend_modes_inside()
    {
        // A Multiply child inside a Normal group only multiplies with its group's
        // contents (transparent), so it shows unblended over the backdrop.
        var builder = new PsdBuilder { Width = 1, Height = 1 };
        builder.Layers.Add(RgbLayer("bg", new PsdRect(0, 0, 1, 1), 100, 100, 100));
        builder.Layers.Add(new BuilderLayer { Name = "</Layer group>", SectionType = 3 });
        var child = RgbLayer("multiply", new PsdRect(0, 0, 1, 1), 200, 200, 200);
        child.BlendKey = "mul ";
        builder.Layers.Add(child);
        builder.Layers.Add(new BuilderLayer { Name = "group", SectionType = 1, SectionBlendKey = "norm" });

        Assert.Equal(new PsdColor(200, 200, 200), RenderLayers(builder).GetPixel(0, 0));
    }

    [Fact]
    public void Psb_with_rle_and_wide_lengths_round_trips()
    {
        var builder = new PsdBuilder { Width = 300, Height = 2, Large = true };
        builder.Layers.Add(RgbLayer("wide", new PsdRect(0, 0, 300, 2), 10, 20, 30));
        builder.MergedChannels.Add(PsdBuilder.Plane8(300, 2, 10));
        builder.MergedChannels.Add(PsdBuilder.Plane8(300, 2, 20));
        builder.MergedChannels.Add(PsdBuilder.Plane8(300, 2, 30));

        var document = PsdDocument.Load(builder.Build());
        Assert.True(document.IsLargeDocument);
        Assert.Equal(new PsdColor(10, 20, 30), document.Render(new RenderOptions { Source = RenderSource.Layers }).GetPixel(299, 1));
        Assert.Equal(new PsdColor(10, 20, 30), document.GetMergedImage().GetPixel(150, 0));
    }

    [Fact]
    public void Unicode_layer_names_win_over_pascal_names()
    {
        var builder = new PsdBuilder { Width = 1, Height = 1 };
        builder.Layers.Add(RgbLayer("Ünïcødé 名前", new PsdRect(0, 0, 1, 1), 0, 0, 0));
        Assert.Equal("Ünïcødé 名前", PsdDocument.Load(builder.Build()).Layers[0].Name);
    }

    [Fact]
    public void Text_extraction_recurses_into_embedded_psd_smart_objects()
    {
        var inner = new PsdBuilder { Width = 2, Height = 2 };
        inner.Layers.Add(RgbLayer("Inner Secret", new PsdRect(0, 0, 2, 2), 1, 2, 3));
        var innerBytes = inner.Build();

        var outer = new PsdBuilder { Width = 2, Height = 2 };
        var smart = RgbLayer("Smart Layer", new PsdRect(0, 0, 2, 2), 1, 2, 3);
        smart.Blocks.Add(("PlLd", PlacedLayerBlock("uuid-1")));
        outer.Layers.Add(smart);
        outer.GlobalBlocks.Add(("lnk2", LinkedFileBlock("uuid-1", "inner.psb", innerBytes)));

        var document = PsdDocument.Load(outer.Build());
        Assert.Equal(PsdLayerKind.SmartObject, document.Layers[0].Kind);
        var file = Assert.Single(document.LinkedFiles);
        Assert.True(file.IsPhotoshopDocument);

        var text = document.ExtractText();
        var nested = Assert.Single(text.Items, i => i.Text == "Inner Secret");
        Assert.Equal(1, nested.Depth);
        Assert.Equal("Smart Layer>Inner Secret", nested.Source);
        Assert.Contains(text.Items, i => i.Kind == Text.PsdTextKind.LinkedFileName && i.Text == "inner.psb");

        var shallow = document.ExtractText(new Text.TextExtractionOptions { MaxEmbeddedDepth = 0 });
        Assert.DoesNotContain(shallow.Items, i => i.Text == "Inner Secret");
    }

    internal static byte[] PlacedLayerBlock(string id)
    {
        var writer = new PsdBuilder.Writer();
        writer.Ascii("plcL");
        writer.U32(3);
        writer.Pascal(id, 1);
        writer.U32(1);
        writer.U32(1);
        writer.U32(16);
        writer.U32(0);
        for (var i = 0; i < 8; i++)
        {
            writer.F64(0);
        }

        return writer.ToArray();
    }

    internal static byte[] LinkedFileBlock(string id, string fileName, byte[] data)
    {
        var body = new PsdBuilder.Writer();
        body.Ascii("liFD");
        body.U32(7);
        body.Pascal(id, 1);
        body.UnicodeString(fileName);
        body.Ascii("8BPB");
        body.Ascii("8BIM");
        body.U64((ulong)data.Length);
        body.U8(0);
        body.Bytes(data);
        body.U32(0);
        body.F64(0);
        body.U8(0);

        var element = new PsdBuilder.Writer();
        element.U64((ulong)body.Length);
        element.Bytes(body.ToArray());
        element.Zeros((int)((4 - (body.Length % 4)) % 4));
        return element.ToArray();
    }

    private static void AssertNear(PsdColor expected, PsdColor actual, int tolerance = 1)
    {
        Assert.True(
            Math.Abs(expected.R - actual.R) <= tolerance && Math.Abs(expected.G - actual.G) <= tolerance && Math.Abs(expected.B - actual.B) <= tolerance,
            $"expected {expected}, got {actual}");
    }
}
