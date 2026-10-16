using System.Buffers.Binary;
using XRay.Psd.Imaging;
using XRay.Psd.IO;
using XRay.Psd.Rendering;
using XRay.Psd.Tests.Support;

namespace XRay.Psd.Tests;

/// <summary>
/// Parallel and vectorized paths must reproduce the sequential, scalar results bit for bit.
/// The parallel runs lower the strip threshold to one item so that even small fixtures
/// split every row loop into many strips.
/// </summary>
public sealed class ParallelismTests
{
    private const int Degree = 8;

    public static TheoryData<string> AllFixtures() => ParsingTests.AllFixtures();

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void Parallel_and_sequential_renders_are_identical(string name)
    {
        var document = Fixtures.Load(name);
        var sequentialLayers = document.Render(new RenderOptions { Source = RenderSource.Layers, MaxDegreeOfParallelism = 1 });
        var sequentialMerged = document.Render(new RenderOptions { Source = RenderSource.MergedImage, MaxDegreeOfParallelism = 1 });

        var parallel = Fixtures.Load(name);
        using (Parallelism.Use(Degree, minimumStripWork: 1))
        {
            var layers = parallel.Render(new RenderOptions { Source = RenderSource.Layers, MaxDegreeOfParallelism = Degree });
            var merged = parallel.Render(new RenderOptions { Source = RenderSource.MergedImage, MaxDegreeOfParallelism = Degree });
            Assert.Equal(sequentialLayers.Pixels, layers.Pixels);
            Assert.Equal(sequentialMerged.Pixels, merged.Pixels);
        }
    }

    public static TheoryData<string> PsdToolsFixtures() => BandRenderingTests.PsdToolsFixtures();

    [Theory]
    [MemberData(nameof(PsdToolsFixtures))]
    public void Parallel_and_sequential_renders_are_identical_on_psd_tools_files(string name)
    {
        var path = Fixtures.PsdTools(name);
        var sequential = PsdDocument.Load(path).Render(new RenderOptions { Source = RenderSource.Layers, MaxDegreeOfParallelism = 1 });
        var parallel = PsdDocument.Load(path);
        using (Parallelism.Use(Degree, minimumStripWork: 1))
        {
            Assert.Equal(sequential.Pixels, parallel.Render(new RenderOptions { Source = RenderSource.Layers, MaxDegreeOfParallelism = Degree }).Pixels);
        }
    }

    [Fact]
    public void Ink_space_adjustments_are_identical_at_every_degree()
    {
        // levels_cmyk.psd builds the CMYK ink space (a parallel grid of refined nodes) and
        // runs four Levels layers through it.
        var path = Fixtures.PsdTools("adjustments/levels_cmyk.psd");
        RgbaImage sequential;
        using (Parallelism.Use(1))
        {
            sequential = PsdDocument.Load(path).Render(new RenderOptions { Source = RenderSource.Layers, MaxDegreeOfParallelism = 1 });
        }

        using (Parallelism.Use(Degree, minimumStripWork: 1))
        {
            var parallel = PsdDocument.Load(path).Render(new RenderOptions { Source = RenderSource.Layers, MaxDegreeOfParallelism = Degree });
            Assert.Equal(sequential.Pixels, parallel.Pixels);
        }
    }

    [Theory]
    [InlineData("qual_rca_pinout.psd")]
    [InlineData("photoshop-bevel-texture-clouds.psd")]
    public void Encoders_write_the_same_bytes_at_every_degree(string name)
    {
        var image = Fixtures.Load(name).Render(new RenderOptions { Source = RenderSource.Layers });
        byte[] png, jpeg, jpegSubsampled;
        using (Parallelism.Use(1))
        {
            png = image.ToPng(dropAlphaIfOpaque: false);
            jpeg = image.ToJpeg(95);
            jpegSubsampled = image.ToJpeg(60);
        }

        using (Parallelism.Use(Degree, minimumStripWork: 1))
        {
            Assert.Equal(png, image.ToPng(dropAlphaIfOpaque: false));
            Assert.Equal(jpeg, image.ToJpeg(95));
            Assert.Equal(jpegSubsampled, image.ToJpeg(60));
        }

        Assert.Equal(image.Pixels, PngDecoder.Decode(png).Pixels);
    }

