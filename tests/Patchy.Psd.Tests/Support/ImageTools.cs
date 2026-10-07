using System.Buffers.Binary;
using Patchy.Psd.Imaging;

namespace Patchy.Psd.Tests.Support;

/// <summary>Difference statistics between two images of equal size.</summary>
internal readonly record struct ImageDiff(int MaxDelta, double MeanDelta, double FractionOver2)
{
    public override string ToString() => $"max {MaxDelta}, mean {MeanDelta:F3}, >2: {FractionOver2:P2}";
}

internal static class ImageTools
{
    /// <summary>Reads an uncompressed 24- or 32-bit BMP (the Photoshop reference renders) into opaque RGBA.</summary>
    public static RgbaImage ReadBmp(string path)
    {
        var data = File.ReadAllBytes(path);
        if (data.Length < 54 || data[0] != 'B' || data[1] != 'M')
        {
            throw new InvalidDataException("Not a BMP file.");
        }

        var offset = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(10));
        var width = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(18));
        var rawHeight = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(22));
        var bits = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(28));
        var compression = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(30));
        if ((bits != 24 && bits != 32) || (compression != 0 && compression != 3))
        {
            throw new InvalidDataException($"Unsupported BMP ({bits} bpp, compression {compression}).");
        }

        var height = Math.Abs(rawHeight);
        var bottomUp = rawHeight > 0;
        var bytesPerPixel = bits / 8;
        var stride = ((width * bytesPerPixel) + 3) & ~3;
        var image = new RgbaImage(width, height);
        for (var y = 0; y < height; y++)
        {
            var row = offset + ((bottomUp ? height - 1 - y : y) * stride);
            for (var x = 0; x < width; x++)
            {
                var p = row + (x * bytesPerPixel);
                image.SetPixel(x, y, new PsdColor(data[p + 2], data[p + 1], data[p]));
            }
        }

        return image;
    }

    /// <summary>Per-channel RGB difference after flattening both images over white.</summary>
    public static ImageDiff Compare(RgbaImage a, RgbaImage b, PsdColor? background = null)
    {
        if (a.Width != b.Width || a.Height != b.Height)
        {
            throw new ArgumentException($"Size mismatch {a.Width}x{a.Height} vs {b.Width}x{b.Height}.");
        }

        var fa = a.Flatten(background ?? PsdColor.White).Pixels;
        var fb = b.Flatten(background ?? PsdColor.White).Pixels;
        var max = 0;
        long sum = 0;
        long over = 0;
        long samples = 0;
        for (var i = 0; i < fa.Length; i += 4)
        {
            for (var c = 0; c < 3; c++)
            {
                var delta = Math.Abs(fa[i + c] - fb[i + c]);
                max = Math.Max(max, delta);
                sum += delta;
                if (delta > 2)
                {
                    over++;
                }

                samples++;
            }
        }

        return new ImageDiff(max, samples == 0 ? 0 : (double)sum / samples, samples == 0 ? 0 : (double)over / samples);
    }
}
