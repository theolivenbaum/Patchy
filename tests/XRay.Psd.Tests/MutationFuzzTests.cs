using System.Buffers.Binary;
using XRay.Psd.Rendering;
using XRay.Psd.Tests.Support;

namespace XRay.Psd.Tests;

/// <summary>
/// Dependency-free randomized robustness test: mutates the committed fixtures
/// (bit flips, truncations, corrupted length fields) with deterministic
/// splitmix64 seeds and checks that only <see cref="PsdFormatException"/>
/// escapes <see cref="PsdDocument.Load(ReadOnlyMemory{byte}, PsdLoadOptions?)"/>,
/// <see cref="PsdDocument.Render"/> and <see cref="PsdDocument.ExtractText"/>.
/// Set XRAY_PSD_MUTATIONS to run every fixture with that many iterations locally;
/// the SharpFuzz harness in fuzz/XRay.Psd.Fuzz covers coverage-guided runs.
/// </summary>
public sealed class MutationFuzzTests
{
    private const int DefaultIterations = 6;

    // Small fixtures keep the default run to a few seconds; a deep run
    // (XRAY_PSD_MUTATIONS set) takes every fixture.
    private const long MaxFixtureBytes = 96 * 1024;

    private static readonly uint[] InterestingValues =
    [
        0, 1, 2, 3, 4, 0x7F, 0x80, 0xFF, 0x100, 0x7FFF, 0x8000, 0xFFFF, 0x10000,
        0x7FFFFFFF, 0x80000000, 0xFFFFFFFE, 0xFFFFFFFF,
    ];

    public static TheoryData<string> SmallFixtures()
    {
        var data = new TheoryData<string>();
        var deep = Environment.GetEnvironmentVariable("XRAY_PSD_MUTATIONS") is { Length: > 0 };
        foreach (var name in Fixtures.AllDocuments())
        {
            if (deep || new FileInfo(Fixtures.PathOf(name)).Length <= MaxFixtureBytes)
            {
                data.Add(name);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(SmallFixtures))]
    public void Mutated_fixtures_only_throw_format_errors(string name)
    {
        var original = File.ReadAllBytes(Fixtures.PathOf(name));
        var iterations = int.TryParse(Environment.GetEnvironmentVariable("XRAY_PSD_MUTATIONS"), out var n) && n > 0 ? n : DefaultIterations;
        var nameSeed = StableHash(name);
        var regions = StructuredRegions(original);
        var failures = new List<string>();
        for (var i = 0; i < iterations; i++)
        {
            var seed = nameSeed + (ulong)i;
            var (bytes, description) = Mutate(original, seed, regions);
            var failure = Exercise(bytes, original.Length);
            if (failure is not null)
            {
                failures.Add($"seed {seed} ({description}): {failure}");
            }
        }

        Assert.True(failures.Count == 0, $"{name}:\n{string.Join('\n', failures.Take(5))}");
    }

    [Fact]
    public void Splitmix64_matches_the_reference_sequence()
    {
        // First outputs for seed 0 from the published splitmix64 reference implementation.
        var rng = new SplitMix64(0);
        Assert.Equal(0xE220A8397B1DCDAFUL, rng.Next());
        Assert.Equal(0x6E789E6AA1B965F4UL, rng.Next());
        Assert.Equal(0x06C45D188009454FUL, rng.Next());
    }

    /// <summary>Applies one to three mutations chosen by <paramref name="seed"/>.</summary>
    internal static (byte[] Bytes, string Description) Mutate(byte[] original, ulong seed, IReadOnlyList<(int Start, int Length)> regions)
    {
        var rng = new SplitMix64(seed);
        var bytes = (byte[])original.Clone();
        var steps = new List<string>();

        // Most of a file is pixel data, which the codecs already treat as opaque.
        // Three times in four, aim at a resource or tagged-block payload instead.
        int PickOffset(ref SplitMix64 random, int size)
        {
            if (regions.Count > 0 && random.Below(4) != 0)
            {
                var (start, length) = regions[(int)random.Below((ulong)regions.Count)];
                var offset = start + (int)random.Below((ulong)Math.Max(1, length - size + 1));
                if (offset + size <= bytes.Length)
                {
                    return offset;
                }
            }

            return (int)random.Below((ulong)Math.Max(1, bytes.Length - size + 1));
        }

        var count = 1 + (int)rng.Below(3);
        for (var step = 0; step < count && bytes.Length > 0; step++)
        {
            switch (rng.Below(5))
            {
                case 0:
                    {
                        var flips = 1 + (int)rng.Below(8);
                        for (var f = 0; f < flips; f++)
                        {
                            var offset = PickOffset(ref rng, 1);
                            bytes[offset] ^= (byte)(1 << (int)rng.Below(8));
                        }

                        steps.Add($"flip {flips} bits");
                        break;
                    }

                case 1:
                    {
                        var length = (int)rng.Below((ulong)bytes.Length);
                        bytes = bytes[..length];
                        steps.Add($"truncate to {length}");
                        break;
                    }

                case 2:
                    {
                        // Overwrite a 16- or 32-bit field with a boundary value.
                        var value = InterestingValues[rng.Below((ulong)InterestingValues.Length)];
                        var wide = rng.Below(2) == 0;
                        var size = wide ? 4 : 2;
                        if (bytes.Length >= size)
                        {
                            var offset = PickOffset(ref rng, size);
                            if (wide)
                            {
                                BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset), value);
                            }
                            else
                            {
                                BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset), (ushort)value);
                            }

                            steps.Add($"set u{size * 8} at {offset} to 0x{value:X}");
                        }

                        break;
                    }

