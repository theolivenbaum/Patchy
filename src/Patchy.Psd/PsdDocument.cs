using Patchy.Psd.Imaging;
using Patchy.Psd.Layers;
using Patchy.Psd.Rendering;
using Patchy.Psd.Text;

namespace Patchy.Psd;

/// <summary>Options for <see cref="PsdDocument.Load(string, PsdLoadOptions?)"/>.</summary>
public sealed class PsdLoadOptions
{
    /// <summary>Parse layer records. Turn off to read only the header, resources and merged image.</summary>
    public bool ReadLayers { get; init; } = true;

    /// <summary>Parse embedded smart-object files from the global link blocks.</summary>
    public bool ReadLinkedFiles { get; init; } = true;
}

/// <summary>
/// A Photoshop document (PSD or PSB) parsed into memory. Pixel data stays
/// compressed until rendering or <see cref="PsdLayer.GetPixels"/> decodes it,
/// so text extraction and inspection stay cheap.
/// </summary>
public sealed class PsdDocument
{
    private readonly List<PsdLayer> _layers = [];
    private readonly List<PsdLayer> _rootLayers = [];

    internal PsdDocument()
    {
    }

    /// <summary>File format version: 1 for PSD, 2 for PSB (large document).</summary>
    public int Version { get; internal set; }

    public bool IsLargeDocument => Version == 2;

    public int Width { get; internal set; }

    public int Height { get; internal set; }

    /// <summary>Total stored channels in the merged image, including alpha channels.</summary>
    public int ChannelCount { get; internal set; }

    /// <summary>Bits per channel: 1, 8, 16 or 32.</summary>
    public int Depth { get; internal set; }

    public PsdColorMode ColorMode { get; internal set; }

    public PsdRect Bounds => new(0, 0, Width, Height);

    /// <summary>Raw color mode data (the indexed palette, or duotone specification).</summary>
    public ReadOnlyMemory<byte> ColorModeData { get; internal set; }

    /// <summary>The 256-entry palette for indexed documents.</summary>
    public PsdColor[]? Palette { get; internal set; }

    public IReadOnlyList<ImageResource> ImageResources { get; internal set; } = [];

    /// <summary>Every layer record in file order (bottom to top), including group records but not the hidden group-end dividers.</summary>
    public IReadOnlyList<PsdLayer> Layers => _layers;

    /// <summary>Top-level layers and groups, bottom to top.</summary>
    public IReadOnlyList<PsdLayer> RootLayers => _rootLayers;

    /// <summary>Document-level tagged blocks that follow the layer info (<c>Txt2</c>, <c>Patt</c>, <c>lnk2</c>...).</summary>
    public IReadOnlyList<TaggedBlock> GlobalTaggedBlocks { get; internal set; } = [];

    /// <summary>Embedded and linked smart-object source files.</summary>
    public IReadOnlyList<LinkedFile> LinkedFiles { get; internal set; } = [];

    /// <summary>Whether the first extra merged channel is document transparency (negative layer count flag).</summary>
    public bool MergedImageHasTransparency { get; internal set; }

    /// <summary>
    /// False when Photoshop saved without "Maximize Compatibility": the merged
    /// image section then holds a placeholder rather than the real composite
    /// (version-info resource 1057).
    /// </summary>
    public bool HasRealMergedImage { get; internal set; } = true;

    internal ReadOnlyMemory<byte> MergedImageData { get; set; }

    internal ReadOnlyMemory<byte> FileData { get; set; }

    /// <summary>Global light angle in degrees (resource 1037), used by effects with "Use Global Light".</summary>
    public float GlobalLightAngle { get; internal set; } = 120f;

    /// <summary>Global light altitude in degrees (resource 1049).</summary>
    public float GlobalLightAltitude { get; internal set; } = 30f;

    /// <summary>Raw global layer mask info.</summary>
    public ReadOnlyMemory<byte> GlobalLayerMaskInfo { get; internal set; }

    internal void AddLayer(PsdLayer layer) => _layers.Add(layer);

    internal void AddRootLayer(PsdLayer layer) => _rootLayers.Add(layer);

    public static PsdDocument Load(string path, PsdLoadOptions? options = null) => Load(File.ReadAllBytes(path), options);

    public static PsdDocument Load(Stream stream, PsdLoadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return Load(buffer.ToArray(), options);
    }

    public static PsdDocument Load(byte[] data, PsdLoadOptions? options = null) => Load(new ReadOnlyMemory<byte>(data), options);

    public static PsdDocument Load(ReadOnlyMemory<byte> data, PsdLoadOptions? options = null) => PsdParser.Parse(data, options ?? new PsdLoadOptions());

    /// <summary>True when the bytes start with the <c>8BPS</c> signature.</summary>
    public static bool IsPsd(ReadOnlySpan<byte> data) => data.Length >= 4 && data[0] == (byte)'8' && data[1] == (byte)'B' && data[2] == (byte)'P' && data[3] == (byte)'S';

    public ImageResource? GetImageResource(ushort id)
    {
        foreach (var resource in ImageResources)
        {
            if (resource.Id == id)
            {
                return resource;
            }
        }

        return null;
    }

    public TaggedBlock? GetGlobalTaggedBlock(string key)
    {
        foreach (var block in GlobalTaggedBlocks)
        {
            if (block.Key == key)
            {
                return block;
            }
        }

        return null;
    }

    /// <summary>Iterates the layer tree top to bottom (the order the Layers panel shows), depth first.</summary>
    public IEnumerable<PsdLayer> EnumerateLayersTopDown()
    {
        static IEnumerable<PsdLayer> Walk(IReadOnlyList<PsdLayer> layers)
        {
            for (var i = layers.Count - 1; i >= 0; i--)
            {
                yield return layers[i];
                foreach (var child in Walk(layers[i].Children))
                {
                    yield return child;
                }
            }
        }

        return Walk(_rootLayers);
    }

    /// <summary>Decodes the merged (flattened) image Photoshop stored in the file.</summary>
    public RgbaImage GetMergedImage() => MergedImageDecoder.Decode(this).ToRgba(Bounds);

    /// <summary>Renders the document to 8-bit RGBA. See <see cref="RenderOptions"/> for the source choice.</summary>
    public RgbaImage Render(RenderOptions? options = null) => PsdRenderer.Render(this, options ?? new RenderOptions());

    /// <summary>Extracts every piece of text: layer and group names, type-layer content, channel, path and slice names, and text inside embedded smart objects.</summary>
    public PsdTextContent ExtractText(TextExtractionOptions? options = null) => TextExtractor.Extract(this, options ?? new TextExtractionOptions());

    public override string ToString() => $"{(IsLargeDocument ? "PSB" : "PSD")} {Width}x{Height} {ColorMode} {Depth}-bit, {_layers.Count} layers";
}
