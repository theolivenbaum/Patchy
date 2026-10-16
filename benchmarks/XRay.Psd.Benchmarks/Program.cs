using BenchmarkDotNet.Running;
using XRay.Psd.Benchmarks;

// dotnet run -c Release --project benchmarks/XRay.Psd.Benchmarks -- --filter "*"
// Add "--job short" for a quick pass; see docs/performance.md.
// "--write-synthetic <path>" saves the generated large document for inspection,
// "--write-synthetic-effects <path>" the generated effect-heavy one.
if (args is ["--write-synthetic", var path])
{
    File.WriteAllBytes(path, BenchmarkData.Bytes(BenchmarkData.Synthetic));
    return;
}

if (args is ["--write-synthetic-effects", var effectsPath])
{
    File.WriteAllBytes(effectsPath, BenchmarkData.Bytes(BenchmarkData.SyntheticEffects));
    return;
}

BenchmarkSwitcher.FromAssembly(typeof(LoadBenchmarks).Assembly).Run(args);
