using XRay.Psd.Imaging;
using XRay.Psd.IO;
using XRay.Psd.Layers;
using XRay.Psd.Rendering;
using XRay.Psd.Text;

namespace XRay.Psd;

/// <summary>Options for <see cref="PsdDocument.Load(string, PsdLoadOptions?)"/>.</summary>
public sealed class PsdLoadOptions
{
    /// <summary>Parse layer records. Turn off to read only the header, resources and merged image.</summary>
    public bool ReadLayers { get; init; } = true;

    /// <summary>Parse embedded smart-object files from the global link blocks.</summary>
    public bool ReadLinkedFiles { get; init; } = true;

    /// <summary>
    /// Convert gray, RGB, indexed and CMYK pixels to sRGB through the document's
    /// embedded ICC profile (resource 1039), as Photoshop displays them. Turn off
    /// to treat RGB and gray as sRGB and use the naive CMYK formula. Documents
    /// without a usable profile convert the same way either way.
    /// </summary>
    public bool ColorManagement { get; init; } = true;

    /// <summary>
    /// Map the file into memory instead of reading it into a managed array (the path
    /// overloads only). Pages load when parsing or rendering touches them, so opening a
    /// large document reads little more than its layer records. The document then holds
    /// the file open until it is disposed. Files too large for one array (2 GB and up)
    /// are always mapped.
    /// </summary>
    public bool MemoryMap { get; init; }
}

