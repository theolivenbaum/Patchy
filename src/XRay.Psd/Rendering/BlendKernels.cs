using System.Numerics;
using System.Runtime.CompilerServices;

namespace XRay.Psd.Rendering;

/// <summary>
/// Row compositing with the W3C/PDF source-over formula Photoshop uses:
/// <c>co = as(1-ab)Cs + as*ab*B(Cs,Cb) + (1-as)ab*Cb</c>, <c>ao = as + ab(1-as)</c>,
/// stored as straight color <c>co/ao</c>. All planes are straight-alpha floats.
/// </summary>
internal static class BlendKernels
{
    /// <summary>
    /// Composites one row. <paramref name="sa"/> is the effective source alpha
    /// (pixel alpha times opacity and masks). In <paramref name="clipMode"/> the
    /// destination is a clipping group: the source blends against the base color
    /// at full strength only where the base has coverage, and alpha is unchanged.
    /// With <paramref name="clipCoverage"/> (a clip base whose Blend If hid part of
    /// its own alpha) the clip shape is that coverage instead, and members restore
    /// output alpha inside it: <c>max(da, clip * (as + min(da, clip)/clip * (1 - as)))</c>
    /// (reference <c>IsolatedClipGroupTarget</c> after <c>freeze_clip</c>).
    /// </summary>
    public static void CompositeRow(
        PsdBlendMode mode,
        ReadOnlySpan<float> sr, ReadOnlySpan<float> sg, ReadOnlySpan<float> sb, ReadOnlySpan<float> sa,
        Span<float> dr, Span<float> dg, Span<float> db, Span<float> da,
        bool clipMode, ReadOnlySpan<float> clipCoverage = default)
    {
        if (clipMode && !clipCoverage.IsEmpty)
        {
            CompositeRowWithClipCoverage(mode, sr, sg, sb, sa, dr, dg, db, da, clipCoverage);
            return;
        }

        switch (mode)
        {
            case PsdBlendMode.Multiply: Row<Separable<MultiplyBlend>>(sr, sg, sb, sa, dr, dg, db, da, clipMode); break;
            case PsdBlendMode.Screen: Row<Separable<ScreenBlend>>(sr, sg, sb, sa, dr, dg, db, da, clipMode); break;
            case PsdBlendMode.Overlay: Row<Separable<OverlayBlend>>(sr, sg, sb, sa, dr, dg, db, da, clipMode); break;
            case PsdBlendMode.SoftLight: Row<Separable<SoftLightBlend>>(sr, sg, sb, sa, dr, dg, db, da, clipMode); break;
            case PsdBlendMode.HardLight: Row<Separable<HardLightBlend>>(sr, sg, sb, sa, dr, dg, db, da, clipMode); break;
            case PsdBlendMode.Darken: Row<Separable<DarkenBlend>>(sr, sg, sb, sa, dr, dg, db, da, clipMode); break;
            case PsdBlendMode.Lighten: Row<Separable<LightenBlend>>(sr, sg, sb, sa, dr, dg, db, da, clipMode); break;
            case PsdBlendMode.ColorDodge: Row<Separable<ColorDodgeBlend>>(sr, sg, sb, sa, dr, dg, db, da, clipMode); break;
            case PsdBlendMode.ColorBurn: Row<Separable<ColorBurnBlend>>(sr, sg, sb, sa, dr, dg, db, da, clipMode); break;
            case PsdBlendMode.LinearBurn: Row<Separable<LinearBurnBlend>>(sr, sg, sb, sa, dr, dg, db, da, clipMode); break;
            case PsdBlendMode.LinearDodge: Row<Separable<LinearDodgeBlend>>(sr, sg, sb, sa, dr, dg, db, da, clipMode); break;
            case PsdBlendMode.Difference: Row<Separable<DifferenceBlend>>(sr, sg, sb, sa, dr, dg, db, da, clipMode); break;
            case PsdBlendMode.Exclusion: Row<Separable<ExclusionBlend>>(sr, sg, sb, sa, dr, dg, db, da, clipMode); break;
            case PsdBlendMode.Subtract: Row<Separable<SubtractBlend>>(sr, sg, sb, sa, dr, dg, db, da, clipMode); break;
            case PsdBlendMode.Divide: Row<Separable<DivideBlend>>(sr, sg, sb, sa, dr, dg, db, da, clipMode); break;
            case PsdBlendMode.VividLight: Row<Separable<VividLightBlend>>(sr, sg, sb, sa, dr, dg, db, da, clipMode); break;
            case PsdBlendMode.LinearLight: Row<Separable<LinearLightBlend>>(sr, sg, sb, sa, dr, dg, db, da, clipMode); break;
            case PsdBlendMode.PinLight: Row<Separable<PinLightBlend>>(sr, sg, sb, sa, dr, dg, db, da, clipMode); break;
            case PsdBlendMode.HardMix: Row<Separable<HardMixBlend>>(sr, sg, sb, sa, dr, dg, db, da, clipMode); break;
            case PsdBlendMode.Hue: Row<HueBlend>(sr, sg, sb, sa, dr, dg, db, da, clipMode); break;
            case PsdBlendMode.Saturation: Row<SaturationBlend>(sr, sg, sb, sa, dr, dg, db, da, clipMode); break;
            case PsdBlendMode.Color: Row<ColorBlend>(sr, sg, sb, sa, dr, dg, db, da, clipMode); break;
            case PsdBlendMode.Luminosity: Row<LuminosityBlend>(sr, sg, sb, sa, dr, dg, db, da, clipMode); break;
            case PsdBlendMode.DarkerColor: Row<DarkerColorBlend>(sr, sg, sb, sa, dr, dg, db, da, clipMode); break;
            case PsdBlendMode.LighterColor: Row<LighterColorBlend>(sr, sg, sb, sa, dr, dg, db, da, clipMode); break;
            default: Row<Separable<NormalBlend>>(sr, sg, sb, sa, dr, dg, db, da, clipMode); break;
        }
    }