                case 3:
                    {
                        // Corrupt one of the section or record length fields the parser follows.
                        var lengths = LengthFieldOffsets(bytes);
                        if (lengths.Count > 0)
                        {
                            var offset = lengths[(int)rng.Below((ulong)lengths.Count)];
                            var current = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset));
                            var value = rng.Below(3) switch
                            {
                                0 => InterestingValues[rng.Below((ulong)InterestingValues.Length)],
                                1 => current + (uint)rng.Below(16) + 1,
                                _ => current - (uint)Math.Min(current, rng.Below(16) + 1),
                            };
                            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset), value);
                            steps.Add($"length at {offset}: {current} -> {value}");
                        }

                        break;
                    }

                default:
                    {
                        // Overwrite a short run with random bytes.
                        var length = 1 + (int)rng.Below(16);
                        var offset = PickOffset(ref rng, 1);
                        for (var k = offset; k < Math.Min(bytes.Length, offset + length); k++)
                        {
                            bytes[k] = (byte)rng.Next();
                        }

                        steps.Add($"randomize {length} bytes at {offset}");
                        break;
                    }
            }
        }

        return (bytes, string.Join(", ", steps));
    }

    /// <summary>
    /// Byte ranges of the original file that hold structure rather than pixels:
    /// image resource payloads, layer and global tagged blocks, layer mask
    /// records and blending ranges. Found by loading the fixture and mapping the
    /// payload slices back to file offsets.
    /// </summary>
    internal static List<(int Start, int Length)> StructuredRegions(byte[] original)
    {
        var regions = new List<(int, int)>();
        void Add(ReadOnlyMemory<byte> memory)
        {
            if (memory.Length > 0 && System.Runtime.InteropServices.MemoryMarshal.TryGetArray(memory, out var segment) && ReferenceEquals(segment.Array, original))
            {
                regions.Add((segment.Offset, segment.Count));
            }
        }

        var document = PsdDocument.Load(original);
        Add(document.ColorModeData);
        foreach (var resource in document.ImageResources)
        {
            Add(resource.Data);
        }

        foreach (var block in document.GlobalTaggedBlocks)
        {
            Add(block.Data);
        }

        foreach (var layer in document.Layers)
        {
            Add(layer.BlendingRanges);
            foreach (var block in layer.TaggedBlocks)
            {
                Add(block.Data);
            }
        }

        return regions;
    }

    /// <summary>
    /// Offsets of the length fields along the top-level structure: the section
    /// lengths, each image resource size and the layer info length. Best effort;
    /// it stops at the first inconsistency.
    /// </summary>
    internal static List<int> LengthFieldOffsets(byte[] bytes)
    {
        var offsets = new List<int>();
        if (bytes.Length < 30)
        {
            return offsets;
        }

        var large = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(4)) == 2;
        var position = 26;
        offsets.Add(position);
        position += 4 + (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(position)), int.MaxValue / 2);
        if (position + 4 > bytes.Length)
        {
            return offsets;
        }

        offsets.Add(position);
        var resourcesEnd = (long)position + 4 + BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(position));
        var cursor = position + 4;
        while (cursor + 12 <= Math.Min(resourcesEnd, bytes.Length))
        {
            var nameLength = bytes[cursor + 6];
            var sizeOffset = cursor + 6 + ((nameLength + 2) & ~1);
            if (sizeOffset + 4 > bytes.Length)
            {
                break;
            }

            offsets.Add(sizeOffset);
            var size = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(sizeOffset));
            cursor = (int)Math.Min(int.MaxValue / 2, (long)sizeOffset + 4 + ((size + 1) & ~1u));
        }

        if (resourcesEnd + 8 <= bytes.Length)
        {
            var layerInfo = (int)resourcesEnd;
            offsets.Add(large ? layerInfo + 4 : layerInfo);
            var inner = layerInfo + (large ? 8 : 4);
            if (inner + 8 <= bytes.Length)
            {
                offsets.Add(large ? inner + 4 : inner);
            }
        }

        return offsets;
    }

    /// <summary>Loads, renders and extracts text; returns a description of any non-format exception.</summary>
    internal static string? Exercise(byte[] bytes, int originalLength)
    {
        try
        {
            var document = PsdDocument.Load(bytes);
            _ = document.ExtractText();
            TouchResources(document);
            foreach (var layer in document.Layers)
            {
                _ = layer.Style?.Effects.Count;
            }

            // Header mutations can describe enormous canvases; rendering those only
            // measures allocation speed, not parser robustness.
            if ((long)document.Width * document.Height <= Math.Max(1L << 20, originalLength * 4L))
            {
                _ = document.Render();
                _ = document.Render(new RenderOptions { Source = RenderSource.Layers });
            }
        }
        catch (PsdFormatException)
        {
            // Rejecting damaged input is the expected outcome.
        }
        catch (Exception ex)
        {
            return $"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}";
        }

        return null;
    }

    private static void TouchResources(PsdDocument document)
    {
        var resources = document.Resources;
        _ = resources.Resolution;
        _ = resources.GridAndGuides;
        _ = resources.Thumbnail;
        _ = resources.Slices;
        _ = resources.LayerComps;
        _ = resources.PrintScale;
        _ = resources.PixelAspectRatio;
        _ = resources.VersionInfo;
        _ = resources.IccProfile;
        _ = resources.Xmp;
        _ = resources.ChannelNames;
        foreach (var dataSet in resources.Iptc)
        {
            _ = dataSet.Text;
        }
    }

    private static ulong StableHash(string text)
    {
        var hash = 1469598103934665603UL; // FNV-1a
        foreach (var c in text)
        {
            hash = (hash ^ c) * 1099511628211UL;
        }

        return hash;
    }

    /// <summary>The splitmix64 generator (Steele, Lea and Flood), as used for Dissolve.</summary>
    internal struct SplitMix64(ulong state)
    {
        private ulong _state = state;

        public ulong Next()
        {
            var z = _state += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        public ulong Below(ulong bound) => bound == 0 ? 0 : Next() % bound;
    }
}
