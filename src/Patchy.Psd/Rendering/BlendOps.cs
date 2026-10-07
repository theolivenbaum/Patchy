using System.Numerics;
using System.Runtime.CompilerServices;

namespace Patchy.Psd.Rendering;

/// <summary>
/// A blend function B(source, backdrop) over a vector of pixels. Implemented
/// as static abstract members so <see cref="BlendKernels"/> specializes one
/// tight SIMD loop per mode at JIT time with no per-pixel dispatch.
/// </summary>
internal interface IBlendOp
{
    static abstract void Apply(
        Vector<float> sr, Vector<float> sg, Vector<float> sb,
        Vector<float> dr, Vector<float> dg, Vector<float> db,
        out Vector<float> rr, out Vector<float> rg, out Vector<float> rb);
}

/// <summary>Per-channel blend function, lifted to RGB by <see cref="Separable{T}"/>.</summary>
internal interface IChannelBlend
{
    static abstract Vector<float> Blend(Vector<float> s, Vector<float> d);
}

internal readonly struct Separable<T> : IBlendOp
    where T : struct, IChannelBlend
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Apply(
        Vector<float> sr, Vector<float> sg, Vector<float> sb,
        Vector<float> dr, Vector<float> dg, Vector<float> db,
        out Vector<float> rr, out Vector<float> rg, out Vector<float> rb)
    {
        rr = T.Blend(sr, dr);
        rg = T.Blend(sg, dg);
        rb = T.Blend(sb, db);
    }
}

// The formulas follow Photoshop's 8-bit kernels from the reference compositor
// (blend_math.cpp) expressed on [0,1] floats: the 8-bit "128" split becomes
// 128/255, and Linear Light / Pin Light keep Photoshop's -256/255 offsets.
internal static class BlendConstants
{
    public static readonly Vector<float> Zero = Vector<float>.Zero;
    public static readonly Vector<float> One = Vector<float>.One;
    public static readonly Vector<float> Two = new(2f);
    public static readonly Vector<float> Half = new(128f / 255f);
    public static readonly Vector<float> Offset256 = new(256f / 255f);
    public static readonly Vector<float> Epsilon = new(1e-6f);
    public static readonly Vector<float> LumR = new(0.3f);
    public static readonly Vector<float> LumG = new(0.59f);
    public static readonly Vector<float> LumB = new(0.11f);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector<float> Clamp01(Vector<float> v) => Vector.Min(Vector.Max(v, Zero), One);
}

internal readonly struct NormalBlend : IChannelBlend
{
    public static Vector<float> Blend(Vector<float> s, Vector<float> d) => s;
}

internal readonly struct MultiplyBlend : IChannelBlend
{
    public static Vector<float> Blend(Vector<float> s, Vector<float> d) => s * d;
}

internal readonly struct ScreenBlend : IChannelBlend
{
    public static Vector<float> Blend(Vector<float> s, Vector<float> d) => s + d - (s * d);
}

internal readonly struct OverlayBlend : IChannelBlend
{
    public static Vector<float> Blend(Vector<float> s, Vector<float> d) => HardLightBlend.Blend(d, s);
}

internal readonly struct HardLightBlend : IChannelBlend
{
    public static Vector<float> Blend(Vector<float> s, Vector<float> d)
    {
        var low = BlendConstants.Two * s * d;
        var high = BlendConstants.One - (BlendConstants.Two * (BlendConstants.One - s) * (BlendConstants.One - d));
        return Vector.ConditionalSelect(Vector.LessThan(s, BlendConstants.Half), low, high);
    }
}

internal readonly struct SoftLightBlend : IChannelBlend
{
    public static Vector<float> Blend(Vector<float> s, Vector<float> d)
    {
        var one = BlendConstants.One;
        var two = BlendConstants.Two;
        var low = d - ((one - (two * s)) * d * (one - d));
        var dd = Vector.ConditionalSelect(
            Vector.LessThanOrEqual(d, new Vector<float>(0.25f)),
            ((((new Vector<float>(16f) * d) - new Vector<float>(12f)) * d) + new Vector<float>(4f)) * d,
            Vector.SquareRoot(d));
        var high = d + (((two * s) - one) * (dd - d));
        return Vector.ConditionalSelect(Vector.LessThanOrEqual(s, new Vector<float>(0.5f)), low, high);
    }
}

