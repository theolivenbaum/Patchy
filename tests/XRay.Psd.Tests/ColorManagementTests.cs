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

    private static readonly IccTransformSettings Absolute = new(IccRenderingIntent.AbsoluteColorimetric, true);

    [Theory]
    [InlineData(0, 0, 0, 0, 225, 223, 216)]
    [InlineData(0, 0, 0, 255, 47, 45, 44)]
    [InlineData(0, 255, 255, 0, 207, 44, 45)]
    [InlineData(110, 0, 250, 0, 140, 179, 59)]
    [InlineData(255, 0, 0, 0, 0, 153, 203)]
    [InlineData(64, 32, 200, 10, 168, 168, 80)]
    public void Absolute_colorimetric_keeps_the_swop_paper_white(int c, int m, int y, int k, int r, int g, int b)
    {
        // lcms 2.19, INTENT_ABSOLUTE_COLORIMETRIC: A2B1 scaled by the media white over D50, no black point compensation.
        var transform = Transform(SwopProfile(), 4, Absolute);

        Assert.Equal(default, transform.BlackPoint);
        AssertNear(Convert(transform, c, m, y, k), r, g, b);
    }

    private static IccBuilder WithAdobeColorants(IccBuilder builder) => builder
        .Add("rXYZ", IccBuilder.Xyz(TestProfiles.AdobeRed[0], TestProfiles.AdobeRed[1], TestProfiles.AdobeRed[2]))
        .Add("gXYZ", IccBuilder.Xyz(TestProfiles.AdobeGreen[0], TestProfiles.AdobeGreen[1], TestProfiles.AdobeGreen[2]))
        .Add("bXYZ", IccBuilder.Xyz(TestProfiles.AdobeBlue[0], TestProfiles.AdobeBlue[1], TestProfiles.AdobeBlue[2]))
        .Add("rTRC", IccBuilder.Gamma(563 / 256.0))
        .Add("gTRC", IccBuilder.Gamma(563 / 256.0))
        .Add("bTRC", IccBuilder.Gamma(563 / 256.0));

    [Fact]
    public void Absolute_colorimetric_scales_by_the_media_white_like_lcms()
    {
        // A paper-white gray output profile: lcms tints even the gray axis, through the planar tables too.
        var gray = new IccBuilder { DeviceClass = "prtr", ColorSpace = "GRAY" }
            .Add("desc", IccBuilder.Description("Test Paper Gray"))
            .Add("wtpt", IccBuilder.Xyz(0.88, 0.9, 0.70))
            .Add("kTRC", IccBuilder.Gamma(2.2))
            .Build();
        var absoluteGray = Transform(gray, 1, Absolute);
        AssertNear(Convert(absoluteGray, 64), 60, 58, 57);
        AssertNear(Convert(absoluteGray, 128), 127, 122, 119);
        AssertNear(Convert(absoluteGray, 255), 250, 242, 236);
        AssertNear(Convert(Transform(gray, 1), 128), 129, 129, 129);
        var image = new PlanarImage(new PsdRect(0, 0, 1, 1));
        Assert.True(absoluteGray.Apply([[128 / 255f]], 8, image));
        AssertNear(((int)Math.Round(image.R[0] * 255), (int)Math.Round(image.G[0] * 255), (int)Math.Round(image.B[0] * 255)), 127, 122, 119);

        // An input-class RGB profile uses its white; a v2 display profile counts as D50 (no change).
        var input = WithAdobeColorants(new IccBuilder { DeviceClass = "scnr" }.Add("wtpt", IccBuilder.Xyz(0.9, 0.95, 0.75))).Build();
        var display = WithAdobeColorants(new IccBuilder().Add("wtpt", IccBuilder.Xyz(0.9, 0.95, 0.75))).Build();
        AssertNear(Convert(Transform(input, 3, Absolute), 255, 255, 255), 245, 251, 244);
        AssertNear(Convert(Transform(input, 3, Absolute), 64, 128, 192), 0, 127, 187);
        AssertNear(Convert(Transform(input, 3, Absolute), 128, 128, 128), 124, 127, 123);
        AssertNear(Convert(Transform(display, 3, Absolute), 64, 128, 192), 0, 129, 196);
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
    public void Cmyk_fixture_effect_and_text_colors_convert_like_the_pixels()
    {
        // psd_cmyk_document_converts_style_and_text_colors: the C42 M45 Y67 K13 'CMYC' overlay
        // lands on Photoshop's (143,123,92) and the C0 M100 Y100 K0 /Type 2 text on #ed1c24.
        var document = Fixtures.Load(CmykFixture);
        var overlayLayer = document.Layers.Single(l => l.Name == "Overlay");
        var effects = LayerEffects.Parse(overlayLayer.Effects!, document.GlobalLightAngle)!;
        var text = document.Layers.Single(l => l.Name == "Label").Text!;

        Assert.Equal(new PsdColor(143, 123, 92), Assert.Single(effects.Overlays).Color);
        Assert.All(text.StyleRuns, run => Assert.Equal(new PsdColor(237, 28, 36), run.FillColor));
        Assert.Equal(new PsdColor(237, 28, 36), document.TextEngine!.Objects.SelectMany(o => o.StyleRuns).First().FillColor);
        var bounds = overlayLayer.Bounds;
        var render = document.Render(new RenderOptions { Source = RenderSource.Layers });
        Assert.Equal(new PsdColor(143, 123, 92), render.GetPixel(bounds.Left + 2, bounds.Top + 2));

        // With color management off, both fall back to the naive ink mix.
        var naive = PsdDocument.Load(Fixtures.PathOf(CmykFixture), new PsdLoadOptions { ColorManagement = false });
        var naiveEffects = LayerEffects.Parse(naive.Layers.Single(l => l.Name == "Overlay").Effects!, naive.GlobalLightAngle)!;
        Assert.Equal(new PsdColor(129, 122, 73), Assert.Single(naiveEffects.Overlays).Color);
        Assert.All(naive.Layers.Single(l => l.Name == "Label").Text!.StyleRuns, run => Assert.Equal(new PsdColor(255, 0, 0), run.FillColor));
    }

    /// <summary>A <c>SoCo</c> payload: descriptor version 16, then a descriptor whose <c>Clr </c> is a <paramref name="classId"/> object.</summary>
    private static byte[] SolidColorBlock(string classId, params (string Key, double Value)[] components)
    {
        var writer = new PsdBuilder.Writer();
        writer.U32(16);
        writer.UnicodeString(string.Empty);
        writer.DescriptorId("null");
        writer.U32(1);
        writer.DescriptorId("Clr ");
        writer.Ascii("Objc");
        writer.UnicodeString(string.Empty);
        writer.DescriptorId(classId);
        writer.U32((uint)components.Length);
        foreach (var (key, value) in components)
        {
            writer.DescriptorId(key);
            writer.Ascii("doub");
            writer.F64(value);
        }

        return writer.ToArray();
    }

    private static PsdDocument FillDocument(PsdColorMode mode, int depth, byte[]? profile, byte[] fill, bool managed = true)
    {
        var builder = new PsdBuilder { Width = 2, Height = 1, Mode = mode, Depth = depth };
        if (profile is not null)
        {
            builder.Resources.Add((ImageResourceIds.IccProfile, "", profile));
        }

        var layer = new BuilderLayer { Name = "fill", Rect = new PsdRect(0, 0, 2, 1) };
        layer.Blocks.Add(("SoCo", fill));
        builder.Layers.Add(layer);
        return PsdDocument.Load(builder.Build(), new PsdLoadOptions { ColorManagement = managed });
    }

    [Fact]
    public void Gray_descriptor_and_text_colors_convert_through_the_gray_profile()
    {
        // 'Gry ' 50 is 50% black: lightness 0.5, level 128, which Gray Gamma 2.2 shows as 129 (lcms).
        var fill = SolidColorBlock("Grsc", ("Gry ", 50));
        var managed = FillDocument(PsdColorMode.Grayscale, 8, TestProfiles.GrayGamma22(), fill);
        var plain = FillDocument(PsdColorMode.Grayscale, 8, TestProfiles.GrayGamma22(), fill, managed: false);

        Assert.Equal(new PsdColor(129, 129, 129), managed.Layers[0].FillColor);
        Assert.Equal(new PsdColor(128, 128, 128), plain.Layers[0].FillColor);

        // EngineData /Type 0 is [alpha, lightness]; 32 / 255 converts to 26 like the pixel test above.
        var node = Text.EngineDataParser.Parse("<< /FillColor << /Type 0 /Values [ 1.0 .12549 ] >> >>"u8)!["FillColor"];
        Assert.Equal(new PsdColor(26, 26, 26), Text.EngineStyles.Color(node, managed.Colors));
        Assert.Equal(new PsdColor(32, 32, 32), Text.EngineStyles.Color(node, plain.Colors));
    }

    [Fact]
    public void Cmyk_engine_and_legacy_colors_use_the_swop_profile()
    {
        var document = FillDocument(PsdColorMode.Cmyk, 8, SwopProfile(), SolidColorBlock("CMYC", ("Cyn ", 43), ("Mgnt", 0), ("Ylw ", 98), ("Blck", 0)));

        // The C43 Y98 fill matches the fixture's pixel pin, so a fill layer and its pixels agree.
        Assert.Equal(new PsdColor(158, 204, 62), document.Layers[0].FillColor);
        var node = Text.EngineDataParser.Parse("<< /FillColor << /Type 2 /Values [ 1.0 0.0 1.0 1.0 0.0 ] >> >>"u8)!["FillColor"];
        Assert.Equal(new PsdColor(237, 28, 36), Text.EngineStyles.Color(node, document.Colors));

        // PS 5 10-byte color, space 2: CMYK stored inverted (65535 = no ink).
        byte[] legacy = [0, 2, 0xFF, 0xFF, 0, 0, 0, 0, 0xFF, 0xFF];
        Assert.Equal(new PsdColor(237, 28, 36), Text.LegacyText.ReadColor(new IO.BigEndianReader(legacy), document.Colors));
        Assert.Equal(new PsdColor(255, 0, 0), Text.LegacyText.ReadColor(new IO.BigEndianReader(legacy)));
    }

    [Fact]
    public void Rgb_descriptor_colors_follow_the_document_profile()
    {
        var fill = SolidColorBlock("RGBC", ("Rd  ", 64), ("Grn ", 128), ("Bl  ", 192));

        // Same conversion as the Adobe RGB pixels in Rgb_documents_with_a_wide_gamut_profile_convert_and_can_opt_out.
        AssertNear(FillDocument(PsdColorMode.Rgb, 8, TestProfiles.AdobeRgb(), fill).Layers[0].FillColor!.Value, 0, 129, 196);
        Assert.Equal(new PsdColor(64, 128, 192), FillDocument(PsdColorMode.Rgb, 8, TestProfiles.AdobeRgb(), fill, managed: false).Layers[0].FillColor);
        Assert.Equal(new PsdColor(64, 128, 192), FillDocument(PsdColorMode.Rgb, 8, null, fill).Layers[0].FillColor);
    }

    [Fact]
    public void Thirty_two_bit_descriptor_colors_are_linear_light()
    {
        // psd-tools' 300dpi.psb: Photoshop shows the (172, 11, 11) fill of a 32-bit document as its sRGB encoding.
        var fill = SolidColorBlock("RGBC", ("Rd  ", 172), ("Grn ", 11), ("Bl  ", 11));
        static byte Encoded(double value) => (byte)Math.Round(ColorSpaces.EncodeSrgb(value / 255) * 255);

        var plain = FillDocument(PsdColorMode.Rgb, 32, null, fill).Layers[0].FillColor!.Value;
        Assert.Equal(new PsdColor(Encoded(172), Encoded(11), Encoded(11)), plain);

        // With a profile, only the colorant matrix applies, as for 32-bit pixels: linear gray stays neutral.
        var gray = SolidColorBlock("RGBC", ("Rd  ", 127.5), ("Grn ", 127.5), ("Bl  ", 127.5));
        AssertNear(FillDocument(PsdColorMode.Rgb, 32, TestProfiles.AdobeRgb(), gray).Layers[0].FillColor!.Value, 188, 188, 188);
    }

    /// <summary>A <c>Patt</c> block with one uncompressed 8-bit pattern of the given mode and planes.</summary>
    private static byte[] PatternBlock(int mode, int width, int height, byte[][] planes)
    {
        var vma = new PsdBuilder.Writer();
        vma.U32(0);
        vma.U32(0);
        vma.U32((uint)height);
        vma.U32((uint)width);
        vma.U32((uint)planes.Length);
        foreach (var plane in planes)
        {
            vma.U32(1);
            vma.U32((uint)(23 + plane.Length));
            vma.U32(8);
            vma.I32(0);
            vma.I32(0);
            vma.I32(height);
            vma.I32(width);
            vma.U16(8);
            vma.U8(0);
            vma.Bytes(plane);
        }

        // The two trailing slots (user mask, transparency) are not written.
        vma.U32(0);
        vma.U32(0);
        var record = new PsdBuilder.Writer();
        record.U32(1);
        record.U32((uint)mode);
        record.U16((ushort)height);
        record.U16((ushort)width);
        record.UnicodeString("tile");
        record.U8(4);
        record.Ascii("tile");
        record.U32(3);
        record.U32((uint)vma.Length);
        record.Bytes(vma.ToArray());
        var block = new PsdBuilder.Writer();
        block.U32((uint)record.Length);
        block.Bytes(record.ToArray());
        while (block.Length % 4 != 0)
        {
            block.U8(0);
        }

        return block.ToArray();
    }

    [Fact]
    public void Cmyk_pattern_tiles_convert_through_the_document_profile()
    {
        // Two texels, stored inverted: C43 Y98 and C0 M100 Y100.
        byte[][] inks = [[255 - 110, 255], [255, 0], [255 - 250, 0], [255, 255]];
        PsdDocument Load(byte[]? profile, bool managed = true)
        {
            var builder = new PsdBuilder { Width = 2, Height = 1, Mode = PsdColorMode.Cmyk };
            if (profile is not null)
            {
                builder.Resources.Add((ImageResourceIds.IccProfile, "", profile));
            }

            builder.GlobalBlocks.Add(("Patt", PatternBlock(4, 2, 1, inks)));
            return PsdDocument.Load(builder.Build(), new PsdLoadOptions { ColorManagement = managed });
        }

        var tile = Load(SwopProfile()).Patterns["tile"];
        Assert.Equal([158, 204, 62, 255, 237, 28, 36, 255], tile.Rgba);

        // Without a profile, the naive mix of the pixel decode.
        Assert.Equal([145, 255, 5, 255, 255, 0, 0, 255], Load(null).Patterns["tile"].Rgba);
        Assert.Equal([145, 255, 5, 255, 255, 0, 0, 255], Load(SwopProfile(), managed: false).Patterns["tile"].Rgba);
    }

    [Fact]
    public void Gray_and_rgb_pattern_tiles_follow_a_matching_document_profile()
    {
        var gray = new PsdBuilder { Width = 1, Height = 1, Mode = PsdColorMode.Grayscale };
        gray.Resources.Add((ImageResourceIds.IccProfile, "", TestProfiles.GrayGamma22()));
        gray.GlobalBlocks.Add(("Patt", PatternBlock(1, 2, 1, [[32, 128]])));
        Assert.Equal([26, 26, 26, 255, 129, 129, 129, 255], PsdDocument.Load(gray.Build()).Patterns["tile"].Rgba);

        var rgb = new PsdBuilder { Width = 1, Height = 1 };
        rgb.Resources.Add((ImageResourceIds.IccProfile, "", TestProfiles.AdobeRgb()));
        rgb.GlobalBlocks.Add(("Patt", PatternBlock(3, 1, 1, [[64], [128], [192]])));
        var texel = PsdDocument.Load(rgb.Build()).Patterns["tile"].Rgba;
        AssertNear((texel[0], texel[1], texel[2]), 0, 129, 196);

        // A gray tile in an RGB document has no matching profile and stays as stored.
        var mixed = new PsdBuilder { Width = 1, Height = 1 };
        mixed.Resources.Add((ImageResourceIds.IccProfile, "", TestProfiles.AdobeRgb()));
        mixed.GlobalBlocks.Add(("Patt", PatternBlock(1, 1, 1, [[100]])));
        Assert.Equal([100, 100, 100, 255], PsdDocument.Load(mixed.Build()).Patterns["tile"].Rgba);
    }

    private static readonly short[] LinearCurve = [0, 50, 100, 200, 300, 400, 500, 600, 700, 800, 900, 950, 1000];

    /// <summary>
    /// Duotone color mode data: version 1, ink count, four 10-byte colors, four
    /// 64-byte Pascal names, four 28-byte transfer functions, dot gain, eleven overprint colors.
    /// </summary>
    private static byte[] DuotoneSpec(params (ushort Space, short[] Components, short[] Curve)[] inks)
    {
        var writer = new PsdBuilder.Writer();
        writer.U16(1);
        writer.U16((ushort)inks.Length);
        for (var i = 0; i < 4; i++)
        {
            var (space, components) = i < inks.Length ? (inks[i].Space, inks[i].Components) : ((ushort)0, new short[4]);
            writer.U16(space);
            foreach (var component in components)
            {
                writer.I16(component);
            }
        }

        for (var i = 0; i < 4; i++)
        {
            var name = new byte[64];
            name[0] = 3;
            "Ink"u8.CopyTo(name.AsSpan(1));
            writer.Bytes(name);
        }

        for (var i = 0; i < 4; i++)
        {
            foreach (var point in i < inks.Length ? inks[i].Curve : LinearCurve)
            {
                writer.I16(point);
            }

            writer.U16(0);
        }

        writer.U16(20);
        writer.Zeros(11 * 10);
        return writer.ToArray();
    }

    private static readonly (ushort, short[]) BlackInk = (0, [0, 0, 0, 0]);

    private static readonly (ushort, short[]) RedInk = (0, [-1, 0, 0, 0]); // 65535, 0, 0 as RGB

    private static RgbaImage RenderDuotone(byte[] spec, byte[] gray, int depth = 8, byte[]? profile = null)
    {
        var builder = new PsdBuilder { Width = gray.Length, Height = 1, Mode = PsdColorMode.Duotone, Depth = depth, ColorModeData = spec };
        if (profile is not null)
        {
            builder.Resources.Add((ImageResourceIds.IccProfile, "", profile));
        }

        builder.MergedChannels.Add(depth == 8 ? gray : PsdBuilder.Plane16(gray.Length, 1, (x, _) => (ushort)(gray[x] * 257)));
        return PsdDocument.Load(builder.Build()).Render();
    }

    [Fact]
    public void Monotone_black_duotone_renders_like_grayscale()
    {
        byte[] gray = [0, 32, 128, 200, 255];
        var spec = DuotoneSpec((BlackInk.Item1, BlackInk.Item2, LinearCurve));

        var plain = RenderDuotone(spec, gray);
        var profiled = RenderDuotone(spec, gray, profile: TestProfiles.GrayGamma22());
        for (var x = 0; x < gray.Length; x++)
        {
            Assert.Equal(new PsdColor(gray[x], gray[x], gray[x]), plain.GetPixel(x, 0));
        }

        // Through the document's gray profile the ink reflectance follows Gray Gamma 2.2 (32 -> 26, 128 -> 129).
        Assert.Equal(new PsdColor(26, 26, 26), profiled.GetPixel(1, 0));
        Assert.Equal(new PsdColor(129, 129, 129), profiled.GetPixel(2, 0));
    }

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    public void Duotone_inks_multiply_in_linear_light(int depth)
    {
        var spec = DuotoneSpec((BlackInk.Item1, BlackInk.Item2, LinearCurve), (RedInk.Item1, RedInk.Item2, LinearCurve));

        var image = RenderDuotone(spec, [0, 128, 255], depth);

        // Tone 0.498 on both inks: black leaves 21.6% linear light, red removes it from green and blue only.
        Assert.Equal(new PsdColor(0, 0, 0), image.GetPixel(0, 0));
        AssertNear(image.GetPixel(1, 0), 128, 61, 61);
        Assert.Equal(new PsdColor(255, 255, 255), image.GetPixel(2, 0));
    }

    [Fact]
    public void Duotone_curves_and_lab_inks_shape_the_result()
    {
        // A curve that puts 30% black ink at the 50% tone.
        short[] light = [0, -1, -1, -1, -1, -1, 300, -1, -1, -1, -1, -1, 1000];
        Assert.Equal(0.3, Duotone.EvaluateCurve(light, 0.5), 9);
        Assert.Equal(0.3, Duotone.EvaluateCurve([0, 50, 100, 200, 300, 400, 500, 600, 700, 800, 900, 950, 1000], 0.3), 9);
        var image = RenderDuotone(DuotoneSpec((BlackInk.Item1, BlackInk.Item2, light)), [127]);
        // 30% black coverage reflects the luminance of gray 0.7, which encodes back to 0.7 (178.5).
        Assert.InRange(image.GetPixel(0, 0).R, 177, 179);

        // Lab inks (space 7: L in hundredths, a and b signed hundredths): L 50 neutral at full tone.
        var lab = RenderDuotone(DuotoneSpec(((ushort)7, [5000, 0, 0, 0], LinearCurve)), [0]);
        AssertNear(lab.GetPixel(0, 0), 119, 119, 119);
    }

    [Fact]
    public void Multichannel_planes_read_as_inverted_cmy_inks()
    {
        // As the reference's convert_multichannel_planes_to_rgb: plane bytes are 255 - ink, a missing plane is no ink.
        var three = new PsdBuilder { Width = 2, Height = 1, Mode = PsdColorMode.Multichannel };
        three.MergedChannels.Add([0, 255]);
        three.MergedChannels.Add([255, 128]);
        three.MergedChannels.Add([255, 0]);
        var two = new PsdBuilder { Width = 1, Height = 1, Mode = PsdColorMode.Multichannel };
        two.MergedChannels.Add([40]);
        two.MergedChannels.Add([80]);

        var image = PsdDocument.Load(three.Build()).Render();
        Assert.Equal(new PsdColor(0, 255, 255), image.GetPixel(0, 0));
        Assert.Equal(new PsdColor(255, 128, 0), image.GetPixel(1, 0));
        Assert.Equal(new PsdColor(40, 80, 255), PsdDocument.Load(two.Build()).Render().GetPixel(0, 0));
    }

    [Fact]
    public void Unreadable_duotone_specifications_fall_back_to_gray()
    {
        Assert.Equal(new PsdColor(90, 90, 90), RenderDuotone([0, 1, 0, 1], [90]).GetPixel(0, 0));
        var spec = DuotoneSpec((BlackInk.Item1, BlackInk.Item2, LinearCurve));
        spec[1] = 9; // ink count out of range
        Assert.Equal(new PsdColor(90, 90, 90), RenderDuotone(spec, [90]).GetPixel(0, 0));
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
    public void Dot_gain_gray_ramp_matches_photoshop()
    {
        // The reference pins its local gray-ramp-dotgain20.psd against Photoshop's Convert to
        // Profile (within 2 levels); this builds the same 256-step ramp with a Dot Gain 20% stand-in.
        var builder = new PsdBuilder { Width = 256, Height = 1, Mode = PsdColorMode.Grayscale };
        builder.Resources.Add((ImageResourceIds.IccProfile, "", TestProfiles.DotGain20Like()));
        var layer = new BuilderLayer { Name = "ramp", Rect = new PsdRect(0, 0, 256, 1) };
        layer.Channels[-1] = PsdBuilder.Plane8(256, 1, 255);
        layer.Channels[0] = PsdBuilder.Plane8(256, 1, (x, _) => (byte)x);
        builder.Layers.Add(layer);
        builder.MergedChannels.Add(PsdBuilder.Plane8(256, 1, (x, _) => (byte)x));
        builder.Layers[0].Blocks.Add(("SoCo", SolidColorBlock("Grsc", ("Gry ", 50))));

        var document = PsdDocument.Load(builder.Build());
        var pixels = document.Layers[0].GetPixels()!;
        var merged = document.GetMergedImage();
        foreach (var (gray, srgb) in TestProfiles.DotGain20Pins)
        {
            var pixel = pixels.GetPixel(gray, 0);
            Assert.InRange(pixel.R, srgb - 1, srgb + 1);
            Assert.True(pixel.R == pixel.G && pixel.G == pixel.B);
            Assert.Equal(pixel, merged.GetPixel(gray, 0));
        }

        // A 50% 'Grsc' color converts like the 128 pixel: Photoshop's 20% dot gain at mid tone.
        Assert.Equal(pixels.GetPixel(128, 0), document.Layers[0].FillColor);
        var previous = -1;
        for (var x = 0; x < 256; x++)
        {
            Assert.True(pixels.GetPixel(x, 0).R >= previous);
            previous = pixels.GetPixel(x, 0).R;
        }
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
