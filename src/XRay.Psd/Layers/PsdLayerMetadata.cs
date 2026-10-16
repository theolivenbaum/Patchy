using XRay.Psd.Descriptors;
using XRay.Psd.IO;

namespace XRay.Psd.Layers;

/// <summary>A point or offset in document pixels.</summary>
public readonly record struct PsdPoint(double X, double Y);

/// <summary>
/// One item of a layer's metadata block (<c>shmd</c>): a four-character key, the
/// "copy on sheet duplication" flag and the payload. Photoshop's items are
/// <c>cust</c> (layer time), <c>cmls</c> (layer comp state), <c>mlst</c> (animation
/// frames), <c>tmln</c> (timeline), <c>extn</c> (generator settings), all versioned
/// descriptors, and <c>mdyn</c> (animation flags, binary).
/// </summary>
/// <param name="Key">The item key, such as <c>cmls</c>.</param>
/// <param name="CopyOnSheetDuplication">Whether Photoshop copies the item when the layer is duplicated.</param>
/// <param name="Data">The raw payload.</param>
/// <param name="Descriptor">The payload as a descriptor, when it is a versioned descriptor that decodes.</param>
public sealed record PsdLayerMetadataItem(string Key, bool CopyOnSheetDuplication, ReadOnlyMemory<byte> Data, Descriptor? Descriptor);

/// <summary>
/// What one layer comp records for a layer (one entry of <c>cmls/layerSettings</c>).
/// <see cref="CompIds"/> lists the comps (resource 1065 <c>compID</c>) the entry applies
/// to; ID 0 is the document state Photoshop keeps beside the comps ("Last Document State").
/// Each value is null when the entry does not store it, which means the comp does not
/// change it from the layer's saved state.
/// </summary>
public sealed record PsdLayerCompState
{
    /// <summary>The comp IDs this state applies to (<c>compList</c>).</summary>
    public IReadOnlyList<int> CompIds { get; init; } = [];

    /// <summary>Layer visibility in the comp (<c>enab</c>).</summary>
    public bool? Visible { get; init; }

    /// <summary>
    /// The layer's position in the comp as an offset in pixels from its saved position
    /// (<c>Ofst</c>). In <c>layer_comps.psd</c> the document state stores 0, 0 and the comp
    /// that moves the eyes stores 9, 0.
    /// </summary>
    public PsdPoint? Offset { get; init; }

    /// <summary>The vector mask's stored offset (<c>vectorMask/Ofst</c>), as stored.</summary>
    public PsdPoint? VectorMaskOffset { get; init; }

    /// <summary>The layer effects' reference point (<c>FXRefPoint</c>), in pixels.</summary>
    public PsdPoint? EffectsReferencePoint { get; init; }

    /// <summary>Blend mode (appearance, <c>blendOptions/Md  </c>).</summary>
    public PsdBlendMode? BlendMode { get; init; }

    /// <summary>Opacity from 0 to 1 (appearance, <c>blendOptions/Opct</c>).</summary>
    public double? Opacity { get; init; }

    /// <summary>Fill opacity from 0 to 1 (appearance, <c>blendOptions/fillOpacity</c>).</summary>
    public double? FillOpacity { get; init; }

    /// <summary>The comp's layer effects descriptor (appearance, <c>Lefx</c>), in the <c>lfx2</c> layout.</summary>
    public Descriptor? Effects { get; init; }

    /// <summary>The raw entry.</summary>
    public required Descriptor Descriptor { get; init; }
}

/// <summary>
/// Typed view of a layer's metadata block (<c>shmd</c>): every item, the layer time
/// (<c>cust</c>) and the per-comp layer state (<c>cmls</c>) that Photoshop's layer comps
/// (<see cref="Resources.PsdImageResources.LayerComps"/>) apply. Built by
/// <see cref="PsdLayer.Metadata"/>; a damaged block yields the items that decoded.
/// </summary>
public sealed class PsdLayerMetadata
{
    private PsdLayerMetadata()
    {
    }

    /// <summary>Every item in file order.</summary>
    public IReadOnlyList<PsdLayerMetadataItem> Items { get; private init; } = [];

    /// <summary>The layer's last modification time (<c>cust/layerTime</c>, Unix seconds), when stored.</summary>
    public DateTimeOffset? LayerTime { get; private init; }

    /// <summary>The layer ID the comp state was recorded for (<c>cmls/LyrI</c>), when stored.</summary>
    public int? CompLayerId { get; private init; }

    /// <summary>The effects reference point when the comps were captured (<c>cmls/origFXRefPoint</c>).</summary>
    public PsdPoint? OriginalEffectsReferencePoint { get; private init; }

    /// <summary>The per-comp states (<c>cmls/layerSettings</c>); empty without layer comps.</summary>
    public IReadOnlyList<PsdLayerCompState> CompStates { get; private init; } = [];

    /// <summary>The item with the given key, or null.</summary>
    public PsdLayerMetadataItem? GetItem(string key) => Items.FirstOrDefault(i => i.Key == key);

