using System.Numerics;
using ImGuiNET;
using Microsoft.Xna.Framework.Graphics;
using Swtor.Assets;
using Swtor.Formats;
using Swtor.Formats.Gr2;
using Swtor.Formats.Xml;

namespace Swtor.App;

// Body and equipment: bare body parts by default, replaced by equipment assets that the user picks.
public sealed partial class CharacterPanel
{
    private sealed class EquipChoice(AppearanceAsset asset)
    {
        public AppearanceAsset Asset { get; } = asset;
        public int Variant { get; set; }
        public string? SchemeId { get; set; }
    }

    // Slots that can hold equipment. The first four show a bare body part when empty.
    private static readonly string[] EquipSlots = ["chest", "hand", "leg", "boot", "waist", "bracer"];
    private static readonly string[] NakedSlots = ["chest", "hand", "leg", "boot"];
    private const int MaxListed = 400;

    private readonly Dictionary<string, EquipChoice> _equipment = [];
    private readonly Dictionary<string, string> _equipFilter = [];
    private readonly Dictionary<(string Slot, string? Bodytype), List<AppearanceAsset>> _equipOptions = [];
    private string? _bodytype;

    // Adds the bare body or the equipment of each slot to the preview.
    private void AddBodyAndEquipment(char gender, string? bodytype, string? headName)
    {
        if (_index is null || _spec is null) return;
        _bodytype = bodytype;
        ApplyStartupEquipment();
        var skin = TintFor(AppearanceSlot.Head);

        foreach (string slot in EquipSlots)
        {
            ResolvedPart? part;
            Vector4? tint = null;
            Palette? primary = null, secondary = null;
            if (_equipment.TryGetValue(slot, out var choice))
            {
                var variant = choice.Asset.Materials.Count > 0 ? choice.Asset.Materials[Math.Min(choice.Variant, choice.Asset.Materials.Count - 1)] : null;
                part = PartResolver.ResolveAsset(_index, choice.Asset, variant?.Id, gender, bodytype);
                (primary, secondary) = SchemePalettes(choice.SchemeId, slot);
            }
            else if (NakedSlots.Contains(slot)
                && _index.Appearances.FindAsset(slot, NakedBody.AssetName(slot, _spec.Race, headName)) is { } naked)
            {
                part = PartResolver.ResolveAsset(_index, naked, null, gender, bodytype);
                tint = skin;
            }
            else
            {
                continue;
            }
            if (part is null) continue;

            var texture = LoadTexture(part, tint, primary, secondary);
            AddModel(part.ModelPath, texture, slot);
            foreach (string attachment in part.Attachments) AddModel(attachment, texture, slot);
        }
    }

    /// <summary>Equips assets by art name before the first display. Used for start-up arguments such as "--equip chest=chest_armor01_heavy_bh_a02".</summary>
    public void SetStartupEquipment(IEnumerable<(string Slot, string ArtName)> items)
    {
        _startupEquipment = items.ToList();
    }

    private List<(string Slot, string ArtName)>? _startupEquipment;

    private void ApplyStartupEquipment()
    {
        if (_startupEquipment is null || _index is null) return;
        foreach (var (slot, name) in _startupEquipment)
            if (_index.Appearances.FindAsset(slot, name) is { } asset) _equipment[slot] = new EquipChoice(asset);
        _startupEquipment = null;
    }

    private void AddModel(string modelPath, Texture2D? texture, string label)
    {
        try
        {
            _preview.Add(Gr2Reader.Parse(File.ReadAllBytes(_index!.FullPath(modelPath))), texture);
        }
        catch (Exception e) when (e is GameFormatException or IOException)
        {
            _error = $"{label}: {e.Message}";
        }
    }

    // The two palettes that a color scheme gives for a slot. Null when no scheme is chosen.
    private (Palette?, Palette?) SchemePalettes(string? schemeId, string slot)
    {
        if (schemeId is null || _index is null) return (null, null);
        var colors = _index.Colors;
        if (colors.FindScheme(schemeId) is not { } scheme || !scheme.Slots.TryGetValue(slot, out var palettes)) return (null, null);
        return (colors.ReadPalette(palettes.Primary), colors.ReadPalette(palettes.Secondary));
    }