internal readonly struct DarkenBlend : IChannelBlend
{
    public static Vector<float> Blend(Vector<float> s, Vector<float> d) => Vector.Min(s, d);
}

internal readonly struct LightenBlend : IChannelBlend
{
    public static Vector<float> Blend(Vector<float> s, Vector<float> d) => Vector.Max(s, d);
}

internal readonly struct ColorDodgeBlend : IChannelBlend
{
    public static Vector<float> Blend(Vector<float> s, Vector<float> d)
    {
        var divisor = Vector.Max(BlendConstants.One - s, BlendConstants.Epsilon);
        var value = Vector.Min(BlendConstants.One, d / divisor);
        value = Vector.ConditionalSelect(Vector.GreaterThanOrEqual(s, BlendConstants.One), BlendConstants.One, value);
        return Vector.ConditionalSelect(Vector.LessThanOrEqual(d, BlendConstants.Zero), BlendConstants.Zero, value);
    }
}

internal readonly struct ColorBurnBlend : IChannelBlend
{
    public static Vector<float> Blend(Vector<float> s, Vector<float> d)
    {
        var divisor = Vector.Max(s, BlendConstants.Epsilon);
        var value = BlendConstants.One - Vector.Min(BlendConstants.One, (BlendConstants.One - d) / divisor);
        value = Vector.ConditionalSelect(Vector.LessThanOrEqual(s, BlendConstants.Zero), BlendConstants.Zero, value);
        return Vector.ConditionalSelect(Vector.GreaterThanOrEqual(d, BlendConstants.One), BlendConstants.One, value);
    }
}

internal readonly struct LinearBurnBlend : IChannelBlend
{
    public static Vector<float> Blend(Vector<float> s, Vector<float> d) => Vector.Max(BlendConstants.Zero, s + d - BlendConstants.One);
}

internal readonly struct LinearDodgeBlend : IChannelBlend
{
    public static Vector<float> Blend(Vector<float> s, Vector<float> d) => Vector.Min(BlendConstants.One, s + d);
}

internal readonly struct DifferenceBlend : IChannelBlend
{
    public static Vector<float> Blend(Vector<float> s, Vector<float> d) => Vector.Abs(d - s);
}

internal readonly struct ExclusionBlend : IChannelBlend
{
    public static Vector<float> Blend(Vector<float> s, Vector<float> d) => s + d - (BlendConstants.Two * s * d);
}

internal readonly struct SubtractBlend : IChannelBlend
{
    public static Vector<float> Blend(Vector<float> s, Vector<float> d) => Vector.Max(BlendConstants.Zero, d - s);
}

internal readonly struct DivideBlend : IChannelBlend
{
    public static Vector<float> Blend(Vector<float> s, Vector<float> d)
    {
        var value = Vector.Min(BlendConstants.One, d / Vector.Max(s, BlendConstants.Epsilon));
        value = Vector.ConditionalSelect(Vector.LessThanOrEqual(s, BlendConstants.Zero), BlendConstants.One, value);
        return Vector.ConditionalSelect(Vector.LessThanOrEqual(d, BlendConstants.Zero), BlendConstants.Zero, value);
    }
}

internal readonly struct VividLightBlend : IChannelBlend
{
    public static Vector<float> Blend(Vector<float> s, Vector<float> d)
    {
        var one = BlendConstants.One;
        var doubled = Vector.Max(BlendConstants.Two * s, BlendConstants.Epsilon);
        var burn = Vector.Max(BlendConstants.Zero, one - ((one - d) / doubled));
        var divisor = Vector.Max(BlendConstants.Two * (one - s), BlendConstants.Epsilon);
        var dodge = Vector.Min(one, d / divisor);
        var value = Vector.ConditionalSelect(Vector.LessThan(s, BlendConstants.Half), burn, dodge);
        value = Vector.ConditionalSelect(Vector.LessThanOrEqual(s, BlendConstants.Zero), BlendConstants.Zero, value);
        return Vector.ConditionalSelect(Vector.GreaterThanOrEqual(s, one), one, value);
    }
}

