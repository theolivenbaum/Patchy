namespace XRay.Psd.Text;

/// <summary>A rectangle in text space (relative to the type layer's transform origin), in engine units.</summary>
public readonly record struct TextBounds(double Left, double Top, double Right, double Bottom)
{
    public double Width => Right - Left;

    public double Height => Bottom - Top;

    public bool IsEmpty => Width <= 0 || Height <= 0;
}

/// <summary>Photoshop anti-aliasing method for a type layer (descriptor <c>AntA</c>, EngineData <c>/AntiAlias</c>).</summary>
public enum TextAntiAlias
{
    None = 0,
    Crisp = 1,
    Strong = 2,
    Smooth = 3,
    Sharp = 4,
}

/// <summary>Capitalization of a style run (<c>/FontCaps</c>).</summary>
public enum TextCaps
{
    Normal = 0,
    SmallCaps = 1,
    AllCaps = 2,
}

/// <summary>Superscript and subscript position of a style run (<c>/FontBaseline</c>).</summary>
public enum TextBaselinePosition
{
    Normal = 0,
    Superscript = 1,
    Subscript = 2,
}

/// <summary>Base direction of a paragraph (<c>/ParagraphDirection</c>, Txt2 paragraph key 33).</summary>
public enum TextDirection
{
    /// <summary>Not stated: the first strong character decides (Unicode bidi default).</summary>
    Auto,
    LeftToRight,
    RightToLeft,
}

/// <summary>How the text is laid out: at a point, inside a box, or along a path.</summary>
public enum TextShapeKind
{
    /// <summary>Point text (<c>ShapeType 0</c>): lines break only at paragraph separators.</summary>
    Point = 0,

    /// <summary>Paragraph text in a box (<c>ShapeType 1</c>, <c>BoxBounds</c>).</summary>
    Box = 1,

    /// <summary>A shape type this reader does not know. The raw value is in <see cref="TextLayerInfo.ShapeType"/>.</summary>
    Other = 2,

    /// <summary>Type on a path (Txt2 frame type 2): one line laid along <see cref="TextLayerInfo.Path"/>.</summary>
    Path = 3,
}

/// <summary>A point in text space (engine units, y down).</summary>
public readonly record struct TextPoint(double X, double Y);

/// <summary>One cubic Bezier segment of a type path, in text space.</summary>
public readonly record struct TextPathSegment(TextPoint Start, TextPoint Control1, TextPoint Control2, TextPoint End)
{
    /// <summary>The point at parameter <paramref name="t"/> (0 to 1).</summary>
    public TextPoint PointAt(double t)
    {
        var u = 1 - t;
        var a = u * u * u;
        var b = 3 * u * u * t;
        var c = 3 * u * t * t;
        var d = t * t * t;
        return new TextPoint(
            (a * Start.X) + (b * Control1.X) + (c * Control2.X) + (d * End.X),
            (a * Start.Y) + (b * Control1.Y) + (c * Control2.Y) + (d * End.Y));
    }
}

/// <summary>
/// The path a type-on-a-path layer follows: the Txt2 frame's Bezier curve
/// (<c>/0/8/0[i]/0/1/0</c>, four points per cubic segment) in text space, and the
/// text range Photoshop's start and end markers set (<c>/2/6</c>).
/// </summary>
public sealed record TextPath
{
    /// <summary>The cubic segments in path order.</summary>
    public IReadOnlyList<TextPathSegment> Segments { get; init; } = [];

    /// <summary>
    /// Start marker as a path position: the segment index plus the curve parameter inside it
    /// (1.5 is halfway along the second segment's parameter). The text starts here.
    /// </summary>
    public double RangeStart { get; init; }

    /// <summary>End marker as a path position (see <see cref="RangeStart"/>). Glyphs past it are overset and not drawn.</summary>
    public double RangeEnd { get; init; }

    /// <summary>Flipped text (path data <c>/11/0</c>): the text runs the other way, on the other side of the path.</summary>
    public bool Flipped { get; init; }

    /// <summary>True when the last segment ends where the first starts.</summary>
    public bool IsClosed => Segments.Count > 0 && Segments[0].Start == Segments[^1].End;

    /// <summary>The extent of every segment's control polygon in text space.</summary>
    public TextBounds Bounds
    {
        get
        {
            if (Segments.Count == 0)
            {
                return default;
            }

            double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
            foreach (var segment in Segments)
            {
                foreach (var point in (ReadOnlySpan<TextPoint>)[segment.Start, segment.Control1, segment.Control2, segment.End])
                {
                    left = Math.Min(left, point.X);
                    top = Math.Min(top, point.Y);
                    right = Math.Max(right, point.X);
                    bottom = Math.Max(bottom, point.Y);
                }
            }

            return new TextBounds(left, top, right, bottom);
        }
    }

    /// <summary>The point at a path position (segment index plus parameter), clamped to the path.</summary>
    public TextPoint PointAt(double position)
    {
        if (Segments.Count == 0)
        {
            return default;
        }

        var clamped = double.IsFinite(position) ? Math.Clamp(position, 0, Segments.Count) : 0;
        var index = Math.Min((int)clamped, Segments.Count - 1);
        return Segments[index].PointAt(clamped - index);
    }

