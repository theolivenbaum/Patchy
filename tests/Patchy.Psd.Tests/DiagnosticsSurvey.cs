using Patchy.Psd.Rendering;
using Patchy.Psd.Tests.Support;

namespace Patchy.Psd.Tests;

/// <summary>
/// Prints how the layer compositor compares with Photoshop's merged image and
/// reference BMP for every fixture. Informational only; set PATCHY_PSD_SURVEY=1.
/// </summary>
public sealed class DiagnosticsSurvey(ITestOutputHelper output)
{
    [Fact]
    public void Survey()
    {
        if (Environment.GetEnvironmentVariable("PATCHY_PSD_SURVEY") != "1")
        {
            return;
        }

        var lines = new List<string>();
        foreach (var name in Fixtures.AllDocuments())
        {
            try
            {
                var document = Fixtures.Load(name);
                var merged = document.Render(new RenderOptions { Source = RenderSource.MergedImage });
                var layers = document.Render(new RenderOptions { Source = RenderSource.Layers });
                var line = $"{name,-55} {(document.HasRealMergedImage ? "real" : "PLCH")} layers-vs-merged {ImageTools.Compare(layers, merged)}";
                var bmp = Path.ChangeExtension(Fixtures.PathOf(name), ".bmp");
                if (File.Exists(bmp))
                {
                    var reference = ImageTools.ReadBmp(bmp);
                    if (reference.Width == merged.Width && reference.Height == merged.Height)
                    {
                        line += $" | merged-vs-bmp {ImageTools.Compare(merged, reference)} | layers-vs-bmp {ImageTools.Compare(layers, reference)}";
                    }
                }

                output.WriteLine(line);
                lines.Add(line);
                Fixtures.SaveArtifact(Path.GetFileNameWithoutExtension(name) + ".layers.png", layers);
                Fixtures.SaveArtifact(Path.GetFileNameWithoutExtension(name) + ".merged.png", merged);
            }
            catch (Exception ex)
            {
                lines.Add($"{name,-55} FAILED {ex.GetType().Name}: {ex.Message}");
            }
        }

        File.WriteAllLines(Path.Combine(Fixtures.RootDirectory, "..", "..", "..", "test-output", "survey.txt"), lines);
    }
}
