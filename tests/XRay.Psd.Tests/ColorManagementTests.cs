using XRay.Psd.Imaging;
using XRay.Psd.Imaging.Icc;
using XRay.Psd.Rendering;
using XRay.Psd.Tests.Support;

namespace XRay.Psd.Tests;

/// <summary>
/// ICC color management: the profile parser, the device-to-sRGB transforms and
/// their wiring into pixel decoding. Expected sRGB values come from Little CMS
/// 2.19 (relative colorimetric with black point compensation, the reference's
/// settings) run on the same profile bytes, and from the reference's Photoshop
/// pins for the SWOP fixture (<c>.reference/docs/ps-compat.md</c>,
/// <c>pattern_styles_fixtures_tests.cpp</c>).
/// </summary>
public sealed class ColorManagementTests
{
    private const string CmykFixture = "photoshop-cmyk-style-colors.psd";

    private static byte[] SwopProfile() => Fixtures.Load(CmykFixture).GetImageResource(ImageResourceIds.IccProfile)!.Data.ToArray();

    private static IccSrgbTransform Transform(byte[] profile, int channels, IccTransformSettings? settings = null)
    {
        var parsed = IccProfile.Parse(profile);
        Assert.NotNull(parsed);
        var transform = IccSrgbTransform.Create(parsed, channels, settings ?? IccTransformSettings.Default);
        Assert.NotNull(transform);
        return transform;
    }

    /// <summary>Evaluates 8-bit device values (ICC convention: CMYK is ink) to rounded 8-bit sRGB.</summary>
    private static (int R, int G, int B) Convert(IccSrgbTransform transform, params int[] device)
    {
        var (r, g, b) = transform.Evaluate([.. device.Select(v => v / 255.0)]);
        return ((int)Math.Round(r * 255), (int)Math.Round(g * 255), (int)Math.Round(b * 255));
    }

    private static void AssertNear((int R, int G, int B) actual, int r, int g, int b, int tolerance = 1)
    {
        Assert.True(
            Math.Abs(actual.R - r) <= tolerance && Math.Abs(actual.G - g) <= tolerance && Math.Abs(actual.B - b) <= tolerance,
            $"expected ({r},{g},{b}) +-{tolerance}, got {actual}");
    }

    private static void AssertNear(PsdColor actual, int r, int g, int b, int tolerance = 1) => AssertNear((actual.R, actual.G, actual.B), r, g, b, tolerance);

    [Fact]
    public void Swop_profile_header_and_tags_parse()
    {
        var profile = IccProfile.Parse(SwopProfile());

        Assert.NotNull(profile);
        Assert.Equal("U.S. Web Coated (SWOP) v2", profile.Description);
        Assert.Equal(2, profile.MajorVersion);
        Assert.Equal(1, profile.MinorVersion);
        Assert.Equal(IccProfile.ClassOutput, profile.DeviceClass);
        Assert.Equal(IccProfile.SpaceCmyk, profile.ColorSpace);
        Assert.Equal(IccProfile.SpaceLab, profile.Pcs);
        Assert.Equal(4, profile.DeviceChannels);
        Assert.Equal("mft2", profile.GetAToB(0)!.Type);
        Assert.Equal("mft2", profile.GetAToB(1)!.Type);
        Assert.Equal("mft1", profile.GetBToA(0)!.Type);
        Assert.Equal(IccPcsEncoding.LabV2, profile.GetAToB(1)!.LabEncoding);
        Assert.Contains("gamt", profile.TagSignatures);
        Assert.NotNull(profile.MediaWhitePoint);
        Assert.False(profile.IsMatrixShaper);
    }

    [Fact]
    public void Matrix_trc_profile_parses_colorants_and_gamma()
    {
        var profile = IccProfile.Parse(TestProfiles.AdobeRgb())!;

        Assert.Equal("Test Adobe RGB (1998)", profile.Description);
        Assert.True(profile.IsMatrixShaper);
        Assert.Equal(TestProfiles.AdobeGreen[1], profile.GreenColorant!.Value.Y, 4);
        Assert.Equal(Math.Pow(0.5, 563 / 256.0), profile.RedTrc!.Evaluate(0.5), 6);
        Assert.Null(profile.GetAToB(0));
    }

