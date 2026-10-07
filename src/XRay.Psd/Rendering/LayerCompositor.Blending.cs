using XRay.Psd.Imaging;
using XRay.Psd.Layers;

namespace XRay.Psd.Rendering;

/// <summary>
/// Advanced Blending: Blend If gates, Fill on the eight special modes, and the
/// channel restrictions (<c>brst</c>). Rules: .reference/docs/blend-modes.md,
/// .reference/docs/ps-compat.md ("Advanced Blending Channels") and
/// .reference/docs/layer-effects-render.md ("Blend If rendering").
/// </summary>
internal sealed partial class LayerCompositor
{
    private const byte RestrictRed = 0x01;
    private const byte RestrictGreen = 0x02;
    private const byte RestrictBlue = 0x04;
    private const byte RestrictAll = RestrictRed | RestrictGreen | RestrictBlue;

    private readonly Dictionary<PsdLayer, BlendIf?> _blendIfCache = [];
    private readonly Dictionary<PsdLayer, byte> _restrictionCache = [];

    // Clip coverage of a clipping-run buffer whose base has Blend If: the base's
    // ungated alpha, which stays the clip shape for the members (reference
    // record_clip_coverage / freeze_clip).
    private readonly Dictionary<PlanarImage, float[]> _clipCoverage = [];

    /// <summary>The layer's rendered Blend If gate, or null (cached).</summary>
    private BlendIf? BlendIfOf(PsdLayer layer)
    {
        if (!_blendIfCache.TryGetValue(layer, out var gate))
        {
            gate = BlendIf.FromLayer(layer);
            _blendIfCache[layer] = gate;
        }

        return gate;
    }

    /// <summary>
    /// The channels a layer excludes from compositing (bit 0 red, 1 green, 2 blue).
    /// <c>brst</c> is a bare list of big-endian u32 channel indices; anything that does
    /// not map onto RGB (another color mode, an index above 2, a ragged length) is not
    /// rendered, as in the reference import (<c>psd_document_io.cpp</c>).
    /// </summary>
    private byte RestrictionOf(PsdLayer layer)
    {
        if (_restrictionCache.TryGetValue(layer, out var cached))
        {
            return cached;
        }

        byte mask = 0;
        if (layer.GetTaggedBlock("brst") is { } block && block.Data.Length % 4 == 0 && layer.Document.ColorMode == PsdColorMode.Rgb)
        {
            var data = block.Data.Span;
            for (var i = 0; i < data.Length; i += 4)
            {
                var index = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data[i..]);
                if (index > 2)
                {
                    mask = 0;
                    break;
                }

                mask |= (byte)(1 << (int)index);
            }
        }

