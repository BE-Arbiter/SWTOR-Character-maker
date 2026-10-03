using Swtor.Formats;
using Swtor.Formats.Gom;

namespace Swtor.Tests;

public class GomTests
{
    private static GomReader Reader(params byte[] bytes) => new(bytes, 0, bytes.Length);

    [Theory]
    [InlineData(new byte[] { 0x00 }, 0UL)]
    [InlineData(new byte[] { 0xBF }, 191UL)]
    [InlineData(new byte[] { 0xC8, 0xC0 }, 192UL)]
    [InlineData(new byte[] { 0xC9, 0x03, 0x6C }, 0x036CUL)]
    [InlineData(new byte[] { 0xCF, 0x40, 0, 0, 0, 0x28, 0xCF, 0x66, 0x6A }, 0x4000000028CF666AUL)]
    public void ReadNumber_DecodesPrefixedBigEndian(byte[] bytes, ulong expected)
    {
        var reader = Reader(bytes);

        Assert.Equal(expected, reader.ReadNumber());
        Assert.True(reader.AtEnd);
    }

    [Theory]
    [InlineData(new byte[] { 0x05 }, 5L)]
    [InlineData(new byte[] { 0xC8, 0xFF }, 255L)]
    [InlineData(new byte[] { 0xC0, 0x80 }, -128L)]
    [InlineData(new byte[] { 0xD0 }, long.MinValue)]
    public void ReadSigned_NegativeBelowC8(byte[] bytes, long expected)
    {
        Assert.Equal(expected, Reader(bytes).ReadSigned());
    }

    [Fact]
    public void ReadNumber_InvalidPrefix_Throws()
    {
        Assert.Throws<GameFormatException>(() => Reader(0xD5).ReadNumber());
    }

    [Fact]
    public void ReadObject_DecodesStringListMapAndEnum()
    {
        byte[] data =
        [
            0x05, 0x04,                          // class total, 4 stored fields
            0x01, 0x06, 0x02, (byte)'h', (byte)'i', // field id 1: string "hi"
            0x01, 0x07, 0x02, 0x02, 0x02,        // field id 2: list of Int64, total 2, stored 2
              0x01, 0x07,                        //   index 1, value 7
              0x02, 0x08,                        //   index 2, value 8
            0x01, 0x08, 0x06, 0x03, 0x01, 0x01,  // field id 3: map<String,Boolean>, total 1, stored 1
              0xD2, 0x01, (byte)'k', 0x01,       //   marker, key "k", value true
            0x01, 0x05, 0x04,                    // field id 4: enum stored as 4 (value 3, zero-based)
        ];
        var reader = Reader(data);

        var obj = GomObjectReader.ReadObject(reader);

        Assert.True(reader.AtEnd);
        Assert.Equal([1UL, 2UL, 3UL, 4UL], obj.Fields.Select(f => f.Id));
        Assert.Equal("hi", obj.Find(1)!.Value);
        Assert.Equal([7L, 8L], ((GomList)obj.Find(2)!.Value!).Items.Cast<long>());
        var map = (GomMap)obj.Find(3)!.Value!;
        Assert.Equal(("k", true), (map.Entries[0].Key, map.Entries[0].Value));
        Assert.Equal(new GomEnumValue(3), obj.Find(4)!.Value);
    }

    [Fact]
    public void ReadObject_UnknownType_Throws()
    {
        Assert.Throws<GameFormatException>(() => GomObjectReader.ReadObject(Reader(0x01, 0x01, 0x01, 0x7F)));
    }

    [Fact]
    public void Prototype_RealFile_ReadsHeaderAndObject()
    {
        var data = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "proto_small.node"));

        var node = GomPrototypeFile.Parse(data);

        Assert.StartsWith("cnv.location.coruscant", node.Name);
        Assert.Equal(0xE0UL, node.Id >> 56);
        Assert.NotEmpty(node.Object.Fields);
    }

    [Fact]
    public void Prototype_Truncated_Throws()
    {
        var data = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "proto_small.node"));

        Assert.Throws<GameFormatException>(() => GomPrototypeFile.Parse(data[..(data.Length / 2)]));
    }
}
