using XRay.Psd.Descriptors;
using XRay.Psd.IO;

namespace XRay.Psd.Layers;

/// <summary>Where a vector stroke sits relative to its path.</summary>
public enum VectorStrokeAlignment
{
    Center = 0,
    Inside = 1,
    Outside = 2,
}

/// <summary>End caps of open vector strokes and dashes.</summary>
public enum VectorStrokeCap
{
    Butt = 0,
    Round = 1,
    Square = 2,
}

/// <summary>Corner joins of vector strokes.</summary>
public enum VectorStrokeJoin
{
    Miter = 0,
    Round = 1,
    Bevel = 2,
}

/// <summary>
/// A shape layer's vector stroke and fill switch, from the <c>vstk</c> block
/// (descriptor class <c>strokeStyle</c>). Parsing follows
/// <c>parse_vector_stroke_block</c> in <c>.reference/src/psd/psd_vector.cpp</c>.
/// </summary>
public sealed class VectorStrokeStyle
{
    private const double MaxWidth = 100_000;

    /// <summary>Whether the stroke is drawn (<c>strokeEnabled</c>, default false).</summary>
    public bool Enabled { get; init; }

    /// <summary>Whether the shape's fill content is drawn (<c>fillEnabled</c>, default true).</summary>
    public bool FillEnabled { get; init; } = true;

    /// <summary>Stroke width in pixels (point widths convert through <c>strokeStyleResolution</c>).</summary>
    public double Width { get; init; }

    /// <summary>Miter limit as a ratio of the half width (Photoshop's default is 100).</summary>
    public double MiterLimit { get; init; } = 100;

    public VectorStrokeCap Cap { get; init; }

    public VectorStrokeJoin Join { get; init; }

    public VectorStrokeAlignment Alignment { get; init; }

    /// <summary>Alternating dash and gap lengths in stroke-width multiples; empty for a solid stroke.</summary>
    public IReadOnlyList<double> Dashes { get; init; } = [];

    /// <summary>Dash phase in stroke-width multiples.</summary>
    public double DashOffset { get; init; }

    public PsdBlendMode BlendMode { get; init; } = PsdBlendMode.Normal;

    /// <summary>Stroke opacity 0..1 (<c>strokeStyleOpacity</c>).</summary>
    public double Opacity { get; init; } = 1;

    /// <summary>
    /// The stroke paint (<c>strokeStyleContent</c>): a <c>solidColorLayer</c>,
    /// <c>gradientLayer</c> or <c>patternLayer</c> object, or null when absent.
    /// </summary>
    public Descriptor? Content { get; init; }

    /// <summary>Fill block key equivalent of <see cref="Content"/>: <c>SoCo</c>, <c>GdFl</c>, <c>PtFl</c>, or null.</summary>
    public string? ContentKey { get; init; }

    /// <summary>Parses a <c>vstk</c> payload; returns null when it is not a <c>strokeStyle</c> descriptor.</summary>
    internal static VectorStrokeStyle? Parse(ReadOnlyMemory<byte> data)
    {
        Descriptor descriptor;
        try
        {
            // Vector descriptor blocks start with descriptor version 16, or with a
            // block version followed by 16 (reference read_block_descriptor).
            var reader = new BigEndianReader(data);
            if (reader.Remaining < 8)
            {
                return null;
            }

            if (reader.ReadUInt32() != 16 && reader.ReadUInt32() != 16)
            {
                return null;
            }

            descriptor = Descriptor.Read(reader);
        }
        catch (PsdFormatException)
        {
            return null;
        }

        if (descriptor.ClassId != "strokeStyle")
        {
            return null;
        }

        var resolution = descriptor.GetNumber("strokeStyleResolution", 72);
        if (!double.IsFinite(resolution) || resolution <= 0)
        {
            resolution = 72;
        }

        var width = Sanitize(LengthInPixels(descriptor["strokeStyleLineWidth"], resolution), 0, MaxWidth);
        var dashes = new List<double>();
        if (descriptor.GetList("strokeStyleLineDashSet") is { } list)
        {
            foreach (var entry in list)
            {
                var value = entry.AsNumber();
                if (double.IsFinite(value))
                {
                    dashes.Add(Math.Clamp(value, 0, MaxWidth));
                }
            }
        }

        var offset = LengthInPixels(descriptor["strokeStyleLineDashOffset"], resolution);
        var content = descriptor.GetObject("strokeStyleContent");
        var contentKey = content?.ClassId switch
        {
            "solidColorLayer" => "SoCo",
            "gradientLayer" => "GdFl",
            "patternLayer" => "PtFl",
            _ => null,
        };

        return new VectorStrokeStyle
        {
            Enabled = descriptor.GetBoolean("strokeEnabled", false),
            FillEnabled = descriptor.GetBoolean("fillEnabled", true),
            Width = width,
            MiterLimit = Sanitize(descriptor.GetNumber("strokeStyleMiterLimit", 100), 1, 1e6),
            Cap = descriptor.GetEnum("strokeStyleLineCapType") switch
            {
                "strokeStyleRoundCap" => VectorStrokeCap.Round,
                "strokeStyleSquareCap" => VectorStrokeCap.Square,
                _ => VectorStrokeCap.Butt,
            },
            Join = descriptor.GetEnum("strokeStyleLineJoinType") switch
            {
                "strokeStyleRoundJoin" => VectorStrokeJoin.Round,
                "strokeStyleBevelJoin" => VectorStrokeJoin.Bevel,
                _ => VectorStrokeJoin.Miter,
            },
            Alignment = descriptor.GetEnum("strokeStyleLineAlignment") switch
            {
                "strokeStyleAlignInside" => VectorStrokeAlignment.Inside,
                "strokeStyleAlignOutside" => VectorStrokeAlignment.Outside,
                _ => VectorStrokeAlignment.Center,
            },
            Dashes = dashes,
            DashOffset = width > 0 && double.IsFinite(offset) ? Math.Clamp(offset / width, -1e6, 1e6) : 0,
            BlendMode = descriptor.GetEnum("strokeStyleBlendMode") is { } mode ? BlendModeKeys.FromDescriptorEnum(mode) : PsdBlendMode.Normal,
            Opacity = Sanitize(descriptor.GetNumber("strokeStyleOpacity", 100), 0, 100) / 100,
            Content = contentKey is null ? null : content,
            ContentKey = contentKey,
        };
    }

    private static double Sanitize(double value, double min, double max) => double.IsFinite(value) ? Math.Clamp(value, min, max) : min;

    // Point lengths scale by resolution / 72; pixel and unitless values pass through.
    private static double LengthInPixels(DescriptorValue? value, double resolution)
    {
        if (value is null)
        {
            return 0;
        }

        var number = value.AsNumber();
        if (double.IsNaN(number))
        {
            return 0;
        }

        return value.Type == DescriptorValueType.UnitFloat && value.Unit == "#Pnt" ? number * resolution / 72.0 : number;
    }
}
