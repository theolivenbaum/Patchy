using System.Text;
using XRay.Psd.Descriptors;
using XRay.Psd.IO;
using XRay.Psd.Layers;
using XRay.Psd.Text;

namespace XRay.Psd;

/// <summary>Reads the five PSD sections into a <see cref="PsdDocument"/>.</summary>
internal static class PsdParser
{
    private const int MaxDimensionPsd = 30000;
    private const int MaxDimensionPsb = 300000;

    private static readonly HashSet<string> AdjustmentKeys =
    [
        "levl", "curv", "brit", "blnc", "blwh", "hue ", "hue2", "selc", "mixr", "grdm",
        "phfl", "expA", "vibA", "nvrt", "thrs", "post", "clrL",
    ];

    private static readonly HashSet<string> FillKeys = ["SoCo", "GdFl", "PtFl"];

    // Keys whose length field is u64 in PSB files, regardless of signature.
    private static readonly HashSet<string> WideKeys =
    [
        "LMsk", "Lr16", "Lr32", "Layr", "Mt16", "Mt32", "Mtrn", "Alph", "FMsk", "lnk2", "FEid", "FXid", "PxSD", "cinf",
    ];

    /// <summary>The largest window one span can address.</summary>
    private const int MaxWindow = int.MaxValue;

    public static PsdDocument Parse(ReadOnlyMemory<byte> data, PsdLoadOptions options) => Parse(new MemorySource(data), options);

    /// <summary>
    /// Parses a file. Files up to <paramref name="windowedAbove"/> bytes are read through
    /// one span; larger ones (PSB files past 2 GB) take the windowed path, which walks
    /// the layer section with 64-bit offsets and gives every channel its own window.
    /// Tests lower the threshold to run the windowed path on small files.
    /// </summary>
    internal static PsdDocument Parse(PsdSource source, PsdLoadOptions options, long windowedAbove = MaxWindow)
    {
        if (source.Length > windowedAbove)
        {
            return ParseWindowed(source, options);
        }

        var data = source.Slice(0, (int)source.Length);
        var reader = new BigEndianReader(data);
        // Color management is known before parsing: descriptor and text colors convert while layers parse.
        var document = new PsdDocument { FileData = data, ColorManagementEnabled = options.ColorManagement };
        try
        {
            ReadHeader(reader, document);
            ReadColorModeData(reader, document);
            ReadImageResources(reader, document);
            ReadLayerAndMaskInfo(reader, document, options);
            document.MergedImageData = reader.ReadMemory(reader.Remaining);
            TextEngineResolver.Apply(document);
        }
        catch (PsdFormatException)
        {
            throw;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OverflowException or IndexOutOfRangeException)
        {
            throw new PsdFormatException($"Damaged PSD structure: {ex.Message}", ex);
        }

        return document;
    }

    private static PsdDocument ParseWindowed(PsdSource source, PsdLoadOptions options)
    {
        // Header, color mode data and resources come first and are small.
        var head = new BigEndianReader(source.Slice(0, (int)Math.Min(source.Length, MaxWindow)));
        var document = new PsdDocument { FileData = head.Memory };
        try
        {
            ReadHeader(head, document);
            ReadColorModeData(head, document);
            ReadImageResources(head, document);
            var mergedStart = ReadLayerAndMaskInfoWindowed(source, head.Position, document, options);

            // The merged image decodes from one window; past 2 GB it is cut there (see docs/performance.md).
            document.MergedImageData = source.Slice(mergedStart, (int)Math.Min(source.Length - mergedStart, MaxWindow));
            TextEngineResolver.Apply(document);
        }
        catch (PsdFormatException)
        {
            throw;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OverflowException or IndexOutOfRangeException)
        {
            throw new PsdFormatException($"Damaged PSD structure: {ex.Message}", ex);
        }

        return document;
    }

