using System.Text;
using XRay.Psd.Descriptors;
using XRay.Psd.Imaging.Icc;
using XRay.Psd.IO;

namespace XRay.Psd.Resources;

/// <summary>
/// Typed views of the common image resources, parsed on first access. Each
/// property is null when the document has no such resource or it does not
/// decode; a damaged resource never throws. The raw blocks stay available in
/// <see cref="PsdDocument.ImageResources"/>.
/// </summary>
public sealed class PsdImageResources
{
    private readonly PsdDocument _document;
    private readonly Lazy<PsdResolutionInfo?> _resolution;
    private readonly Lazy<PsdGridAndGuides?> _gridAndGuides;
    private readonly Lazy<PsdThumbnail?> _thumbnail;
    private readonly Lazy<PsdSlices?> _slices;
    private readonly Lazy<PsdLayerComps?> _layerComps;
    private readonly Lazy<PsdPrintScale?> _printScale;
    private readonly Lazy<double?> _pixelAspectRatio;
    private readonly Lazy<PsdVersionInfo?> _versionInfo;
    private readonly Lazy<PsdIccProfile?> _iccProfile;
    private readonly Lazy<string?> _xmp;
    private readonly Lazy<IReadOnlyList<PsdIptcDataSet>> _iptc;
    private readonly Lazy<IReadOnlyList<string>> _channelNames;

    internal PsdImageResources(PsdDocument document)
    {
        _document = document;
        const LazyThreadSafetyMode mode = LazyThreadSafetyMode.PublicationOnly;
        _resolution = new(() => Parse(ImageResourceIds.ResolutionInfo, ParseResolution), mode);
        _gridAndGuides = new(() => Parse(ImageResourceIds.GridAndGuides, ParseGridAndGuides), mode);
        _thumbnail = new(ParseThumbnail, mode);
        _slices = new(() => Parse(ImageResourceIds.Slices, ParseSlices), mode);
        _layerComps = new(() => Parse(ImageResourceIds.LayerComps, ParseLayerComps), mode);
        _printScale = new(() => Parse(ImageResourceIds.PrintScale, ParsePrintScale), mode);
        _pixelAspectRatio = new(ParsePixelAspectRatio, mode);
        _versionInfo = new(() => Parse(ImageResourceIds.VersionInfo, ParseVersionInfo), mode);
        _iccProfile = new(ParseIccProfile, mode);
        _xmp = new(ParseXmp, mode);
        _iptc = new(ParseIptc, mode);
        _channelNames = new(() => Text.TextExtractor.ChannelNames(_document), mode);
    }

    /// <summary>Print resolution (1005).</summary>
    public PsdResolutionInfo? Resolution => _resolution.Value;

    /// <summary>Grid spacing and ruler guides (1032).</summary>
    public PsdGridAndGuides? GridAndGuides => _gridAndGuides.Value;

    /// <summary>The thumbnail (1036, or the Photoshop 4 resource 1033 when 1036 is absent).</summary>
    public PsdThumbnail? Thumbnail => _thumbnail.Value;

    /// <summary>Web slices (1050).</summary>
    public PsdSlices? Slices => _slices.Value;

    /// <summary>Layer comps (1065).</summary>
    public PsdLayerComps? LayerComps => _layerComps.Value;

    /// <summary>Print scale and placement (1062).</summary>
    public PsdPrintScale? PrintScale => _printScale.Value;

    /// <summary>Pixel aspect ratio, width over height (1064); 1 for square pixels.</summary>
    public double? PixelAspectRatio => _pixelAspectRatio.Value;

    /// <summary>Version information (1057): writer and reader names and whether the merged image is real.</summary>
    public PsdVersionInfo? VersionInfo => _versionInfo.Value;

    /// <summary>The embedded ICC profile (1039) with its description.</summary>
    public PsdIccProfile? IccProfile => _iccProfile.Value;

    /// <summary>The XMP packet (1060) as XML text.</summary>
    public string? Xmp => _xmp.Value;

    /// <summary>IPTC-NAA datasets (1028) in file order; empty when absent.</summary>
    public IReadOnlyList<PsdIptcDataSet> Iptc => _iptc.Value;

    /// <summary>Alpha channel names (Unicode 1045, else Pascal 1006); empty when absent.</summary>
    public IReadOnlyList<string> ChannelNames => _channelNames.Value;

    private T? Parse<T>(ushort id, Func<BigEndianReader, T?> parse)
        where T : class
    {
        if (_document.GetImageResource(id) is not { } resource)
        {
            return null;
        }

        try
        {
            return parse(new BigEndianReader(resource.Data));
        }
        catch (PsdFormatException)
        {
            return null;
        }
    }