    [Fact]
    public void V4_profile_reads_mluc_chad_and_mAB()
    {
        var profile = IccProfile.Parse(TestProfiles.AdobeRgbV4Lut())!;

        Assert.Equal(4, profile.MajorVersion);
        Assert.Equal("Test Adobe RGB v4 LUT", profile.Description);
        Assert.Equal([1, 0, 0, 0, 1, 0, 0, 0, 1], profile.ChromaticAdaptation!);
        Assert.Equal("mAB", profile.GetAToB(0)!.Type);
        Assert.False(profile.IsMatrixShaper);
    }

    [Fact]
    public void Parametric_curves_follow_the_icc_formulas()
    {
        // Type 4 with the sRGB parameters is the sRGB decoding curve.
        var srgb = IccCurve.FromParametric(4, [2.4, 1 / 1.055, 0.055 / 1.055, 1 / 12.92, 0.04045, 0, 0]);
        var gamma = IccCurve.FromParametric(0, [2.2]);
        var type1 = IccCurve.FromParametric(1, [2.0, 2.0, -1.0]);
        var type2 = IccCurve.FromParametric(2, [1.0, 1.0, 0.0, 0.25]);
        var type3 = IccCurve.FromParametric(3, [1.0, 1.0, 0.0, 0.5, 0.5]);
        foreach (var x in new[] { 0.0, 0.01, 0.04, 0.2, 0.5, 0.9, 1.0 })
        {
            Assert.Equal(IccPcs.DecodeSrgb(x), srgb.Evaluate(x), 9);
            Assert.Equal(Math.Pow(x, 2.2), gamma.Evaluate(x), 9);
            Assert.Equal(x >= 0.5 ? Math.Pow((2 * x) - 1, 2) : 0, type1.Evaluate(x), 9);
            Assert.Equal(x + 0.25, type2.Evaluate(x), 9);
            Assert.Equal(x >= 0.5 ? x : 0.5 * x, type3.Evaluate(x), 9);
        }

        var table = IccCurve.FromTable([0f, 0.25f, 1f]);
        Assert.Equal(0.125, table.Evaluate(0.25), 6);
        Assert.Equal(0.625, table.Evaluate(0.75), 6);
    }

    [Fact]
    public void Unusable_profiles_are_rejected_without_throwing()
    {
        Assert.Null(IccProfile.Parse([1, 2, 3, 4]));
        Assert.Null(IccProfile.Parse([]));
        var swop = SwopProfile();

        // A truncated profile loses its LUTs, so there is no CMYK conversion.
        var truncated = IccProfile.Parse(swop.AsSpan(0, 2000));
        Assert.True(truncated is null || IccSrgbTransform.Create(truncated, 4, IccTransformSettings.Default) is null);

        // Wrong color space for the document mode.
        Assert.Null(IccSrgbTransform.Create(IccProfile.Parse(TestProfiles.AdobeRgb())!, 4, IccTransformSettings.Default));
        Assert.Null(IccSrgbTransform.Create(IccProfile.Parse(swop)!, 3, IccTransformSettings.Default));

        // Random corruption anywhere in the header, tag table or tag data must degrade, never throw.
        var random = new Random(7);
        foreach (var source in new[] { swop, TestProfiles.AdobeRgb(), TestProfiles.AdobeRgbV4Lut(), TestProfiles.SyntheticCmyk(), TestProfiles.GrayLiftedBlack() })
        {
            for (var trial = 0; trial < 60; trial++)
            {
                var damaged = source.ToArray();
                for (var flips = 0; flips < 8; flips++)
                {
                    var at = trial % 2 == 0 ? random.Next(Math.Min(damaged.Length, 400)) : random.Next(damaged.Length);
                    damaged[at] = (byte)random.Next(256);
                }

                var profile = IccProfile.Parse(damaged);
                if (profile is not null)
                {
                    var transform = IccSrgbTransform.Create(profile, profile.DeviceChannels, IccTransformSettings.Default);
                    if (transform is not null && transform.Channels is 1 or 3 or 4)
                    {
                        _ = transform.Evaluate(new double[transform.Channels]);
                    }
                }
            }
        }
    }

