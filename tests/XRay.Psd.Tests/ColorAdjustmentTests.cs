using XRay.Psd.Imaging;
using XRay.Psd.Rendering;
using XRay.Psd.Tests.Support;

namespace XRay.Psd.Tests;

/// <summary>
/// The adjustments the reference does not model (Channel Mixer, Black and White, Selective
/// Color, Gradient Map, Photo Filter, Vibrance) and noise gradients. Payload bytes come
/// from Photoshop-written files in psd-tools' test collection; see docs/adjustments.md for
/// how each model was checked.
/// </summary>
public sealed class ColorAdjustmentTests
{
    // fill_adjustments.psd (psd-tools): Photoshop's identity Channel Mixer, its Warming
    // Filter (85) Photo Filter at 25% with Preserve Luminosity, and a Vibrance of -6/+2.
    private const string IdentityMixer = "0001000000640000000000000000000000640000000000000000000000640000000000000000000000640000";
    private const string WarmingFilter = "000200071A320C802EE000000000001901000000";
    private const string VibranceMinus6 = "00000010000000010000000000006E756C6C000000020000000876696272616E63656C6F6E67FFFFFFFA00000000537472746C6F6E67000000020000";

    // gradient-map.psd and gradient-map-v3-classic.psd (psd-tools): foreground to background.
    private const string GradientMapV1 = "00010000000000190046006F0072006500670072006F0075006E006400200074006F0020004200610063006B00670072006F0075006E006400000002000000000000003200007B7A5B085B080000000000001000000000320000FFFFFFFFFFFF000000000002000000000000003200FF000010000000003200FF00021000002000001C09F81A000000010000080000030000000000000000800080008000800000000000";
    private const string GradientMapV3 = "0003000047636C73000000190046006F0072006500670072006F0075006E006400200074006F0020004200610063006B00670072006F0075006E00640000000200000000000000320000FFFF000000000000000000001000000000320000FFFFFFFFFFFF000000000002000000000000003200FF000010000000003200FF0002100000200000293A5138000000010000080000030000000000000000800080008000800000000000";

    // ---- Channel Mixer ------------------------------------------------------------------

    [Fact]
    public void Channel_mixer_parses_and_mixes()
    {
        var identity = Adjustments.ParseChannelMixer(Convert.FromHexString(IdentityMixer))!;
        Assert.False(identity.Monochrome);
        Assert.Equal(new MixerChannel(100, 0, 0, 0), identity.Red);
        Assert.Equal(new MixerChannel(0, 0, 100, 0), identity.Blue);
        Assert.Equal((12, 34, 56), Adjustments.ChannelMixerValue(12, 34, 56, identity));

        var settings = new ChannelMixerSettings(false, new(50, 50, 0, 0), new(0, 100, 0, 0), new(0, 0, 100, 10));
        Assert.Equal((150, 200, 76), Adjustments.ChannelMixerValue(100, 200, 50, settings));
        Assert.Equal((255, 0, 0), Adjustments.ChannelMixerValue(255, 0, 0, new ChannelMixerSettings(false, new(200, 0, 0, 0), new(-200, 0, 0, 0), new(0, 0, 0, -200))));

        // A monochrome mixer stores its gray record first and writes it to every channel.
        var mono = new PsdBuilder.Writer();
        mono.U16(1);
        mono.U16(1);
        foreach (var value in new short[] { 30, 59, 11, 0, 0 })
        {
            mono.I16(value);
        }

        mono.Zeros(30);
        var image = RenderOver(new PsdColor(200, 100, 0), ("mixr", mono.ToArray()));
        Assert.Equal(new PsdColor(119, 119, 119), image.GetPixel(0, 0));

        Assert.Null(Adjustments.ParseChannelMixer([0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]));
        Assert.Null(Adjustments.ParseChannelMixer([0, 1, 0, 0, 0, 100]));
    }

    // ---- Black and White ----------------------------------------------------------------