    // .reference/src/psd/psd_image_resources.cpp (print_settings_from_resolution_resource):
    // 16.16 unsigned resolutions, always pixels per inch; non-positive values read as 300.
    private static PsdResolutionInfo? ParseResolution(BigEndianReader reader)
    {
        if (reader.Remaining < 16)
        {
            return null;
        }

        var horizontal = SanitizedPpi(reader.ReadUInt32() / 65536.0);
        var horizontalUnit = (PsdResolutionUnit)Unit(reader.ReadUInt16(), 2);
        var widthUnit = (PsdDimensionUnit)Unit(reader.ReadUInt16(), 5);
        var vertical = SanitizedPpi(reader.ReadUInt32() / 65536.0);
        var verticalUnit = (PsdResolutionUnit)Unit(reader.ReadUInt16(), 2);
        var heightUnit = (PsdDimensionUnit)Unit(reader.ReadUInt16(), 5);
        return new PsdResolutionInfo(horizontal, horizontalUnit, widthUnit, vertical, verticalUnit, heightUnit);

        static double SanitizedPpi(double value) => double.IsFinite(value) && value > 0 ? value : 300.0;

        static int Unit(ushort value, int max) => value <= max ? value : 0;
    }

    // .reference/src/psd/psd_image_resources.cpp (grid_guides_from_resource): version 1,
    // cycles and positions in 1/32 pixel, a guide count bounded by the payload.
    // Grid cycles of 0 or less read as the default 576 (18 px). Guide positions keep
    // their sign (the reference clamps them to 0 only for its document model).
    private static PsdGridAndGuides? ParseGridAndGuides(BigEndianReader reader)
    {
        const int DefaultGridCycle32 = 576;
        if (reader.Remaining < 16 || reader.ReadUInt32() != 1)
        {
            return null;
        }

        var horizontal = reader.ReadInt32();
        var vertical = reader.ReadInt32();
        var count = reader.ReadUInt32();
        if (count > (uint)(reader.Remaining / 5))
        {
            return null;
        }

        var guides = new PsdGuide[count];
        for (var i = 0; i < guides.Length; i++)
        {
            var position = reader.ReadInt32();
            var direction = reader.ReadByte();
            guides[i] = new PsdGuide(position / 32.0, direction == 1 ? PsdGuideOrientation.Horizontal : PsdGuideOrientation.Vertical);
        }

        return new PsdGridAndGuides(
            (horizontal > 0 ? horizontal : DefaultGridCycle32) / 32.0,
            (vertical > 0 ? vertical : DefaultGridCycle32) / 32.0,
            guides);
    }

    // Thumbnail resource: format, width, height, row bytes, total size, compressed
    // size (u32 each), bits per pixel, planes (u16 each), then the payload.
    private PsdThumbnail? ParseThumbnail()
    {
        var legacy = _document.GetImageResource(ImageResourceIds.Thumbnail) is null;
        return Parse(legacy ? ImageResourceIds.ThumbnailLegacy : ImageResourceIds.Thumbnail, reader =>
        {
            var format = reader.ReadUInt32();
            var width = reader.ReadUInt32();
            var height = reader.ReadUInt32();
            var rowBytes = reader.ReadUInt32();
            _ = reader.ReadUInt32(); // total size
            var compressed = reader.ReadUInt32();
            var bits = reader.ReadUInt16();
            _ = reader.ReadUInt16(); // planes
            if (format > 1 || width is 0 or > 65535 || height is 0 or > 65535 || rowBytes > int.MaxValue)
            {
                return null;
            }

            var length = format == 1 ? Math.Min(compressed, (uint)reader.Remaining) : (uint)reader.Remaining;
            if (format == 1 && compressed == 0)
            {
                length = (uint)reader.Remaining;
            }

            var data = reader.ReadMemory((long)length);
            return new PsdThumbnail((PsdThumbnailFormat)format, (int)width, (int)height, (int)rowBytes, bits, legacy, data);
        });
    }

    private static PsdSlices? ParseSlices(BigEndianReader reader)
    {
        var version = reader.ReadUInt32();
        if (version is 7 or 8)
        {
            return SlicesFromDescriptor((int)version, Descriptor.ReadVersioned(reader));
        }

        if (version != 6)
        {
            return null;
        }

        // Version 6 (Adobe "Slices resource format"): group bounds, group name, slice records.
        var bounds = ReadRect(reader, topLeftFirst: true);
        var groupName = reader.ReadUnicodeString();
        var count = reader.ReadUInt32();
        var slices = new List<PsdSlice>();
        for (var i = 0u; i < count && reader.Remaining > 0; i++)
        {
            try
            {
                slices.Add(ReadSliceV6(reader));
            }
            catch (PsdFormatException)
            {
                break; // keep the slices that decoded
            }
        }

        return new PsdSlices(6, bounds, groupName, slices, null);
    }

