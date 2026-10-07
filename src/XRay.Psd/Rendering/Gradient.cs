using XRay.Psd.Descriptors;

namespace XRay.Psd.Rendering;

internal enum GradientType
{
    Linear,
    Radial,
    Angle,
    Reflected,
    Diamond,

    /// <summary>Stroke-only: follows the stroke band's distance field.</summary>
    ShapeBurst,
}

internal enum GradientInterpolation
{
    Classic,
    Perceptual,
    Linear,
}

/// <summary>How Linear/Reflected gradients map bounds onto the ramp (see <c>GradientSpanBasis</c> in the reference blend_math).</summary>
internal enum GradientSpan
{
    /// <summary>Layer-style overlays: the corner-to-corner projection, with the center and half ramp snapped to whole pixels.</summary>
    LayerProjection,

    /// <summary>Gradient fill layers (<c>GdFl</c>): the center chord through the bounds, unsnapped.</summary>
    CenterChord,
}

internal readonly record struct GradientColorStop(float Location, PsdColor Color, float Midpoint);

internal readonly record struct GradientAlphaStop(float Location, float Opacity, float Midpoint);

/// <summary>
/// A solid (stop-based) gradient from a <c>Grad</c> descriptor plus its placement
/// (type, angle, scale, reverse, offset, alignment). Evaluation follows the
/// reference: <c>gradient_position</c>, <c>gradient_color</c> and
/// <c>gradient_stop_opacity</c> in .reference/src/core/blend_math.cpp. Noise
/// (<c>ClNs</c>) gradients evaluate the reference's deterministic noise field
/// (<see cref="GradientNoise"/>).
/// </summary>
internal sealed class Gradient
{
    public List<GradientColorStop> ColorStops { get; } = [];

    public List<GradientAlphaStop> AlphaStops { get; } = [];

    public float Smoothness { get; init; } = 1f;

    public GradientInterpolation Interpolation { get; init; }

    public GradientType Type { get; init; }

    public float AngleDegrees { get; init; } = 90f;

    public float Scale { get; init; } = 1f;

    public bool Reverse { get; init; }

    public bool AlignWithLayer { get; init; } = true;

    public float OffsetXPercent { get; init; }

    public float OffsetYPercent { get; init; }

    /// <summary>Noise settings when the gradient form is <c>ClNs</c>; null for stop gradients.</summary>
    public GradientNoise? Noise { get; init; }

