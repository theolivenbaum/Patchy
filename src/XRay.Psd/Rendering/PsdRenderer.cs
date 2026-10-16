using XRay.Psd.Imaging;
using XRay.Psd.Layers;

namespace XRay.Psd.Rendering;

/// <summary>Where <see cref="PsdDocument.Render"/> takes its pixels from.</summary>
public enum RenderSource
{
    /// <summary>
    /// The merged image Photoshop saved (exact, includes effects, adjustments and
    /// text) when it is real; otherwise the layer compositor.
    /// </summary>
    Auto,

    /// <summary>Always decode the saved merged image.</summary>
    MergedImage,

    /// <summary>Always composite the layer tree.</summary>
    Layers,
}

/// <summary>Options for rendering a document.</summary>
public sealed class RenderOptions
{
    public RenderSource Source { get; init; } = RenderSource.Auto;

    /// <summary>When set, the result is flattened over this color (and fully opaque).</summary>
    public PsdColor? Background { get; init; }

    /// <summary>
    /// Overrides layer visibility for the layer compositor (return true to draw).
    /// Setting it forces <see cref="RenderSource.Layers"/> under <see cref="RenderSource.Auto"/>.
    /// </summary>
    public Func<PsdLayer, bool>? LayerVisibility { get; init; }

    /// <summary>
    /// Re-renders type layers from their text model (see <see cref="TextRasterMode"/>), for
    /// example with <c>XRay.Psd.Text</c>. Null (the default) always uses the stored pixels.
    /// </summary>
    public ITextLayerRasterizer? TextRasterizer { get; init; }

    /// <summary>Which type layers <see cref="TextRasterizer"/> draws. <see cref="TextRasterMode.Always"/> forces <see cref="RenderSource.Layers"/> under <see cref="RenderSource.Auto"/>.</summary>
    public TextRasterMode TextRasterMode { get; init; } = TextRasterMode.MissingPixels;

    internal bool RerendersAllText => TextRasterizer is not null && TextRasterMode == TextRasterMode.Always;

    /// <summary>
    /// Renders Color Lookup adjustment layers from their embedded LUT or profile. Off by
    /// default: no Photoshop capture has measured the result yet (docs/adjustments.md).
    /// </summary>
    internal bool RenderColorLookup { get; init; }

    /// <summary>
    /// The most threads a render may use for channel decoding, compositing rows,
    /// mask blurs and color conversion. Defaults to the processor count; 1 renders
    /// on the calling thread only, zero or negative means the processor count.
    /// The result is bit-identical for every value.
    /// </summary>
    public int MaxDegreeOfParallelism { get; init; } = Environment.ProcessorCount;

    /// <summary>
    /// Rows per band for <see cref="PsdDocument.RenderBands"/> and <see cref="PsdDocument.RenderToPng(Stream, RenderOptions?, Codecs.PngCompressionLevel)"/>.
    /// Zero or negative (the default) picks about four million pixels per band (64 MB per
    /// float buffer). The pixels do not depend on it.
    /// </summary>
    public int BandHeight { get; init; }
}

/// <summary>One horizontal strip of a rendered document: <see cref="Image"/> holds document rows from <see cref="Top"/> on, at full width.</summary>
public sealed class RenderBand
{
    internal RenderBand(int top, RgbaImage image)
    {
        Top = top;
        Image = image;
    }

    /// <summary>The document row of the band's first row.</summary>
    public int Top { get; }

    /// <summary>The band's pixels, as wide as the document.</summary>
    public RgbaImage Image { get; }
}

internal static class PsdRenderer
{
    public static RgbaImage Render(PsdDocument document, RenderOptions options)
    {
        // The result is one RGBA array; past it (about 23000 x 23000 pixels) only bands fit.
        _ = IO.ChannelCodec.BufferLength((long)document.Width * document.Height * 4);
        using var scope = Parallelism.Use(options.MaxDegreeOfParallelism);
        var planar = UsesMergedImage(document, options) ? MergedImageDecoder.Decode(document) : new LayerCompositor(document, options).Render();
        var image = planar.ToRgba(document.Bounds);
        return options.Background is { } background ? image.Flatten(background) : image;
    }

