using Patchy.Psd.Imaging;
using Patchy.Psd.Layers;
using Patchy.Psd.Rendering;
using Patchy.Psd.Tests.Support;

namespace Patchy.Psd.Tests;

/// <summary>
/// Adjustment layers: payload parsing, the calibrated math (expected values come from
/// .reference/tests/core/adjustments_curves_tests.cpp, which pins them against Photoshop
/// 2026 captures), and the compositor against Photoshop's renders where a fixture has one.
/// </summary>
public sealed class AdjustmentTests
{
    // ---- Fixtures against Photoshop -------------------------------------------------

    /// <summary>Fixtures whose saved composite is Photoshop's own render of the adjustment.</summary>
    [Theory]
    [InlineData("photoshop-clipping-mask.psd", 0, 0.0)]                 // Levels, clipped
    [InlineData("photoshop-brightness-contrast-legacy.psd", 1, 0.8)]    // legacy brit, +/-1 envelope
    [InlineData("photoshop-brightness-contrast-modern.psd", 0, 0.0)]    // CgEd modern algorithm
    [InlineData("photoshop-hue-saturation-colorize.psd", 0, 0.0)]       // colorize, masked
    public void Adjustment_fixtures_match_the_saved_composite(string name, int maxDelta, double meanDelta)
    {
        var document = Fixtures.Load(name);
        var rendered = document.Render(new RenderOptions { Source = RenderSource.Layers });

        var diff = ImageTools.Compare(rendered, document.GetMergedImage());

        Assert.True(diff.MaxDelta <= maxDelta && diff.MeanDelta <= meanDelta, $"{name}: {diff}");
    }

    /// <summary>
    /// The Hue/Saturation probe fixtures: their saved composite predates the byte-patched
    /// settings, so the Photoshop BMP is the reference. Bounds are the reference's
    /// calibration envelopes (master within 2/255; band feather ramps up to 7/255).
    /// </summary>
    [Theory]
    [InlineData("photoshop-hue-saturation-master.psd", 1, 0.1)]
    [InlineData("photoshop-hue-saturation-bands.psd", 7, 0.15)]
    public void Hue_saturation_fixtures_match_photoshop_bmp(string name, int maxDelta, double meanDelta)
    {
        var document = Fixtures.Load(name);
        var reference = ImageTools.ReadBmp(Path.ChangeExtension(Fixtures.PathOf(name), ".bmp"));
        var rendered = document.Render(new RenderOptions { Source = RenderSource.Layers });

        var diff = ImageTools.Compare(rendered, reference);

        Assert.True(diff.MaxDelta <= maxDelta && diff.MeanDelta <= meanDelta, $"{name}: {diff}");
    }

    [Theory]
    [InlineData("photoshop-curves-masked.psd", "Curves rich-masked", true)]
    [InlineData("photoshop-curves-clipped.psd", "Curves rich-clipped", false)]
    public void Curves_fixtures_parse_and_apply_the_composed_lut(string name, string layerName, bool leftHalfMasked)
    {
        var document = Fixtures.Load(name);
        var layer = document.Layers.Single(l => l.Name == layerName);
        var curves = Adjustments.ParseCurves(layer.GetTaggedBlock("curv")!.Data.Span);
        Assert.NotNull(curves);
        AssertCurve(curves.Rgb, (0, 8), (64, 35), (128, 190), (255, 245));
        AssertCurve(curves.Red, (0, 20), (80, 45), (160, 225), (255, 230));
        AssertCurve(curves.Green, (0, 4), (96, 120), (220, 238), (255, 250));
        AssertCurve(curves.Blue, (0, 250), (112, 160), (255, 5));

        // The saved composites of these two files do not include the curve, so the check
        // is the compositor against the (Photoshop-calibrated) LUT on the base pixels.
        var lut = Adjustments.BuildCurvesLut(curves);
        var background = document.Layers.Single(l => l.Kind == PsdLayerKind.Pixel).GetPixels()!;
        var rendered = document.Render(new RenderOptions { Source = RenderSource.Layers });
        for (var y = 0; y < rendered.Height; y++)
        {
            for (var x = 0; x < rendered.Width; x++)
            {
                var input = background.GetPixel(x, y);
                var expected = leftHalfMasked && x < 4 ? input : new PsdColor(lut.Red[input.R], lut.Green[input.G], lut.Blue[input.B]);
                Assert.Equal(expected, rendered.GetPixel(x, y));
            }
        }
    }

    [Fact]
    public void Fixture_payloads_parse_to_photoshop_settings()
    {
        var legacy = BlockOf("photoshop-brightness-contrast-legacy.psd", "Brightness/Contrast 1", "brit");
        Assert.Equal(new BrightnessContrastSettings(30, -20, true), Adjustments.ParseBrightnessContrastLegacy(legacy.Data.Span));

        var modern = Fixtures.Load("photoshop-brightness-contrast-modern.psd").Layers.Single(l => l.Name == "Brightness/Contrast 1");
        Assert.Equal(new BrightnessContrastSettings(40, 25, false), Adjustments.ParseBrightnessContrastDescriptor(modern.GetTaggedBlock("CgEd")!.Data));
        Assert.Equal(new BrightnessContrastSettings(0, 0, true), Adjustments.ParseBrightnessContrastLegacy(modern.GetTaggedBlock("brit")!.Data.Span));

        var balance = BlockOf("photoshop-color-balance.psd", "Color Balance 1", "blnc");
        Assert.Equal(new ColorBalanceSettings(45, -25, 35), Adjustments.ParseColorBalance(balance.Data.Span));
        var fullBalance = BlockOf("photoshop-color-balance-full.psd", "Color Balance 1", "blnc");
        Assert.Equal(new ColorBalanceSettings(45, -25, 35), Adjustments.ParseColorBalance(fullBalance.Data.Span));

        var levels = Adjustments.ParseLevels(BlockOf("photoshop-clipping-mask.psd", "Levels 1", "levl").Data.Span);
        Assert.NotNull(levels);
        Assert.Equal(new LevelsRecord(20, 235, 125, 30, 255), levels.Master);
        Assert.Equal(LevelsRecord.Identity, levels.Red);

        var colorize = Adjustments.ParseHueSaturation(BlockOf("photoshop-hue-saturation-colorize.psd", "Hue/Saturation 1", "hue2").Data.Span);
        Assert.NotNull(colorize);
        Assert.True(colorize.Colorize);
        Assert.Equal(203, colorize.ColorizeHue);
        Assert.Equal(52, colorize.ColorizeSaturation);

        var bands = Fixtures.Load("photoshop-hue-saturation-bands.psd");
        var first = Adjustments.ParseHueSaturation(bands.Layers.Single(l => l.Name == "Master 1").GetTaggedBlock("hue2")!.Data.Span)!;
        Assert.Equal(new HueSaturationBand(315, 345, 15, 45, 60, 0, 0), first.Bands[0]);
        var fifth = Adjustments.ParseHueSaturation(bands.Layers.Single(l => l.Name == "Master 5").GetTaggedBlock("hue2")!.Data.Span)!;
        Assert.Equal(40, fifth.Bands[0].Hue);
        Assert.Equal(-40, fifth.Bands[1].Hue);

        var master = Fixtures.Load("photoshop-hue-saturation-master.psd");
        int[][] sliders = [[-12, 14, -14], [45, 0, 0], [-90, 0, 0], [0, -100, 0], [0, 60, 0], [0, 0, 40]];
        for (var i = 0; i < sliders.Length; i++)
        {
            var settings = Adjustments.ParseHueSaturation(master.Layers.Single(l => l.Name == $"Master {i + 1}").GetTaggedBlock("hue2")!.Data.Span)!;
            Assert.False(settings.Colorize);
            Assert.Equal(sliders[i], new[] { settings.Hue, settings.Saturation, settings.Lightness });
        }
    }

