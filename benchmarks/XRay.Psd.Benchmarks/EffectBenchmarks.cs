using BenchmarkDotNet.Attributes;
using XRay.Psd.Imaging;
using XRay.Psd.Rendering;

namespace XRay.Psd.Benchmarks;

/// <summary>
/// The layer compositor on effect-heavy fixtures (styled type layers, bevels with
/// textures, multiple strokes, shape strokes) at one thread and at every processor.
/// </summary>
[MemoryDiagnoser]
public class EffectBenchmarks
{
    private PsdDocument _document = null!;

    [Params(BenchmarkData.SyntheticEffects, "qual_rca_pinout.psd", "photoshop-bevel-texture-clouds.psd", "photoshop-lmfx-multi-stroke.psd", "photoshop-shape-strokes.psd")]
    public string Document { get; set; } = string.Empty;

    [Params(1, 0)]
    public int Degree { get; set; }

    [GlobalSetup]
    public void Setup() => _document = PsdDocument.Load(BenchmarkData.Bytes(Document));

    [Benchmark]
    public RgbaImage LayerComposite() => _document.Render(new RenderOptions { Source = RenderSource.Layers, MaxDegreeOfParallelism = Degree });
}