    [Theory]
    [InlineData(255, 0, 0, 255, 0, 0)]
    [InlineData(0, 255, 0, 0, 255, 0)]
    [InlineData(0, 0, 255, 0, 0, 255)]
    [InlineData(128, 128, 128, 129, 129, 129)]
    [InlineData(64, 128, 192, 0, 129, 196)]
    [InlineData(10, 20, 30, 0, 12, 24)]
    [InlineData(0, 0, 0, 0, 0, 0)]
    [InlineData(255, 255, 255, 255, 255, 255)]
    public void Adobe_rgb_converts_like_lcms(int r, int g, int b, int er, int eg, int eb)
    {
        AssertNear(Convert(Transform(TestProfiles.AdobeRgb(), 3), r, g, b), er, eg, eb);
    }

    [Fact]
    public void Adobe_rgb_green_is_outside_the_srgb_gamut()
    {
        var transform = Transform(TestProfiles.AdobeRgb(), 3);
        var (red, green, blue) = transform.EvaluateLinear([0, 1, 0]);

        Assert.InRange(red, -0.42, -0.37);
        Assert.InRange(green, 0.99, 1.01);
        Assert.InRange(blue, -0.06, -0.03);

        // Adobe RGB shares sRGB's red primary, with a higher luminance: it clips to pure red.
        var (r, g, b) = transform.EvaluateLinear([1, 0, 0]);
        Assert.InRange(r, 1.3, 1.5);
        Assert.InRange(Math.Abs(g) + Math.Abs(b), 0, 0.002);
        Assert.False(transform.IsSrgbEquivalent);
    }

    [Fact]
    public void V4_matrix_shaper_lut_matches_the_matrix_trc_profile()
    {
        var lut = Transform(TestProfiles.AdobeRgbV4Lut(), 3);
        var matrix = Transform(TestProfiles.AdobeRgb(), 3);

        Assert.True(lut.UsesLut);
        Assert.True(lut.IsMatrixShaper);
        var random = new Random(3);
        for (var i = 0; i < 500; i++)
        {
            double[] device = [random.NextDouble(), random.NextDouble(), random.NextDouble()];
            var (r1, g1, b1) = lut.Evaluate(device);
            var (r2, g2, b2) = matrix.Evaluate(device);
            Assert.True(Math.Abs(r1 - r2) < 0.6 / 255 && Math.Abs(g1 - g2) < 0.6 / 255 && Math.Abs(b1 - b2) < 0.6 / 255);
        }
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(32, 26)]
    [InlineData(64, 62)]
    [InlineData(128, 129)]
    [InlineData(192, 193)]
    [InlineData(255, 255)]
    public void Gray_gamma_profile_converts_like_lcms(int gray, int expected)
    {
        var result = Convert(Transform(TestProfiles.GrayGamma22(), 1), gray);

        Assert.Equal(expected, result.R);
        Assert.Equal(result.R, result.G);
        Assert.Equal(result.R, result.B);
    }

    [Fact]
    public void Black_point_compensation_maps_a_lifted_gray_black_to_srgb_black()
    {
        var compensated = Transform(TestProfiles.GrayLiftedBlack(), 1);
        var plain = Transform(TestProfiles.GrayLiftedBlack(), 1, new IccTransformSettings(IccRenderingIntent.RelativeColorimetric, false));

        // lcms cmsDetectBlackPoint: (0.048214, 0.050004, 0.041248).
        Assert.Equal(0.050004, compensated.BlackPoint.Y, 4);
        Assert.Equal(0.048214, compensated.BlackPoint.X, 4);
        AssertNear(Convert(compensated, 0), 0, 0, 0, 0);
        AssertNear(Convert(compensated, 128), 129, 129, 129);
        AssertNear(Convert(plain, 0), 63, 63, 63);
        Assert.Equal(default, plain.BlackPoint);
    }

