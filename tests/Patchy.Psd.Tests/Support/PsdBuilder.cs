using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Patchy.Psd.Tests.Support;

/// <summary>A layer for <see cref="PsdBuilder"/>. Channel planes hold raw big-endian samples at the document depth.</summary>
internal sealed class BuilderLayer
{
    public string Name { get; set; } = "Layer";

    public PsdRect Rect { get; set; }

    public string BlendKey { get; set; } = "norm";

    public byte Opacity { get; set; } = 255;

    public bool Clipped { get; set; }

    public bool Hidden { get; set; }

    /// <summary>Section divider type for groups: 0 none, 1 open folder, 3 bounding divider.</summary>
    public int SectionType { get; set; }

    public string? SectionBlendKey { get; set; }

    public SortedDictionary<short, byte[]> Channels { get; } = [];

    public List<(string Key, byte[] Data)> Blocks { get; } = [];

    public PsdRect? MaskRect { get; set; }

    public byte MaskDefault { get; set; }

    public byte MaskFlags { get; set; }
}

/// <summary>
/// Writes minimal but valid PSD/PSB files for tests: any depth and color mode,
/// all four channel compressions, layers in the standard section or (16/32-bit)
/// in the Lr16/Lr32 global block, and arbitrary resources and tagged blocks.
/// </summary>
internal sealed class PsdBuilder
{
    public int Width { get; init; } = 4;

    public int Height { get; init; } = 4;

    public int Depth { get; init; } = 8;

    public PsdColorMode Mode { get; init; } = PsdColorMode.Rgb;

    public bool Large { get; init; }

    public PsdCompression LayerCompression { get; init; } = PsdCompression.Rle;

    public PsdCompression MergedCompression { get; init; } = PsdCompression.Rle;

    /// <summary>Merged image channel planes (raw big-endian samples), in channel order.</summary>
    public List<byte[]> MergedChannels { get; } = [];

    public bool MergedTransparency { get; init; }

    /// <summary>Puts 16/32-bit layers into the Lr16/Lr32 global block like Photoshop does.</summary>
    public bool DeepLayersInGlobalBlock { get; init; } = true;

    public byte[] ColorModeData { get; init; } = [];

    public List<(ushort Id, string Name, byte[] Data)> Resources { get; } = [];

    public List<BuilderLayer> Layers { get; } = [];

    public List<(string Key, byte[] Data)> GlobalBlocks { get; } = [];

    public int RowBytes => Depth switch
    {
        1 => (Width + 7) / 8,
        _ => Width * Depth / 8,
    };

