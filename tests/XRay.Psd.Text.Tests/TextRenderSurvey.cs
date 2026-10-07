using System.Globalization;
using System.Text;
using XRay.Psd.Tests.Support;
using XRay.Psd.Text.Tests.Support;

namespace XRay.Psd.Text.Tests;

/// <summary>
/// Diagnostic survey (opt-in with XRAY_PSD_TEXT_SURVEY=1): re-renders every type layer of
/// every committed fixture with the bundled font and writes the comparison with Photoshop's
/// own raster to test-output/text-survey.txt, plus side-by-side PNGs.
/// </summary>
public sealed class TextRenderSurvey
{
    [Fact]
    public void Survey()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("XRAY_PSD_TEXT_SURVEY") == "1", "Set XRAY_PSD_TEXT_SURVEY=1 to run the text survey.");
        // Characters the bundled face lacks (CJK) come from local-test-fixtures/fonts when present.
        using var renderer = new TextLayerRenderer(TestFonts.Settings(localGlyphs: true));
        var report = new StringBuilder();
        foreach (var name in Fixtures.AllDocuments())
        {
            var document = Fixtures.Load(name);
            foreach (var layer in document.EnumerateLayersTopDown().Where(l => l.Text is not null))
            {
                var missing = renderer.FindMissingFonts(layer.Text!);
                var label = $"{name} [{layer.Name}]";
                var raster = renderer.Render(layer);
                if (raster is null)
                {
                    report.AppendLine(CultureInfo.InvariantCulture, $"{label,-70} no output");
                    continue;
                }

                var match = TextComparison.Compare(layer, raster);
                var note = missing.Count > 0 ? $" (fallback for {string.Join(", ", missing)})" : string.Empty;
                report.AppendLine(CultureInfo.InvariantCulture, $"{label,-70} {match}{note}");
                var safe = string.Concat(layer.Name.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
                Fixtures.SaveArtifact($"text-{Path.GetFileNameWithoutExtension(name)}-{safe}.png", TextComparison.SideBySide(layer, raster));
            }
        }

        var root = new DirectoryInfo(Fixtures.RootDirectory).Parent!.Parent!.Parent!.FullName;
        Directory.CreateDirectory(Path.Combine(root, "test-output"));
        File.WriteAllText(Path.Combine(root, "test-output", "text-survey.txt"), report.ToString());
    }

    /// <summary>Opt-in (XRAY_PSD_TEXT_PROFILE=fixture.psd): column and row alpha sums of stored versus re-rendered pixels.</summary>
    [Fact]
    public void Profile()
    {
        var name = Environment.GetEnvironmentVariable("XRAY_PSD_TEXT_PROFILE");
        Assert.SkipWhen(string.IsNullOrEmpty(name), "Set XRAY_PSD_TEXT_PROFILE=<fixture> to dump ink profiles.");
        using var renderer = new TextLayerRenderer(TestFonts.Settings(localGlyphs: true));
        var report = new StringBuilder();
        foreach (var layer in Fixtures.Load(name!).EnumerateLayersTopDown().Where(l => l.Text is not null))
        {
            var raster = renderer.Render(layer)!;
            var stored = layer.GetPixels()!;
            var rect = layer.Bounds.Union(raster.Bounds);
            int A(XRay.Psd.Imaging.RgbaImage image, PsdRect r, int x, int y) => r.Contains(x, y) ? image.Pixels[((((y - r.Top) * image.Width) + (x - r.Left)) * 4) + 3] : 0;
            report.AppendLine(CultureInfo.InvariantCulture, $"[{layer.Name}] stored {layer.Bounds} rendered {raster.Bounds}");
            report.AppendLine("columns x: stored rendered");
            for (var x = rect.Left; x < rect.Right; x++)
            {
                int s = 0, r = 0;
                for (var y = rect.Top; y < rect.Bottom; y++)
                {
                    s += A(stored, layer.Bounds, x, y);
                    r += A(raster.Image, raster.Bounds, x, y);
                }

                report.AppendLine(CultureInfo.InvariantCulture, $"  {x}: {s} {r}");
            }

            report.AppendLine("rows y: stored rendered");
            for (var y = rect.Top; y < rect.Bottom; y++)
            {
                int s = 0, r = 0;
                for (var x = rect.Left; x < rect.Right; x++)
                {
                    s += A(stored, layer.Bounds, x, y);
                    r += A(raster.Image, raster.Bounds, x, y);
                }

                report.AppendLine(CultureInfo.InvariantCulture, $"  {y}: {s} {r}");
            }
        }

        var root = new DirectoryInfo(Fixtures.RootDirectory).Parent!.Parent!.Parent!.FullName;
        File.WriteAllText(Path.Combine(root, "test-output", "text-profile.txt"), report.ToString());
    }
}
