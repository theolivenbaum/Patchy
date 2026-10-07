namespace XRay.Psd.Rendering;

/// <summary>
/// Photoshop 2026 Hue/Saturation, calibrated pixel for pixel against COM-rendered probe
/// files (.reference/src/core/adjustment_layer.cpp, .reference/docs/adjustments-calibration.md
/// "Hue/Saturation calibration"). Colorize and the master sliders share three stages: the
/// lightness slider blends toward white or black and rounds, the hue lives on a 1530-step
/// wheel, and the result is rebuilt from an integer lightness plus a half-chroma spread
/// with asymmetric rounding.
/// </summary>
internal static partial class Adjustments
{
    /// <summary>Per-degree hue interpolant (x/255) within the 60-degree sector: mid = p + f * (q - p).</summary>
    private static ReadOnlySpan<byte> ColorizeHueInterp =>
    [
        0,   0,   7,  14,  14,  21,  28,  28,  34,  40,  47,  47,  53,  59,  59,
        65,  71,  76,  76,  82,  88,  88,  93,  99, 104, 104, 110, 115, 115, 121,
        126, 132, 132, 137, 142, 142, 148, 153, 159, 159, 165, 170, 170, 176, 182,
        187, 187, 193, 199, 199, 205, 211, 211, 218, 224, 231, 231, 237, 244, 244,
        255, 255, 244, 244, 237, 231, 231, 224, 218, 211, 211, 205, 199, 199, 193,
        187, 182, 182, 176, 170, 170, 165, 159, 153, 153, 148, 142, 142, 137, 132,
        126, 126, 121, 115, 115, 110, 104, 104,  99,  93,  88,  88,  82,  76,  76,
        71,  65,  59,  59,  53,  47,  47,  40,  34,  28,  28,  21,  14,  14,   7,
        0,   0,   0,   7,  14,  14,  21,  28,  34,  34,  40,  47,  47,  53,  59,
        65,  65,  71,  76,  76,  82,  88,  88,  93,  99, 104, 104, 110, 115, 115,
        121, 126, 132, 132, 137, 142, 142, 148, 153, 159, 159, 165, 170, 170, 176,
        182, 187, 187, 193, 199, 199, 205, 211, 218, 218, 224, 231, 231, 237, 244,
        255, 255, 244, 237, 237, 231, 224, 224, 218, 211, 205, 205, 199, 193, 193,
        187, 182, 176, 176, 170, 165, 165, 159, 153, 148, 148, 142, 137, 137, 132,
        126, 121, 121, 115, 110, 110, 104,  99,  93,  93,  88,  82,  82,  76,  71,
        65,  65,  59,  53,  53,  47,  40,  40,  34,  28,  21,  21,  14,   7,   7,
        0,   0,   7,   7,  14,  21,  21,  28,  34,  40,  40,  47,  53,  53,  59,
        65,  71,  71,  76,  82,  82,  88,  93,  99,  99, 104, 110, 110, 115, 121,
        126, 126, 132, 137, 137, 142, 148, 148, 153, 159, 165, 165, 170, 176, 176,
        182, 187, 193, 193, 199, 205, 205, 211, 218, 224, 224, 231, 237, 237, 244,
        255, 255, 255, 244, 237, 237, 231, 224, 218, 218, 211, 205, 205, 199, 193,
        187, 187, 182, 176, 176, 170, 165, 165, 159, 153, 148, 148, 142, 137, 137,
        132, 126, 121, 121, 115, 110, 110, 104,  99,  93,  93,  88,  82,  82,  76,
        71,  65,  65,  59,  53,  53,  47,  40,  34,  34,  28,  21,  21,  14,   7,
    ];

