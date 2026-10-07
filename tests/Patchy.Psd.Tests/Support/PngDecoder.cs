using System.Buffers.Binary;
using System.IO.Compression;
using Patchy.Psd.Imaging;

namespace Patchy.Psd.Tests.Support;

/// <summary>Minimal PNG decoder (8-bit RGB/RGBA, non-interlaced) to verify the encoder round trip.</summary>
internal static class PngDecoder
{
    public static RgbaImage Decode(byte[] png)
    {
        var offset = 8;
        int width = 0, height = 0, colorType = 0;
        using var idat = new MemoryStream();
        while (offset < png.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset));
            var type = System.Text.Encoding.ASCII.GetString(png, offset + 4, 4);
            var data = png.AsSpan(offset + 8, length);
            if (type == "IHDR")
            {
                width = BinaryPrimitives.ReadInt32BigEndian(data);
                height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                colorType = data[9];
            }
            else if (type == "IDAT")
            {
                idat.Write(data);
            }

            offset += 12 + length;
        }

        var channels = colorType == 6 ? 4 : 3;
        var stride = width * channels;
        idat.Position = 0;
        using var zlib = new ZLibStream(idat, CompressionMode.Decompress);
        var raw = new byte[(stride + 1) * height];
        zlib.ReadExactly(raw);
        var image = new RgbaImage(width, height);
        var previous = new byte[stride];
        var current = new byte[stride];
        for (var y = 0; y < height; y++)
        {
            var filter = raw[y * (stride + 1)];
            raw.AsSpan((y * (stride + 1)) + 1, stride).CopyTo(current);
            for (var i = 0; i < stride; i++)
            {
                int a = i >= channels ? current[i - channels] : 0;
                int b = previous[i];
                int c = i >= channels ? previous[i - channels] : 0;
                current[i] = filter switch
                {
                    1 => (byte)(current[i] + a),
                    2 => (byte)(current[i] + b),
                    3 => (byte)(current[i] + ((a + b) >> 1)),
                    4 => (byte)(current[i] + Paeth(a, b, c)),
                    _ => current[i],
                };
            }

            for (var x = 0; x < width; x++)
            {
                var p = x * channels;
                image.SetPixel(x, y, new PsdColor(current[p], current[p + 1], current[p + 2], channels == 4 ? current[p + 3] : (byte)255));
            }

            (previous, current) = (current, previous);
        }

        return image;
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