        _restrictionCache[layer] = mask;
        return mask;
    }

    /// <summary>Every pixel a layer (with its effects) can write, used to snapshot channel-restricted composites.</summary>
    private PsdRect AffectedBounds(PsdLayer layer)
    {
        if (layer.Kind is PsdLayerKind.Adjustment or PsdLayerKind.Group)
        {
            return _canvas;
        }

        var bounds = LayerPixels(layer)?.Bounds ?? default;
        if (!bounds.IsEmpty && EffectsOf(layer) is { } fx)
        {
            var reach = EffectReach(fx);
            bounds = new PsdRect(bounds.Left - reach, bounds.Top - reach, bounds.Right + reach, bounds.Bottom + reach);
        }

        return bounds;
    }

    /// <summary>
    /// Runs <paramref name="draw"/> with Photoshop's channel restriction: each excluded
    /// channel keeps the backdrop's PREMULTIPLIED value, <c>c = pre_c * pre_a / out_a</c>,
    /// while the other channels and alpha composite normally (reference
    /// <c>ChannelRestrictedTarget</c>). Keeping the premultiplied value per write or once
    /// at the end is the same thing, so the whole composite runs first and the excluded
    /// channels are restored over <paramref name="region"/> afterwards.
    /// </summary>
    private static void WithChannelRestriction(PlanarImage target, byte restriction, PsdRect region, Action draw)
    {
        region = region.Intersect(target.Bounds);
        if (restriction == 0 || region.IsEmpty)
        {
            draw();
            return;
        }

        var before = Crop(target, region);
        draw();
        var width = region.Width;
        for (var y = region.Top; y < region.Bottom; y++)
        {
            var t = target.RowOffset(y, region.Left);
            var b = before.RowOffset(y, region.Left);
            if ((restriction & RestrictRed) != 0)
            {
                KeepChannel(before.R.AsSpan(b, width), before.A.AsSpan(b, width), target.R.AsSpan(t, width), target.A.AsSpan(t, width));
            }

            if ((restriction & RestrictGreen) != 0)
            {
                KeepChannel(before.G.AsSpan(b, width), before.A.AsSpan(b, width), target.G.AsSpan(t, width), target.A.AsSpan(t, width));
            }

            if ((restriction & RestrictBlue) != 0)
            {
                KeepChannel(before.B.AsSpan(b, width), before.A.AsSpan(b, width), target.B.AsSpan(t, width), target.A.AsSpan(t, width));
            }
        }
    }

    private static void KeepChannel(ReadOnlySpan<float> preColor, ReadOnlySpan<float> preAlpha, Span<float> color, ReadOnlySpan<float> alpha)
    {
        for (var i = 0; i < color.Length; i++)
        {
            var a = alpha[i];
            if (a <= 0f)
            {
                continue;
            }

            // Equal alphas restore the exact value so alpha-neutral writes cannot drift.
            color[i] = preAlpha[i] == a ? preColor[i] : Math.Clamp(preColor[i] * preAlpha[i] / a, 0f, 1f);
        }
    }

    /// <summary>The clip coverage row of a Blend If clipping-run buffer, or empty.</summary>
    private ReadOnlySpan<float> ClipCoverageRow(PlanarImage target, int offset, int width) =>
        _clipCoverage.TryGetValue(target, out var plane) ? plane.AsSpan(offset, width) : default;

    /// <summary>
    /// Composites one row of layer content. <paramref name="coverage"/> holds the source
    /// coverage (pixel alpha times masks and knockouts, without opacity or Fill) and is
    /// consumed. Applies, in the reference order: clip-coverage recording, the Blend If
    /// gates (This Layer on the source color, Underlying on <paramref name="gateBackdrop"/>
    /// or the target as it stands), the special-Fill split, then opacity, Fill and Dissolve.
    /// </summary>
    private void CompositeLayerRow(
        PlanarImage target, int y, int x0, PsdBlendMode mode,
        ReadOnlySpan<float> r, ReadOnlySpan<float> g, ReadOnlySpan<float> b, Span<float> coverage,
        float opacity, float fill, BlendIf? gate, PlanarImage? gateBackdrop, bool clipMode)
    {
        var width = coverage.Length;
        var t = target.RowOffset(y, x0);
        _clipCoverage.TryGetValue(target, out var clip);
        if (clip is not null && !clipMode)
        {
            // The clip shape is the base's coverage before Blend If (Blend If on a
            // clipping base does not shrink the clip).
            var row = clip.AsSpan(t, width);
            var strength = opacity * fill;
            for (var i = 0; i < width; i++)
            {
                var a = Math.Clamp(coverage[i] * strength, 0f, 1f);
                row[i] = a + (row[i] * (1f - a));
            }
        }

        if (gate is not null)
        {
            ApplyGate(gate, r, g, b, coverage, gateBackdrop ?? target, y, x0);
        }

        var clipRow = clipMode && clip is not null ? clip.AsSpan(t, width) : default;
        var dr = target.R.AsSpan(t, width);
        var dg = target.G.AsSpan(t, width);
        var db = target.B.AsSpan(t, width);
        var da = target.A.AsSpan(t, width);
        if (fill < 1f && SpecialFill.Applies(mode))
        {
            SpecialFill.CompositeRow(mode, r, g, b, coverage, fill, opacity, dr, dg, db, da, clipMode, clipRow);
            return;
        }

        BlendKernels.Scale(coverage, opacity * fill);
        if (mode == PsdBlendMode.Dissolve)
        {
            Dissolve(coverage, x0, y);
            mode = PsdBlendMode.Normal;
        }

        BlendKernels.CompositeRow(mode, r, g, b, coverage, dr, dg, db, da, clipMode, clipRow);
    }

    /// <summary>Multiplies <paramref name="coverage"/> by both Blend If gates; backdrop pixels outside <paramref name="backdrop"/> count as transparent (they pass).</summary>
    private void ApplyGate(BlendIf gate, ReadOnlySpan<float> r, ReadOnlySpan<float> g, ReadOnlySpan<float> b, Span<float> coverage, PlanarImage backdrop, int y, int x0)
    {
        gate.MultiplySource(r, g, b, coverage);
        if (!gate.HasUnderlying || y < backdrop.Bounds.Top || y >= backdrop.Bounds.Bottom)
        {
            return;
        }

        var start = Math.Max(x0, backdrop.Bounds.Left);
        var end = Math.Min(x0 + coverage.Length, backdrop.Bounds.Right);
        if (end <= start)
        {
            return;
        }

        var width = end - start;
        var o = backdrop.RowOffset(y, start);
        gate.MultiplyUnderlying(
            backdrop.R.AsSpan(o, width), backdrop.G.AsSpan(o, width), backdrop.B.AsSpan(o, width), backdrop.A.AsSpan(o, width),
            coverage.Slice(start - x0, width));
    }

    /// <summary>Multiplies an adjustment layer's amount row by its Blend If gates: This Layer on the adjusted color, Underlying on the pre-adjustment backdrop.</summary>
    private static void ApplyAdjustmentGate(BlendIf gate, ReadOnlySpan<float> adjustedR, ReadOnlySpan<float> adjustedG, ReadOnlySpan<float> adjustedB,
        ReadOnlySpan<float> backdropR, ReadOnlySpan<float> backdropG, ReadOnlySpan<float> backdropB, ReadOnlySpan<float> backdropA, Span<float> amount)
    {
        gate.MultiplySource(adjustedR, adjustedG, adjustedB, amount);
        gate.MultiplyUnderlying(backdropR, backdropG, backdropB, backdropA, amount);
    }
}
