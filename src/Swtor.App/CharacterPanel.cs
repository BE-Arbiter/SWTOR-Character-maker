using System.Globalization;
using System.Numerics;
using ImGuiNET;
using Microsoft.Xna.Framework.Graphics;
using Swtor.Assets;
using Swtor.Formats;
using Swtor.Formats.Dds;
using Swtor.Formats.Gr2;
using Swtor.Formats.Xml;

namespace Swtor.App;

/// <summary>
/// The "Character" tab: choose class, gender and race, then the appearance options that the game offers
/// for that combination. The preview shows the head, hair and face hair.
/// </summary>
public sealed partial class CharacterPanel
{
    // Slots shown as lists, then slots shown as color swatches.
    private static readonly AppearanceSlot[] ListSlots =
        [AppearanceSlot.Head, AppearanceSlot.Hair, AppearanceSlot.FaceHair, AppearanceSlot.Complexion, AppearanceSlot.Age, AppearanceSlot.FacePaint];
    private static readonly AppearanceSlot[] ColorSlots = [AppearanceSlot.SkinColor, AppearanceSlot.HairColor, AppearanceSlot.EyeColor];
    private static readonly AppearanceSlot[] ModelSlots = [AppearanceSlot.Head, AppearanceSlot.Hair, AppearanceSlot.FaceHair];
    private static readonly AppearanceSlot[] OptionalSlots = [AppearanceSlot.Hair, AppearanceSlot.FaceHair, AppearanceSlot.FacePaint];
    private static readonly AppearanceSlot[] NoneByDefault = [AppearanceSlot.FaceHair];

    private readonly GraphicsDevice _device;
    private readonly ModelPreview _preview;
    private readonly List<Texture2D> _textures = [];
    private readonly Dictionary<AppearanceSlot, long> _selected = [];

    private AssetIndex? _index;
    private CharacterCatalog? _catalog;
    private CharacterSpec? _spec;
    private string _class = "", _gender = "", _race = "";
    private bool _legacy;
    private string? _error;

    public CharacterPanel(GraphicsDevice device, ModelPreview preview)
    {
        _device = device;
        _preview = preview;
    }

    /// <summary>Gives the panel its data. <paramref name="catalog"/> is null until the game database is loaded.</summary>
    public void SetData(AssetIndex? index, CharacterCatalog? catalog)
    {
        bool first = _catalog is null && catalog is not null;
        _index = index;
        _catalog = catalog;
        if (first && catalog!.Specs.Count > 0)
        {
            var spec = catalog.Specs.FirstOrDefault(s => s.Class == "trooper" && s.Gender == "male" && s.Race == "human" && !s.IsLegacy) ?? catalog.Specs[0];
            SelectSpec(spec.Class, spec.Gender, spec.Race, spec.IsLegacy);
        }
    }

    /// <summary>Shows this character in the preview. Call when the tab becomes active.</summary>
    public void Rebuild()
    {
        if (_spec is null || _index is null) return;
        _error = null;
        _preview.Clear();
        foreach (var texture in _textures) texture.Dispose();
        _textures.Clear();

        char gender = _spec.Gender == "female" ? 'f' : 'm';
        ResolvedPart? headPart = null;
        foreach (var slot in ModelSlots)
        {
            if (OptionFor(slot) is not { } option) continue;
            if (PartResolver.Resolve(_index, option, gender, headPart?.Bodytype) is not { } part) continue;
            if (slot == AppearanceSlot.Head) headPart = part;
            AddModel(part.ModelPath, LoadTexture(part, TintFor(slot)), slot.ToString());
        }

        string? headName = OptionFor(AppearanceSlot.Head) is { } head ? _index.Appearances.FindAsset(head.AssetId)?.Asset.ArtName : null;
        AddBodyAndEquipment(gender, headPart?.Bodytype, headName);
        _preview.Frame();
    }

    public void Draw()
    {
        if (_catalog is null)
        {
            ImGui.TextDisabled("Loading the game database...");
            return;
        }
        if (_error is not null) ImGui.TextColored(new Vector4(1, 0.5f, 0.4f, 1), _error);

        DrawSpecChoice();
        if (_spec is null) return;
        ImGui.Separator();
        foreach (var slot in ListSlots) DrawListSlot(slot);
        ImGui.Separator();
        foreach (var slot in ColorSlots) DrawColorSlot(slot);
        ImGui.Separator();
        DrawEquipment();
    }