    /// <summary>Photoshop's effective colorize saturation ratio per percent (slightly below s/100).</summary>
    private static readonly double[] ColorizeSaturationScale =
    [
        0.000000000, 0.007905262, 0.019710941, 0.027668416, 0.039421881,
        0.047270696, 0.059073014, 0.066946710, 0.078843763, 0.086640420,
        0.098455023, 0.110265169, 0.118146027, 0.129960630, 0.137863155,
        0.149803150, 0.157507281, 0.169323089, 0.177190272, 0.189000384,
        0.200803537, 0.208678535, 0.220530338, 0.228370759, 0.240176779,
        0.249015748, 0.259921260, 0.267786839, 0.279548726, 0.287450787,
        0.299606299, 0.311067367, 0.318931578, 0.330738946, 0.338646177,
        0.350410526, 0.358300525, 0.370104305, 0.378000768, 0.389797144,
        0.401607074, 0.409465789, 0.421278069, 0.429150262, 0.441060676,
        0.448841267, 0.460652039, 0.468626969, 0.480353559, 0.488212135,
        0.501968504, 0.511857893, 0.519710941, 0.531513797, 0.539421881,
        0.551231577, 0.559073014, 0.571147357, 0.578843763, 0.590730136,
        0.602385922, 0.610265169, 0.622070134, 0.629960630, 0.641761664,
        0.649803150, 0.661437828, 0.669323089, 0.681130891, 0.689000384,
        0.700803537, 0.712621052, 0.720530338, 0.732303348, 0.740176779,
        0.751984252, 0.759921260, 0.771696337, 0.779548726, 0.791502625,
        0.803170548, 0.811067367, 0.822875656, 0.830738946, 0.842556139,
        0.850410526, 0.862224811, 0.870104305, 0.881917104, 0.889797144,
        0.901607074, 0.913423683, 0.921278069, 0.933202100, 0.941060676,
        0.952793047, 0.960652039, 0.972459005, 0.980353559, 0.992156742,
        1.003952500,
    ];

    /// <summary>
    /// Photoshop's effective master saturation multiplier per slider percent, indexed
    /// delta + 100. No closed form reproduces it; +100 is 128, not unbounded.
    /// </summary>
    private static readonly double[] MasterSaturationScale =
    [
        0.000000000, 0.015503876, 0.027027027, 0.034482759, 0.047619048,
        0.054545455, 0.066666667, 0.074074074, 0.085714286, 0.097560976,
        0.105263158, 0.117647059, 0.125000000, 0.136363636, 0.142857143,
        0.155963303, 0.163934426, 0.176470588, 0.187500000, 0.195121951,
        0.206896552, 0.214285714, 0.226415094, 0.235294118, 0.247311828,
        0.253333333, 0.266666667, 0.277777778, 0.285714286, 0.296296296,
        0.304347826, 0.315789474, 0.324324324, 0.333333333, 0.347826087,
        0.355555556, 0.368421053, 0.375000000, 0.387096774, 0.393939394,
        0.406250000, 0.413793103, 0.425531915, 0.437500000, 0.444444444,
        0.457142857, 0.465116279, 0.476190476, 0.483870968, 0.496062992,
        0.500000000, 0.515151515, 0.527272727, 0.534883721, 0.545454545,
        0.555555556, 0.566037736, 0.574468085, 0.586206897, 0.600000000,
        0.606060606, 0.617021277, 0.625000000, 0.636363636, 0.645161290,
        0.656000000, 0.666666667, 0.675675676, 0.687500000, 0.695652174,
        0.707317073, 0.714285714, 0.727272727, 0.733333333, 0.747368421,
        0.753246753, 0.764705882, 0.777777778, 0.785714286, 0.800000000,
        0.804878049, 0.816326531, 0.823529412, 0.836363636, 0.847457627,
        0.857142857, 0.866666667, 0.875000000, 0.886792453, 0.894736842,
        0.905982906, 0.914285714, 0.925925926, 0.937500000, 0.945454545,
        0.956521739, 0.965517241, 0.976744186, 0.984126984, 1.000000000,
        1.000000000, 1.011764706, 1.023255814, 1.031250000, 1.043478261,
        1.050847458, 1.066666667, 1.074074074, 1.086956522, 1.097560976,
        1.111111111, 1.121212121, 1.137254902, 1.153846154, 1.160000000,
        1.176470588, 1.187500000, 1.205128205, 1.216216216, 1.235294118,
        1.247311828, 1.266666667, 1.277777778, 1.297872340, 1.310344828,
        1.333333333, 1.352941176, 1.368421053, 1.388888889, 1.403508772,
        1.428571429, 1.444444444, 1.470588235, 1.485714286, 1.514285714,
        1.529411765, 1.560000000, 1.575757576, 1.608695652, 1.640000000,
        1.658536585, 1.692307692, 1.716981132, 1.750000000, 1.777777778,
        1.811594203, 1.838709677, 1.882352941, 1.909090909, 1.952380952,
        2.000000000, 2.030769231, 2.076923077, 2.111111111, 2.166666667,
        2.200000000, 2.263157895, 2.307692308, 2.368421053, 2.411764706,
        2.481481481, 2.533333333, 2.609756098, 2.692307692, 2.750000000,
        2.842105263, 2.904761905, 3.000000000, 3.081081081, 3.200000000,
        3.275862069, 3.411764706, 3.500000000, 3.666666667, 3.761904762,
        3.933333333, 4.125000000, 4.263157895, 4.500000000, 4.666666667,
        4.923076923, 5.117647059, 5.444444444, 5.692307692, 6.090909091,
        6.400000000, 6.909090909, 7.333333333, 8.000000000, 8.818181818,
        9.500000000, 10.666666667, 11.666666667, 13.500000000, 15.000000000,
        18.250000000, 21.333333333, 28.400000000, 36.500000000, 64.000000000,
        128.000000000,
    ];