    [Fact]
    public void Color_balance_is_parsed_but_not_rendered()
    {
        // The reference's midtones-only model is far from Photoshop on this fixture (max
        // 98/255); leaving the layer out is closer (29/255) until it is calibrated.
        var layer = Fixtures.Load("photoshop-color-balance.psd").Layers.Single(l => l.Name == "Color Balance 1");
        Assert.Null(Adjustments.Create(layer));

        var lut = Adjustments.BuildColorBalanceLut(new ColorBalanceSettings(45, -25, 35));
        Assert.Equal(130, lut.Red[15]);   // 15 + round(45 * 2.55)
        Assert.Equal(176, lut.Green[240]); // 240 - round(25 * 2.55)
        Assert.Equal(255, lut.Blue[200]);
    }

    // ---- Curves -----------------------------------------------------------------------

    [Fact]
    public void Curve_points_normalize_and_luts_compose()
    {
        var normalized = Adjustments.NormalizeCurve([new(255, 260), new(64, 20), new(64, 45), new(-8, 12), new(128, 190)]);
        Assert.Equal(new CurvePoint[] { new(0, 12), new(64, 45), new(128, 190), new(255, 255) }, normalized);

        var identity = Adjustments.BuildCurveLut([new(0, 0), new(128, 128), new(255, 255)]);
        for (var v = 0; v < 256; v++)
        {
            Assert.Equal(v, identity[v]);
        }

        var clamped = Adjustments.BuildCurveLut([new(16, 9), new(240, 246)]);
        Assert.All(clamped[..17], value => Assert.Equal(9, value));
        Assert.All(clamped[240..], value => Assert.Equal(246, value));

        var curves = new CurvesSettings(
            [new(0, 8), new(64, 35), new(128, 190), new(255, 245)],
            [new(0, 20), new(255, 230)],
            [new(0, 0), new(96, 120), new(255, 255)],
            [new(0, 255), new(255, 0)]);
        var composed = Adjustments.BuildCurvesLut(curves);
        var master = Adjustments.BuildCurveLut(curves.Rgb);
        var red = Adjustments.BuildCurveLut(curves.Red);
        var blue = Adjustments.BuildCurveLut(curves.Blue);
        for (var v = 0; v < 256; v++)
        {
            Assert.Equal(master[red[v]], composed.Red[v]);
            Assert.Equal(master[blue[v]], composed.Blue[v]);
        }
    }

    /// <summary>FNV-1a hashes of Photoshop 2026's ramp captures, pinning every LUT byte.</summary>
    [Theory]
    [InlineData("0,0 255,255", 0x16d173bdfcdae583UL)]
    [InlineData("0,255 255,0", 0x7ec50c4b6e7dc283UL)]
    [InlineData("0,0 128,220 255,255", 0x011a874190be8f3aUL)]
    [InlineData("0,0 64,30 128,200 200,180 255,255", 0x6bf59b8d1add82b7UL)]
    [InlineData("0,0 17,64 91,80 173,230 255,255", 0x25b500895b388a20UL)]
    [InlineData("0,0 64,64 128,64 192,192 255,255", 0x7eff683aa1d494ebUL)]
    [InlineData("0,0 127,0 128,255 255,255", 0xe8c09cd965f43303UL)]
    [InlineData("16,0 128,160 240,255", 0x2c49d26da6d1523eUL)]
    [InlineData("0,0 17,48 34,20 51,92 68,55 85,140 102,80 119,180 136,110 153,215 170,135 187,235 204,170 221,248 238,205 255,255", 0x12dd8b9c1d5dc5b8UL)]
    public void Curve_lut_matches_photoshop_calibration(string points, ulong expected)
    {
        Assert.Equal(expected, Fnv(Adjustments.BuildCurveLut(Points(points))));
    }

    [Fact]
    public void Composed_curve_lut_matches_photoshop_calibration()
    {
        var curves = CurvesSettings.Identity with
        {
            Rgb = Points("0,8 64,35 128,190 255,245"),
            Red = Points("0,20 80,45 160,225 255,230"),
        };
        Assert.Equal(0xe19b4c2c6f440cb5UL, Fnv(Adjustments.BuildCurvesLut(curves).Red));
    }

