using System.Globalization;
using ImGuiNET;
using Swtor.Assets;
using Swtor.Formats.Xml;

namespace Swtor.App;

// NPC heads: any head of art/dynamic/head/index.xml, also the ones that the character creator does not offer
// (Lana, Theron, trandoshans...). It replaces the head of the creator. The creator head stays selected: it still
// decides which hair and other options are allowed.
public sealed partial class CharacterPanel
{
    private sealed record NpcHead(AppearanceAsset Asset, AssetMaterial? Material, char Gender);

    private NpcHead? _npcHead;
    private bool _useNpcHead;

    // True keeps the colors of the NPC head files: no skin color, eye color, complexion or face paint.
    private bool _npcOriginalColors = true;
    private const int MaxNpcListed = 6000;
    private string _npcFilter = "";
    private List<NpcHead>? _npcHeads;

    // The head that the preview shows: the NPC head when one is used, otherwise the head of the creator.
    private CharacterOption? HeadOption() =>
        _useNpcHead && _npcHead is { } npc && long.TryParse(npc.Asset.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out long id)
            ? new CharacterOption(-1, AppearanceSlot.Head, id,
                long.TryParse(npc.Material?.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out long material) ? material : 0)
            : OptionFor(AppearanceSlot.Head);

    // True when the colors of the creator must not be applied to the head and the bare body.
    private bool KeepNpcColors => _useNpcHead && _npcHead is not null && _npcOriginalColors;

    // All usable heads (one entry per material, and per gender when the asset has both), sorted by name.
    // Materials named "DO NOT USE" are placeholders of the artists: they are hidden.
    private List<NpcHead> NpcHeads()
    {
        if (_npcHeads is not null) return _npcHeads;
        var list = new List<NpcHead>();
        foreach (var asset in _index!.Appearances.AssetsOfSlot("head"))
        {
            if (!asset.BaseFile.EndsWith(".gr2", StringComparison.OrdinalIgnoreCase)) continue;
            var materials = asset.Materials.Where(m => !IsDoNotUse(m)).ToList();
            if (asset.Materials.Count > 0 && materials.Count == 0) continue;
            foreach (char gender in HeadGenders(asset))
            {
                if (!PartResolver.HasModel(_index, asset, gender, null)) continue;
                if (materials.Count == 0) list.Add(new NpcHead(asset, null, gender));
                foreach (var material in materials) list.Add(new NpcHead(asset, material, gender));
            }
        }
        list.Sort((a, b) => string.CompareOrdinal(NpcLabel(a), NpcLabel(b)));
        return _npcHeads = list;
    }

    private static bool IsDoNotUse(AssetMaterial material) => material.Name.Contains("do not use", StringComparison.OrdinalIgnoreCase);

    // Genders of the body types of a head: the second letter of the code ("bfa" -> 'f'). 'm' when there is none.
    private static List<char> HeadGenders(AppearanceAsset asset)
    {
        var genders = new List<char>();
        foreach (string bodytype in asset.Bodytypes)
            {
            char g = bodytype.Length >= 2 ? char.ToLowerInvariant(bodytype[1]) : ' ';
            if (g is 'f' or 'm' && !genders.Contains(g)) genders.Add(g);
        }
        if (genders.Count == 0) genders.Add('m');
        return genders;
    }

    private static string NpcLabel(NpcHead head)
    {
        string name = head.Material is null || head.Asset.Materials.Count < 2 ? head.Asset.ArtName : $"{head.Asset.ArtName} / {head.Material.Name}";
        return HeadGenders(head.Asset).Count > 1 ? $"{name} ({head.Gender})" : name;
    }

    // Check box, filter and list of NPC heads, drawn after the head list of the creator.
    private void DrawNpcHeadChoice()
    {
        bool changed = ImGui.Checkbox("NPC head", ref _useNpcHead);
        if (!_useNpcHead)
        {
            if (changed) Rebuild();
            return;
        }
        ImGui.SameLine();
        changed |= ImGui.Checkbox("Original colors", ref _npcOriginalColors);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Keep the skin and eye colors of the NPC files. Skin color, eye color, complexion and face paint are not applied.");

        var heads = NpcHeads();
        if (_npcHead is not null && !heads.Contains(_npcHead)) _npcHead = null;
        string filter = _npcFilter.Trim();
        var shown = filter.Length == 0 ? heads : heads.Where(h => NpcLabel(h).Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        if (_npcHead is null && shown.Count > 0)
        {
            _npcHead = shown[0];
            changed = true;
        }

        ImGui.SetNextItemWidth(110);
        ImGui.InputTextWithHint("##npcfilter", "name", ref _npcFilter, 64);
        ImGui.SameLine();
        int current = _npcHead is null ? -1 : shown.IndexOf(_npcHead);
        int step = Stepper.Before("npchead", 0, 0);
        if (ImGui.BeginCombo("##npchead", _npcHead is null ? "(none)" : NpcLabel(_npcHead)))
        {
            for (int i = 0; i < shown.Count && i < MaxNpcListed; i++)
            {
                if (!ImGui.Selectable($"{NpcLabel(shown[i])}##npc{i}", i == current)) continue;
                _npcHead = shown[i];
                changed = true;
            }
            if (shown.Count > MaxNpcListed) ImGui.TextDisabled($"{shown.Count - MaxNpcListed} more, use the filter");
            ImGui.EndCombo();
        }
        step += Stepper.After("npchead");
        if (step != 0 && shown.Count > 0)
        {
            _npcHead = shown[Stepper.Move(shown.Count, current, step, false)];
            changed = true;
        }
        ImGui.TextDisabled($"{shown.Count} heads");
        if (changed) Rebuild();
    }

    // Restores the NPC head of a save. A missing asset turns the NPC head off.
    private void ApplyNpcHead(CharacterSave save)
    {
        _npcHead = null;
        _useNpcHead = false;
        _npcOriginalColors = save.NpcOriginalColors;
        if (save.NpcHead is not { } saved || _index!.Appearances.FindAsset(saved.AssetId) is not { Slot: "head" } found) return;
        string material = saved.MaterialId.ToString(CultureInfo.InvariantCulture);
        var genders = HeadGenders(found.Asset);
        char own = _gender == "female" ? 'f' : 'm';
        _npcHead = new NpcHead(found.Asset, found.Asset.Materials.FirstOrDefault(m => m.Id == material) ?? found.Asset.Materials.FirstOrDefault(),
            genders.Contains(own) ? own : genders[0]);
        _useNpcHead = true;
    }
}
