using System.Buffers.Binary;
using System.Text;
using Swtor.Formats;
using Swtor.Formats.Stb;

namespace Swtor.Tests;

public class StringTableTests
{
    // Builds a table: 3 header bytes, count, 26-byte entries (id, type, flag, float, length, offset, unknown), then texts.
    private static byte[] Build(params (long Id, byte Type, string Text)[] entries)
    {
        var texts = entries.Select(e => Encoding.UTF8.GetBytes(e.Text)).ToList();
        int dataStart = 7 + 26 * entries.Length;
        var data = new List<byte>();
        data.AddRange(new byte[] { 1, 0, 0 });
        data.AddRange(BitConverter.GetBytes(entries.Length));
        int offset = dataStart;
        for (int i = 0; i < entries.Length; i++)
        {
            var entry = new byte[26];
            BinaryPrimitives.WriteInt64LittleEndian(entry, entries[i].Id);
            entry[8] = entries[i].Type;
            BinaryPrimitives.WriteSingleLittleEndian(entry.AsSpan(10), 1f);
            BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(14), texts[i].Length);
            BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(18), offset);
            data.AddRange(entry);
            offset += texts[i].Length;
        }
        foreach (var text in texts) data.AddRange(text);
        return [.. data];
    }

    [Fact]
    public void Get_ReturnsTextAndSkipsEmptyDuplicates()
    {
        var table = StringTable.Parse(Build((100, 65, "Veste d'attaque"), (100, 70, "Veste d'attaque (f)"), (101, 65, ""), (102, 65, "Gants")));

        Assert.Equal("Veste d'attaque", table.Get(100));
        Assert.Null(table.Get(101)); // Empty text counts as missing.
        Assert.Equal("Gants", table.Get(102));
        Assert.Null(table.Get(999));
    }

    [Theory]
    [InlineData("str.itm#102", "Gants")]
    [InlineData("str.itm#999", null)]
    [InlineData("no hash here", null)]
    public void GetByReference_UsesNumberAfterHash(string reference, string? expected)
    {
        var table = StringTable.Parse(Build((102, 65, "Gants")));

        Assert.Equal(expected, table.GetByReference(reference));
    }

    [Fact]
    public void Parse_TruncatedFile_Throws()
    {
        var data = Build((1, 65, "abc"), (2, 65, "def"));

        Assert.Throws<GameFormatException>(() => StringTable.Parse(data[..30]));
        Assert.Throws<GameFormatException>(() => StringTable.Parse(data[..^2]));
    }
}
