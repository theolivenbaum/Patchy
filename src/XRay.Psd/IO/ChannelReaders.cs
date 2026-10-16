using System.Buffers.Binary;
using System.Collections.Concurrent;
using XRay.Psd.Layers;

namespace XRay.Psd.IO;

/// <summary>
/// The open ZIP streams of one band render: each ZIP-compressed channel keeps its
/// inflater from band to band, so a document rendered top to bottom inflates every
/// channel once. Raw and RLE channels need no state (their rows are addressable).
/// Channels decode on different threads, but each channel's stream is used by one at a time.
/// </summary>
internal sealed class ChannelReaders : IDisposable
{
    private readonly ConcurrentDictionary<object, ZipRowReader> _readers = new(ReferenceEqualityComparer.Instance);

    /// <summary>The stream of a ZIP channel, or null for other compressions.</summary>
    public ZipRowReader? For(LayerChannel channel) => IsZip(channel.Bytes) ? _readers.GetOrAdd(channel, static _ => new ZipRowReader()) : null;

    public void Dispose()
    {
        foreach (var reader in _readers.Values)
        {
            reader.Dispose();
        }

        _readers.Clear();
    }

    private static bool IsZip(ByteRange payload)
    {
        if (payload.Length < 2)
        {
            return false;
        }

        var compression = (PsdCompression)BinaryPrimitives.ReadUInt16BigEndian(payload.Window(0, 2).Span);
        return compression is PsdCompression.Zip or PsdCompression.ZipPrediction;
    }
}
