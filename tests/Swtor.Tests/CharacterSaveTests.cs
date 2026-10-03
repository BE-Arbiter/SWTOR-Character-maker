using Swtor.Assets;

namespace Swtor.Tests;

public class CharacterSaveTests
{
    private static CharacterSave Sample() => new()
    {
        Class = "trooper", Gender = "female", Race = "zabrak", Legacy = true,
        Options = { ["Head"] = new SavedOption(111, 222), ["Hair"] = new SavedOption(333, 0) },
        Equipment = { ["chest"] = new SavedEquipment("1000065", "1001117", "1002026"), ["leg"] = new SavedEquipment("5", null, null) },
    };

    [Fact]
    public void Json_RoundTrip_KeepsEverything()
    {
        var loaded = CharacterSave.FromJson(Sample().ToJson());

        Assert.Equal(("trooper", "female", "zabrak", true), (loaded.Class, loaded.Gender, loaded.Race, loaded.Legacy));
        Assert.Equal(new SavedOption(111, 222), loaded.Options["Head"]);
        Assert.Equal(new SavedEquipment("1000065", "1001117", "1002026"), loaded.Equipment["chest"]);
        Assert.Equal(new SavedEquipment("5", null, null), loaded.Equipment["leg"]);
    }

    [Fact]
    public void SaveAndLoad_ThroughFile_CreatesFolder()
    {
        string path = Path.Combine(Path.GetTempPath(), "swtor-save-test-" + Guid.NewGuid().ToString("N"), "sub", "c.json");
        try
        {
            Sample().Save(path);

            Assert.Equal("zabrak", CharacterSave.Load(path).Race);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(path))!, recursive: true);
        }
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{ \"version\": 99, \"class\": \"a\", \"gender\": \"b\", \"race\": \"c\" }")]
    [InlineData("{ \"version\": 1 }")]
    [InlineData("null")]
    public void FromJson_InvalidInput_Throws(string json)
    {
        Assert.Throws<InvalidDataException>(() => CharacterSave.FromJson(json));
    }
}