    [Fact]
    public void Cmyk_lut_profile_uses_the_perceptual_black_round_trip()
    {
        var transform = Transform(TestProfiles.SyntheticCmyk(), 4);

        // lcms: (0.016742, 0.017364, 0.014323), the relative Lab of B2A0's rich black.
        Assert.Equal(0.016742, transform.BlackPoint.X, 4);
        Assert.Equal(0.017364, transform.BlackPoint.Y, 4);
        Assert.Equal(0.014323, transform.BlackPoint.Z, 4);
        AssertNear(Convert(transform, 0, 0, 0, 0), 255, 255, 255);
        AssertNear(Convert(transform, 0, 0, 0, 255), 88, 88, 88);
        AssertNear(Convert(transform, 255, 255, 255, 255), 32, 0, 0);
        AssertNear(Convert(transform, 0, 255, 255, 0), 255, 143, 55);
        AssertNear(Convert(transform, 128, 128, 128, 128), 147, 114, 84);
        AssertNear(Convert(transform, 64, 32, 200, 10), 238, 220, 125);

        // Without B2A0 lcms finds no perceptual black and compensation is a no-op.
        var plain = Transform(TestProfiles.SyntheticCmyk(withPerceptualBackward: false), 4);
        Assert.Equal(default, plain.BlackPoint);
        AssertNear(Convert(plain, 0, 0, 0, 255), 94, 94, 94);
        AssertNear(Convert(plain, 255, 255, 255, 255), 50, 0, 0);
    }

    [Theory]
    [InlineData(0, 0, 0, 0, 255, 255, 255)]
    [InlineData(0, 0, 0, 255, 35, 31, 32)]
    [InlineData(255, 255, 255, 255, 0, 0, 0)]
    [InlineData(0, 255, 255, 0, 237, 28, 36)]
    [InlineData(110, 0, 250, 0, 158, 204, 62)]
    [InlineData(107, 115, 171, 33, 143, 123, 92)]
    [InlineData(255, 0, 0, 0, 0, 174, 239)]
    [InlineData(0, 255, 0, 0, 236, 0, 140)]
    [InlineData(0, 0, 255, 0, 255, 242, 0)]
    [InlineData(128, 128, 128, 128, 82, 74, 72)]
    [InlineData(64, 32, 200, 10, 191, 191, 90)]
    public void Swop_conversion_matches_photoshop_and_lcms(int c, int m, int y, int k, int r, int g, int b)
    {
        // Rows 2 to 6 are the reference's Photoshop pins; the rest are lcms 2.19.
        AssertNear(Convert(Transform(SwopProfile(), 4), c, m, y, k), r, g, b);
    }

    [Fact]
    public void Swop_black_point_matches_lcms()
    {
        var transform = Transform(SwopProfile(), 4);

        Assert.Equal(0.021379, transform.BlackPoint.X, 5);
        Assert.Equal(0.022173, transform.BlackPoint.Y, 5);
        Assert.Equal(0.018290, transform.BlackPoint.Z, 5);
        Assert.True(transform.UsesLut);
    }

    /// <summary>Random SWOP inks (C, M, Y, K) and the sRGB lcms 2.19 converts them to.</summary>
    private static readonly int[][] SwopLcmsSamples =
    [
        [130, 183, 14, 238, 21, 0, 28],
        [127, 26, 80, 57, 105, 154, 148],
        [190, 240, 126, 194, 31, 0, 29],
        [52, 127, 6, 110, 129, 91, 123],
        [208, 143, 93, 199, 4, 31, 46],
        [81, 36, 71, 227, 39, 48, 45],
        [64, 67, 0, 2, 186, 180, 217],
        [107, 110, 84, 85, 113, 104, 111],
        [148, 160, 101, 104, 84, 70, 86],
        [93, 100, 196, 152, 86, 76, 40],
        [11, 184, 212, 84, 167, 76, 41],
        [74, 135, 33, 169, 84, 58, 79],
        [154, 1, 173, 33, 92, 171, 113],
        [158, 181, 156, 246, 4, 0, 0],
        [161, 94, 246, 241, 0, 16, 0],
        [90, 29, 131, 11, 164, 186, 140],
        [183, 206, 9, 214, 21, 0, 46],
        [187, 192, 4, 231, 6, 0, 35],
        [23, 92, 100, 60, 182, 138, 119],
        [125, 236, 176, 181, 60, 5, 22],
        [128, 236, 55, 188, 57, 2, 47],
        [151, 18, 221, 46, 97, 154, 74],
        [106, 174, 185, 75, 122, 78, 63],
        [174, 141, 47, 159, 45, 53, 80],
    ];

