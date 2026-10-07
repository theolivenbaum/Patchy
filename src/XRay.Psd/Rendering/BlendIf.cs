using XRay.Psd.Layers;

namespace XRay.Psd.Rendering;

/// <summary>
/// Photoshop's Blend If gates (layer blending ranges), calibrated in the reference
/// <c>blend_if_*</c> functions (.reference/src/core/blend_math.cpp) and documented in
/// .reference/docs/layer-effects-render.md "Blend If rendering":
/// <list type="bullet">
/// <item>The record holds Gray, Red, Green and Blue entries, each a This Layer and an Underlying Layer range of four bytes (black low, black high, white low, white high), followed by one identity transparency entry. Other shapes stay unrendered.</item>
/// <item>A split black range <c>[a,b]</c> keeps byte v as <c>(v-a+1)/(b-a+1)</c>, joined handles cut off hard, the white side mirrors it. Each gate is a byte <c>(n*255)/d</c>, and the four gates multiply with truncation (<c>f = f*g/255</c>).</item>
/// <item>Gray is <c>(299R + 590G + 111B + 500)/1000</c>.</item>
/// <item>The Underlying gate tests the backdrop's covered fraction only: <c>(1-da) + da*gate</c>.</item>
/// </list>
/// Gates are evaluated on 8-bit colors; the float planes are rounded to bytes first.
/// </summary>
internal sealed class BlendIf
{
    private const int EntryCount = 4;
    private static readonly byte[] IdentityEntry = [0, 0, 255, 255, 0, 0, 255, 255];

    // Per channel (Gray, R, G, B): 256-entry gate tables for each side.
    private readonly byte[][] _source;
    private readonly byte[][] _underlying;

    private BlendIf(byte[][] source, byte[][] underlying, bool hasSource, bool hasUnderlying)
    {
        _source = source;
        _underlying = underlying;
        HasSource = hasSource;
        HasUnderlying = hasUnderlying;
    }

    /// <summary>Some This Layer range is not the identity.</summary>
    public bool HasSource { get; }

    /// <summary>Some Underlying Layer range is not the identity (the gate needs the backdrop).</summary>
    public bool HasUnderlying { get; }

    /// <summary>
    /// The rendered gate of a layer, or null when its ranges are absent, the identity,
    /// or not modeled (a non-RGB document, another payload size, unordered handles).
    /// </summary>
    public static BlendIf? FromLayer(PsdLayer layer) =>
        layer.Document.ColorMode == PsdColorMode.Rgb ? Parse(layer.BlendingRanges.Span) : null;

    /// <summary>Decodes an RGB blending-ranges payload (reference <c>decode_layer_blend_if</c>).</summary>
    public static BlendIf? Parse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 40 || !payload[32..].SequenceEqual(IdentityEntry))
        {
            return null;
        }

        var source = new byte[EntryCount][];
        var underlying = new byte[EntryCount][];
        var hasSource = false;
        var hasUnderlying = false;
        for (var channel = 0; channel < EntryCount; channel++)
        {
            var entry = payload.Slice(channel * 8, 8);
            if (!IsValid(entry[..4]) || !IsValid(entry[4..]))
            {
                return null;
            }

            hasSource |= !IsIdentity(entry[..4]);
            hasUnderlying |= !IsIdentity(entry[4..]);
            source[channel] = Table(entry[0], entry[1], entry[2], entry[3]);
            underlying[channel] = Table(entry[4], entry[5], entry[6], entry[7]);
        }

        return hasSource || hasUnderlying ? new BlendIf(source, underlying, hasSource, hasUnderlying) : null;
    }

    private static bool IsValid(ReadOnlySpan<byte> range) => range[0] <= range[1] && range[1] <= range[2] && range[2] <= range[3];

    private static bool IsIdentity(ReadOnlySpan<byte> range) => range[0] == 0 && range[1] == 0 && range[2] == 255 && range[3] == 255;

    private static byte[] Table(byte blackLow, byte blackHigh, byte whiteLow, byte whiteHigh)
    {
        var table = new byte[256];
        for (var v = 0; v < 256; v++)
        {
            table[v] = ThresholdByte(blackLow, blackHigh, whiteLow, whiteHigh, (byte)v);
        }

        return table;
    }

    /// <summary>One range's gate for a byte value (reference <c>blend_if_threshold_alpha_byte</c>).</summary>
    public static byte ThresholdByte(byte blackLow, byte blackHigh, byte whiteLow, byte whiteHigh, byte value)
    {
        if (value < blackLow || value > whiteHigh)
        {
            return 0;
        }

        if (value < blackHigh)
        {
            // Split handles include both byte endpoints in the feather.
            return (byte)((value - blackLow + 1) * 255 / (blackHigh - blackLow + 1));
        }

        if (value > whiteLow)
        {
            return (byte)((whiteHigh - value + 1) * 255 / (whiteHigh - whiteLow + 1));
        }

        return 255;
    }

    /// <summary>Photoshop's composite gray for Blend If (reference <c>blend_if_gray_value</c>).</summary>
    public static byte GrayValue(byte r, byte g, byte b) => (byte)(((299 * r) + (590 * g) + (111 * b) + 500) / 1000);

    /// <summary>The This Layer gate (0-255) for a source color.</summary>
    public byte SourceByte(byte r, byte g, byte b) => Gate(_source, r, g, b);

    /// <summary>The Underlying Layer gate (0-255) for a fully covered backdrop color.</summary>
    public byte UnderlyingByte(byte r, byte g, byte b) => Gate(_underlying, r, g, b);

    private static byte Gate(byte[][] tables, byte r, byte g, byte b)
    {
        var factor = 255;
        factor = factor * tables[0][GrayValue(r, g, b)] / 255;
        factor = factor * tables[1][r] / 255;
        factor = factor * tables[2][g] / 255;
        factor = factor * tables[3][b] / 255;
        return (byte)factor;
    }

    /// <summary>Multiplies <paramref name="factor"/> by the This Layer gate of each source pixel.</summary>
    public void MultiplySource(ReadOnlySpan<float> r, ReadOnlySpan<float> g, ReadOnlySpan<float> b, Span<float> factor)
    {
        if (!HasSource)
        {
            return;
        }

        for (var i = 0; i < factor.Length; i++)
        {
            if (factor[i] > 0f)
            {
                factor[i] *= SourceByte(ToByte(r[i]), ToByte(g[i]), ToByte(b[i])) / 255f;
            }
        }
    }

    /// <summary>
    /// Multiplies <paramref name="factor"/> by the Underlying Layer gate of each backdrop
    /// pixel; the transparent fraction of the backdrop always passes.
    /// </summary>
    public void MultiplyUnderlying(ReadOnlySpan<float> r, ReadOnlySpan<float> g, ReadOnlySpan<float> b, ReadOnlySpan<float> a, Span<float> factor)
    {
        if (!HasUnderlying)
        {
            return;
        }

        for (var i = 0; i < factor.Length; i++)
        {
            if (factor[i] > 0f)
            {
                var coverage = Math.Clamp(a[i], 0f, 1f);
                factor[i] *= (1f - coverage) + (coverage * (UnderlyingByte(ToByte(r[i]), ToByte(g[i]), ToByte(b[i])) / 255f));
            }
        }
    }

    internal static byte ToByte(float value) => (byte)Math.Clamp((int)((value * 255f) + 0.5f), 0, 255);
}
