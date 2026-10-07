using SharpFuzz;
using XRay.Psd;
using XRay.Psd.Rendering;

// Fuzz target for PsdDocument.Load, Render and ExtractText. Only
// PsdFormatException is an accepted outcome; any other exception escapes and
// the fuzzer records the input as a crash.
//
//   XRay.Psd.Fuzz                 libFuzzer mode (run under libfuzzer-dotnet)
//   XRay.Psd.Fuzz --afl           AFL mode (run under afl-fuzz)
//   XRay.Psd.Fuzz <file>...       replay inputs without a fuzzer (reproduce a crash)
if (args.Length > 0 && args[0] == "--afl")
{
    Fuzzer.OutOfProcess.Run(stream =>
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        Target.Run(buffer.ToArray());
    });
}
else if (args.Length > 0)
{
    foreach (var path in args)
    {
        Target.Run(File.ReadAllBytes(path));
        Console.WriteLine($"ok {path}");
    }
}
else
{
    Fuzzer.LibFuzzer.Run(span => Target.Run(span.ToArray()));
}

internal static class Target
{
    // Rendering cost grows with the canvas; a mutated header can claim a
    // 30000 x 30000 document. Those still load and extract text, but rendering
    // them only measures allocation speed, so stay below this many pixels.
    private const long MaxRenderPixels = 1 << 22;

    public static void Run(byte[] data)
    {
        try
        {
            var document = PsdDocument.Load(data);
            _ = document.ExtractText();
            var resources = document.Resources;
            _ = resources.Resolution;
            _ = resources.GridAndGuides;
            _ = resources.Thumbnail;
            _ = resources.Slices;
            _ = resources.LayerComps;
            _ = resources.VersionInfo;
            _ = resources.IccProfile;
            foreach (var layer in document.Layers)
            {
                _ = layer.Style?.Effects.Count;
            }

            if ((long)document.Width * document.Height <= MaxRenderPixels)
            {
                _ = document.Render();
                _ = document.Render(new RenderOptions { Source = RenderSource.Layers });
            }
        }
        catch (PsdFormatException)
        {
            // Rejecting damaged input is the expected outcome.
        }
    }
}
