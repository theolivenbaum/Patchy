namespace XRay.Psd.IO;

/// <summary>
/// A run of file bytes addressed with 64-bit offsets: either one in-memory span, or a
/// range of a <see cref="PsdSource"/> that may be longer than one span can address (a
/// layer channel or merged image of a PSB past 2 GB). Readers take windows of it, so
/// nothing ever needs the whole range as one span.
/// </summary>
internal readonly struct ByteRange
{
    private readonly ReadOnlyMemory<byte> _memory;
    private readonly PsdSource? _source;
    private readonly long _offset;

    public ByteRange(ReadOnlyMemory<byte> memory)
    {
        _memory = memory;
        _source = null;
        _offset = 0;
        Length = memory.Length;
    }

    public ByteRange(PsdSource source, long offset, long length)
    {
        _memory = default;
        _source = source;
        _offset = offset;
        Length = length;
    }

    public long Length { get; }

    /// <summary>Whether the range is one in-memory span (<see cref="Memory"/> holds all of it).</summary>
    public bool IsContiguous => _source is null;

    /// <summary>The bytes as one span; only for <see cref="IsContiguous"/> ranges.</summary>
    public ReadOnlyMemory<byte> Memory => _source is null ? _memory : throw new InvalidOperationException("The range is not contiguous.");

    /// <summary>
    /// The bytes at [<paramref name="offset"/>, <paramref name="offset"/> + <paramref name="length"/>),
    /// cut to the end of the range (an offset past the end gives an empty window).
    /// </summary>
    public ReadOnlyMemory<byte> Window(long offset, int length)
    {
        if (offset < 0 || length <= 0 || offset >= Length)
        {
            return ReadOnlyMemory<byte>.Empty;
        }

        var count = (int)Math.Min(length, Length - offset);
        return _source is null ? _memory.Slice((int)offset, count) : _source.Slice(_offset + offset, count);
    }

    /// <summary>A sub-range from <paramref name="offset"/> to the end.</summary>
    public ByteRange From(long offset)
    {
        offset = Math.Clamp(offset, 0, Length);
        return _source is null ? new ByteRange(_memory[(int)offset..]) : new ByteRange(_source, _offset + offset, Length - offset);
    }

    /// <summary>A sub-range of at most <paramref name="length"/> bytes from <paramref name="offset"/>.</summary>
    public ByteRange Slice(long offset, long length)
    {
        offset = Math.Clamp(offset, 0, Length);
        length = Math.Clamp(length, 0, Length - offset);
        return _source is null ? new ByteRange(_memory.Slice((int)offset, (int)length)) : new ByteRange(_source, _offset + offset, length);
    }

    /// <summary>A read-only stream over the range, reading it window by window.</summary>
    public Stream OpenRead() => new RangeStream(this);

    /// <summary>Forward-only reads in windows of at most 1 GiB.</summary>
    private sealed class RangeStream(ByteRange range) : Stream
    {
        private const int Chunk = 1 << 30;

        private long _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => range.Length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var window = range.Window(_position, Math.Min(buffer.Length, Chunk));
            window.Span.CopyTo(buffer);
            _position += window.Length;
            return window.Length;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
