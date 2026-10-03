using System.Numerics;

namespace Swtor.Formats.Gom;

/// <summary>
/// Decodes the object data of a GOM node. The data describes itself: every field has a type byte.
/// Field names and declared types are in the client.gom definitions, but are not needed to read the values.
/// </summary>
public static class GomObjectReader
{
    private const int MaxDepth = 64;
    private const byte LookupMarker = 0xD2;

    /// <summary>Reads one object from the current position of <paramref name="reader"/>.</summary>
    public static GomObject ReadObject(GomReader reader) => ReadObject(reader, 0);

    private static GomObject ReadObject(GomReader reader, int depth)
    {
        if (depth > MaxDepth) throw new GameFormatException("GOM objects are nested too deep", reader.Position);

        long classTotal = reader.ReadSigned();
        int stored = reader.ReadCount();
        var fields = new List<GomField>(Math.Min(stored, 1024));
        ulong id = 0;
        for (int i = 0; i < stored; i++)
        {
            // Field ids are stored as differences from the previous id, to keep the numbers small.
            id = unchecked(id + (ulong)reader.ReadSigned());
            var type = (GomType)reader.ReadByte();
            fields.Add(new GomField(id, type, ReadValue(reader, type, depth)));
        }
        return new GomObject(classTotal, fields);
    }

    private static object? ReadValue(GomReader reader, GomType type, int depth)
    {
        switch (type)
        {
            case GomType.UInt64: return reader.ReadNumber();
            case GomType.Int64 or GomType.Time or GomType.TimeSpan: return reader.ReadSigned();
            case GomType.Boolean:
                return reader.ReadByte() == 1;
            case GomType.Float: return reader.ReadSingle();
            case GomType.Enum: return new GomEnumValue(checked((int)reader.ReadSigned()) - 1);
            case GomType.String: return reader.ReadString();
            case GomType.List: return ReadList(reader, depth);
            case GomType.Map: return ReadMap(reader, depth);
            case GomType.EmbeddedClass: return ReadObject(reader, depth + 1);
            case GomType.Script or GomType.ClassRef: return new GomRef(reader.ReadNumber());
            case GomType.Vec3:
                return new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            case GomType.Tuple: return ReadTuple(reader, depth);
            default:
                throw new GameFormatException($"Unsupported GOM type 0x{(byte)type:X2}", reader.Position - 1);
        }
    }

    // List: item type byte, total count, stored count, then (index, item) pairs.
    private static GomList ReadList(GomReader reader, int depth)
    {
        var itemType = (GomType)reader.ReadByte();
        reader.ReadCount(); // Total count. Only the stored items follow.
        int stored = reader.ReadCount();
        var items = new List<object?>(Math.Min(stored, 4096));
        for (int i = 0; i < stored; i++)
        {
            reader.ReadCount(); // Index of the item in the full list.
            items.Add(ReadValue(reader, itemType, depth));
        }
        return new GomList(itemType, items);
    }

    // Map: key type byte, value type byte, total count, stored count, then (optional 0xD2, key, value) entries.
    private static GomMap ReadMap(GomReader reader, int depth)
    {
        var keyType = (GomType)reader.ReadByte();
        var valueType = (GomType)reader.ReadByte();
        reader.ReadCount();
        int stored = reader.ReadCount();
        var entries = new List<KeyValuePair<object?, object?>>(Math.Min(stored, 4096));
        for (int i = 0; i < stored; i++)
        {
            if (reader.PeekByte() == LookupMarker) reader.ReadByte();
            var key = ReadValue(reader, keyType, depth);
            entries.Add(new(key, ReadValue(reader, valueType, depth)));
        }
        return new GomMap(keyType, valueType, entries);
    }

    // Tuple: total count, stored count (equal), then (index, type byte, item) for each item.
    private static GomList ReadTuple(GomReader reader, int depth)
    {
        int total = reader.ReadCount();
        int stored = reader.ReadCount();
        if (total != stored) throw new GameFormatException("Tuple counts differ", reader.Position);
        var items = new List<object?>(Math.Min(stored, 256));
        for (int i = 0; i < stored; i++)
        {
            reader.ReadCount(); // Index (counts from 1).
            var type = (GomType)reader.ReadByte();
            items.Add(ReadValue(reader, type, depth));
        }
        return new GomList(GomType.None, items);
    }
}
