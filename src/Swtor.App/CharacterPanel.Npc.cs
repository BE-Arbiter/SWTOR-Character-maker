using System.Globalization;
using Swtor.Assets;
using Swtor.Formats.Xml;

namespace Swtor.App;

// NPC mode: the preview shows the look of a game NPC (head, hair, colors and equipment from the "npp" object)
// instead of the creator choices. The creator character is saved when the mode starts and comes back when it ends.
public sealed partial class CharacterPanel
{
    private static readonly (AppearanceSlot Slot, string Folder)[] NpcEquipment =
    [
        (AppearanceSlot.Face, "face"), (AppearanceSlot.Chest, "chest"), (AppearanceSlot.Hand, "hand"),
        (AppearanceSlot.Leg, "leg"), (AppearanceSlot.Boot, "boot"), (AppearanceSlot.Waist, "waist"), (AppearanceSlot.Bracer, "bracer"),
    ];

    private static readonly AppearanceSlot[] NpcOptionSlots =
    [
        AppearanceSlot.Hair, AppearanceSlot.FaceHair, AppearanceSlot.Complexion, AppearanceSlot.Age, AppearanceSlot.FacePaint,
        AppearanceSlot.SkinColor, AppearanceSlot.HairColor, AppearanceSlot.EyeColor,
    ];

    private NpcLook? _npcLook;
    private Dictionary<AppearanceSlot, CharacterOption>? _npcOptions;
    private CharacterSave? _stash;

    /// <summary>The NPC that the preview shows, or null in creator mode.</summary>
    public NpcLook? CurrentNpc => _npcLook;

    /// <summary>True when the game data is loaded, so <see cref="ShowNpc"/> works.</summary>
    public bool CanShowNpc => _catalog is not null && _index is not null;

    /// <summary>
    /// Shows an NPC in the preview. Returns false when the data is not loaded yet.
    /// The head and the options are the ones of the look; the equipment replaces the one of the creator.
    /// </summary>
    public bool ShowNpc(NpcLook look)
    {
        if (_catalog is null || _index is null) return false;
        _stash ??= ToSave();
        _npcLook = look;

        // The creator spec only gives a body to start from: any spec of the right gender will do.
        string gender = look.Gender == 'f' ? "female" : "male";
        var spec = _catalog.Find("trooper", gender, "human", false) ?? _catalog.Specs.FirstOrDefault(s => s.Gender == gender);
        if (spec is not null) SelectSpec(spec.Class, spec.Gender, spec.Race, spec.IsLegacy);

        _npcOptions = [];
        foreach (var slot in NpcOptionSlots)
            if (look.Part(slot) is { } part) _npcOptions[slot] = new CharacterOption(-1, slot, part.AssetId, part.MaterialId);

        _npcHead = null;
        _useNpcHead = false;
        _npcOriginalColors = false; // The look names its own skin, hair and eye colors.
        if (look.Part(AppearanceSlot.Head) is { } head && _index.Appearances.FindAsset(head.AssetId) is { Slot: "head" } found)
        {
            string material = head.MaterialId.ToString(CultureInfo.InvariantCulture);
            _npcHead = new NpcHead(found.Asset, found.Asset.Materials.FirstOrDefault(m => m.Id == material) ?? found.Asset.Materials.FirstOrDefault(), look.Gender);
            _useNpcHead = true;
        }

        _equipment.Clear();
        foreach (var (slot, folder) in NpcEquipment)
        {
            if (look.Part(slot) is not { } part || _index.Appearances.FindAsset(part.AssetId) is not { } item) continue;
            // The blank asset and the bare body assets mean "nothing": the bare body of the head is used for them.
            if (item.Asset.BaseFile.Length == 0 || item.Asset.ArtName.Contains("_naked_", StringComparison.Ordinal)) continue;
            string material = part.MaterialId.ToString(CultureInfo.InvariantCulture);
            _equipment[folder] = new EquipChoice(item.Asset)
            {
                Variant = Math.Max(0, item.Asset.Materials.ToList().FindIndex(m => m.Id == material)),
                PrimaryId = part.PrimaryPalette > 0 ? part.PrimaryPalette.ToString(CultureInfo.InvariantCulture) : null,
                SecondaryId = part.SecondaryPalette > 0 ? part.SecondaryPalette.ToString(CultureInfo.InvariantCulture) : null,
            };
        }
        Rebuild();
        return true;
    }

    /// <summary>Ends NPC mode and brings back the creator character. Does nothing in creator mode.</summary>
    public void LeaveNpc()
    {
        if (_npcLook is null) return;
        _npcLook = null;
        _npcOptions = null;
        var stash = _stash;
        _stash = null;
        if (stash is not null) Apply(stash);
        else Rebuild();
    }

    // Race of the bare body. In NPC mode the species written in the head name ("head_twilek_bfa_...") decides.
    private string BodyRace(AppearanceAsset? head)
    {
        if (_npcLook is null || head is null) return _spec!.Race;
        string[] parts = head.ArtName.Split('_');
        string species = parts.Length > 1 ? parts[1] : "";
        return species switch
        {
            "twilek" or "cathar" or "rattataki" => species,
            "sith" or "bloodsith" => "sith",
            _ => "human",
        };
    }
}