internal readonly struct LinearLightBlend : IChannelBlend
{
    public static Vector<float> Blend(Vector<float> s, Vector<float> d) =>
        BlendConstants.Clamp01(d + (BlendConstants.Two * s) - BlendConstants.Offset256);
}

internal readonly struct PinLightBlend : IChannelBlend
{
    public static Vector<float> Blend(Vector<float> s, Vector<float> d)
    {
        var two = BlendConstants.Two * s;
        var low = Vector.Min(d, two);
        var high = Vector.Max(d, Vector.Max(BlendConstants.Zero, two - BlendConstants.Offset256));
        return Vector.ConditionalSelect(Vector.LessThan(s, BlendConstants.Half), low, high);
    }
}

internal readonly struct HardMixBlend : IChannelBlend
{
    public static Vector<float> Blend(Vector<float> s, Vector<float> d)
    {
        // Photoshop thresholds the textbook vivid light at 127/255.
        var one = BlendConstants.One;
        var doubled = Vector.Max(BlendConstants.Two * s, BlendConstants.Epsilon);
        var burn = Vector.Max(BlendConstants.Zero, one - ((one - d) / doubled));
        burn = Vector.ConditionalSelect(Vector.LessThanOrEqual(s, BlendConstants.Zero), Vector.ConditionalSelect(Vector.GreaterThanOrEqual(d, one), one, BlendConstants.Zero), burn);
        var dodge = Vector.Min(one, d / Vector.Max(one - (BlendConstants.Two * (s - BlendConstants.Half)), BlendConstants.Epsilon));
        var vivid = Vector.ConditionalSelect(Vector.LessThan(s, BlendConstants.Half), burn, dodge);
        return Vector.ConditionalSelect(Vector.GreaterThan(vivid, new Vector<float>(127.5f / 255f)), one, BlendConstants.Zero);
    }
}

/// <summary>PDF-spec non-separable helpers (luma-weighted set_lum, clip_color and set_sat), vectorized.</summary>
internal static class NonSeparable
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector<float> Lum(Vector<float> r, Vector<float> g, Vector<float> b) =>
        (BlendConstants.LumR * r) + (BlendConstants.LumG * g) + (BlendConstants.LumB * b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector<float> Sat(Vector<float> r, Vector<float> g, Vector<float> b) =>
        Vector.Max(r, Vector.Max(g, b)) - Vector.Min(r, Vector.Min(g, b));

    public static void SetLum(ref Vector<float> r, ref Vector<float> g, ref Vector<float> b, Vector<float> l)
    {
        var delta = l - Lum(r, g, b);
        r += delta;
        g += delta;
        b += delta;
        ClipColor(ref r, ref g, ref b);
    }

    public static void ClipColor(ref Vector<float> r, ref Vector<float> g, ref Vector<float> b)
    {
        var l = Lum(r, g, b);
        var n = Vector.Min(r, Vector.Min(g, b));
        var x = Vector.Max(r, Vector.Max(g, b));
        var zero = BlendConstants.Zero;
        var one = BlendConstants.One;

        var lowMask = Vector.LessThan(n, zero) & Vector.GreaterThan(l - n, zero);
        var lowScale = l / Vector.Max(l - n, BlendConstants.Epsilon);
        r = Vector.ConditionalSelect(lowMask, l + ((r - l) * lowScale), r);
        g = Vector.ConditionalSelect(lowMask, l + ((g - l) * lowScale), g);
        b = Vector.ConditionalSelect(lowMask, l + ((b - l) * lowScale), b);

        x = Vector.Max(r, Vector.Max(g, b));
        var highMask = Vector.GreaterThan(x, one) & Vector.GreaterThan(x - l, zero);
        var highScale = (one - l) / Vector.Max(x - l, BlendConstants.Epsilon);
        r = Vector.ConditionalSelect(highMask, l + ((r - l) * highScale), r);
        g = Vector.ConditionalSelect(highMask, l + ((g - l) * highScale), g);
        b = Vector.ConditionalSelect(highMask, l + ((b - l) * highScale), b);
    }

    /// <summary>
    /// set_sat: the max channel becomes s, the min 0, the middle scales
    /// proportionally. Ties resolve identically to the sorted PDF algorithm.
    /// </summary>
    public static void SetSat(ref Vector<float> r, ref Vector<float> g, ref Vector<float> b, Vector<float> s)
    {
        var max = Vector.Max(r, Vector.Max(g, b));
        var min = Vector.Min(r, Vector.Min(g, b));
        var range = max - min;
        var hasRange = Vector.GreaterThan(range, BlendConstants.Zero);
        var scale = s / Vector.Max(range, BlendConstants.Epsilon);
        r = Vector.ConditionalSelect(hasRange, (r - min) * scale, BlendConstants.Zero);
        g = Vector.ConditionalSelect(hasRange, (g - min) * scale, BlendConstants.Zero);
        b = Vector.ConditionalSelect(hasRange, (b - min) * scale, BlendConstants.Zero);
    }
}