    [Theory]
    [InlineData(PngCompressionLevelCase.Fastest)]
    [InlineData(PngCompressionLevelCase.Default)]
    public void Large_png_deflates_in_segments_and_round_trips(PngCompressionLevelCase level)
    {
        // 1200 x 1100 RGBA is about 5 MB of filtered rows: several parallel deflate segments.
        var image = new RgbaImage(1200, 1100);
        var random = new Random(7);
        for (var i = 0; i < image.Pixels.Length; i++)
        {
            image.Pixels[i] = (byte)((i % 4 == 3) ? 255 - (i / 4800) : ((i / 4) % 1200 / 5) + random.Next(3));
        }

        var codecLevel = level == PngCompressionLevelCase.Fastest ? Codecs.PngCompressionLevel.Fastest : Codecs.PngCompressionLevel.Default;
        byte[] sequential;
        using (Parallelism.Use(1))
        {
            sequential = image.ToPng(dropAlphaIfOpaque: false, codecLevel);
        }

        var parallel = image.ToPng(dropAlphaIfOpaque: false, codecLevel);
        Assert.Equal(sequential, parallel);
        Assert.Equal(image.Pixels, PngDecoder.Decode(parallel).Pixels);
    }

    public enum PngCompressionLevelCase
    {
        Fastest,
        Default,
    }

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    public void Prediction_undo_matches_the_scalar_definition(int depth)
    {
        var random = new Random(depth);
        foreach (var width in new[] { 1, 7, 8, 9, 15, 16, 17, 33, 100, 1027 })
        {
            const int height = 5;
            var bytesPerSample = depth / 8;
            var data = new byte[width * height * bytesPerSample];
            random.NextBytes(data);
            var expected = (byte[])data.Clone();
            ScalarUndoPrediction(expected, width, height, depth);

            using (Parallelism.Use(Degree, minimumStripWork: 1))
            {
                ChannelCodec.UndoPrediction(data, width, height, depth);
            }

            Assert.Equal(expected, data);
        }
    }

    [Fact]
    public void Rle_rows_decode_the_same_in_strips()
    {
        var random = new Random(3);
        const int width = 97;
        const int height = 61;
        var rows = new List<byte[]>();
        var expected = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                expected[(y * width) + x] = random.Next(3) == 0 ? (byte)random.Next(256) : (byte)(x / 9);
            }

