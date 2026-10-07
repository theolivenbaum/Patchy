using System.Buffers.Binary;
using XRay.Psd.Imaging;
using XRay.Psd.IO;

namespace XRay.Psd.Rendering;

/// <summary>Decodes the image data section: the flattened composite Photoshop saves after the layers.</summary>
internal static class MergedImageDecoder
{
    public static PlanarImage Decode(PsdDocument document)
    {
        var image = new PlanarImage(document.Bounds);
        var data = document.MergedImageData.Span;
        if (data.Length < 2)
        {
            image.Fill(1f, 1f, 1f, 1f);
            return image;
        }

        var width = document.Width;
        var height = document.Height;
        var depth = document.Depth;
        var channels = document.ChannelCount;
        var rowBytes = ChannelCodec.RowBytes(width, depth);
        var planeBytes = (long)rowBytes * height;
        var colorCount = Math.Min(ColorSpaces.ColorChannelCount(document.ColorMode), channels);
        var wanted = Math.Min(channels, colorCount + (document.MergedImageHasTransparency ? 1 : 0));
        var compression = (PsdCompression)BinaryPrimitives.ReadUInt16BigEndian(data);
        var body = data[2..];
        var planes = new byte[wanted][];
        switch (compression)
        {
            case PsdCompression.Raw:
                for (var c = 0; c < wanted; c++)
                {
                    planes[c] = new byte[planeBytes];
                    var start = c * planeBytes;
                    if (start < body.Length)
                    {
                        var available = (int)Math.Min(planeBytes, body.Length - start);
                        body.Slice((int)start, available).CopyTo(planes[c]);
                    }
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

                    var counts = body[..(int)tableBytes];
                    var rows = body[(int)tableBytes..];
                    var offset = 0L;
                    for (var c = 0; c < wanted; c++)
                    {
                        var channelCounts = counts.Slice(c * height * countSize, height * countSize);
                        var size = 0L;
                        for (var row = 0; row < height; row++)
                        {
                            size += countSize == 4
                                ? BinaryPrimitives.ReadUInt32BigEndian(channelCounts[(row * 4)..])
                                : BinaryPrimitives.ReadUInt16BigEndian(channelCounts[(row * 2)..]);
                        }

                        planes[c] = new byte[planeBytes];
                        var start = (int)Math.Min(offset, rows.Length);
                        var length = (int)Math.Min(size, rows.Length - start);
                        ChannelCodec.DecodeRle(rows.Slice(start, length), channelCounts, countSize, height, rowBytes, planes[c]);
                        offset += size;
                    }

                    break;
                }

            case PsdCompression.Zip:
            case PsdCompression.ZipPrediction:
                {
                    var all = new byte[planeBytes * channels];
                    ChannelCodec.Inflate(body, all);
                    if (compression == PsdCompression.ZipPrediction)
                    {
                        ChannelCodec.UndoPrediction(all, width, height * channels, depth);
                    }

                    for (var c = 0; c < wanted; c++)
                    {
                        planes[c] = all.AsSpan((int)(c * planeBytes), (int)planeBytes).ToArray();
                    }

                    break;
                }

            default:
                throw new PsdFormatException($"Unknown merged image compression {(int)compression}.");
        }

        var floats = new float[]?[colorCount];
        for (var c = 0; c < colorCount; c++)
        {
            floats[c] = new float[width * height];
            ChannelCodec.ToFloat(planes[c], width, height, depth, floats[c]);
        }

        ColorSpaces.ToRgb(document, depth, floats, image);
        if (wanted > colorCount)
        {
            ChannelCodec.ToFloat(planes[colorCount], width, height, depth, image.A);
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
