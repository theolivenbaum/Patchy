namespace Patchy.Psd.Tests.Support;

/// <summary>Synthetic ICC profiles for the color management tests, built with <see cref="IccBuilder"/>.</summary>
internal static class TestProfiles
{
    // Adobe RGB (1998) colorants as Adobe's profile stores them (D50-adapted).
    public static readonly double[] AdobeRed = [0.60974, 0.31111, 0.01947];
    public static readonly double[] AdobeGreen = [0.20528, 0.62567, 0.06087];
    public static readonly double[] AdobeBlue = [0.14919, 0.06322, 0.74457];

    /// <summary>Adobe RGB (1998): v2 matrix/TRC display profile, gamma 563/256.</summary>
    public static byte[] AdobeRgb() => new IccBuilder()
        .Add("desc", IccBuilder.Description("Test Adobe RGB (1998)"))
        .Add("wtpt", IccBuilder.Xyz(0.9642, 1.0, 0.8249))
        .Add("rXYZ", IccBuilder.Xyz(AdobeRed[0], AdobeRed[1], AdobeRed[2]))
        .Add("gXYZ", IccBuilder.Xyz(AdobeGreen[0], AdobeGreen[1], AdobeGreen[2]))
        .Add("bXYZ", IccBuilder.Xyz(AdobeBlue[0], AdobeBlue[1], AdobeBlue[2]))
        .Add("rTRC", IccBuilder.Gamma(563 / 256.0))
        .Add("gTRC", IccBuilder.Gamma(563 / 256.0))
        .Add("bTRC", IccBuilder.Gamma(563 / 256.0))
        .Build();

    /// <summary>
    /// The same Adobe RGB model as a v4 profile whose only conversion is an
    /// <c>mAB</c> A2B0 in matrix/shaper form (parametric gamma M curves, the
    /// colorant matrix on normalized XYZ, identity B curves).
    /// </summary>
    public static byte[] AdobeRgbV4Lut()
    {
        const double scale = 32768.0 / 65535.0;
        double[] matrix =
        [
            AdobeRed[0] * scale, AdobeGreen[0] * scale, AdobeBlue[0] * scale,
            AdobeRed[1] * scale, AdobeGreen[1] * scale, AdobeBlue[1] * scale,
            AdobeRed[2] * scale, AdobeGreen[2] * scale, AdobeBlue[2] * scale,
        ];
        var gamma = IccBuilder.Parametric(0, 563 / 256.0);
        var identity = IccBuilder.Table([]);
        return new IccBuilder { Version = 0x04300000 }
            .Add("desc", IccBuilder.MultiLocalized("Test Adobe RGB v4 LUT"))
            .Add("wtpt", IccBuilder.Xyz(0.9642, 1.0, 0.8249))
            .Add("chad", IccBuilder.Sf32(1, 0, 0, 0, 1, 0, 0, 0, 1))
            .Add("A2B0", IccBuilder.MatrixShaperAToB([gamma, gamma, gamma], matrix, [0, 0, 0], [identity, identity, identity]))
            .Build();
    }

    /// <summary>A gray profile with a gamma 2.2 kTRC (like Gray Gamma 2.2).</summary>
    public static byte[] GrayGamma22() => new IccBuilder { ColorSpace = "GRAY" }
        .Add("desc", IccBuilder.Description("Test Gray Gamma 2.2"))
        .Add("wtpt", IccBuilder.Xyz(0.9642, 1.0, 0.8249))
        .Add("kTRC", IccBuilder.Gamma(2.2))
        .Build();

    /// <summary>A gray profile whose darkest value is 5% luminance (a lifted black, for black point compensation).</summary>
    public static byte[] GrayLiftedBlack() => new IccBuilder { ColorSpace = "GRAY" }
        .Add("desc", IccBuilder.Description("Test Lifted Gray"))
        .Add("wtpt", IccBuilder.Xyz(0.9642, 1.0, 0.8249))
        .Add("kTRC", IccBuilder.Table([.. Enumerable.Range(0, 256).Select(i => 0.05 + (0.95 * Math.Pow(i / 255.0, 2.2)))]))
        .Build();

    /// <summary>Lab of a synthetic press: affine in the four inks (exact under any multilinear or tetrahedral interpolation).</summary>
    public static (double L, double A, double B) PressLab(double c, double m, double y, double k) =>
        (100 - (15 * c) - (15 * m) - (5 * y) - (60 * k), (60 * m) - (40 * c), (70 * y) - (30 * c));

    /// <summary>
    /// A v2 CMYK output profile with a Lab PCS: A2B0 and A2B1 are 2-point
    /// <c>mft2</c> LUTs of <see cref="PressLab"/>; B2A0 is an <c>mft1</c> that
    /// sends L* to a rich black (C80 M70 Y70 K100 at L* 0).
    /// </summary>
    public static byte[] SyntheticCmyk(bool withPerceptualBackward = true)
    {
        var aToB = IccBuilder.Lut(true, 4, 3, 2, node =>
        {
            var (l, a, b) = PressLab(node[0], node[1], node[2], node[3]);
            return [l * 652.80 / 65535.0, (a + 128) * 256 / 65535.0, (b + 128) * 256 / 65535.0];
        });
        var builder = new IccBuilder { DeviceClass = "prtr", ColorSpace = "CMYK", Pcs = "Lab " }
            .Add("desc", IccBuilder.Description("Test Press"))
            .Add("wtpt", IccBuilder.Xyz(0.9642, 1.0, 0.8249))
            .Add("A2B0", aToB)
            .Add("A2B1", aToB);
        if (withPerceptualBackward)
        {
            builder.Add("B2A0", IccBuilder.Lut(false, 3, 4, 2, node =>
            {
                var shadow = 1 - node[0];
                return [0.8 * shadow, 0.7 * shadow, 0.7 * shadow, shadow];
            }));
        }

        return builder.Build();
    }
}