    /// <param name="effect">The effect or fill descriptor that holds <c>Grad</c> and the placement keys.</param>
    /// <param name="defaultAngle">
    /// Angle used when <c>Angl</c> is missing: 90 for layer effects, 0 for fill and
    /// stroke content (reference <c>parse_fill_content</c>: Photoshop draws psd-tools'
    /// gradient-styles.psd noise fills, which omit the key, as vertical bands).
    /// </param>
    public static Gradient? FromDescriptor(Descriptor effect, double defaultAngle = 90)
    {
        var type = effect.GetEnum("Type") switch
        {
            "Rdl " => GradientType.Radial,
            "Angl" => GradientType.Angle,
            "Rflc" => GradientType.Reflected,
            "Dmnd" => GradientType.Diamond,
            "shapeburst" => GradientType.ShapeBurst,
            _ => GradientType.Linear,
        };
        var interpolation = (effect.GetEnum("gs99") ?? effect.GetEnum("gradientsInterpolationMethod")) switch
        {
            "perceptual" or "Perc" or "Smoo" => GradientInterpolation.Perceptual,
            "linear" or "Lnr " => GradientInterpolation.Linear,
            _ => GradientInterpolation.Classic,
        };
        var offset = effect.GetObject("Ofst");
        var grad = effect.GetObject("Grad");
        var gradient = new Gradient
        {
            Type = type,
            Interpolation = interpolation,
            AngleDegrees = (float)effect.GetNumber("Angl", defaultAngle),
            Scale = Math.Max(0.01f, (float)(effect.GetNumber("Scl ", 100) / 100)),
            Reverse = effect.GetBoolean("Rvrs"),
            AlignWithLayer = effect.GetBoolean("Algn", true),
            OffsetXPercent = (float)(offset?.GetNumber("Hrzn", 0) ?? 0),
            OffsetYPercent = (float)(offset?.GetNumber("Vrtc", 0) ?? 0),
            Smoothness = grad is null ? 1f : Math.Clamp((float)(grad.GetNumber("Intr", 4096) / 4096), 0f, 1f),
            Noise = grad is not null && grad.GetEnum("GrdF") == "ClNs" ? GradientNoise.FromDescriptor(grad) : null,
        };

        if (grad?.GetList("Clrs") is { } colors)
        {
            foreach (var item in colors)
            {
                if (item.Object is not { } stop)
                {
                    continue;
                }

                var kind = stop.GetEnum("Type");
                var color = stop.GetColor("Clr ") ?? (kind == "BckC" ? PsdColor.White : PsdColor.Black);
                gradient.ColorStops.Add(new GradientColorStop(
                    Math.Clamp((float)(stop.GetNumber("Lctn", 0) / 4096), 0f, 1f),
                    color,
                    Math.Clamp((float)(stop.GetNumber("Mdpn", 50) / 100), 0f, 1f)));
            }
        }

        if (grad?.GetList("Trns") is { } transparency)
        {
            foreach (var item in transparency)
            {
                if (item.Object is not { } stop)
                {
                    continue;
                }

                gradient.AlphaStops.Add(new GradientAlphaStop(
                    Math.Clamp((float)(stop.GetNumber("Lctn", 0) / 4096), 0f, 1f),
                    Math.Clamp((float)(stop.GetNumber("Opct", 100) / 100), 0f, 1f),
                    Math.Clamp((float)(stop.GetNumber("Mdpn", 50) / 100), 0f, 1f)));
            }
        }

        // Stable sorts keep coincident stops in file order.
        var sortedColors = gradient.ColorStops.OrderBy(s => s.Location).ToList();
        gradient.ColorStops.Clear();
        gradient.ColorStops.AddRange(sortedColors);
        var sortedAlpha = gradient.AlphaStops.OrderBy(s => s.Location).ToList();
        gradient.AlphaStops.Clear();
        gradient.AlphaStops.AddRange(sortedAlpha);
        return gradient;
    }

    /// <summary>Ramp position (0..1) of document pixel (x, y) for gradient placement over <paramref name="bounds"/>.</summary>
    public float Position(PsdRect bounds, int x, int y, GradientSpan basis)
    {
        var snap = basis == GradientSpan.LayerProjection;
        var centerX = bounds.Left + (bounds.Width * (0.5f + (OffsetXPercent / 100f)));
        var centerY = bounds.Top + (bounds.Height * (0.5f + (OffsetYPercent / 100f)));
        if (snap)
        {
            centerX = MathF.Floor(centerX) + 0.5f;
            centerY = MathF.Floor(centerY) + 0.5f;
        }

        var px = x + 0.5f;
        var py = y + 0.5f;
        var radians = AngleDegrees * MathF.PI / 180f;
        var cos = MathF.Cos(radians);
        var sin = MathF.Sin(radians);
        var localX = ((px - centerX) * cos) - ((py - centerY) * sin);
        var localY = ((px - centerX) * sin) + ((py - centerY) * cos);
        var absCos = MathF.Abs(cos);
        var absSin = MathF.Abs(sin);
        var span = basis == GradientSpan.CenterChord
            ? MathF.Max(1f, MathF.Min(absCos > 1e-6f ? bounds.Width / absCos : float.PositiveInfinity, absSin > 1e-6f ? bounds.Height / absSin : float.PositiveInfinity))
            : MathF.Max(1f, (absCos * bounds.Width) + (absSin * bounds.Height));
        var rawHalf = span * Scale * 0.5f;
        var halfRamp = snap ? MathF.Max(1f, MathF.Floor(rawHalf)) : MathF.Max(0.5f, rawHalf);
        float position;
        switch (Type)
        {
            case GradientType.Radial:
                position = MathF.Sqrt((localX * localX) + (localY * localY)) / halfRamp;
                break;
            case GradientType.Angle:
                if (localX == 0 && localY == 0)
                {
                    position = 0.25f;
                }
                else
                {
                    position = MathF.Atan2(localY, localX) / (2f * MathF.PI);
                    if (position < 0)
                    {
                        position += 1f;
                    }
                }

                break;
            case GradientType.Reflected:
                position = MathF.Abs(localX) / halfRamp;
                break;
            case GradientType.Diamond:
                position = (MathF.Abs(localX) + MathF.Abs(localY)) / halfRamp;
                break;
            default:
                position = 0.5f + (localX / (2f * halfRamp));
                break;
        }

        if (Reverse)
        {
            position = 1f - position;
        }

        return Math.Clamp(position, 0f, 1f);
    }

