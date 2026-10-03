using Swtor.Formats.Gom;

namespace Swtor.Assets;

/// <summary>Appearance slots of the game. The values are the indices of the game enumeration "appSlot*".</summary>
public enum AppearanceSlot
{
    Age = 0, Boot, Bracer, Chest, Complexion, Creature, EyeColor, Face, FaceHair, FacePaint, Hair, HairColor,
    Hand, Head, Leg, SkinColor, Waist, Unknown, GarmentHue, ColorScheme, MainHand, OffHand,
}

/// <summary>
/// One choice in the character creator, for example one head or one hair color.
/// <see cref="AssetId"/> is an asset id from art/dynamic/&lt;slot&gt;/index.xml. <see cref="MaterialId"/> is the id
/// of one material of that asset (0 if none).
/// </summary>
public sealed record CharacterOption(long Key, AppearanceSlot Slot, long AssetId, long MaterialId);

/// <summary>
/// The creator data for one class, gender and race ("pcs.trooper.male.human" in the game database).
/// For each option, <see cref="Compatible"/> lists the options of other slots that can be combined with it.
/// </summary>
public sealed class CharacterSpec
{
    // Field ids in the game data are hashes of names that are not known. These ids were found by reading the data.
    private const ulong OptionsField = 0x40000003A1E59EF0;    // map: option key -> option object
    private const ulong CompatibleField = 0x40000003A3725973; // map: option key -> map: slot -> list of option keys
    private const ulong SlotField = 0x4000000316C18126;       // in an option: enum slot
    private const ulong AssetField = 0x4000000316C18127;      // in an option: asset id
    private const ulong MaterialField = 0x4000000316C18128;   // in an option: material id

    public required string Name { get; init; }

    /// <summary>For example "sith_inquisitor".</summary>
    public required string Class { get; init; }

    /// <summary>"male" or "female".</summary>
    public required string Gender { get; init; }

    /// <summary>For example "twilek". A "_legacy" ending is removed and sets <see cref="IsLegacy"/>.</summary>
    public required string Race { get; init; }

    public required bool IsLegacy { get; init; }

    public required IReadOnlyList<CharacterOption> Options { get; init; }

    public required IReadOnlyDictionary<long, IReadOnlyDictionary<AppearanceSlot, IReadOnlyList<long>>> Compatible { get; init; }

    /// <summary>Reads a spec from a game object. Returns null if the name or data does not look like a spec.</summary>
    public static CharacterSpec? FromNode(GomNode node)
    {
        string[] parts = node.Name.Split('.');
        if (parts.Length != 4 || parts[0] != "pcs") return null;

        var options = new List<CharacterOption>();
        if (node.Object.Find(OptionsField)?.Value is GomMap optionMap)
        {
            foreach (var entry in optionMap.Entries)
            {
                if (entry.Key is not long key || entry.Value is not GomObject option) continue;
                if (option.Find(SlotField)?.Value is not GomEnumValue slot) continue;
                options.Add(new CharacterOption(
                    key, (AppearanceSlot)slot.Value,
                    option.Find(AssetField)?.Value as long? ?? 0,
                    option.Find(MaterialField)?.Value as long? ?? 0));
            }
        }

        var compatible = new Dictionary<long, IReadOnlyDictionary<AppearanceSlot, IReadOnlyList<long>>>();
        if (node.Object.Find(CompatibleField)?.Value is GomMap compatibleMap)
        {
            foreach (var entry in compatibleMap.Entries)
            {
                if (entry.Key is not long key || entry.Value is not GomMap bySlot) continue;
                var lists = new Dictionary<AppearanceSlot, IReadOnlyList<long>>();
                foreach (var slotEntry in bySlot.Entries)
                {
                    if (slotEntry.Key is GomEnumValue slot && slotEntry.Value is GomList list)
                        lists[(AppearanceSlot)slot.Value] = list.Items.OfType<long>().ToList();
                }
                compatible[key] = lists;
            }
        }

        string race = parts[3];
        bool legacy = race.EndsWith("_legacy", StringComparison.Ordinal);
        return new CharacterSpec
        {
            Name = node.Name, Class = parts[1], Gender = parts[2],
            Race = legacy ? race[..^"_legacy".Length] : race, IsLegacy = legacy,
            Options = options, Compatible = compatible,
        };
    }
}
