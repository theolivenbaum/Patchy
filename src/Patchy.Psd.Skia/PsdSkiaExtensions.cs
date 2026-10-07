using System.Runtime.InteropServices;
using Patchy.Psd.Imaging;
using Patchy.Psd.Rendering;
using SkiaSharp;

namespace Patchy.Psd.Skia;

/// <summary>Bridges <see cref="RgbaImage"/> and <see cref="PsdDocument"/> to SkiaSharp.</summary>
public static class PsdSkiaExtensions
{
    /// <summary>Copies the image into a new unpremultiplied RGBA8888 <see cref="SKBitmap"/>.</summary>
    public static SKBitmap ToSKBitmap(this RgbaImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var info = new SKImageInfo(image.Width, image.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var bitmap = new SKBitmap(info);
        var destination = bitmap.GetPixels();
        var rowBytes = image.Width * 4;
        if (bitmap.RowBytes == rowBytes)
        {
            Marshal.Copy(image.Pixels, 0, destination, image.Pixels.Length);
        }
        else
        {
            for (var y = 0; y < image.Height; y++)
            {
                Marshal.Copy(image.Pixels, y * rowBytes, destination + (y * bitmap.RowBytes), rowBytes);
            }
        }

        bitmap.NotifyPixelsChanged();
        return bitmap;
    }

    /// <summary>Creates an immutable <see cref="SKImage"/> from the pixels.</summary>
    public static SKImage ToSKImage(this RgbaImage image)
    {
        using var bitmap = image.ToSKBitmap();
        return SKImage.FromBitmap(bitmap);
    }

    /// <summary>Copies any Skia bitmap into an <see cref="RgbaImage"/> (converting to unpremultiplied RGBA).</summary>
    public static RgbaImage ToRgbaImage(this SKBitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        var info = new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var pixels = new byte[bitmap.Width * bitmap.Height * 4];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            using var pixmap = bitmap.PeekPixels();
            if (pixmap is null || !pixmap.ReadPixels(info, handle.AddrOfPinnedObject(), info.RowBytes, 0, 0))
            {
                throw new InvalidOperationException("Skia could not convert the bitmap to RGBA8888.");
            }
        }
        finally
        {
            handle.Free();
        }

        return new RgbaImage(bitmap.Width, bitmap.Height, pixels);
    }

    /// <summary>Encodes with a Skia codec (PNG, JPEG, WebP; AVIF where the native build supports it).</summary>
    public static byte[] Encode(this RgbaImage image, SKEncodedImageFormat format, int quality = 90)
    {
        using var skImage = image.ToSKImage();
        using var data = skImage.Encode(format, quality) ?? throw new NotSupportedException($"Skia cannot encode {format} on this platform.");
        return data.ToArray();
    }

    /// <summary>Renders the document and returns it as an <see cref="SKBitmap"/>.</summary>
    public static SKBitmap RenderToSKBitmap(this PsdDocument document, RenderOptions? options = null) =>
        document.Render(options).ToSKBitmap();

    /// <summary>Renders the document and encodes it with a Skia codec.</summary>
    public static byte[] RenderAndEncode(this PsdDocument document, SKEncodedImageFormat format, int quality = 90, RenderOptions? options = null) =>
        document.Render(options).Encode(format, quality);
}
