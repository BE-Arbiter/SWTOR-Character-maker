using Swtor.Assets;

namespace Swtor.Tests;

public class UiTextTests
{
    [Fact]
    public void WithoutTables_GivesEnglishNames()
    {
        var text = UiText.Empty;
        Assert.Equal("Sith Warrior", text.Class("sith_warrior"));
        Assert.Equal("Twilek", text.Race("twilek"));
        Assert.Equal("Skin color", text.Slot(AppearanceSlot.SkinColor));
        Assert.Equal("Chest", text.EquipSlot("chest"));
        Assert.Equal("Agile", text.BodyType("bma"));
        Assert.Equal("Robust", text.BodyType("bfb"));
    }

    [Theory]
    [InlineData("FORME DE LA TÊTE", "Forme de la tête")]
    [InlineData("Torse", "Torse")]
    [InlineData("", "")]
    public void SentenceCase_OnlyChangesAllCapitals(string input, string expected) =>
        Assert.Equal(expected, UiText.SentenceCase(input));
}
