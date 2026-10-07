using SkiaSharp;
using XRay.Psd.Imaging;
using XRay.Psd.Layers;
using XRay.Psd.Rendering;

namespace XRay.Psd.Text;

/// <summary>Settings for <see cref="TextLayerRenderer"/>.</summary>
public sealed class TextRenderSettings
{
    /// <summary>Maps a run's PostScript name to a typeface. Defaults to the installed fonts (<see cref="SystemFontResolver.Default"/>).</summary>
    public IFontResolver FontResolver { get; init; } = SystemFontResolver.Default;

    /// <summary>The face used for runs whose font does not resolve. Null uses Skia's default typeface.</summary>
    public SKTypeface? FallbackTypeface { get; init; }

    /// <summary>Faces tried, in order, for characters neither the run's face nor <see cref="FallbackTypeface"/> can draw (for example a CJK face).</summary>
    public IReadOnlyList<SKTypeface> GlyphFallbacks { get; init; } = [];

    /// <summary>Whether characters no configured face can draw may come from any installed font that has them (default true).</summary>
    public bool UseSystemGlyphFallback { get; init; } = true;

    /// <summary>
    /// Whether glyph origins snap to whole pixels on axis-aligned transforms, as Photoshop
    /// rasterizes (each glyph's x and each baseline rounded, halves up). Default true.
    /// </summary>
    public bool SnapToPixelGrid { get; init; } = true;

    /// <summary>Whether Warp Text is applied (default true).</summary>
    public bool ApplyWarp { get; init; } = true;
}

/// <summary>
/// Re-renders type layers from the parsed text model: font resolution through an
/// <see cref="IFontResolver"/>, HarfBuzz shaping, Photoshop's line, leading, tracking and
/// alignment model, the layer transform, Warp Text, and SkiaSharp rasterization of the
/// unhinted glyph outlines. Plug it into the compositor with <see cref="TextLayerRasterizer"/>.
/// Instances cache faces and glyph outlines; they are safe to use from one thread at a time.
/// </summary>
public sealed class TextLayerRenderer : IDisposable
{
    // Rasters larger than this (per side) are clipped to the document.
    private const int MaxRasterSide = 32768;
    private const long MaxRasterArea = 1L << 28;

    private readonly Dictionary<SKTypeface, FontFace> _faces = [];
    private readonly Dictionary<FontRequest, SKTypeface?> _resolved = [];
    private readonly Dictionary<int, FontFace?> _glyphFallback = [];
    private readonly object _gate = new();
    private SKTypeface? _defaultTypeface;

    public TextLayerRenderer(TextRenderSettings? settings = null)
    {
        Settings = settings ?? new TextRenderSettings();
    }

    public TextRenderSettings Settings { get; }

    /// <summary>
    /// Renders a type layer in document pixels, clipped to the document. Returns null for
    /// layers without text, without visible glyphs, or with an empty result.
    /// </summary>
    public TextLayerRaster? Render(PsdLayer layer)
    {
        ArgumentNullException.ThrowIfNull(layer);
        return layer.Text is { } text ? Render(text, layer.Document.Bounds) : null;
    }

    /// <summary>
    /// Renders text in document pixels, optionally clipped to <paramref name="clip"/>. Type on a
    /// path (<see cref="TextShapeKind.Other"/>) returns null: its path geometry is not decoded yet.
    /// </summary>
    public TextLayerRaster? Render(TextLayerInfo text, PsdRect? clip = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.ShapeKind == TextShapeKind.Other)
        {
            return null;
        }

