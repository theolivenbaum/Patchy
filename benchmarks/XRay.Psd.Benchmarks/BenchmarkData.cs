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

    /// <summary>Name of the generated effect-heavy document (not a file in the repository).</summary>
    public const string SyntheticEffects = "synthetic-effects-2400x1600.psd";

    private static readonly Lazy<byte[]> SyntheticBytes = new(BuildSynthetic);

    private static readonly Lazy<byte[]> SyntheticEffectsBytes = new(BuildSyntheticEffects);

    public static byte[] Bytes(string name) => name switch
    {
        Synthetic => SyntheticBytes.Value,
        SyntheticEffects => SyntheticEffectsBytes.Value,
        _ => File.ReadAllBytes(Path.Combine(FixtureRoot.Value, name)),
    };

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

    /// <summary>
    /// A 2400x1600 RGB document whose two large soft-edged layers carry every common
    /// effect: drop shadow, outer and inner glow, inner shadow, outside stroke, satin,
    /// gradient overlay and a smooth bevel on the first; a chisel bevel, a centered
    /// stroke and a color overlay on the second.
    /// </summary>
    private static byte[] BuildSyntheticEffects()
    {
        const int width = 2400;
        const int height = 1600;
        var builder = new PsdBuilder { Width = width, Height = height, LayerCompression = PsdCompression.Rle, MergedCompression = PsdCompression.Rle };
        var canvas = new PsdRect(0, 0, width, height);
        var background = new BuilderLayer { Name = "Background", Rect = canvas };
        for (var c = 0; c < 3; c++)
        {
            background.Channels[(short)c] = PsdBuilder.Plane8(width, height, 235);
        }

        builder.Layers.Add(background);
        builder.Layers.Add(Blobs("Styled 1", new PsdRect(150, 120, 1350, 1480), 3, Effects(
            Effect("DrSh", ("Md  ", BlendMode("mul ")), ("Clr ", DescriptorWriter.Rgb(0, 0, 0)), ("Opct", DescriptorWriter.Percent(70)), ("uglg", false),
                ("lagl", new DescriptorWriter.Unit("#Ang", 120)), ("Dstn", DescriptorWriter.Pixels(18)), ("Ckmt", DescriptorWriter.Percent(10)), ("blur", DescriptorWriter.Pixels(24))),
            Effect("OrGl", ("Md  ", BlendMode("scrn")), ("Clr ", DescriptorWriter.Rgb(255, 230, 120)), ("Opct", DescriptorWriter.Percent(60)), ("Ckmt", DescriptorWriter.Percent(0)),
                ("blur", DescriptorWriter.Pixels(30)), ("Inpr", DescriptorWriter.Percent(50))),
            Effect("IrGl", ("Md  ", BlendMode("scrn")), ("Clr ", DescriptorWriter.Rgb(255, 255, 255)), ("Opct", DescriptorWriter.Percent(50)), ("Ckmt", DescriptorWriter.Percent(0)),
                ("blur", DescriptorWriter.Pixels(16)), ("Inpr", DescriptorWriter.Percent(50))),
            Effect("IrSh", ("Md  ", BlendMode("mul ")), ("Clr ", DescriptorWriter.Rgb(20, 0, 60)), ("Opct", DescriptorWriter.Percent(60)), ("uglg", false),
                ("lagl", new DescriptorWriter.Unit("#Ang", 60)), ("Dstn", DescriptorWriter.Pixels(8)), ("Ckmt", DescriptorWriter.Percent(0)), ("blur", DescriptorWriter.Pixels(12))),
            Effect("ChFX", ("Md  ", BlendMode("mul ")), ("Clr ", DescriptorWriter.Rgb(0, 40, 90)), ("Opct", DescriptorWriter.Percent(40)),
                ("lagl", new DescriptorWriter.Unit("#Ang", 19)), ("Dstn", DescriptorWriter.Pixels(11)), ("blur", DescriptorWriter.Pixels(14)), ("Invr", true)),
            Effect("FrFX", ("Styl", new DescriptorWriter.EnumValue("FStl", "OutF")), ("PntT", new DescriptorWriter.EnumValue("FrFl", "SClr")), ("Md  ", BlendMode("Nrml")),
                ("Opct", DescriptorWriter.Percent(100)), ("Sz  ", DescriptorWriter.Pixels(6)), ("Clr ", DescriptorWriter.Rgb(200, 30, 30)), ("overprint", false)),
            Effect("ebbl", ("hglM", BlendMode("scrn")), ("hglC", DescriptorWriter.Rgb(255, 255, 255)), ("hglO", DescriptorWriter.Percent(75)), ("sdwM", BlendMode("mul ")),
                ("sdwC", DescriptorWriter.Rgb(0, 0, 0)), ("sdwO", DescriptorWriter.Percent(75)), ("bvlT", new DescriptorWriter.EnumValue("bvlT", "SfBL")),
                ("bvlS", new DescriptorWriter.EnumValue("BESl", "InrB")), ("uglg", false), ("lagl", new DescriptorWriter.Unit("#Ang", 120)), ("Lald", new DescriptorWriter.Unit("#Ang", 30)),
                ("srgR", DescriptorWriter.Percent(100)), ("blur", DescriptorWriter.Pixels(14)), ("Sftn", DescriptorWriter.Pixels(2)), ("bvlD", new DescriptorWriter.EnumValue("BESs", "In  "))))));
        builder.Layers.Add(Blobs("Styled 2", new PsdRect(1200, 200, 2300, 1400), 5, Effects(
            Effect("SoFi", ("Md  ", BlendMode("Nrml")), ("Clr ", DescriptorWriter.Rgb(40, 120, 220)), ("Opct", DescriptorWriter.Percent(80))),
            Effect("FrFX", ("Styl", new DescriptorWriter.EnumValue("FStl", "CtrF")), ("PntT", new DescriptorWriter.EnumValue("FrFl", "SClr")), ("Md  ", BlendMode("Nrml")),
                ("Opct", DescriptorWriter.Percent(100)), ("Sz  ", DescriptorWriter.Pixels(4)), ("Clr ", DescriptorWriter.Rgb(255, 255, 255)), ("overprint", false)),
            Effect("ebbl", ("hglM", BlendMode("scrn")), ("hglC", DescriptorWriter.Rgb(255, 255, 255)), ("hglO", DescriptorWriter.Percent(75)), ("sdwM", BlendMode("mul ")),
                ("sdwC", DescriptorWriter.Rgb(0, 0, 0)), ("sdwO", DescriptorWriter.Percent(75)), ("bvlT", new DescriptorWriter.EnumValue("bvlT", "PrBL")),
                ("bvlS", new DescriptorWriter.EnumValue("BESl", "InrB")), ("uglg", false), ("lagl", new DescriptorWriter.Unit("#Ang", 135)), ("Lald", new DescriptorWriter.Unit("#Ang", 40)),
                ("srgR", DescriptorWriter.Percent(150)), ("blur", DescriptorWriter.Pixels(10)), ("bvlD", new DescriptorWriter.EnumValue("BESs", "In  "))))));
        for (var c = 0; c < 3; c++)
        {
            builder.MergedChannels.Add(PsdBuilder.Plane8(width, height, 235));
        }

        return builder.Build();
    }

    private static DescriptorWriter.EnumValue BlendMode(string key) => DescriptorWriter.BlendMode(key);

    private static (string Key, DescriptorWriter Value) Effect(string kind, params (string Key, object Value)[] items)
    {
        var effect = new DescriptorWriter(kind).Add("enab", true);
        foreach (var (key, value) in items)
        {
            effect.Add(key, value);
        }

        return (kind, effect);
    }

    private static byte[] Effects(params (string Key, DescriptorWriter Value)[] effects)
    {
        var root = new DescriptorWriter("null").Add("masterFXSwitch", true);
        foreach (var (key, value) in effects)
        {
            root.Add(key, value);
        }

        return root.Versioned(0);
    }

    /// <summary>A layer of soft-edged discs on a grid, colored by position, with the given effects block.</summary>
    private static BuilderLayer Blobs(string name, PsdRect rect, int seed, byte[] lfx2)
    {
        var layer = new BuilderLayer { Name = name, Rect = rect };
        layer.Channels[0] = PsdBuilder.Plane8(rect.Width, rect.Height, (x, y) => Sample(x, y, seed, 0));
        layer.Channels[1] = PsdBuilder.Plane8(rect.Width, rect.Height, (x, y) => Sample(x, y, seed, 1));
        layer.Channels[2] = PsdBuilder.Plane8(rect.Width, rect.Height, (x, y) => Sample(x, y, seed, 2));
        layer.Channels[-1] = PsdBuilder.Plane8(rect.Width, rect.Height, (x, y) => Coverage(x, y, seed));
        layer.Blocks.Add(("lfx2", lfx2));
        return layer;
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