    /// <summary>
    /// The layer and mask section with 64-bit offsets: the layer records parse from a
    /// window at the start of the layer info, each channel's data becomes its own
    /// window at its file offset, and the global mask info and tagged blocks after the
    /// layer info parse from one more window. Returns the offset of the image data section.
    /// </summary>
    private static long ReadLayerAndMaskInfoWindowed(PsdSource source, long position, PsdDocument document, PsdLoadOptions options)
    {
        var large = document.IsLargeDocument;
        var length = ReadLengthAt(source, position, source.Length, large);
        var sectionStart = position + (large ? 8 : 4);
        var sectionEnd = sectionStart + length;
        if (length < (large ? 8 : 4))
        {
            return sectionEnd;
        }

        var layerInfoLength = ReadLengthAt(source, sectionStart, sectionEnd, large);
        var layerInfoStart = sectionStart + (large ? 8 : 4);
        var layerInfoEnd = layerInfoStart + layerInfoLength;
        var records = new List<PsdLayer>();
        if (layerInfoLength >= 2)
        {
            var layerInfo = new BigEndianReader(source.Slice(layerInfoStart, (int)Math.Min(layerInfoLength, MaxWindow)));
            if (options.ReadLayers)
            {
                records = ReadLayerInfo(layerInfo, document, out var mergedAlpha, source, layerInfoStart, layerInfoEnd);
                document.MergedImageHasTransparency = mergedAlpha;
            }
            else
            {
                document.MergedImageHasTransparency = layerInfo.ReadInt16() < 0;
            }
        }

        var rest = new BigEndianReader(source.Slice(layerInfoEnd, (int)Math.Min(sectionEnd - layerInfoEnd, MaxWindow)));
        ReadGlobalLayerBlocks(rest, document, options, records);
        return sectionEnd;
    }

    /// <summary>Reads a u32 (PSD) or u64 (PSB) section length at <paramref name="offset"/> that must fit before <paramref name="end"/>.</summary>
    private static long ReadLengthAt(PsdSource source, long offset, long end, bool large)
    {
        var size = large ? 8 : 4;
        if (end - offset < size)
        {
            throw new PsdFormatException($"Unexpected end of data at offset {offset}.");
        }

        var reader = new BigEndianReader(source.Slice(offset, size));
        var value = large ? reader.ReadUInt64() : reader.ReadUInt32();
        if (value > (ulong)(end - offset - size))
        {
            throw new PsdFormatException($"Section length {value} at offset {offset} exceeds the remaining {end - offset - size} bytes.");
        }

        return (long)value;
    }

    private static void ReadHeader(BigEndianReader reader, PsdDocument document)
    {
        if (reader.Length < 26 || reader.ReadSignature() != "8BPS")
        {
            throw new PsdFormatException("Not a Photoshop document (missing 8BPS signature).");
        }

        document.Version = reader.ReadUInt16();
        if (document.Version is not (1 or 2))
        {
            throw new PsdFormatException($"Unsupported PSD version {document.Version}.");
        }

        reader.Skip(6);
        document.ChannelCount = reader.ReadUInt16();
        document.Height = (int)reader.ReadUInt32();
        document.Width = (int)reader.ReadUInt32();
        document.Depth = reader.ReadUInt16();
        document.ColorMode = (PsdColorMode)reader.ReadUInt16();

        var max = document.IsLargeDocument ? MaxDimensionPsb : MaxDimensionPsd;
        if (document.Width <= 0 || document.Height <= 0 || document.Width > max || document.Height > max)
        {
            throw new PsdFormatException($"Invalid document size {document.Width}x{document.Height}.");
        }

        if (document.Depth is not (1 or 8 or 16 or 32))
        {
            throw new PsdFormatException($"Unsupported bit depth {document.Depth}.");
        }

        if (document.ChannelCount is < 1 or > 56)
        {
            throw new PsdFormatException($"Invalid channel count {document.ChannelCount}.");
        }
    }

    private static void ReadColorModeData(BigEndianReader reader, PsdDocument document)
    {
        var length = reader.ReadUInt32();
        document.ColorModeData = reader.ReadMemory((long)length);
        if (document.ColorMode == PsdColorMode.Indexed && length >= 768)
        {
            var span = document.ColorModeData.Span;
            var palette = new PsdColor[256];
            for (var i = 0; i < 256; i++)
            {
                palette[i] = new PsdColor(span[i], span[256 + i], span[512 + i]);
            }

            document.Palette = palette;
        }
    }

