namespace XRay.Psd.Tests.Support;

/// <summary>
/// Optional fixtures that are not committed: the psd-tools test collection
/// (MIT) checked out into local-test-fixtures/psd-tools as described in
/// docs/testing.md. Tests that need them skip when the checkout is absent.
/// </summary>
internal static class LocalFixtures
{
    private static readonly Lazy<string> PsdToolsRoot = new(() =>
        Path.Combine(new DirectoryInfo(Fixtures.RootDirectory).Parent!.Parent!.Parent!.FullName, "local-test-fixtures", "psd-tools", "tests", "psd_files"));

    /// <summary>The path of a psd-tools fixture (relative to tests/psd_files), skipping the test when it is missing.</summary>
    public static string PsdTools(string relativePath)
    {
        var path = Path.Combine(PsdToolsRoot.Value, relativePath);
        Assert.SkipUnless(File.Exists(path), $"local-test-fixtures/psd-tools is not checked out (see docs/testing.md); missing {relativePath}.");
        return path;
    }
}