        lock (_gate)
        {
            var layout = Layout(text);
            return Rasterize(text, layout, clip);
        }
    }

    /// <summary>The fonts of a type layer that do not resolve through <see cref="TextRenderSettings.FontResolver"/> (PostScript names).</summary>
    public IReadOnlyList<string> FindMissingFonts(TextLayerInfo text)
    {
        ArgumentNullException.ThrowIfNull(text);
        lock (_gate)
        {
            var missing = new SortedSet<string>(StringComparer.Ordinal);
            if (text.StyleRuns.Count == 0)
            {
                if (ResolveTypeface(null) is null)
                {
                    missing.Add("ArialMT");
                }
            }

            foreach (var run in text.StyleRuns)
            {
                if (ResolveTypeface(run) is null)
                {
                    missing.Add(run.FontName ?? run.FontFamily ?? "(none)");
                }
            }

            return [.. missing];
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var face in _faces.Values)
            {
                face.Dispose();
            }

            _faces.Clear();
            _defaultTypeface?.Dispose();
            _defaultTypeface = null;
        }
    }

    internal LaidOutText Layout(TextLayerInfo text)
    {
        var engine = new TextLayoutEngine(FaceForRun, FallbackForCodePoint);
        return engine.Layout(text);
    }

    // ---- Fonts ----

    private SKTypeface? ResolveTypeface(TextStyleRun? run)
    {
        var request = run is null ? FontRequest.FromPostScriptName("ArialMT") : FontRequest.FromRun(run);
        if (!_resolved.TryGetValue(request, out var typeface))
        {
            typeface = Settings.FontResolver.Resolve(request);
            _resolved[request] = typeface;
        }

        return typeface;
    }

    private (FontFace Face, bool Substituted) FaceForRun(TextStyleRun? run)
    {
        if (ResolveTypeface(run) is { } typeface)
        {
            return (FaceFor(typeface), false);
        }

        return (FaceFor(FallbackTypeface()), true);
    }

    private SKTypeface FallbackTypeface()
    {
        if (Settings.FallbackTypeface is { } fallback)
        {
            return fallback;
        }

        return _defaultTypeface ??= SKTypeface.CreateDefault();
    }

    private FontFace FaceFor(SKTypeface typeface)
    {
        if (!_faces.TryGetValue(typeface, out var face))
        {
            face = new FontFace(typeface);
            _faces[typeface] = face;
        }

        return face;
    }

    private FontFace? FallbackForCodePoint(int codePoint)
    {
        if (_glyphFallback.TryGetValue(codePoint, out var cached))
        {
            return cached;
        }

        FontFace? result = null;
        var fallback = FaceFor(FallbackTypeface());
        if (fallback.Covers(codePoint))
        {
            result = fallback;
        }

        foreach (var typeface in Settings.GlyphFallbacks)
        {
            if (result is null && FaceFor(typeface) is { } candidate && candidate.Covers(codePoint))
            {
                result = candidate;
            }
        }

        if (result is null && Settings.UseSystemGlyphFallback && SKFontManager.Default.MatchCharacter(codePoint) is { } system)
        {
            result = FaceFor(system);
            if (!result.Covers(codePoint))
            {
                result = null;
            }
        }

        _glyphFallback[codePoint] = result;
        return result;
    }

    // ---- Rasterization ----

    private TextLayerRaster? Rasterize(TextLayerInfo text, LaidOutText layout, PsdRect? clip)
    {
        if (layout.Glyphs.Count == 0 && layout.Bars.Count == 0)
        {
            return null;
        }

        var transform = DocumentTransform(text.Transform);
        var axisAligned = Math.Abs(transform.B) < 1e-9 && Math.Abs(transform.C) < 1e-9;
        TextWarpMesh? warp = null;
        if (Settings.ApplyWarp && text.WarpSettings is { IsIdentity: false } warpSettings)
        {
            // The warp acts over the layout box: the frame for box text, the descriptor bounds otherwise (.reference/docs/warp.md).
            var box = text.ShapeKind == TextShapeKind.Box && text.BoxBounds is { IsEmpty: false } frame ? frame : text.Bounds;
            if (box is { IsEmpty: false } warpBox)
            {
                warp = TextWarpMesh.Create(warpSettings, warpBox);
            }
        }

        var snap = Settings.SnapToPixelGrid && axisAligned && warp is null;
        var paths = new List<(SKPath Path, int Style, bool Stroke)>();
        try
        {
            foreach (var glyph in layout.Glyphs)
            {
                if (glyph.Glyph == 0 || glyph.Face.GlyphPath(glyph.Glyph) is not { } outline)
                {
                    continue;
                }

                var style = layout.Styles[glyph.Style];
                if (style.FillEnabled)
                {
                    paths.Add((GlyphPath(outline, glyph, style, transform, snap, warp), glyph.Style, false));
                }

                if (style.Stroke is not null && style.StrokeWidth > 0)
                {
                    paths.Add((GlyphPath(outline, glyph, style, transform, snap, warp), glyph.Style, true));
                }
            }

            foreach (var bar in layout.Bars)
            {
                using var builder = new SKPathBuilder();
                builder.AddRect(new SKRect((float)bar.Left, (float)bar.Top, (float)bar.Right, (float)bar.Bottom), SKPathDirection.Clockwise);
                using var barPath = builder.Detach();
                paths.Add((MapPath(barPath, transform, warp), bar.Style, false));
            }

            var bounds = SKRect.Empty;
            foreach (var (path, style, stroke) in paths)
            {
                var pathBounds = path.Bounds;
                if (stroke)
                {
                    var half = (float)(StrokeWidthInDocument(layout.Styles[style], transform) / 2);
                    pathBounds.Inflate(half, half);
                }

                bounds = bounds.IsEmpty ? pathBounds : SKRect.Union(bounds, pathBounds);
            }

            if (bounds.IsEmpty || !float.IsFinite(bounds.Left) || !float.IsFinite(bounds.Right) || !float.IsFinite(bounds.Top) || !float.IsFinite(bounds.Bottom))
            {
                return null;
            }

            const double limitCoordinate = int.MaxValue / 4;
            static int Edge(double value) => (int)Math.Clamp(value, -limitCoordinate, limitCoordinate);
            var rect = new PsdRect(
                Edge(Math.Floor(bounds.Left)) - 1,
                Edge(Math.Floor(bounds.Top)) - 1,
                Edge(Math.Ceiling(bounds.Right)) + 1,
                Edge(Math.Ceiling(bounds.Bottom)) + 1);
            if (clip is { } limit)
            {
                rect = rect.Intersect(limit);
            }

            if (rect.IsEmpty || rect.Width > MaxRasterSide || rect.Height > MaxRasterSide || (long)rect.Width * rect.Height > MaxRasterArea)
            {
                return null;
            }

            return Draw(text, layout, paths, transform, rect);
        }
        finally
        {
            foreach (var (path, _, _) in paths)
            {
                path.Dispose();
            }
        }
    }

    private static TextLayerRaster? Draw(TextLayerInfo text, LaidOutText layout, List<(SKPath Path, int Style, bool Stroke)> paths, Affine transform, PsdRect rect)
    {
        var info = new SKImageInfo(rect.Width, rect.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        bitmap.Erase(SKColors.Transparent);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Translate(-rect.Left, -rect.Top);
            using var paint = new SKPaint { IsAntialias = text.AntiAlias != TextAntiAlias.None, BlendMode = SKBlendMode.SrcOver };
            foreach (var (path, styleIndex, stroke) in paths)
            {
                var style = layout.Styles[styleIndex];
                var color = stroke ? style.Stroke!.Value : style.Fill;
                paint.Color = new SKColor(color.R, color.G, color.B, color.A);
                paint.Style = stroke ? SKPaintStyle.Stroke : SKPaintStyle.Fill;
                paint.StrokeWidth = stroke ? (float)StrokeWidthInDocument(style, transform) : 0;
                canvas.DrawPath(path, paint);
            }
        }

        var image = new RgbaImage(rect.Width, rect.Height);
        var source = bitmap.GetPixelSpan();
        var target = image.Pixels;
        var any = false;
        for (var i = 0; i < target.Length; i += 4)
        {
            int a = source[i + 3];
            if (a == 0)
            {
                continue;
            }

            any = true;
            target[i] = (byte)Math.Min(255, ((source[i] * 255) + (a / 2)) / a);
            target[i + 1] = (byte)Math.Min(255, ((source[i + 1] * 255) + (a / 2)) / a);
            target[i + 2] = (byte)Math.Min(255, ((source[i + 2] * 255) + (a / 2)) / a);
            target[i + 3] = (byte)a;
        }

        return any ? new TextLayerRaster(rect.Left, rect.Top, image) : null;
    }

    private static double StrokeWidthInDocument(LayoutStyle style, Affine transform) => style.StrokeWidth * transform.VerticalScale;

    /// <summary>The glyph outline in document space: faux bold widening in em space, the glyph matrix, the warp, then the layer transform.</summary>
    private static SKPath GlyphPath(SKPath outline, PlacedGlyph glyph, LayoutStyle style, Affine transform, bool snap, TextWarpMesh? warp)
    {
        using var widened = style.FauxBold ? Embolden(outline) : null;
        var source = widened ?? outline;
        const double unit = 1.0 / FontFace.OutlineSize;
        var glyphMatrix = new Affine(glyph.A * unit, glyph.B * unit, glyph.C * unit, glyph.D * unit, glyph.E, glyph.F);
        if (warp is null)
        {
            var combined = transform.Multiply(glyphMatrix);
            if (snap)
            {
                // Photoshop rounds each glyph's document origin to a whole pixel, halves up
                // (.reference/docs/text-render-calibration.md, "Pixel grid and fractional anchors").
                var (x, y) = transform.Apply(glyph.E, glyph.F);
                combined = combined with { E = Math.Floor(x + 0.5), F = Math.Floor(y + 0.5) };
            }

            var result = new SKPath();
            var matrix = combined.ToSkia();
            source.Transform(matrix, result);
            return result;
        }

        using var textSpace = new SKPath();
        source.Transform(glyphMatrix.ToSkia(), textSpace);
        return MapPath(textSpace, transform, warp);
    }

    /// <summary>Faux bold: the outline stroked with a pen 0.03 em wide (reference <c>apply_faux_bold_to_document</c>).</summary>
    private static SKPath Embolden(SKPath outline)
    {
        using var paint = new SKPaint { Style = SKPaintStyle.StrokeAndFill, StrokeWidth = (float)(TextLayoutEngine.FauxBoldEm * FontFace.OutlineSize), StrokeJoin = SKStrokeJoin.Round, IsAntialias = true };
        return paint.GetFillPath(outline) ?? new SKPath(outline);
    }

    /// <summary>Maps a text-space path to document space through the optional warp (curves are flattened first when warping).</summary>
    private static SKPath MapPath(SKPath path, Affine transform, TextWarpMesh? warp)
    {
        if (warp is null)
        {
            var mapped = new SKPath();
            path.Transform(transform.ToSkia(), mapped);
            return mapped;
        }

        using var result = new SKPathBuilder { FillType = path.FillType };
        var bounds = path.Bounds;
        var tolerance = Math.Max(0.05, Math.Max(bounds.Width, bounds.Height) / 256);
        using var iterator = path.CreateRawIterator();
        var points = new SKPoint[4];
        SKPathVerb verb;
        SKPoint last = default;
        while ((verb = iterator.Next(points)) != SKPathVerb.Done)
        {
            switch (verb)
            {
                case SKPathVerb.Move:
                    last = points[0];
                    result.MoveTo(Map(points[0]));
                    break;
                case SKPathVerb.Line:
                    Segment(result, last, points[1]);
                    last = points[1];
                    break;
                case SKPathVerb.Quad:
                    Curve(result, points[0], points[1], points[1], points[2], quadratic: true);
                    last = points[2];
                    break;
                case SKPathVerb.Conic:
                    foreach (var quad in ConicToQuads(points[0], points[1], points[2], iterator.ConicWeight()))
                    {
                        Curve(result, quad.P0, quad.P1, quad.P1, quad.P2, quadratic: true);
                    }

                    last = points[2];
                    break;
                case SKPathVerb.Cubic:
                    Curve(result, points[0], points[1], points[2], points[3], quadratic: false);
                    last = points[3];
                    break;
                case SKPathVerb.Close:
                    result.Close();
                    break;
            }
        }

        return result.Detach();

        SKPoint Map(SKPoint point)
        {
            var (wx, wy) = warp.Map(point.X, point.Y);
            var (x, y) = transform.Apply(wx, wy);
            return new SKPoint((float)x, (float)y);
        }

        void Segment(SKPathBuilder target, SKPoint from, SKPoint to)
        {
            // Straight text-space edges bend under the warp: subdivide them too.
            var length = Math.Sqrt(((to.X - from.X) * (to.X - from.X)) + ((to.Y - from.Y) * (to.Y - from.Y)));
            var steps = Math.Clamp((int)Math.Ceiling(length / (tolerance * 8)), 1, 64);
            for (var s = 1; s <= steps; s++)
            {
                var t = (float)s / steps;
                target.LineTo(Map(new SKPoint(from.X + ((to.X - from.X) * t), from.Y + ((to.Y - from.Y) * t))));
            }
        }

        void Curve(SKPathBuilder target, SKPoint p0, SKPoint p1, SKPoint p2, SKPoint p3, bool quadratic)
        {
            var hull = Distance(p0, p1) + Distance(p1, p2) + Distance(p2, p3);
            var steps = Math.Clamp((int)Math.Ceiling(Math.Sqrt(hull / tolerance) * 2), 2, 64);
            for (var s = 1; s <= steps; s++)
            {
                var t = (double)s / steps;
                var u = 1 - t;
                double x, y;
                if (quadratic)
                {
                    x = (u * u * p0.X) + (2 * u * t * p1.X) + (t * t * p3.X);
                    y = (u * u * p0.Y) + (2 * u * t * p1.Y) + (t * t * p3.Y);
                }
                else
                {
                    x = (u * u * u * p0.X) + (3 * u * u * t * p1.X) + (3 * u * t * t * p2.X) + (t * t * t * p3.X);
                    y = (u * u * u * p0.Y) + (3 * u * u * t * p1.Y) + (3 * u * t * t * p2.Y) + (t * t * t * p3.Y);
                }

                target.LineTo(Map(new SKPoint((float)x, (float)y)));
            }
        }

        static double Distance(SKPoint a, SKPoint b) => Math.Sqrt(((b.X - a.X) * (b.X - a.X)) + ((b.Y - a.Y) * (b.Y - a.Y)));
    }

    private static IEnumerable<(SKPoint P0, SKPoint P1, SKPoint P2)> ConicToQuads(SKPoint p0, SKPoint p1, SKPoint p2, float weight)
    {
        var quads = SKPath.ConvertConicToQuads(p0, p1, p2, weight, 2);
        for (var i = 0; i + 2 < quads.Length; i += 2)
        {
            yield return (quads[i], quads[i + 1], quads[i + 2]);
        }
    }

    private static Affine DocumentTransform(IReadOnlyList<double> values)
    {
        if (values.Count < 6)
        {
            return Affine.Identity;
        }

        var affine = new Affine(values[0], values[1], values[2], values[3], values[4], values[5]);
        return affine.IsFinite && Math.Abs((affine.A * affine.D) - (affine.B * affine.C)) > 1e-12 ? affine : Affine.Identity;
    }

    /// <summary>A 2D affine map: <c>x' = A x + C y + E</c>, <c>y' = B x + D y + F</c> (the TySh order <c>xx xy yx yy tx ty</c>).</summary>
    private readonly record struct Affine(double A, double B, double C, double D, double E, double F)
    {
        public static Affine Identity => new(1, 0, 0, 1, 0, 0);

        public bool IsFinite => double.IsFinite(A) && double.IsFinite(B) && double.IsFinite(C) && double.IsFinite(D) && double.IsFinite(E) && double.IsFinite(F);

        /// <summary>Length of the transformed y axis: the vertical scale that multiplies sizes.</summary>
        public double VerticalScale => Math.Sqrt((C * C) + (D * D));

        public (double X, double Y) Apply(double x, double y) => ((A * x) + (C * y) + E, (B * x) + (D * y) + F);

        /// <summary>This map after <paramref name="inner"/>.</summary>
        public Affine Multiply(Affine inner) => new(
            (A * inner.A) + (C * inner.B),
            (B * inner.A) + (D * inner.B),
            (A * inner.C) + (C * inner.D),
            (B * inner.C) + (D * inner.D),
            (A * inner.E) + (C * inner.F) + E,
            (B * inner.E) + (D * inner.F) + F);

        public SKMatrix ToSkia() => new((float)A, (float)C, (float)E, (float)B, (float)D, (float)F, 0, 0, 1);
    }
}

