using System.Buffers.Binary;
using XRay.Psd.Imaging;
using XRay.Psd.IO;

namespace XRay.Psd.Rendering;

/// <summary>Decodes the image data section: the flattened composite Photoshop saves after the layers.</summary>
internal static class MergedImageDecoder
{
    public static PlanarImage Decode(PsdDocument document) => Decode(document, 0, document.Height, null);

    /// <summary>
    /// Decodes document rows [<paramref name="top"/>, <paramref name="bottom"/>) into an image of
    /// those rows whose <see cref="PlanarImage.Logical"/> bounds are the document's. Every row
    /// is identical to the same row of a whole decode. The section is read as a file range,
    /// so a merged image past 2 GB decodes completely. <paramref name="bands"/> carries the
    /// RLE channel offsets and the ZIP streams from one band to the next.
    /// </summary>
    public static PlanarImage Decode(PsdDocument document, int top, int bottom, MergedImageBands? bands)
    {
        top = Math.Clamp(top, 0, document.Height);
        bottom = Math.Clamp(bottom, top, document.Height);
        var rect = new PsdRect(0, top, document.Width, bottom);
        var image = new PlanarImage(rect) { Logical = document.Bounds };
        var data = document.MergedImage;
        if (data.Length < 2)
        {
            image.Fill(1f, 1f, 1f, 1f);
            return image;
        }

        var width = document.Width;
        var height = document.Height;
        var rows = bottom - top;
        var depth = document.Depth;
        var channels = document.ChannelCount;
        var rowBytes = ChannelCodec.RowBytes(width, depth);
        var planeBytes = (long)rowBytes * height;
        var colorCount = Math.Min(ColorSpaces.ColorChannelCount(document.ColorMode), channels);
        var wanted = Math.Min(channels, colorCount + (document.MergedImageHasTransparency ? 1 : 0));
        var compression = (PsdCompression)BinaryPrimitives.ReadUInt16BigEndian(data.Window(0, 2).Span);
        var body = data.From(2);
        var bandBytes = ChannelCodec.BufferLength((long)rowBytes * rows);
        var planes = new byte[wanted][];
        switch (compression)
        {
            case PsdCompression.Raw:
                for (var c = 0; c < wanted; c++)
                {
                    planes[c] = new byte[bandBytes];
                    body.Window((c * planeBytes) + ((long)top * rowBytes), bandBytes).Span.CopyTo(planes[c]);
                }

                break;
            case PsdCompression.Rle:
                {
                    var countSize = document.IsLargeDocument ? 4 : 2;
                    var tableBytes = (long)channels * height * countSize;
                    if (tableBytes > body.Length)
                    {
                        throw new PsdFormatException("Merged image RLE table is truncated.");
                    }

                    var packed = body.From(tableBytes);
                    var starts = bands?.RleStarts ?? RleChannelStarts(body, countSize, height, wanted);
                    if (bands is not null)
                    {
                        bands.RleStarts = starts;
                    }

                    for (var c = 0; c < wanted; c++)
                    {
                        var counts = body.Slice((long)c * height * countSize, (long)height * countSize);
                        var start = Math.Min(starts[c], packed.Length);
                        var channelBody = packed.Slice(start, Math.Min(starts[c + 1] - starts[c], packed.Length - start));
                        planes[c] = new byte[bandBytes];
                        ChannelCodec.DecodeRleRows(counts, channelBody, countSize, rowBytes, top, bottom, planes[c]);
                    }

                    break;
                }

            case PsdCompression.Zip:
            case PsdCompression.ZipPrediction:
                {
                    // One stream holds every channel. A single decode walks it once; bands keep
                    // one stream per channel, each reading its rows at its own offset.
                    using var single = bands is null ? new ZipRowReader() : null;
                    for (var c = 0; c < wanted; c++)
                    {
                        planes[c] = new byte[bandBytes];
                        var offset = (c * planeBytes) + ((long)top * rowBytes);
                        (single ?? bands!.Zip(c)).Read(body, offset, planes[c]);

                        if (compression == PsdCompression.ZipPrediction)
                        {
                            ChannelCodec.UndoPrediction(planes[c], width, rows, depth);
                        }
                    }

                    break;
                }

            default:
                throw new PsdFormatException($"Unknown merged image compression {(int)compression}.");
        }

        var floats = new float[]?[colorCount];
        for (var c = 0; c < colorCount; c++)
        {
            floats[c] = new float[ChannelCodec.BufferLength((long)width * rows)];
            ChannelCodec.ToFloat(planes[c], width, rows, depth, floats[c]);
        }

        ColorSpaces.ToRgb(document, depth, floats, image);
        if (wanted > colorCount)
        {
            ChannelCodec.ToFloat(planes[colorCount], width, rows, depth, image.A);
            if (depth == 1)
            {
                // A 1-bit plane decodes set bits as 0; transparency uses the opposite sense.
                for (var i = 0; i < image.A.Length; i++)
                {
                    image.A[i] = 1f - image.A[i];
                }
            }

            UnmatteWhite(image);
        }
        else
        {
            image.A.AsSpan().Fill(1f);
        }

        return image;
    }

