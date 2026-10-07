using BenchmarkDotNet.Attributes;
using XRay.Psd.Imaging;
using XRay.Psd.Rendering;

namespace XRay.Psd.Benchmarks;

/// <summary>Parsing and text extraction (no pixel decoding).</summary>
[MemoryDiagnoser]
public class LoadBenchmarks
{
    private byte[] _bytes = [];
    private PsdDocument _document = null!;

    [Params("qual_rca_pinout.psd", "photoshop-bevel-texture-clouds.psd", BenchmarkData.Synthetic)]
    public string Document { get; set; } = string.Empty;

    [GlobalSetup]
    public void Setup()
    {
        _bytes = BenchmarkData.Bytes(Document);
        _document = PsdDocument.Load(_bytes);
    }

    [Benchmark]
    public PsdDocument Load() => PsdDocument.Load(_bytes);

    [Benchmark]
    public PsdDocument LoadStream()
    {
        using var stream = new MemoryStream(_bytes, writable: false);
        return PsdDocument.Load(stream);
    }

    [Benchmark]
    public int ExtractText() => _document.ExtractText().Items.Count;
}

/// <summary>Merged image decode, per-layer channel decode and the layer compositor.</summary>
[MemoryDiagnoser]
public class RenderBenchmarks
{
    private PsdDocument _document = null!;

    [Params("qual_rca_pinout.psd", "photoshop-bevel-texture-clouds.psd", "arrows.psd", BenchmarkData.Synthetic)]
    public string Document { get; set; } = string.Empty;

    [GlobalSetup]
    public void Setup() => _document = PsdDocument.Load(BenchmarkData.Bytes(Document));

    [Benchmark]
    public RgbaImage MergedDecode() => _document.GetMergedImage();

    [Benchmark]
    public int DecodeLayerPixels()
    {
        var total = 0;
        foreach (var layer in _document.Layers)
        {
            total += layer.GetPixels()?.Width ?? 0;
        }

        return total;
    }

    [Benchmark]
    public RgbaImage LayerComposite() => _document.Render(new RenderOptions { Source = RenderSource.Layers });
}

/// <summary>The built-in PNG and JPEG encoders on a rendered document.</summary>
[MemoryDiagnoser]
public class EncodeBenchmarks
{
    private RgbaImage _image = null!;

    [Params("qual_rca_pinout.psd", BenchmarkData.Synthetic)]
    public string Document { get; set; } = string.Empty;

    [GlobalSetup]
    public void Setup() => _image = PsdDocument.Load(BenchmarkData.Bytes(Document)).Render(new RenderOptions { Source = RenderSource.Layers });

    [Benchmark]
    public byte[] Png() => _image.ToPng(dropAlphaIfOpaque: false);

    [Benchmark]
    public byte[] Jpeg() => _image.ToJpeg();
}