    /// <summary>
    /// <c>photoshop_lightness_value</c>: the percent is quantized to a byte first
    /// (|l| * 255 / 100 truncated), then the value blends toward white or black.
    /// </summary>
    internal static int PhotoshopLightnessValue(int value, int lightness)
    {
        var step = Math.Abs(lightness) * 255 / 100;
        if (lightness > 0)
        {
            return (int)(value + ((255.0 - value) * step / 255.0) + 0.5);
        }

        if (lightness < 0)
        {
            return (int)((value * (255.0 - step) / 255.0) + 0.5);
        }

        return value;
    }

    /// <summary>The pixel's position on Photoshop's 1530-step hue wheel (six sectors of 255).</summary>
    private static double PhotoshopWheelPosition(int red, int green, int blue)
    {
        var maximum = Math.Max(red, Math.Max(green, blue));
        var minimum = Math.Min(red, Math.Min(green, blue));
        var span = (double)(maximum - minimum);
        if (span <= 0.0)
        {
            return 0.0;
        }

        double Ramp(int middle, int low) => 255.0 * (middle - low) / span;
        if (red == maximum && blue == minimum)
        {
            return Ramp(green, blue);
        }

        if (green == maximum && blue == minimum)
        {
            return 510.0 - Ramp(red, blue);
        }

        if (green == maximum && red == minimum)
        {
            return 510.0 + Ramp(blue, red);
        }

        if (blue == maximum && red == minimum)
        {
            return 1020.0 - Ramp(green, red);
        }

        if (blue == maximum && green == minimum)
        {
            return 1020.0 + Ramp(red, green);
        }

        return 1530.0 - Ramp(blue, green);
    }

    private static void PhotoshopWheelSplit(double position, out int sector, out double interpolant)
    {
        position %= 1530.0;
        if (position < 0.0)
        {
            position += 1530.0;
        }

        sector = Math.Min(5, (int)(position / 255.0));
        var offset = position - (sector * 255.0);
        interpolant = sector % 2 == 0 ? offset : 255.0 - offset;
    }

    /// <summary>
    /// <c>photoshop_hsl_reconstruct</c>: round toward the max channel, truncate toward the
    /// min, which makes an all-zero master an exact identity over the RGB cube.
    /// </summary>
    private static (int R, int G, int B) PhotoshopHslReconstruct(int light, double halfChroma, int sector, double interpolant)
    {
        var q = Math.Min(255, light + (int)(halfChroma + 0.5));
        var p = Math.Max(0, light - (int)halfChroma);
        var mid = p + (int)(((q - p) * interpolant / 255.0) + 0.5);
        return sector switch
        {
            0 => (q, mid, p),
            1 => (mid, q, p),
            2 => (p, q, mid),
            3 => (p, mid, q),
            4 => (mid, p, q),
            _ => (q, p, mid),
        };
    }

