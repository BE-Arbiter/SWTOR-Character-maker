using System.Buffers.Binary;
using System.Text;
using ZstdSharp;

namespace Swtor.Formats.Gom;

/// <summary>A decoded game object (item, ability, spawner, ...). <see cref="Name"/> is the dotted name, for example "itm.mat.craft".</summary>
public sealed record GomNode(ulong Id, string Name, ulong ClassId, IReadOnlyList<ulong> GlommedClassIds, GomObject Object);

/// <summary>Position and header data of one object in a bucket file. Reading this is cheap. Decoding the object is not.</summary>
public sealed record GomNodeInfo(
    ulong Id, ulong ClassId, string Name, int GlommedCount, int ObjectSize,
    int RecordOffset, int RecordLength, int DataOffset);

/// <summary>
/// A bucket file (systemgenerated/buckets/*.bkt): a list of objects, each compressed with zstd.
/// The file has a "PBUK" header, then a "DBLB" section. Records are aligned to 8 bytes from the start of the "DBLB" tag.
/// </summary>
public sealed class GomBucketFile
{
    private readonly byte[] _data;

    public IReadOnlyList<GomNodeInfo> Nodes { get; }

    private GomBucketFile(byte[] data, List<GomNodeInfo> nodes)
    {
        _data = data;
        Nodes = nodes;
    }

    /// <summary>Reads the record headers of a bucket file. Takes ownership of <paramref name="data"/>.</summary>
    public static GomBucketFile Open(byte[] data)
    {
        if (data.Length < 0x24 || ReadU32(data, 0) != 0x4B554250) // "PBUK"
            throw new GameFormatException("Not a bucket file", 0);

        // First section (a small DBLB header), then the section that holds the records.
        int section = 8 + 4 + (int)ReadU32(data, 8);
        int dblb = section + 4;
        if (dblb + 8 > data.Length || ReadU32(data, dblb) != 0x424C4244) // "DBLB"
            throw new GameFormatException("Bucket has no DBLB section", dblb);

        var nodes = new List<GomNodeInfo>();
        int position = dblb + 8;
        while (position + 0x30 <= data.Length)
        {
            int length = (int)ReadU32(data, position);
            if (length == 0) break;
            if (length < 0x30 || position + length > data.Length)
                throw new GameFormatException("Invalid record length", position);
            nodes.Add(ReadInfo(data, position, length));
            position = dblb + ((position - dblb + length + 7) & ~7);
        }
        return new GomBucketFile(data, nodes);
    }

    /// <summary>Decompresses and decodes one object.</summary>
    public GomNode Decode(GomNodeInfo info)
    {
        var compressed = _data.AsSpan(info.DataOffset, info.RecordOffset + info.RecordLength - info.DataOffset);
        byte[] raw = IsZstd(compressed) ? Decompress(compressed, info) : compressed.ToArray();

        // The decompressed data is: 0 to 7 padding bytes, the glommed class ids, then the object.
        int skip = raw.Length - 8 * info.GlommedCount - info.ObjectSize;
        if (skip is < 0 or > 7) throw new GameFormatException($"Unexpected object size in {info.Name}", info.RecordOffset);

        var glommed = new ulong[info.GlommedCount];
        for (int i = 0; i < glommed.Length; i++) glommed[i] = BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(skip + 8 * i));

