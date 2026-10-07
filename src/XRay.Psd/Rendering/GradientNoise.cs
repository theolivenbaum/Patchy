using XRay.Psd.Descriptors;

namespace XRay.Psd.Rendering;

/// <summary>Color model of a noise gradient (<c>ClrS</c>).</summary>
internal enum GradientNoiseModel
{
    Rgb,
    Hsb,
    Lab,
}

/// <summary>
/// A noise (<c>ClNs</c>) gradient: the reference's deterministic noise field, ported
/// from <c>smooth_noise</c>, <c>gradient_noise_channel</c> and the noise branches of
/// <c>gradient_color</c> and <c>gradient_stop_opacity</c> in
/// .reference/src/core/blend_math.cpp. Photoshop's own random sequence is not known, so
/// the bands differ from Photoshop's; what matches is the character: roughness, the
/// per-channel ranges, the color model and transparency. Every value is a pure function
/// of the seed and the ramp position (splitmix64 hashing, no platform RNG).
/// </summary>
internal sealed class GradientNoise
{
    private readonly int[] _minimum;
    private readonly int[] _maximum;

    public GradientNoise(uint seed, int roughness, bool addTransparency, GradientNoiseModel model, int[] minimum, int[] maximum)
    {
        Seed = seed;
        Roughness = Math.Clamp(roughness, 0, 4096);
        AddTransparency = addTransparency;
        Model = model;
        _minimum = Normalize(minimum, 0);
        _maximum = Normalize(maximum, 100);
    }

    /// <summary><c>RndS</c>.</summary>
    public uint Seed { get; }

    /// <summary><c>Smth</c>, 0..4096 (Photoshop's roughness percent times 40.96).</summary>
    public int Roughness { get; }

    /// <summary><c>ShTr</c>: the fourth noise channel drives opacity.</summary>
    public bool AddTransparency { get; }

    public GradientNoiseModel Model { get; }

    /// <summary>Reads the noise keys of a <c>Grad</c> object (reference <c>parse_layer_style_gradient</c>).</summary>
    public static GradientNoise FromDescriptor(Descriptor grad)
    {
        var model = grad.GetEnum("ClrS") switch
        {
            "HSBl" or "HSB " or "HSBC" => GradientNoiseModel.Hsb,
            "LbCl" or "Lab " or "LABC" => GradientNoiseModel.Lab,
            _ => GradientNoiseModel.Rgb,
        };
        var seed = grad.GetNumber("RndS", 0);
        return new GradientNoise(
            double.IsFinite(seed) ? (uint)Math.Clamp(seed, 0, uint.MaxValue) : 0,
            (int)Math.Clamp(Math.Round(grad.GetNumber("Smth", 2048), MidpointRounding.AwayFromZero), 0, 4096),
            grad.GetBoolean("ShTr"),
            model,
            Range(grad, "Mnm ", 0),
            Range(grad, "Mxm ", 100));
    }

    /// <summary><c>gradient_noise_channel</c>: four octaves of smoothed value noise mapped into the channel's range.</summary>
    public double Channel(int channel, double position)
    {
        var roughness = Roughness / 4096.0;
        var total = 0.0;
        var weight = 0.0;
        var amplitude = 1.0;
        var frequency = 4.0 + (roughness * 60.0);
        for (var octave = 0; octave < 4; octave++)
        {
            total += SmoothNoise(unchecked(Seed + (uint)(octave * 977)), channel, position * frequency, roughness) * amplitude;
            weight += amplitude;
            amplitude *= 0.5;
            frequency *= 2.0;
        }

        var value = total / Math.Max(0.0001, weight);
        var index = Math.Clamp(channel, 0, 3);
        var minimum = _minimum[index] / 100.0;
        var maximum = _maximum[index] / 100.0;
        return Math.Clamp(minimum + ((maximum - minimum) * value), 0.0, 1.0);
    }

