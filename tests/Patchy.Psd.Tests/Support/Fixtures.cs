using Patchy.Psd.Imaging;

namespace Patchy.Psd.Tests.Support;

/// <summary>Locates the committed PSD fixtures (tests/fixtures/psd) from the test output directory.</summary>
internal static class Fixtures
{
    private static readonly Lazy<string> Root = new(() =>
    {
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

        throw new DirectoryNotFoundException("tests/fixtures/psd not found above " + AppContext.BaseDirectory);
    });

    public static string RootDirectory => Root.Value;

    public static string PathOf(string name) => System.IO.Path.Combine(Root.Value, name);

    public static PsdDocument Load(string name) => PsdDocument.Load(PathOf(name));

    public static IEnumerable<string> AllDocuments() =>
        System.IO.Directory.EnumerateFiles(Root.Value)
            .Where(f => f.EndsWith(".psd", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".psb", StringComparison.OrdinalIgnoreCase))
            .Select(System.IO.Path.GetFileName)
            .OfType<string>()
            .Order(StringComparer.Ordinal);

    /// <summary>Writes a diagnostic image under test-output/ at the repository root.</summary>
    public static void SaveArtifact(string name, RgbaImage image)
    {
        var root = new DirectoryInfo(Root.Value).Parent!.Parent!.Parent!.FullName;
        var output = System.IO.Path.Combine(root, "test-output");
        System.IO.Directory.CreateDirectory(output);
        image.SavePng(System.IO.Path.Combine(output, name));
    }
}
