namespace Patchy.Psd;

/// <summary>Thrown when a file is not a PSD/PSB or its structure is damaged.</summary>
public sealed class PsdFormatException : Exception
{
    public PsdFormatException(string message)
        : base(message)
    {
    }

    public PsdFormatException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>Document color modes from the PSD header.</summary>
public enum PsdColorMode : ushort
{
    Bitmap = 0,
    Grayscale = 1,
    Indexed = 2,
    Rgb = 3,
    Cmyk = 4,
    Multichannel = 7,
    Duotone = 8,
    Lab = 9,
}

/// <summary>Channel image compression methods.</summary>
public enum PsdCompression : ushort
{
    Raw = 0,
    Rle = 1,
    Zip = 2,
    ZipPrediction = 3,
}

/// <summary>Photoshop layer blend modes. Values are stable; append only.</summary>
public enum PsdBlendMode
{
    Normal = 0,
    PassThrough,
    Dissolve,
    Darken,
    Multiply,
    ColorBurn,
    LinearBurn,
    DarkerColor,
    Lighten,
    Screen,
    ColorDodge,
    LinearDodge,
    LighterColor,
    Overlay,
    SoftLight,
    HardLight,
    VividLight,
    LinearLight,
    PinLight,
    HardMix,
    Difference,
    Exclusion,
    Subtract,
    Divide,
    Hue,
    Saturation,
    Color,
    Luminosity,
}

/// <summary>What a layer record represents.</summary>
public enum PsdLayerKind
{
    /// <summary>An ordinary raster layer.</summary>
    Pixel,

    /// <summary>A layer group (folder). Its children are in <see cref="Layers.PsdLayer.Children"/>.</summary>
    Group,

    /// <summary>A type layer; see <see cref="Layers.PsdLayer.Text"/>.</summary>
    Text,

    /// <summary>A shape or fill layer (solid color, gradient, or pattern fill, with an optional vector mask).</summary>
    Fill,

    /// <summary>An adjustment layer (curves, levels, hue/saturation and so on).</summary>
    Adjustment,

    /// <summary>A placed or embedded smart object.</summary>
    SmartObject,
}

/// <summary>Section divider types from the <c>lsct</c>/<c>lsdk</c> blocks.</summary>
public enum PsdSectionType
{
    None = 0,
    OpenFolder = 1,
    ClosedFolder = 2,
    BoundingDivider = 3,
}

/// <summary>An integer rectangle in document pixel coordinates (right and bottom exclusive).</summary>
public readonly record struct PsdRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Math.Max(0, Right - Left);

    public int Height => Math.Max(0, Bottom - Top);

    public bool IsEmpty => Width == 0 || Height == 0;

    public PsdRect Intersect(PsdRect other)
    {
        var left = Math.Max(Left, other.Left);
        var top = Math.Max(Top, other.Top);
        var right = Math.Min(Right, other.Right);
        var bottom = Math.Min(Bottom, other.Bottom);
        return right <= left || bottom <= top ? default : new PsdRect(left, top, right, bottom);
    }

    public PsdRect Union(PsdRect other)
    {
        if (IsEmpty)
        {
            return other;
        }

        if (other.IsEmpty)
        {
            return this;
        }

        return new PsdRect(Math.Min(Left, other.Left), Math.Min(Top, other.Top), Math.Max(Right, other.Right), Math.Max(Bottom, other.Bottom));
    }

    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;

    public override string ToString() => $"({Left},{Top})-({Right},{Bottom}) {Width}x{Height}";
}

/// <summary>An 8-bit sRGB color with straight alpha.</summary>
public readonly record struct PsdColor(byte R, byte G, byte B, byte A = 255)
{
    public static PsdColor White => new(255, 255, 255);

    public static PsdColor Black => new(0, 0, 0);

    public static PsdColor Transparent => new(0, 0, 0, 0);

    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}{(A == 255 ? string.Empty : A.ToString("X2", System.Globalization.CultureInfo.InvariantCulture))}";
}