    [Fact]
    public void Black_and_white_weights_the_two_largest_channels()
    {
        var settings = BlackWhiteSettings.Default;
        Assert.Equal((102, 102, 102), Adjustments.BlackWhiteValue(255, 0, 0, settings));    // reds 40
        Assert.Equal((153, 153, 153), Adjustments.BlackWhiteValue(255, 255, 0, settings));  // yellows 60
        Assert.Equal((102, 102, 102), Adjustments.BlackWhiteValue(0, 255, 0, settings));    // greens 40
        Assert.Equal((153, 153, 153), Adjustments.BlackWhiteValue(0, 255, 255, settings));  // cyans 60
        Assert.Equal((51, 51, 51), Adjustments.BlackWhiteValue(0, 0, 255, settings));       // blues 20
        Assert.Equal((204, 204, 204), Adjustments.BlackWhiteValue(255, 0, 255, settings));  // magentas 80
        Assert.Equal((90, 90, 90), Adjustments.BlackWhiteValue(90, 90, 90, settings));      // neutrals keep their value
        Assert.Equal((128, 128, 128), Adjustments.BlackWhiteValue(255, 128, 0, settings));  // 128 * 0.6 + 127 * 0.4

        var tinted = Adjustments.BlackWhiteValue(255, 128, 0, settings with { Tint = new PsdColor(225, 211, 179) });
        Assert.True(tinted.R > tinted.G && tinted.G > tinted.B);
        Assert.InRange((0.3 * tinted.R) + (0.59 * tinted.G) + (0.11 * tinted.B), 127, 129);
    }

    [Fact]
    public void Black_and_white_reads_its_descriptor_and_renders()
    {
        var descriptor = Descriptor(writer =>
        {
            Long(writer, "Rd  ", 100);
            Long(writer, "Yllw", 0);
            Long(writer, "Grn ", 0);
            Long(writer, "Cyn ", 0);
            Long(writer, "Bl  ", 0);
            Long(writer, "Mgnt", 0);
            writer.DescriptorId("useTint");
            writer.Ascii("bool");
            writer.U8(0);
        }, 7);
        var settings = Adjustments.ParseBlackWhite(descriptor)!;
        Assert.Equal(new BlackWhiteSettings(100, 0, 0, 0, 0, 0, null), settings);

        var image = RenderOver(new PsdColor(200, 50, 20), ("blwh", descriptor));
        Assert.Equal(new PsdColor(170, 170, 170), image.GetPixel(0, 0)); // 20 + 30 * 0 + 150 * 1
        Assert.Null(Adjustments.ParseBlackWhite(new byte[] { 0, 0, 0, 16, 1 }));
    }

    // ---- Selective Color ----------------------------------------------------------------

    [Fact]
    public void Selective_color_is_parsed_and_modeled_but_not_rendered()
    {
        var payload = new PsdBuilder.Writer();
        payload.U16(1);
        payload.U16(0);
        payload.Zeros(8);
        payload.I16(-100); // reds: cyan -100
        payload.Zeros(6);
        payload.Zeros(8 * 8);
        var settings = Adjustments.ParseSelectiveColor(payload.ToArray())!;
        Assert.False(settings.Absolute);
        Assert.Equal(new SelectiveColorRange(-100, 0, 0, 0), settings.Ranges[0]);
        Assert.Equal((222, 100, 100), Adjustments.SelectiveColorValue(200, 100, 100, settings));
        Assert.Equal((255, 0, 0), Adjustments.SelectiveColorValue(255, 0, 0, settings)); // no cyan to remove
        Assert.Equal((100, 200, 100), Adjustments.SelectiveColorValue(100, 200, 100, settings));

        var black = new SelectiveColorRange[9];
        black[0] = new SelectiveColorRange(0, 0, 0, 100);
        Assert.Equal((122, 61, 61), Adjustments.SelectiveColorValue(200, 100, 100, new SelectiveColorSettings(true, black)));

        var none = new SelectiveColorSettings(false, new SelectiveColorRange[9]);
        Assert.Equal((17, 128, 250), Adjustments.SelectiveColorValue(17, 128, 250, none));

        var image = RenderOver(new PsdColor(200, 100, 100), ("selc", payload.ToArray()));
        Assert.Equal(new PsdColor(200, 100, 100), image.GetPixel(0, 0));
        Assert.Null(Adjustments.ParseSelectiveColor(new byte[20]));
    }

    // ---- Gradient Map -------------------------------------------------------------------

