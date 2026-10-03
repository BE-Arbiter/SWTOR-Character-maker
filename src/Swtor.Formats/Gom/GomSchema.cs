using System.Buffers.Binary;
using System.Text;

namespace Swtor.Formats.Gom;

/// <summary>
/// Declared type of a field. <see cref="ReferenceId"/> is the enum or class id for Enum, EmbeddedClass and ClassRef.
/// <see cref="Item"/> is the item type of a list, or the key type of a map. <see cref="Value"/> is the value type of a map.
/// </summary>
public sealed record GomTypeDescriptor(GomType Type, ulong ReferenceId, GomTypeDescriptor? Item, GomTypeDescriptor? Value);

/// <summary>An enumeration. Values are numbered from 0 in the order of <see cref="Names"/>.</summary>
public sealed record GomEnumDefinition(ulong Id, IReadOnlyList<string> Names);

/// <summary>A field definition. The name is not stored in the file: only the id (a hash of the name) is.</summary>
public sealed record GomFieldDefinition(ulong Id, GomTypeDescriptor? Type);

/// <summary>A class: the ids of its fields. The class name is not stored in the file.</summary>
public sealed record GomClassDefinition(ulong Id, IReadOnlyList<ulong> FieldIds);

/// <summary>
/// The type definitions of the game (systemgenerated/client.gom): enumerations, classes and fields.
/// Records have the same header as bucket records and are aligned to 8 bytes from the start of the file.
/// </summary>
public sealed class GomSchema
{
    private const int EnumRecord = 2, FieldRecord = 3, ClassRecord = 4;

    public IReadOnlyDictionary<ulong, GomEnumDefinition> Enums { get; }

    public IReadOnlyDictionary<ulong, GomFieldDefinition> Fields { get; }

    public IReadOnlyDictionary<ulong, GomClassDefinition> Classes { get; }

    private GomSchema(
        Dictionary<ulong, GomEnumDefinition> enums,
        Dictionary<ulong, GomFieldDefinition> fields,
        Dictionary<ulong, GomClassDefinition> classes)
    {
        Enums = enums;
        Fields = fields;
        Classes = classes;
    }

    /// <summary>Returns the name of an enum value, or null when the enum or value is unknown.</summary>
    public string? EnumName(ulong enumId, int value) =>
        Enums.TryGetValue(enumId, out var e) && value >= 0 && value < e.Names.Count ? e.Names[value] : null;

    /// <summary>Parses client.gom.</summary>
    public static GomSchema Parse(byte[] data)
    {
        if (data.Length < 8 || BinaryPrimitives.ReadUInt32LittleEndian(data) != 0x424C4244) // "DBLB"
            throw new GameFormatException("Not a client.gom file", 0);

        var enums = new Dictionary<ulong, GomEnumDefinition>();
        var fields = new Dictionary<ulong, GomFieldDefinition>();
        var classes = new Dictionary<ulong, GomClassDefinition>();

        int position = 8;
        while (position + 0x18 <= data.Length)
        {
            int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position));
            if (length == 0) break;
            if (length < 0x18 || position + length > data.Length) throw new GameFormatException("Invalid record length", position);

            var record = data.AsSpan(position, length);
            ulong id = BinaryPrimitives.ReadUInt64LittleEndian(record[8..]);
            int kind = (BinaryPrimitives.ReadUInt16LittleEndian(record[16..]) >> 3) & 0xF;
            try
            {
                switch (kind)
                {
                    case EnumRecord: enums[id] = ReadEnum(record, id); break;
                    case FieldRecord: fields[id] = ReadField(record, id); break;
                    case ClassRecord: classes[id] = ReadClass(record, id); break;
                }
            }
            catch (ArgumentOutOfRangeException)
            {
                throw new GameFormatException("Definition is outside its record", position);
            }
            position = (position + length + 7) & ~7;
        }
        return new GomSchema(enums, fields, classes);
    }

    // Enum payload (from 0x18): u16 value count, u16 offset of the offset table. Each offset points to a terminated name.
    private static GomEnumDefinition ReadEnum(ReadOnlySpan<byte> r, ulong id)
    {
        int count = BinaryPrimitives.ReadUInt16LittleEndian(r[0x18..]);
        int table = BinaryPrimitives.ReadUInt16LittleEndian(r[0x1A..]);
        var names = new string[count];
        for (int i = 0; i < count; i++)
        {
            var text = r[BinaryPrimitives.ReadUInt16LittleEndian(r[(table + 2 * i)..])..];
            names[i] = Encoding.UTF8.GetString(text[..text.IndexOf((byte)0)]);
        }
        return new GomEnumDefinition(id, names);
    }

    // Field payload (from 0x18): u16 modifiers, u16 type length, u16 type offset.
    private static GomFieldDefinition ReadField(ReadOnlySpan<byte> r, ulong id)
    {
        int length = BinaryPrimitives.ReadUInt16LittleEndian(r[0x1A..]);
        int offset = BinaryPrimitives.ReadUInt16LittleEndian(r[0x1C..]);
        int p = offset;
        return new GomFieldDefinition(id, length == 0 ? null : ReadType(r[..(offset + length)], ref p));
    }

    // Class payload (from 0x18): u16 archetype, two u64 script ids, then u16 component count and offset, u16 field count and offset.
    private static GomClassDefinition ReadClass(ReadOnlySpan<byte> r, ulong id)
    {
        int count = BinaryPrimitives.ReadUInt16LittleEndian(r[0x2E..]);
        int offset = BinaryPrimitives.ReadUInt16LittleEndian(r[0x30..]);
        var ids = new ulong[count];
        for (int i = 0; i < count; i++) ids[i] = BinaryPrimitives.ReadUInt64LittleEndian(r[(offset + 8 * i)..]);
        return new GomClassDefinition(id, ids);
    }

    // Type in a definition: type byte, then an id for enum and class types, or nested types for list and map.
    private static GomTypeDescriptor ReadType(ReadOnlySpan<byte> r, ref int p)
    {
        var type = (GomType)r[p++];
        switch (type)
        {
            case GomType.Enum or GomType.EmbeddedClass or GomType.ClassRef:
                ulong id = BinaryPrimitives.ReadUInt64LittleEndian(r[p..]);
                p += 8;
                return new GomTypeDescriptor(type, id, null, null);
            case GomType.List:
                return new GomTypeDescriptor(type, 0, ReadType(r, ref p), null);
            case GomType.Map:
                var key = ReadType(r, ref p);
                return new GomTypeDescriptor(type, 0, key, ReadType(r, ref p));
            default:
                return new GomTypeDescriptor(type, 0, null, null);
        }
    }
}