    /// <summary>
    /// The planar path samples the conversion on Little CMS's own 17-node CMYK
    /// grid with its interpolation, so it tracks lcms (offline: 99.4% of 20,000
    /// random SWOP colors byte-exact, never more than one level off) rather than
    /// the unsampled double-precision evaluation, which differs by up to 13 levels
    /// where heavy ink clips at the sRGB gamut boundary.
    /// </summary>
    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    public void Planar_cmyk_conversion_matches_lcms(int depth)
    {
        var transform = Transform(SwopProfile(), 4);
        var count = SwopLcmsSamples.Length;
        var planes = new float[4][];
        for (var c = 0; c < 4; c++)
        {
            planes[c] = new float[count];
            for (var i = 0; i < count; i++)
            {
                // PSD planes store CMYK inverted; 16-bit samples scale by 257.
                var stored = 255 - SwopLcmsSamples[i][c];
                planes[c][i] = depth == 8 ? stored / 255f : stored * 257 / 65535f;
            }
        }

        var image = new PlanarImage(new PsdRect(0, 0, count, 1));
        Assert.True(transform.Apply(planes, depth, image));
        image.A.AsSpan().Fill(1f);
        var rgba = image.ToRgba(image.Bounds);
        for (var i = 0; i < count; i++)
        {
            var expected = SwopLcmsSamples[i];
            AssertNear(rgba.GetPixel(i, 0), expected[4], expected[5], expected[6]);
        }
    }

    [Fact]
    public void Planar_matrix_and_gray_paths_match_the_exact_conversion()
    {
        var rgb = Transform(TestProfiles.AdobeRgb(), 3);
        var gray = Transform(TestProfiles.GrayLiftedBlack(), 1);
        var count = 256 * 3;
        float[][] planes = [new float[count], new float[count], new float[count]];
        for (var i = 0; i < count; i++)
        {
            planes[0][i] = (i % 256) / 255f;
            planes[1][i] = ((i * 7) % 256) / 255f;
            planes[2][i] = ((i * 13) % 256) / 255f;
        }

        var image = new PlanarImage(new PsdRect(0, 0, count, 1));
        Assert.True(rgb.Apply(planes, 8, image));
        var grayImage = new PlanarImage(new PsdRect(0, 0, count, 1));
        Assert.True(gray.Apply([planes[0]], 8, grayImage));
        for (var i = 0; i < count; i++)
        {
            var (r, g, b) = rgb.Evaluate([planes[0][i], planes[1][i], planes[2][i]]);
            Assert.Equal(r, image.R[i], 0.0005);
            Assert.Equal(g, image.G[i], 0.0005);
            Assert.Equal(b, image.B[i], 0.0005);
            var (k, _, _) = gray.Evaluate([planes[0][i]]);
            Assert.Equal(k, grayImage.R[i], 0.0001);
            Assert.Equal(grayImage.R[i], grayImage.B[i]);
        }
    }

