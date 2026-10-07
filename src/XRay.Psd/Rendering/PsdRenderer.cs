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
}

internal static class PsdRenderer
{
    public static RgbaImage Render(PsdDocument document, RenderOptions options)
    {
        var useMerged = options.Source switch
        {
            RenderSource.MergedImage => true,
            RenderSource.Layers => false,
            _ => options.LayerVisibility is null && (document.HasRealMergedImage || document.Layers.Count == 0) && document.MergedImageData.Length >= 2,
        };

        var planar = useMerged ? MergedImageDecoder.Decode(document) : new LayerCompositor(document, options).Render();
        var image = planar.ToRgba(document.Bounds);
        return options.Background is { } background ? image.Flatten(background) : image;
    }

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
