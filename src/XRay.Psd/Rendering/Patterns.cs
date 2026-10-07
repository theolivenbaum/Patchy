using XRay.Psd.IO;
using XRay.Psd.Layers;

namespace XRay.Psd.Rendering;

/// <summary>A decoded pattern tile: straight RGBA bytes.</summary>
internal sealed class PatternTile(string id, string name, int width, int height, byte[] rgba)
{
    public string Id { get; } = id;

    public string Name { get; } = name;

    public int Width { get; } = width;

    public int Height { get; } = height;

    public byte[] Rgba { get; } = rgba;
}

/// <summary>Placement of a pattern (overlay effect, pattern fill layer, bevel texture).</summary>
internal sealed record PatternPlacement(string PatternId, float Scale, float AngleDegrees, bool LinkWithLayer, float PhaseX, float PhaseY)
{
    public static PatternPlacement? FromDescriptor(Descriptors.Descriptor descriptor)
    {
        var pattern = descriptor.GetObject("Ptrn");
        var id = pattern?.GetString("Idnt");
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        var phase = descriptor.GetObject("phase");
        return new PatternPlacement(
            id,
            Math.Max(0.01f, (float)(descriptor.GetNumber("Scl ", 100) / 100)),
            (float)descriptor.GetNumber("Angl", 0),
            descriptor.GetBoolean("Algn", true),
            (float)(phase?.GetNumber("Hrzn", 0) ?? 0),
            (float)(phase?.GetNumber("Vrtc", 0) ?? 0));
    }
}

/// <summary>
/// The document's pattern store, decoded from the global <c>Patt</c>/<c>Pat2</c>/<c>Pat3</c>
/// blocks (.reference/src/psd/psd_patterns.cpp): one record per pattern with a
/// virtual-memory-array of channel planes (raw or PackBits, 8 or 16 bit).
/// </summary>
internal static class PatternStore
{
    private const int MaxPixels = 64 * 1024 * 1024;

    public static Dictionary<string, PatternTile> Parse(PsdDocument document)
    {
        var patterns = new Dictionary<string, PatternTile>(StringComparer.Ordinal);
        foreach (var block in document.GlobalTaggedBlocks)
        {
            if (block.Key is not ("Patt" or "Pat2" or "Pat3"))
            {
                continue;
            }

            var reader = new BigEndianReader(block.Data);
            try
            {
                while (reader.Remaining >= 16)
                {
                    if (ParseOne(reader) is { } tile)
                    {
                        patterns.TryAdd(tile.Id, tile);
                    }
                }
            }
            catch (PsdFormatException)
            {
                // Keep what decoded cleanly.
            }
        }

        return patterns;
    }

    private static PatternTile? ParseOne(BigEndianReader reader)
    {
        var length = reader.ReadUInt32();
        if (length < 16 || length > reader.Remaining)
        {
            throw new PsdFormatException("Invalid pattern length.");
        }

        var end = reader.Position + (int)length;
        PatternTile? result = null;
        try
        {
            var version = reader.ReadUInt32();
            var mode = reader.ReadUInt32();
            var height = (int)reader.ReadUInt16();
            var width = (int)reader.ReadUInt16();
            var name = reader.ReadUnicodeString();
            var id = PsdParser.DecodeLatin1(reader.ReadSpan(reader.ReadByte())).TrimEnd('\0');
            byte[]? colorTable = null;
            if (version == 1 && mode == 2)
            {
                colorTable = reader.ReadSpan(768).ToArray();
            }

            var supported = mode is 1 or 2 or 3 or 4 or 7;
            if (version == 1 && supported && width > 0 && height > 0 && (long)width * height <= MaxPixels && id.Length > 0)
            {
                result = DecodeImage(reader, end, (int)mode, width, height, id, name, colorTable);
            }
        }
        catch (PsdFormatException)
        {
            result = null;
        }

        reader.Position = end;
        var padding = (int)((4 - ((4 + length) % 4)) % 4);
        reader.Skip(Math.Min(padding, reader.Remaining));
        return result;
    }

