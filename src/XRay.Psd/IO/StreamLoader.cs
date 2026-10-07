using System.Buffers;

namespace XRay.Psd.IO;

/// <summary>
/// Reads a stream into one exactly sized array. A seekable stream is read straight
/// into the array (no intermediate copy); other streams fill pooled chunks that are
/// copied once into the result.
/// </summary>
internal static class StreamLoader
{
    private const int ChunkSize = 1 << 20;

    public static byte[] ReadAll(Stream stream)
    {
        if (RemainingLength(stream) is { } remaining)
        {
            var data = new byte[remaining];
            var read = stream.ReadAtLeast(data, data.Length, throwOnEndOfStream: false);
            return read == data.Length ? data : data[..read];
        }

        var chunks = new List<byte[]>();
        try
        {
            long total = 0;
            while (true)
            {
                var chunk = ArrayPool<byte>.Shared.Rent(ChunkSize);
                chunks.Add(chunk);
                var read = stream.ReadAtLeast(chunk.AsSpan(0, ChunkSize), ChunkSize, throwOnEndOfStream: false);
                total += read;
                CheckSize(total);
                if (read < ChunkSize)
                {
                    return Concatenate(chunks, total);
                }
            }
        }
        finally
        {
            Return(chunks);
        }
    }

    public static async Task<byte[]> ReadAllAsync(Stream stream, CancellationToken cancellationToken)
    {
        if (RemainingLength(stream) is { } remaining)
        {
            var data = new byte[remaining];
            var read = await stream.ReadAtLeastAsync(data, data.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
            return read == data.Length ? data : data[..read];
        }

        var chunks = new List<byte[]>();
        try
        {
            long total = 0;
            while (true)
            {
                var chunk = ArrayPool<byte>.Shared.Rent(ChunkSize);
                chunks.Add(chunk);
                var read = await stream.ReadAtLeastAsync(chunk.AsMemory(0, ChunkSize), ChunkSize, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
                total += read;
                CheckSize(total);
                if (read < ChunkSize)
                {
                    return Concatenate(chunks, total);
                }
            }
        }
        finally
        {
            Return(chunks);
        }
    }

    private static int? RemainingLength(Stream stream)
    {
        if (!stream.CanSeek)
        {
            return null;
        }

        var remaining = Math.Max(0, stream.Length - stream.Position);
        CheckSize(remaining);
        return (int)remaining;
    }

    private static void CheckSize(long length)
    {
        if (length > Array.MaxLength)
        {
            throw new PsdFormatException("The stream is too large to load into memory (2 GB or more); load the file from a path, which memory-maps it.");
        }
    }

    private static byte[] Concatenate(List<byte[]> chunks, long total)
    {
        var result = new byte[total];
        var offset = 0;
        foreach (var chunk in chunks)
        {
            var count = (int)Math.Min(ChunkSize, total - offset);
            chunk.AsSpan(0, count).CopyTo(result.AsSpan(offset));
            offset += count;
        }

        return result;
    }

    private static void Return(List<byte[]> chunks)
    {
        foreach (var chunk in chunks)
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }
    }
}
