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
        DrawSortChoice();
        DrawArmorFilters();
        if (_items is null) ImGui.TextDisabled("Item names are loading...");

        foreach (string slot in EquipSlots)
        {
            ImGui.PushID(slot);
            DrawEquipSlot(slot);
            ImGui.PopID();
        }
    }

    private void DrawSortChoice()
    {
        ImGui.SetNextItemWidth(-1);
        if (!ImGui.BeginCombo("##sort", $"Sort: {SortLabel(_equipSort)}")) return;
        foreach (var mode in Enum.GetValues<EquipSort>())
            if (ImGui.Selectable(SortLabel(mode), mode == _equipSort)) _equipSort = mode;
        ImGui.EndCombo();
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
        string none = NakedSlots.Contains(slot) ? "(bare)" : "(none)";
        int step = Stepper.Before("asset", 0, 28);
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
        step += Stepper.After("asset");
        ImGui.SameLine(0, ImGui.GetStyle().ItemSpacing.X / 2);
        if (ImGui.Button("...##gallery", new System.Numerics.Vector2(22, 0))) _galleryRequest = slot;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Browse with icons");
        if (step != 0) StepEquipment(slot, choice, options, filter, step);

        if (slot == "face" && choice is not null && ImGui.Checkbox("Hide hair", ref _hideHairUnderHelmet)) Rebuild();
        if (choice is not null && HasHood(choice.Asset))
        {
            bool helmet = _equipment.ContainsKey("face");
            if (helmet) ImGui.BeginDisabled();
            if (ImGui.Checkbox(helmet ? "Hood up (not with a helmet)" : "Hood up", ref _hoodUp)) Rebuild();
            if (helmet) ImGui.EndDisabled();
            // The helmet slot has its own check box. Both change the same setting.
            if (HoodShownUp())
            {
                ImGui.SameLine();
                if (ImGui.Checkbox("Hide hair##hood", ref _hideHairUnderHelmet)) Rebuild();
            }
        }
        if (choice is null) return;
        DrawVariantChoice(choice);
        DrawSchemeChoice(slot, choice);
        DrawDyeChoice(slot, choice);
    }

    // The entries that pass the name filter and the weight and class filters.
    private List<EquipEntry> FilterEntries(List<EquipEntry> options, string filter) =>
        options.Where(e => (filter.Length == 0 || e.SearchText.Contains(filter, StringComparison.OrdinalIgnoreCase)) && PassesArmorFilters(e)).ToList();

    // Goes to the previous or next item of the list as the filters show it. Past the ends comes the empty slot.
    private void StepEquipment(string slot, EquipChoice? choice, List<EquipEntry> options, string filter, int step)
    {
        var shown = FilterEntries(options, filter);
        int at = Stepper.Move(shown.Count, choice is null ? -1 : shown.FindIndex(e => e.Asset == choice.Asset), step, true);
        if (at < 0) _equipment.Remove(slot);
        else _equipment[slot] = new EquipChoice(shown[at].Asset);
        Rebuild();
    }

    private void DrawVariantChoice(EquipChoice choice)
    {
        var variants = choice.Asset.Materials;
        if (variants.Count < 2) return;
        int step = Stepper.Before("variant");
        int picked = -1;
        if (ImGui.BeginCombo("##variant", VariantLabel(choice.Asset, variants[choice.Variant])))
        {
            for (int i = 0; i < variants.Count; i++)
                if (ImGui.Selectable($"{VariantLabel(choice.Asset, variants[i])}##{i}", i == choice.Variant)) picked = i;
            ImGui.EndCombo();
        }
        step += Stepper.After("variant");
        if (step != 0) picked = Stepper.Move(variants.Count, choice.Variant, step, false);
        if (picked < 0) return;
        choice.Variant = picked;
        choice.SchemeId = null;
        Rebuild();
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
        var (schemePrimary, schemeSecondary) = SchemePalettes(choice.SchemeId, slot);
        string? primary = choice.PrimaryId, secondary = choice.SecondaryId;
        Palette? primaryCustom = choice.PrimaryCustom, secondaryCustom = choice.SecondaryCustom;
        bool rebuild = DrawPalettePicker("Primary", slot, ref primary, ref primaryCustom, schemePrimary)
            | DrawPalettePicker("Secondary", slot, ref secondary, ref secondaryCustom, schemeSecondary);
        // Custom values change while a slider moves (the swatch follows). The texture is only rebuilt when the slider is released.
        choice.PrimaryId = primary;
        choice.SecondaryId = secondary;
        choice.PrimaryCustom = primaryCustom;
        choice.SecondaryCustom = secondaryCustom;
        if (rebuild) Rebuild();
    }

    // A swatch button that opens a grid of colors. Returns true when the texture must be rebuilt. A null id and a null custom color
    // mean "from the scheme". The grid has two groups: the colors of the item (its own color schemes) and the dyes. Other palettes
    // would not suit the item. Below them, a custom color has the four values of the game shader.
    // <paramref name="schemePalette"/> is the palette of the scheme, the start of a new custom color.
    private bool DrawPalettePicker(string label, string slot, ref string? id, ref Palette? custom, Palette? schemePalette)
    {
        var colors = _index!.Colors;
        string popup = "##palette" + label;
        string current = custom is not null ? $"Custom ({DescribeColor(CustomSwatch(custom))})" : id is null ? "(scheme)" : ColorLabel(id);

        ImGui.AlignTextToFramePadding();
        ImGui.Text(label);
        ImGui.SameLine(80);
        if (custom is not null) ImGui.ColorButton("##current" + label, CustomSwatch(custom), 0, new System.Numerics.Vector2(20, 20));
        else if (id is not null) ImGui.ColorButton("##current" + label, Swatch(_index, id), 0, new System.Numerics.Vector2(20, 20));
        else ImGui.Dummy(new System.Numerics.Vector2(20, 20));
        ImGui.SameLine();
        if (ImGui.Button($"{current}##open{label}", new System.Numerics.Vector2(-1, 0))) ImGui.OpenPopup(popup);

        if (!ImGui.BeginPopup(popup)) return false;
        bool picked = false;
        if (ImGui.Selectable("(from scheme)", id is null && custom is null))
        {
            id = null;
            picked = true;
        }

        var own = _equipment.TryGetValue(slot, out var choice) ? OwnColors(slot, choice.Asset) : [];
        var taken = new HashSet<string>(own.Select(p => p.Id));
        var dyes = DyeColors().Where(p => !taken.Contains(p.Id)).ToList();
        string? shownId = custom is null ? id : null;
        if (DrawPaletteGroup("Colors of this item", own, ref shownId) | DrawPaletteGroup("Dyes", dyes, ref shownId))
        {
            id = shownId;
            picked = true;
        }
        if (picked)
        {
            custom = null;
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return true;
        }

        string? chosen = id;
        bool rebuild = DrawCustomColor(label, ref custom, () => (chosen is null ? null : colors.ReadPalette(chosen)) ?? schemePalette);
        if (rebuild && custom is not null) id = null;
        ImGui.EndPopup();
        return rebuild;
    }

    // The custom color part of a palette picker: a button that starts one from the current color, then four sliders with the values of
    // the game shader. The sliders show "Saturation" the usual way round (the game value is 1 - saturation).
    // Returns true when the texture must be rebuilt: a slider was released, or the custom color was made or removed.
    private bool DrawCustomColor(string label, ref Palette? custom, Func<Palette?> current)
    {
        ImGui.Spacing();
        ImGui.TextColored(new System.Numerics.Vector4(0.6f, 0.75f, 1f, 1f), "Custom color");
        ImGui.Separator();
        if (custom is null)
        {
            if (!ImGui.Button($"Make a custom color from the current one##custom{label}")) return false;
            var start = current() ?? Swtor.Formats.Dds.PaletteTint.Custom(0f, 0.5f, 0f, 1f);
            custom = Swtor.Formats.Dds.PaletteTint.Custom(start.Hue, start.Saturation, start.Brightness, start.Contrast);
            return true;
        }

        float hue = custom.Hue * 360f, saturation = 1f - custom.Saturation, brightness = custom.Brightness, contrast = custom.Contrast;
        bool edited = false, released = false;
        ImGui.ColorButton($"##customswatch{label}", CustomSwatch(custom), 0, new System.Numerics.Vector2(60, 60));
        ImGui.SameLine();
        ImGui.BeginGroup();
        ImGui.SetNextItemWidth(220);
        edited |= ImGui.SliderFloat($"Hue##{label}", ref hue, 0f, 360f, "%.0f");
        released |= ImGui.IsItemDeactivatedAfterEdit();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Turns the colors of the item around the color wheel (degrees).");
        ImGui.SetNextItemWidth(220);
        edited |= ImGui.SliderFloat($"Saturation##{label}", ref saturation, 0f, 1f, "%.2f");
        released |= ImGui.IsItemDeactivatedAfterEdit();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("0 is grey, 1 is full color.");
        ImGui.SetNextItemWidth(220);
        edited |= ImGui.SliderFloat($"Brightness##{label}", ref brightness, -1f, 1f, "%.2f");
        released |= ImGui.IsItemDeactivatedAfterEdit();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Neutral 0. Higher is lighter (white near 1), lower is darker.");
        ImGui.SetNextItemWidth(220);
        edited |= ImGui.SliderFloat($"Contrast##{label}", ref contrast, 0f, 3f, "%.2f");
        released |= ImGui.IsItemDeactivatedAfterEdit();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Neutral 1. 0 gives a flat color. High values keep light highlights on worn areas (the game black uses 3).");
        ImGui.EndGroup();
        if (edited) custom = Swtor.Formats.Dds.PaletteTint.Custom(hue / 360f, 1f - saturation, brightness, contrast);

        if (ImGui.Button($"Remove the custom color##custom{label}"))
        {
            custom = null;
            return true;
        }
        return released;
    }

    private static System.Numerics.Vector4 CustomSwatch(Palette custom)
    {
        var (r, g, b) = Swtor.Formats.Dds.PaletteTint.Swatch(custom);
        return new System.Numerics.Vector4(r, g, b, 1);
    }

    private static string DescribeColor(System.Numerics.Vector4 c) => Swtor.Formats.Dds.ColorNames.Describe(c.X, c.Y, c.Z);

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
            bool selected = entry.Id == id;
            if (selected) ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 2f);
            if (ImGui.ColorButton($"##{title}{entry.Id}", Swatch(_index!, entry.Id), 0, new System.Numerics.Vector2(22, 22)))
            {
                id = entry.Id;
                changed = true;
            }
            if (selected) ImGui.PopStyleVar();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(ColorLabel(entry.Id));
        }
        return changed;
    }

    // The palettes of the color schemes of an item, for its slot: what the game gives to this item. Sorted by color.
    private List<PaletteEntry> OwnColors(string slot, AppearanceAsset asset)
    {
        var colors = _index!.Colors;
        var ids = new HashSet<string>();
        foreach (var material in asset.Materials)
            foreach (string schemeId in material.ColorSchemeIds)
                if (colors.FindScheme(schemeId) is { } scheme && scheme.Slots.TryGetValue(slot, out var pair))
                {
                    ids.Add(pair.Primary);
                    ids.Add(pair.Secondary);
                }
        return SortedByColor(ids.Select(colors.FindPaletteEntry).OfType<PaletteEntry>().Where(IsRealColor));
    }

    // The dyes of the game: palettes that any item can take. The filler palettes are left out. Colors that look the same are listed once.
    private List<PaletteEntry> DyeColors()
    {
        if (_dyes is not null) return _dyes;
        var seen = new HashSet<(int, int, int)>();
        var list = new List<PaletteEntry>();
        foreach (var palette in SortedByColor(_index!.Colors.Palettes.Where(p => IsDye(p) && IsRealColor(p))))
        {
            var c = Swatch(_index, palette.Id);
            if (seen.Add(((int)(c.X * 100), (int)(c.Y * 100), (int)(c.Z * 100)))) list.Add(palette);
        }
        return _dyes = list;
    }

    private List<PaletteEntry>? _dyes;

    private bool IsRealColor(PaletteEntry entry) => _index!.Colors.ReadPalette(entry.Id) is { IsPlaceholder: false };

    // Grays first from dark to light, then the colors by hue and lightness.
    private List<PaletteEntry> SortedByColor(IEnumerable<PaletteEntry> entries) =>
        entries.OrderBy(e => ColorKey(Swatch(_index!, e.Id))).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();

    private static (int Group, float Hue, float Light) ColorKey(System.Numerics.Vector4 c)
    {
        float max = Math.Max(c.X, Math.Max(c.Y, c.Z)), min = Math.Min(c.X, Math.Min(c.Y, c.Z));
        float light = (max + min) / 2, chroma = max - min;
        if (chroma < 0.08f || light < 0.07f) return (0, 0, light);
        float h = max == c.X ? (c.Y - c.Z) / chroma % 6 : max == c.Y ? (c.Z - c.X) / chroma + 2 : (c.X - c.Y) / chroma + 4;
        return (1, h < 0 ? h + 6 : h, light);
    }

    // "Navy (dye h57 p)": the name of the color, then the file name of the palette.
    private string ColorLabel(string id)
    {
        var c = Swatch(_index!, id);
        string file = PaletteLabel(_index!.Colors.FindPaletteEntry(id)?.Name ?? id);
        return $"{Swtor.Formats.Dds.ColorNames.Describe(c.X, c.Y, c.Z)} ({file})";
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
        int step = Stepper.Before("scheme");
        bool picked = false;
        string? pickedId = choice.SchemeId;
        if (ImGui.BeginCombo("##scheme", choice.SchemeId is null ? "(default colors)" : colors.FindScheme(choice.SchemeId)?.Name ?? choice.SchemeId))
        {
            if (ImGui.Selectable("(default colors)", choice.SchemeId is null))
            {
                pickedId = null;
                picked = true;
            }
            foreach (string id in ids)
            {
                if (colors.FindScheme(id) is not { } scheme || !ImGui.Selectable($"{scheme.Name}##{id}", id == choice.SchemeId)) continue;
                pickedId = id;
                picked = true;
            }
            ImGui.EndCombo();
        }
        step += Stepper.After("scheme");
        if (step != 0)
        {
            var known = ids.Where(i => colors.FindScheme(i) is not null).ToList();
            int at = Stepper.Move(known.Count, choice.SchemeId is null ? -1 : known.IndexOf(choice.SchemeId), step, true);
            pickedId = at < 0 ? null : known[at];
            picked = true;
        }
        if (!picked) return;
        choice.SchemeId = pickedId;
        choice.PrimaryId = choice.SecondaryId = null;
        choice.PrimaryCustom = choice.SecondaryCustom = null;
        Rebuild();
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
