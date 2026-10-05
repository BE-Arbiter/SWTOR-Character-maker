using System.Text.Json;

namespace Swtor.Assets;

/// <summary>A creator choice saved as ids from the game data (not as list positions), so it survives updates of the option lists.</summary>
public sealed record SavedOption(long AssetId, long MaterialId);

/// <summary>
/// An equipped asset: asset id, material id (null for the first material) and the color scheme guid (null for default colors).
/// <paramref name="PrimaryId"/> and <paramref name="SecondaryId"/> are palette asset ids chosen by hand. They replace the palettes of the scheme. Null keeps the scheme.
/// <paramref name="PrimaryCustom"/> and <paramref name="SecondaryCustom"/> are custom colors: the four values of the game shader
/// (hue, saturation, brightness, contrast, see <c>PaletteTint.Custom</c>). They win over the palette ids.
/// </summary>
public sealed record SavedEquipment(string AssetId, string? MaterialId, string? SchemeId, string? PrimaryId = null, string? SecondaryId = null,
    float[]? PrimaryCustom = null, float[]? SecondaryCustom = null);

/// <summary>
/// A saved character. Stored as indented JSON. <see cref="Options"/> is keyed by <see cref="AppearanceSlot"/> name
/// and holds only the slots that have a choice. <see cref="Equipment"/> is keyed by slot folder name ("chest", "leg", ...).
/// </summary>
public sealed class CharacterSave
{
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public int Version { get; set; } = CurrentVersion;

    public string Class { get; set; } = "";

    public string Gender { get; set; } = "";

    public string Race { get; set; } = "";

    public bool Legacy { get; set; }

    public Dictionary<string, SavedOption> Options { get; set; } = [];

    public Dictionary<string, SavedEquipment> Equipment { get; set; } = [];

    /// <summary>A head of the head index that replaces the head of <see cref="Options"/> (often a head that only NPCs have), or null.</summary>
    public SavedOption? NpcHead { get; set; }

    /// <summary>True keeps the colors of the files of <see cref="NpcHead"/>: skin color, eye color, complexion and face paint are not applied.</summary>
    public bool NpcOriginalColors { get; set; } = true;

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Reads a save from JSON. Throws <see cref="InvalidDataException"/> when the text is not a valid save of a known version.</summary>
    public static CharacterSave FromJson(string json)
    {
        try
        {
            var save = JsonSerializer.Deserialize<CharacterSave>(json, JsonOptions)
                ?? throw new InvalidDataException("The file is empty.");
            if (save.Version != CurrentVersion) throw new InvalidDataException($"Unsupported save version {save.Version}.");
            if (save.Class.Length == 0 || save.Gender.Length == 0 || save.Race.Length == 0)
                throw new InvalidDataException("The save has no class, gender or race.");
            return save;
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"Invalid save file: {e.Message}", e);
        }
    }

    public void Save(string path)
    {
        string? folder = Path.GetDirectoryName(Path.GetFullPath(path));
        if (folder is not null) Directory.CreateDirectory(folder);
        File.WriteAllText(path, ToJson());
    }

    public static CharacterSave Load(string path) => FromJson(File.ReadAllText(path));
}