    private static PatternTile? DecodeImage(BigEndianReader reader, int patternEnd, int mode, int width, int height, string id, string name, byte[]? colorTable)
    {
        var vmaVersion = reader.ReadUInt32();
        var vmaLength = reader.ReadUInt32();
        if (vmaVersion != 3 || vmaLength < 20 || vmaLength > patternEnd - reader.Position)
        {
            return null;
        }

        var vmaEnd = reader.Position + (int)vmaLength;
        reader.Skip(16);
        var declaredChannels = reader.ReadUInt32();
        if (declaredChannels > 64)
        {
            return null;
        }

        var colorCount = mode switch { 3 => 3, 4 => 4, _ => 1 };
        var planes = new Plane?[colorCount];
        Plane? alpha = null;
        for (var slot = 0u; slot < declaredChannels + 2 && reader.Position < vmaEnd; slot++)
        {
            var relevant = slot < colorCount || slot == declaredChannels + 1;
            var plane = ReadPlane(reader, vmaEnd, relevant);
            if (plane is null)
            {
                continue;
            }

            if (slot < colorCount)
            {
                planes[slot] = plane;
            }
            else if (slot == declaredChannels + 1)
            {
                alpha = plane;
            }
        }

        if (planes.All(p => p is null))
        {
            return null;
        }

        var rgba = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var o = ((y * width) + x) * 4;
                switch (mode)
                {
                    case 2:
                        {
                            var index = Sample(planes[0], x, y, 0);
                            rgba[o] = colorTable![index * 3];
                            rgba[o + 1] = colorTable[(index * 3) + 1];
                            rgba[o + 2] = colorTable[(index * 3) + 2];
                            break;
                        }

                    case 3:
                        rgba[o] = Sample(planes[0], x, y, 0);
                        rgba[o + 1] = Sample(planes[1], x, y, 0);
                        rgba[o + 2] = Sample(planes[2], x, y, 0);
                        break;
                    case 4:
                        {
                            var k = Sample(planes[3], x, y, 255);
                            rgba[o] = (byte)(Sample(planes[0], x, y, 255) * k / 255);
                            rgba[o + 1] = (byte)(Sample(planes[1], x, y, 255) * k / 255);
                            rgba[o + 2] = (byte)(Sample(planes[2], x, y, 255) * k / 255);
                            break;
                        }

                    default:
                        {
                            var gray = Sample(planes[0], x, y, 0);
                            rgba[o] = rgba[o + 1] = rgba[o + 2] = gray;
                            break;
                        }
                }

                rgba[o + 3] = alpha is null ? (byte)255 : Sample(alpha, x, y, 255);
            }
        }

        return new PatternTile(id, name, width, height, rgba);
    }

    private sealed record Plane(int Top, int Left, int Width, int Height, byte[] Samples);

    private static byte Sample(Plane? plane, int x, int y, byte fallback)
    {
        if (plane is null)
        {
            return fallback;
        }

        var px = x - plane.Left;
        var py = y - plane.Top;
        return px < 0 || py < 0 || px >= plane.Width || py >= plane.Height ? fallback : plane.Samples[(py * plane.Width) + px];
    }

    private static Plane? ReadPlane(BigEndianReader reader, int containerEnd, bool decode)
    {
        if (containerEnd - reader.Position < 4)
        {
            throw new PsdFormatException("Pattern channel list is truncated.");
        }

        var written = reader.ReadUInt32();
        if (written == 0)
        {
            return null;
        }

        var length = reader.ReadUInt32();
        if (length == 0)
        {
            return null;
        }

        if (length < 23 || length > containerEnd - reader.Position)
        {
            throw new PsdFormatException("Pattern channel is truncated.");
        }

        var end = reader.Position + (int)length;
        if (!decode)
        {
            reader.Position = end;
            return null;
        }

        var depth = reader.ReadUInt32();
        var top = reader.ReadInt32();
        var left = reader.ReadInt32();
        var bottom = reader.ReadInt32();
        var right = reader.ReadInt32();
        var pixelDepth = reader.ReadUInt16();
        var compression = reader.ReadByte();
        var width = right - left;
        var height = bottom - top;
        if (width <= 0 || height <= 0 || width > 30000 || height > 30000 || (long)width * height > MaxPixels || depth is not (8 or 16) || pixelDepth is not (8 or 16))
        {
            throw new PsdFormatException("Pattern channel is invalid.");
        }

        var bytesPerSample = pixelDepth / 8;
        var rowBytes = width * bytesPerSample;
        var raw = new byte[rowBytes * height];
        if (compression == 0)
        {
            reader.ReadSpan(raw.Length).CopyTo(raw);
        }
        else if (compression == 1)
        {
            var counts = reader.ReadSpan(height * 2);
            ChannelCodec.DecodeRle(reader.Memory.Span[reader.Position..end], counts, 2, height, rowBytes, raw);
        }
        else
        {
            throw new PsdFormatException("Unsupported pattern compression.");
        }

        var samples = new byte[width * height];
        if (bytesPerSample == 1)
        {
            raw.AsSpan(0, samples.Length).CopyTo(samples);
        }
        else
        {
            // Photoshop's 16-bit samples use the 0..32768 scale.
            for (var i = 0; i < samples.Length; i++)
            {
                var value = (uint)((raw[i * 2] << 8) | raw[(i * 2) + 1]);
                samples[i] = (byte)Math.Min(255u, ((value * 255u) + 16384u) / 32768u);
            }
        }

        reader.Position = end;
        return new Plane(top, left, width, height, samples);
    }
}

/// <summary>
/// Wrap-tiled pattern sampler in document space, ported from
/// .reference/src/core/pattern_sampler.hpp: linked patterns anchor at the
/// layer's effects reference point (<c>fxrp</c>), unlinked at the document
/// origin, plus the phase. 100% is nearest-texel, magnification and rotation
/// interpolate linearly, minification box-averages the footprint.
/// </summary>
internal sealed class PatternSampler
{
    private readonly PatternTile _tile;
    private readonly double _anchorX;
    private readonly double _anchorY;
    private readonly double _inverseScale;
    private readonly bool _rotated;
    private readonly double _cos;
    private readonly double _sin;
    private readonly bool _nearest;
    private readonly bool _box;

