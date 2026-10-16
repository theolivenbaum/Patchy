using BenchmarkDotNet.Attributes;
using XRay.Psd.Rendering;

namespace XRay.Psd.Benchmarks;

/// <summary>Whole-canvas rendering against band rendering, as RGBA and straight into a PNG stream.</summary>
[MemoryDiagnoser]
public class BandBenchmarks
{
    private PsdDocument _document = null!;

    [Params(BenchmarkData.Synthetic, "qual_rca_pinout.psd")]
    public string Document { get; set; } = string.Empty;

    [GlobalSetup]
    public void Setup() => _document = PsdDocument.Load(BenchmarkData.Bytes(Document));

    [Benchmark(Baseline = true)]
    public byte[] WholeToPng() => _document.Render(new RenderOptions { Source = RenderSource.Layers }).ToPng(dropAlphaIfOpaque: false);

    [Benchmark]
    public int Bands()
    {
        var rows = 0;
        foreach (var band in _document.RenderBands(new RenderOptions { Source = RenderSource.Layers, BandHeight = 256 }))
        {
            rows += band.Image.Height;
        }

        return rows;
    }

    [Benchmark]
    public void BandsToPng() => _document.RenderToPng(Stream.Null, new RenderOptions { Source = RenderSource.Layers, BandHeight = 256 });
}
