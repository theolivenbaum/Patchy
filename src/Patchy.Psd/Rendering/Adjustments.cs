using System.Buffers.Binary;
using System.Numerics;
using Patchy.Psd.Layers;

namespace Patchy.Psd.Rendering;

/// <summary>A color transform applied by an adjustment layer to the backdrop.</summary>
internal interface IAdjustment
{
    void Apply(Span<float> r, Span<float> g, Span<float> b);
}

/// <summary>Factory for the adjustment layers the renderer supports (see TODO.md for the rest).</summary>
internal static class Adjustments
{
    public static IAdjustment? Create(PsdLayer layer)
    {
        var block = layer.ContentKey is { } key ? layer.GetTaggedBlock(key) : null;
        return layer.ContentKey switch
        {
            "nvrt" => new InvertAdjustment(),
            "thrs" when block is { Data.Length: >= 2 } => new ThresholdAdjustment(Math.Clamp(BinaryPrimitives.ReadUInt16BigEndian(block.Data.Span), (ushort)1, (ushort)255)),
            "post" when block is { Data.Length: >= 2 } => new PosterizeAdjustment(Math.Clamp(BinaryPrimitives.ReadUInt16BigEndian(block.Data.Span), (ushort)2, (ushort)255)),
            _ => null,
        };
    }

    private sealed class InvertAdjustment : IAdjustment
    {
        public void Apply(Span<float> r, Span<float> g, Span<float> b)
        {
            Invert(r);
            Invert(g);
            Invert(b);
        }

        private static void Invert(Span<float> values)
        {
            var i = 0;
            var one = Vector<float>.One;
            for (; i <= values.Length - Vector<float>.Count; i += Vector<float>.Count)
            {
                (one - new Vector<float>(values[i..])).CopyTo(values[i..]);
            }

            for (; i < values.Length; i++)
            {
                values[i] = 1f - values[i];
            }
        }
    }

    /// <summary>Photoshop's Threshold: integer luma (30/59/11) against the level.</summary>
    private sealed class ThresholdAdjustment(int level) : IAdjustment
    {
        public void Apply(Span<float> r, Span<float> g, Span<float> b)
        {
            for (var i = 0; i < r.Length; i++)
            {
                var luma = ((Byte(r[i]) * 30) + (Byte(g[i]) * 59) + (Byte(b[i]) * 11)) / 100;
                var value = luma >= level ? 1f : 0f;
                r[i] = g[i] = b[i] = value;
            }
        }
    }

    /// <summary>Photoshop's Posterize: equal-width input buckets mapped to the truncated output ramp.</summary>
    private sealed class PosterizeAdjustment(int levels) : IAdjustment
    {
        public void Apply(Span<float> r, Span<float> g, Span<float> b)
        {
            Posterize(r);
            Posterize(g);
            Posterize(b);
        }

        private void Posterize(Span<float> values)
        {
            for (var i = 0; i < values.Length; i++)
            {
                var bucket = Byte(values[i]) * levels / 256;
                values[i] = bucket * 255 / (levels - 1) / 255f;
            }
        }
    }

    private static int Byte(float value) => (int)Math.Clamp(MathF.Round(value * 255f), 0f, 255f);
}
