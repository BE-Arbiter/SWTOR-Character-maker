using System.Globalization;
using ImGuiNET;
using Swtor.Assets;
using Swtor.Formats.Xml;

namespace Swtor.App;

// Equipment lists: item names, sorting and filtering.
public sealed partial class CharacterPanel
{
    private enum EquipSort { Name, ArtName, Level, Quality }

    // One asset in an equipment list with the items that show it (best first).
    private sealed record EquipEntry(AppearanceAsset Asset, IReadOnlyList<ItemInfo> Items, string SearchText, ArmorInfo Armor);

    private readonly Dictionary<(string Slot, string? Bodytype, EquipSort Sort), List<EquipEntry>> _equipOptions = [];
    private EquipSort _equipSort = EquipSort.Name;
    private ItemCatalog? _items;
    private ArmorWeight _weightFilter = ArmorWeight.Unknown; // Unknown means all weights.
    private string? _classFilter;                           // Null means all classes.

    /// <summary>Gives the panel the item names. Until then the lists show art names only.</summary>
    public void SetItems(ItemCatalog items)
    {
        _items = items;
        _equipOptions.Clear();
    }

    private void DrawEquipment()
    {
        if (_index is null) return;
        ImGui.Text("Equipment");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##sort", $"Sort: {SortLabel(_equipSort)}"))
        {
            foreach (var mode in Enum.GetValues<EquipSort>())
                if (ImGui.Selectable(SortLabel(mode), mode == _equipSort)) _equipSort = mode;
            ImGui.EndCombo();
        }
        DrawArmorFilters();
        if (_items is null) ImGui.TextDisabled("Item names are loading...");

        foreach (string slot in EquipSlots)
        {
            ImGui.PushID(slot);
            DrawEquipSlot(slot);
            ImGui.PopID();
        }
    }

