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
    public void Json_RoundTrip_KeepsCustomColors()
    {
        var save = Sample();
        save.Equipment["chest"] = new SavedEquipment("1", "2", null, null, "1720514", [0.6f, 0.2f, -0.1f, 1.5f]);

        var loaded = CharacterSave.FromJson(save.ToJson()).Equipment["chest"];

        Assert.NotNull(loaded.PrimaryCustom);
        Assert.Equal([0.6f, 0.2f, -0.1f, 1.5f], loaded.PrimaryCustom);
        Assert.Null(loaded.SecondaryCustom);
        Assert.Equal("1720514", loaded.SecondaryId);
    }

    [Fact]
    public void Json_NpcHead_RoundTripsAndDefaultsToOriginalColors()
    {
        var save = Sample();
        save.NpcHead = new SavedOption(3014313, 3014314);
        save.NpcOriginalColors = false;

        var loaded = CharacterSave.FromJson(save.ToJson());
        var old = CharacterSave.FromJson("""{ "version": 1, "class": "trooper", "gender": "male", "race": "human" }""");

        Assert.Equal(new SavedOption(3014313, 3014314), loaded.NpcHead);
        Assert.False(loaded.NpcOriginalColors);
        Assert.Null(old.NpcHead);
        Assert.True(old.NpcOriginalColors);
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