    private static void ReadImageResources(BigEndianReader reader, PsdDocument document)
    {
        var section = reader.Slice(reader.ReadUInt32());
        var resources = new List<ImageResource>();
        while (section.Remaining >= 12)
        {
            var signature = section.ReadSignature();
            if (signature is not ("8BIM" or "MeSa" or "AgHg" or "PHUT" or "DCSR"))
            {
                break;
            }

            var id = section.ReadUInt16();
            var name = section.ReadPascalString(2);
            var size = section.ReadUInt32();
            if (size > section.Remaining)
            {
                break;
            }

            var data = section.ReadMemory((long)size);
            if ((size & 1) != 0 && section.Remaining > 0)
            {
                section.Skip(1);
            }

            resources.Add(new ImageResource(id, name, data));
        }

        document.ImageResources = resources;

        if (document.GetImageResource(ImageResourceIds.GlobalAngle) is { Data.Length: >= 4 } angle)
        {
            document.GlobalLightAngle = new BigEndianReader(angle.Data).ReadInt32();
        }

        if (document.GetImageResource(ImageResourceIds.GlobalAltitude) is { Data.Length: >= 4 } altitude)
        {
            document.GlobalLightAltitude = new BigEndianReader(altitude.Data).ReadInt32();
        }

        // Version info: version u32, hasRealMergedData u8, ...
        if (document.GetImageResource(ImageResourceIds.VersionInfo) is { Data.Length: >= 5 } versionInfo)
        {
            document.HasRealMergedImage = versionInfo.Data.Span[4] != 0;
        }
    }

    private static void ReadLayerAndMaskInfo(BigEndianReader reader, PsdDocument document, PsdLoadOptions options)
    {
        var large = document.IsLargeDocument;
        var length = reader.ReadLength(large);
        if (length == 0)
        {
            return;
        }

        var section = reader.Slice(length);
        if (section.Remaining < (large ? 8 : 4))
        {
            return;
        }

        var layerInfoLength = section.ReadLength(large);
        var layerInfo = section.Slice(layerInfoLength);
        var records = new List<PsdLayer>();
        if (options.ReadLayers && layerInfo.Remaining >= 2)
        {
            records = ReadLayerInfo(layerInfo, document, out var mergedAlpha);
            document.MergedImageHasTransparency = mergedAlpha;
        }
        else if (layerInfo.Remaining >= 2)
        {
            document.MergedImageHasTransparency = layerInfo.ReadInt16() < 0;
        }

        ReadGlobalLayerBlocks(section, document, options, records);
    }

    /// <summary>The global layer mask info and the document-level tagged blocks after the layer info, then the layer tree.</summary>
    private static void ReadGlobalLayerBlocks(BigEndianReader section, PsdDocument document, PsdLoadOptions options, List<PsdLayer> records)
    {
        var large = document.IsLargeDocument;
        if (section.Remaining >= 4)
        {
            var maskLength = section.ReadUInt32();
            if (maskLength <= section.Remaining)
            {
                document.GlobalLayerMaskInfo = section.ReadMemory((long)maskLength);
            }
        }

        var blocks = ReadTaggedBlocks(section, large, section.Length);
        document.GlobalTaggedBlocks = blocks;

        // 16/32-bit documents keep their layers in a global Lr16/Lr32 block (Layr for 8-bit variants).
        if (records.Count == 0)
        {
            foreach (var block in blocks)
            {
                if (block.Key is "Lr16" or "Lr32" or "Layr" && block.Data.Length >= 2)
                {
                    var blockReader = new BigEndianReader(block.Data);
                    if (options.ReadLayers)
                    {
                        records = ReadLayerInfo(blockReader, document, out var mergedAlpha);
                        document.MergedImageHasTransparency |= mergedAlpha;
                    }
                    else
                    {
                        document.MergedImageHasTransparency |= blockReader.ReadInt16() < 0;
                    }

                    break;
                }
            }
        }

        if (options.ReadLinkedFiles)
        {
            document.LinkedFiles = ReadLinkedFiles(blocks, large);
        }

        BuildTree(document, records);
    }

