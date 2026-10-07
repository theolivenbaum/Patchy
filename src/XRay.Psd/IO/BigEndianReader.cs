using System.Buffers.Binary;
using System.Text;

namespace XRay.Psd.IO;

/// <summary>
/// Bounds-checked big-endian cursor over an in-memory PSD buffer. Every read
/// throws <see cref="PsdFormatException"/> on truncation, so a damaged length
/// never turns into an out-of-range slice or a huge allocation.
/// </summary>
internal sealed class BigEndianReader
{
    private readonly ReadOnlyMemory<byte> _data;

    public BigEndianReader(ReadOnlyMemory<byte> data)
    {
        _data = data;
    }

    public int Position { get; set; }

    public int Length => _data.Length;

    public int Remaining => _data.Length - Position;

    public ReadOnlyMemory<byte> Memory => _data;

    private ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || count > Remaining)
        {
            throw new PsdFormatException($"Unexpected end of data at offset {Position} (needed {count} bytes, {Remaining} left).");
        }

        var span = _data.Span.Slice(Position, count);
        Position += count;
        return span;
    }

    public byte ReadByte() => Take(1)[0];

    public sbyte ReadSByte() => unchecked((sbyte)Take(1)[0]);

    public ushort ReadUInt16() => BinaryPrimitives.ReadUInt16BigEndian(Take(2));

    public short ReadInt16() => BinaryPrimitives.ReadInt16BigEndian(Take(2));

    public uint ReadUInt32() => BinaryPrimitives.ReadUInt32BigEndian(Take(4));

    public int ReadInt32() => BinaryPrimitives.ReadInt32BigEndian(Take(4));

    public ulong ReadUInt64() => BinaryPrimitives.ReadUInt64BigEndian(Take(8));

    public long ReadInt64() => BinaryPrimitives.ReadInt64BigEndian(Take(8));

    public double ReadDouble() => BinaryPrimitives.ReadDoubleBigEndian(Take(8));

    public float ReadSingle() => BinaryPrimitives.ReadSingleBigEndian(Take(4));

    /// <summary>Reads a 16.16 fixed-point number.</summary>
    public double ReadFixed16() => ReadInt32() / 65536.0;

    /// <summary>Reads a four-character code such as <c>8BIM</c> or <c>Nrml</c>.</summary>
    public string ReadSignature()
    {
        return Encoding.Latin1.GetString(Take(4));
    }

    public ReadOnlyMemory<byte> ReadMemory(int count)
    {
        if (count < 0 || count > Remaining)
        {
            throw new PsdFormatException($"Unexpected end of data at offset {Position} (needed {count} bytes, {Remaining} left).");
        }

        var memory = _data.Slice(Position, count);
        Position += count;
        return memory;
    }

    public ReadOnlyMemory<byte> ReadMemory(long count)
    {
        if (count < 0 || count > Remaining)
        {
            throw new PsdFormatException($"Unexpected end of data at offset {Position} (needed {count} bytes, {Remaining} left).");
        }

        return ReadMemory((int)count);
    }

    public ReadOnlySpan<byte> ReadSpan(int count) => Take(count);

    public void Skip(long count)
    {
        if (count < 0 || count > Remaining)
        {
            throw new PsdFormatException($"Cannot skip {count} bytes at offset {Position} ({Remaining} left).");
        }

        Position += (int)count;
    }

    /// <summary>Moves to <paramref name="position"/>, clamped to the buffer end.</summary>
    public void SeekClamped(long position) => Position = (int)Math.Clamp(position, 0, Length);

    /// <summary>Creates a reader over the next <paramref name="count"/> bytes and advances past them.</summary>
    public BigEndianReader Slice(long count) => new(ReadMemory(count));

    /// <summary>
    /// Reads a Pascal string (length byte plus bytes), padding the total consumed
    /// size to a multiple of <paramref name="padding"/>. Text is decoded as
    /// Windows-1252 compatible Latin-1, which is what Photoshop writes on Windows;
    /// layers carry a separate Unicode name in <c>luni</c> that callers should prefer.
    /// </summary>
    public string ReadPascalString(int padding = 2)
    {
        var start = Position;
        var length = ReadByte();
        var text = Encoding.Latin1.GetString(Take(length));
        var consumed = Position - start;
        var padded = padding <= 1 ? consumed : (consumed + padding - 1) / padding * padding;
        Skip(Math.Min(padded - consumed, Remaining));
        return text;
    }

    /// <summary>Reads a u32 code-unit count followed by UTF-16BE text (descriptor and resource strings).</summary>
    public string ReadUnicodeString()
    {
        var count = ReadUInt32();
        if (count > (uint)(Remaining / 2))
        {
            throw new PsdFormatException($"Unicode string length {count} exceeds the remaining data.");
        }

        var bytes = Take((int)count * 2);
        var text = Encoding.BigEndianUnicode.GetString(bytes);
        return text.TrimEnd('\0');
    }

    /// <summary>Reads a section length that is u64 in PSB files and u32 in PSD files.</summary>
    public long ReadLength(bool large)
    {
        var value = large ? ReadUInt64() : ReadUInt32();
        if (value > (ulong)Remaining)
        {
            throw new PsdFormatException($"Section length {value} at offset {Position} exceeds the remaining {Remaining} bytes.");
        }

        return (long)value;
    }
}
