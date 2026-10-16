using XRay.Psd.Descriptors;
using XRay.Psd.Imaging;

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

/// <summary>
/// The document color model a Classic gradient interpolates in. Photoshop mixes stop
/// colors in the document's own channels: ink percentages in CMYK, gray in grayscale
/// and duotone, L*a*b* in Lab, the profile's RGB in RGB documents with a profile that
/// is not sRGB, and linear light in 32-bit RGB (the 4x4 colormodes and colorprofiles
/// files of psd-tools; at 32 bits the blue-to-red midpoint is (188, 0, 130), not the
/// (132, 0, 89) of an sRGB mix). sRGB documents mix the sRGB bytes, as the reference does.
/// </summary>
internal enum GradientColorModel
{
    Srgb,
    Ink,
    Gray,
    Lab,
    ProfileRgb,
    LinearRgb,
}

/// <summary>How Linear/Reflected gradients map bounds onto the ramp (see <c>GradientSpanBasis</c> in the reference blend_math).</summary>
internal enum GradientSpan
{
    /// <summary>Layer-style overlays and strokes: the center chord, with the center and half ramp snapped to whole pixels.</summary>
    LayerProjection,

    /// <summary>Gradient fill layers (<c>GdFl</c>): the center chord, with the gradient line's ends snapped to pixel centers.</summary>
    CenterChord,
}

/// <summary>Bounds a gradient is placed over, in document pixels (fractional for vector paths).</summary>
internal readonly record struct GradientBox(float Left, float Top, float Width, float Height)
{
    public static GradientBox From(PsdRect rect) => new(rect.Left, rect.Top, rect.Width, rect.Height);
}