    /// <summary>
    /// Reads the layer records and attaches their channel data. With <paramref name="source"/>
    /// (the windowed path) the reader covers the records, starting at file offset
    /// <paramref name="readerOffset"/>, and channel data is sliced from the source up to
    /// <paramref name="dataEnd"/> instead of from the reader.
    /// </summary>
    private static List<PsdLayer> ReadLayerInfo(BigEndianReader reader, PsdDocument document, out bool mergedAlpha, PsdSource? source = null, long readerOffset = 0, long dataEnd = 0)
    {
        var rawCount = reader.ReadInt16();
        mergedAlpha = rawCount < 0;
        var count = Math.Abs((int)rawCount);
        var layers = new List<PsdLayer>(count);
        var channelLengths = new List<long[]>(count);
        var ordinaryNumber = 0;
        for (var i = 0; i < count; i++)
        {
            var layer = ReadLayerRecord(reader, document, out var lengths);
            layer.Index = i;
            if (layer.SectionType == PsdSectionType.None)
            {
                ordinaryNumber++;
            }

            if (string.IsNullOrEmpty(layer.Name))
            {
                // Photoshop shows unnamed layers as "Layer N", and an unnamed bottom
                // layer without transparency is the legacy background.
                var hasAlpha = layer.Channels.Any(c => c.Id == -1);
                layer.Name = i == 0 && layer.SectionType == PsdSectionType.None && !hasAlpha
                    ? "Background"
                    : layer.SectionType == PsdSectionType.None ? $"Layer {ordinaryNumber}" : "Layer";
            }

            layers.Add(layer);
            channelLengths.Add(lengths);
        }

        // Channel image data follows all records, in record and channel order.
        var dataOffset = readerOffset + reader.Position;
        for (var i = 0; i < layers.Count; i++)
        {
            var layer = layers[i];
            var lengths = channelLengths[i];
            var channels = new List<LayerChannel>(lengths.Length);
            for (var c = 0; c < lengths.Length; c++)
            {
                if (source is not null)
                {
                    // A single channel past 2 GB cannot be one span; it reads as empty.
                    var length = Math.Min(lengths[c], Math.Max(0, dataEnd - dataOffset));
                    channels.Add(new LayerChannel(layer.Channels[c].Id, length <= MaxWindow ? source.Slice(dataOffset, (int)length) : ReadOnlyMemory<byte>.Empty));
                    dataOffset += length;
                    continue;
                }

                var available = Math.Min(lengths[c], reader.Remaining);
                channels.Add(new LayerChannel(layer.Channels[c].Id, reader.ReadMemory(available)));
            }

            layer.Channels = channels;
        }

        return layers;
    }

    private static PsdLayer ReadLayerRecord(BigEndianReader reader, PsdDocument document, out long[] channelLengths)
    {
        var large = document.IsLargeDocument;
        var layer = new PsdLayer(document);
        var top = reader.ReadInt32();
        var left = reader.ReadInt32();
        var bottom = reader.ReadInt32();
        var right = reader.ReadInt32();
        layer.Bounds = SafeRect(left, top, right, bottom);

        var channelCount = reader.ReadUInt16();
        if (channelCount > 56)
        {
            throw new PsdFormatException($"Layer has {channelCount} channels.");
        }

        channelLengths = new long[channelCount];
        var placeholders = new List<LayerChannel>(channelCount);
        for (var i = 0; i < channelCount; i++)
        {
            var id = reader.ReadInt16();
            channelLengths[i] = large ? (long)Math.Min(reader.ReadUInt64(), long.MaxValue) : reader.ReadUInt32();
            placeholders.Add(new LayerChannel(id, ReadOnlyMemory<byte>.Empty));
        }

        layer.Channels = placeholders;
        var signature = reader.ReadSignature();
        if (signature is not ("8BIM" or "8B64"))
        {
            throw new PsdFormatException($"Invalid layer blend signature '{signature}'.");
        }

        layer.BlendMode = BlendModeKeys.FromKey(reader.ReadSignature());
        layer.Opacity = reader.ReadByte();
        layer.IsClipped = reader.ReadByte() != 0;
        var flags = reader.ReadByte();
        layer.TransparencyProtected = (flags & 0x01) != 0;
        layer.IsVisible = (flags & 0x02) == 0;
        reader.Skip(1);

        var extra = reader.Slice(reader.ReadUInt32());
        if (extra.Remaining >= 4)
        {
            ReadMaskData(extra.Slice(extra.ReadUInt32()), layer);
        }

        if (extra.Remaining >= 4)
        {
            layer.BlendingRanges = extra.ReadMemory((long)extra.ReadUInt32());
        }

        if (extra.Remaining >= 1)
        {
            layer.Name = extra.ReadPascalString(4);
        }

        var blocks = ReadTaggedBlocks(extra, large, extra.Length);
        layer.TaggedBlocks = blocks;
        ApplyTaggedBlocks(layer, blocks, document);
        return layer;
    }