    /// <summary>
    /// Where each wanted channel's packed rows start after the count table: the sum of the
    /// earlier channels' row counts (unclamped, as the sequential walk adds them up), plus
    /// one end entry.
    /// </summary>
    private static long[] RleChannelStarts(ByteRange body, int countSize, int height, int wanted)
    {
        var starts = new long[wanted + 1];
        var offset = 0L;
        const int chunk = 1 << 20;
        for (var c = 0; c < wanted; c++)
        {
            starts[c] = offset;
            var size = 0L;
            for (var row = 0; row < height; row += chunk / countSize)
            {
                var count = Math.Min(chunk / countSize, height - row);
                var table = body.Window(((long)c * height * countSize) + ((long)row * countSize), count * countSize).Span;
                for (var i = 0; i < count; i++)
                {
                    size += countSize == 4 ? BinaryPrimitives.ReadUInt32BigEndian(table[(i * 4)..]) : BinaryPrimitives.ReadUInt16BigEndian(table[(i * 2)..]);
                }
            }

            offset += size;
        }

        starts[wanted] = offset;
        return starts;
    }

    /// <summary>
    /// Photoshop stores the merged image of a transparent document matted
    /// against white: color = c*a + (1-a). Recover straight color.
    /// Confirmed on arrows.psd (Photoshop CS4): no stored pixel falls below the
    /// white matte, and where the layer composite agrees on alpha (64 and up) the
    /// unmatted color matches it within 10 levels while the raw color is off by up
    /// to 188. The reference reads the plane as straight color because its own
    /// writer stores straight color (<c>merged_flatten_composite</c>,
    /// .reference/src/psd/psd_channel_data.cpp); Patchy-written transparent
    /// documents therefore decode slightly too light at soft edges here.
    /// </summary>
    private static void UnmatteWhite(PlanarImage image)
    {
        for (var i = 0; i < image.A.Length; i++)
        {
            var a = image.A[i];
            if (a <= 0f)
            {
                image.R[i] = image.G[i] = image.B[i] = 0f;
                continue;
            }

            if (a >= 1f)
            {
                continue;
            }

            var inverse = 1f / a;
            image.R[i] = Math.Clamp((image.R[i] - (1f - a)) * inverse, 0f, 1f);
            image.G[i] = Math.Clamp((image.G[i] - (1f - a)) * inverse, 0f, 1f);
            image.B[i] = Math.Clamp((image.B[i] - (1f - a)) * inverse, 0f, 1f);
        }
    }
}

/// <summary>State a band-by-band merged image decode keeps: RLE channel offsets and the open ZIP streams.</summary>
internal sealed class MergedImageBands : IDisposable
{
    public long[]? RleStarts { get; set; }

    private readonly Dictionary<int, ZipRowReader> _zip = [];

    /// <summary>The ZIP stream of one channel (channels decode one after another, never concurrently).</summary>
    public ZipRowReader Zip(int channel)
    {
        if (!_zip.TryGetValue(channel, out var reader))
        {
            reader = new ZipRowReader();
            _zip[channel] = reader;
        }

        return reader;
    }

    public void Dispose()
    {
        foreach (var reader in _zip.Values)
        {
            reader.Dispose();
        }

        _zip.Clear();
    }
}