    // Weight and class filters. They apply to every slot. The weight and the class come from the art name (see ArmorInfo).
    private void DrawArmorFilters()
    {
        ImGui.SetNextItemWidth(110);
        if (ImGui.BeginCombo("##weight", _weightFilter == ArmorWeight.Unknown ? "All weights" : _weightFilter.ToString()))
        {
            if (ImGui.Selectable("All weights", _weightFilter == ArmorWeight.Unknown)) _weightFilter = ArmorWeight.Unknown;
            foreach (var weight in new[] { ArmorWeight.Light, ArmorWeight.Medium, ArmorWeight.Heavy })
                if (ImGui.Selectable(weight.ToString(), weight == _weightFilter)) _weightFilter = weight;
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##armorclass", _classFilter is null ? "All classes" : ArmorInfo.Classes[_classFilter]))
        {
            if (ImGui.Selectable("All classes", _classFilter is null)) _classFilter = null;
            foreach (var (code, name) in ArmorInfo.Classes)
                if (ImGui.Selectable(name, code == _classFilter)) _classFilter = code;
            ImGui.EndCombo();
        }
    }

    private bool PassesArmorFilters(EquipEntry entry) =>
        (_weightFilter == ArmorWeight.Unknown || entry.Armor.Weight == _weightFilter)
        && (_classFilter is null || entry.Armor.ClassCode == _classFilter);

    private void DrawEquipSlot(string slot)
    {
        _equipment.TryGetValue(slot, out var choice);
        var options = EquipOptions(slot);
        bool filtered = _weightFilter != ArmorWeight.Unknown || _classFilter is not null;
        ImGui.TextDisabled(filtered ? $"{slot} ({options.Count(PassesArmorFilters)} of {options.Count})" : $"{slot} ({options.Count})");

        _equipFilter.TryGetValue(slot, out string? filter);
        filter ??= "";
        ImGui.SetNextItemWidth(110);
        if (ImGui.InputTextWithHint("##filter", "name or art", ref filter, 64)) _equipFilter[slot] = filter;
        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1);
        string none = NakedSlots.Contains(slot) ? "(bare)" : "(none)";
        if (ImGui.BeginCombo("##asset", choice is null ? none : EntryLabel(MakeEntry(choice.Asset))))
        {
            if (ImGui.Selectable(none, choice is null))
            {
                _equipment.Remove(slot);
                Rebuild();
            }
            int listed = 0;
            foreach (var entry in options)
            {
                if (filter.Length > 0 && !entry.SearchText.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
                if (!PassesArmorFilters(entry)) continue;
                if (++listed > MaxListed)
                {
                    ImGui.TextDisabled("... use the filter");
                    break;
                }
                if (ImGui.Selectable($"{EntryLabel(entry)}##{entry.Asset.Id}", entry.Asset == choice?.Asset))
                {
                    _equipment[slot] = new EquipChoice(entry.Asset);
                    Rebuild();
                }
                if (ImGui.IsItemHovered() && entry.Items.Count > 0) ImGui.SetTooltip(ItemsTooltip(entry));
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
        if (!ImGui.BeginCombo("##variant", VariantLabel(choice.Asset, variants[choice.Variant]))) return;
        for (int i = 0; i < variants.Count; i++)
        {
            if (!ImGui.Selectable($"{VariantLabel(choice.Asset, variants[i])}##{i}", i == choice.Variant)) continue;
            choice.Variant = i;
            choice.SchemeId = null;
            Rebuild();
        }
        ImGui.EndCombo();
    }

    // Material file name, with the name of an item that uses this variant when there is one.
    private string VariantLabel(AppearanceAsset asset, AssetMaterial variant)
    {
        string file = Path.GetFileNameWithoutExtension(variant.FileName);
        var item = _items?.ForAsset(long.Parse(asset.Id, CultureInfo.InvariantCulture))
            .FirstOrDefault(i => i.MaterialId.ToString(CultureInfo.InvariantCulture) == variant.Id);
        return item is null ? file : $"{file} - {item.Name}";
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

    // Assets of a slot that have a model for the current body type, with their item names, in the chosen order.
    // The list is built once per slot, body type and sort order.
    private List<EquipEntry> EquipOptions(string slot)
    {
        var key = (slot, _bodytype, _equipSort);
        if (_equipOptions.TryGetValue(key, out var cached)) return cached;

        char gender = _spec?.Gender == "female" ? 'f' : 'm';
        var entries = _index!.Appearances.AssetsOfSlot(slot)
            .Where(a => a.BaseFile.Length > 0 && !a.ArtName.Contains("_naked_", StringComparison.Ordinal) && PartResolver.HasModel(_index, a, gender, _bodytype))
            .Select(MakeEntry)
            .ToList();

        // Entries without an item name go last in every order except art name.
        IOrderedEnumerable<EquipEntry> ordered = _equipSort switch
        {
            EquipSort.ArtName => entries.OrderBy(e => e.Asset.ArtName, StringComparer.OrdinalIgnoreCase),
            EquipSort.Level => entries.OrderByDescending(e => e.Items.Count > 0 ? e.Items.Max(i => i.Level) : -1).ThenBy(e => e.Asset.ArtName, StringComparer.OrdinalIgnoreCase),
            EquipSort.Quality => entries.OrderByDescending(e => e.Items.Count > 0 ? e.Items.Max(i => i.Quality) : -1).ThenBy(e => e.Asset.ArtName, StringComparer.OrdinalIgnoreCase),
            _ => entries.OrderBy(e => e.Items.Count == 0).ThenBy(e => e.Items.Count > 0 ? e.Items[0].Name : e.Asset.ArtName, StringComparer.CurrentCultureIgnoreCase),
        };
        return _equipOptions[key] = ordered.ToList();
    }

    private EquipEntry MakeEntry(AppearanceAsset asset)
    {
        var items = _items?.ForAsset(long.Parse(asset.Id, CultureInfo.InvariantCulture)) ?? [];
        string search = asset.ArtName + " " + string.Join(' ', items.Select(i => i.Name));
        return new EquipEntry(asset, items, search, ArmorInfo.Parse(asset.ArtName));
    }

    // Item name with the number of other items, then the art name. Just the art name when no item shows the asset.
    private static string EntryLabel(EquipEntry entry) =>
        entry.Items.Count == 0
            ? entry.Asset.ArtName
            : $"{entry.Items[0].Name}{(entry.Items.Count > 1 ? $" (+{entry.Items.Count - 1})" : "")}  [{entry.Asset.ArtName}]";

    private static string ItemsTooltip(EquipEntry entry)
    {
        var lines = entry.Items.Take(10).Select(i => $"{i.Name}  (level {i.Level}, {QualityText(i.QualityName)})").ToList();
        if (entry.Items.Count > 10) lines.Add($"... {entry.Items.Count - 10} more");
        return string.Join('\n', lines);
    }

    // "itmQualityArtifact" becomes "Artifact".
    private static string QualityText(string name) =>
        name.StartsWith("itmQuality", StringComparison.Ordinal) ? name["itmQuality".Length..] : name;

    private static string SortLabel(EquipSort mode) => mode switch
    {
        EquipSort.Name => "item name",
        EquipSort.ArtName => "art name",
        EquipSort.Level => "item level (high first)",
        _ => "quality (high first)",
    };
}