    [Fact]
    public void Acv_reads_bitmap_shapes_and_the_indexed_extension()
    {
        // Photoshop 2026: u32 bitmap (composite, red, blue), then an indexed Crv extension
        // that repeats and supersedes every channel.
        var current = new PsdBuilder.Writer();
        current.U16(1);
        current.U32(0x0000000b);
        Curve(current, "0,0 128,180 255,255");
        Curve(current, "0,10 255,245");
        Curve(current, "0,250 255,5");
        current.Ascii("Crv ");
        current.U16(4);
        current.U32(4);
        current.U16(0);
        Curve(current, "0,0 128,190 255,255");
        current.U16(1);
        Curve(current, "0,12 255,240");
        current.U16(2);
        Curve(current, "0,4 96,120 255,251");
        current.U16(3);
        Curve(current, "0,255 255,0");
        var parsed = Adjustments.ReadAcv(current.ToArray());
        AssertCurve(parsed.Rgb, (0, 0), (128, 190), (255, 255));
        AssertCurve(parsed.Red, (0, 12), (255, 240));
        AssertCurve(parsed.Green, (0, 4), (96, 120), (255, 251));
        AssertCurve(parsed.Blue, (0, 255), (255, 0));

        // Adobe's documented u16 bitmap (composite and green); missing channels stay identity.
        var legacy = new PsdBuilder.Writer();
        legacy.U16(1);
        legacy.U16(0x0005);
        Curve(legacy, "0,0 128,175 255,255");
        Curve(legacy, "0,3 255,252");
        parsed = Adjustments.ReadAcv(legacy.ToArray());
        AssertCurve(parsed.Rgb, (0, 0), (128, 175), (255, 255));
        AssertCurve(parsed.Green, (0, 3), (255, 252));
        AssertCurve(parsed.Red, (0, 0), (255, 255));

        // Channel 18 in the u32 bitmap is validated and ignored.
        var high = new PsdBuilder.Writer();
        high.U16(1);
        high.U32(0x00040001);
        Curve(high, "0,7 255,248");
        Curve(high, "0,30 255,220");
        AssertCurve(Adjustments.ReadAcv(high.ToArray()).Rgb, (0, 7), (255, 248));

        // A zero bitmap is valid when every curve lives in the extension.
        var extensionOnly = new PsdBuilder.Writer();
        extensionOnly.U16(1);
        extensionOnly.U32(0);
        extensionOnly.Ascii("Crv ");
        extensionOnly.U16(4);
        extensionOnly.U32(1);
        extensionOnly.U16(0);
        Curve(extensionOnly, "0,8 255,247");
        AssertCurve(Adjustments.ReadAcv(extensionOnly.ToArray()).Rgb, (0, 8), (255, 247));

        // Photoshop's identity body: zero bitmap, empty extension, padding.
        byte[] identity = [0x00, 0x01, 0x00, 0x00, 0x00, 0x00, (byte)'C', (byte)'r', (byte)'v', (byte)' ', 0x00, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
        Assert.Equal(CurvesSettings.Identity.Rgb, Adjustments.ReadAcv(identity).Rgb);
    }

    [Fact]
    public void Acv_rejects_malformed_bodies()
    {
        Assert.Throws<PsdFormatException>(() => Adjustments.ReadAcv([]));
        Assert.Throws<PsdFormatException>(() => Adjustments.ReadAcv([0, 3]));
        Assert.Throws<PsdFormatException>(() => Adjustments.ReadAcv([0, 4, 0, 0]));
        Assert.Throws<PsdFormatException>(() => Adjustments.ReadAcv([0, 4, 0, 20]));
        Assert.Throws<PsdFormatException>(() => Adjustments.ReadAcv([0, 4, 0, 1, 0, 1, 0, 0, 0, 0]));                 // one point
        Assert.Throws<PsdFormatException>(() => Adjustments.ReadAcv([0, 4, 0, 1, 0, 2, 0, 0, 0, 0, 1, 0, 0, 255]));   // output 256
        Assert.Throws<PsdFormatException>(() => Adjustments.ReadAcv([0, 4, 0, 1, 0, 2, 0, 0, 0, 64, 0, 255, 0, 64])); // repeated input
        Assert.Throws<PsdFormatException>(() => Adjustments.ReadAcv([0, 4, 0, 1, 0, 2, 0, 0, 0, 0, 0, 255, 0, 255, 1])); // trailing data
        Assert.Throws<PsdFormatException>(() => Adjustments.ReadAcv(new byte[4097]));
        Assert.Null(Adjustments.ParseCurves([1, 0, 4]));
    }

    // ---- Levels, Posterize, Threshold -----------------------------------------------

    [Fact]
    public void Levels_apply_the_channel_record_before_the_composite()
    {
        // Photoshop's render of psd-tools' levels_rgb.psd; the reverse order gives 64 and 194.
        var first = new LevelsSettings(new LevelsRecord(34, 255, 116, 0, 203), LevelsRecord.Identity, new LevelsRecord(0, 222, 63, 55, 255), LevelsRecord.Identity);
        Assert.Equal(52, Adjustments.BuildLevelsLut(first).Green[59]);
        var second = new LevelsSettings(new LevelsRecord(0, 213, 122, 48, 255), new LevelsRecord(0, 255, 47, 0, 194), LevelsRecord.Identity, LevelsRecord.Identity);
        Assert.Equal(199, Adjustments.BuildLevelsLut(second).Red[222]);

        var redOnly = LevelsSettings.Identity with { Red = new LevelsRecord(BlackOutput: 255) };
        var lut = Adjustments.BuildLevelsLut(redOnly);
        Assert.Equal((255, 200, 0), (lut.Red[0], lut.Green[200], lut.Blue[0]));
    }

    [Fact]
    public void Posterize_and_threshold_follow_photoshop()
    {
        Assert.Equal(0, Adjustments.PosterizeValue(127, 2));
        Assert.Equal(255, Adjustments.PosterizeValue(128, 2));
        Assert.Equal(0, Adjustments.PosterizeValue(63, 4));
        Assert.Equal(85, Adjustments.PosterizeValue(64, 4));
        Assert.Equal(170, Adjustments.PosterizeValue(128, 4));
        Assert.Equal(0, Adjustments.PosterizeValue(85, 3));
        Assert.Equal(127, Adjustments.PosterizeValue(86, 3));
        Assert.Equal(255, Adjustments.PosterizeValue(171, 3));
        Assert.Equal(42, Adjustments.PosterizeValue(40, 7));
        Assert.Equal(212, Adjustments.PosterizeValue(200, 7));
        Assert.Equal(76, Adjustments.ThresholdLuminance(255, 0, 0));
        Assert.Equal(150, Adjustments.ThresholdLuminance(0, 255, 0));
    }

    // ---- Exposure ---------------------------------------------------------------------

    [Theory]
    [InlineData(-182, 1203, 100, 72, 54, 49, 104, 101, 100)]
    [InlineData(125, 0, 152, 229, 211, 221, 255, 255, 255)]
    [InlineData(-4, 4144, 44, 162, 177, 193, 195, 216, 240)]
    [InlineData(203, 775, 152, 79, 88, 107, 192, 204, 227)]
    public void Exposure_matches_photoshop_probes(int exposure, int offset, int gamma, int r, int g, int b, int er, int eg, int eb)
    {
        // Photoshop's render of psd-tools' exposure_rgb.psd, within 1/255.
        var settings = new ExposureSettings(exposure, offset, gamma);
        Assert.InRange(Adjustments.ExposureValue(r, settings), er - 1, er + 1);
        Assert.InRange(Adjustments.ExposureValue(g, settings), eg - 1, eg + 1);
        Assert.InRange(Adjustments.ExposureValue(b, settings), eb - 1, eb + 1);
    }

    [Fact]
    public void Exposure_parses_photoshop_floats_and_identity_is_exact()
    {
        var identity = new ExposureSettings(0, 0, 100);
        for (var v = 0; v < 256; v++)
        {
            Assert.Equal(v, Adjustments.ExposureValue(v, identity));
        }

        var payload = new PsdBuilder.Writer();
        payload.U16(1);
        payload.U32(BitConverter.SingleToUInt32Bits(2.03f));
        payload.U32(BitConverter.SingleToUInt32Bits(0.0775f));
        payload.U32(BitConverter.SingleToUInt32Bits(1.52f));
        payload.U16(0);
        Assert.Equal(new ExposureSettings(203, 775, 152), Adjustments.ParseExposure(payload.ToArray()));
        Assert.Null(Adjustments.ParseExposure(new byte[13]));
    }

    // ---- Brightness/Contrast ----------------------------------------------------------

    [Fact]
    public void Legacy_brightness_contrast_matches_photoshop_ramps()
    {
        Assert.Equal(30, Adjustments.BrightnessContrastValue(0, 30, 0, true));
        Assert.Equal(255, Adjustments.BrightnessContrastValue(250, 30, 0, true));
        Assert.Equal(1, Adjustments.BrightnessContrastValue(64, 0, 50, true));
        Assert.Equal(3, Adjustments.BrightnessContrastValue(65, 0, 50, true));
        Assert.Equal(11, Adjustments.BrightnessContrastValue(69, 0, 50, true));
        Assert.Equal(0, Adjustments.BrightnessContrastValue(126, 0, 100, true));
        Assert.Equal(255, Adjustments.BrightnessContrastValue(127, 0, 100, true));
        Assert.Equal(0, Adjustments.BrightnessContrastValue(176, -50, 100, true));
        Assert.Equal(255, Adjustments.BrightnessContrastValue(177, -50, 100, true));
        Assert.Equal(12, Adjustments.BrightnessContrastValue(1, -40, -40, true));
    }

    [Fact]
    public void Modern_brightness_contrast_matches_photoshop_captures()
    {
        // (63, -12) is byte-exact over the whole ramp.
        for (var v = 0; v < 256; v++)
        {
            Assert.Equal(Capture63Neg12[v], Adjustments.BrightnessContrastValue(v, 63, -12, false));
        }

        // The spot grid: every sample within 1, all but the b = -110 rounding ties exact.
        var exact = 0;
        for (var i = 0; i < CaptureSamples.Length; i += 4)
        {
            var actual = Adjustments.BrightnessContrastValue(CaptureSamples[i + 2], CaptureSamples[i], CaptureSamples[i + 1], false);
            Assert.InRange(actual, CaptureSamples[i + 3] - 1, CaptureSamples[i + 3] + 1);
            exact += actual == CaptureSamples[i + 3] ? 1 : 0;
        }

        Assert.True(exact >= (CaptureSamples.Length / 4) - 8, $"{exact} exact");
    }

    [Fact]
    public void Brightness_contrast_descriptor_wins_over_the_compatibility_brit()
    {
        var descriptor = new PsdBuilder.Writer();
        descriptor.U32(16);
        descriptor.UnicodeString(string.Empty);
        descriptor.DescriptorId("null");
        descriptor.U32(3);
        descriptor.DescriptorId("Brgh");
        descriptor.Ascii("long");
        descriptor.I32(-200);
        descriptor.DescriptorId("Cntr");
        descriptor.Ascii("long");
        descriptor.I32(60);
        descriptor.DescriptorId("useLegacy");
        descriptor.Ascii("bool");
        descriptor.U8(0);

        // Modern ranges clamp brightness to -150..150.
        Assert.Equal(new BrightnessContrastSettings(-150, 60, false), Adjustments.ParseBrightnessContrastDescriptor(descriptor.ToArray()));

        var image = RenderOverGray(100, ("brit", [0, 0, 0, 0, 0, 0, 0, 0]), ("CgEd", descriptor.ToArray()));
        Assert.Equal(Adjustments.BrightnessContrastValue(100, -150, 60, false), image.GetPixel(0, 0).R);

        // brit alone is the legacy record.
        image = RenderOverGray(100, ("brit", [0x00, 0x1E, 0xFF, 0xEC, 0x00, 0x7F, 0x00, 0x00]));
        Assert.Equal(Adjustments.BrightnessContrastValue(100, 30, -20, true), image.GetPixel(0, 0).R);
    }

    // ---- Hue/Saturation ---------------------------------------------------------------

    [Fact]
    public void Colorize_matches_photoshop_reference()
    {
        // Stale master values are ignored while colorize is on.
        var settings = new HueSaturationSettings { Colorize = true, ColorizeHue = 203, ColorizeSaturation = 52, Hue = -180, Saturation = -61 };
        AssertHue(settings, (0, 0, 0), (0, 0, 0));
        AssertHue(settings, (64, 64, 64), (31, 71, 97));
        AssertHue(settings, (120, 120, 120), (58, 132, 182));
        AssertHue(settings, (128, 128, 128), (62, 141, 194));
        AssertHue(settings, (200, 200, 200), (172, 206, 229));
        AssertHue(settings, (255, 255, 255), (255, 255, 255));
        AssertHue(settings, (220, 140, 60), (81, 152, 200));
        AssertHue(settings with { ColorizeHue = 204, ColorizeLightness = 40 }, (100, 100, 100), (114, 170, 210));
        AssertHue(settings with { ColorizeHue = 204, ColorizeLightness = -40 }, (100, 100, 100), (29, 65, 91));
        AssertHue(settings with { ColorizeHue = 0, ColorizeSaturation = 25 }, (128, 128, 128), (160, 97, 97));
        AssertHue(settings with { ColorizeHue = 120, ColorizeSaturation = 100 }, (128, 128, 128), (1, 255, 1));
        AssertHue(settings with { ColorizeHue = 300 }, (90, 90, 90), (137, 44, 137));
    }

    [Fact]
    public void Master_sliders_match_photoshop_reference()
    {
        var banner = Master(-12, 14, -14);
        AssertHue(banner, (128, 128, 128), (110, 110, 110));
        AssertHue(banner, (60, 60, 60), (52, 52, 52));
        AssertHue(banner, (200, 50, 50), (183, 33, 63));
        AssertHue(banner, (255, 0, 0), (220, 0, 44));
        AssertHue(banner, (180, 90, 20), (166, 45, 6));
        AssertHue(banner, (200, 85, 50), (183, 38, 33));
        AssertHue(banner, (200, 173, 50), (183, 125, 33));
        AssertHue(banner, (50, 200, 85), (33, 183, 38));
        AssertHue(banner, (50, 200, 173), (33, 183, 125));
        AssertHue(Master(45, 0, 0), (200, 50, 50), (200, 162, 50));
        AssertHue(Master(-90, 0, 0), (200, 50, 50), (125, 50, 200));
        AssertHue(Master(0, -100, 0), (200, 50, 50), (125, 125, 125));
        AssertHue(Master(0, -100, 0), (255, 0, 0), (127, 127, 127));
        AssertHue(Master(0, 0, 40), (200, 50, 50), (222, 132, 132));
        AssertHue(Master(0, 0, 40), (64, 64, 64), (140, 140, 140));
        AssertHue(Master(0, 0, -40), (200, 50, 50), (120, 30, 30));
        AssertHue(Master(0, 0, -40), (200, 200, 200), (120, 120, 120));
        AssertHue(Master(30, -40, 20), (200, 85, 50), (187, 168, 115));
        AssertHue(Master(30, -40, 20), (200, 138, 50), (181, 187, 115));
        AssertHue(Master(30, -40, 20), (200, 173, 50), (164, 187, 115));
        AssertHue(Master(30, -40, 20), (85, 200, 50), (115, 187, 134));

        // Photoshop renders (65, 159, 181); the calibration envelope is 2/255.
        var (r, g, b) = Adjustments.ApplyHueSaturation(86, 155, 200, banner);
        Assert.InRange(r, 63, 67);
        Assert.InRange(g, 157, 161);
        Assert.InRange(b, 179, 183);

        AssertHue(Master(0, 0, 100), (40, 90, 200), (255, 255, 255));
        AssertHue(Master(0, 0, -100), (40, 90, 200), (0, 0, 0));
    }

    [Fact]
    public void Zero_master_is_an_exact_identity_and_neutrals_stay_neutral()
    {
        var zero = new HueSaturationSettings();
        for (var red = 0; red < 256; red += 3)
        {
            for (var green = 0; green < 256; green += 5)
            {
                for (var blue = 0; blue < 256; blue += 7)
                {
                    Assert.Equal((red, green, blue), Adjustments.ApplyHueSaturation(red, green, blue, zero));
                }
            }
        }

        foreach (var (r, g, b) in new[] { (7, 3, 0), (255, 128, 0), (255, 200, 2), (1, 0, 0), (255, 254, 254) })
        {
            Assert.Equal((r, g, b), Adjustments.ApplyHueSaturation(r, g, b, zero));
        }

        foreach (var hue in new[] { -180, -12, 0, 45, 180 })
        {
            foreach (var saturation in new[] { -100, -14, 0, 14, 100 })
            {
                var settings = Master(hue, saturation, 0);
                for (var v = 0; v < 256; v++)
                {
                    Assert.Equal((v, v, v), Adjustments.ApplyHueSaturation(v, v, v, settings));
                }
            }
        }
    }

    [Fact]
    public void Bands_select_by_original_hue_and_compose_with_the_master()
    {
        var defaults = Adjustments.DefaultHueSaturationBands();
        var reds = new HueSaturationSettings { Bands = Band(0, defaults[0] with { Hue = 60 }) };
        var rotated = Adjustments.ApplyHueSaturation(255, 0, 0, reds);
        Assert.Equal(255, rotated.R);
        Assert.True(rotated.G > 200);
        Assert.Equal(0, rotated.B);
        foreach (var (r, g, b) in new[] { (0, 255, 0), (0, 255, 255), (128, 128, 128), (0, 0, 255) })
        {
            Assert.Equal((r, g, b), Adjustments.ApplyHueSaturation(r, g, b, reds));
        }

        Assert.Equal((150, 150, 150), Adjustments.ApplyHueSaturation(150, 0, 0, new HueSaturationSettings { Bands = Band(0, defaults[0] with { Lightness = 100 }) }));
        Assert.Equal((20, 20, 20), Adjustments.ApplyHueSaturation(150, 20, 20, new HueSaturationSettings { Bands = Band(0, defaults[0] with { Lightness = -100 }) }));

        Assert.Equal(1.0, Adjustments.HueSaturationBandWeight(0, defaults[0]));
        Assert.Equal(0.5, Adjustments.HueSaturationBandWeight(330, defaults[0]));
        Assert.Equal(0.0, Adjustments.HueSaturationBandWeight(90, defaults[0]));
    }

    [Fact]
    public void Legacy_hue_key_uses_the_hue2_layout()
    {
        var payload = Hue2Payload(colorize: false, 0, 25, 0, 0, -100, 0);
        var image = RenderOverColor(new PsdColor(200, 50, 50), ("hue ", payload));
        Assert.Equal(new PsdColor(125, 125, 125), image.GetPixel(1, 1));

        image = RenderOverColor(new PsdColor(200, 50, 50), ("hue2", Hue2Payload(colorize: false, 0, 25, 0, 45, 0, 0)));
        Assert.Equal(new PsdColor(200, 162, 50), image.GetPixel(0, 0));

        // A short legacy header (no band records) keeps the default hextants.
        Assert.NotNull(Adjustments.ParseHueSaturation(payload.AsSpan(0, 16)));
        Assert.Null(Adjustments.ParseHueSaturation(payload.AsSpan(0, 15)));
    }

    // ---- Through the compositor --------------------------------------------------------

    [Fact]
    public void Levels_layer_renders_through_the_compositor()
    {
        var image = RenderOverColor(new PsdColor(0, 200, 0), ("levl", LevlPayload(LevelsRecord.Identity, new LevelsRecord(BlackOutput: 255))));
        Assert.Equal(new PsdColor(255, 200, 0), image.GetPixel(0, 0));
    }

    [Fact]
    public void Grayscale_documents_copy_the_gray_record_to_every_channel()
    {
        // A one-plane document keeps its record in the slot RGB calls red.
        var builder = new PsdBuilder { Width = 2, Height = 1, Mode = PsdColorMode.Grayscale };
        var gray = new BuilderLayer { Name = "gray", Rect = new PsdRect(0, 0, 2, 1) };
        gray.Channels[-1] = PsdBuilder.Plane8(2, 1, 255);
        gray.Channels[0] = PsdBuilder.Plane8(2, 1, 100);
        builder.Layers.Add(gray);
        var levels = new BuilderLayer { Name = "Levels", Rect = default };
        levels.Blocks.Add(("levl", LevlPayload(LevelsRecord.Identity, new LevelsRecord(BlackOutput: 255))));
        builder.Layers.Add(levels);
        builder.MergedChannels.Add(gray.Channels[0]);

        var image = PsdDocument.Load(builder.Build()).Render(new RenderOptions { Source = RenderSource.Layers });
        Assert.Equal(new PsdColor(255, 255, 255), image.GetPixel(0, 0));
    }

    [Fact]
    public void Exposure_and_curves_layers_render_through_the_compositor()
    {
        var exposure = new PsdBuilder.Writer();
        exposure.U16(1);
        exposure.U32(BitConverter.SingleToUInt32Bits(1f));
        exposure.U32(0);
        exposure.U32(BitConverter.SingleToUInt32Bits(1f));
        exposure.U16(0);
        var image = RenderOverGray(100, ("expA", exposure.ToArray()));
        Assert.Equal(Adjustments.ExposureValue(100, new ExposureSettings(100, 0, 100)), image.GetPixel(0, 0).R);

        var curv = new PsdBuilder.Writer();
        curv.U8(0);
        curv.U16(4);
        curv.U16(1);
        Curve(curv, "0,255 255,0");
        image = RenderOverGray(100, ("curv", curv.ToArray()));
        Assert.Equal(155, image.GetPixel(0, 0).R);
    }

    [Fact]
    public void Malformed_payloads_leave_the_layer_unrendered()
    {
        foreach (var (key, data) in new (string, byte[])[]
        {
            ("levl", [0, 2, 0, 0]),
            ("curv", [0, 0, 9]),
            ("hue2", [0, 3]),
            ("brit", [0, 1]),
            ("expA", [0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]),
        })
        {
            var image = RenderOverGray(100, (key, data));
            Assert.Equal(new PsdColor(100, 100, 100), image.GetPixel(0, 0));
        }

        // A damaged CgEd descriptor falls back to the legacy brit record.
        var fallback = RenderOverGray(100, ("brit", [0x00, 0x1E, 0xFF, 0xEC, 0x00, 0x7F, 0x00, 0x00]), ("CgEd", [0, 0, 0, 16, 1]));
        Assert.Equal(Adjustments.BrightnessContrastValue(100, 30, -20, true), fallback.GetPixel(0, 0).R);
    }

    // ---- Helpers -----------------------------------------------------------------------

    private static TaggedBlock BlockOf(string fixture, string layer, string key) =>
        Fixtures.Load(fixture).Layers.Single(l => l.Name == layer).GetTaggedBlock(key)!;

    private static HueSaturationSettings Master(int hue, int saturation, int lightness) =>
        new() { Hue = hue, Saturation = saturation, Lightness = lightness, ColorizeHue = 203, ColorizeSaturation = 52 };

    private static HueSaturationBand[] Band(int index, HueSaturationBand band)
    {
        var bands = Adjustments.DefaultHueSaturationBands();
        bands[index] = band;
        return bands;
    }

    private static void AssertHue(HueSaturationSettings settings, (int R, int G, int B) input, (int R, int G, int B) expected) =>
        Assert.Equal(expected, Adjustments.ApplyHueSaturation(input.R, input.G, input.B, settings));

    private static CurvePoint[] Points(string text) =>
        [.. text.Split(' ').Select(p => p.Split(',')).Select(p => new CurvePoint(int.Parse(p[0], System.Globalization.CultureInfo.InvariantCulture), int.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture)))];