    public static byte[] Plane8(int width, int height, Func<int, int, byte> sample)
    {
        var data = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                data[(y * width) + x] = sample(x, y);
            }
        }

        return data;
    }

    public static byte[] Plane16(int width, int height, Func<int, int, ushort> sample)
    {
        var data = new byte[width * height * 2];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(((y * width) + x) * 2), sample(x, y));
            }
        }

        return data;
    }

    public static byte[] Plane32(int width, int height, Func<int, int, float> sample)
    {
        var data = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                BinaryPrimitives.WriteSingleBigEndian(data.AsSpan(((y * width) + x) * 4), sample(x, y));
            }
        }

        return data;
    }

    public static byte[] Plane8(int width, int height, byte value) => Plane8(width, height, (_, _) => value);

    public byte[] Build()
    {
        var output = new Writer();
        output.Ascii("8BPS");
        output.U16((ushort)(Large ? 2 : 1));
        output.Zeros(6);
        output.U16((ushort)Math.Max(1, MergedChannels.Count));
        output.U32((uint)Height);
        output.U32((uint)Width);
        output.U16((ushort)Depth);
        output.U16((ushort)Mode);

        output.U32((uint)ColorModeData.Length);
        output.Bytes(ColorModeData);

        var resources = new Writer();
        foreach (var (id, name, data) in Resources)
        {
            resources.Ascii("8BIM");
            resources.U16(id);
            resources.Pascal(name, 2);
            resources.U32((uint)data.Length);
            resources.Bytes(data);
            if (data.Length % 2 != 0)
            {
                resources.U8(0);
            }
        }

        output.U32((uint)resources.Length);
        output.Bytes(resources.ToArray());

        var layerSection = new Writer();
        var layerInfo = Layers.Count > 0 ? BuildLayerInfo() : [];
        var deep = Depth > 8 && DeepLayersInGlobalBlock && Layers.Count > 0;
        if (deep)
        {
            Length(layerSection, 0);
        }
        else
        {
            Length(layerSection, layerInfo.Length);
            layerSection.Bytes(layerInfo);
        }

        layerSection.U32(0); // global layer mask info
        if (deep)
        {
            WriteBlock(layerSection, Depth == 16 ? "Lr16" : "Lr32", layerInfo, global: true);
        }

        foreach (var (key, data) in GlobalBlocks)
        {
            WriteBlock(layerSection, key, data, global: true);
        }

        Length(output, layerSection.Length);
        output.Bytes(layerSection.ToArray());

        var merged = MergedChannels.Count > 0 ? MergedChannels : [new byte[RowBytes * Height]];
        output.U16((ushort)MergedCompression);
        output.Bytes(EncodeMerged(merged));
        return output.ToArray();
    }

    private byte[] BuildLayerInfo()
    {
        var info = new Writer();
        info.I16((short)(MergedTransparency ? -Layers.Count : Layers.Count));
        var encoded = new List<List<byte[]>>();
        foreach (var layer in Layers)
        {
            var channels = new List<byte[]>();
            foreach (var (id, data) in layer.Channels)
            {
                var rect = id is -2 && layer.MaskRect is { } mask ? mask : layer.Rect;
                channels.Add(EncodeLayerChannel(data, rect.Width, rect.Height));
            }

            encoded.Add(channels);
            info.I32(layer.Rect.Top);
            info.I32(layer.Rect.Left);
            info.I32(layer.Rect.Bottom);
            info.I32(layer.Rect.Right);
            info.U16((ushort)layer.Channels.Count);
            var index = 0;
            foreach (var id in layer.Channels.Keys)
            {
                info.I16(id);
                Length(info, channels[index++].Length);
            }

            info.Ascii("8BIM");
            info.Ascii(layer.BlendKey);
            info.U8(layer.Opacity);
            info.U8((byte)(layer.Clipped ? 1 : 0));
            info.U8((byte)(layer.Hidden ? 0x02 : 0x00));
            info.U8(0);

            var extra = new Writer();
            if (layer.MaskRect is { } maskRect)
            {
                extra.U32(20);
                extra.I32(maskRect.Top);
                extra.I32(maskRect.Left);
                extra.I32(maskRect.Bottom);
                extra.I32(maskRect.Right);
                extra.U8(layer.MaskDefault);
                extra.U8(layer.MaskFlags);
                extra.Zeros(2);
            }
            else
            {
                extra.U32(0);
            }

            extra.U32(0); // blending ranges
            extra.Pascal(layer.Name, 4);
            var unicode = new Writer();
            unicode.UnicodeString(layer.Name);
            WriteBlock(extra, "luni", unicode.ToArray(), global: false);
            if (layer.SectionType != 0)
            {
                var section = new Writer();
                section.U32((uint)layer.SectionType);
                if (layer.SectionBlendKey is { } key)
                {
                    section.Ascii("8BIM");
                    section.Ascii(key);
                }

                WriteBlock(extra, "lsct", section.ToArray(), global: false);
            }

            foreach (var (key, data) in layer.Blocks)
            {
                WriteBlock(extra, key, data, global: false);
            }

            info.U32((uint)extra.Length);
            info.Bytes(extra.ToArray());
        }

        foreach (var channels in encoded)
        {
            foreach (var channel in channels)
            {
                info.Bytes(channel);
            }
        }

        if (info.Length % 2 != 0)
        {
            info.U8(0);
        }

        return info.ToArray();
    }

    private void Length(Writer writer, long length)
    {
        if (Large)
        {
            writer.U64((ulong)length);
        }
        else
        {
            writer.U32((uint)length);
        }
    }

    private void WriteBlock(Writer writer, string key, byte[] data, bool global)
    {
        var wide = Large && key is "Lr16" or "Lr32" or "Layr" or "lnk2";
        writer.Ascii("8BIM");
        writer.Ascii(key);
        var padded = data.Length + (global ? (4 - (data.Length % 4)) % 4 : data.Length % 2);
        if (wide)
        {
            writer.U64((ulong)padded);
        }
        else
        {
            writer.U32((uint)padded);
        }

        writer.Bytes(data);
        writer.Zeros(padded - data.Length);
    }

    private byte[] EncodeLayerChannel(byte[] samples, int width, int height)
    {
        var writer = new Writer();
        writer.U16((ushort)LayerCompression);
        var rowBytes = Depth == 1 ? (width + 7) / 8 : width * Depth / 8;
        switch (LayerCompression)
        {
            case PsdCompression.Raw:
                writer.Bytes(samples);
                break;
            case PsdCompression.Rle:
                {
                    var rows = new List<byte[]>();
                    for (var y = 0; y < height; y++)
                    {
                        rows.Add(PackBits(samples.AsSpan(y * rowBytes, rowBytes)));
                    }

                    foreach (var row in rows)
                    {
                        if (Large)
                        {
                            writer.U32((uint)row.Length);
                        }
                        else
                        {
                            writer.U16((ushort)row.Length);
                        }
                    }

                    foreach (var row in rows)
                    {
                        writer.Bytes(row);
                    }

                    break;
                }

            default:
                writer.Bytes(Zip(samples, width, height, LayerCompression == PsdCompression.ZipPrediction));
                break;
        }

        return writer.ToArray();
    }

    private byte[] EncodeMerged(List<byte[]> channels)
    {
        var writer = new Writer();
        switch (MergedCompression)
        {
            case PsdCompression.Raw:
                foreach (var channel in channels)
                {
                    writer.Bytes(channel);
                }

                break;
            case PsdCompression.Rle:
                {
                    var rows = new List<byte[]>();
                    foreach (var channel in channels)
                    {
                        for (var y = 0; y < Height; y++)
                        {
                            rows.Add(PackBits(channel.AsSpan(y * RowBytes, RowBytes)));
                        }
                    }

                    foreach (var row in rows)
                    {
                        if (Large)
                        {
                            writer.U32((uint)row.Length);
                        }
                        else
                        {
                            writer.U16((ushort)row.Length);
                        }
                    }

                    foreach (var row in rows)
                    {
                        writer.Bytes(row);
                    }

                    break;
                }

            default:
                writer.Bytes(Zip(channels.SelectMany(c => c).ToArray(), Width, Height * channels.Count, MergedCompression == PsdCompression.ZipPrediction));
                break;
        }

        return writer.ToArray();
    }

    private byte[] Zip(byte[] samples, int width, int height, bool prediction)
    {
        var data = (byte[])samples.Clone();
        if (prediction)
        {
            ApplyPrediction(data, width, height);
        }

        using var stream = new MemoryStream();
        using (var zlib = new ZLibStream(stream, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(data);
        }

        return stream.ToArray();
    }

    private void ApplyPrediction(byte[] data, int width, int height)
    {
        var rowBytes = width * Depth / 8;
        for (var y = 0; y < height; y++)
        {
            var row = data.AsSpan(y * rowBytes, rowBytes);
            switch (Depth)
            {
                case 8:
                    for (var x = rowBytes - 1; x > 0; x--)
                    {
                        row[x] -= row[x - 1];
                    }

                    break;
                case 16:
                    for (var x = width - 1; x > 0; x--)
                    {
                        var value = (ushort)(BinaryPrimitives.ReadUInt16BigEndian(row[(x * 2)..]) - BinaryPrimitives.ReadUInt16BigEndian(row[((x - 1) * 2)..]));
                        BinaryPrimitives.WriteUInt16BigEndian(row[(x * 2)..], value);
                    }

                    break;
                case 32:
                    {
                        // Split into byte planes, then delta-code the whole row.
                        var planes = new byte[rowBytes];
                        for (var x = 0; x < width; x++)
                        {
                            for (var b = 0; b < 4; b++)
                            {
                                planes[(b * width) + x] = row[(x * 4) + b];
                            }
                        }

                        for (var i = rowBytes - 1; i > 0; i--)
                        {
                            planes[i] -= planes[i - 1];
                        }

                        planes.CopyTo(row);
                        break;
                    }
            }
        }
    }

    /// <summary>PackBits encoder (runs of 3+ become repeat packets).</summary>
    public static byte[] PackBits(ReadOnlySpan<byte> row)
    {
        var output = new List<byte>();
        var i = 0;
        while (i < row.Length)
        {
            var run = 1;
            while (i + run < row.Length && run < 128 && row[i + run] == row[i])
            {
                run++;
            }

            if (run >= 3)
            {
                output.Add((byte)(sbyte)(1 - run));
                output.Add(row[i]);
                i += run;
                continue;
            }

            var start = i;
            var literal = 0;
            while (i < row.Length && literal < 128)
            {
                if (i + 2 < row.Length && row[i] == row[i + 1] && row[i] == row[i + 2])
                {
                    break;
                }

                i++;
                literal++;
            }

            output.Add((byte)(literal - 1));
            for (var k = start; k < start + literal; k++)
            {
                output.Add(row[k]);
            }
        }

        return [.. output];
    }

    /// <summary>Little helper for big-endian output.</summary>
    internal sealed class Writer
    {
        private readonly MemoryStream _stream = new();

        public long Length => _stream.Length;

        public byte[] ToArray() => _stream.ToArray();

        public void U8(byte value) => _stream.WriteByte(value);

        public void U16(ushort value)
        {
            Span<byte> b = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(b, value);
            _stream.Write(b);
        }

        public void I16(short value) => U16(unchecked((ushort)value));

        public void U32(uint value)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(b, value);
            _stream.Write(b);
        }

        public void I32(int value) => U32(unchecked((uint)value));

        public void U64(ulong value)
        {
            Span<byte> b = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(b, value);
            _stream.Write(b);
        }

        public void F64(double value)
        {
            Span<byte> b = stackalloc byte[8];
            BinaryPrimitives.WriteDoubleBigEndian(b, value);
            _stream.Write(b);
        }

        public void Bytes(ReadOnlySpan<byte> data) => _stream.Write(data);

        public void Zeros(int count)
        {
            for (var i = 0; i < count; i++)
            {
                _stream.WriteByte(0);
            }
        }

        public void Ascii(string text) => _stream.Write(Encoding.ASCII.GetBytes(text));

        public void Pascal(string text, int padding)
        {
            var bytes = Encoding.Latin1.GetBytes(text);
            U8((byte)bytes.Length);
            Bytes(bytes);
            var consumed = bytes.Length + 1;
            var padded = (consumed + padding - 1) / padding * padding;
            Zeros(padded - consumed);
        }

        public void UnicodeString(string text)
        {
            U32((uint)text.Length);
            Bytes(Encoding.BigEndianUnicode.GetBytes(text));
        }

        /// <summary>Descriptor ID: 0 + four-char code, or length + ASCII for longer keys.</summary>
        public void DescriptorId(string id)
        {
            if (id.Length == 4)
            {
                U32(0);
            }
            else
            {
                U32((uint)id.Length);
            }

            Ascii(id);
        }
    }
}
