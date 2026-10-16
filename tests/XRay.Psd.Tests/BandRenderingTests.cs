using System.Buffers.Binary;
using XRay.Psd.Imaging;
using XRay.Psd.Rendering;
using XRay.Psd.Tests.Support;

namespace XRay.Psd.Tests;

/// <summary>
/// Band rendering (<see cref="PsdDocument.RenderBands"/>, <see cref="PsdDocument.RenderToPng(Stream, RenderOptions?, Codecs.PngCompressionLevel)"/>)
/// must reproduce the whole-canvas render bit for bit, whatever the band height.
/// </summary>
public sealed class BandRenderingTests
{
    public static TheoryData<string> AllFixtures() => ParsingTests.AllFixtures();

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void Bands_match_the_whole_render(string name)
    {
        var document = Fixtures.Load(name);
        foreach (var source in new[] { RenderSource.Layers, RenderSource.Auto })
        {
            var whole = document.Render(new RenderOptions { Source = source });

            // Odd band heights put band edges everywhere; tiny documents also render a row at a time.
            var heights = document.Height <= 160 ? new[] { 1, 7 } : new[] { 13, 97 };
            foreach (var height in heights)
            {
                var banded = Assemble(document, new RenderOptions { Source = source, BandHeight = height });
                Assert.True(whole.Pixels.AsSpan().SequenceEqual(banded.Pixels), $"{source} bands of {height} rows differ from the whole render (first difference at pixel {FirstDifference(whole, banded)})");
            }
        }
    }