    // The clip run of a Blend If base: colors blend exactly as in clip mode, then
    // alpha grows inside the recorded clip coverage. Rare, so it reuses the
    // ordinary clip-mode row on a copy of the alpha and fixes alpha afterwards.
    private static void CompositeRowWithClipCoverage(
        PsdBlendMode mode,
        ReadOnlySpan<float> sr, ReadOnlySpan<float> sg, ReadOnlySpan<float> sb, ReadOnlySpan<float> sa,
        Span<float> dr, Span<float> dg, Span<float> db, Span<float> da,
        ReadOnlySpan<float> clipCoverage)
    {
        var count = sa.Length;
        var inside = new float[count];
        for (var i = 0; i < count; i++)
        {
            inside[i] = clipCoverage[i] > 0f ? 1f : 0f;
        }

        CompositeRow(mode, sr, sg, sb, sa, dr, dg, db, inside, clipMode: true);
        for (var i = 0; i < count; i++)
        {
            var clip = clipCoverage[i];
            if (clip <= 0f)
            {
                continue;
            }

            var a = Math.Clamp(sa[i], 0f, 1f);
            var normalized = Math.Min(da[i], clip) / clip;
            da[i] = Math.Max(da[i], clip * (a + (normalized * (1f - a))));
        }
    }