    private static void AssertCurve(CurvePoint[] actual, params (int Input, int Output)[] expected) =>
        Assert.Equal(expected.Select(p => new CurvePoint(p.Input, p.Output)), actual);

    private static void Curve(PsdBuilder.Writer writer, string points)
    {
        var parsed = Points(points);
        writer.U16((ushort)parsed.Length);
        foreach (var point in parsed)
        {
            writer.U16((ushort)point.Output);
            writer.U16((ushort)point.Input);
        }
    }

    private static ulong Fnv(byte[] lut)
    {
        var hash = 1469598103934665603UL;
        foreach (var value in lut)
        {
            hash ^= value;
            hash *= 1099511628211UL;
        }

        return hash;
    }

    private static byte[] LevlPayload(LevelsRecord master, LevelsRecord red)
    {
        var writer = new PsdBuilder.Writer();
        writer.U16(2);
        for (var i = 0; i < 29; i++)
        {
            var record = i switch { 0 => master, 1 => red, _ => LevelsRecord.Identity };
            writer.U16((ushort)record.BlackInput);
            writer.U16((ushort)record.WhiteInput);
            writer.U16((ushort)record.BlackOutput);
            writer.U16((ushort)record.WhiteOutput);
            writer.U16((ushort)record.GammaPercent);
        }

        return writer.ToArray();
    }