    private static PsdRect SafeRect(int left, int top, int right, int bottom)
    {
        if (right < left || bottom < top || (long)right - left > MaxDimensionPsb || (long)bottom - top > MaxDimensionPsb)
        {
            return default;
        }

        return new PsdRect(left, top, right, bottom);
    }

    private static void ReadMaskData(BigEndianReader mask, PsdLayer layer)
    {
        if (mask.Remaining < 18)
        {
            return;
        }

        var top = mask.ReadInt32();
        var left = mask.ReadInt32();
        var bottom = mask.ReadInt32();
        var right = mask.ReadInt32();
        var defaultColor = mask.ReadByte();
        var flags = mask.ReadByte();
        var fromRendering = (flags & 0x08) != 0;

        LayerMask? realUser = null;
        // The 36+ byte form carries the real user mask fields right after the flags,
        // ahead of any mask parameters (Photoshop's actual order).
        var parametersOnly = (flags & 0x18) == 0x10;
        if (mask.Length >= 36 && !parametersOnly && mask.Remaining >= 18)
        {
            var realFlags = mask.ReadByte();
            var realDefault = mask.ReadByte();
            var rt = mask.ReadInt32();
            var rl = mask.ReadInt32();
            var rb = mask.ReadInt32();
            var rr = mask.ReadInt32();
            realUser = new LayerMask
            {
                Bounds = SafeRect(rl, rt, rr, rb),
                DefaultColor = realDefault,
                Disabled = (realFlags & 0x02) != 0,
                Linked = (realFlags & 0x01) == 0,
                ChannelId = -3,
            };
        }
        else if (mask.Remaining >= 2 && mask.Length == 20 && (flags & 0x10) == 0)
        {
            // The plain 20-byte form ends in two pad bytes. With mask parameters
            // (flag bit 4) those two bytes are the parameter flags and a density
            // byte instead (photoshop-user-mask-params.psd "density-only",
            // photoshop-shape-feather.psd "sdensity-60").
            mask.Skip(2);
        }

        byte userDensity = 255;
        double userFeather = 0;
        byte vectorDensity = 255;
        double vectorFeather = 0;
        if ((flags & 0x10) != 0 && mask.Remaining >= 1)
        {
            var parameterFlags = mask.ReadByte();
            if ((parameterFlags & 0x01) != 0 && mask.Remaining >= 1)
            {
                userDensity = mask.ReadByte();
            }

            if ((parameterFlags & 0x02) != 0 && mask.Remaining >= 8)
            {
                userFeather = mask.ReadDouble();
            }

            if ((parameterFlags & 0x04) != 0 && mask.Remaining >= 1)
            {
                vectorDensity = mask.ReadByte();
            }

            if ((parameterFlags & 0x08) != 0 && mask.Remaining >= 8)
            {
                vectorFeather = mask.ReadDouble();
            }
        }

        static double SafeFeather(double f) => double.IsFinite(f) && f > 0 && f <= 1000 ? f : 0;

        layer.VectorMaskDensity = vectorDensity;
        layer.VectorMaskFeather = SafeFeather(vectorFeather);

        var primary = new LayerMask
        {
            Bounds = SafeRect(left, top, right, bottom),
            DefaultColor = defaultColor,
            Disabled = (flags & 0x02) != 0,
            Linked = (flags & 0x01) == 0,
            FromRendering = fromRendering,
            ChannelId = -2,
            Density = fromRendering ? vectorDensity : userDensity,
            Feather = SafeFeather(fromRendering ? vectorFeather : userFeather),
        };

        if (realUser is not null)
        {
            // Raster mask plus a parameterized vector mask: -3 is the painted mask and
            // -2 only Photoshop's combined render of both, so the vector mask is
            // rasterized from its path instead.
            layer.Mask = new LayerMask
            {
                Bounds = realUser.Bounds,
                DefaultColor = realUser.DefaultColor,
                Disabled = realUser.Disabled,
                Linked = realUser.Linked,
                ChannelId = -3,
                Density = userDensity,
                Feather = SafeFeather(userFeather),
            };
        }
        else if (fromRendering)
        {
            layer.RenderedVectorMask = primary;
        }
        else
        {
            layer.Mask = primary;
        }
    }

