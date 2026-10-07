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

    /// <summary>Any other shape type, such as type on a path. The raw value is in <see cref="TextLayerInfo.ShapeType"/>.</summary>
    Other = 2,
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