    private static byte[] Hue2Payload(bool colorize, int colorizeHue, int colorizeSaturation, int colorizeLightness, int hue, int saturation, int lightness)
    {
        var writer = new PsdBuilder.Writer();
        writer.U16(2);
        writer.U8((byte)(colorize ? 1 : 0));
        writer.U8(0);
        foreach (var value in new[] { colorizeHue, colorizeSaturation, colorizeLightness, hue, saturation, lightness })
        {
            writer.I16((short)value);
        }

        foreach (var band in Adjustments.DefaultHueSaturationBands())
        {
            foreach (var value in new[] { band.OuterStart, band.InnerStart, band.InnerEnd, band.OuterEnd, 0, 0, 0 })
            {
                writer.I16((short)value);
            }
        }

        return writer.ToArray();
    }

    private static RgbaImage RenderOverGray(byte value, params (string Key, byte[] Data)[] blocks) =>
        RenderOverColor(new PsdColor(value, value, value), blocks);

    private static RgbaImage RenderOverColor(PsdColor color, params (string Key, byte[] Data)[] blocks)
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

    private static readonly byte[] Capture63Neg12 =
    [
        0, 2, 3, 5, 6, 8, 10, 11, 13, 14, 16, 18, 19, 21, 22, 24,
        26, 27, 29, 30, 32, 33, 35, 36, 38, 40, 41, 43, 44, 46, 47, 49,
        50, 52, 53, 55, 56, 58, 59, 61, 62, 64, 65, 67, 68, 70, 71, 73,
        74, 76, 77, 79, 80, 82, 83, 84, 86, 87, 89, 90, 92, 93, 95, 96,
        97, 99, 100, 102, 103, 104, 106, 107, 109, 110, 111, 113, 114, 116, 117, 118,
        120, 121, 122, 124, 125, 127, 128, 129, 131, 132, 133, 135, 136, 137, 138, 140,
        141, 142, 144, 145, 146, 147, 149, 150, 151, 152, 154, 155, 156, 157, 158, 160,
        161, 162, 163, 164, 165, 167, 168, 169, 170, 171, 172, 173, 174, 176, 177, 178,
        179, 180, 181, 182, 183, 184, 185, 186, 187, 188, 189, 190, 191, 192, 193, 194,
        195, 196, 197, 198, 199, 200, 200, 201, 202, 203, 204, 205, 206, 207, 207, 208,
        209, 210, 211, 212, 212, 213, 214, 215, 216, 216, 217, 218, 219, 219, 220, 221,
        221, 222, 223, 224, 224, 225, 226, 226, 227, 227, 228, 229, 229, 230, 231, 231,
        232, 232, 233, 234, 234, 235, 235, 236, 236, 237, 237, 238, 238, 239, 239, 240,
        240, 241, 241, 242, 242, 243, 243, 243, 244, 244, 245, 245, 245, 246, 246, 247,
        247, 247, 248, 248, 248, 249, 249, 249, 250, 250, 250, 251, 251, 251, 251, 252,
        252, 252, 252, 253, 253, 253, 253, 254, 254, 254, 254, 254, 254, 255, 255, 255,
    ];

