using System.Globalization;
using Swtor.Assets;

namespace Swtor.App;

// Saving and loading the character as a CharacterSave.
public sealed partial class CharacterPanel
{
    private CharacterSave? _pendingSave;

    /// <summary>Message about the last failed load, or null.</summary>
    public string? LoadError { get; private set; }

    /// <summary>Describes the current character. Returns null until a class, gender and race are chosen.</summary>
    public CharacterSave? ToSave()
    {
        if (_spec is null) return null;
        var save = new CharacterSave { Class = _spec.Class, Gender = _spec.Gender, Race = _spec.Race, Legacy = _spec.IsLegacy };
        foreach (var (slot, key) in _selected)
        {
            if (_spec.Options.FirstOrDefault(o => o.Key == key) is { } option)
                save.Options[slot.ToString()] = new SavedOption(option.AssetId, option.MaterialId);
        }
        if (_useNpcHead && HeadOption() is { Key: -1 } npc) save.NpcHead = new SavedOption(npc.AssetId, npc.MaterialId);
        save.NpcOriginalColors = _npcOriginalColors;
        foreach (var (slot, choice) in _equipment)
        {
            var materials = choice.Asset.Materials;
            string? material = materials.Count > 0 ? materials[Math.Min(choice.Variant, materials.Count - 1)].Id : null;
            save.Equipment[slot] = new SavedEquipment(choice.Asset.Id, material, choice.SchemeId, choice.PrimaryId, choice.SecondaryId,
                ToValues(choice.PrimaryCustom), ToValues(choice.SecondaryCustom));
        }
        return save;
    }

    /// <summary>
    /// Restores a saved character. If the game database is not loaded yet, the save is applied when it is.
    /// Choices that no longer exist are skipped. Sets <see cref="LoadError"/> when the race or class is unknown.
    /// </summary>
    public void Apply(CharacterSave save)
    {
        LoadError = null;
        if (_catalog is null || _index is null)
        {
            _pendingSave = save;
            return;
        }
        if (_catalog.Find(save.Class, save.Gender, save.Race, save.Legacy) is null
            && _catalog.Find(save.Class, save.Gender, save.Race, !save.Legacy) is null)
        {
            LoadError = $"Unknown character type: {save.Class} / {save.Gender} / {save.Race}.";
            return;
        }

        SelectSpec(save.Class, save.Gender, save.Race, save.Legacy);

        // The head goes first: it decides which other options are allowed.
        var order = ListSlots.Concat(ColorSlots).ToList();
        foreach (var slot in order)
        {
            if (save.Options.TryGetValue(slot.ToString(), out var saved))
            {
                var allowed = Allowed(slot);
                var match = allowed.FirstOrDefault(o => o.AssetId == saved.AssetId && o.MaterialId == saved.MaterialId)
                    ?? allowed.FirstOrDefault(o => o.AssetId == saved.AssetId);
                if (match is not null) _selected[slot] = match.Key;
            }
            else if (OptionalSlots.Contains(slot))
            {
                _selected.Remove(slot);
            }
        }
        ReapplyCompatibility(); // Fixes choices that the new head does not allow.
        ApplyNpcHead(save);

        _equipment.Clear();
        foreach (var (slot, saved) in save.Equipment)
        {
            if (!long.TryParse(saved.AssetId, NumberStyles.Integer, CultureInfo.InvariantCulture, out long id)
                || _index.Appearances.FindAsset(id) is not { } found) continue;
            var choice = new EquipChoice(found.Asset)
            {
                Variant = Math.Max(0, found.Asset.Materials.ToList().FindIndex(m => m.Id == saved.MaterialId)),
                SchemeId = saved.SchemeId,
                PrimaryId = saved.PrimaryId,
                SecondaryId = saved.SecondaryId,
                PrimaryCustom = FromValues(saved.PrimaryCustom),
                SecondaryCustom = FromValues(saved.SecondaryCustom),
            };
            _equipment[slot] = choice;
        }
        Rebuild();
    }

    // Applies a save that came in before the game database was ready.
    // A custom color as saved: hue, saturation, brightness, contrast.
    private static float[]? ToValues(Swtor.Formats.Xml.Palette? custom) =>
        custom is null ? null : [custom.Hue, custom.Saturation, custom.Brightness, custom.Contrast];

    private static Swtor.Formats.Xml.Palette? FromValues(float[]? values) =>
        values is { Length: 4 } v ? Swtor.Formats.Dds.PaletteTint.Custom(v[0], v[1], v[2], v[3]) : null;

    private void ApplyPendingSave()
    {
        if (_pendingSave is not { } save) return;
        _pendingSave = null;
        Apply(save);
    }
}
