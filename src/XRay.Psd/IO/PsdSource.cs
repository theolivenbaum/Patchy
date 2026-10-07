using System.Buffers;
using System.IO.MemoryMappedFiles;

namespace XRay.Psd.IO;

/// <summary>
/// The bytes of a PSD/PSB file, addressed with 64-bit offsets. The parser reads
/// through windows of at most <see cref="int.MaxValue"/> bytes, which is what lets a
/// memory-mapped PSB larger than 2 GB keep every layer channel addressable.
/// </summary>
internal abstract class PsdSource : IDisposable
{
    public abstract long Length { get; }

    /// <summary>A window of the file. The caller keeps <paramref name="offset"/> and <paramref name="length"/> inside <see cref="Length"/>.</summary>
    public abstract ReadOnlyMemory<byte> Slice(long offset, int length);

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
    }
}

/// <summary>A file already in memory.</summary>
internal sealed class MemorySource(ReadOnlyMemory<byte> data) : PsdSource
{
    public override long Length => data.Length;

    public override ReadOnlyMemory<byte> Slice(long offset, int length) => data.Slice((int)offset, length);
}

/// <summary>
/// A read-only memory-mapped file. Pages load on first touch, so parsing a large
/// document reads only the record headers, and each layer's channel data is paged
/// in when rendering decodes it. Windows handed out stay valid until the source is
/// disposed; after that, reading one throws <see cref="ObjectDisposedException"/>.
/// </summary>
internal sealed unsafe class MappedFileSource : PsdSource
{
    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;
    private readonly byte* _pointer;
    private int _disposed;

    private MappedFileSource(MemoryMappedFile file, MemoryMappedViewAccessor view, long length)
    {
        _file = file;
        _view = view;
        Length = length;
        byte* pointer = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
        _pointer = pointer + view.PointerOffset;
    }

    ~MappedFileSource()
    {
        Dispose(false);
    }

    public override long Length { get; }

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public static PsdSource Open(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.RandomAccess);
        var length = stream.Length;
        if (length == 0)
        {
            // An empty file cannot be mapped; the parser rejects it with a format error.
            return new MemorySource(ReadOnlyMemory<byte>.Empty);
        }

        var file = MemoryMappedFile.CreateFromFile(stream, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: false);
        try
        {
            var view = file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            return new MappedFileSource(file, view, length);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    public override ReadOnlyMemory<byte> Slice(long offset, int length)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (offset < 0 || length < 0 || offset > Length - length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        return new Window(this, _pointer + offset, length).Memory;
    }

    protected override void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        if (disposing)
        {
            _view.Dispose();
            _file.Dispose();
        }
    }

    /// <summary>One window over the mapped view; every span access checks that the mapping is still open.</summary>
    private sealed class Window(MappedFileSource owner, byte* pointer, int length) : MemoryManager<byte>
    {
        public override Span<byte> GetSpan()
        {
            ObjectDisposedException.ThrowIf(owner.IsDisposed, owner);
            return new Span<byte>(pointer, length);
        }

        public override MemoryHandle Pin(int elementIndex = 0)
        {
            ObjectDisposedException.ThrowIf(owner.IsDisposed, owner);
            return new MemoryHandle(pointer + elementIndex);
        }

        public override void Unpin()
        {
        }

        protected override void Dispose(bool disposing)
        {
        }
    }
}