    [Fact]
    public void Gradient_map_reads_photoshop_payloads()
    {
        var (v1, reverse) = Adjustments.ParseGradientMap(Convert.FromHexString(GradientMapV1))!.Value;
        Assert.False(reverse);
        Assert.Null(v1.Noise);
        Assert.Equal(2, v1.ColorStops.Count);
        Assert.Equal(new PsdColor(123, 91, 91), v1.ColorStops[0].Color);
        Assert.Equal(PsdColor.White, v1.ColorStops[1].Color);
        Assert.Equal(0.5f, v1.ColorStops[1].Midpoint);
        Assert.Equal(1f, v1.Smoothness);

        var (v3, _) = Adjustments.ParseGradientMap(Convert.FromHexString(GradientMapV3))!.Value;
        Assert.Equal(GradientInterpolation.Classic, v3.Interpolation);
        Assert.Equal(new PsdColor(255, 0, 0), v3.ColorStops[0].Color);

        var lut = Adjustments.BuildGradientMapLut(v3, reverse: false);
        Assert.Equal((255, 0, 0), (lut.Red[0], lut.Green[0], lut.Blue[0]));
        Assert.Equal((255, 255, 255), (lut.Red[255], lut.Green[255], lut.Blue[255]));
        Assert.Equal(255, lut.Red[128]);
        Assert.InRange(lut.Green[128], 120, 135);
        for (var v = 1; v < 256; v++)
        {
            Assert.True(lut.Green[v] >= lut.Green[v - 1]);
        }

        var reversed = Adjustments.BuildGradientMapLut(v3, reverse: true);
        Assert.Equal((255, 0, 0), (reversed.Red[255], reversed.Green[255], reversed.Blue[255]));

        Assert.Null(Adjustments.ParseGradientMap([0, 2, 0, 0]));
        Assert.Null(Adjustments.ParseGradientMap(Convert.FromHexString(GradientMapV1)[..40]));
    }

    [Fact]
    public void Gradient_map_maps_luminosity_through_the_ramp()
    {
        var payload = Convert.FromHexString(GradientMapV3);
        var lut = Adjustments.BuildGradientMapLut(Adjustments.ParseGradientMap(payload)!.Value.Gradient, false);

        // Threshold's 30/59/11 luminosity picks the entry.
        var image = RenderOver(new PsdColor(200, 100, 0), ("grdm", payload));
        var index = Adjustments.ThresholdLuminance(200, 100, 0);
        Assert.Equal(new PsdColor(lut.Red[index], lut.Green[index], lut.Blue[index]), image.GetPixel(0, 0));

        // The reverse byte flips the ramp.
        payload[2] = 1;
        image = RenderOver(new PsdColor(0, 0, 0), ("grdm", payload));
        Assert.Equal(PsdColor.White, image.GetPixel(0, 0));
    }

    // ---- Photo Filter -------------------------------------------------------------------

    [Fact]
    public void Photo_filter_reads_lab_colors_and_matches_photoshop_on_white()
    {
        var warming = Adjustments.ParsePhotoFilter(Convert.FromHexString(WarmingFilter))!.Value;
        Assert.Equal(25, warming.Density);
        Assert.True(warming.PreserveLuminosity);
        Assert.InRange(warming.Red * 255, 233, 239); // Photoshop lists Warming Filter (85) as (236, 138, 0)
        Assert.InRange(warming.Green * 255, 130, 140);
        Assert.InRange(warming.Blue * 255, 0, 1);

        // In fill_adjustments.psd the layer turns (217, 212, 209) into Photoshop's (230, 207, 200).
        var (r, g, b) = Adjustments.PhotoFilterValue(217, 212, 209, warming);
        Assert.InRange(r, 228, 232);
        Assert.InRange(g, 205, 209);
        Assert.InRange(b, 198, 202);

        // Version 3 stores the same Lab color as i32 hundredths.
        var v3 = new PsdBuilder.Writer();
        v3.U16(3);
        v3.I32(6706);
        v3.I32(3200);
        v3.I32(12000);
        v3.U32(25);
        v3.U8(1);
        Assert.Equal(warming, Adjustments.ParsePhotoFilter(v3.ToArray()));

        // Without Preserve Luminosity the filter only darkens; at density 0 it is the identity.
        var dark = Adjustments.PhotoFilterValue(200, 200, 200, warming with { PreserveLuminosity = false });
        Assert.True(dark.R <= 200 && dark.G < 200 && dark.B < 200);
        Assert.Equal((10, 20, 30), Adjustments.PhotoFilterValue(10, 20, 30, warming with { Density = 0, PreserveLuminosity = false }));
        Assert.Null(Adjustments.ParsePhotoFilter([0, 4, 0, 0]));
    }

