namespace Patchy.Psd;

/// <summary>An image resource block from the PSD image resources section.</summary>
public sealed record ImageResource(ushort Id, string Name, ReadOnlyMemory<byte> Data);

/// <summary>Well-known image resource IDs.</summary>
public static class ImageResourceIds
{
    public const ushort ResolutionInfo = 1005;
    public const ushort AlphaChannelNames = 1006;
    public const ushort Caption = 1008;
    public const ushort IptcNaa = 1028;
    public const ushort GridAndGuides = 1032;
    public const ushort Thumbnail = 1036;
    public const ushort GlobalAngle = 1037;
    public const ushort IccProfile = 1039;
    public const ushort UnicodeAlphaNames = 1045;
    public const ushort IndexedColorTableCount = 1046;
    public const ushort TransparencyIndex = 1047;
    public const ushort Slices = 1050;
    public const ushort AlphaIdentifiers = 1053;
    public const ushort VersionInfo = 1057;
    public const ushort Xmp = 1060;
    public const ushort PathFirst = 2000;
    public const ushort PathLast = 2997;
    public const ushort ClippingPathName = 2999;
    public const ushort WorkPath = 1025;
}

/// <summary>A linked or embedded smart-object source file from the <c>lnk2</c>/<c>lnkD</c>/<c>lnk3</c>/<c>lnkE</c> blocks.</summary>
public sealed record LinkedFile(string Id, string FileName, string FileType, string Kind, ReadOnlyMemory<byte> Data)
{
    /// <summary>True when the payload is itself a PSD or PSB document.</summary>
    public bool IsPhotoshopDocument => Data.Length >= 4 && Data.Span[0] == (byte)'8' && Data.Span[1] == (byte)'B' && Data.Span[2] == (byte)'P' && Data.Span[3] == (byte)'S';
}
