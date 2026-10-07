namespace Patchy.Psd.Imaging.Icc;

/// <summary>
/// A one-dimensional ICC tone curve: <c>curv</c> (identity, a single gamma, or a
/// sampled table) or <c>para</c> (the five parametric function types). Inputs
/// and outputs are normalized to [0,1]; tables interpolate linearly.
/// </summary>
internal sealed class IccCurve
{
    private readonly float[]? _table;
    private readonly double _gamma;
    private readonly int _function = -1;
    private readonly double[] _parameters = [];

    private IccCurve(float[]? table, double gamma, int function, double[] parameters)
    {
        _table = table;
        _gamma = gamma;
        _function = function;
        _parameters = parameters;
    }

    public static IccCurve Identity { get; } = new(null, 1.0, -1, []);

    public static IccCurve FromGamma(double gamma) => new(null, gamma, -1, []);

    /// <summary>A sampled curve; entries are normalized outputs at evenly spaced inputs.</summary>
    public static IccCurve FromTable(float[] table) => table.Length < 2 ? Identity : new(table, 1.0, -1, []);

    /// <summary>A parametric curve (ICC <c>para</c> function type 0 to 4) with its g, a, b, c, d, e, f parameters.</summary>
    public static IccCurve FromParametric(int function, ReadOnlySpan<double> parameters)
    {
        var all = new double[7];
        parameters[..Math.Min(parameters.Length, 7)].CopyTo(all);
        return new(null, all[0], function, all);
    }

    /// <summary>True when the curve is exactly the identity (no table, unit gamma).</summary>
    public bool IsIdentity => _table is null && _function < 0 && _gamma == 1.0;

    /// <summary>Number of samples for a tabulated curve, else 0.</summary>
    public int TableLength => _table?.Length ?? 0;

    public double Evaluate(double x)
    {
        if (double.IsNaN(x))
        {
            x = 0;
        }

        x = Math.Clamp(x, 0.0, 1.0);
        if (_table is { } table)
        {
            var position = x * (table.Length - 1);
            var index = (int)position;
            if (index >= table.Length - 1)
            {
                return table[^1];
            }

            var fraction = position - index;
            return table[index] + ((table[index + 1] - table[index]) * fraction);
        }

        if (_function < 0)
        {
            return _gamma == 1.0 ? x : Math.Pow(x, _gamma);
        }

        var p = _parameters;
        double g = p[0], a = p[1], b = p[2], c = p[3], d = p[4], e = p[5], f = p[6];
        return _function switch
        {
            0 => Math.Pow(x, g),
            1 => Math.Abs(a) < 1e-9 ? 0 : x >= -b / a ? Power((a * x) + b, g) : 0,
            2 => Math.Abs(a) < 1e-9 ? c : x >= -b / a ? Power((a * x) + b, g) + c : c,
            3 => x >= d ? Power((a * x) + b, g) : c * x,
            4 => x >= d ? Power((a * x) + b, g) + e : (c * x) + f,
            _ => x,
        };
    }

    private static double Power(double value, double exponent) => value <= 0 ? 0 : Math.Pow(value, exponent);
}
