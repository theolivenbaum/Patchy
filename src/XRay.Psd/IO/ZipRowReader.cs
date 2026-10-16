using System.IO.Compression;

namespace XRay.Psd.IO;

/// <summary>
/// One zlib stream read forward in pieces: band rendering asks for consecutive row
/// ranges of a ZIP channel, and this keeps the inflater between them instead of
/// inflating from the start for every band. Asking for bytes before the current
/// position restarts the stream. Damage behaves as in <see cref="ChannelCodec.Inflate"/>:
/// the bytes before it are kept and everything after it reads as zeros.
/// </summary>
internal sealed class ZipRowReader : IDisposable
{
    private Stream? _input;
    private ZLibStream? _zlib;
    private ByteRange _range;
    private long _position;
    private bool _damaged;
    private byte[]? _scratch;

    /// <summary>Fills <paramref name="destination"/> with the inflated bytes starting at output offset <paramref name="offset"/>.</summary>
    public void Read(ByteRange range, long offset, Span<byte> destination)
    {
        if (_zlib is null || offset < _position || !SameRange(range))
        {
            Open(range);
        }

        destination.Clear();
        if (_damaged)
        {
            return;
        }

        try
        {
            _scratch ??= new byte[1 << 16];
            while (_position < offset)
            {
                var read = _zlib!.Read(_scratch.AsSpan(0, (int)Math.Min(_scratch.Length, offset - _position)));
                if (read == 0)
                {
                    _damaged = true;
                    return;
                }

                _position += read;
            }

            var total = 0;
            while (total < destination.Length)
            {
                var read = _zlib!.Read(destination[total..]);
                if (read == 0)
                {
                    _damaged = true;
                    break;
                }

                total += read;
            }

            _position += total;
        }
        catch (InvalidDataException)
        {
            // Keep what inflated before the damage; later reads are zeros.
            _damaged = true;
        }
    }

    public void Dispose()
    {
        _zlib?.Dispose();
        _input?.Dispose();
        _zlib = null;
        _input = null;
    }

    private bool SameRange(ByteRange range) => range.Length == _range.Length && range.Equals(_range);

    private void Open(ByteRange range)
    {
        Dispose();
        _range = range;
        _input = range.OpenRead();
        _zlib = new ZLibStream(_input, CompressionMode.Decompress);
        _position = 0;
        _damaged = false;
    }
}