    /// <summary>The state the comp with <paramref name="compId"/> records for this layer, or null.</summary>
    public PsdLayerCompState? GetCompState(int compId) => CompStates.FirstOrDefault(s => s.CompIds.Contains(compId));

    /// <summary>Parses a <c>shmd</c> payload. Returns null when not even the item count decodes.</summary>
    internal static PsdLayerMetadata? Parse(ReadOnlyMemory<byte> payload)
    {
        // Layout (Adobe "Metadata setting", psd-tools MetadataSettings): u32 count, then per
        // item '8BIM' (or '8ELE'), key, u8 copy flag, 3 padding bytes, u32 length, payload.
        var reader = new BigEndianReader(payload);
        if (reader.Remaining < 4)
        {
            return null;
        }

        var count = reader.ReadUInt32();
        var items = new List<PsdLayerMetadataItem>();
        try
        {
            for (var i = 0u; i < count && reader.Remaining >= 16; i++)
            {
                var signature = reader.ReadSignature();
                if (signature is not ("8BIM" or "8ELE"))
                {
                    break;
                }

                var key = reader.ReadSignature();
                var copy = reader.ReadByte() != 0;
                reader.Skip(3);
                var length = reader.ReadUInt32();
                if (length > reader.Remaining)
                {
                    break;
                }

                var data = reader.ReadMemory((long)length);
                items.Add(new PsdLayerMetadataItem(key, copy, data, ReadDescriptor(data)));
            }
        }
        catch (PsdFormatException)
        {
            // Keep the items that decoded.
        }

        var custom = items.FirstOrDefault(i => i.Key == "cust")?.Descriptor;
        var comps = items.FirstOrDefault(i => i.Key == "cmls")?.Descriptor;
        return new PsdLayerMetadata
        {
            Items = items,
            LayerTime = LayerTimeOf(custom),
            CompLayerId = comps is not null && comps.TryGet("LyrI", out var id) && id.Type is DescriptorValueType.Integer && id.Integer is >= int.MinValue and <= int.MaxValue ? (int)id.Integer : null,
            OriginalEffectsReferencePoint = Point(comps?.GetObject("origFXRefPoint")),
            CompStates = comps is null ? [] : CompStatesOf(comps),
        };
    }

    private static Descriptor? ReadDescriptor(ReadOnlyMemory<byte> data)
    {
        if (data.Length < 12 || System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data.Span) != 16)
        {
            return null;
        }

        try
        {
            return Descriptor.ReadVersioned(new BigEndianReader(data));
        }
        catch (PsdFormatException)
        {
            return null;
        }
    }

    private static DateTimeOffset? LayerTimeOf(Descriptor? custom)
    {
        var seconds = custom?.GetNumber("layerTime") ?? double.NaN;

        // Plausible Unix times only (1970 to 9999).
        return double.IsFinite(seconds) && seconds is >= 0 and < 253402300800
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(seconds * 1000))
            : null;
    }

    private static List<PsdLayerCompState> CompStatesOf(Descriptor comps)
    {
        var states = new List<PsdLayerCompState>();
        foreach (var item in comps.GetList("layerSettings") ?? [])
        {
            if (item.Object is not { } entry)
            {
                continue;
            }

            var ids = new List<int>();
            foreach (var id in entry.GetList("compList") ?? [])
            {
                var value = id.AsNumber();
                if (double.IsFinite(value) && value is >= int.MinValue and <= int.MaxValue)
                {
                    ids.Add((int)value);
                }
            }

            var blend = entry.GetObject("blendOptions");
            states.Add(new PsdLayerCompState
            {
                CompIds = ids,
                Visible = entry.TryGet("enab", out var visible) && visible.Type == DescriptorValueType.Boolean ? visible.Boolean : null,
                Offset = Point(entry.GetObject("Ofst")),
                VectorMaskOffset = Point(entry.GetObject("vectorMask")?.GetObject("Ofst")),
                EffectsReferencePoint = Point(entry.GetObject("FXRefPoint")),
                BlendMode = blend?.GetEnum("Md  ") is { } mode ? BlendModeKeys.FromDescriptorEnum(mode) : null,
                Opacity = Percent(blend, "Opct"),
                FillOpacity = Percent(blend, "fillOpacity"),
                Effects = entry.GetObject("Lefx"),
                Descriptor = entry,
            });
        }

        return states;
    }

    private static PsdPoint? Point(Descriptor? point)
    {
        if (point is null)
        {
            return null;
        }

        var x = point.GetNumber("Hrzn");
        var y = point.GetNumber("Vrtc");
        return double.IsFinite(x) && double.IsFinite(y) ? new PsdPoint(x, y) : null;
    }

    private static double? Percent(Descriptor? descriptor, string key)
    {
        var value = descriptor?.GetNumber(key) ?? double.NaN;
        return double.IsFinite(value) ? Math.Clamp(value / 100, 0, 1) : null;
    }
}
