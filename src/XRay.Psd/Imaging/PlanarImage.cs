using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace XRay.Psd.Imaging;

/// <summary>
/// Straight-alpha RGBA image stored as four float planes, positioned at
/// <see cref="Bounds"/> in document space. Planar storage lets every blend and
/// conversion loop run over contiguous <see cref="Vector{T}"/> lanes.
/// </summary>
internal sealed class PlanarImage
{
    public PlanarImage(PsdRect bounds)
    {
        Bounds = bounds;
        var count = bounds.Width * bounds.Height;
        R = new float[count];
        G = new float[count];
        B = new float[count];
        A = new float[count];
    }

    public PsdRect Bounds { get; }

    public int Width => Bounds.Width;

    public int Height => Bounds.Height;

    public float[] R { get; }

    public float[] G { get; }

    public float[] B { get; }

    public float[] A { get; }

    public int RowOffset(int documentY, int documentX) => ((documentY - Bounds.Top) * Width) + (documentX - Bounds.Left);

    public PlanarImage Clone()
    {
        var copy = new PlanarImage(Bounds);
        R.CopyTo(copy.R, 0);
        G.CopyTo(copy.G, 0);
        B.CopyTo(copy.B, 0);
        A.CopyTo(copy.A, 0);
        return copy;
    }

    /// <summary>Fills every pixel with an opaque or translucent color.</summary>
    public void Fill(float r, float g, float b, float a)
    {
        R.AsSpan().Fill(r);
        G.AsSpan().Fill(g);
        B.AsSpan().Fill(b);
        A.AsSpan().Fill(a);
    }

    /// <summary>Converts the region <paramref name="rect"/> (document space) to 8-bit straight RGBA. Pixels outside this image are transparent.</summary>
    public RgbaImage ToRgba(PsdRect rect)
    {
        var output = new RgbaImage(rect.Width, rect.Height);
        var overlap = rect.Intersect(Bounds);
        if (overlap.IsEmpty)
        {
            return output;
        }

        var width = overlap.Width;
        var pixels = output.Pixels;
        Parallelism.For(overlap.Height, width, (start, end) =>
        {
            for (var y = overlap.Top + start; y < overlap.Top + end; y++)
            {
                var source = RowOffset(y, overlap.Left);
                var target = (((y - rect.Top) * rect.Width) + (overlap.Left - rect.Left)) * 4;
                InterleaveRow(R.AsSpan(source, width), G.AsSpan(source, width), B.AsSpan(source, width), A.AsSpan(source, width), pixels.AsSpan(target, width * 4));
            }
        });

        return output;
    }

    /// <summary>
    /// Rounds four float planes to bytes and interleaves them as RGBA; fully transparent
    /// pixels stay zero. Each lane packs <c>r | g &lt;&lt; 8 | b &lt;&lt; 16 | a &lt;&lt; 24</c> in a
    /// <see cref="Vector{T}"/> of 32-bit words, using the same rounding as
    /// <see cref="UnitFloatToBytes"/> (vector lanes for whole vectors, the scalar formula for the tail).
    /// </summary>
    internal static void InterleaveRow(ReadOnlySpan<float> r, ReadOnlySpan<float> g, ReadOnlySpan<float> b, ReadOnlySpan<float> a, Span<byte> rgba)
    {
        var width = a.Length;
        var x = 0;
        if (Vector.IsHardwareAccelerated && BitConverter.IsLittleEndian && width >= Vector<float>.Count)
        {
            var count = Vector<float>.Count;
            var words = MemoryMarshal.Cast<byte, uint>(rgba);
            var scale = new Vector<float>(255f);
            var half = new Vector<float>(0.5f);
            var max = new Vector<float>(255f);
            var low = new Vector<uint>(0xFF);
            for (; x <= width - count; x += count)
            {
                var vr = ToByteLanes(new Vector<float>(r[x..]), scale, half, max) & low;
                var vg = ToByteLanes(new Vector<float>(g[x..]), scale, half, max) & low;
                var vb = ToByteLanes(new Vector<float>(b[x..]), scale, half, max) & low;
                var va = ToByteLanes(new Vector<float>(a[x..]), scale, half, max) & low;
                var packed = vr | (vg << 8) | (vb << 16) | (va << 24);
                Vector.ConditionalSelect(Vector.Equals(va, Vector<uint>.Zero), Vector<uint>.Zero, packed).CopyTo(words[x..]);
            }
        }

        for (var target = x * 4; x < width; x++, target += 4)
        {
            var alpha = ToByte(a[x]);
            if (alpha == 0)
            {
                rgba.Slice(target, 4).Clear();
                continue;
            }

            rgba[target] = ToByte(r[x]);
            rgba[target + 1] = ToByte(g[x]);
            rgba[target + 2] = ToByte(b[x]);
            rgba[target + 3] = alpha;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector<uint> ToByteLanes(Vector<float> value, Vector<float> scale, Vector<float> half, Vector<float> max) =>
        Vector.AsVectorUInt32(Vector.ConvertToInt32(Vector.Min(Vector.Max((value * scale) + half, Vector<float>.Zero), max)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte ToByte(float value)
    {
        var v = (value * 255f) + 0.5f;
        return v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)v;
    }

    /// <summary>SIMD conversion of [0,1] floats to rounded, clamped bytes.</summary>
    public static void UnitFloatToBytes(ReadOnlySpan<float> source, Span<byte> destination)
    {
        var i = 0;
        if (Vector.IsHardwareAccelerated && source.Length >= Vector<float>.Count)
        {
            var count = Vector<float>.Count;
            var scale = new Vector<float>(255f);
            var half = new Vector<float>(0.5f);
            var zero = Vector<float>.Zero;
            var max = new Vector<float>(255f);
            Span<int> lanes = stackalloc int[count];
            for (; i <= source.Length - count; i += count)
            {
                var v = Vector.Min(Vector.Max((new Vector<float>(source[i..]) * scale) + half, zero), max);
                Vector.ConvertToInt32(v).CopyTo(lanes);
                for (var lane = 0; lane < count; lane++)
                {
                    destination[i + lane] = (byte)lanes[lane];
                }
            }
        }

        for (; i < source.Length; i++)
        {
            var v = (source[i] * 255f) + 0.5f;
            destination[i] = v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)v;
        }
    }
}