    private static List<TaggedBlock> ReadTaggedBlocks(BigEndianReader reader, bool large, int end)
    {
        var blocks = new List<TaggedBlock>();
        while (end - reader.Position >= 12)
        {
            var start = reader.Position;
            var signature = reader.ReadSignature();
            if (signature is not ("8BIM" or "8B64"))
            {
                reader.Position = start;
                break;
            }

            var key = reader.ReadSignature();
            var wide = signature == "8B64" || (large && WideKeys.Contains(key));
            if (wide && reader.Remaining < 8)
            {
                break;
            }

            var length = wide ? reader.ReadUInt64() : reader.ReadUInt32();
            if (length > (ulong)reader.Remaining)
            {
                break;
            }

            blocks.Add(new TaggedBlock(key, reader.ReadMemory((long)length)));

            // Global blocks are padded to four bytes, layer blocks normally carry
            // their padding inside the length. Skip up to three pad bytes when
            // the next signature does not start right away.
            if (!StartsWithSignature(reader, end))
            {
                for (var pad = 1; pad <= 3 && reader.Position + pad <= end; pad++)
                {
                    reader.Position += pad;
                    if (StartsWithSignature(reader, end))
                    {
                        break;
                    }

                    reader.Position -= pad;
                }
            }
        }

        return blocks;
    }

    private static bool StartsWithSignature(BigEndianReader reader, int end)
    {
        if (end - reader.Position < 4)
        {
            return false;
        }

        var span = reader.Memory.Span.Slice(reader.Position, 4);
        return span[0] == (byte)'8' && span[1] == (byte)'B' && ((span[2] == (byte)'I' && span[3] == (byte)'M') || (span[2] == (byte)'6' && span[3] == (byte)'4'));
    }