    private void DrawSpecChoice()
    {
        var catalog = _catalog!;
        string? newClass = null, newGender = null, newRace = null;
        bool newLegacy = _legacy;

        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##class", Pretty(_class)))
        {
            foreach (var c in catalog.Classes) if (ImGui.Selectable(Pretty(c), c == _class)) newClass = c;
            ImGui.EndCombo();
        }
        ImGui.SetNextItemWidth(120);
        if (ImGui.BeginCombo("##gender", Pretty(_gender)))
        {
            foreach (var g in catalog.Genders(_class)) if (ImGui.Selectable(Pretty(g), g == _gender)) newGender = g;
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(-60);
        if (ImGui.BeginCombo("##race", Pretty(_race)))
        {
            foreach (var r in catalog.Races(_class, _gender)) if (ImGui.Selectable(Pretty(r), r == _race)) newRace = r;
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        bool hasLegacy = catalog.Find(_class, _gender, _race, true) is not null;
        if (hasLegacy && ImGui.Checkbox("Legacy", ref newLegacy)) { }

        if (newClass is not null || newGender is not null || newRace is not null || newLegacy != _legacy)
        {
            string c = newClass ?? _class;
            var genders = catalog.Genders(c);
            string g = newGender ?? (genders.Contains(_gender) ? _gender : genders[0]);
            var races = catalog.Races(c, g);
            string r = newRace ?? (races.Contains(_race) ? _race : races[0]);
            SelectSpec(c, g, r, newLegacy);
            Rebuild();
        }
    }

    private void SelectSpec(string className, string gender, string race, bool legacy)
    {
        var spec = _catalog!.Find(className, gender, race, legacy) ?? _catalog.Find(className, gender, race, !legacy);
        if (spec is null) return;
        var previous = _spec;
        _spec = spec;
        (_class, _gender, _race, _legacy) = (spec.Class, spec.Gender, spec.Race, spec.IsLegacy);

        // Keep the same asset in a slot when the new spec has it. Otherwise take the first option.
        var old = new Dictionary<AppearanceSlot, long>(_selected);
        _selected.Clear();
        foreach (var slot in ListSlots.Concat(ColorSlots))
        {
            var options = Allowed(slot);
            var keep = previous is not null && old.TryGetValue(slot, out long key)
                ? previous.Options.FirstOrDefault(o => o.Key == key)
                : null;
            var match = keep is null ? null : options.FirstOrDefault(o => o.AssetId == keep.AssetId && o.MaterialId == keep.MaterialId);
            if (match is not null) _selected[slot] = match.Key;
            else if (options.Count > 0 && !NoneByDefault.Contains(slot)) _selected[slot] = options[0].Key;
        }
    }

    // Options of a slot that fit the selected head. The head list itself is not filtered.
    private IReadOnlyList<CharacterOption> Allowed(AppearanceSlot slot)
    {
        var all = _spec!.Options.Where(o => o.Slot == slot).ToList();
        if (slot == AppearanceSlot.Head) return all;
        if (_selected.TryGetValue(AppearanceSlot.Head, out long headKey)
            && _spec.Compatible.TryGetValue(headKey, out var bySlot) && bySlot.TryGetValue(slot, out var keys))
        {
            var allowed = new HashSet<long>(keys);
            var filtered = all.Where(o => allowed.Contains(o.Key)).ToList();
            if (filtered.Count > 0) return filtered;
        }
        return all;
    }

    private CharacterOption? OptionFor(AppearanceSlot slot) =>
        _selected.TryGetValue(slot, out long key) ? _spec?.Options.FirstOrDefault(o => o.Key == key) : null;

    private void DrawListSlot(AppearanceSlot slot)
    {
        var options = Allowed(slot);
        if (options.Count == 0) return;

        var current = OptionFor(slot);
        ImGui.Text($"{slot} ({options.Count})");
        ImGui.SetNextItemWidth(-1);
        if (!ImGui.BeginCombo($"##{slot}", current is null ? "(none)" : Label(current))) return;

        bool changed = false;
        if (OptionalSlots.Contains(slot) && ImGui.Selectable("(none)", current is null))
        {
            _selected.Remove(slot);
            changed = true;
        }
        foreach (var option in options)
        {
            if (!ImGui.Selectable($"{Label(option)}##{option.Key}", option.Key == current?.Key)) continue;
            _selected[slot] = option.Key;
            changed = true;
            if (slot == AppearanceSlot.Head) ReapplyCompatibility();
        }
        ImGui.EndCombo();
        if (changed) Rebuild();
    }

    // After a head change, keep each other slot if its option is still allowed. Otherwise take the first allowed one.
    private void ReapplyCompatibility()
    {
        foreach (var slot in ListSlots.Concat(ColorSlots))
        {
            if (slot == AppearanceSlot.Head) continue;
            var allowed = Allowed(slot);
            if (_selected.TryGetValue(slot, out long key) && allowed.Any(o => o.Key == key)) continue;
            if (allowed.Count > 0 && !NoneByDefault.Contains(slot)) _selected[slot] = allowed[0].Key;
            else _selected.Remove(slot);
        }
    }

    // Colors are shown as swatches. The game gives one representative color for each entry.
    private void DrawColorSlot(AppearanceSlot slot)
    {
        var options = Allowed(slot);
        if (options.Count == 0 || _index is null) return;

        ImGui.Text($"{slot} ({options.Count})");
        float width = ImGui.GetContentRegionAvail().X - 24;
        bool changed = false;
        float x = 0;
        foreach (var option in options)
        {
            var asset = _index.Appearances.FindAsset(option.AssetId)?.Asset;
            var color = ParseColor(asset?.RepresentativeColor);
            bool selected = _selected.TryGetValue(slot, out long key) && key == option.Key;
            if (x + 26 > width) x = 0;
            else if (x > 0) ImGui.SameLine();
            if (selected) ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(1, 1, 1, 1));
            if (selected) ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 2f);
            if (ImGui.ColorButton($"##{slot}{option.Key}", color, ImGuiColorEditFlags.NoTooltip, new Vector2(22, 22)))
            {
                _selected[slot] = option.Key;
                changed = true;
            }
            if (selected) ImGui.PopStyleVar();
            if (selected) ImGui.PopStyleColor();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(asset?.ArtName ?? option.AssetId.ToString(CultureInfo.InvariantCulture));
            x += 26;
        }
        if (OptionFor(slot) is { } chosen)
            ImGui.TextDisabled(_index.Appearances.FindAsset(chosen.AssetId)?.Asset.ArtName ?? "");
        if (changed) Rebuild();
    }