    private static PsdSlice ReadSliceV6(BigEndianReader reader)
    {
        var id = reader.ReadInt32();
        var groupId = reader.ReadInt32();
        var origin = reader.ReadUInt32();
        var layerId = origin == 1 ? reader.ReadInt32() : 0;
        var name = reader.ReadUnicodeString();
        var type = reader.ReadUInt32();
        var left = reader.ReadInt32();
        var top = reader.ReadInt32();
        var right = reader.ReadInt32();
        var bottom = reader.ReadInt32();
        var slice = new PsdSlice
        {
            Id = id,
            GroupId = groupId,
            Origin = origin <= 2 ? (PsdSliceOrigin)origin : PsdSliceOrigin.UserGenerated,
            AssociatedLayerId = layerId,
            Name = name,
            Type = type == 0 ? PsdSliceType.NoImage : PsdSliceType.Image,
            Bounds = new PsdRect(left, top, right, bottom),
            Url = reader.ReadUnicodeString(),
            Target = reader.ReadUnicodeString(),
            Message = reader.ReadUnicodeString(),
            AltTag = reader.ReadUnicodeString(),
            CellTextIsHtml = reader.ReadByte() != 0,
            CellText = reader.ReadUnicodeString(),
            HorizontalAlignment = reader.ReadInt32(),
            VerticalAlignment = reader.ReadInt32(),
        };
        var alpha = reader.ReadByte();
        var red = reader.ReadByte();
        var green = reader.ReadByte();
        var blue = reader.ReadByte();
        slice = slice with { BackgroundColor = new PsdColor(red, green, blue, alpha) };

        // Photoshop 7 and later append a descriptor (version 16) to each record.
        // There is no length; like psd-tools, peek for the version and rewind
        // when what follows does not read as a descriptor.
        if (reader.Remaining >= 8)
        {
            var start = reader.Position;
            if (reader.ReadUInt32() == 16)
            {
                try
                {
                    _ = DescriptorReader.ReadDescriptor(reader, 0);
                    return slice;
                }
                catch (PsdFormatException)
                {
                }
            }

            reader.Position = start;
        }

        return slice;
    }

    private static PsdSlices SlicesFromDescriptor(int version, Descriptor root)
    {
        var slices = new List<PsdSlice>();
        foreach (var item in root.GetList("slices") ?? [])
        {
            if (item.Object is not { } d)
            {
                continue;
            }

            var background = d.GetObject("bgColor");
            slices.Add(new PsdSlice
            {
                Id = (int)d.GetNumber("sliceID", 0),
                GroupId = (int)d.GetNumber("groupID", 0),
                Origin = d.GetEnum("origin") switch
                {
                    "autoGenerated" => PsdSliceOrigin.AutoGenerated,
                    "layerGenerated" => PsdSliceOrigin.LayerBased,
                    _ => PsdSliceOrigin.UserGenerated,
                },
                AssociatedLayerId = (int)d.GetNumber("layerID", 0),
                Name = d.GetString("Nm  ") ?? string.Empty,
                Type = d.GetEnum("Type") == "noImage" ? PsdSliceType.NoImage : PsdSliceType.Image,
                Bounds = DescriptorRect(d.GetObject("bounds")),
                Url = d.GetString("url ") ?? string.Empty,
                Target = d.GetString("null") ?? string.Empty,
                Message = d.GetString("Msge") ?? string.Empty,
                AltTag = d.GetString("altTag") ?? string.Empty,
                CellTextIsHtml = d.GetBoolean("cellTextIsHTML"),
                CellText = d.GetString("cellText") ?? string.Empty,
                BackgroundColor = background is null
                    ? PsdColor.Transparent
                    : new PsdColor(Byte(background, "Rd  "), Byte(background, "Grn "), Byte(background, "Bl  "), Byte(background, "alpha")),
            });
        }

        return new PsdSlices(version, DescriptorRect(root.GetObject("bounds")), root.GetString("baseName") ?? string.Empty, slices, root);

        static byte Byte(Descriptor d, string key) => (byte)Math.Clamp(d.GetNumber(key, 0), 0, 255);
    }

    private static PsdRect DescriptorRect(Descriptor? bounds) => bounds is null
        ? default
        : new PsdRect(Int(bounds, "Left"), Int(bounds, "Top "), Int(bounds, "Rght"), Int(bounds, "Btom"));

    private static int Int(Descriptor d, string key) => (int)Math.Clamp(d.GetNumber(key, 0), int.MinValue, int.MaxValue);