internal readonly struct HueBlend : IBlendOp
{
    public static void Apply(Vector<float> sr, Vector<float> sg, Vector<float> sb, Vector<float> dr, Vector<float> dg, Vector<float> db, out Vector<float> rr, out Vector<float> rg, out Vector<float> rb)
    {
        rr = sr;
        rg = sg;
        rb = sb;
        NonSeparable.SetSat(ref rr, ref rg, ref rb, NonSeparable.Sat(dr, dg, db));
        NonSeparable.SetLum(ref rr, ref rg, ref rb, NonSeparable.Lum(dr, dg, db));
    }
}

internal readonly struct SaturationBlend : IBlendOp
{
    public static void Apply(Vector<float> sr, Vector<float> sg, Vector<float> sb, Vector<float> dr, Vector<float> dg, Vector<float> db, out Vector<float> rr, out Vector<float> rg, out Vector<float> rb)
    {
        rr = dr;
        rg = dg;
        rb = db;
        NonSeparable.SetSat(ref rr, ref rg, ref rb, NonSeparable.Sat(sr, sg, sb));
        NonSeparable.SetLum(ref rr, ref rg, ref rb, NonSeparable.Lum(dr, dg, db));
    }
}

internal readonly struct ColorBlend : IBlendOp
{
    public static void Apply(Vector<float> sr, Vector<float> sg, Vector<float> sb, Vector<float> dr, Vector<float> dg, Vector<float> db, out Vector<float> rr, out Vector<float> rg, out Vector<float> rb)
    {
        rr = sr;
        rg = sg;
        rb = sb;
        NonSeparable.SetLum(ref rr, ref rg, ref rb, NonSeparable.Lum(dr, dg, db));
    }
}

internal readonly struct LuminosityBlend : IBlendOp
{
    public static void Apply(Vector<float> sr, Vector<float> sg, Vector<float> sb, Vector<float> dr, Vector<float> dg, Vector<float> db, out Vector<float> rr, out Vector<float> rg, out Vector<float> rb)
    {
        rr = dr;
        rg = dg;
        rb = db;
        NonSeparable.SetLum(ref rr, ref rg, ref rb, NonSeparable.Lum(sr, sg, sb));
    }
}

internal readonly struct DarkerColorBlend : IBlendOp
{
    public static void Apply(Vector<float> sr, Vector<float> sg, Vector<float> sb, Vector<float> dr, Vector<float> dg, Vector<float> db, out Vector<float> rr, out Vector<float> rg, out Vector<float> rb)
    {
        // Ties keep the backdrop.
        var pick = Vector.LessThan(NonSeparable.Lum(sr, sg, sb), NonSeparable.Lum(dr, dg, db));
        rr = Vector.ConditionalSelect(pick, sr, dr);
        rg = Vector.ConditionalSelect(pick, sg, dg);
        rb = Vector.ConditionalSelect(pick, sb, db);
    }
}

internal readonly struct LighterColorBlend : IBlendOp
{
    public static void Apply(Vector<float> sr, Vector<float> sg, Vector<float> sb, Vector<float> dr, Vector<float> dg, Vector<float> db, out Vector<float> rr, out Vector<float> rg, out Vector<float> rb)
    {
        var pick = Vector.GreaterThan(NonSeparable.Lum(sr, sg, sb), NonSeparable.Lum(dr, dg, db));
        rr = Vector.ConditionalSelect(pick, sr, dr);
        rg = Vector.ConditionalSelect(pick, sg, dg);
        rb = Vector.ConditionalSelect(pick, sb, db);
    }
}
