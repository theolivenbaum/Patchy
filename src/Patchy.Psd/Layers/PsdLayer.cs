using Patchy.Psd.Descriptors;
using Patchy.Psd.Imaging;
using Patchy.Psd.IO;
using Patchy.Psd.Text;

namespace Patchy.Psd.Layers;

/// <summary>A tagged ("additional layer information") block: a four-character key and its raw payload.</summary>
public sealed record TaggedBlock(string Key, ReadOnlyMemory<byte> Data);

/// <summary>One stored channel of a layer: ID (0.. color, -1 alpha, -2/-3 masks) and its compressed payload.</summary>
public sealed record LayerChannel(short Id, ReadOnlyMemory<byte> Data);

/// <summary>
/// A layer record from the layer and mask section, linked into the group tree.
/// Pixel data stays compressed until <see cref="GetPixels"/> or rendering asks for it.
/// </summary>
public sealed class PsdLayer
{
    private readonly List<PsdLayer> _children = [];

    internal PsdLayer(PsdDocument document)
    {
        Document = document;
    }

    public PsdDocument Document { get; }

    /// <summary>Layer name (the Unicode <c>luni</c> name when present).</summary>
    public string Name { get; internal set; } = string.Empty;

    /// <summary>Layer ID from <c>lyid</c>, or 0.</summary>
    public uint Id { get; internal set; }

    /// <summary>Position in the file's layer list (0 is the bottom record).</summary>
    public int Index { get; internal set; }

    public PsdLayerKind Kind { get; internal set; } = PsdLayerKind.Pixel;

    public PsdRect Bounds { get; internal set; }

    public PsdBlendMode BlendMode { get; internal set; } = PsdBlendMode.Normal;

    /// <summary>Layer opacity 0-255.</summary>
    public byte Opacity { get; internal set; } = 255;

    /// <summary>Fill opacity 0-255 (<c>iOpa</c>): fades the layer content but not its effects.</summary>
    public byte FillOpacity { get; internal set; } = 255;

    /// <summary>Clipped to the layer below (clipping mask member).</summary>
    public bool IsClipped { get; internal set; }

    public bool IsVisible { get; internal set; } = true;

    public bool TransparencyProtected { get; internal set; }

    public PsdSectionType SectionType { get; internal set; }

    public bool IsGroup => Kind == PsdLayerKind.Group;

    public PsdLayer? Parent { get; internal set; }

    /// <summary>Children of a group, bottom to top (file order).</summary>
    public IReadOnlyList<PsdLayer> Children => _children;

    public IReadOnlyList<LayerChannel> Channels { get; internal set; } = [];

    public IReadOnlyList<TaggedBlock> TaggedBlocks { get; internal set; } = [];

    /// <summary>The raster layer mask the user painted, if any.</summary>
    public LayerMask? Mask { get; internal set; }

    /// <summary>
    /// Photoshop's baked vector-mask coverage (mask flag bit 3). Rendering uses
    /// it in place of rasterizing <see cref="VectorMask"/> when present.
    /// </summary>
    public LayerMask? RenderedVectorMask { get; internal set; }

    public VectorPath? VectorMask { get; internal set; }

    /// <summary>Vector mask density 0-255 from the mask parameters.</summary>
    public byte VectorMaskDensity { get; internal set; } = 255;

    /// <summary>Vector mask feather (gaussian sigma in pixels) from the mask parameters.</summary>
    public double VectorMaskFeather { get; internal set; }

    /// <summary>The shape's vector stroke and fill switch (<c>vstk</c>), when present.</summary>
    public VectorStrokeStyle? VectorStroke { get; internal set; }

    /// <summary>Raw "blend if" ranges from the layer record.</summary>
    public ReadOnlyMemory<byte> BlendingRanges { get; internal set; }

    /// <summary>Type-tool data for text layers.</summary>
    public TextLayerInfo? Text { get; internal set; }

    /// <summary>Solid fill color for <c>SoCo</c> fill/shape layers.</summary>
    public PsdColor? FillColor { get; internal set; }

    /// <summary>The fill descriptor for fill/shape layers (<c>SoCo</c>, <c>GdFl</c>, <c>PtFl</c>).</summary>
    public Descriptor? FillDescriptor { get; internal set; }

    /// <summary>Tagged-block key of the fill or adjustment (<c>SoCo</c>, <c>curv</c>, <c>hue2</c>...).</summary>
    public string? ContentKey { get; internal set; }