    /// <summary>Straight color at a ramp position, as [0,1] floats.</summary>
    public (float R, float G, float B) Color(float position, bool endpointSmoothing)
    {
        if (Noise is { } noise)
        {
            return noise.Color(position);
        }

        var stops = ColorStops;
        if (stops.Count == 0)
        {
            return (position, position, position);
        }

        if (position <= stops[0].Location)
        {
            return Unit(stops[0].Color);
        }

        if (position >= stops[^1].Location)
        {
            return Unit(stops[^1].Color);
        }

        for (var index = 1; index < stops.Count; index++)
        {
            var right = stops[index];
            var left = stops[index - 1];
            if (position > right.Location)
            {
                continue;
            }

            var span = MathF.Max(0.0001f, right.Location - left.Location);
            var t = (position - left.Location) / span;
            if (right.Midpoint != 0.5f)
            {
                t = MidpointRemap(t, right.Midpoint);
            }

            switch (Interpolation)
            {
                case GradientInterpolation.Linear:
                    return (
                        LinearMix(left.Color.R, right.Color.R, t),
                        LinearMix(left.Color.G, right.Color.G, t),
                        LinearMix(left.Color.B, right.Color.B, t));
                case GradientInterpolation.Perceptual:
                    return Oklab.Mix(left.Color, right.Color, t);
                default:
                    {
                        var previous = index > 1 ? stops[index - 2].Color : left.Color;
                        var next = index + 1 < stops.Count ? stops[index + 1].Color : right.Color;
                        var smoothness = stops.Count > 2 || endpointSmoothing ? Smoothness : 0f;
                        float Channel(byte p0, byte p1, byte p2, byte p3)
                        {
                            var linear = p1 + ((p2 - p1) * t);
                            var cubic = CatmullRom(p0, p1, p2, p3, t);
                            return Math.Clamp(MathF.Round(linear + ((cubic - linear) * smoothness)), 0f, 255f) / 255f;
                        }

                        return (
                            Channel(previous.R, left.Color.R, right.Color.R, next.R),
                            Channel(previous.G, left.Color.G, right.Color.G, next.G),
                            Channel(previous.B, left.Color.B, right.Color.B, next.B));
                    }
            }
        }

        return Unit(stops[^1].Color);
    }

    /// <summary>Opacity at a ramp position.</summary>
    public float Opacity(float position, bool endpointSmoothing)
    {
        if (Noise is { } noise)
        {
            return noise.AddTransparency ? (float)noise.Channel(3, position) : 1f;
        }

        var stops = AlphaStops;
        if (stops.Count == 0)
        {
            return 1f;
        }

        if (position <= stops[0].Location)
        {
            return stops[0].Opacity;
        }

        if (position >= stops[^1].Location)
        {
            return stops[^1].Opacity;
        }

        for (var index = 1; index < stops.Count; index++)
        {
            var right = stops[index];
            var left = stops[index - 1];
            if (position > right.Location)
            {
                continue;
            }

            var span = MathF.Max(0.0001f, right.Location - left.Location);
            var t = (position - left.Location) / span;
            if (right.Midpoint != 0.5f)
            {
                t = MidpointRemap(t, right.Midpoint);
            }

            if (endpointSmoothing && Smoothness > 0)
            {
                var previous = index > 1 ? stops[index - 2].Opacity : left.Opacity;
                var next = index + 1 < stops.Count ? stops[index + 1].Opacity : right.Opacity;
                var linear = left.Opacity + ((right.Opacity - left.Opacity) * t);
                var cubic = CatmullRom(previous, left.Opacity, right.Opacity, next, t);
                return Math.Clamp(linear + ((cubic - linear) * Smoothness), 0f, 1f);
            }

            return left.Opacity + ((right.Opacity - left.Opacity) * t);
        }

        return stops[^1].Opacity;
    }

