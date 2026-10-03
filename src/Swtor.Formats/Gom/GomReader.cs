using System.Buffers.Binary;
using System.Text;

namespace Swtor.Formats.Gom;

/// <summary>
/// Reads the variable-length numbers and strings of the GOM data format.
/// A number starts with one byte. Below 0xC0 the byte is the value. 0xC8 to 0xCF mean that
/// (byte and 7) + 1 big-endian bytes follow. For signed numbers, 0xC0 to 0xC7 are the same
/// but negative, and 0xD0 is the smallest 64-bit value.
/// </summary>
public sealed class GomReader(byte[] data, int start, int end)
{
    private readonly byte[] _data = data;
    private readonly int _end = end;

    public int Position { get; set; } = start;

    public int Remaining => _end - Position;

    public bool AtEnd => Position >= _end;

    public byte ReadByte()
    {
        if (Position >= _end) throw new GameFormatException("Unexpected end of GOM data", Position);
        return _data[Position++];
    }

    /// <summary>Returns the next byte without reading it, or -1 at the end.</summary>
    public int PeekByte() => Position < _end ? _data[Position] : -1;

    public ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || count > _end - Position) throw new GameFormatException("Unexpected end of GOM data", Position);
        var span = _data.AsSpan(Position, count);
        Position += count;
        return span;
    }

    public float ReadSingle() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));

    public ulong ReadNumber()
    {
        int at = Position;
        byte b = ReadByte();
        if (b < 0xC0) return b;
        if (b < 0xC8 || b >= 0xD0) throw new GameFormatException($"Invalid number prefix 0x{b:X2}", at);
        return ReadBigEndian((b & 7) + 1);
    }

    public long ReadSigned()
    {
        int at = Position;
        byte b = ReadByte();
        if (b < 0xC0) return b;
        if (b == 0xD0) return long.MinValue;
        if (b > 0xD0) throw new GameFormatException($"Invalid signed number prefix 0x{b:X2}", at);
        ulong magnitude = ReadBigEndian((b & 7) + 1);
        return b < 0xC8 ? -unchecked((long)magnitude) : unchecked((long)magnitude);
    }

    /// <summary>Reads a signed number that must be a valid collection size.</summary>
    public int ReadCount()
    {
        int at = Position;
        long value = ReadSigned();
        if (value < 0 || value > int.MaxValue) throw new GameFormatException($"Invalid count {value}", at);
        return (int)value;
    }

    /// <summary>Reads a string with a count prefix (UTF-8, no terminator).</summary>
    public string ReadString() => Encoding.UTF8.GetString(Take(ReadCount()));

    private ulong ReadBigEndian(int length)
    {
        ulong value = 0;
        foreach (byte b in Take(length)) value = (value << 8) | b;
        return value;
    }
}
