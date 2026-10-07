using XRay.Psd.Imaging.Icc;
using XRay.Psd.IO;

namespace XRay.Psd.Imaging;

/// <summary>One ink of a duotone specification.</summary>
/// <param name="Name">Ink name (Pascal string from the specification).</param>
/// <param name="Color">The ink at 100% as sRGB.</param>
/// <param name="Curve">The 13 transfer points (ink percent times 10 at 0, 5, 10, 20 ... 90, 95, 100% tone), -1 where unset.</param>
internal sealed record DuotoneInk(string Name, PsdColor Color, short[] Curve);

/// <summary>
/// Duotone rendering from the duotone specification in the color mode data.
/// The reference does not model duotones (it reads the gray plane, see
/// <c>.reference/docs/file-formats.md</c>), so this follows the layout of
/// Photoshop's Duotone Options file, which the color mode data repeats:
/// version (1), ink count (1 to 4), four 10-byte colors, four 64-byte Pascal
/// names, four 28-byte transfer functions (13 points, 0 to 1000 or -1, then an
/// override flag), dot gain and overprint colors (both ignored).
/// <para>
/// Each pixel's tone is <c>t = 1 - gray</c>. Ink <c>i</c> covers
/// <c>c_i = curve_i(t)</c>, a natural cubic spline through the set points like
/// Photoshop's curve dialogs. Its reflectance is the luminance of the gray
/// level <c>1 - c_i</c> through the document's gray profile (the plain sRGB
/// decoding without one), so a monotone black ink with a linear curve renders
/// exactly like the grayscale path. Inks multiply as filters in linear light,
/// <c>L = prod(1 - (1 - r_i)(1 - ink_i))</c> per sRGB channel, with no
/// overprint colors, and the result is sRGB-encoded.
/// </para>
/// </summary>
internal sealed class Duotone
{
    private const int MinimumLength = 2 + 2 + 40 + 256 + 112;
    private static readonly double[] CurveInputs = [0, 0.05, 0.10, 0.20, 0.30, 0.40, 0.50, 0.60, 0.70, 0.80, 0.90, 0.95, 1.0];

    private readonly object _gate = new();
    private float[]? _table8;
    private float[]? _table16;

    private readonly Spline[] _curves;

    private Duotone(IReadOnlyList<DuotoneInk> inks)
    {
        Inks = inks;
        _curves = [.. inks.Select(ink => new Spline(ink.Curve))];
    }

    public IReadOnlyList<DuotoneInk> Inks { get; }