    private static (float, float, float) Unit(PsdColor color) => (color.R / 255f, color.G / 255f, color.B / 255f);

    private static float MidpointRemap(float value, float midpoint)
    {
        value = Math.Clamp(value, 0f, 1f);
        midpoint = Math.Clamp(midpoint, 0.0001f, 0.9999f);
        return value <= midpoint ? 0.5f * value / midpoint : 0.5f + (0.5f * (value - midpoint) / (1f - midpoint));
    }

    private static float CatmullRom(float p0, float p1, float p2, float p3, float t)
    {
        var t2 = t * t;
        var t3 = t2 * t;
        return 0.5f * ((2f * p1) + ((-p0 + p2) * t) + (((2f * p0) - (5f * p1) + (4f * p2) - p3) * t2) + ((-p0 + (3f * p1) - (3f * p2) + p3) * t3));
    }

    private static float LinearMix(byte a, byte b, float t)
    {
        var la = SrgbToLinear(a / 255.0);
        var lb = SrgbToLinear(b / 255.0);
        return (float)Math.Clamp(Math.Round(Imaging.ColorSpaces.EncodeSrgb(la + ((lb - la) * t)) * 255), 0, 255) / 255f;
    }

    internal static double SrgbToLinear(double value)
    {
        value = Math.Clamp(value, 0, 1);
        return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }

    internal static class Oklab
    {
        public static (float, float, float) Mix(PsdColor a, PsdColor b, float t)
        {
            var (al, aa, ab) = ToOklab(a);
            var (bl, ba, bb) = ToOklab(b);
            return FromOklab(al + ((bl - al) * t), aa + ((ba - aa) * t), ab + ((bb - ab) * t));
        }

        private static (double L, double A, double B) ToOklab(PsdColor color)
        {
            var r = SrgbToLinear(color.R / 255.0);
            var g = SrgbToLinear(color.G / 255.0);
            var b = SrgbToLinear(color.B / 255.0);
            var l = Math.Cbrt((0.4122214708 * r) + (0.5363325363 * g) + (0.0514459929 * b));
            var m = Math.Cbrt((0.2119034982 * r) + (0.6806995451 * g) + (0.1073969566 * b));
            var s = Math.Cbrt((0.0883024619 * r) + (0.2817188376 * g) + (0.6299787005 * b));
            return (
                (0.2104542553 * l) + (0.7936177850 * m) - (0.0040720468 * s),
                (1.9779984951 * l) - (2.4285922050 * m) + (0.4505937099 * s),
                (0.0259040371 * l) + (0.7827717662 * m) - (0.8086757660 * s));
        }

        public static (float, float, float) FromOklab(double lightness, double a, double b)
        {
            var l = lightness + (0.3963377774 * a) + (0.2158037573 * b);
            var m = lightness - (0.1055613458 * a) - (0.0638541728 * b);
            var s = lightness - (0.0894841775 * a) - (1.2914855480 * b);
            l = l * l * l;
            m = m * m * m;
            s = s * s * s;
            var r = (4.0767416621 * l) - (3.3077115913 * m) + (0.2309699292 * s);
            var g = (-1.2684380046 * l) + (2.6097574011 * m) - (0.3413193965 * s);
            var bl = (-0.0041960863 * l) - (0.7034186147 * m) + (1.7076147010 * s);
            static float Encode(double v) => (float)Math.Clamp(Math.Round(Imaging.ColorSpaces.EncodeSrgb(Math.Clamp(v, 0, 1)) * 255), 0, 255) / 255f;
            return (Encode(r), Encode(g), Encode(bl));
        }
    }
}