    /// <summary>Which source <see cref="Render"/> takes for these options.</summary>
    internal static bool UsesMergedImage(PsdDocument document, RenderOptions options) => options.Source switch
    {
        RenderSource.MergedImage => true,
        RenderSource.Layers => false,
        _ => options.LayerVisibility is null && !options.RerendersAllText && (document.HasRealMergedImage || document.Layers.Count == 0) && document.MergedImage.Length >= 2,
    };

    /// <summary>Rows per band: the option, or about four million pixels.</summary>
    internal static int BandHeight(PsdDocument document, RenderOptions options) =>
        options.BandHeight > 0 ? options.BandHeight : (int)Math.Clamp((1L << 22) / Math.Max(1, document.Width), 1, Math.Max(1, document.Height));

    /// <summary>
    /// Renders the document band by band, top to bottom, as <see cref="Render"/> would but
    /// holding only one band of the canvas at a time (see <see cref="LayerCompositor.BeginBands"/>
    /// for what still keeps its whole extent). Each band's pixels are identical to the same
    /// rows of <see cref="Render"/>. Bands render lazily as the sequence is enumerated.
    /// </summary>
    public static IEnumerable<RenderBand> RenderBands(PsdDocument document, RenderOptions options)
    {
        var height = BandHeight(document, options);
        var degree = options.MaxDegreeOfParallelism;
        if (UsesMergedImage(document, options))
        {
            using var bands = new MergedImageBands();
            for (var top = 0; top < document.Height; top += height)
            {
                var bottom = Math.Min(document.Height, top + height);
                RgbaImage image;
                using (Parallelism.Use(degree))
                {
                    var planar = MergedImageDecoder.Decode(document, top, bottom, bands);
                    image = Finish(planar.ToRgba(planar.Bounds), options);
                }

                yield return new RenderBand(top, image);
            }

            yield break;
        }

        using var compositor = new LayerCompositor(document, options);
        using (Parallelism.Use(degree))
        {
            compositor.BeginBands();
        }

        for (var top = 0; top < document.Height; top += height)
        {
            var rect = new PsdRect(0, top, document.Width, Math.Min(document.Height, top + height));
            RgbaImage image;
            using (Parallelism.Use(degree))
            {
                image = Finish(compositor.RenderBand(rect).ToRgba(rect), options);
            }

            yield return new RenderBand(top, image);
        }
    }

    /// <summary>
    /// Streams the rendered document into a PNG band by band (<see cref="RenderBands"/>).
    /// The image keeps its alpha channel unless the result is opaque by construction: a
    /// <see cref="RenderOptions.Background"/>, or a merged image without transparency.
    /// </summary>
    public static void RenderToPng(PsdDocument document, Stream output, RenderOptions options, Codecs.PngCompressionLevel level)
    {
        var opaque = options.Background is not null || (UsesMergedImage(document, options) && !document.MergedImageHasTransparency);
        var writer = new Codecs.PngEncoder.RowWriter(output, document.Width, document.Height, withAlpha: !opaque, level);
        foreach (var band in RenderBands(document, options))
        {
            using (Parallelism.Use(options.MaxDegreeOfParallelism))
            {
                writer.Write(band.Image);
            }
        }

        using (Parallelism.Use(options.MaxDegreeOfParallelism))
        {
            writer.Finish();
        }
    }

    private static RgbaImage Finish(RgbaImage image, RenderOptions options) => options.Background is { } background ? image.Flatten(background) : image;

    /// <summary>Renders a single layer (or group, isolated) to document-size RGBA.</summary>
    public static RgbaImage RenderLayer(PsdLayer layer)
    {
        var document = layer.Document;
        var options = new RenderOptions
        {
            Source = RenderSource.Layers,
            LayerVisibility = candidate => candidate == layer || IsDescendant(candidate, layer) || IsAncestor(candidate, layer),
        };
        return new LayerCompositor(document, options).Render().ToRgba(document.Bounds);
    }

    private static bool IsDescendant(PsdLayer candidate, PsdLayer root)
    {
        for (var parent = candidate.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent == root)
            {
                return candidate.IsVisible;
            }
        }

        return false;
    }

    private static bool IsAncestor(PsdLayer candidate, PsdLayer layer)
    {
        for (var parent = layer.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent == candidate)
            {
                return true;
            }
        }

        return false;
    }
}