/// <summary>
/// The <see cref="ITextLayerRasterizer"/> for <see cref="RenderOptions.TextRasterizer"/>: renders type
/// layers with a <see cref="TextLayerRenderer"/>. Layout or font failures keep the stored pixels.
/// </summary>
public sealed class TextLayerRasterizer : ITextLayerRasterizer, IDisposable
{
    private readonly TextLayerRenderer _renderer;
    private readonly bool _ownsRenderer;

    public TextLayerRasterizer(TextRenderSettings? settings = null)
        : this(new TextLayerRenderer(settings), ownsRenderer: true)
    {
    }

    public TextLayerRasterizer(TextLayerRenderer renderer)
        : this(renderer, ownsRenderer: false)
    {
    }

    private TextLayerRasterizer(TextLayerRenderer renderer, bool ownsRenderer)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        _renderer = renderer;
        _ownsRenderer = ownsRenderer;
    }

    public TextLayerRenderer Renderer => _renderer;

    public TextLayerRaster? Rasterize(PsdLayer layer)
    {
        try
        {
            return _renderer.Render(layer);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IndexOutOfRangeException or OverflowException or ArithmeticException)
        {
            // Text from a damaged file degrades to the stored pixels, like other malformed blocks.
            return null;
        }
    }

    public void Dispose()
    {
        if (_ownsRenderer)
        {
            _renderer.Dispose();
        }
    }
}
