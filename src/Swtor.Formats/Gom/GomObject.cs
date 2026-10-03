using System.Numerics;

namespace Swtor.Formats.Gom;

/// <summary>Type byte of a stored value.</summary>
public enum GomType : byte
{
    None = 0x00,
    UInt64 = 0x01,
    Int64 = 0x02,
    Boolean = 0x03,
    Float = 0x04,
    Enum = 0x05,
    String = 0x06,
    List = 0x07,
    Map = 0x08,
    EmbeddedClass = 0x09,
    Script = 0x0E,
    ClassRef = 0x0F,
    Timer = 0x11,
    Vec3 = 0x12,
    TimeSpan = 0x14,
    Time = 0x15,
    Tuple = 0x18,
}

/// <summary>One stored field. <see cref="Id"/> is a 64-bit hash of the field name.</summary>
/// <remarks>
/// Value types by <see cref="Type"/>: UInt64 = ulong, Int64/Time/TimeSpan = long, Boolean = bool,
/// Float = float, Enum = <see cref="GomEnumValue"/>, String = string, List/Tuple = <see cref="GomList"/>,
/// Map = <see cref="GomMap"/>, EmbeddedClass = <see cref="GomObject"/>, Script/ClassRef = <see cref="GomRef"/>,
/// Vec3 = <see cref="Vector3"/>.
/// </remarks>
public sealed record GomField(ulong Id, GomType Type, object? Value);

/// <summary>A decoded object: a list of fields. <see cref="ClassTotal"/> is the first number of the stored object.</summary>
public sealed class GomObject(long classTotal, IReadOnlyList<GomField> fields)
{
    public long ClassTotal { get; } = classTotal;

    public IReadOnlyList<GomField> Fields { get; } = fields;

    /// <summary>Returns the first field with this id, or null.</summary>
    public GomField? Find(ulong id)
    {
        foreach (var field in Fields)
            if (field.Id == id) return field;
        return null;
    }
}

/// <summary>Zero-based value of an enumeration. The enumeration itself comes from the field definition.</summary>
public readonly record struct GomEnumValue(int Value);

/// <summary>Reference to another object by id.</summary>
public readonly record struct GomRef(ulong Id);

/// <summary>A list or tuple. Items of a list share one type. For tuples <see cref="ItemType"/> is None.</summary>
public sealed record GomList(GomType ItemType, IReadOnlyList<object?> Items);

/// <summary>A map as a list of key/value pairs in stored order.</summary>
public sealed record GomMap(GomType KeyType, GomType ValueType, IReadOnlyList<KeyValuePair<object?, object?>> Entries);