    public PatternSampler(PatternTile tile, PatternPlacement placement, PsdLayer? layer)
    {
        _tile = tile;
        double anchorX = placement.PhaseX;
        double anchorY = placement.PhaseY;
        if (placement.LinkWithLayer && layer?.GetTaggedBlock("fxrp") is { Data.Length: 16 } fxrp)
        {
            var reader = new BigEndianReader(fxrp.Data);
            anchorX += reader.ReadDouble();
            anchorY += reader.ReadDouble();
        }

        _anchorX = anchorX;
        _anchorY = anchorY;
        var scale = Math.Max(0.01, placement.Scale);
        _inverseScale = 1.0 / scale;
        var radians = placement.AngleDegrees * Math.PI / 180.0;
        _rotated = Math.Abs(radians) > 1e-9;
        _cos = Math.Cos(radians);
        _sin = Math.Sin(radians);
        _nearest = !_rotated && Math.Abs(scale - 1.0) < 1e-6;
        _box = !_nearest && !_rotated && scale < 1.0;
    }

    private static int Wrap(long index, int size)
    {
        var value = (int)(index % size);
        return value < 0 ? value + size : value;
    }

    private (float R, float G, float B, float A) Texel(int x, int y)
    {
        var o = ((y * _tile.Width) + x) * 4;
        var p = _tile.Rgba;
        return (p[o], p[o + 1], p[o + 2], p[o + 3] / 255f);
    }

    /// <summary>Straight color (0..1) and alpha at a document pixel.</summary>
    public (float R, float G, float B, float A) Sample(int x, int y)
    {
        var width = _tile.Width;
        var height = _tile.Height;
        if (_nearest)
        {
            var (r, g, b, a) = Texel(Wrap((long)Math.Floor(x + 0.5 - _anchorX), width), Wrap((long)Math.Floor(y + 0.5 - _anchorY), height));
            return (r / 255f, g / 255f, b / 255f, a);
        }

        if (_box)
        {
            var u0 = (x - _anchorX) * _inverseScale;
            var u1 = (x + 1.0 - _anchorX) * _inverseScale;
            var v0 = (y - _anchorY) * _inverseScale;
            var v1 = (y + 1.0 - _anchorY) * _inverseScale;
            double sr = 0, sg = 0, sb = 0, sa = 0, total = 0;
            for (var ty = (long)Math.Floor(v0); ty <= (long)Math.Ceiling(v1) - 1; ty++)
            {
                var coverY = Math.Min(v1, ty + 1.0) - Math.Max(v0, ty);
                if (coverY <= 0)
                {
                    continue;
                }

                var row = Wrap(ty, height);
                for (var tx = (long)Math.Floor(u0); tx <= (long)Math.Ceiling(u1) - 1; tx++)
                {
                    var coverX = Math.Min(u1, tx + 1.0) - Math.Max(u0, tx);
                    if (coverX <= 0)
                    {
                        continue;
                    }

                    var weight = coverX * coverY;
                    var (r, g, b, a) = Texel(Wrap(tx, width), row);
                    sr += weight * r;
                    sg += weight * g;
                    sb += weight * b;
                    sa += weight * a;
                    total += weight;
                }
            }

            if (total <= 0)
            {
                return (0, 0, 0, 0);
            }

            return ((float)(Math.Round(sr / total) / 255), (float)(Math.Round(sg / total) / 255), (float)(Math.Round(sb / total) / 255), (float)Math.Clamp(sa / total, 0, 1));
        }

        var u = x - _anchorX;
        var v = y - _anchorY;
        if (_rotated)
        {
            var ru = (u * _cos) - (v * _sin);
            var rv = (u * _sin) + (v * _cos);
            u = ru;
            v = rv;
        }

        u *= _inverseScale;
        v *= _inverseScale;
        var fu = Math.Floor(u);
        var fv = Math.Floor(v);
        var fx = (float)(u - fu);
        var fy = (float)(v - fv);
        var x0 = Wrap((long)fu, width);
        var y0 = Wrap((long)fv, height);
        var x1 = x0 + 1 == width ? 0 : x0 + 1;
        var y1 = y0 + 1 == height ? 0 : y0 + 1;
        var tl = Texel(x0, y0);
        var tr = Texel(x1, y0);
        var bl = Texel(x0, y1);
        var br = Texel(x1, y1);
        static float Lerp(float a, float b, float t) => a + ((b - a) * t);
        float Mix(float a, float b, float c, float d) => Lerp(Lerp(a, b, fx), Lerp(c, d, fx), fy);
        return (
            MathF.Round(Mix(tl.R, tr.R, bl.R, br.R)) / 255f,
            MathF.Round(Mix(tl.G, tr.G, bl.G, br.G)) / 255f,
            MathF.Round(Mix(tl.B, tr.B, bl.B, br.B)) / 255f,
            Mix(tl.A, tr.A, bl.A, br.A));
    }
}
