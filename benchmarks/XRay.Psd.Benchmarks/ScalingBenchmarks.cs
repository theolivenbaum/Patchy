using BenchmarkDotNet.Attributes;
using XRay.Psd.Imaging;
using XRay.Psd.Rendering;

namespace XRay.Psd.Benchmarks;

/// <summary>
/// The same work on one thread and on every processor (<see cref="RenderOptions.MaxDegreeOfParallelism"/>;
/// encoders take the degree from the ambient scope). Degree 0 means the processor count.
/// </summary>
[MemoryDiagnoser]
public class ScalingBenchmarks
{
    private PsdDocument _document = null!;
    private RgbaImage _image = null!;

    [Params("qual_rca_pinout.psd", BenchmarkData.Synthetic)]
    public string Document { get; set; } = string.Empty;

    [Params(1, 0)]
    public int Degree { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _document = PsdDocument.Load(BenchmarkData.Bytes(Document));
        _image = _document.Render(new RenderOptions { Source = RenderSource.Layers });
    }

    [Benchmark]
    public RgbaImage LayerComposite() => _document.Render(new RenderOptions { Source = RenderSource.Layers, MaxDegreeOfParallelism = Degree });

    [Benchmark]
    public RgbaImage MergedDecode() => _document.Render(new RenderOptions { Source = RenderSource.MergedImage, MaxDegreeOfParallelism = Degree });

    [Benchmark]
    public byte[] Png()
    {
        using var scope = Parallelism.Use(Degree);
        return _image.ToPng(dropAlphaIfOpaque: false);
    }

    [Benchmark]
    public byte[] Jpeg()
    {
        using var scope = Parallelism.Use(Degree);
        return _image.ToJpeg();
    }
}

/// <summary>Path-based loading: read into an array, or memory-map.</summary>
[MemoryDiagnoser]
public class FileLoadBenchmarks
{
    private string _path = string.Empty;

    [Params("qual_rca_pinout.psd", BenchmarkData.Synthetic)]
    public string Document { get; set; } = string.Empty;

    [GlobalSetup]
    public void Setup() => _path = BenchmarkData.TempFile(Document);

    [GlobalCleanup]
    public void Cleanup() => File.Delete(_path);

    [Benchmark(Baseline = true)]
    public int LoadPath() => PsdDocument.Load(_path).Layers.Count;

    [Benchmark]
    public int LoadPathMapped()
    {
        using var document = PsdDocument.Load(_path, new PsdLoadOptions { MemoryMap = true });
        return document.Layers.Count;
    }

    [Benchmark]
    public int LoadPathAsync() => PsdDocument.LoadAsync(_path).GetAwaiter().GetResult().Layers.Count;
}
