using System.Security.Cryptography;
using XRay.Psd.IO;
using XRay.Psd.Rendering;
using XRay.Psd.Tests.Support;

namespace XRay.Psd.Tests;

/// <summary>Stream, async and memory-mapped loading, and the windowed (64-bit offset) parse path.</summary>
public sealed class LoadingTests
{
    public static TheoryData<string> AllFixtures() => ParsingTests.AllFixtures();

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void Windowed_parse_matches_the_in_memory_parse(string name)
    {
        var bytes = File.ReadAllBytes(Fixtures.PathOf(name));
        var options = new PsdLoadOptions();
        var expected = PsdDocument.Load(bytes);

        // Threshold 0 sends every file down the path PSB files past 2 GB take.
        var windowed = PsdParser.Parse(new MemorySource(bytes), options, windowedAbove: 0);

        Assert.Equal(Describe(expected), Describe(windowed));
        Assert.Equal(Hash(expected.Render(new RenderOptions { Source = RenderSource.Layers })), Hash(windowed.Render(new RenderOptions { Source = RenderSource.Layers })));
        Assert.Equal(expected.ExtractText().ToString(), windowed.ExtractText().ToString());
    }

    [Theory]
    [InlineData("arrows.psd")]
    [InlineData("photoshop-basic.psb")]
    [InlineData("qual_rca_pinout.psd")]
    public void Memory_mapped_documents_match_and_release_on_dispose(string name)
    {
        var path = Fixtures.PathOf(name);
        var expected = PsdDocument.Load(File.ReadAllBytes(path));
        var mapped = PsdDocument.Load(path, new PsdLoadOptions { MemoryMap = true });
        Assert.NotNull(mapped.Source);
        Assert.Equal(Describe(expected), Describe(mapped));
        Assert.Equal(Hash(expected.Render()), Hash(mapped.Render()));
        Assert.Equal(Hash(expected.GetMergedImage()), Hash(mapped.GetMergedImage()));

        // The windowed path over a mapping (what a PSB past 2 GB uses).
        using (var source = MappedFileSource.Open(path))
        {
            var windowed = PsdParser.Parse(source, new PsdLoadOptions(), windowedAbove: 0);
            Assert.Equal(Hash(expected.Render(new RenderOptions { Source = RenderSource.Layers })), Hash(windowed.Render(new RenderOptions { Source = RenderSource.Layers })));
        }

        mapped.Dispose();
        mapped.Dispose();
        Assert.Throws<ObjectDisposedException>(() => mapped.GetMergedImage());
    }

    [Fact]
    public async Task Async_and_stream_loads_match_the_byte_load()
    {
        var path = Fixtures.PathOf("arrows.psd");
        var bytes = File.ReadAllBytes(path);
        var expected = Describe(PsdDocument.Load(bytes));

        var fromPath = await PsdDocument.LoadAsync(path, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(expected, Describe(fromPath));

        var mapped = await PsdDocument.LoadAsync(path, new PsdLoadOptions { MemoryMap = true }, TestContext.Current.CancellationToken);
        Assert.Equal(expected, Describe(mapped));
        mapped.Dispose();

        await using (var seekable = new MemoryStream(bytes))
        {
            seekable.Position = 0;
            Assert.Equal(expected, Describe(await PsdDocument.LoadAsync(seekable, cancellationToken: TestContext.Current.CancellationToken)));
        }

        Assert.Equal(expected, Describe(await PsdDocument.LoadAsync(new ForwardOnlyStream(bytes), cancellationToken: TestContext.Current.CancellationToken)));
        Assert.Equal(expected, Describe(PsdDocument.Load(new ForwardOnlyStream(bytes))));

        // A seekable stream loads from its current position.
        var prefixed = new byte[bytes.Length + 7];
        bytes.CopyTo(prefixed, 7);
        using var offset = new MemoryStream(prefixed) { Position = 7 };
        Assert.Equal(expected, Describe(PsdDocument.Load(offset)));
    }

    [Fact]
    public async Task Async_load_honors_cancellation()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PsdDocument.LoadAsync(new ForwardOnlyStream(new byte[4 << 20]), cancellationToken: cancelled.Token));
    }

    [Fact]
    public void Non_seekable_streams_larger_than_one_chunk_load()
    {
        // Pad a real document past the 1 MiB chunk size; the merged image section absorbs the tail.
        var bytes = File.ReadAllBytes(Fixtures.PathOf("arrows.psd"));
        var padded = new byte[bytes.Length + (3 << 20)];
        bytes.CopyTo(padded, 0);
        var document = PsdDocument.Load(new ForwardOnlyStream(padded));
        Assert.Equal(Describe(PsdDocument.Load(padded)), Describe(document));
    }

    [Fact]
    public void Truncated_files_fail_cleanly_on_the_windowed_path()
    {
        foreach (var name in new[] { "arrows.psd", "photoshop-basic.psb", "photoshop-group-opacity.psd" })
        {
            var bytes = File.ReadAllBytes(Fixtures.PathOf(name));
            foreach (var fraction in new[] { 0.05, 0.3, 0.6, 0.95 })
            {
                var cut = bytes.AsMemory(0, (int)(bytes.Length * fraction));
                try
                {
                    var document = PsdParser.Parse(new MemorySource(cut), new PsdLoadOptions(), windowedAbove: 0);
                    _ = document.Render();
                    _ = document.ExtractText();
                }
                catch (PsdFormatException)
                {
                    // Rejecting a damaged file is fine; any other exception type is a bug.
                }
            }
        }
    }

    private static string Describe(PsdDocument document)
    {
        var lines = new List<string> { document.ToString(), $"merged {Hash(document.MergedImageData.Span)} alpha {document.MergedImageHasTransparency}" };
        foreach (var layer in document.Layers)
        {
            lines.Add($"{layer.Path} {layer.Kind} {layer.Bounds} {string.Join(',', layer.Channels.Select(c => $"{c.Id}:{Hash(c.Data.Span)}"))} {string.Join(',', layer.TaggedBlocks.Select(b => b.Key + b.Data.Length))}");
        }

        lines.Add(string.Join(',', document.GlobalTaggedBlocks.Select(b => b.Key + b.Data.Length)));
        lines.Add(string.Join(',', document.ImageResources.Select(r => r.Id + ":" + r.Data.Length)));
        return string.Join('\n', lines);
    }

    private static string Hash(Imaging.RgbaImage image) => Hash(image.Pixels);

    private static string Hash(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA256.HashData(data))[..12];

    /// <summary>A read-only stream that cannot seek and returns short reads.</summary>
    private sealed class ForwardOnlyStream(byte[] data) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var count = Math.Min(Math.Min(buffer.Length, 50_000), data.Length - _position);
            data.AsSpan(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