    // ---- Vibrance -----------------------------------------------------------------------

    [Fact]
    public void Vibrance_favors_less_saturated_colors()
    {
        Assert.Equal(new VibranceSettings(-6, 2), Adjustments.ParseVibrance(Convert.FromHexString(VibranceMinus6)));

        var none = new VibranceSettings(0, 0);
        Assert.Equal((130, 120, 110), Adjustments.VibranceValue(130, 120, 110, none));
        var boost = new VibranceSettings(50, 0);
        Assert.Equal((90, 90, 90), Adjustments.VibranceValue(90, 90, 90, boost));

        // A dull color gains more chroma than a saturated one.
        var dull = Adjustments.VibranceValue(130, 120, 110, boost);
        var vivid = Adjustments.VibranceValue(250, 40, 20, boost);
        Assert.True((dull.R - dull.B) / 20.0 > (vivid.R - vivid.B) / 230.0);
        Assert.True(dull.R - dull.B > 25);

        var desaturate = Adjustments.VibranceValue(130, 120, 110, new VibranceSettings(0, -100));
        Assert.True(Math.Abs(desaturate.R - desaturate.B) <= 1);
    }

    [Fact]
    public void Photoshop_chain_with_photo_filter_and_vibrance_matches_if_available()
    {
        // psd-tools' fill_adjustments.psd stacks every adjustment kind over three effect
        // rectangles; with Photo Filter, Vibrance and Color Balance rendered the layer
        // composite is within these bounds of Photoshop's (all three left out: max 29, mean 8.4).
        var path = Fixtures.LocalPath("psd-tools/fill_adjustments.psd");
        Assert.SkipWhen(path is null, "local-test-fixtures/psd-tools/fill_adjustments.psd is not present");
        var document = PsdDocument.Load(path!);
        var diff = ImageTools.Compare(document.Render(new RenderOptions { Source = RenderSource.Layers }), document.GetMergedImage());
        Assert.True(diff.MaxDelta <= 33 && diff.MeanDelta <= 4.3, diff.ToString());
    }

    // ---- Noise gradients ----------------------------------------------------------------

    [Fact]
    public void Noise_gradients_are_deterministic_and_respect_their_ranges()
    {
        var noise = new GradientNoise(991827, 3171, true, GradientNoiseModel.Rgb, [20, 0, 0, 0], [40, 100, 100, 100]);
        var values = new HashSet<double>();
        for (var i = 0; i <= 64; i++)
        {
            var position = i / 64.0;
            Assert.Equal(noise.Channel(1, position), noise.Channel(1, position));
            Assert.InRange(noise.Channel(0, position), 0.2, 0.4);
            values.Add(noise.Channel(1, position));
        }

        Assert.True(values.Count > 32);
        Assert.NotEqual(noise.Channel(1, 0.37), new GradientNoise(5, 3171, true, GradientNoiseModel.Rgb, [0, 0, 0, 0], [100, 100, 100, 100]).Channel(1, 0.37));

        // Roughness 0 flattens the field to the middle of each range.
        var smooth = new GradientNoise(7, 0, false, GradientNoiseModel.Rgb, [0, 0, 0, 0], [100, 100, 100, 100]);
        Assert.Equal((128 / 255f, 128 / 255f, 128 / 255f), smooth.Color(0.3));
        Assert.Equal(0.5, smooth.Channel(3, 0.9));

        // HSB and Lab models produce colors in range; transparency only with ShTr.
        var gradient = new Gradient { Noise = noise };
        Assert.InRange(gradient.Opacity(0.5f, false), 0f, 1f);
        Assert.Equal(1f, new Gradient { Noise = smooth }.Opacity(0.5f, false));
        foreach (var model in new[] { GradientNoiseModel.Hsb, GradientNoiseModel.Lab })
        {
            var (r, g, b) = new GradientNoise(3, 4096, false, model, [0, 0, 0, 0], [100, 100, 100, 100]).Color(0.61);
            Assert.InRange(r, 0f, 1f);
            Assert.InRange(g, 0f, 1f);
            Assert.InRange(b, 0f, 1f);
        }
    }

