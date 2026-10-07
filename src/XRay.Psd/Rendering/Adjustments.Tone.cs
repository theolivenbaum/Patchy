namespace XRay.Psd.Rendering;

/// <summary>
/// Channel-separable tone adjustments: Levels, Color Balance, Brightness/Contrast and
/// Exposure, each as exact 256-entry tables (.reference/src/core/adjustment_layer.cpp).
/// </summary>
internal static partial class Adjustments
{
    /// <summary>
    /// <c>levels_value</c>: one record as a real-valued transfer. An identity record
    /// returns its input untouched so a master-only adjustment equals a single stage.
    /// </summary>
    private static double LevelsValue(double value, LevelsRecord record)
    {
        record = record.Clamped();
        if (!record.HasEffect)
        {
            return value;
        }

        var inputRange = (double)(record.WhiteInput - record.BlackInput);
        var gamma = record.GammaPercent / 100.0;
        var inverseGamma = gamma <= 0.0 ? 1.0 : 1.0 / gamma;
        var normalized = Math.Clamp((value - record.BlackInput) / inputRange, 0.0, 1.0);
        var leveled = Math.Pow(normalized, inverseGamma);
        return record.BlackOutput + (leveled * (record.WhiteOutput - record.BlackOutput));
    }

    /// <summary>
    /// <c>apply_levels</c>: the component record runs first, then Composite RGB, and the
    /// channel result reaches the composite stage unrounded (within 2/255 of Photoshop's
    /// render of psd-tools' levels_rgb.psd; the reverse order is off by up to 52).
    /// </summary>
    internal static ChannelLuts BuildLevelsLut(LevelsSettings settings)
    {
        var master = settings.Master.Clamped();
        return new ChannelLuts(LevelsChannel(settings.Red, master), LevelsChannel(settings.Green, master), LevelsChannel(settings.Blue, master));

        static byte[] LevelsChannel(LevelsRecord record, LevelsRecord master)
        {
            var lut = new byte[256];
            for (var v = 0; v < 256; v++)
            {
                lut[v] = ClampByte((float)LevelsValue(LevelsValue(v, record), master));
            }

            return lut;
        }
    }

    /// <summary>Input black points per shadows slider step (fitted, see <see cref="BuildColorBalanceLut"/>).</summary>
    private const double ColorBalanceShadowStep = 2.18;

    /// <summary>Input white points per highlights slider step (fitted).</summary>
    private const double ColorBalanceHighlightStep = 1.26;

    /// <summary>
    /// Color Balance as three per-channel tables. The reference renders a flat
    /// <c>round(slider * 2.55)</c> midtones offset, which is far from Photoshop; this model
    /// is fitted to Photoshop's composites of photoshop-color-balance.psd (midtones only,
    /// within 1/255) and photoshop-color-balance-full.psd (all three ranges and Preserve
    /// Luminosity, within 2/255, mean 1.0). Each channel runs, in order:
    /// <list type="number">
    /// <item>Highlights, a Levels input white point: a slider toward the channel's own
    /// color lowers its white by 1.26 levels per step; a slider of another channel toward
    /// its complement (cyan, magenta, yellow) lowers this channel's white by half that.</item>
    /// <item>Midtones, a gamma on the channel: <c>v' = (v/255)^(2^(-slider/100))</c>.</item>
    /// <item>Shadows, a Levels input black point: a slider toward the complement raises the
    /// channel's own black by 2.18 levels per step; a slider of another channel toward its
    /// own color raises this channel's black by half that.</item>
    /// </list>
    /// So shadows only deepen and highlights only lift, and a shift toward a color moves the
    /// other two channels half as far. The two step sizes and the stage order come from one
    /// 8-color capture with Preserve Luminosity on, which the fit absorbs; the flag is not
    /// modeled separately. See docs/adjustments.md.
    /// </summary>
    internal static ChannelLuts BuildColorBalanceLut(ColorBalanceSettings settings)
    {
        var luts = new byte[3][];
        for (var channel = 0; channel < 3; channel++)
        {
            var black = Math.Min(254.0, ColorBalanceShadowStep * Shift(settings.Shadows, channel, towardColor: false));
            var white = Math.Max(1.0, 255.0 - (ColorBalanceHighlightStep * Shift(settings.Highlights, channel, towardColor: true)));
            var gamma = Math.Pow(2.0, -Math.Clamp(settings.Midtones[channel], -100, 100) / 100.0);
            var lut = new byte[256];
            for (var v = 0; v < 256; v++)
            {
                var x = Math.Min(255.0, v * 255.0 / white);
                x = 255.0 * Math.Pow(x / 255.0, gamma);
                x = Math.Max(0.0, (x - black) * 255.0 / (255.0 - black));
                lut[v] = RoundByte(x);
            }

            luts[channel] = lut;
        }

        return new ChannelLuts(luts[0], luts[1], luts[2]);

        // The slider steps that move one channel's point: its own slider in the given
        // direction, plus half of every other slider in the opposite one.
        static double Shift(ColorBalanceRange range, int channel, bool towardColor)
        {
            var total = 0.0;
            for (var c = 0; c < 3; c++)
            {
                var slider = Math.Clamp(range[c], -100, 100);
                var own = towardColor ? Math.Max(slider, 0) : Math.Max(-slider, 0);
                var other = towardColor ? Math.Max(-slider, 0) : Math.Max(slider, 0);
                total += c == channel ? own : 0.5 * other;
            }

            return total;
        }
    }

