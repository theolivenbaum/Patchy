using SkiaSharp;
using XRay.Psd.Imaging;
using XRay.Psd.Layers;
using XRay.Psd.Rendering;

namespace XRay.Psd.Text.Tests.Support;

/// <summary>The bundled Liberation Sans face (metric-compatible with Arial) and renderers that use only it.</summary>
internal static class TestFonts
{
    private static readonly Lazy<FontCollection> Collection = new(() =>
    {
        var collection = new FontCollection();
        collection.AddFile(Path.Combine(AppContext.BaseDirectory, "Fonts", "LiberationSans-Regular.ttf"));
        return collection;
    });

    /// <summary>Resolves ArialMT (and its aliases) to the bundled face; nothing else resolves.</summary>
    public static FontCollection Fonts => Collection.Value;

    public static SKTypeface LiberationSans => Fonts.Typefaces[0];

    /// <summary>Settings that never touch system fonts: bundled face, bundled fallback, no per-glyph system fallback.</summary>
    public static TextRenderSettings Settings(bool snap = true, bool localGlyphs = false) => new()
    {
        FontResolver = Fonts,
        FallbackTypeface = LiberationSans,
        GlyphFallbacks = localGlyphs ? LocalFonts.Typefaces : [],
        UseSystemGlyphFallback = false,
        SnapToPixelGrid = snap,
    };

    private static readonly Lazy<FontCollection> Local = new(() =>
    {
        var collection = new FontCollection();
        var directory = Path.Combine(new DirectoryInfo(XRay.Psd.Tests.Support.Fixtures.RootDirectory).Parent!.Parent!.Parent!.FullName, "local-test-fixtures", "fonts");
        if (Directory.Exists(directory))
        {
            foreach (var file in Directory.EnumerateFiles(directory).Order(StringComparer.Ordinal))
            {
                if (file.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".otf", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase))
                {
                    collection.AddFile(file);
                }
            }
        }

        return collection;
    });

    /// <summary>
    /// Fonts a developer copied into local-test-fixtures/fonts (gitignored), such as a CJK face
    /// for the vertical type fixtures. Tests that need them skip when the folder is empty.
    /// </summary>
    public static FontCollection LocalFonts => Local.Value;

    /// <summary>A local face that can draw the code point, if any.</summary>
    public static bool HasLocalGlyph(int codePoint) =>
        LocalFonts.Typefaces.Any(face => { using var font = new SKFont(face); return font.ContainsGlyph(codePoint); });
}

/// <summary>How a re-rendered type layer compares with the pixels Photoshop stored for it.</summary>
/// <param name="Iou">Intersection over union of the two ink masks (alpha at least 128).</param>
/// <param name="MeanAlphaError">Mean absolute alpha difference (0-255) over the union of both ink boxes.</param>
/// <param name="CoverageRatio">Total re-rendered alpha over total stored alpha.</param>
/// <param name="Left">Ink box edge deltas in pixels (re-rendered minus stored).</param>
public sealed record TextMatch(double Iou, double MeanAlphaError, double CoverageRatio, int Left, int Top, int Right, int Bottom, PsdRect StoredInk, PsdRect RenderedInk)
{
    public int MaxEdgeDelta => Math.Max(Math.Max(Math.Abs(Left), Math.Abs(Right)), Math.Max(Math.Abs(Top), Math.Abs(Bottom)));

    public override string ToString() =>
        FormattableString.Invariant($"IoU {Iou:0.000}, mean |da| {MeanAlphaError:0.0}, coverage {CoverageRatio:0.000}, edges L{Left:+0;-0;0} T{Top:+0;-0;0} R{Right:+0;-0;0} B{Bottom:+0;-0;0}");
}