    [Fact]
    public void Noise_gradient_fill_layers_render_the_noise_field_across_the_canvas()
    {
        // A GdFl layer without pixels and without Angl runs at 0 degrees: the noise varies
        // along x and is constant down each column.
        var fill = Descriptor(writer =>
        {
            writer.DescriptorId("Type");
            writer.Ascii("enum");
            writer.DescriptorId("GrdT");
            writer.DescriptorId("Lnr ");
            writer.DescriptorId("Grad");
            writer.Ascii("Objc");
            writer.UnicodeString("Noise");
            writer.DescriptorId("Grdn");
            writer.U32(8);
            writer.DescriptorId("GrdF");
            writer.Ascii("enum");
            writer.DescriptorId("GrdF");
            writer.DescriptorId("ClNs");
            writer.DescriptorId("ShTr");
            writer.Ascii("bool");
            writer.U8(0);
            writer.DescriptorId("VctC");
            writer.Ascii("bool");
            writer.U8(1);
            writer.DescriptorId("ClrS");
            writer.Ascii("enum");
            writer.DescriptorId("ClrS");
            writer.DescriptorId("RGBC");
            Long(writer, "RndS", 12345);
            Long(writer, "Smth", 4096);
            Range(writer, "Mnm ", 0);
            Range(writer, "Mxm ", 100);
        }, 2);
        var builder = new PsdBuilder { Width = 32, Height = 4 };
        var layer = new BuilderLayer { Name = "Noise", Rect = default };
        layer.Blocks.Add(("GdFl", fill));
        builder.Layers.Add(layer);
        var image = PsdDocument.Load(builder.Build()).Render(new RenderOptions { Source = RenderSource.Layers });

        var expected = new Gradient { AngleDegrees = 0, Noise = new GradientNoise(12345, 4096, false, GradientNoiseModel.Rgb, [0, 0, 0, 0], [100, 100, 100, 100]) };
        var distinct = new HashSet<PsdColor>();
        for (var x = 0; x < 32; x++)
        {
            var color = image.GetPixel(x, 0);
            Assert.Equal(color, image.GetPixel(x, 3));
            distinct.Add(color);
        }

        Assert.True(distinct.Count > 8);
        var position = expected.Position(new PsdRect(0, 0, 32, 4), 5, 0, GradientSpan.CenterChord);
        var (r, g, b) = expected.Color(position, true);
        Assert.Equal(new PsdColor((byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255)), image.GetPixel(5, 0));
    }

    // ---- Helpers ------------------------------------------------------------------------

    private static byte[] Descriptor(Action<PsdBuilder.Writer> items, int count)
    {
        var writer = new PsdBuilder.Writer();
        writer.U32(16);
        writer.UnicodeString(string.Empty);
        writer.DescriptorId("null");
        writer.U32((uint)count);
        items(writer);
        return writer.ToArray();
    }

    private static void Long(PsdBuilder.Writer writer, string key, int value)
    {
        writer.DescriptorId(key);
        writer.Ascii("long");
        writer.I32(value);
    }

    private static void Range(PsdBuilder.Writer writer, string key, int value)
    {
        writer.DescriptorId(key);
        writer.Ascii("VlLs");
        writer.U32(4);
        for (var i = 0; i < 4; i++)
        {
            writer.Ascii("doub");
            writer.F64(value - 0.0012);
        }
    }

    private static RgbaImage RenderOver(PsdColor color, params (string Key, byte[] Data)[] blocks)
    {
        var builder = new PsdBuilder { Width = 2, Height = 2 };
        var background = new BuilderLayer { Name = "Background", Rect = new PsdRect(0, 0, 2, 2) };
        background.Channels[-1] = PsdBuilder.Plane8(2, 2, 255);
        background.Channels[0] = PsdBuilder.Plane8(2, 2, color.R);
        background.Channels[1] = PsdBuilder.Plane8(2, 2, color.G);
        background.Channels[2] = PsdBuilder.Plane8(2, 2, color.B);
        builder.Layers.Add(background);
        var adjustment = new BuilderLayer { Name = "Adjustment", Rect = default };
        adjustment.Blocks.AddRange(blocks);
        builder.Layers.Add(adjustment);
        return PsdDocument.Load(builder.Build()).Render(new RenderOptions { Source = RenderSource.Layers });
    }
}