            rows.Add(PsdBuilder.PackBits(expected.AsSpan(y * width, width)));
        }

        var counts = new byte[height * 2];
        for (var y = 0; y < height; y++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(counts.AsSpan(y * 2), (ushort)rows[y].Length);
        }

        var data = rows.SelectMany(row => row).ToArray();
        var output = new byte[width * height];
        using (Parallelism.Use(Degree, minimumStripWork: 1))
        {
            ChannelCodec.DecodeRle(data, counts, 2, height, width, output);
        }

        Assert.Equal(expected, output);

        // A truncated body leaves the rows past the damage zeroed, as the sequential walk did.
        var cut = new byte[width * height];
        using (Parallelism.Use(Degree, minimumStripWork: 1))
        {
            ChannelCodec.DecodeRle(data.AsSpan(0, data.Length / 2), counts, 2, height, width, cut);
        }

        Assert.Equal(expected.AsSpan(0, width * 10).ToArray(), cut.AsSpan(0, width * 10).ToArray());
        Assert.All(cut.AsSpan((height - 5) * width).ToArray(), value => Assert.Equal(0, value));
    }

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    public void Lab_vector_lanes_match_the_scalar_conversion(int depth)
    {
        var random = new Random(depth);
        const int count = 1003;
        float[] l = new float[count], a = new float[count], b = new float[count];
        for (var i = 0; i < count; i++)
        {
            l[i] = (float)random.NextDouble();
            a[i] = (float)random.NextDouble();
            b[i] = (float)random.NextDouble();
        }

        l[0] = 0f;
        a[1] = 1f;
        b[2] = 0.5f;
        var image = new PlanarImage(new PsdRect(0, 0, count, 1));
        using (Parallelism.Use(Degree, minimumStripWork: 1))
        {
            ColorSpaces.ToRgb(PsdColorMode.Lab, depth, [l, a, b], image, null);
        }

        for (var i = 0; i < count; i++)
        {
            var lightness = l[i] * 100.0;
            var aa = depth == 16 ? (a[i] * 65535.0 / 257.0) - 128.0 : (a[i] * 255.0) - 128.0;
            var bb = depth == 16 ? (b[i] * 65535.0 / 257.0) - 128.0 : (b[i] * 255.0) - 128.0;
            var (r, g, bl) = ColorSpaces.LabToSrgb(lightness, aa, bb);
            Assert.Equal((float)Math.Clamp(r, 0, 1), image.R[i]);
            Assert.Equal((float)Math.Clamp(g, 0, 1), image.G[i]);
            Assert.Equal((float)Math.Clamp(bl, 0, 1), image.B[i]);
        }
    }

    [Fact]
    public void Rgba_interleave_matches_per_plane_rounding()
    {
        var random = new Random(11);
        foreach (var width in new[] { 1, 7, 8, 9, 31, 64, 129 })
        {
            var image = new PlanarImage(new PsdRect(3, 2, 3 + width, 6));
            foreach (var plane in new[] { image.R, image.G, image.B, image.A })
            {
                for (var i = 0; i < plane.Length; i++)
                {
                    plane[i] = random.Next(10) switch
                    {
                        0 => -0.25f,
                        1 => 1.5f,
                        2 => 0f,
                        3 => float.NaN,
                        _ => (float)random.NextDouble(),
                    };
                }
            }

            var rect = new PsdRect(0, 0, width + 5, 7);
            RgbaImage rgba;
            using (Parallelism.Use(Degree, minimumStripWork: 1))
            {
                rgba = image.ToRgba(rect);
            }

            for (var y = rect.Top; y < rect.Bottom; y++)
            {
                for (var x = rect.Left; x < rect.Right; x++)
                {
                    var expected = PsdColor.Transparent;
                    if (image.Bounds.Contains(x, y))
                    {
                        var o = image.RowOffset(y, x);
                        var alpha = Byte(image.A[o]);
                        expected = alpha == 0 ? PsdColor.Transparent : new PsdColor(Byte(image.R[o]), Byte(image.G[o]), Byte(image.B[o]), alpha);
                    }

                    Assert.Equal(expected, rgba.GetPixel(x, y));
                }
            }
        }

        static byte Byte(float value)
        {
            var one = new byte[1];
            PlanarImage.UnitFloatToBytes([value], one);
            return one[0];
        }
    }

    [Theory]
    [InlineData("qual_rca_pinout.psd")]
    [InlineData("photoshop-gradient-overlay-geometry.psd")]
    public void Band_prefetch_and_png_rows_are_identical_at_every_degree(string name)
    {
        var document = Fixtures.Load(name);
        byte[] sequential;
        using (var stream = new MemoryStream())
        {
            document.RenderToPng(stream, new RenderOptions { Source = RenderSource.Layers, BandHeight = 50, MaxDegreeOfParallelism = 1 });
            sequential = stream.ToArray();
        }

        using (Parallelism.Use(Degree, minimumStripWork: 1))
        {
            using var stream = new MemoryStream();
            document.RenderToPng(stream, new RenderOptions { Source = RenderSource.Layers, BandHeight = 50, MaxDegreeOfParallelism = Degree });
            Assert.Equal(sequential, stream.ToArray());
        }
    }

    [Fact]
    public void Path_rasterizer_bands_and_row_windows_match_the_sequential_raster()
    {
        var path = StarPath(new Random(5), 23);
        var rect = new PsdRect(-7, -3, 181, 149);
        float[] sequential;
        using (Parallelism.Use(1))
        {
            sequential = PathRasterizer.RasterizePath(path, rect);
        }

        Assert.Contains(sequential, value => value > 0f && value < 1f);
        using (Parallelism.Use(Degree, minimumStripWork: 1))
        {
            Assert.Equal(sequential, PathRasterizer.RasterizePath(path, rect));

            // Any window of rows is the same rows of the whole raster.
            foreach (var (first, end) in new[] { (0, 1), (5, 6), (37, 90), (100, rect.Height), (rect.Height - 1, rect.Height) })
            {
                var window = PathRasterizer.RasterizePath(path, rect, first, end);
                Assert.Equal(sequential.AsSpan(first * rect.Width, (end - first) * rect.Width).ToArray(), window);
            }
        }
    }

    [Fact]
    public void Vector_stroker_bands_match_the_sequential_raster()
    {
        var path = StarPath(new Random(9), 17);
        var style = new Layers.VectorStrokeStyle { Enabled = true, Width = 5, Join = Layers.VectorStrokeJoin.Round, Cap = Layers.VectorStrokeCap.Round, Alignment = Layers.VectorStrokeAlignment.Outside };
        var clip = new PsdRect(0, 0, 200, 160);
        (float[] Coverage, PsdRect Rect) sequential;
        using (Parallelism.Use(1))
        {
            sequential = VectorStroker.Rasterize(path, style, clip);
        }

        using (Parallelism.Use(Degree, minimumStripWork: 1))
        {
            var parallel = VectorStroker.Rasterize(path, style, clip);
            Assert.Equal(sequential.Rect, parallel.Rect);
            Assert.Equal(sequential.Coverage, parallel.Coverage);
        }
    }

    /// <summary>Two overlapping star polygons with curved edges, in two shape groups (Add then Xor).</summary>
    private static Layers.VectorPath StarPath(Random random, int points)
    {
        var subpaths = new List<Layers.PathSubpath>();
        for (var group = 0; group < 2; group++)
        {
            var subpath = new Layers.PathSubpath { Closed = true, ShapeGroup = group, Operation = group == 0 ? Layers.PathCombineOperation.Add : Layers.PathCombineOperation.Xor };
            for (var i = 0; i < points; i++)
            {
                var angle = i * 2 * Math.PI / points;
                var radius = (i % 2 == 0 ? 70 : 30) + (random.NextDouble() * 10);
                var x = 90 + (group * 13.37) + (Math.Cos(angle) * radius);
                var y = 70 + (group * 7.1) + (Math.Sin(angle) * radius);
                subpath.Knots.Add(new Layers.PathKnot(x - 3.3, y + 1.7, x, y, x + 3.3, y - 1.7, false));
            }

            subpaths.Add(subpath);
        }

        return new Layers.VectorPath { Subpaths = subpaths };
    }

    [Fact]
    public void Exceptions_from_strips_surface_unwrapped()
    {
        using (Parallelism.Use(Degree, minimumStripWork: 1))
        {
            var error = Assert.Throws<PsdFormatException>(() => Parallelism.For(100, 1, (start, _) =>
            {
                if (start >= 50)
                {
                    throw new PsdFormatException("strip " + start);
                }
            }));
            Assert.StartsWith("strip ", error.Message, StringComparison.Ordinal);
        }
    }

    private static void ScalarUndoPrediction(byte[] data, int width, int height, int depth)
    {
        var rowBytes = width * depth / 8;
        for (var y = 0; y < height; y++)
        {
            var row = data.AsSpan(y * rowBytes, rowBytes);
            switch (depth)
            {
                case 8:
                    {
                        byte acc = 0;
                        for (var x = 0; x < row.Length; x++)
                        {
                            acc += row[x];
                            row[x] = acc;
                        }

                        break;
                    }

                case 16:
                    {
                        ushort acc = 0;
                        for (var x = 0; x < width; x++)
                        {
                            acc += BinaryPrimitives.ReadUInt16BigEndian(row[(x * 2)..]);
                            BinaryPrimitives.WriteUInt16BigEndian(row[(x * 2)..], acc);
                        }

                        break;
                    }

                default:
                    {
                        var planes = new byte[rowBytes];
                        byte acc = 0;
                        for (var i = 0; i < rowBytes; i++)
                        {
                            acc += row[i];
                            planes[i] = acc;
                        }

                        for (var x = 0; x < width; x++)
                        {
                            row[(x * 4) + 0] = planes[x];
                            row[(x * 4) + 1] = planes[width + x];
                            row[(x * 4) + 2] = planes[(2 * width) + x];
                            row[(x * 4) + 3] = planes[(3 * width) + x];
                        }

                        break;
                    }
            }
        }
    }
}