    private static (int R, int G, int B) ApplyColorize(int red, int green, int blue, HueSaturationSettings settings)
    {
        var hue = ((settings.ColorizeHue % 360) + 360) % 360;
        var saturation = Math.Clamp(settings.ColorizeSaturation, 0, 100);
        var lightness = Math.Clamp(settings.ColorizeLightness, -100, 100);
        var maximum = Math.Max(red, Math.Max(green, blue));
        var minimum = Math.Min(red, Math.Min(green, blue));
        var light = PhotoshopLightnessValue((maximum + minimum) >> 1, lightness);
        var band = Math.Min(light, 255 - light);
        var halfChroma = band * ColorizeSaturationScale[saturation];
        return PhotoshopHslReconstruct(light, halfChroma, hue / 60, ColorizeHueInterp[hue]);
    }

    /// <summary>
    /// A band's strength at an input hue: 0 outside the outer stops, linear ramps between
    /// the outer and inner stops, 1 between the inner stops; stops may wrap past 360.
    /// </summary>
    internal static double HueSaturationBandWeight(double hueDegrees, HueSaturationBand band)
    {
        static double Forward(int from, double to)
        {
            var delta = (to - from) % 360.0;
            return delta < 0.0 ? delta + 360.0 : delta;
        }

        var rampIn = Forward(band.OuterStart, band.InnerStart);
        var plateau = Forward(band.InnerStart, band.InnerEnd);
        var rampOut = Forward(band.InnerEnd, band.OuterEnd);
        if (rampIn + plateau + rampOut <= 0.0)
        {
            return 0.0;
        }

        var position = Forward(band.OuterStart, hueDegrees);
        if (position < rampIn)
        {
            return position / rampIn;
        }

        if (position <= rampIn + plateau)
        {
            return 1.0;
        }

        var tail = position - rampIn - plateau;
        return tail >= rampOut ? 0.0 : 1.0 - (tail / rampOut);
    }