    private static PsdRect ReadRect(BigEndianReader reader, bool topLeftFirst)
    {
        var a = reader.ReadInt32();
        var b = reader.ReadInt32();
        var c = reader.ReadInt32();
        var d = reader.ReadInt32();
        return topLeftFirst ? new PsdRect(b, a, d, c) : new PsdRect(a, b, c, d);
    }

    // Layer comps: descriptor version 16, then a descriptor whose "list" holds one
    // "Comp" object per comp; capturedInfo bits are visibility (1), position (2)
    // and appearance (4).
    private static PsdLayerComps? ParseLayerComps(BigEndianReader reader)
    {
        var root = Descriptor.ReadVersioned(reader);
        var comps = new List<PsdLayerComp>();
        foreach (var item in root.GetList("list") ?? [])
        {
            if (item.Object is not { } d)
            {
                continue;
            }

            var captured = (int)d.GetNumber("capturedInfo", 0);
            comps.Add(new PsdLayerComp(
                Int(d, "compID"),
                d.GetString("Nm  ") ?? string.Empty,
                d.GetString("comment") ?? string.Empty,
                (captured & 1) != 0,
                (captured & 2) != 0,
                (captured & 4) != 0));
        }

        int? last = root.TryGet("lastAppliedComp", out _) ? Int(root, "lastAppliedComp") : null;
        return new PsdLayerComps(comps, last, root);
    }

    private static PsdPrintScale? ParsePrintScale(BigEndianReader reader)
    {
        var style = reader.ReadUInt16();
        var x = reader.ReadSingle();
        var y = reader.ReadSingle();
        var scale = reader.ReadSingle();
        return new PsdPrintScale(style <= 2 ? (PsdPrintScaleStyle)style : PsdPrintScaleStyle.Centered, x, y, scale);
    }

    private double? ParsePixelAspectRatio()
    {
        if (_document.GetImageResource(ImageResourceIds.PixelAspectRatio) is not { Data.Length: >= 12 } resource)
        {
            return null;
        }

        var reader = new BigEndianReader(resource.Data);
        _ = reader.ReadUInt32(); // version 1 or 2
        var ratio = reader.ReadDouble();
        return double.IsFinite(ratio) && ratio > 0 ? ratio : null;
    }

    private static PsdVersionInfo? ParseVersionInfo(BigEndianReader reader)
    {
        var version = reader.ReadInt32();
        var real = reader.ReadByte() != 0;
        var writer = reader.ReadUnicodeString();
        var readerName = reader.ReadUnicodeString();
        var fileVersion = reader.Remaining >= 4 ? reader.ReadInt32() : 0;
        return new PsdVersionInfo(version, real, writer, readerName, fileVersion);
    }

    private PsdIccProfile? ParseIccProfile()
    {
        if (_document.GetImageResource(ImageResourceIds.IccProfile) is not { Data.Length: > 0 } resource)
        {
            return null;
        }

        var profile = Imaging.Icc.IccProfile.Parse(resource.Data.Span);
        return profile is null
            ? new PsdIccProfile(resource.Data, string.Empty, string.Empty, string.Empty, new Version(0, 0))
            : new PsdIccProfile(
                resource.Data,
                profile.Description,
                Signature(profile.ColorSpace),
                Signature(profile.DeviceClass),
                new Version(profile.MajorVersion, profile.MinorVersion));

        static string Signature(uint value) => Encoding.Latin1.GetString([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);
    }

    private string? ParseXmp() => _document.GetImageResource(ImageResourceIds.Xmp) is { } resource
        ? Encoding.UTF8.GetString(resource.Data.Span).TrimEnd('\0')
        : null;

    // IPTC-NAA records: 0x1C, record, dataset, u16 length (extended lengths stop the walk).
    private IReadOnlyList<PsdIptcDataSet> ParseIptc()
    {
        if (_document.GetImageResource(ImageResourceIds.IptcNaa) is not { } resource)
        {
            return [];
        }

        var data = resource.Data;
        var span = data.Span;
        var results = new List<PsdIptcDataSet>();
        var offset = 0;
        while (offset + 5 <= span.Length && span[offset] == 0x1C)
        {
            var length = (span[offset + 3] << 8) | span[offset + 4];
            if ((length & 0x8000) != 0 || offset + 5 + length > span.Length)
            {
                break;
            }

            results.Add(new PsdIptcDataSet(span[offset + 1], span[offset + 2], data.Slice(offset + 5, length)));
            offset += 5 + length;
        }

        return results;
    }

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    internal static string DecodeIptcText(ReadOnlySpan<byte> value)
    {
        try
        {
            return StrictUtf8.GetString(value).TrimEnd('\0');
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(value).TrimEnd('\0');
        }
    }
}