    [Fact]
    public void Cmyk_fixture_layers_convert_through_the_embedded_profile()
    {
        var document = Fixtures.Load(CmykFixture);
        var overlay = document.Layers.Single(l => l.Name == "Overlay").GetPixels()!;
        var label = document.Layers.Single(l => l.Name == "Label").GetPixels()!;

        Assert.NotNull(document.ColorTransform);

        // The reference pins (lcms2, matching Photoshop): C43 Y98 fill and C0 M100 Y100 text.
        Assert.Equal(new PsdColor(158, 204, 62), overlay.GetPixel(overlay.Width / 2, overlay.Height / 2));
        var text = Enumerable.Range(0, label.Width * label.Height)
            .Select(i => label.GetPixel(i % label.Width, i / label.Width))
            .First(p => p.A == 255);
        Assert.Equal(new PsdColor(237, 28, 36), text);

        // Photoshop saved it without Maximize Compatibility: the merged image is a white
        // placeholder, so the layer render cannot be compared with it.
        Assert.False(document.HasRealMergedImage);
        Assert.Equal(new PsdColor(255, 255, 255), document.GetMergedImage().GetPixel(48, 32));

        var naive = PsdDocument.Load(Fixtures.PathOf(CmykFixture), new PsdLoadOptions { ColorManagement = false });
        Assert.Null(naive.ColorTransform);
        var naiveOverlay = naive.Layers.Single(l => l.Name == "Overlay").GetPixels()!;
        Assert.Equal(new PsdColor(145, 255, 5), naiveOverlay.GetPixel(overlay.Width / 2, overlay.Height / 2));
    }

    [Fact]
    public void Srgb_profiled_fixtures_keep_their_pixels()
    {
        foreach (var name in new[] { "arrows.psd", "photoshop-posterize.psd", "photoshop-basic.psb" })
        {
            var document = Fixtures.Load(name);
            Assert.NotNull(document.GetImageResource(ImageResourceIds.IccProfile));
            Assert.Null(document.ColorTransform);

            // The embedded HP "sRGB IEC61966-2.1" is recognized as sRGB itself.
            var profile = IccProfile.Parse(document.GetImageResource(ImageResourceIds.IccProfile)!.Data.Span)!;
            Assert.True(IccSrgbTransform.Create(profile, 3, IccTransformSettings.Default)!.IsSrgbEquivalent);
        }
    }

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    public void Cmyk_documents_convert_merged_and_layer_pixels(int depth)
    {
        var builder = new PsdBuilder { Width = 4, Height = 1, Depth = depth, Mode = PsdColorMode.Cmyk };
        builder.Resources.Add((ImageResourceIds.IccProfile, "", SwopProfile()));

        // Inks per pixel: paper, K100, M100 Y100, C43 Y98 (stored inverted).
        int[][] inks = [[0, 0, 0, 0], [0, 0, 0, 255], [0, 255, 255, 0], [110, 0, 250, 0]];
        byte[] Plane(int channel) => depth == 8
            ? PsdBuilder.Plane8(4, 1, (x, _) => (byte)(255 - inks[x][channel]))
            : PsdBuilder.Plane16(4, 1, (x, _) => (ushort)((255 - inks[x][channel]) * 257));
        var layer = new BuilderLayer { Name = "inks", Rect = new PsdRect(0, 0, 4, 1) };
        layer.Channels[-1] = depth == 8 ? PsdBuilder.Plane8(4, 1, 255) : PsdBuilder.Plane16(4, 1, (_, _) => 65535);
        for (short c = 0; c < 4; c++)
        {
            layer.Channels[c] = Plane(c);
            builder.MergedChannels.Add(Plane(c));
        }

        builder.Layers.Add(layer);
        var document = PsdDocument.Load(builder.Build());
        var merged = document.GetMergedImage();
        var layerPixels = document.Layers[0].GetPixels()!;
        (int R, int G, int B)[] expected = [(255, 255, 255), (35, 31, 32), (237, 28, 36), (158, 204, 62)];
        for (var x = 0; x < 4; x++)
        {
            AssertNear(merged.GetPixel(x, 0), expected[x].R, expected[x].G, expected[x].B);
            Assert.Equal(merged.GetPixel(x, 0), layerPixels.GetPixel(x, 0));
        }
    }

    [Fact]
    public void Unusable_cmyk_profile_falls_back_to_the_naive_formula()
    {
        // As in the reference's psd_imported_cmyk_icc_profile_is_not_exported_as_rgb_profile:
        // full-ink channel bytes (all zero) still decode to black.
        var builder = new PsdBuilder { Width = 1, Height = 1, Mode = PsdColorMode.Cmyk };
        builder.Resources.Add((ImageResourceIds.IccProfile, "", [1, 2, 3, 4]));
        for (var c = 0; c < 4; c++)
        {
            builder.MergedChannels.Add([0]);
        }

        var document = PsdDocument.Load(builder.Build());

        Assert.Null(document.ColorTransform);
        Assert.Equal(new PsdColor(0, 0, 0), document.Render().GetPixel(0, 0));
    }