    /// <summary>Photoshop 2026 modern-mode captures: brightness, contrast, input, expected.</summary>
    private static readonly int[] CaptureSamples =
    [
        150, 0, 0, 0, 150, 0, 1, 3, 150, 0, 16, 41, 150, 0, 33, 85,
        150, 0, 64, 162, 150, 0, 96, 214, 150, 0, 127, 238, 150, 0, 128, 239,
        150, 0, 160, 249, 150, 0, 192, 253, 150, 0, 224, 254, 150, 0, 240, 255,
        150, 0, 254, 255, 150, 0, 255, 255, 125, 0, 0, 0, 125, 0, 1, 2,
        125, 0, 16, 35, 125, 0, 33, 73, 125, 0, 64, 141, 125, 0, 96, 198,
        125, 0, 127, 229, 125, 0, 128, 230, 125, 0, 160, 245, 125, 0, 192, 251,
        125, 0, 224, 254, 125, 0, 240, 254, 125, 0, 254, 255, 125, 0, 255, 255,
        110, 0, 0, 0, 110, 0, 1, 2, 110, 0, 16, 32, 110, 0, 33, 66,
        110, 0, 64, 128, 110, 0, 96, 185, 110, 0, 127, 220, 110, 0, 128, 221,
        110, 0, 160, 240, 110, 0, 192, 249, 110, 0, 224, 253, 110, 0, 240, 254,
        110, 0, 254, 255, 110, 0, 255, 255, 101, 0, 0, 0, 101, 0, 1, 2,
        101, 0, 16, 30, 101, 0, 33, 62, 101, 0, 64, 121, 101, 0, 96, 174,
        101, 0, 127, 210, 101, 0, 128, 211, 101, 0, 160, 233, 101, 0, 192, 246,
        101, 0, 224, 252, 101, 0, 240, 254, 101, 0, 254, 255, 101, 0, 255, 255,
        100, 0, 0, 0, 100, 0, 1, 2, 100, 0, 16, 30, 100, 0, 33, 62,
        100, 0, 64, 120, 100, 0, 96, 173, 100, 0, 127, 208, 100, 0, 128, 209,
        100, 0, 160, 232, 100, 0, 192, 245, 100, 0, 224, 252, 100, 0, 240, 253,
        100, 0, 254, 255, 100, 0, 255, 255, 89, 0, 0, 0, 89, 0, 1, 2,
        89, 0, 16, 28, 89, 0, 33, 58, 89, 0, 64, 112, 89, 0, 96, 164,
        89, 0, 127, 200, 89, 0, 128, 201, 89, 0, 160, 227, 89, 0, 192, 242,
        89, 0, 224, 251, 89, 0, 240, 253, 89, 0, 254, 255, 89, 0, 255, 255,
        63, 0, 0, 0, 63, 0, 1, 1, 63, 0, 16, 24, 63, 0, 33, 49,
        63, 0, 64, 95, 63, 0, 96, 142, 63, 0, 127, 181, 63, 0, 128, 182,
        63, 0, 160, 212, 63, 0, 192, 233, 63, 0, 224, 248, 63, 0, 240, 252,
        63, 0, 254, 255, 63, 0, 255, 255, 25, 0, 0, 0, 25, 0, 1, 1,
        25, 0, 16, 19, 25, 0, 33, 39, 25, 0, 64, 75, 25, 0, 96, 112,
        25, 0, 127, 148, 25, 0, 128, 150, 25, 0, 160, 185, 25, 0, 192, 216,
        25, 0, 224, 240, 25, 0, 240, 249, 25, 0, 254, 255, 25, 0, 255, 255,
        1, 0, 0, 0, 1, 0, 1, 1, 1, 0, 16, 16, 1, 0, 33, 33,
        1, 0, 64, 64, 1, 0, 96, 97, 1, 0, 127, 128, 1, 0, 128, 129,
        1, 0, 160, 161, 1, 0, 192, 194, 1, 0, 224, 225, 1, 0, 240, 241,
        1, 0, 254, 254, 1, 0, 255, 255, -1, 0, 0, 0, -1, 0, 1, 1,
        -1, 0, 16, 16, -1, 0, 33, 33, -1, 0, 64, 64, -1, 0, 96, 95,
        -1, 0, 127, 126, -1, 0, 128, 127, -1, 0, 160, 159, -1, 0, 192, 190,
        -1, 0, 224, 223, -1, 0, 240, 239, -1, 0, 254, 254, -1, 0, 255, 255,
        -25, 0, 0, 0, -25, 0, 1, 1, -25, 0, 16, 14, -25, 0, 33, 28,
        -25, 0, 64, 55, -25, 0, 96, 82, -25, 0, 127, 108, -25, 0, 128, 109,
        -25, 0, 160, 137, -25, 0, 192, 167, -25, 0, 224, 202, -25, 0, 240, 224,
        -25, 0, 254, 252, -25, 0, 255, 255, -63, 0, 0, 0, -63, 0, 1, 1,
        -63, 0, 16, 11, -63, 0, 33, 22, -63, 0, 64, 43, -63, 0, 96, 65,
        -63, 0, 127, 85, -63, 0, 128, 86, -63, 0, 160, 109, -63, 0, 192, 138,
        -63, 0, 224, 177, -63, 0, 240, 205, -63, 0, 254, 249, -63, 0, 255, 255,
        -100, 0, 0, 0, -100, 0, 1, 1, -100, 0, 16, 9, -100, 0, 33, 18,
        -100, 0, 64, 34, -100, 0, 96, 51, -100, 0, 127, 68, -100, 0, 128, 68,
        -100, 0, 160, 87, -100, 0, 192, 111, -100, 0, 224, 147, -100, 0, 240, 177,
        -100, 0, 254, 245, -100, 0, 255, 255, -110, 0, 0, 0, -110, 0, 1, 1,
        -110, 0, 16, 8, -110, 0, 33, 17, -110, 0, 64, 32, -110, 0, 96, 48,
        -110, 0, 127, 64, -110, 0, 128, 64, -110, 0, 160, 81, -110, 0, 192, 101,
        -110, 0, 224, 132, -110, 0, 240, 160, -110, 0, 254, 238, -110, 0, 255, 255,
        -125, 0, 0, 0, -125, 0, 1, 0, -125, 0, 16, 7, -125, 0, 33, 15,
        -125, 0, 64, 29, -125, 0, 96, 44, -125, 0, 127, 58, -125, 0, 128, 58,
        -125, 0, 160, 73, -125, 0, 192, 92, -125, 0, 224, 120, -125, 0, 240, 147,
        -125, 0, 254, 228, -125, 0, 255, 255, -150, 0, 0, 0, -150, 0, 1, 0,
        -150, 0, 16, 6, -150, 0, 33, 13, -150, 0, 64, 25, -150, 0, 96, 37,
        -150, 0, 127, 49, -150, 0, 128, 50, -150, 0, 160, 63, -150, 0, 192, 79,
        -150, 0, 224, 106, -150, 0, 240, 131, -150, 0, 254, 214, -150, 0, 255, 255,
        0, 100, 0, 0, 0, 100, 1, 0, 0, 100, 16, 5, 0, 100, 33, 14,
        0, 100, 64, 40, 0, 100, 96, 78, 0, 100, 127, 127, 0, 100, 128, 128,
        0, 100, 160, 178, 0, 100, 192, 216, 0, 100, 224, 242, 0, 100, 240, 250,
        0, 100, 254, 255, 0, 100, 255, 255, 0, 75, 0, 0, 0, 75, 1, 0,
        0, 75, 16, 8, 0, 75, 33, 19, 0, 75, 64, 46, 0, 75, 96, 82,
        0, 75, 127, 127, 0, 75, 128, 128, 0, 75, 160, 174, 0, 75, 192, 210,
        0, 75, 224, 237, 0, 75, 240, 248, 0, 75, 254, 255, 0, 75, 255, 255,
        0, 50, 0, 0, 0, 50, 1, 1, 0, 50, 16, 11, 0, 50, 33, 24,
        0, 50, 64, 52, 0, 50, 96, 87, 0, 50, 127, 127, 0, 50, 128, 128,
        0, 50, 160, 169, 0, 50, 192, 204, 0, 50, 224, 233, 0, 50, 240, 245,
        0, 50, 254, 254, 0, 50, 255, 255, 0, 25, 0, 0, 0, 25, 1, 1,
        0, 25, 16, 13, 0, 25, 33, 28, 0, 25, 64, 58, 0, 25, 96, 91,
        0, 25, 127, 127, 0, 25, 128, 128, 0, 25, 160, 165, 0, 25, 192, 198,
        0, 25, 224, 228, 0, 25, 240, 243, 0, 25, 254, 254, 0, 25, 255, 255,
        0, -12, 0, 0, 0, -12, 1, 1, 0, -12, 16, 17, 0, -12, 33, 35,
        0, -12, 64, 67, 0, -12, 96, 98, 0, -12, 127, 127, 0, -12, 128, 128,
        0, -12, 160, 158, 0, -12, 192, 189, 0, -12, 224, 222, 0, -12, 240, 239,
        0, -12, 254, 254, 0, -12, 255, 255, 0, -50, 0, 0, 0, -50, 1, 1,
        0, -50, 16, 21, 0, -50, 33, 42, 0, -50, 64, 76, 0, -50, 96, 105,
        0, -50, 127, 127, 0, -50, 128, 128, 0, -50, 160, 151, 0, -50, 192, 180,
        0, -50, 224, 215, 0, -50, 240, 235, 0, -50, 254, 254, 0, -50, 255, 255,
        63, 50, 0, 0, 63, 50, 1, 1, 63, 50, 16, 16, 63, 50, 33, 38,
        63, 50, 64, 86, 63, 50, 96, 147, 63, 50, 127, 192, 63, 50, 128, 193,
        63, 50, 160, 223, 63, 50, 192, 240, 63, 50, 224, 250, 63, 50, 240, 253,
        63, 50, 254, 255, 63, 50, 255, 255, 100, -50, 0, 0, 100, -50, 1, 3,
        100, -50, 16, 39, 100, -50, 33, 74, 100, -50, 64, 123, 100, -50, 96, 162,
        100, -50, 127, 197, 100, -50, 128, 198, 100, -50, 160, 225, 100, -50, 192, 242,
        100, -50, 224, 250, 100, -50, 240, 253, 100, -50, 254, 255, 100, -50, 255, 255,
        -100, 100, 0, 0, -100, 100, 1, 0, -100, 100, 16, 2, -100, 100, 33, 6,
        -100, 100, 64, 15, -100, 100, 96, 28, -100, 100, 127, 43, -100, 100, 128, 44,
        -100, 100, 160, 66, -100, 100, 192, 100, -100, 100, 224, 159, -100, 100, 240, 200,
        -100, 100, 254, 252, -100, 100, 255, 255, 50, 25, 0, 0, 50, 25, 1, 1,
        50, 25, 16, 18, 50, 25, 33, 40, 50, 25, 64, 83, 50, 25, 96, 132,
        50, 25, 127, 175, 50, 25, 128, 177, 50, 25, 160, 209, 50, 25, 192, 232,
        50, 25, 224, 247, 50, 25, 240, 252, 50, 25, 254, 255, 50, 25, 255, 255,
    ];
}