    /// <summary><c>apply_hue_saturation</c> on one 8-bit color.</summary>
    internal static (int R, int G, int B) ApplyHueSaturation(int red, int green, int blue, HueSaturationSettings settings, byte[]? lightnessRamp = null)
    {
        if (settings.Colorize)
        {
            return ApplyColorize(red, green, blue, settings);
        }

        var hueShift = Math.Clamp(settings.Hue, -180, 180);
        var saturationDelta = Math.Clamp(settings.Saturation, -100, 100);
        var lightnessDelta = Math.Clamp(settings.Lightness, -100, 100);

        // Bands select by the pixel's ORIGINAL hue, and band lightness runs before the
        // master lightness.
        Span<double> weights = stackalloc double[6];
        weights.Clear();
        var bands = settings.Bands;
        if (AnyBandHasEffect(bands))
        {
            var wheel = PhotoshopWheelPosition(red, green, blue);
            var hueDegrees = wheel / 4.25;
            PhotoshopWheelSplit(wheel, out var sector, out var interpolant);
            double maximum = Math.Max(red, Math.Max(green, blue));
            double minimum = Math.Min(red, Math.Min(green, blue));
            var lightness = 0.0;
            for (var i = 0; i < bands.Length && i < weights.Length; i++)
            {
                if (!bands[i].HasEffect)
                {
                    continue;
                }

                weights[i] = HueSaturationBandWeight(hueDegrees, bands[i]);
                lightness += weights[i] * Math.Clamp(bands[i].Lightness, -100, 100);
            }

            // Band lightness collapses the chroma toward the max channel (positive) or the
            // min channel (negative); overlapping bands sum their weighted percents.
            if (lightness > 0.0)
            {
                minimum += (maximum - minimum) * Math.Min(lightness, 100.0) / 100.0;
            }
            else if (lightness < 0.0)
            {
                maximum += (minimum - maximum) * Math.Min(-lightness, 100.0) / 100.0;
            }

            var high = Math.Clamp((int)(maximum + 0.5), 0, 255);
            var low = Math.Clamp((int)(minimum + 0.5), 0, 255);
            (red, green, blue) = PhotoshopHslReconstruct((high + low) >> 1, (high - low) * 0.5, sector, interpolant);
        }

        // The lightness slider runs first and per channel.
        int litR, litG, litB;
        if (lightnessRamp is not null)
        {
            litR = lightnessRamp[red];
            litG = lightnessRamp[green];
            litB = lightnessRamp[blue];
        }
        else
        {
            litR = PhotoshopLightnessValue(red, lightnessDelta);
            litG = PhotoshopLightnessValue(green, lightnessDelta);
            litB = PhotoshopLightnessValue(blue, lightnessDelta);
        }

        var max = Math.Max(litR, Math.Max(litG, litB));
        var min = Math.Min(litR, Math.Min(litG, litB));
        if (max == min)
        {
            return (litR, litG, litB); // The master sliders never tint a neutral pixel.
        }

        var light = (max + min) >> 1;
        var half = (max - min) * 0.5;

        // Band saturation offsets from 1 sum and multiply the master ratio; hue rotations
        // add in whole wheel steps, converted per band.
        var bandSaturationOffset = 0.0;
        var rotation = Math.Floor((hueShift * 4.25) + 0.5);
        for (var i = 0; i < bands.Length && i < weights.Length; i++)
        {
            var weight = weights[i];
            if (weight <= 0.0)
            {
                continue;
            }

            var bandSaturation = Math.Clamp(bands[i].Saturation, -100, 100);
            if (bandSaturation != 0)
            {
                bandSaturationOffset += weight * (MasterSaturationScale[bandSaturation + 100] - 1.0);
            }

            var bandHue = Math.Clamp(bands[i].Hue, -180, 180);
            if (bandHue != 0)
            {
                rotation += Math.Floor((weight * bandHue * 4.25) + 0.5);
            }
        }

        var ratio = MasterSaturationScale[saturationDelta + 100] * Math.Max(0.0, 1.0 + bandSaturationOffset);

        // Saturation grows toward the in-gamut limit and never below the incoming chroma.
        var limit = Math.Max(Math.Min(light, 255 - light), half);
        var halfChroma = Math.Min(half * ratio, limit);
        PhotoshopWheelSplit(PhotoshopWheelPosition(litR, litG, litB) + rotation, out var outSector, out var outInterpolant);
        return PhotoshopHslReconstruct(light, halfChroma, outSector, outInterpolant);
    }

    private static bool AnyBandHasEffect(HueSaturationBand[] bands)
    {
        foreach (var band in bands)
        {
            if (band.HasEffect)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Hue/Saturation mixes channels through HSL, so it runs per pixel (no LUT).</summary>
    private sealed class HueSaturationAdjustment : IAdjustment
    {
        private readonly HueSaturationSettings _settings;
        private readonly byte[] _lightnessRamp = new byte[256];

        public HueSaturationAdjustment(HueSaturationSettings settings)
        {
            _settings = settings;
            var lightness = Math.Clamp(settings.Lightness, -100, 100);
            for (var v = 0; v < 256; v++)
            {
                _lightnessRamp[v] = (byte)PhotoshopLightnessValue(v, lightness);
            }
        }

        public void Apply(Span<float> r, Span<float> g, Span<float> b)
        {
            // Flat areas repeat colors; remember the last mapping.
            var lastIn = -1;
            var lastOut = (0f, 0f, 0f);
            for (var i = 0; i < r.Length; i++)
            {
                var red = Byte(r[i]);
                var green = Byte(g[i]);
                var blue = Byte(b[i]);
                var key = (red << 16) | (green << 8) | blue;
                if (key != lastIn)
                {
                    var (or, og, ob) = ApplyHueSaturation(red, green, blue, _settings, _lightnessRamp);
                    lastOut = (or / 255f, og / 255f, ob / 255f);
                    lastIn = key;
                }

                (r[i], g[i], b[i]) = lastOut;
            }
        }
    }
}
