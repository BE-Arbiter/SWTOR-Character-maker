using Swtor.Assets;

namespace Swtor.Tests;

public class NpcLookTests
{
    private static NpcLook Look(string key, string body, string[] names) =>
        new(key, body, [new NpcPart(AppearanceSlot.Head, 10, 20, 0, 0), new NpcPart(AppearanceSlot.Chest, 11, 21, 5, 6)], names, names.Length);

    [Theory]
    [InlineData("bfn", 'f')]
    [InlineData("bma", 'm')]
    [InlineData("", 'm')]
    public void Gender_IsTheSecondLetterOfTheBodyType(string body, char expected) =>
        Assert.Equal(expected, Look("npp.a.b", body, []).Gender);

    [Fact]
    public void Label_UsesTheFirstNameThenTheKey()
    {
        Assert.Equal("Lana", Look("npp.a.lana", "bfn", ["Lana", "Lana Corrs"]).Label);
        Assert.Equal("a.lana", Look("npp.a.lana", "bfn", []).Label);
    }

    [Fact]
    public void SearchText_HasKeyAndNames_InLowerCase() =>
        Assert.Contains("capitaine", Look("npp.a.b", "bfn", ["Capitaine Vondral"]).SearchText);

    [Fact]
    public void Part_FindsASlot_OrNull()
    {
        var look = Look("npp.a.b", "bfn", []);
        Assert.Equal(11, look.Part(AppearanceSlot.Chest)?.AssetId);
        Assert.Null(look.Part(AppearanceSlot.Hair));
    }
}