    /// <summary>Parses the specification, or returns null when it is missing or not understood.</summary>
    public static Duotone? Parse(ReadOnlyMemory<byte> data)
    {
        if (data.Length < MinimumLength)
        {
            return null;
        }

        try
        {
            var reader = new BigEndianReader(data);
            var version = reader.ReadUInt16();
            var count = reader.ReadUInt16();
            if (version != 1 || count is < 1 or > 4)
            {
                return null;
            }

            var colors = new PsdColor[4];
            for (var i = 0; i < 4; i++)
            {
                colors[i] = ReadInkColor(reader);
            }

            var names = new string[4];
            for (var i = 0; i < 4; i++)
            {
                var name = reader.ReadSpan(64);
                names[i] = PsdParser.DecodeLatin1(name.Slice(1, Math.Min((int)name[0], 63)));
            }

            var inks = new DuotoneInk[count];
            for (var i = 0; i < 4; i++)
            {
                var curve = new short[13];
                for (var p = 0; p < 13; p++)
                {
                    curve[p] = reader.ReadInt16();
                }

                _ = reader.ReadUInt16(); // override flag
                if (i < count)
                {
                    inks[i] = new DuotoneInk(names[i], colors[i], curve);
                }
            }

            return new Duotone(inks);
        }
        catch (PsdFormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Converts a gray plane (0 = black, 1 = white) to RGB through the inks.
    /// Returns false for 32-bit data, which keeps the grayscale path.
    /// </summary>
    public bool Apply(float[] gray, int depth, IccSrgbTransform? grayTransform, PlanarImage image)
    {
        if (depth is not (8 or 16))
        {
            return false;
        }

        var table = GetTable(depth == 8 ? 256 : 65536, grayTransform);
        var max = (table.Length / 3) - 1;
        float[] r = image.R, g = image.G, b = image.B;
        for (var i = 0; i < gray.Length; i++)
        {
            var index = Math.Clamp((int)((gray[i] * max) + 0.5f), 0, max) * 3;
            r[i] = table[index];
            g[i] = table[index + 1];
            b[i] = table[index + 2];
        }

        return true;
    }

    /// <summary>The ink mix for one gray value (0 = black, 1 = white), as encoded sRGB.</summary>
    public (double R, double G, double B) Evaluate(double gray, IccSrgbTransform? grayTransform)
    {
        var tone = 1 - Math.Clamp(gray, 0, 1);
        double red = 1, green = 1, blue = 1;
        Span<double> device = stackalloc double[1];
        for (var i = 0; i < Inks.Count; i++)
        {
            var ink = Inks[i];
            var coverage = Math.Clamp(_curves[i].Evaluate(tone), 0, 1);
            device[0] = 1 - coverage;
            var reflectance = grayTransform is not null ? grayTransform.EvaluateLinear(device).R : IccPcs.DecodeSrgb(device[0]);
            var absorbed = 1 - Math.Clamp(reflectance, 0, 1);
            red *= 1 - (absorbed * (1 - IccPcs.DecodeSrgb(ink.Color.R / 255.0)));
            green *= 1 - (absorbed * (1 - IccPcs.DecodeSrgb(ink.Color.G / 255.0)));
            blue *= 1 - (absorbed * (1 - IccPcs.DecodeSrgb(ink.Color.B / 255.0)));
        }

        return (IccPcs.EncodeSrgb(red), IccPcs.EncodeSrgb(green), IccPcs.EncodeSrgb(blue));
    }

    /// <summary>
    /// Evaluates a 13-point transfer function at <paramref name="tone"/> (0 to 1): a
    /// natural cubic spline through the set points, with 0% and 100% defaulting to
    /// 0 and 100% ink when unset.
    /// </summary>
    internal static double EvaluateCurve(short[] curve, double tone) => new Spline(curve).Evaluate(tone);

    private float[] GetTable(int levels, IccSrgbTransform? grayTransform)
    {
        ref var slot = ref levels == 256 ? ref _table8 : ref _table16;
        var table = Volatile.Read(ref slot);
        if (table is null)
        {
            lock (_gate)
            {
                table = slot ?? BuildTable(levels, grayTransform);
                Volatile.Write(ref slot, table);
            }
        }

        return table;
    }

    private float[] BuildTable(int levels, IccSrgbTransform? grayTransform)
    {
        var table = new float[levels * 3];
        for (var i = 0; i < levels; i++)
        {
            var (r, g, b) = Evaluate(i / (double)(levels - 1), grayTransform);
            table[i * 3] = (float)r;
            table[(i * 3) + 1] = (float)g;
            table[(i * 3) + 2] = (float)b;
        }

        return table;
    }

    /// <summary>
    /// A 10-byte Photoshop color as sRGB: Lab (space 7: L 0 to 10000, a and b signed
    /// hundredths) here, the other spaces through <see cref="Text.LegacyText.ReadColor"/>.
    /// Color-book inks (Pantone and the like) cannot be resolved without the books
    /// and render as black.
    /// </summary>
    private static PsdColor ReadInkColor(BigEndianReader reader)
    {
        var start = reader.Position;
        var space = reader.ReadUInt16();
        if (space == 7)
        {
            var l = reader.ReadUInt16() / 100.0;
            var a = reader.ReadInt16() / 100.0;
            var b = reader.ReadInt16() / 100.0;
            reader.Skip(2);
            var (red, green, blue) = ColorSpaces.LabToSrgb(l, a, b);
            static byte Level(double v) => (byte)Math.Clamp(Math.Round(v * 255), 0, 255);
            return new PsdColor(Level(red), Level(green), Level(blue));
        }

        reader.Position = start;
        if (space is 0 or 1 or 2 or 8)
        {
            return Text.LegacyText.ReadColor(reader);
        }

        reader.Skip(10);
        return PsdColor.Black;
    }

    /// <summary>Natural cubic spline through a transfer function's set points (the Curves adjustment's <c>build_curve_lut</c> scheme).</summary>
    private sealed class Spline
    {
        private readonly double[] _x;
        private readonly double[] _y;
        private readonly double[] _second;

        public Spline(short[] curve)
        {
            var xs = new List<double>(13);
            var ys = new List<double>(13);
            for (var p = 0; p < 13; p++)
            {
                if (curve[p] is >= 0 and <= 1000)
                {
                    xs.Add(CurveInputs[p]);
                    ys.Add(curve[p] / 1000.0);
                }
                else if (p is 0 or 12)
                {
                    xs.Add(CurveInputs[p]);
                    ys.Add(p == 0 ? 0 : 1);
                }
            }

            _x = [.. xs];
            _y = [.. ys];
            var n = _x.Length;
            _second = new double[n];
            var work = new double[n];
            for (var i = 1; i + 1 < n; i++)
            {
                var sigma = (_x[i] - _x[i - 1]) / (_x[i + 1] - _x[i - 1]);
                var pivot = (sigma * _second[i - 1]) + 2.0;
                _second[i] = (sigma - 1.0) / pivot;
                var slopes = ((_y[i + 1] - _y[i]) / (_x[i + 1] - _x[i])) - ((_y[i] - _y[i - 1]) / (_x[i] - _x[i - 1]));
                work[i] = ((6.0 * slopes / (_x[i + 1] - _x[i - 1])) - (sigma * work[i - 1])) / pivot;
            }

            for (var i = n - 2; i >= 0; i--)
            {
                _second[i] = (_second[i] * _second[i + 1]) + work[i];
            }
        }

        public double Evaluate(double t)
        {
            var upper = 1;
            while (upper < _x.Length - 1 && t > _x[upper])
            {
                upper++;
            }

            var span = _x[upper] - _x[upper - 1];
            var a = (_x[upper] - t) / span;
            var b = (t - _x[upper - 1]) / span;
            return (a * _y[upper - 1]) + (b * _y[upper]) + (((((a * a * a) - a) * _second[upper - 1]) + (((b * b * b) - b) * _second[upper])) * span * span / 6.0);
        }
    }
}