    /// <summary>
    /// Reads a Txt2 path frame (<c>/0/8/0[i]/0</c>): control points in groups of eight
    /// numbers, the text range, and the flip flag. Returns null without a whole segment.
    /// </summary>
    internal static TextPath? FromFrame(EngineValue? frame)
    {
        var numbers = new List<double>();
        foreach (var item in frame?.Get("1", "0")?.Items ?? [])
        {
            if (EngineStyles.Number(item) is not double value)
            {
                return null;
            }

            numbers.Add(value);
        }

        var segments = new List<TextPathSegment>(numbers.Count / 8);
        for (var i = 0; i + 8 <= numbers.Count; i += 8)
        {
            segments.Add(new TextPathSegment(
                new TextPoint(numbers[i], numbers[i + 1]),
                new TextPoint(numbers[i + 2], numbers[i + 3]),
                new TextPoint(numbers[i + 4], numbers[i + 5]),
                new TextPoint(numbers[i + 6], numbers[i + 7])));
        }

        if (segments.Count == 0)
        {
            return null;
        }

        var data = frame?["2"];
        var range = data?["6"];
        var start = EngineStyles.Number(range?[0]) is double s && s >= 0 && s <= segments.Count ? s : 0;
        var end = EngineStyles.Number(range?[1]) is double e && e >= 0 && e <= segments.Count ? e : segments.Count;
        return new TextPath
        {
            Segments = segments,
            RangeStart = start,
            RangeEnd = end,
            Flipped = EngineStyles.Flag(data?.Get("11", "0")) ?? false,
        };
    }
}

/// <summary>Which stored records contributed to a <see cref="TextLayerInfo"/>.</summary>
[Flags]
public enum TextDataOrigin
{
    None = 0,

    /// <summary>The layer's Photoshop 6+ <c>TySh</c> descriptor.</summary>
    TypeTool = 1,

    /// <summary>The <c>EngineData</c> inside the <c>TySh</c> descriptor.</summary>
    EngineData = 2,

    /// <summary>The document-level <c>Txt2</c> text engine block.</summary>
    TextEngineBlock = 4,

    /// <summary>A Photoshop 5.x <c>tySh</c> record.</summary>
    LegacyTypeTool = 8,
}

/// <summary>Warp Text style (<c>warpStyle</c>).</summary>
public enum TextWarpStyle
{
    /// <summary>A style token this reader does not know; see <see cref="TextWarp.StyleKey"/>.</summary>
    Unknown = -1,
    None = 0,
    Arc,
    ArcLower,
    ArcUpper,
    Arch,
    Bulge,
    ShellLower,
    ShellUpper,
    Flag,
    Wave,
    Fish,
    Rise,
    FishEye,
    Inflate,
    Squeeze,
    Twist,
    Custom,
}

/// <summary>
/// Warp Text settings from the <c>warp</c> descriptor that follows the text
/// descriptor in <c>TySh</c>. The warp acts over the layer's
/// <see cref="TextLayerInfo.Bounds"/> box.
/// </summary>
/// <param name="Style">The warp style.</param>
/// <param name="StyleKey">The stored <c>warpStyle</c> token (<c>warpArc</c>, <c>warpSqueeze</c>...).</param>
/// <param name="Value">Bend in percent, -100 to 100 (<c>warpValue</c>).</param>
/// <param name="Perspective">Horizontal distortion in percent (<c>warpPerspective</c>).</param>
/// <param name="PerspectiveOther">Vertical distortion in percent (<c>warpPerspectiveOther</c>).</param>
/// <param name="Orientation">Warp axis (<c>warpRotate</c>): horizontal or vertical.</param>
public sealed record TextWarp(
    TextWarpStyle Style,
    string StyleKey,
    double Value,
    double Perspective,
    double PerspectiveOther,
    TextOrientation Orientation)
{
    /// <summary>True when the warp bends nothing (style None, or every parameter zero).</summary>
    public bool IsIdentity => Style == TextWarpStyle.None || (Value == 0 && Perspective == 0 && PerspectiveOther == 0);

    internal static TextWarpStyle StyleFromKey(string? key) => key switch
    {
        null or "" or "warpNone" => TextWarpStyle.None,
        "warpArc" => TextWarpStyle.Arc,
        "warpArcLower" => TextWarpStyle.ArcLower,
        "warpArcUpper" => TextWarpStyle.ArcUpper,
        "warpArch" => TextWarpStyle.Arch,
        "warpBulge" => TextWarpStyle.Bulge,
        "warpShellLower" => TextWarpStyle.ShellLower,
        "warpShellUpper" => TextWarpStyle.ShellUpper,
        "warpFlag" => TextWarpStyle.Flag,
        "warpWave" => TextWarpStyle.Wave,
        "warpFish" => TextWarpStyle.Fish,
        "warpRise" => TextWarpStyle.Rise,
        "warpFisheye" => TextWarpStyle.FishEye,
        "warpInflate" => TextWarpStyle.Inflate,
        "warpSqueeze" => TextWarpStyle.Squeeze,
        "warpTwist" => TextWarpStyle.Twist,
        "warpCustom" => TextWarpStyle.Custom,
        _ => TextWarpStyle.Unknown,
    };
}