    private static void Row<TOp>(
        ReadOnlySpan<float> sr, ReadOnlySpan<float> sg, ReadOnlySpan<float> sb, ReadOnlySpan<float> sa,
        Span<float> dr, Span<float> dg, Span<float> db, Span<float> da,
        bool clipMode)
        where TOp : struct, IBlendOp
    {
        var count = sa.Length;
        var width = Vector<float>.Count;
        var i = 0;
        for (; i <= count - width; i += width)
        {
            var vdr = new Vector<float>(dr[i..]);
            var vdg = new Vector<float>(dg[i..]);
            var vdb = new Vector<float>(db[i..]);
            var vda = new Vector<float>(da[i..]);
            Step<TOp>(new Vector<float>(sr[i..]), new Vector<float>(sg[i..]), new Vector<float>(sb[i..]), new Vector<float>(sa[i..]), ref vdr, ref vdg, ref vdb, ref vda, clipMode);
            vdr.CopyTo(dr[i..]);
            vdg.CopyTo(dg[i..]);
            vdb.CopyTo(db[i..]);
            vda.CopyTo(da[i..]);
        }

        if (i < count)
        {
            // Tail: run the same vector step on a zero-padded copy so the
            // remainder uses identical math.
            var tail = count - i;
            Span<float> buffer = stackalloc float[width * 8];
            buffer.Clear();
            var tsr = buffer[..width];
            var tsg = buffer.Slice(width, width);
            var tsb = buffer.Slice(width * 2, width);
            var tsa = buffer.Slice(width * 3, width);
            var tdr = buffer.Slice(width * 4, width);
            var tdg = buffer.Slice(width * 5, width);
            var tdb = buffer.Slice(width * 6, width);
            var tda = buffer.Slice(width * 7, width);
            sr[i..].CopyTo(tsr);
            sg[i..].CopyTo(tsg);
            sb[i..].CopyTo(tsb);
            sa[i..].CopyTo(tsa);
            dr[i..].CopyTo(tdr);
            dg[i..].CopyTo(tdg);
            db[i..].CopyTo(tdb);
            da[i..].CopyTo(tda);
            var vdr = new Vector<float>(tdr);
            var vdg = new Vector<float>(tdg);
            var vdb = new Vector<float>(tdb);
            var vda = new Vector<float>(tda);
            Step<TOp>(new Vector<float>(tsr), new Vector<float>(tsg), new Vector<float>(tsb), new Vector<float>(tsa), ref vdr, ref vdg, ref vdb, ref vda, clipMode);
            vdr.CopyTo(tdr);
            vdg.CopyTo(tdg);
            vdb.CopyTo(tdb);
            vda.CopyTo(tda);
            tdr[..tail].CopyTo(dr[i..]);
            tdg[..tail].CopyTo(dg[i..]);
            tdb[..tail].CopyTo(db[i..]);
            tda[..tail].CopyTo(da[i..]);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Step<TOp>(
        Vector<float> sr, Vector<float> sg, Vector<float> sb, Vector<float> sa,
        ref Vector<float> dr, ref Vector<float> dg, ref Vector<float> db, ref Vector<float> da,
        bool clipMode)
        where TOp : struct, IBlendOp
    {
        sa = BlendConstants.Clamp01(sa);
        TOp.Apply(sr, sg, sb, dr, dg, db, out var br, out var bg, out var bb);
        br = BlendConstants.Clamp01(br);
        bg = BlendConstants.Clamp01(bg);
        bb = BlendConstants.Clamp01(bb);
        if (clipMode)
        {
            var inside = Vector.GreaterThan(da, BlendConstants.Zero);
            dr = Vector.ConditionalSelect(inside, dr + (sa * (br - dr)), dr);
            dg = Vector.ConditionalSelect(inside, dg + (sa * (bg - dg)), dg);
            db = Vector.ConditionalSelect(inside, db + (sa * (bb - db)), db);
            return;
        }

        var one = BlendConstants.One;
        var outAlpha = sa + da - (sa * da);
        var sourceOnly = sa * (one - da);
        var both = sa * da;
        var destinationOnly = (one - sa) * da;
        var hasAlpha = Vector.GreaterThan(outAlpha, BlendConstants.Zero);
        var inverse = Vector.ConditionalSelect(hasAlpha, one / Vector.Max(outAlpha, BlendConstants.Epsilon), BlendConstants.Zero);
        dr = ((sourceOnly * sr) + (both * br) + (destinationOnly * dr)) * inverse;
        dg = ((sourceOnly * sg) + (both * bg) + (destinationOnly * dg)) * inverse;
        db = ((sourceOnly * sb) + (both * bb) + (destinationOnly * db)) * inverse;
        da = outAlpha;
    }

    /// <summary>Premultiplied interpolation from the before rows toward the current row by <paramref name="t"/> (pass-through group opacity).</summary>
    public static void FadeTowardRow(
        ReadOnlySpan<float> br, ReadOnlySpan<float> bg, ReadOnlySpan<float> bb, ReadOnlySpan<float> ba,
        Span<float> dr, Span<float> dg, Span<float> db, Span<float> da, float t)
    {
        for (var i = 0; i < da.Length; i++)
        {
            var alpha = ba[i] + ((da[i] - ba[i]) * t);
            if (alpha <= 0)
            {
                dr[i] = dg[i] = db[i] = da[i] = 0;
                continue;
            }

            var inverse = 1f / alpha;
            dr[i] = ((br[i] * ba[i]) + (((dr[i] * da[i]) - (br[i] * ba[i])) * t)) * inverse;
            dg[i] = ((bg[i] * ba[i]) + (((dg[i] * da[i]) - (bg[i] * ba[i])) * t)) * inverse;
            db[i] = ((bb[i] * ba[i]) + (((db[i] * da[i]) - (bb[i] * ba[i])) * t)) * inverse;
            da[i] = alpha;
        }
    }

    /// <summary>Multiplies <paramref name="destination"/> by <paramref name="factor"/> element-wise (SIMD).</summary>
    public static void MultiplyInPlace(Span<float> destination, ReadOnlySpan<float> factor)
    {
        var i = 0;
        var width = Vector<float>.Count;
        for (; i <= destination.Length - width; i += width)
        {
            (new Vector<float>(destination[i..]) * new Vector<float>(factor[i..])).CopyTo(destination[i..]);
        }

        for (; i < destination.Length; i++)
        {
            destination[i] *= factor[i];
        }
    }

    /// <summary>Scales <paramref name="destination"/> by a constant (SIMD).</summary>
    public static void Scale(Span<float> destination, float factor)
    {
        if (factor == 1f)
        {
            return;
        }

        var i = 0;
        var width = Vector<float>.Count;
        var vector = new Vector<float>(factor);
        for (; i <= destination.Length - width; i += width)
        {
            (new Vector<float>(destination[i..]) * vector).CopyTo(destination[i..]);
        }

        for (; i < destination.Length; i++)
        {
            destination[i] *= factor;
        }
    }
}
