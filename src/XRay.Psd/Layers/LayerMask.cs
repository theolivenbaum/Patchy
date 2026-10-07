namespace XRay.Psd.Layers;

/// <summary>
/// A raster layer mask. Pixels outside <see cref="Bounds"/> take
/// <see cref="DefaultColor"/>. The plane itself decodes on demand from the
/// owning layer's channel data.
/// </summary>
public sealed record LayerMask
{
    public PsdRect Bounds { get; init; }

    /// <summary>Mask value (0-255) outside the stored rectangle.</summary>
    public byte DefaultColor { get; init; }

    public bool Disabled { get; init; }

    /// <summary>The mask moves with the layer (chain icon on).</summary>
    public bool Linked { get; init; } = true;

    /// <summary>
    /// The stored plane was rendered from other data (Photoshop's baked
    /// vector-mask coverage) rather than painted by the user.
    /// </summary>
    public bool FromRendering { get; init; }

    /// <summary>Mask density 0-255 (255 is full strength).</summary>
    public byte Density { get; init; } = 255;

    /// <summary>Mask feather radius in pixels.</summary>
    public double Feather { get; init; }

    /// <summary>Channel ID holding the plane: -2 (user mask) or -3 (real user mask).</summary>
    public short ChannelId { get; init; } = -2;
}
