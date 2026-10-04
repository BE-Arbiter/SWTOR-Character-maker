using System.Buffers.Binary;
using System.Text;

namespace Swtor.Formats.Stb;

/// <summary>
/// A game text table (for example fr-fr/str/itm.stb). The file is: 3 bytes (version), an int32 entry count,
/// then 26 bytes per entry (id, type, flag, float, text length, text offset, unknown), then the texts.
/// An id can have several entries (for example male and female text). We keep the first non-empty one.
/// </summary>
public sealed class StringTable
{
    private const int HeaderSize = 7;
    private const int EntrySize = 26;

    private readonly byte[] _data;
    private readonly Dictionary<long, (int Offset, int Length)> _entries;

    public int Count => _entries.Count;

    private StringTable(byte[] data, Dictionary<long, (int, int)> entries)
    {
        _data = data;
        _entries = entries;
    }

    /// <summary>Reads a table. Throws <see cref="GameFormatException"/> if the file is shorter than its entry list says.</summary>
    public static StringTable Parse(byte[] data)
    {
        if (data.Length < HeaderSize) throw new GameFormatException("String table is too short", 0);
        int count = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(3));
        if (count < 0 || HeaderSize + (long)count * EntrySize > data.Length)
            throw new GameFormatException("String table entry list is outside the file", 3);

        var entries = new Dictionary<long, (int, int)>(count);
        for (int i = 0; i < count; i++)
        {
            int p = HeaderSize + i * EntrySize;
            long id = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(p));
            int length = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(p + 14));
            int offset = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(p + 18));
            if (length < 0 || offset < 0 || (long)offset + length > data.Length)
                throw new GameFormatException("String is outside the file", p);
            if (length > 0 && !entries.ContainsKey(id)) entries[id] = (offset, length);
        }
        return new StringTable(data, entries);
    }

    /// <summary>All entries (id and text), in no particular order. For tools and debugging.</summary>
    public IEnumerable<(long Id, string Text)> Entries() =>
        _entries.Select(e => (e.Key, Encoding.UTF8.GetString(_data, e.Value.Offset, e.Value.Length)));

    /// <summary>Returns the text for an id, or null if there is none.</summary>
    public string? Get(long id) =>
        _entries.TryGetValue(id, out var e) ? Encoding.UTF8.GetString(_data, e.Offset, e.Length) : null;

    /// <summary>
    /// Returns the text for a reference such as "str.itm#3913664394428416" (the number after '#' is the id).
    /// Returns null for other forms or unknown ids.
    /// </summary>
    public string? GetByReference(string reference)
    {
        int hash = reference.LastIndexOf('#');
        return hash >= 0 && long.TryParse(reference.AsSpan(hash + 1), out long id) ? Get(id) : null;
    }
}