    [Fact]
    public void Rgb_documents_with_a_wide_gamut_profile_convert_and_can_opt_out()
    {
        var builder = new PsdBuilder { Width = 3, Height = 1 };
        builder.Resources.Add((ImageResourceIds.IccProfile, "", TestProfiles.AdobeRgb()));
        builder.MergedChannels.Add([64, 128, 0]);
        builder.MergedChannels.Add([128, 128, 255]);
        builder.MergedChannels.Add([192, 128, 0]);

        var managed = PsdDocument.Load(builder.Build()).Render();
        var unmanaged = PsdDocument.Load(builder.Build(), new PsdLoadOptions { ColorManagement = false }).Render();

        AssertNear(managed.GetPixel(0, 0), 0, 129, 196);
        AssertNear(managed.GetPixel(1, 0), 129, 129, 129);
        AssertNear(managed.GetPixel(2, 0), 0, 255, 0);
        Assert.Equal(new PsdColor(64, 128, 192), unmanaged.GetPixel(0, 0));
        Assert.Equal(new PsdColor(128, 128, 128), unmanaged.GetPixel(1, 0));
    }

    [Fact]
    public void Indexed_palettes_convert_through_an_rgb_profile()
    {
        var palette = new byte[768];
        palette[1] = 64;
        palette[256 + 1] = 128;
        palette[512 + 1] = 192;
        var builder = new PsdBuilder { Width = 2, Height = 1, Mode = PsdColorMode.Indexed, ColorModeData = palette };
        builder.Resources.Add((ImageResourceIds.IccProfile, "", TestProfiles.AdobeRgb()));
        builder.MergedChannels.Add([0, 1]);

        var image = PsdDocument.Load(builder.Build()).Render();

        Assert.Equal(new PsdColor(0, 0, 0), image.GetPixel(0, 0));
        AssertNear(image.GetPixel(1, 0), 0, 129, 196);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    public void Grayscale_documents_convert_through_a_gray_profile(int depth)
    {
        var builder = new PsdBuilder { Width = 3, Height = 1, Depth = depth, Mode = PsdColorMode.Grayscale };
        builder.Resources.Add((ImageResourceIds.IccProfile, "", TestProfiles.GrayGamma22()));
        builder.MergedChannels.Add(depth == 8
            ? [32, 128, 255]
            : PsdBuilder.Plane16(3, 1, (x, _) => (ushort)(new[] { 32, 128, 255 }[x] * 257)));

        var image = PsdDocument.Load(builder.Build()).Render();

        Assert.Equal(new PsdColor(26, 26, 26), image.GetPixel(0, 0));
        Assert.Equal(new PsdColor(129, 129, 129), image.GetPixel(1, 0));
        Assert.Equal(new PsdColor(255, 255, 255), image.GetPixel(2, 0));
    }

    [Fact]
    public void Thirty_two_bit_rgb_converts_linear_primaries()
    {
        // 32-bit data is linear light: only the colorant matrix applies.
        var builder = new PsdBuilder { Width = 2, Height = 1, Depth = 32 };
        builder.Resources.Add((ImageResourceIds.IccProfile, "", TestProfiles.AdobeRgb()));
        builder.MergedChannels.Add(PsdBuilder.Plane32(2, 1, (x, _) => x == 0 ? 0.5f : 0f));
        builder.MergedChannels.Add(PsdBuilder.Plane32(2, 1, (x, _) => x == 0 ? 0.5f : 1f));
        builder.MergedChannels.Add(PsdBuilder.Plane32(2, 1, (x, _) => x == 0 ? 0.5f : 0f));

        var image = PsdDocument.Load(builder.Build()).Render();

        // Linear gray 0.5 stays neutral (sRGB 188); linear Adobe green clips red and blue.
        AssertNear(image.GetPixel(0, 0), 188, 188, 188);
        AssertNear(image.GetPixel(1, 0), 0, 255, 0);
    }
}
