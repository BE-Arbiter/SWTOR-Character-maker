using Swtor.Assets;
using Swtor.Formats.Gom;

namespace Swtor.Tests;

public class CharacterSpecTests
{
    private static GomNode MakeNode(string name)
    {
        // Option 1025 is a head (enum value 13) with asset 111 and material 222.
        var option = new GomObject(0,
        [
            new GomField(0x4000000316C18126, GomType.Enum, new GomEnumValue(13)),
            new GomField(0x4000000316C18127, GomType.Int64, 111L),
            new GomField(0x4000000316C18128, GomType.Int64, 222L),
        ]);
        var options = new GomMap(GomType.Int64, GomType.EmbeddedClass, [new(1025L, option)]);

        // Head 1025 allows skin color options 1039 and 1040 (enum value 15).
        var allowed = new GomMap(GomType.Enum, GomType.List,
            [new(new GomEnumValue(15), new GomList(GomType.Int64, [1039L, 1040L]))]);
        var compatible = new GomMap(GomType.Int64, GomType.Map, [new(1025L, allowed)]);

        return new GomNode(1, name, 2, [],
            new GomObject(0,
            [
                new GomField(0x40000003A1E59EF0, GomType.Map, options),
                new GomField(0x40000003A3725973, GomType.Map, compatible),
            ]));
    }

    [Fact]
    public void FromNode_ReadsNameOptionsAndCompatibility()
    {
        var spec = CharacterSpec.FromNode(MakeNode("pcs.trooper.male.human_legacy"))!;

        Assert.Equal(("trooper", "male", "human", true), (spec.Class, spec.Gender, spec.Race, spec.IsLegacy));
        var option = Assert.Single(spec.Options);
        Assert.Equal(new CharacterOption(1025, AppearanceSlot.Head, 111, 222), option);
        Assert.Equal([1039L, 1040L], spec.Compatible[1025][AppearanceSlot.SkinColor]);
    }

    [Theory]
    [InlineData("itm.something.else")]
    [InlineData("pcs.too.short")]
    public void FromNode_OtherNames_ReturnsNull(string name)
    {
        Assert.Null(CharacterSpec.FromNode(MakeNode(name)));
    }
}