    internal static byte[] BuildBrightnessContrastLut(BrightnessContrastSettings settings)
    {
        var lut = new byte[256];
        for (var v = 0; v < 256; v++)
        {
            lut[v] = BrightnessContrastValue(v, settings.Brightness, settings.Contrast, settings.UseLegacy);
        }

        return lut;
    }

    /// <summary>
    /// <c>brightness_contrast_channel_value</c>, both Photoshop algorithms. Legacy:
    /// positive contrast folds brightness into the input with slope 100/(100-c), c = 100
    /// thresholds at v + b >= 127, negative contrast compresses first and adds brightness
    /// to the output. Modern: <c>contrast(brightness(v))</c> on the unit interval with one
    /// final rounding (docs "Modern Brightness/Contrast").
    /// </summary>
    internal static byte BrightnessContrastValue(int value, int brightness, int contrast, bool useLegacy)
    {
        if (!useLegacy)
        {
            var mb = Math.Clamp(brightness, -150, 150);
            var mc = Math.Clamp(contrast, -50, 100);
            return RoundByte(255.0 * ModernContrastValue(mc, ModernBrightnessValue(mb, value / 255.0)));
        }

        var b = Math.Clamp(brightness, -100, 100);
        var c = Math.Clamp(contrast, -100, 100);
        if (c == 0)
        {
            return (byte)Math.Clamp(value + b, 0, 255);
        }

        if (c >= 100)
        {
            return value + b >= 127 ? (byte)255 : (byte)0;
        }

        if (c > 0)
        {
            return RoundByte(((value + b - 127.5) * 100.0 / (100.0 - c)) + 127.5);
        }

        var compressed = Math.Round(((value - 127.5) * (100.0 + c) / 100.0) + 127.5, MidpointRounding.AwayFromZero);
        return (byte)Math.Clamp(compressed + b, 0, 255);
    }

    // 2^(1/110), multiplied out per step instead of calling exp2 so every runtime computes
    // the identical double.
    private const double ModernBrightnessGainStep = 1.006321233202252;

    private static double ModernBrightnessGain(int b)
    {
        var sigma = 1.0;
        for (var step = 0; step < b; step++)
        {
            sigma *= ModernBrightnessGainStep;
        }

        return sigma;
    }

    /// <summary>b in 1..100: a gain ray up to output 0.5, then one cubic Hermite to (1, 1).</summary>
    private static double ModernBrightnessCore(int b, double v)
    {
        var sigma = ModernBrightnessGain(b);
        var rayEnd = 0.5 / sigma;
        if (v <= rayEnd)
        {
            return sigma * v;
        }

        var h = 1.0 - rayEnd;
        var t = (v - rayEnd) / h;
        var tau = Math.Max(0.1, 1.0 / (1.0 + (12.0 * (sigma - 1.0))));
        var hermite = (((((2.0 * t) - 3.0) * t * t) + 1.0) * 0.5) + (((((t - 2.0) * t) + 1.0) * t) * h * sigma) +
                      ((3.0 - (2.0 * t)) * t * t) + ((t - 1.0) * t * t * h * tau);
        return Math.Min(1.0, hermite);
    }

    private static double ModernBrightnessPositive(int b, double v) =>
        b <= 100 ? ModernBrightnessCore(b, v) : ModernBrightnessCore(b - 100, ModernBrightnessCore(100, v));

    /// <summary>Negative brightness is the exact inverse of the positive curve, by 64-step bisection.</summary>
    private static double ModernBrightnessValue(int b, double v)
    {
        if (b == 0)
        {
            return v;
        }

        if (b > 0)
        {
            return ModernBrightnessPositive(b, v);
        }

        if (v <= 0.0)
        {
            return 0.0;
        }

        if (v >= 1.0)
        {
            return 1.0;
        }

        var low = 0.0;
        var high = 1.0;
        for (var step = 0; step < 64; step++)
        {
            var mid = 0.5 * (low + high);
            if (ModernBrightnessPositive(-b, mid) < v)
            {
                low = mid;
            }
            else
            {
                high = mid;
            }
        }

        return 0.5 * (low + high);
    }

    /// <summary>Contrast: a parabola in each half, pivoting at (0.5, 0.5), beta = 1 - 0.0076 c.</summary>
    private static double ModernContrastValue(int c, double v)
    {
        var beta = 1.0 - (0.0076 * c);
        var alpha = 2.0 - (2.0 * beta);
        if (v <= 0.5)
        {
            return ((alpha * v) + beta) * v;
        }

        var w = 1.0 - v;
        return 1.0 - (((alpha * w) + beta) * w);
    }

    internal static byte[] BuildExposureLut(ExposureSettings settings)
    {
        var lut = new byte[256];
        for (var v = 0; v < 256; v++)
        {
            lut[v] = ExposureValue(v, settings);
        }

        return lut;
    }

    /// <summary>
    /// <c>exposure_channel_value</c>: linearize with a plain 2.2 power, scale by
    /// 2^exposure, add the offset, apply 1/gamma, and encode again. Within 1/255 of
    /// Photoshop's render of psd-tools' exposure_rgb.psd.
    /// </summary>
    internal static byte ExposureValue(int value, ExposureSettings settings)
    {
        settings = settings.Clamped();
        const double displayGamma = 2.2;
        var linear = Math.Pow(value / 255.0, displayGamma);
        var exposed = (linear * Math.Pow(2.0, settings.ExposureHundredths / 100.0)) + (settings.OffsetTenThousandths / 10000.0);
        var corrected = Math.Pow(Math.Max(0.0, exposed), 100.0 / settings.GammaHundredths);
        var encoded = Math.Pow(Math.Clamp(corrected, 0.0, 1.0), 1.0 / displayGamma);
        return RoundByte(encoded * 255.0);
    }
}