    private string Label(CharacterOption option)
    {
        var found = _index?.Appearances.FindAsset(option.AssetId);
        if (found is null) return option.AssetId.ToString(CultureInfo.InvariantCulture);
        var material = found.Value.Asset.Materials.FirstOrDefault(m => m.Id == option.MaterialId.ToString(CultureInfo.InvariantCulture));
        return material is null || option.Slot != AppearanceSlot.Head ? found.Value.Asset.ArtName : $"{found.Value.Asset.ArtName} / {material.Name}";
    }

    // Skin color tints the head. Hair color tints hair and face hair. The game color is an average, so we multiply.
    private Vector4? TintFor(AppearanceSlot slot)
    {
        var colorSlot = slot switch { AppearanceSlot.Head => AppearanceSlot.SkinColor, AppearanceSlot.Hair or AppearanceSlot.FaceHair => AppearanceSlot.HairColor, _ => (AppearanceSlot?)null };
        if (colorSlot is null || OptionFor(colorSlot.Value) is not { } option) return null;
        var color = _index?.Appearances.FindAsset(option.AssetId)?.Asset.RepresentativeColor;
        return color is null ? null : ParseColor(color);
    }

    private Texture2D? LoadTexture(ResolvedPart part, Vector4? tint, Palette? primary = null, Palette? secondary = null)
    {
        if (part.DiffusePath is null || _index is null) return null;
        try
        {
            var image = DdsReader.Decode(File.ReadAllBytes(_index.FullPath(part.DiffusePath)));
            if (tint is { } t) Multiply(image, t);
            if ((primary is not null || secondary is not null) && part.MaskPath is not null)
                image = PaletteTint.Apply(image, DdsReader.Decode(File.ReadAllBytes(_index.FullPath(part.MaskPath))), primary, secondary);
            var texture = TextureLoader.Create(_device, image);
            _textures.Add(texture);
            return texture;
        }
        catch (Exception e) when (e is GameFormatException or IOException)
        {
            _error = $"{Path.GetFileName(part.DiffusePath)}: {e.Message}";
            return null;
        }
    }

    // Multiplies the color channels of an image in place.
    private static void Multiply(DdsImage image, Vector4 color)
    {
        byte[] p = image.Rgba;
        for (int i = 0; i < p.Length; i += 4)
        {
            p[i] = (byte)(p[i] * color.X);
            p[i + 1] = (byte)(p[i + 1] * color.Y);
            p[i + 2] = (byte)(p[i + 2] * color.Z);
        }
    }

    // "sith_inquisitor" becomes "Sith Inquisitor".
    private static string Pretty(string text) =>
        string.Join(' ', text.Split('_').Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..]));

    // Color text: "r,g,b" with values from 0 to 1. Unknown colors are grey.
    private static Vector4 ParseColor(string? text)
    {
        if (text is null) return new Vector4(0.5f, 0.5f, 0.5f, 1);
        string[] parts = text.Split(',');
        if (parts.Length < 3) return new Vector4(0.5f, 0.5f, 0.5f, 1);
        float Read(string s) => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? Math.Clamp(v, 0, 1) : 0.5f;
        return new Vector4(Read(parts[0]), Read(parts[1]), Read(parts[2]), 1);
    }
}
