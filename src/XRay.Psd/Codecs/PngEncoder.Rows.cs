using System.Buffers.Binary;
using XRay.Psd.Imaging;

namespace XRay.Psd.Codecs;

public static partial class PngEncoder
{
    /// <summary>
    /// Writes a PNG from consecutive bands of rows, holding only the rows of the deflate
    /// segments not yet written. The zlib stream is the one <see cref="Encode"/> produces
    /// for the same pixels and alpha choice (same filters, same segments, same checksum),
    /// split into one IDAT chunk per deflate segment, so the decoded image is identical and
    /// the bytes do not depend on the band height or the degree of parallelism.
    /// </summary>
    internal sealed class RowWriter
    {
        private readonly Stream _output;
        private readonly int _width;
        private readonly int _height;
        private readonly bool _withAlpha;
        private readonly int _channels;
        private readonly int _stride;
        private readonly int _filteredStride;
        private readonly PngCompressionLevel _level;
        private readonly long _filteredLength;
        private readonly bool _single;
        private readonly int _segmentBytes;
        private readonly int _segmentCount;
        private readonly byte[] _previous;
        private byte[] _pending = [];
        private int _pendingLength;
        private int _rows;
        private int _nextSegment;
        private uint _adler;

        public RowWriter(Stream output, int width, int height, bool withAlpha, PngCompressionLevel level)
        {
            ArgumentNullException.ThrowIfNull(output);
            _output = output;
            _width = width;
            _height = height;
            _withAlpha = withAlpha;
            _channels = withAlpha ? 4 : 3;
            _stride = width * _channels;
            _filteredStride = _stride + 1;
            _level = level;
            _filteredLength = (long)_filteredStride * height;
            _single = IsSingleStream(_filteredLength);
            _segmentBytes = SegmentBytes(_filteredStride);
            _segmentCount = (int)((_filteredLength + _segmentBytes - 1) / _segmentBytes);
            _previous = new byte[_stride];
            WriteHeader(output, width, height, withAlpha);
        }

        /// <summary>Appends the rows of <paramref name="band"/> (the image's full width), top to bottom.</summary>
        public void Write(RgbaImage band)
        {
            ArgumentNullException.ThrowIfNull(band);
            if (band.Width != _width || _rows + band.Height > _height)
            {
                throw new ArgumentException("The band does not continue the image.", nameof(band));
            }

            var rows = band.Height;
            if (rows == 0)
            {
                return;
            }

            Reserve(_pendingLength + ((long)rows * _filteredStride));
            var pending = _pending;
            var at = _pendingLength;
            var carried = _previous;

            // Rows filter in parallel: each strip packs the raw row above its first row.
            Parallelism.For(rows, _stride * 5L, (start, end) =>
            {
                var previous = new byte[_stride];
                var current = new byte[_stride];
                var candidate = new byte[_filteredStride];
                if (start > 0)
                {
                    PackRow(band, start - 1, _withAlpha, previous);
                }
                else
                {
                    carried.CopyTo(previous, 0);
                }

                for (var y = start; y < end; y++)
                {
                    PackRow(band, y, _withAlpha, current);
                    FilterRow(current, previous, _channels, candidate, pending.AsSpan(at + (y * _filteredStride), _filteredStride));
                    (previous, current) = (current, previous);
                }
            });

            PackRow(band, rows - 1, _withAlpha, _previous);
            _pendingLength += rows * _filteredStride;
            _rows += rows;
            if (!_single)
            {
                FlushSegments(final: false);
            }
        }

        /// <summary>Writes the rest of the stream and the IEND chunk; every row must have been written.</summary>
        public void Finish()
        {
            if (_rows != _height)
            {
                throw new InvalidOperationException($"{_rows} of {_height} rows were written.");
            }

            if (_single)
            {
                WriteChunk(_output, "IDAT", CompressSingle(_pending.AsSpan(0, _pendingLength), _level));
            }
            else
            {
                FlushSegments(final: true);
            }

            WriteChunk(_output, "IEND", []);
        }

        /// <summary>
        /// Deflates the complete segments held (in parallel) and writes one IDAT chunk per
        /// segment; the last segment waits for <paramref name="final"/>. The first chunk starts
        /// with the zlib header, the last ends with the Adler-32 of all filtered bytes.
        /// </summary>
        private void FlushSegments(bool final)
        {
            var available = _pendingLength / _segmentBytes;
            if (final && _pendingLength % _segmentBytes != 0)
            {
                available++;
            }

            var count = Math.Min(available, _segmentCount - _nextSegment - (final ? 0 : 1));

            // Segments deflate in parallel, so wait for a batch of them; the bytes do not depend on it.
            if (count <= 0 || (!final && count < Parallelism.MaxDegree))
            {
                return;
            }

            var first = _nextSegment;
            var pending = _pending;
            var pendingLength = _pendingLength;
            var segments = new byte[count][];
            var checksums = new uint[count];
            Parallelism.For(count, _segmentBytes, (start, end) =>
            {
                for (var i = start; i < end; i++)
                {
                    var offset = i * _segmentBytes;
                    var data = pending.AsSpan(offset, Math.Min(_segmentBytes, pendingLength - offset));
                    checksums[i] = Adler32.Compute(data);
                    segments[i] = DeflateSegment(data, first + i == _segmentCount - 1, _level);
                }
            });

            for (var i = 0; i < count; i++)
            {
                var segment = first + i;
                var length = Math.Min(_segmentBytes, pendingLength - (i * _segmentBytes));
                _adler = segment == 0 ? checksums[i] : Adler32.Combine(_adler, checksums[i], length);
                var head = segment == 0 ? 2 : 0;
                var tail = segment == _segmentCount - 1 ? 4 : 0;
                var chunk = new byte[head + segments[i].Length + tail];
                if (head > 0)
                {
                    chunk[0] = 0x78;
                    chunk[1] = ZlibFlags(_level);
                }

                segments[i].CopyTo(chunk, head);
                if (tail > 0)
                {
                    BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(chunk.Length - 4), _adler);
                }

                WriteChunk(_output, "IDAT", chunk);
            }

            var consumed = Math.Min(pendingLength, count * _segmentBytes);
            _pending.AsSpan(consumed, pendingLength - consumed).CopyTo(_pending);
            _pendingLength -= consumed;
            _nextSegment += count;
        }

        private void Reserve(long length)
        {
            if (length <= _pending.Length)
            {
                return;
            }

            var grown = new byte[checked((int)Math.Max(length, Math.Min(Array.MaxLength, (long)_pending.Length * 2)))];
            _pending.AsSpan(0, _pendingLength).CopyTo(grown);
            _pending = grown;
        }
    }
}
