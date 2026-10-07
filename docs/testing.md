# Testing

How the test suite is organized, which fixtures it uses, and how to run the robustness checks.

## Suites

- `tests/XRay.Psd.Tests`: the core library. Every committed fixture in `tests/fixtures/psd` is parsed, decoded, truncated and mutated; renders are compared with Photoshop's merged image and reference BMPs (`RenderingTests`); formats without a committed fixture are covered by `Support/PsdBuilder.cs`, which writes synthetic PSD and PSB files.
- `tests/XRay.Psd.Skia.Tests`: SkiaSharp interop, Skia decoding of the built-in PNG and JPEG output, thumbnail decoding.
- `DiagnosticsSurvey` (opt-in, `XRAY_PSD_SURVEY=1`): compositor versus merged image versus BMP for every fixture, written to `test-output/survey.txt`.

## Robustness

The rule in CLAUDE.md is that only `PsdFormatException` may escape `PsdDocument.Load`, `Render` and `ExtractText`. Three layers check it.

1. `ParsingTests.Every_fixture_survives_truncation` cuts every fixture at 10, 50 and 90 percent.
2. `MutationFuzzTests` is a dependency-free randomized test. For each fixture up to 96 KB it applies six seeded mutations: bit flips, truncation, 16 and 32-bit boundary values, corrupted section and resource length fields, and random byte runs. Three times in four the mutation lands inside a structured payload (an image resource, a tagged block, blending ranges or the color mode data) found by loading the fixture and mapping the payload slices back to file offsets, because random offsets mostly hit pixel data. Seeds come from splitmix64 and an FNV-1a hash of the fixture name, so a failure names a seed that reproduces it. Each mutated file is loaded, its text extracted, its typed resources and layer styles read, and it is rendered from the merged image and from the layers (skipped when a mutated header claims a canvas far larger than the original). The default run takes a few seconds. A deeper local run covers every fixture:

   ```bash
   XRAY_PSD_MUTATIONS=200 dotnet test --project tests/XRay.Psd.Tests -c Release -- --filter-class "*MutationFuzzTests"
   ```

3. `fuzz/XRay.Psd.Fuzz` is a coverage-guided harness built on SharpFuzz (the only project allowed that dependency). See below.

A 150-iteration run over the small fixtures, a 60-iteration run over all of them, and 20 to 250 mutations of each file in the psd-tools collection found no escaping exception at the time of writing.

## Fuzzing with SharpFuzz

The harness runs the same sequence as the mutation test (load, text, resources, styles, both render paths) and treats any exception other than `PsdFormatException` as a crash. It has three modes: libFuzzer (default), AFL (`--afl`), and replay (`XRay.Psd.Fuzz file...`) for reproducing a saved crash without a fuzzer.

One-time setup:

```bash
dotnet tool install --global SharpFuzz.CommandLine
# libFuzzer driver for .NET: https://github.com/Metalnem/libfuzzer-dotnet/releases
```

Build, then instrument the library in the harness output folder (instrument again after every build):

```bash
dotnet build fuzz/XRay.Psd.Fuzz -c Release
sharpfuzz fuzz/XRay.Psd.Fuzz/bin/Release/net10.0/XRay.Psd.dll
```

Seed a corpus with small fixtures, then run libFuzzer:

```bash
mkdir -p local-test-fixtures/fuzz/corpus local-test-fixtures/fuzz/crashes
cp tests/fixtures/psd/photoshop-basic.psb tests/fixtures/psd/photoshop-shape-solid.psd local-test-fixtures/fuzz/corpus/
./libfuzzer-dotnet --target_path=fuzz/XRay.Psd.Fuzz/bin/Release/net10.0/XRay.Psd.Fuzz \
  -timeout=20 -rss_limit_mb=6144 -artifact_prefix=local-test-fixtures/fuzz/crashes/ local-test-fixtures/fuzz/corpus
```

or AFL++:

```bash
afl-fuzz -i local-test-fixtures/fuzz/corpus -o local-test-fixtures/fuzz/findings -t 20000 -m none \
  dotnet fuzz/XRay.Psd.Fuzz/bin/Release/net10.0/XRay.Psd.Fuzz.dll --afl
```

Replay a finding with `dotnet fuzz/XRay.Psd.Fuzz/bin/Release/net10.0/XRay.Psd.Fuzz.dll <file>`; the stack trace shows where the check is missing. Keep fixes narrow (an explicit bounds or count check that throws `PsdFormatException` or skips the block) and add the input, or a `PsdBuilder` reconstruction of it, as a test.

## Local fixtures (psd-tools)

The reference repository has no Photoshop-saved files in 16 or 32-bit, grayscale, indexed, Lab, duotone, multichannel or bitmap mode, and no embedded PSB smart object. The psd-tools project (MIT) has small single-feature files for all of them. Following the reference (`.reference/testy/fetch_psd_tools_corpus.py`), they are not committed: check them out into the gitignored `local-test-fixtures/`, pinned to the commit the reference uses.

```bash
mkdir -p local-test-fixtures/psd-tools && cd local-test-fixtures/psd-tools
git init -q && git remote add origin https://github.com/psd-tools/psd-tools.git
git sparse-checkout set --no-cone /tests/psd_files/
git fetch -q --depth 1 --filter=blob:none origin 605ee1284952c18c649ba60ebe73df4dabca9fbb
git checkout -q --detach FETCH_HEAD
```

`LocalCorpusTests` then runs: every `colormodes/` file loads with the expected mode and depth, its layer names are extracted, and the layer compositor matches the merged image; `smart-object-slice.psd` exposes its embedded PSB and text extraction recurses into it; `layer_comps.psd`, `metadata.psd` (guides) and `slices.psd` pin the typed resources. Without the checkout these tests are reported as skipped.