    private void DrawEquipment()
    {
        if (_index is null) return;
        ImGui.Text("Equipment");
        foreach (string slot in EquipSlots)
        {
            ImGui.PushID(slot);
            DrawEquipSlot(slot);
            ImGui.PopID();
        }
    }

    private void DrawEquipSlot(string slot)
    {
        _equipment.TryGetValue(slot, out var choice);
        var options = EquipOptions(slot);
        ImGui.TextDisabled($"{slot} ({options.Count})");

        _equipFilter.TryGetValue(slot, out string? filter);
        filter ??= "";
        ImGui.SetNextItemWidth(110);
        if (ImGui.InputTextWithHint("##filter", "filter", ref filter, 64)) _equipFilter[slot] = filter;
        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##asset", choice?.Asset.ArtName ?? (NakedSlots.Contains(slot) ? "(bare)" : "(none)")))
        {
            if (ImGui.Selectable(NakedSlots.Contains(slot) ? "(bare)" : "(none)", choice is null))
            {
                _equipment.Remove(slot);
                Rebuild();
            }
            int listed = 0;
            foreach (var asset in options)
            {
                if (filter.Length > 0 && !asset.ArtName.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
                if (++listed > MaxListed)
                {
                    ImGui.TextDisabled("... use the filter");
                    break;
                }
                if (ImGui.Selectable($"{asset.ArtName}##{asset.Id}", asset == choice?.Asset))
                {
                    _equipment[slot] = new EquipChoice(asset);
                    Rebuild();
                }
            }
            ImGui.EndCombo();
        }

        if (choice is null) return;
        DrawVariantChoice(choice);
        DrawSchemeChoice(slot, choice);
    }

    private void DrawVariantChoice(EquipChoice choice)
    {
        var variants = choice.Asset.Materials;
        if (variants.Count < 2) return;
        ImGui.SetNextItemWidth(-1);
        if (!ImGui.BeginCombo("##variant", Path.GetFileNameWithoutExtension(variants[choice.Variant].FileName))) return;
        for (int i = 0; i < variants.Count; i++)
        {
            if (!ImGui.Selectable($"{Path.GetFileNameWithoutExtension(variants[i].FileName)}##{i}", i == choice.Variant)) continue;
            choice.Variant = i;
            choice.SchemeId = null;
            Rebuild();
        }
        ImGui.EndCombo();
    }

    private void DrawSchemeChoice(string slot, EquipChoice choice)
    {
        var variants = choice.Asset.Materials;
        if (variants.Count == 0 || _index is null) return;
        var ids = variants[Math.Min(choice.Variant, variants.Count - 1)].ColorSchemeIds;
        if (ids.Count == 0) return;

        var colors = _index.Colors;
        ImGui.SetNextItemWidth(-1);
        if (!ImGui.BeginCombo("##scheme", choice.SchemeId is null ? "(default colors)" : colors.FindScheme(choice.SchemeId)?.Name ?? choice.SchemeId)) return;
        if (ImGui.Selectable("(default colors)", choice.SchemeId is null))
        {
            choice.SchemeId = null;
            Rebuild();
        }
        foreach (string id in ids)
        {
            if (colors.FindScheme(id) is not { } scheme || !ImGui.Selectable($"{scheme.Name}##{id}", id == choice.SchemeId)) continue;
            choice.SchemeId = id;
            Rebuild();
        }
        ImGui.EndCombo();
    }

    // Assets of a slot that have a model for the current body type. The list is built once per body type.
    private List<AppearanceAsset> EquipOptions(string slot)
    {
        var key = (slot, _bodytype);
        if (_equipOptions.TryGetValue(key, out var cached)) return cached;

        char gender = _spec?.Gender == "female" ? 'f' : 'm';
        var list = _index!.Appearances.AssetsOfSlot(slot)
            .Where(a => a.BaseFile.Length > 0 && !a.ArtName.Contains("_naked_", StringComparison.Ordinal) && PartResolver.HasModel(_index, a, gender, _bodytype))
            .OrderBy(a => a.ArtName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return _equipOptions[key] = list;
    }
}