    /// <summary>Layer-effects descriptor (<c>lfx2</c>/<c>lmfx</c>), when present.</summary>
    public Descriptor? Effects { get; internal set; }

    /// <summary>Whether the layer-effects descriptor marks its effects as visible.</summary>
    public bool EffectsVisible { get; internal set; }

    /// <summary>Smart-object placement descriptor (<c>SoLd</c>/<c>SoLE</c>/<c>PlLd</c>), when present.</summary>
    public Descriptor? SmartObject { get; internal set; }

    /// <summary>Unique ID of the linked/embedded file this smart object shows.</summary>
    public string? SmartObjectFileId { get; internal set; }

    /// <summary>"Blend Interior Effects as Group" (<c>infx</c>, default false).</summary>
    public bool BlendInteriorElements { get; internal set; }

    /// <summary>Whether clipped layers blend with this base as a group (<c>clbl</c>, default true).</summary>
    public bool BlendClippedElements { get; internal set; } = true;

    /// <summary>Tagged block lookup by key (first match).</summary>
    public TaggedBlock? GetTaggedBlock(string key)
    {
        foreach (var block in TaggedBlocks)
        {
            if (block.Key == key)
            {
                return block;
            }
        }

        return null;
    }

    public LayerChannel? GetChannel(short id)
    {
        foreach (var channel in Channels)
        {
            if (channel.Id == id)
            {
                return channel;
            }
        }

        return null;
    }

    internal void AddChild(PsdLayer child)
    {
        child.Parent = this;
        _children.Add(child);
    }

    /// <summary>Visible here and in every ancestor group.</summary>
    public bool IsEffectivelyVisible
    {
        get
        {
            for (var layer = this; layer is not null; layer = layer.Parent)
            {
                if (!layer.IsVisible)
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>Slash-separated path of group names down to this layer.</summary>
    public string Path => Parent is null ? Name : $"{Parent.Path}/{Name}";

    /// <summary>Decodes one channel to a float plane of <paramref name="rect"/> size (masks use their own rectangle).</summary>
    internal float[]? DecodeChannelPlane(short id, PsdRect rect)
    {
        var channel = GetChannel(id);
        if (channel is null || rect.IsEmpty)
        {
            return null;
        }

        var doc = Document;
        var bytes = ChannelCodec.DecodeLayerChannel(channel.Data.Span, rect.Width, rect.Height, doc.Depth, doc.IsLargeDocument);
        var plane = new float[rect.Width * rect.Height];
        ChannelCodec.ToFloat(bytes, rect.Width, rect.Height, doc.Depth, plane);
        return plane;
    }

    /// <summary>
    /// Decodes the layer's stored pixels (layer bounds) to straight sRGB floats.
    /// Masks, opacity and blend mode are not applied.
    /// </summary>
    internal PlanarImage? DecodePixels()
    {
        var bounds = Bounds;
        if (bounds.IsEmpty)
        {
            return null;
        }

        var doc = Document;
        var colorCount = ColorSpaces.ColorChannelCount(doc.ColorMode);
        var planes = new float[]?[colorCount];
        var any = false;
        for (short i = 0; i < colorCount; i++)
        {
            planes[i] = DecodeChannelPlane(i, bounds);
            any |= planes[i] is not null;
        }

        var alpha = DecodeChannelPlane(-1, bounds);
        if (!any && alpha is null)
        {
            return null;
        }

        var image = new PlanarImage(bounds);
        ColorSpaces.ToRgb(doc, doc.Depth, planes, image);
        if (alpha is not null)
        {
            alpha.AsSpan().CopyTo(image.A);
        }
        else
        {
            image.A.AsSpan().Fill(1f);
        }

        return image;
    }

    /// <summary>Decodes a mask plane (user mask or baked vector mask) for its own rectangle.</summary>
    internal float[]? DecodeMask(LayerMask mask) => DecodeChannelPlane(mask.ChannelId, mask.Bounds);

    /// <summary>Returns the layer's stored pixels as straight 8-bit RGBA within <see cref="Bounds"/>, or null for empty layers.</summary>
    public RgbaImage? GetPixels()
    {
        var planar = DecodePixels();
        return planar?.ToRgba(Bounds);
    }

    /// <summary>
    /// Renders this layer alone (with its masks, opacity and blend mode, inside its
    /// ancestor groups) to a document-size image. Groups render their visible children.
    /// </summary>
    public RgbaImage Render() => Rendering.PsdRenderer.RenderLayer(this);

    public override string ToString() => $"{Kind} '{Name}' {Bounds} {BlendMode} op={Opacity}";
}