internal static class TextComparison
{
    /// <summary>Compares the layer's stored alpha with a re-rendered raster in document space.</summary>
    public static TextMatch Compare(PsdLayer layer, TextLayerRaster raster)
    {
        var stored = layer.GetPixels() ?? new RgbaImage(0, 0);
        var storedRect = layer.Bounds;
        var renderedRect = raster.Bounds;
        var union = storedRect.Union(renderedRect);

        int StoredAlpha(int x, int y) => storedRect.Contains(x, y) ? stored.Pixels[((((y - storedRect.Top) * stored.Width) + (x - storedRect.Left)) * 4) + 3] : 0;
        int RenderedAlpha(int x, int y) => renderedRect.Contains(x, y) ? raster.Image.Pixels[((((y - renderedRect.Top) * raster.Image.Width) + (x - renderedRect.Left)) * 4) + 3] : 0;

        long both = 0, either = 0, sumStored = 0, sumRendered = 0, error = 0;
        var storedInk = default(PsdRect);
        var renderedInk = default(PsdRect);
        for (var y = union.Top; y < union.Bottom; y++)
        {
            for (var x = union.Left; x < union.Right; x++)
            {
                var a = StoredAlpha(x, y);
                var b = RenderedAlpha(x, y);
                sumStored += a;
                sumRendered += b;
                error += Math.Abs(a - b);
                if (a >= 128 && b >= 128)
                {
                    both++;
                }

                if (a >= 128 || b >= 128)
                {
                    either++;
                }

                if (a >= 64)
                {
                    storedInk = storedInk.Union(new PsdRect(x, y, x + 1, y + 1));
                }

                if (b >= 64)
                {
                    renderedInk = renderedInk.Union(new PsdRect(x, y, x + 1, y + 1));
                }
            }
        }

        var inkUnion = storedInk.Union(renderedInk);
        var area = Math.Max(1L, (long)inkUnion.Width * inkUnion.Height);
        long inkError = 0;
        for (var y = inkUnion.Top; y < inkUnion.Bottom; y++)
        {
            for (var x = inkUnion.Left; x < inkUnion.Right; x++)
            {
                inkError += Math.Abs(StoredAlpha(x, y) - RenderedAlpha(x, y));
            }
        }

        return new TextMatch(
            either == 0 ? 0 : (double)both / either,
            (double)inkError / area,
            sumStored == 0 ? 0 : (double)sumRendered / sumStored,
            renderedInk.Left - storedInk.Left,
            renderedInk.Top - storedInk.Top,
            renderedInk.Right - storedInk.Right,
            renderedInk.Bottom - storedInk.Bottom,
            storedInk,
            renderedInk);
    }

    /// <summary>Stored pixels (left), re-rendered (middle) and their alpha difference (right) for a visual check.</summary>
    public static RgbaImage SideBySide(PsdLayer layer, TextLayerRaster raster)
    {
        var rect = layer.Bounds.Union(raster.Bounds);
        var pad = 4;
        rect = new PsdRect(rect.Left - pad, rect.Top - pad, rect.Right + pad, rect.Bottom + pad);
        var w = rect.Width;
        var output = new RgbaImage(w * 3, rect.Height);
        var stored = layer.GetPixels();
        for (var y = rect.Top; y < rect.Bottom; y++)
        {
            for (var x = rect.Left; x < rect.Right; x++)
            {
                var a = stored is not null && layer.Bounds.Contains(x, y) ? stored.Pixels[((((y - layer.Bounds.Top) * stored.Width) + (x - layer.Bounds.Left)) * 4) + 3] : 0;
                var b = raster.Bounds.Contains(x, y) ? raster.Image.Pixels[((((y - raster.Bounds.Top) * raster.Image.Width) + (x - raster.Bounds.Left)) * 4) + 3] : 0;
                var row = y - rect.Top;
                var col = x - rect.Left;
                output.SetPixel(col, row, Gray(255 - a));
                output.SetPixel(col + w, row, Gray(255 - b));
                var d = b - a;
                output.SetPixel(col + (2 * w), row, d >= 0 ? new PsdColor((byte)(255 - d), (byte)(255 - d), 255) : new PsdColor(255, (byte)(255 + d), (byte)(255 + d)));
            }
        }

        var scale = Math.Clamp(900 / Math.Max(1, output.Width), 1, 8);
        if (scale == 1)
        {
            return output;
        }

        var scaled = new RgbaImage(output.Width * scale, output.Height * scale);
        for (var y = 0; y < scaled.Height; y++)
        {
            for (var x = 0; x < scaled.Width; x++)
            {
                scaled.SetPixel(x, y, output.GetPixel(x / scale, y / scale));
            }
        }

        return scaled;
    }

    private static PsdColor Gray(int v) => new((byte)v, (byte)v, (byte)v);
}