    private static void ApplyTaggedBlocks(PsdLayer layer, List<TaggedBlock> blocks, PsdDocument document)
    {
        var sawMultiEffects = false;
        foreach (var block in blocks)
        {
            var data = block.Data;
            switch (block.Key)
            {
                case "luni":
                    if (data.Length >= 4)
                    {
                        try
                        {
                            var name = new BigEndianReader(data).ReadUnicodeString();
                            if (name.Length > 0)
                            {
                                layer.Name = name;
                            }
                        }
                        catch (PsdFormatException)
                        {
                            // Keep the Pascal name.
                        }
                    }

                    break;
                case "lyid":
                    if (data.Length >= 4)
                    {
                        layer.Id = new BigEndianReader(data).ReadUInt32();
                    }

                    break;
                case "iOpa":
                    if (data.Length >= 1)
                    {
                        layer.FillOpacity = data.Span[0];
                    }

                    break;
                case "infx":
                    if (data.Length >= 1)
                    {
                        layer.BlendInteriorElements = data.Span[0] != 0;
                    }

                    break;
                case "clbl":
                    if (data.Length >= 1)
                    {
                        layer.BlendClippedElements = data.Span[0] != 0;
                    }

                    break;
                case "lsct":
                case "lsdk":
                    {
                        var section = new BigEndianReader(data);
                        if (section.Remaining >= 4)
                        {
                            var type = section.ReadUInt32();
                            layer.SectionType = type <= 3 ? (PsdSectionType)type : PsdSectionType.None;
                            if (section.Remaining >= 8 && section.ReadSignature() == "8BIM")
                            {
                                var mode = BlendModeKeys.FromKey(section.ReadSignature());
                                if (layer.SectionType is PsdSectionType.OpenFolder or PsdSectionType.ClosedFolder)
                                {
                                    layer.BlendMode = mode;
                                }
                            }
                        }

                        break;
                    }

                case "TySh":
                    layer.Text = TextLayerInfo.Parse(data, document.Colors);
                    layer.Kind = PsdLayerKind.Text;
                    break;
                case "tySh":
                    layer.Text ??= LegacyText.Parse(data, document.Colors);
                    layer.Kind = PsdLayerKind.Text;
                    break;
                case "vmsk":
                case "vsms":
                    layer.VectorMask ??= VectorPath.ParseVectorMask(data.Span, document.Width, document.Height);
                    break;
                case "vstk":
                    layer.VectorStroke ??= VectorStrokeStyle.Parse(data);
                    layer.VectorStroke?.Content?.AttachColors(document.Colors);
                    break;
                case "lfx2":
                case "lfxs":
                case "lmfx":
                    {
                        // Object-effects version u32 (0), descriptor version u32 (16), descriptor.
                        // 'lfxs' is the group (layer set) form; 'lmfx' (multiple instances)
                        // is authoritative over the compatibility lfx2 written beside it.
                        var effects = TryReadDescriptor(data, skip: 4, document.Colors);
                        if (effects is not null && (block.Key == "lmfx" || !sawMultiEffects))
                        {
                            sawMultiEffects |= block.Key == "lmfx";
                            layer.Effects = effects;
                            layer.EffectsVisible = effects.GetBoolean("masterFXSwitch", true);
                        }

                        break;
                    }

                case "SoLd":
                case "SoLE":
                    {
                        // 'soLD' identifier, version u32, then a versioned descriptor.
                        var placed = TryReadDescriptor(data, skip: 8);
                        if (placed is not null)
                        {
                            layer.SmartObject = placed;
                            layer.SmartObjectFileId = placed.GetString("Idnt") ?? layer.SmartObjectFileId;
                            layer.Kind = layer.Kind == PsdLayerKind.Text ? layer.Kind : PsdLayerKind.SmartObject;
                        }

                        break;
                    }

                case "PlLd":
                    {
                        if (layer.SmartObjectFileId is null && data.Length > 12)
                        {
                            var placed = new BigEndianReader(data);
                            try
                            {
                                placed.Skip(8); // 'plcL', version
                                layer.SmartObjectFileId = placed.ReadPascalString(1);
                            }
                            catch (PsdFormatException)
                            {
                                // Ignore malformed legacy placement data.
                            }
                        }

                        layer.Kind = layer.Kind == PsdLayerKind.Text ? layer.Kind : PsdLayerKind.SmartObject;
                        break;
                    }

                default:
                    if (FillKeys.Contains(block.Key))
                    {
                        layer.Kind = PsdLayerKind.Fill;
                        layer.ContentKey = block.Key;
                        layer.FillDescriptor = TryReadDescriptor(data, skip: 0, document.Colors);
                        if (block.Key == "SoCo" && layer.FillDescriptor is { } soco)
                        {
                            layer.FillColor = soco.GetColor("Clr ");
                        }
                    }
                    else if (AdjustmentKeys.Contains(block.Key))
                    {
                        layer.Kind = PsdLayerKind.Adjustment;
                        layer.ContentKey = block.Key;
                    }

                    break;
            }
        }

        // A legacy 'vscg' block on a shape (vector mask plus vstk) without SoCo/GdFl/PtFl
        // is the shape's paint: the fill when vstk enables filling, else the stroke's
        // fallback paint (reference psd_document_io.cpp, legacy_vector_content). Either
        // way the layer is a shape and renders from its path.
        if (layer.Kind == PsdLayerKind.Pixel && layer.VectorMask is not null && layer.VectorStroke is { } vectorStroke
            && layer.GetTaggedBlock("vscg") is { Data.Length: > 8 } vscg)
        {
            var key = DecodeLatin1(vscg.Data.Span[..4]);
            if (FillKeys.Contains(key) && TryReadDescriptor(vscg.Data, skip: 4) is { } paint)
            {
                layer.Kind = PsdLayerKind.Fill;
                if (vectorStroke.FillEnabled)
                {
                    layer.ContentKey = key;
                    layer.FillDescriptor = paint;
                    layer.FillColor = key == "SoCo" ? paint.GetColor("Clr ") : null;
                }
            }
        }

        if (layer.SectionType is PsdSectionType.OpenFolder or PsdSectionType.ClosedFolder)
        {
            layer.Kind = PsdLayerKind.Group;
        }
    }