        int bodyStart = skip + 8 * info.GlommedCount;
        GomObject obj;
        if (info.ObjectSize == 0)
        {
            obj = new GomObject(0, []);
        }
        else
        {
            var reader = new GomReader(raw, bodyStart, raw.Length);
            obj = GomObjectReader.ReadObject(reader);
            if (!reader.AtEnd) throw new GameFormatException($"Object data is longer than its fields in {info.Name}", reader.Position);
        }
        return new GomNode(info.Id, info.Name, info.ClassId, glommed, obj);
    }

    // Record header (offsets from the record start): u32 length, u32 pad, u64 id, u16 flags, u16 data offset,
    // u16 name offset, u16 name end, u64 class id, ..., i16 glommed count at 0x24, i32 object size at 0x28.
    private static GomNodeInfo ReadInfo(byte[] d, int p, int length)
    {
        ulong id = BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(p + 8));
        int dataOffset = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(p + 0x12));
        int nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(p + 0x14));
        ulong classId = BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(p + 0x18));
        int glommed = BinaryPrimitives.ReadInt16LittleEndian(d.AsSpan(p + 0x24));
        int objectSize = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p + 0x28));
        if (dataOffset > length || nameOffset >= dataOffset || glommed < 0 || objectSize < 0)
            throw new GameFormatException("Invalid record header", p);

        var nameBytes = d.AsSpan(p + nameOffset, dataOffset - nameOffset);
        int end = nameBytes.IndexOf((byte)0);
        string name = Encoding.UTF8.GetString(end < 0 ? nameBytes : nameBytes[..end]);
        return new GomNodeInfo(id, classId, name, glommed, objectSize, p, length, p + dataOffset);
    }

    private static bool IsZstd(ReadOnlySpan<byte> data) =>
        data.Length >= 4 && data[0] == 0x28 && data[1] == 0xB5 && data[2] == 0x2F && data[3] == 0xFD;

    private static byte[] Decompress(ReadOnlySpan<byte> compressed, GomNodeInfo info)
    {
        // Padding bytes may follow the frame. The reader stops at the end of the first frame.
        using var decompressor = new Decompressor();
        int expected = 8 * info.GlommedCount + info.ObjectSize + 8;
        var buffer = new byte[expected];
        int written = decompressor.Unwrap(compressed, buffer);
        return buffer.AsSpan(0, written).ToArray();
    }

    private static uint ReadU32(byte[] d, int o) => BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o));
}

/// <summary>A prototype file (systemgenerated/prototypes/*.node): one object, stored without compression.</summary>
public static class GomPrototypeFile
{
    /// <summary>Header: "PROT", version, u64 id, name, description, two constants, u64 class id, glommed ids, flags, size.</summary>
    public static GomNode Parse(byte[] data)
    {
        if (data.Length < 0x30 || BinaryPrimitives.ReadUInt32LittleEndian(data) != 0x544F5250) // "PROT"
            throw new GameFormatException("Not a prototype file", 0);

        var span = data.AsSpan();
        int p = 8;
        ulong id = BinaryPrimitives.ReadUInt64LittleEndian(span[p..]);
        p += 8;
        string name = ReadCString(span, ref p);
        ReadCString(span, ref p); // Description. Always empty so far.
        p += 8; // Two constants (3 and 1).
        ulong classId = BinaryPrimitives.ReadUInt64LittleEndian(span[p..]);
        p += 8;
        uint glommedCount = BinaryPrimitives.ReadUInt32LittleEndian(span[p..]);
        p += 4;
        if (glommedCount > 1024 || p + 8L * glommedCount + 8 > data.Length) throw new GameFormatException("Invalid glommed count", p - 4);

        var glommed = new ulong[glommedCount];
        for (int i = 0; i < glommed.Length; i++, p += 8) glommed[i] = BinaryPrimitives.ReadUInt64LittleEndian(span[p..]);
        p += 1 + 2 + 1; // Node kind, repeated version, stream style.
        int contentLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[p..]);
        p += 4;
        if (contentLength < 0 || p + contentLength > data.Length) throw new GameFormatException("Invalid content length", p - 4);

        var reader = new GomReader(data, p, p + contentLength);
        var obj = GomObjectReader.ReadObject(reader);
        if (!reader.AtEnd) throw new GameFormatException($"Object data is longer than its fields in {name}", reader.Position);
        return new GomNode(id, name, classId, glommed, obj);
    }

    // Prototype names have a 32-bit length first, then a terminated string of that length.
    private static string ReadCString(ReadOnlySpan<byte> data, ref int p)
    {
        int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[p..]);
        p += 4;
        if (length < 0 || p + length > data.Length) throw new GameFormatException("Invalid string length", p - 4);
        var text = data.Slice(p, length);
        p += length;
        int end = text.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? text : text[..end]);
    }
}
