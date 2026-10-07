namespace Patchy.Psd.Rendering;

/// <summary>
/// Photoshop's eight "special Fill" blend modes, where Fill Opacity does not just
/// scale coverage the way Opacity does (reference <c>composite_special_fill_rgb</c>,
/// .reference/src/core/blend_math.cpp, and .reference/docs/blend-modes.md):
/// <list type="bullet">
/// <item>Color Burn, Linear Burn, Color Dodge, Linear Dodge and Difference move the source byte toward the mode's neutral (white for the burns, black otherwise) by Fill, then blend with Photoshop's 8-bit kernels (Burn and Dodge on a 256 scale here).</item>
/// <item>Vivid Light, Linear Light and Hard Mix fade their kernels' internal integer terms by the Fill byte.</item>
/// <item>The alpha split: the blend result carries the full <c>coverage x opacity</c> weight against the backdrop, while Fill scales only the output-alpha growth and the transparent-backdrop source term.</item>
/// </list>
/// The kernels are integer math on 8-bit colors; float planes are rounded to bytes first.
/// </summary>
internal static class SpecialFill
{
    /// <summary>Whether Fill below 100% takes the special path for <paramref name="mode"/> (reference <c>blend_mode_has_special_fill</c>).</summary>
    public static bool Applies(PsdBlendMode mode) => mode is PsdBlendMode.ColorBurn or PsdBlendMode.LinearBurn
        or PsdBlendMode.ColorDodge or PsdBlendMode.LinearDodge or PsdBlendMode.Difference
        or PsdBlendMode.VividLight or PsdBlendMode.LinearLight or PsdBlendMode.HardMix;

    /// <summary>The blended byte for one channel at a Fill byte (0-255).</summary>
    public static byte Blend(PsdBlendMode mode, byte source, byte destination, int fillByte)
    {
        switch (mode)
        {
            case PsdBlendMode.VividLight:
                return VividLight(source, destination, fillByte);
            case PsdBlendMode.LinearLight:
                return LinearLight(source, destination, fillByte);
            case PsdBlendMode.HardMix:
                return HardMix(source, destination, fillByte);
        }

        var fill = fillByte / 255f;
        var neutral = mode is PsdBlendMode.ColorBurn or PsdBlendMode.LinearBurn ? 255f : 0f;
        var adjusted = neutral + ((source - neutral) * fill);
        var s = Math.Clamp((int)MathF.Round(adjusted, MidpointRounding.AwayFromZero), 0, 255);
        int d = destination;
        switch (mode)
        {
            case PsdBlendMode.ColorBurn:
                {
                    var quotient = adjusted <= 0f ? 256 : (int)MathF.Floor((255f - d) * 256f / adjusted);
                    return (byte)Math.Clamp(255 - quotient, 0, 255);
                }

            case PsdBlendMode.ColorDodge:
                {
                    var divisor = 256f - adjusted;
                    var value = divisor <= 0f ? 255 : (int)MathF.Round(d * 256f / divisor, MidpointRounding.AwayFromZero);
                    return (byte)Math.Clamp(value, 0, 255);
                }

            case PsdBlendMode.LinearBurn:
                return (byte)Math.Clamp(s + d - 255, 0, 255);
            case PsdBlendMode.LinearDodge:
                return (byte)Math.Min(255, s + d);
            default:
                return (byte)Math.Abs(d - s);
        }
    }

    // floor(a/b) for signed a and positive b.
    private static int FloorDiv(int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);

    private static int NearestHalfUp(int a, int b) => ((2 * a) + b) / (2 * b);

    private static int NearestHalfDown(int a, int b) => ((2 * a) + b - 1) / (2 * b);

    private static int NearestHalfUpSigned(int a, int b) => FloorDiv((2 * a) + b, 2 * b);

    // result = clamp(d + round((2s - 255) * fb / 255) - 1); Fill 0 is identity.
    private static byte LinearLight(byte source, byte destination, int fillByte)
    {
        if (fillByte <= 0)
        {
            return destination;
        }

        var delta = NearestHalfUpSigned(((2 * source) - 255) * fillByte, 255) - 1;
        return (byte)Math.Clamp(destination + delta, 0, 255);
    }

