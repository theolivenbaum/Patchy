using BenchmarkDotNet.Running;
using XRay.Psd.Benchmarks;

// dotnet run -c Release --project benchmarks/XRay.Psd.Benchmarks -- --filter "*"
// Add "--job short" for a quick pass; see docs/performance.md.
// "--write-synthetic <path>" saves the generated large document for inspection.
if (args is ["--write-synthetic", var path])
{
    File.WriteAllBytes(path, BenchmarkData.Bytes(BenchmarkData.Synthetic));
    return;
}

BenchmarkSwitcher.FromAssembly(typeof(LoadBenchmarks).Assembly).Run(args);