    /// <summary>The noise branch of <c>gradient_color</c>, as byte-rounded unit floats.</summary>
    public (float R, float G, float B) Color(double position)
    {
        var c0 = Channel(0, position);
        var c1 = Channel(1, position);
        var c2 = Channel(2, position);
        switch (Model)
        {
            case GradientNoiseModel.Hsb:
                {
                    var sector = c0 * 6.0;
                    var index = (int)Math.Floor(sector) % 6;
                    var fraction = sector - Math.Floor(sector);
                    var p = c2 * (1.0 - c1);
                    var q = c2 * (1.0 - (fraction * c1));
                    var t = c2 * (1.0 - ((1.0 - fraction) * c1));
                    var (r, g, b) = index switch
                    {
                        0 => (c2, t, p),
                        1 => (q, c2, p),
                        2 => (p, c2, t),
                        3 => (p, q, c2),
                        4 => (t, p, c2),
                        _ => (c2, p, q),
                    };
                    return (Unit(r), Unit(g), Unit(b));
                }

            case GradientNoiseModel.Lab:
                // The reference renders Lab noise through an OKLab-shaped approximation.
                return Gradient.Oklab.FromOklab(c0, c1 - 0.5, c2 - 0.5);
            default:
                return (Unit(c0), Unit(c1), Unit(c2));
        }
    }

    private static ulong SplitMix64(ulong value)
    {
        unchecked
        {
            value += 0x9e3779b97f4a7c15UL;
            value = (value ^ (value >> 30)) * 0xbf58476d1ce4e5b9UL;
            value = (value ^ (value >> 27)) * 0x94d049bb133111ebUL;
            return value ^ (value >> 31);
        }
    }

    private static double UnitHash(ulong value) => (SplitMix64(value) >> 11) * (1.0 / 9007199254740992.0);

    /// <summary><c>smooth_noise</c>: smoothstep-interpolated hashed lattice values, pulled toward 0.5 by low roughness.</summary>
    private static double SmoothNoise(uint seed, int channel, double coordinate, double roughness)
    {
        var floor = Math.Floor(coordinate);
        var cell = floor >= long.MaxValue - 1 ? long.MaxValue - 1 : floor <= long.MinValue ? long.MinValue : (long)floor;
        var fraction = coordinate - cell;
        var smooth = fraction * fraction * (3.0 - (2.0 * fraction));
        var salt = ((ulong)seed << 1) ^ unchecked((ulong)(channel + 1) * 0xd6e8feb86659fd93UL);
        var a = UnitHash(unchecked((ulong)cell) ^ salt);
        var b = UnitHash(unchecked((ulong)(cell + 1)) ^ salt);
        var value = a + ((b - a) * smooth);
        return 0.5 + ((value - 0.5) * Math.Clamp(roughness, 0.0, 1.0));
    }

    private static float Unit(double value) => Adjustments.ClampByte((float)(value * 255.0)) / 255f;

    private static int[] Normalize(int[] values, int fallback)
    {
        var result = new int[] { fallback, fallback, fallback, fallback };
        for (var i = 0; i < Math.Min(4, values.Length); i++)
        {
            result[i] = Math.Clamp(values[i], 0, 100);
        }

        return result;
    }

    /// <summary>
    /// <c>Mnm </c>/<c>Mxm </c>: four percents; Photoshop's PSDs store doubles (79.9988 for
    /// 80), GRD files longs (reference <c>gradient_noise_range</c>).
    /// </summary>
    private static int[] Range(Descriptor grad, string key, int fallback)
    {
        var result = new int[] { fallback, fallback, fallback, fallback };
        if (grad.GetList(key) is not { } list)
        {
            return result;
        }

        for (var i = 0; i < result.Length && i < list.Count; i++)
        {
            var item = list[i];
            if (item.Type is DescriptorValueType.Integer or DescriptorValueType.LargeInteger)
            {
                result[i] = (int)Math.Clamp(item.Integer, 0, 100);
            }
            else if (item.Type is DescriptorValueType.Double or DescriptorValueType.UnitFloat && double.IsFinite(item.Double))
            {
                result[i] = (int)Math.Clamp(Math.Round(item.Double, MidpointRounding.AwayFromZero), 0, 100);
            }
        }

        return result;
    }
}