/// <summary>
/// A Photoshop document (PSD or PSB) parsed into memory. Pixel data stays
/// compressed until rendering or <see cref="PsdLayer.GetPixels"/> decodes it,
/// so text extraction and inspection stay cheap.
/// </summary>
/// <remarks>
/// Disposing matters only for memory-mapped documents (<see cref="PsdLoadOptions.MemoryMap"/>,
/// or files of 2 GB and more): it closes the mapping, after which reading pixel data
/// throws <see cref="ObjectDisposedException"/>. For documents loaded into memory it does nothing.
/// </remarks>
public sealed class PsdDocument : IDisposable
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

    private Resources.PsdImageResources? _resources;

    /// <summary>Typed, lazily parsed views of the common image resources (resolution, guides, thumbnail, slices, layer comps, metadata).</summary>
    public Resources.PsdImageResources Resources => LazyInitializer.EnsureInitialized(ref _resources, () => new Resources.PsdImageResources(this));

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

    /// <summary>The memory-mapped file behind the document, released by <see cref="Dispose"/>.</summary>
    internal IO.PsdSource? Source { get; set; }

    /// <summary>Global light angle in degrees (resource 1037), used by effects with "Use Global Light".</summary>
    public float GlobalLightAngle { get; internal set; } = 120f;

    /// <summary>Global light altitude in degrees (resource 1049).</summary>
    public float GlobalLightAltitude { get; internal set; } = 30f;

    /// <summary>Raw global layer mask info.</summary>
    public ReadOnlyMemory<byte> GlobalLayerMaskInfo { get; internal set; }

    private Dictionary<string, Rendering.PatternTile>? _patterns;

    /// <summary>Decoded pattern tiles from the global pattern blocks, keyed by pattern ID (decoded on first use).</summary>
    internal Dictionary<string, Rendering.PatternTile> Patterns => _patterns ??= Rendering.PatternStore.Parse(this);

    private TextEngineBlock? _textEngine;
    private bool _textEngineParsed;

    /// <summary>
    /// The document-level text engine block (<c>Txt2</c>) with one text object per
    /// type layer, parsed on first use; null when the document has none or it does not decode.
    /// </summary>
    public TextEngineBlock? TextEngine
    {
        get
        {
            if (!_textEngineParsed)
            {
                _textEngine = TextEngineResolver.ParseBlock(this);
                _textEngineParsed = true;
            }

            return _textEngine;
        }
    }

    private System.Runtime.CompilerServices.StrongBox<Imaging.Icc.IccSrgbTransform?>? _colorTransform;

    /// <summary>Whether pixel decoding uses the embedded ICC profile (<see cref="PsdLoadOptions.ColorManagement"/>).</summary>
    internal bool ColorManagementEnabled { get; set; } = true;

    /// <summary>The cached embedded-profile-to-sRGB transform, or null when none applies (built on first use).</summary>
    internal Imaging.Icc.IccSrgbTransform? ColorTransform =>
        LazyInitializer.EnsureInitialized(ref _colorTransform, () => new(ColorManagementEnabled ? Imaging.Icc.IccSrgbTransform.ForDocument(this) : null)).Value;

    private DocumentColors? _colors;

    /// <summary>Converts descriptor, text and pattern colors the same way as the pixels (see <see cref="DocumentColors"/>).</summary>
    internal DocumentColors Colors => LazyInitializer.EnsureInitialized(ref _colors, () => new DocumentColors(this));

    internal void AddLayer(PsdLayer layer) => _layers.Add(layer);

    internal void AddRootLayer(PsdLayer layer) => _rootLayers.Add(layer);

    /// <summary>Loads a file. See <see cref="PsdLoadOptions.MemoryMap"/> for mapping instead of reading it.</summary>
    public static PsdDocument Load(string path, PsdLoadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        options ??= new PsdLoadOptions();
        if (options.MemoryMap || new FileInfo(path).Length > Array.MaxLength)
        {
            return LoadMapped(path, options);
        }

        return Load(File.ReadAllBytes(path), options);
    }

    /// <summary>
    /// Loads from a stream, reading it from its current position to the end. A seekable
    /// stream is read straight into one array of the remaining length; other streams are
    /// buffered once. The stream is not disposed.
    /// </summary>
    public static PsdDocument Load(Stream stream, PsdLoadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return Load(StreamLoader.ReadAll(stream), options);
    }

    public static PsdDocument Load(byte[] data, PsdLoadOptions? options = null) => Load(new ReadOnlyMemory<byte>(data), options);

    public static PsdDocument Load(ReadOnlyMemory<byte> data, PsdLoadOptions? options = null)
    {
        options ??= new PsdLoadOptions();
        return PsdParser.Parse(data, options);
    }

    /// <summary>
    /// Reads a file asynchronously, then parses it. Parsing reads only the structure
    /// (pixel data stays compressed), so it runs on the calling thread after the read.
    /// With <see cref="PsdLoadOptions.MemoryMap"/> (or a file of 2 GB and more) the file is
    /// mapped instead, which involves no bulk read.
    /// </summary>
    public static async Task<PsdDocument> LoadAsync(string path, PsdLoadOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        options ??= new PsdLoadOptions();
        if (options.MemoryMap || new FileInfo(path).Length > Array.MaxLength)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return LoadMapped(path, options);
        }

        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using (stream.ConfigureAwait(false))
        {
            var data = await StreamLoader.ReadAllAsync(stream, cancellationToken).ConfigureAwait(false);
            return Load(data, options);
        }
    }

    /// <summary>Reads a stream asynchronously from its current position to the end, then parses it. The stream is not disposed.</summary>
    public static async Task<PsdDocument> LoadAsync(Stream stream, PsdLoadOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var data = await StreamLoader.ReadAllAsync(stream, cancellationToken).ConfigureAwait(false);
        return Load(data, options);
    }

    private static PsdDocument LoadMapped(string path, PsdLoadOptions options)
    {
        var source = IO.MappedFileSource.Open(path);
        try
        {
            var document = PsdParser.Parse(source, options);
            document.ColorManagementEnabled = options.ColorManagement;
            document.Source = source;
            return document;
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    /// <summary>Releases the memory mapping of a mapped document; does nothing for documents loaded into memory.</summary>
    public void Dispose() => Source?.Dispose();

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