    /// <summary>
    /// Reads a u32-versioned descriptor after skipping <paramref name="skip"/> prefix bytes. Returns null when it fails to parse.
    /// With <paramref name="colors"/>, its colors convert like the document's pixels.
    /// </summary>
    internal static Descriptor? TryReadDescriptor(ReadOnlyMemory<byte> data, int skip, Imaging.DocumentColors? colors = null)
    {
        try
        {
            var reader = new BigEndianReader(data);
            reader.Skip(skip);
            var descriptor = Descriptor.ReadVersioned(reader);
            return colors is null ? descriptor : descriptor.AttachColors(colors);
        }
        catch (PsdFormatException)
        {
            return null;
        }
    }

    private static void BuildTree(PsdDocument document, List<PsdLayer> records)
    {
        // Records run bottom to top: a bounding divider opens a group's child list,
        // and the folder record above its children closes it.
        var stack = new Stack<List<PsdLayer>>();
        var current = new List<PsdLayer>();
        foreach (var record in records)
        {
            if (record.SectionType == PsdSectionType.BoundingDivider)
            {
                stack.Push(current);
                current = [];
                continue;
            }

            if (record.Kind == PsdLayerKind.Group)
            {
                foreach (var child in current)
                {
                    record.AddChild(child);
                }

                current = stack.Count > 0 ? stack.Pop() : [];
                current.Add(record);
                document.AddLayer(record);
                continue;
            }

            current.Add(record);
            document.AddLayer(record);
        }

        // Unbalanced dividers: flatten whatever is still open into the root.
        while (stack.Count > 0)
        {
            var outer = stack.Pop();
            outer.AddRange(current);
            current = outer;
        }

        foreach (var layer in current)
        {
            document.AddRootLayer(layer);
        }
    }

    private static List<LinkedFile> ReadLinkedFiles(List<TaggedBlock> blocks, bool large)
    {
        var files = new List<LinkedFile>();
        foreach (var block in blocks)
        {
            if (block.Key is not ("lnk2" or "lnkD" or "lnk3" or "lnkE"))
            {
                continue;
            }

            var reader = new BigEndianReader(block.Data);
            while (reader.Remaining >= 8)
            {
                try
                {
                    var length = (long)reader.ReadUInt64();
                    if (length <= 0 || length > reader.Remaining)
                    {
                        break;
                    }

                    var element = reader.Slice(length);
                    var padding = (int)((4 - (length % 4)) % 4);
                    reader.Skip(Math.Min(padding, reader.Remaining));
                    var kind = element.ReadSignature();
                    var version = element.ReadUInt32();
                    if (kind is not ("liFD" or "liFE" or "liFA") || version is < 1 or > 32)
                    {
                        continue;
                    }

                    var id = element.ReadPascalString(1);
                    var fileName = element.ReadUnicodeString();
                    var fileType = element.ReadSignature();
                    _ = element.ReadSignature(); // creator
                    var size = (long)element.ReadUInt64();
                    if (element.ReadByte() != 0)
                    {
                        _ = Descriptor.ReadVersioned(element);
                    }

                    var payload = ReadOnlyMemory<byte>.Empty;
                    if (kind == "liFD" && size >= 0 && size <= element.Remaining)
                    {
                        payload = element.ReadMemory(size);
                    }

                    files.Add(new LinkedFile(id, fileName, fileType.TrimEnd('\0', ' '), kind, payload));
                }
                catch (PsdFormatException)
                {
                    break;
                }
            }
        }

        return files;
    }

    internal static string DecodeLatin1(ReadOnlySpan<byte> bytes) => Encoding.Latin1.GetString(bytes);
}