    // Each half fades its 100%-Fill doubled term toward the half's neutral.
    private static byte VividLight(byte source, byte destination, int fillByte)
    {
        if (fillByte <= 0)
        {
            return destination;
        }

        if (source >= 128)
        {
            var doubled100 = NearestHalfUp((source - 128) * 255, 127);
            var faded = NearestHalfUp(doubled100 * fillByte, 255);
            var divisor = 255 - faded;
            return divisor <= 0 ? (byte)255 : (byte)Math.Min(255, NearestHalfUp(destination * 255, divisor));
        }

        var burn100 = NearestHalfUp(source * 255, 128);
        var doubled = 255 - NearestHalfUp((255 - burn100) * fillByte, 255);
        var numerator = destination + doubled - 255;
        if (numerator <= 0 || doubled <= 0)
        {
            return 0;
        }

        return (byte)Math.Min(255, NearestHalfDown(numerator * 255, doubled));
    }

    // A steep ramp: clamp(round((d - A) * 255 / (255 - fb2))), A = round((255 - s) * fb2 / 255).
    private static byte HardMix(byte source, byte destination, int fillByte)
    {
        var fb2 = fillByte - (fillByte >= 128 ? 1 : 0);
        if (fb2 <= 0)
        {
            return destination;
        }

        var anchor = NearestHalfUp((255 - source) * fb2, 255);
        var value = NearestHalfUpSigned((destination - anchor) * 255, 255 - fb2);
        return (byte)Math.Clamp(value, 0, 255);
    }

    /// <summary>
    /// Composites one row with the special-Fill split. <paramref name="coverage"/> is the
    /// source coverage (pixel alpha times masks and Blend If, without opacity or Fill).
    /// In <paramref name="clipMode"/> the destination is a clipping group: the pixel blends
    /// at full strength inside the clip and alpha follows <paramref name="clipCoverage"/>
    /// when given (unchanged otherwise).
    /// </summary>
    public static void CompositeRow(
        PsdBlendMode mode,
        ReadOnlySpan<float> sr, ReadOnlySpan<float> sg, ReadOnlySpan<float> sb, ReadOnlySpan<float> coverage,
        float fill, float opacity,
        Span<float> dr, Span<float> dg, Span<float> db, Span<float> da,
        bool clipMode, ReadOnlySpan<float> clipCoverage = default)
    {
        fill = Math.Clamp(fill, 0f, 1f);
        opacity = Math.Clamp(opacity, 0f, 1f);
        var fillByte = (int)MathF.Round(fill * 255f, MidpointRounding.AwayFromZero);
        for (var i = 0; i < coverage.Length; i++)
        {
            var c = Math.Clamp(coverage[i], 0f, 1f);
            var effective = c * fill * opacity;
            if (effective <= 0f)
            {
                continue;
            }

            var inside = clipCoverage.IsEmpty ? da[i] > 0f : clipCoverage[i] > 0f;
            if (clipMode && !inside)
            {
                continue;
            }

            var destinationAlpha = clipMode ? 1f : Math.Clamp(da[i], 0f, 1f);
            var overlap = c * opacity;
            var outAlpha = effective + (destinationAlpha * (1f - effective));
            var inverse = 1f / outAlpha;
            dr[i] = Channel(mode, sr[i], dr[i], fillByte, effective, overlap, destinationAlpha) * inverse;
            dg[i] = Channel(mode, sg[i], dg[i], fillByte, effective, overlap, destinationAlpha) * inverse;
            db[i] = Channel(mode, sb[i], db[i], fillByte, effective, overlap, destinationAlpha) * inverse;
            if (!clipMode)
            {
                da[i] = outAlpha;
            }
            else if (!clipCoverage.IsEmpty)
            {
                var clip = clipCoverage[i];
                var normalized = Math.Min(da[i], clip) / clip;
                da[i] = Math.Max(da[i], clip * (effective + (normalized * (1f - effective))));
            }
        }
    }

    // Premultiplied numerator of one channel: source-only + blended overlap + destination-only.
    private static float Channel(PsdBlendMode mode, float source, float destination, int fillByte, float effective, float overlap, float destinationAlpha)
    {
        var blended = Blend(mode, BlendIf.ToByte(source), BlendIf.ToByte(destination), fillByte) / 255f;
        return (source * effective * (1f - destinationAlpha))
            + (blended * overlap * destinationAlpha)
            + (destination * destinationAlpha * (1f - overlap));
    }
}
