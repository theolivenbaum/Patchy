using System.Numerics;
using System.Runtime.InteropServices;
using XRay.Psd.Codecs;

namespace XRay.Psd.Imaging;

/// <summary>An 8-bit sRGB image with straight (non-premultiplied) alpha, stored as interleaved RGBA rows.</summary>
public sealed class RgbaImage
{
    public RgbaImage(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);
        Width = width;
        Height = height;
        Pixels = new byte[checked(width * height * 4)];
    }

    public RgbaImage(int width, int height, byte[] pixels)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (pixels.Length != (long)width * height * 4)
        {
            throw new ArgumentException("Pixel buffer size does not match the dimensions.", nameof(pixels));
        }

        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Interleaved RGBA bytes, row-major, no padding.</summary>
    public byte[] Pixels { get; }

    public PsdColor GetPixel(int x, int y)
    {
        var i = ((y * Width) + x) * 4;
        return new PsdColor(Pixels[i], Pixels[i + 1], Pixels[i + 2], Pixels[i + 3]);
    }

    public void SetPixel(int x, int y, PsdColor color)
    {
        var i = ((y * Width) + x) * 4;
        Pixels[i] = color.R;
        Pixels[i + 1] = color.G;
        Pixels[i + 2] = color.B;
        Pixels[i + 3] = color.A;
    }

    /// <summary>True when every pixel is fully opaque.</summary>
    public bool IsOpaque
    {
        get
        {
            var i = 0;
            if (Vector.IsHardwareAccelerated && BitConverter.IsLittleEndian)
            {
                // Alpha is the high byte of each little-endian RGBA word.
                var words = MemoryMarshal.Cast<byte, uint>(Pixels.AsSpan());
                var opaque = new Vector<uint>(0xFF000000u);
                for (; i <= words.Length - Vector<uint>.Count; i += Vector<uint>.Count)
                {
                    if (!Vector.EqualsAll(new Vector<uint>(words[i..]) & opaque, opaque))
                    {
                        return false;
                    }
                }

                i *= 4;
            }

            for (i += 3; i < Pixels.Length; i += 4)
            {
                if (Pixels[i] != 255)
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>Returns a copy composited over a solid background color (the result is opaque).</summary>
    public RgbaImage Flatten(PsdColor background)
    {
        var result = new RgbaImage(Width, Height);
        var source = Pixels;
        var target = result.Pixels;
        Parallelism.For(Height, Width, (first, last) =>
        {
            for (var i = first * Width * 4; i < last * Width * 4; i += 4)
            {
                int a = source[i + 3];
                var inverse = 255 - a;
                target[i] = (byte)(((source[i] * a) + (background.R * inverse) + 127) / 255);
                target[i + 1] = (byte)(((source[i + 1] * a) + (background.G * inverse) + 127) / 255);
                target[i + 2] = (byte)(((source[i + 2] * a) + (background.B * inverse) + 127) / 255);
                target[i + 3] = 255;
            }
        });

        return result;
    }

    /// <summary>Encodes as PNG (RGBA, or RGB when <paramref name="dropAlphaIfOpaque"/> and the image is opaque).</summary>
    public byte[] ToPng(bool dropAlphaIfOpaque = true, PngCompressionLevel level = PngCompressionLevel.Default) =>
        PngEncoder.Encode(this, dropAlphaIfOpaque, level);

    /// <summary>Encodes as baseline JPEG. Transparent areas are flattened over <paramref name="background"/> (white by default).</summary>
    public byte[] ToJpeg(int quality = 90, PsdColor? background = null) =>
        JpegEncoder.Encode(this, quality, background ?? PsdColor.White);

    public void SavePng(string path, bool dropAlphaIfOpaque = true) => File.WriteAllBytes(path, ToPng(dropAlphaIfOpaque));

    public void SaveJpeg(string path, int quality = 90, PsdColor? background = null) => File.WriteAllBytes(path, ToJpeg(quality, background));

    /// <summary>Saves by extension: <c>.png</c>, <c>.jpg</c>/<c>.jpeg</c>.</summary>
    public void Save(string path, int jpegQuality = 90)
    {
        var extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
        switch (extension)
        {
            case ".png":
                SavePng(path);
                break;
            case ".jpg":
            case ".jpeg":
                SaveJpeg(path, jpegQuality);
                break;
            default:
                throw new NotSupportedException($"Unsupported output extension '{extension}'. Use .png or .jpg.");
        }
    }
}