/// <summary>
/// A gradient placement resolved once for a region: the ramp origin, the unit axis
/// (screen coordinates, y down) and the ramp length. <see cref="Position"/> is the
/// per-pixel part of the reference <c>gradient_position</c>.
/// </summary>
internal readonly struct GradientFrame
{
    private readonly GradientType _type;
    private readonly bool _reverse;
    private readonly float _originX;
    private readonly float _originY;
    private readonly float _axisX;
    private readonly float _axisY;
    private readonly float _length;
    private readonly bool _linearFromCenter;

    /// <summary>
    /// A frame with its origin at (<paramref name="originX"/>, <paramref name="originY"/>) and a
    /// unit axis (<paramref name="axisX"/>, <paramref name="axisY"/>). With
    /// <paramref name="linearFromCenter"/>, a Linear ramp is centered on the origin and runs
    /// <paramref name="length"/> to each side (layer styles); otherwise every type starts at the
    /// origin and reaches 1 at <paramref name="length"/>. <paramref name="type"/> and
    /// <paramref name="reverse"/> come from the gradient.
    /// </summary>
    public GradientFrame(GradientType type, bool reverse, float originX, float originY, float axisX, float axisY, float length, bool linearFromCenter)
    {
        _type = type;
        _reverse = reverse;
        _originX = originX;
        _originY = originY;
        _axisX = axisX;
        _axisY = axisY;
        _length = length;
        _linearFromCenter = linearFromCenter;
    }

    /// <summary>Ramp position (0..1) of document pixel (x, y), sampled at the pixel center.</summary>
    public float Position(int x, int y)
    {
        var dx = x + 0.5f - _originX;
        var dy = y + 0.5f - _originY;

        // The reference's rotated frame: local x along the axis, local y 90 degrees
        // clockwise from it on screen.
        var localX = (dx * _axisX) + (dy * _axisY);
        var localY = (dy * _axisX) - (dx * _axisY);
        float position;
        switch (_type)
        {
            case GradientType.Radial:
                position = MathF.Sqrt((localX * localX) + (localY * localY)) / _length;
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
                position = MathF.Abs(localX) / _length;
                break;
            case GradientType.Diamond:
                position = (MathF.Abs(localX) + MathF.Abs(localY)) / _length;
                break;
            default:
                position = _linearFromCenter ? 0.5f + (localX / (2f * _length)) : localX / _length;
                break;
        }

        if (_reverse)
        {
            position = 1f - position;
        }

        return Math.Clamp(position, 0f, 1f);
    }
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

    /// <summary>The model the color stops interpolate in (see <see cref="GradientColorModel"/>).</summary>
    public GradientColorModel ColorModel { get; private set; }

    // Stop components in ColorModel units, parallel to ColorStops; the conversion to sRGB;
    // and the sampled ramps (without and with endpoint smoothing), built on first use.
    private double[][]? _nativeStops;
    private DocumentColors? _colors;
    private float[]? _ramp;
    private float[]? _smoothRamp;
    private const int RampSize = 4096;

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

        var model = interpolation == GradientInterpolation.Classic && gradient.Noise is null ? NativeModel(effect.Colors) : GradientColorModel.Srgb;
        var parsed = new List<(GradientColorStop Stop, double[]? Native)>();
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
                parsed.Add((new GradientColorStop(
                    Math.Clamp((float)(stop.GetNumber("Lctn", 0) / 4096), 0f, 1f),
                    color,
                    Math.Clamp((float)(stop.GetNumber("Mdpn", 50) / 100), 0f, 1f)), NativeComponents(stop.GetObject("Clr "), model)));
            }
        }

        // Stable sorts keep coincident stops in file order.
        parsed = parsed.OrderBy(s => s.Stop.Location).ToList();
        gradient.ColorStops.AddRange(parsed.Select(s => s.Stop));
        if (model != GradientColorModel.Srgb && parsed.Count > 0 && parsed.All(s => s.Native is not null) && effect.Colors is { } documentColors)
        {
            gradient.ColorModel = model;
            gradient._nativeStops = parsed.Select(s => s.Native!).ToArray();
            gradient._colors = documentColors;
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

        var sortedAlpha = gradient.AlphaStops.OrderBy(s => s.Location).ToList();
        gradient.AlphaStops.Clear();
        gradient.AlphaStops.AddRange(sortedAlpha);
        return gradient;
    }

    /// <summary>Ramp position (0..1) of document pixel (x, y) for gradient placement over <paramref name="bounds"/>.</summary>
    public float Position(PsdRect bounds, int x, int y, GradientSpan basis) => Frame(GradientBox.From(bounds), basis).Position(x, y);

    /// <summary>
    /// The placement over <paramref name="bounds"/>, computed once so a fill loop
    /// only evaluates <see cref="GradientFrame.Position"/> per pixel.
    /// </summary>
    public GradientFrame Frame(GradientBox bounds, GradientSpan basis)
    {
        var centerX = bounds.Left + (bounds.Width * (0.5f + (OffsetXPercent / 100f)));
        var centerY = bounds.Top + (bounds.Height * (0.5f + (OffsetYPercent / 100f)));
        var radians = AngleDegrees * MathF.PI / 180f;
        var cos = MathF.Cos(radians);
        var sin = MathF.Sin(radians);
        var absCos = MathF.Abs(cos);
        var absSin = MathF.Abs(sin);

        // Both bases span the center chord through the bounds, min(w/|cos|, h/|sin|),
        // times Scale (reference blend_math.cpp, GradientSpanBasis::CenterChord). The
        // reference gives layer styles the corner-to-corner projection instead; the two
        // agree at the axis angles its overlay calibration used, and Photoshop's overlays
        // follow the chord: psd-tools' layer_effects.psd text runs its 87 degree overlay
        // over 60 px, its layer height, not the 89 px projection.
        var chord = MathF.Max(1f, MathF.Min(absCos > 1e-6f ? bounds.Width / absCos : float.PositiveInfinity, absSin > 1e-6f ? bounds.Height / absSin : float.PositiveInfinity));
        if (basis == GradientSpan.LayerProjection)
        {
            // Reference gradient_position: center snapped to a pixel center, whole-pixel half ramp.
            var halfRamp = MathF.Max(1f, MathF.Floor(chord * Scale * 0.5f));
            return new GradientFrame(Type, Reverse, MathF.Floor(centerX) + 0.5f, MathF.Floor(centerY) + 0.5f, cos, -sin, halfRamp, linearFromCenter: true);
        }

        // Gradient fill layers (GdFl). Photoshop snaps the gradient line's
        // ends to pixel centers: Linear runs from the snapped start of the chord to its
        // snapped end; Radial, Reflected, Diamond and Angle run from the snapped center
        // to the snapped end, and take their direction from those two points, so the
        // drawn angle is quantized. At a Scale other than 100% a Linear ramp mirrors its
        // snapped end about the snapped center instead. Fitted on psd-tools'
        // gradient-styles.psd (every type at 30, 60 reversed and -60 degrees at 50%),
        // gradient-sizes.psd, rgb-blend-modes.psd, masks3.psd and the 4x4 colormodes
        // files, whose 30 degree reflected fill Photoshop draws along the 45 degree
        // diagonal: the snapped center (2.5, 2.5) and end (4.5, 0.5) of the 4x4 canvas.
        var halfX = cos * chord * Scale * 0.5f;
        var halfY = -sin * chord * Scale * 0.5f;
        var endX = SnapToPixel(centerX + halfX);
        var endY = SnapToPixel(centerY + halfY);
        float startX;
        float startY;
        if (Type != GradientType.Linear)
        {
            startX = SnapToPixel(centerX);
            startY = SnapToPixel(centerY);
        }
        else if (MathF.Abs(Scale - 1f) < 1e-4f)
        {
            startX = SnapToPixel(centerX - halfX);
            startY = SnapToPixel(centerY - halfY);
        }
        else
        {
            startX = (2f * SnapToPixel(centerX)) - endX;
            startY = (2f * SnapToPixel(centerY)) - endY;
        }

        var axisX = endX - startX;
        var axisY = endY - startY;
        var length = MathF.Sqrt((axisX * axisX) + (axisY * axisY));
        if (length < 0.5f)
        {
            // Bounds too small for a snapped line: keep the exact geometry.
            var half = MathF.Max(0.25f, chord * Scale * 0.5f);
            return Type == GradientType.Linear
                ? new GradientFrame(Type, Reverse, centerX - (cos * half), centerY + (sin * half), cos, -sin, 2f * half, linearFromCenter: false)
                : new GradientFrame(Type, Reverse, centerX, centerY, cos, -sin, half, linearFromCenter: false);
        }

        return new GradientFrame(Type, Reverse, startX, startY, axisX / length, axisY / length, length, linearFromCenter: false);
    }

    /// <summary>
    /// Floors to the containing pixel and returns its center. The tolerance absorbs the
    /// fixed-point noise of path coordinates (an edge at 64 is stored as 63.99999).
    /// </summary>
    private static float SnapToPixel(float value) => MathF.Floor(value + 1e-3f) + 0.5f;

    /// <summary>Straight color at a ramp position, as [0,1] floats.</summary>
    public (float R, float G, float B) Color(float position, bool endpointSmoothing)
    {
        if (Noise is { } noise)
        {
            return noise.Color(position);
        }

        if (_nativeStops is not null)
        {
            var ramp = endpointSmoothing
                ? LazyInitializer.EnsureInitialized(ref _smoothRamp, () => NativeRamp(true))
                : LazyInitializer.EnsureInitialized(ref _ramp, () => NativeRamp(false));
            // NaN (from a damaged placement) maps to the first entry.
            var entry = 3 * (int)MathF.Round((position > 0f ? MathF.Min(position, 1f) : 0f) * (RampSize - 1));
            return (ramp[entry], ramp[entry + 1], ramp[entry + 2]);
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

    /// <summary>The model a document's descriptor gradients interpolate in.</summary>
    private static GradientColorModel NativeModel(DocumentColors? colors) => colors switch
    {
        null => GradientColorModel.Srgb,
        { ColorMode: PsdColorMode.Cmyk } => GradientColorModel.Ink,
        { ColorMode: PsdColorMode.Grayscale or PsdColorMode.Duotone } => GradientColorModel.Gray,
        { ColorMode: PsdColorMode.Lab } => GradientColorModel.Lab,
        { ColorMode: PsdColorMode.Rgb, Depth: 32 } => GradientColorModel.LinearRgb,
        { ColorMode: PsdColorMode.Rgb } when colors.TransformFor(3) is not null => GradientColorModel.ProfileRgb,
        _ => GradientColorModel.Srgb,
    };

    /// <summary>
    /// A stop color's components in <paramref name="model"/> units (ink and gray as
    /// fractions, Lab and RGB as stored; 32-bit RGB is stored as linear light), or null
    /// when the color is in another model.
    /// </summary>
    private static double[]? NativeComponents(Descriptor? color, GradientColorModel model)
    {
        if (color is null)
        {
            return null;
        }

        static bool Finite(double[] values) => values.All(double.IsFinite);
        double[]? components = model switch
        {
            GradientColorModel.Ink when color.TryGet("Cyn ", out _) =>
                [color.GetNumber("Cyn ", 0) / 100, color.GetNumber("Mgnt", 0) / 100, color.GetNumber("Ylw ", 0) / 100, color.GetNumber("Blck", 0) / 100],
            GradientColorModel.Gray when color.TryGet("Gry ", out _) => [1 - (color.GetNumber("Gry ", 0) / 100)],
            GradientColorModel.Lab when color.TryGet("Lmnc", out _) => [color.GetNumber("Lmnc", 0), color.GetNumber("A   ", 0), color.GetNumber("B   ", 0)],
            GradientColorModel.LinearRgb or GradientColorModel.ProfileRgb when color.TryGet("Rd  ", out _) => [color.GetNumber("Rd  ", 0), color.GetNumber("Grn ", 0), color.GetNumber("Bl  ", 0)],
            _ => null,
        };
        return components is not null && Finite(components) ? components : null;
    }

    /// <summary>
    /// Samples the ramp in the document model: the Classic segment interpolation of
    /// <see cref="Color"/> on unrounded stop components, then one conversion to sRGB.
    /// </summary>
    private float[] NativeRamp(bool endpointSmoothing)
    {
        var stops = ColorStops;
        var native = _nativeStops!;
        var colors = _colors!;
        var channels = native[0].Length;
        var smoothness = stops.Count > 2 || endpointSmoothing ? Smoothness : 0f;
        var ramp = new float[3 * RampSize];
        var mix = new double[channels];
        for (var i = 0; i < RampSize; i++)
        {
            var position = i / (float)(RampSize - 1);
            if (position <= stops[0].Location || stops.Count == 1)
            {
                native[0].CopyTo(mix, 0);
            }
            else if (position >= stops[^1].Location)
            {
                native[^1].CopyTo(mix, 0);
            }
            else
            {
                var index = 1;
                while (index < stops.Count - 1 && position > stops[index].Location)
                {
                    index++;
                }

                var left = stops[index - 1];
                var right = stops[index];
                var t = (position - left.Location) / MathF.Max(0.0001f, right.Location - left.Location);
                if (right.Midpoint != 0.5f)
                {
                    t = MidpointRemap(t, right.Midpoint);
                }

                var previous = native[index > 1 ? index - 2 : index - 1];
                var next = native[index + 1 < stops.Count ? index + 1 : index];
                for (var c = 0; c < channels; c++)
                {
                    var p1 = native[index - 1][c];
                    var p2 = native[index][c];
                    var linear = p1 + ((p2 - p1) * t);
                    var cubic = CatmullRom((float)previous[c], (float)p1, (float)p2, (float)next[c], t);
                    mix[c] = linear + ((cubic - linear) * smoothness);
                }
            }

            var color = ColorModel switch
            {
                GradientColorModel.Ink => colors.FromInk(mix[0], mix[1], mix[2], mix[3]),
                GradientColorModel.Gray when colors.Duotone is { } duotone => DuotoneColor(duotone, mix[0], colors),
                GradientColorModel.Gray => colors.FromGray(mix[0]),
                GradientColorModel.Lab => LabColor(mix[0], mix[1], mix[2]),
                GradientColorModel.ProfileRgb => colors.FromRgb(mix[0], mix[1], mix[2]),
                _ => colors.FromDescriptorRgb(Math.Max(0, mix[0]), Math.Max(0, mix[1]), Math.Max(0, mix[2])),
            };
            ramp[3 * i] = color.R / 255f;
            ramp[(3 * i) + 1] = color.G / 255f;
            ramp[(3 * i) + 2] = color.B / 255f;
        }

        return ramp;
    }

    private static PsdColor LabColor(double lightness, double a, double b)
    {
        var (r, g, bl) = ColorSpaces.LabToSrgb(lightness, a, b);
        return new PsdColor(Level(r), Level(g), Level(bl));
    }

    /// <summary>A gray value of a duotone document through its inks, like its pixels (<see cref="Duotone.Apply"/>).</summary>
    private static PsdColor DuotoneColor(Duotone duotone, double gray, DocumentColors colors)
    {
        var (r, g, b) = duotone.Evaluate(Math.Round(Math.Clamp(gray, 0, 1) * 255) / 255, colors.TransformFor(1));
        return new PsdColor(Level(r), Level(g), Level(b));
    }

    private static byte Level(double unit) => (byte)Math.Clamp(Math.Round(unit * 255), 0, 255);

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
