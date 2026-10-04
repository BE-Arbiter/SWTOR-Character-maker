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
        if (ImGui.BeginCombo("##armorclass", _classFilter is null ? "All classes" : ArmorClassName(_classFilter)))
        {
            if (ImGui.Selectable("All classes", _classFilter is null)) _classFilter = null;
            foreach (string code in ArmorInfo.Classes.Keys)
                if (ImGui.Selectable(ArmorClassName(code), code == _classFilter)) _classFilter = code;
            ImGui.EndCombo();
        }
    }

    // Class name in the game language. The generic look has no game text.
    private string ArmorClassName(string code) =>
        ArmorInfo.ClassKey(code) is { } key ? _text.Class(key) : ArmorInfo.Classes[code];

    private bool PassesArmorFilters(EquipEntry entry) =>
        (_weightFilter == ArmorWeight.Unknown || entry.Armor.Weight == _weightFilter)
        && (_classFilter is null || entry.Armor.ClassCode == _classFilter);

    private void DrawEquipSlot(string slot)
    {
        _equipment.TryGetValue(slot, out var choice);
        var options = EquipOptions(slot);
        bool filtered = _weightFilter != ArmorWeight.Unknown || _classFilter is not null;
        string slotName = _text.EquipSlot(slot);
        ImGui.TextDisabled(filtered ? $"{slotName} ({options.Count(PassesArmorFilters)} / {options.Count})" : $"{slotName} ({options.Count})");

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
        DrawDyeChoice(slot, choice);
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

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Numerics.Vector4> _swatches = new();

    /// <summary>
    /// Reads all garment palettes and computes their swatches on a background thread, so the color picker opens at once.
    /// Call once when the asset index is ready.
    /// </summary>
    public void PrecachePalettes()
    {
        if (_index is not { } index) return;
        Task.Run(() =>
        {
            foreach (var entry in index.Colors.Palettes) Swatch(index, entry.Id);
        });
    }

    // Free choice of the two colors of an item, like the dye modules of the game. The palettes are the garment palettes of the game.
    private void DrawDyeChoice(string slot, EquipChoice choice)
    {
        if (_index is null) return;
        if (!choice.HasMask)
        {
            ImGui.TextDisabled("This item has no color mask.");
            return;
        }
        string? primary = choice.PrimaryId, secondary = choice.SecondaryId;
        bool changed = DrawPalettePicker("Primary", slot, ref primary) | DrawPalettePicker("Secondary", slot, ref secondary);
        if (!changed) return;
        choice.PrimaryId = primary;
        choice.SecondaryId = secondary;
        Rebuild();
    }

    // A swatch button that opens a grid of all palettes. Returns true when the choice changed. A null id means "from the scheme".
    private bool DrawPalettePicker(string label, string slot, ref string? id)
    {
        var colors = _index!.Colors;
        string popup = "##palette" + label;
        string current = id is null ? "(scheme)" : PaletteLabel(colors.FindPaletteEntry(id)?.Name ?? id);

        ImGui.AlignTextToFramePadding();
        ImGui.Text(label);
        ImGui.SameLine(80);
        if (id is not null) ImGui.ColorButton("##current" + label, Swatch(_index, id), 0, new System.Numerics.Vector2(20, 20));
        else ImGui.Dummy(new System.Numerics.Vector2(20, 20));
        ImGui.SameLine();
        if (ImGui.Button($"{current}##open{label}", new System.Numerics.Vector2(-1, 0))) ImGui.OpenPopup(popup);

        bool changed = false;
        if (!ImGui.BeginPopup(popup)) return false;
        if (ImGui.Selectable("(from scheme)", id is null))
        {
            id = null;
            changed = true;
        }

        // Three groups: the colors already used on this character, the dyes, then all the others.
        var inUse = PalettesInUse().Select(colors.FindPaletteEntry).OfType<PaletteEntry>().ToList();
        var used = new HashSet<string>(inUse.Select(p => p.Id));
        var dyes = colors.Palettes.Where(p => !used.Contains(p.Id) && IsDye(p)).ToList();
        var others = colors.Palettes.Where(p => !used.Contains(p.Id) && !IsDye(p)).ToList();
        changed |= DrawPaletteGroup("Colors in use", inUse, ref id);
        changed |= DrawPaletteGroup("Dyes", dyes, ref id);
        changed |= DrawPaletteGroup("Other colors", others, ref id);

        if (changed) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
        return changed;
    }

    // A subtitle and a grid of swatches. Returns true when a swatch was clicked.
    private bool DrawPaletteGroup(string title, IReadOnlyList<PaletteEntry> entries, ref string? id)
    {
        if (entries.Count == 0) return false;
        ImGui.Spacing();
        ImGui.TextColored(new System.Numerics.Vector4(0.6f, 0.75f, 1f, 1f), $"{title} ({entries.Count})");
        ImGui.Separator();
        const int perRow = 14;
        bool changed = false;
        for (int i = 0; i < entries.Count; i++)
        {
            if (i % perRow != 0) ImGui.SameLine();
            var entry = entries[i];
            if (ImGui.ColorButton($"##{title}{entry.Id}", Swatch(_index!, entry.Id), 0, new System.Numerics.Vector2(22, 22)))
            {
                id = entry.Id;
                changed = true;
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(PaletteLabel(entry.Name));
        }
        return changed;
    }

    // Palette ids that the equipped items use now: the colors picked by hand and the colors of the chosen schemes.
    private IEnumerable<string> PalettesInUse()
    {
        var ids = new List<string>();
        foreach (var (slot, choice) in _equipment)
        {
            if (choice.SchemeId is not null && _index!.Colors.FindScheme(choice.SchemeId) is { } scheme && scheme.Slots.TryGetValue(slot, out var pair))
            {
                ids.Add(pair.Primary);
                ids.Add(pair.Secondary);
            }
            if (choice.PrimaryId is not null) ids.Add(choice.PrimaryId);
            if (choice.SecondaryId is not null) ids.Add(choice.SecondaryId);
        }
        return ids.Distinct();
    }

    private static bool IsDye(PaletteEntry palette) => palette.Name.Contains("_dye_", StringComparison.OrdinalIgnoreCase);

    // The game has no names for single palettes in the extract. "garmenthue_dye_h35_p" becomes "dye h35 p".
    private static string PaletteLabel(string name) =>
        (name.StartsWith("garmenthue_", StringComparison.OrdinalIgnoreCase) ? name["garmenthue_".Length..] : name).Replace('_', ' ');

    // Color shown for a palette (what it gives to a mid-grey pixel). The palette files are read once.
    private System.Numerics.Vector4 Swatch(AssetIndex index, string id)
    {
        if (_swatches.TryGetValue(id, out var cached)) return cached;
        var color = new System.Numerics.Vector4(0.5f, 0.5f, 0.5f, 1);
        if (index.Colors.ReadPalette(id) is { } palette)
        {
            var (r, g, b) = Swtor.Formats.Dds.PaletteTint.Swatch(palette);
            color = new System.Numerics.Vector4(r, g, b, 1);
        }
        return _swatches[id] = color;
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
            choice.PrimaryId = choice.SecondaryId = null;
            Rebuild();
        }
        foreach (string id in ids)
        {
            if (colors.FindScheme(id) is not { } scheme || !ImGui.Selectable($"{scheme.Name}##{id}", id == choice.SchemeId)) continue;
            choice.SchemeId = id;
            choice.PrimaryId = choice.SecondaryId = null;
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