    /// <summary>The committed psd-tools files: knockout, contours, gradient fills and ink-space adjustments.</summary>
    public static TheoryData<string> PsdToolsFixtures()
    {
        var root = Path.GetFullPath(Fixtures.PsdTools("."));
        var data = new TheoryData<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*.ps?", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(PsdToolsFixtures))]
    public void Bands_match_the_whole_render_on_psd_tools_files(string name)
    {
        var document = PsdDocument.Load(Fixtures.PsdTools(name));
        foreach (var source in new[] { RenderSource.Layers, RenderSource.Auto })
        {
            var whole = document.Render(new RenderOptions { Source = source });
            var height = document.Height <= 160 ? 1 : 13;
            var banded = Assemble(document, new RenderOptions { Source = source, BandHeight = height });
            Assert.True(whole.Pixels.AsSpan().SequenceEqual(banded.Pixels), $"{source} bands of {height} rows differ from the whole render (first difference at pixel {FirstDifference(whole, banded)})");
        }
    }

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void Banded_png_streams_the_encoder_output(string name)
    {
        var document = Fixtures.Load(name);
        var options = new RenderOptions { BandHeight = 29 };
        var whole = document.Render(options);
        using var stream = new MemoryStream();
        document.RenderToPng(stream, options);
        var png = stream.ToArray();

        // Same pixels, and the same zlib stream as the one-piece encoder with the same alpha choice.
        var opaque = PsdRenderer.UsesMergedImage(document, options) && !document.MergedImageHasTransparency;
        var reference = whole.ToPng(dropAlphaIfOpaque: opaque);
        Assert.Equal(Idat(reference), Idat(png));
        Assert.Equal(whole.Pixels, PngDecoder.Decode(png).Pixels);
    }

    [Fact]
    public void Large_banded_png_splits_into_deflate_segments()
    {
        // About 5 MB of filtered rows: several deflate segments, one IDAT chunk each.
        var document = PsdDocument.Load(new PsdBuilder { Width = 1300, Height = 1000, Layers = { Noise(1300, 1000) } }.Build());
        RenderOptions Options(int bandHeight, int degree) => new() { Source = RenderSource.Layers, BandHeight = bandHeight, Background = new PsdColor(10, 20, 30), MaxDegreeOfParallelism = degree };
        var whole = document.Render(Options(0, 1));
        byte[] sequential;
        using (var stream = new MemoryStream())
        {
            document.RenderToPng(stream, Options(77, 1));
            sequential = stream.ToArray();
        }

        using (Parallelism.Use(8, minimumStripWork: 1))
        {
            using var stream = new MemoryStream();
            document.RenderToPng(stream, Options(300, 8));
            Assert.Equal(sequential, stream.ToArray());
        }

        Assert.True(Chunks(sequential).Count(type => type == "IDAT") > 3);
        Assert.Equal(Idat(whole.ToPng()), Idat(sequential));
        Assert.Equal(whole.Pixels, PngDecoder.Decode(sequential).Pixels);
    }

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void Truncated_fixtures_band_like_the_whole_render(string name)
    {
        var bytes = File.ReadAllBytes(Fixtures.PathOf(name));
        foreach (var fraction in new[] { 0.3, 0.7, 0.95 })
        {
            var cut = bytes.AsMemory(0, (int)(bytes.Length * fraction));
            PsdDocument document;
            try
            {
                document = PsdDocument.Load(cut);
            }
            catch (PsdFormatException)
            {
                continue;
            }

            foreach (var source in new[] { RenderSource.Layers, RenderSource.MergedImage })
            {
                RgbaImage? whole = null;
                try
                {
                    whole = document.Render(new RenderOptions { Source = source });
                }
                catch (PsdFormatException)
                {
                    // A damaged file may be rejected; the band path must then fail the same way or degrade.
                }

                try
                {
                    var banded = Assemble(document, new RenderOptions { Source = source, BandHeight = 17 });
                    if (whole is not null)
                    {
                        Assert.True(whole.Pixels.AsSpan().SequenceEqual(banded.Pixels), $"{source} at {fraction}: first difference at pixel {FirstDifference(whole, banded)}");
                    }

                    using var png = new MemoryStream();
                    document.RenderToPng(png, new RenderOptions { Source = source, BandHeight = 17 });
                }
                catch (PsdFormatException)
                {
                    Assert.Null(whole);
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(MutationFuzzTests.SmallFixtures), MemberType = typeof(MutationFuzzTests))]
    public void Mutated_fixtures_band_without_other_exceptions(string name)
    {
        var original = File.ReadAllBytes(Fixtures.PathOf(name));
        var regions = MutationFuzzTests.StructuredRegions(original);
        for (var i = 0; i < 4; i++)
        {
            var (bytes, description) = MutationFuzzTests.Mutate(original, 0xBA4D0000UL + ((ulong)name.Length * 31) + (ulong)i, regions);
            try
            {
                var document = PsdDocument.Load(bytes);
                if ((long)document.Width * document.Height > Math.Max(1L << 20, original.Length * 4L))
                {
                    continue;
                }

                foreach (var source in new[] { RenderSource.Layers, RenderSource.Auto })
                {
                    foreach (var band in document.RenderBands(new RenderOptions { Source = source, BandHeight = 23 }))
                    {
                        Assert.Equal(document.Width, band.Image.Width);
                    }
                }

                using var png = new MemoryStream();
                document.RenderToPng(png, new RenderOptions { BandHeight = 23 });
            }
            catch (PsdFormatException)
            {
                // Rejecting damaged input is the expected outcome.
            }
            catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
            {
                Assert.Fail($"{name} ({description}): {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            }
        }
    }

    [Fact]
    public void Bands_are_identical_at_every_degree()
    {
        foreach (var name in new[] { "qual_rca_pinout.psd", "photoshop-bevel-texture-clouds.psd", "photoshop-shape-strokes.psd" })
        {
            var document = Fixtures.Load(name);
            var sequential = Assemble(document, new RenderOptions { Source = RenderSource.Layers, BandHeight = 64, MaxDegreeOfParallelism = 1 });
            using (Parallelism.Use(8, minimumStripWork: 1))
            {
                var parallel = Assemble(document, new RenderOptions { Source = RenderSource.Layers, BandHeight = 64, MaxDegreeOfParallelism = 8 });
                Assert.Equal(sequential.Pixels, parallel.Pixels);
            }
        }
    }

    [Fact]
    public void Synthetic_fills_masks_and_zip_channels_band_exactly()
    {
        // Feathered and unfeathered raster masks, ZIP-with-prediction channels and a
        // group, across many bands.
        var builder = new PsdBuilder { Width = 90, Height = 70, LayerCompression = PsdCompression.ZipPrediction };
        builder.Layers.Add(Noise(90, 70));
        var masked = Noise(60, 50, new PsdRect(10, 8, 70, 58), seed: 3);
        masked.MaskRect = new PsdRect(5, 5, 80, 60);
        masked.Channels[-2] = PsdBuilder.Plane8(75, 55, (x, y) => (byte)((x * 3) + y));
        masked.BlendKey = "mul ";
        builder.Layers.Add(masked);
        var feathered = Noise(50, 40, new PsdRect(30, 20, 80, 60), seed: 5);
        feathered.MaskRect = new PsdRect(30, 20, 80, 60);
        feathered.Channels[-2] = PsdBuilder.Plane8(50, 40, (x, y) => (byte)(((x / 10) + (y / 10)) % 2 * 255));
        feathered.MaskTail = FeatherParameters(4.5);
        feathered.MaskFlags = 0x10;
        builder.Layers.Add(feathered);
        var document = PsdDocument.Load(builder.Build());
        var whole = document.Render(new RenderOptions { Source = RenderSource.Layers });
        foreach (var height in new[] { 1, 3, 11, 70 })
        {
            Assert.Equal(whole.Pixels, Assemble(document, new RenderOptions { Source = RenderSource.Layers, BandHeight = height }).Pixels);
        }
    }

    private static RgbaImage Assemble(PsdDocument document, RenderOptions options)
    {
        var image = new RgbaImage(document.Width, document.Height);
        var next = 0;
        foreach (var band in document.RenderBands(options))
        {
            Assert.Equal(next, band.Top);
            Assert.Equal(document.Width, band.Image.Width);
            Assert.InRange(band.Image.Height, 1, options.BandHeight);
            band.Image.Pixels.CopyTo(image.Pixels, band.Top * document.Width * 4);
            next += band.Image.Height;
        }

        Assert.Equal(document.Height, next);
        return image;
    }

    private static int FirstDifference(RgbaImage a, RgbaImage b)
    {
        for (var i = 0; i < a.Pixels.Length; i++)
        {
            if (a.Pixels[i] != b.Pixels[i])
            {
                return i / 4;
            }
        }

        return -1;
    }

    /// <summary>The IHDR payload and the concatenated IDAT payloads (the zlib stream).</summary>
    private static byte[] Idat(byte[] png)
    {
        using var data = new MemoryStream();
        var offset = 8;
        while (offset < png.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset));
            var type = System.Text.Encoding.ASCII.GetString(png, offset + 4, 4);
            if (type is "IHDR" or "IDAT")
            {
                data.Write(png.AsSpan(offset + 8, length));
            }

            offset += 12 + length;
        }

        return data.ToArray();
    }

    private static IEnumerable<string> Chunks(byte[] png)
    {
        var offset = 8;
        while (offset < png.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset));
            yield return System.Text.Encoding.ASCII.GetString(png, offset + 4, 4);
            offset += 12 + length;
        }
    }

    private static BuilderLayer Noise(int width, int height, PsdRect? rect = null, int seed = 1)
    {
        var layer = new BuilderLayer { Name = $"noise {seed}", Rect = rect ?? new PsdRect(0, 0, width, height) };
        for (short c = 0; c < 3; c++)
        {
            var channel = c;
            layer.Channels[c] = PsdBuilder.Plane8(width, height, (x, y) => (byte)((x * (7 + channel + seed)) ^ (y * (13 + seed)) ^ (x * y)));
        }

        layer.Channels[-1] = PsdBuilder.Plane8(width, height, (x, y) => (byte)(255 - (((x + y) * seed) % 97)));
        return layer;
    }

    /// <summary>Mask parameters with a user-mask feather (parameter flag 0x02: a double after the flags byte).</summary>
    private static byte[] FeatherParameters(double feather)
    {
        var tail = new byte[9];
        tail[0] = 0x02;
        BinaryPrimitives.WriteDoubleBigEndian(tail.AsSpan(1), feather);
        return tail;
    }
}
