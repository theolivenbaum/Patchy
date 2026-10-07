using XRay.Psd.Tests.Support;

namespace XRay.Psd.Benchmarks;

/// <summary>Benchmark inputs: committed fixtures and one synthetic large document.</summary>
internal static class BenchmarkData
{
    /// <summary>Name of the generated document (not a file in the repository).</summary>
    public const string Synthetic = "synthetic-4000x3000.psd";

    private static readonly Lazy<string> FixtureRoot = new(() =>
    {
        if (Environment.GetEnvironmentVariable("XRAY_PSD_FIXTURES") is { Length: > 0 } configured)
        {
            return configured;
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "tests", "fixtures", "psd");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("tests/fixtures/psd not found; set XRAY_PSD_FIXTURES.");
    });

    private static readonly Lazy<byte[]> SyntheticBytes = new(BuildSynthetic);

    public static byte[] Bytes(string name) => name == Synthetic ? SyntheticBytes.Value : File.ReadAllBytes(Path.Combine(FixtureRoot.Value, name));

    /// <summary>Writes the document to a temporary file (for path-based loading) and returns its path.</summary>
    public static string TempFile(string name)
    {
        var path = Path.Combine(Path.GetTempPath(), "xray-psd-bench-" + name);
        File.WriteAllBytes(path, Bytes(name));
        return path;
    }

    /// <summary>
    /// A 4000x3000 RGB document with six full-size RLE layers in different blend
    /// modes and opacities, one with a raster mask, plus an RLE merged image.
    /// Content is a deterministic mix of gradients and hashed noise.
    /// </summary>
    private static byte[] BuildSynthetic()
    {
        const int width = 4000;
        const int height = 3000;
        var builder = new PsdBuilder { Width = width, Height = height, LayerCompression = PsdCompression.Rle, MergedCompression = PsdCompression.Rle };
        var rect = new PsdRect(0, 0, width, height);
        string[] modes = ["norm", "mul ", "scrn", "over", "sLit", "hLit"];
        for (var layerIndex = 0; layerIndex < modes.Length; layerIndex++)
        {
            var seed = layerIndex + 1;
            var layer = new BuilderLayer
            {
                Name = $"Layer {seed}",
                Rect = rect,
                BlendKey = modes[layerIndex],
                Opacity = (byte)(layerIndex == 0 ? 255 : 160 + (layerIndex * 15)),
            };
            layer.Channels[0] = PsdBuilder.Plane8(width, height, (x, y) => Sample(x, y, seed, 0));
            layer.Channels[1] = PsdBuilder.Plane8(width, height, (x, y) => Sample(x, y, seed, 1));
            layer.Channels[2] = PsdBuilder.Plane8(width, height, (x, y) => Sample(x, y, seed, 2));
            if (layerIndex > 0)
            {
                // Soft-edged blobs of coverage so blending sees partial alpha.
                layer.Channels[-1] = PsdBuilder.Plane8(width, height, (x, y) => Coverage(x, y, seed));
            }

            if (layerIndex == 3)
            {
                layer.MaskRect = new PsdRect(500, 400, 3500, 2600);
                layer.MaskDefault = 0;
                layer.Channels[-2] = PsdBuilder.Plane8(3000, 2200, (x, y) => (byte)((x * 255) / 2999));
            }

            builder.Layers.Add(layer);
        }

        for (var channel = 0; channel < 3; channel++)
        {
            var c = channel;
            builder.MergedChannels.Add(PsdBuilder.Plane8(width, height, (x, y) => Sample(x, y, 7, c)));
        }

        return builder.Build();
    }

    private static byte Sample(int x, int y, int seed, int channel)
    {
        var gradient = ((x * (seed + channel + 1)) + (y * (seed + 2))) / 37;
        var noise = Hash((uint)x, (uint)y, (uint)((seed * 3) + channel)) & 0x0F;
        return (byte)((gradient + (int)noise) & 0xFF);
    }

    private static byte Coverage(int x, int y, int seed)
    {
        var cx = (x / 500 * 500) + 250;
        var cy = (y / 500 * 500) + 250;
        var dx = x - cx;
        var dy = y - cy;
        var distance = Math.Sqrt((dx * dx) + (dy * dy));
        var radius = 120 + (seed * 20);
        return (byte)Math.Clamp((radius - distance) * 4, 0, 255);
    }

    private static uint Hash(uint x, uint y, uint seed)
    {
        var h = (x * 0x9E3779B1u) ^ (y * 0x85EBCA77u) ^ (seed * 0xC2B2AE3Du);
        h ^= h >> 15;
        h *= 0x2C1B3C6Du;
        h ^= h >> 12;
        return h;
    }
}
