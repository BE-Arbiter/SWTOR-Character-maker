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
    private UiText _text = UiText.Empty;
    private readonly DdsCache _images = new();

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
        if (_index is not null) _text = UiText.Load(_index.Root);
        if (first) PrecachePalettes();
        if (first && catalog!.Specs.Count > 0)
        {
            var spec = catalog.Specs.FirstOrDefault(s => s.Class == "trooper" && s.Gender == "male" && s.Race == "human" && !s.IsLegacy) ?? catalog.Specs[0];
            SelectSpec(spec.Class, spec.Gender, spec.Race, spec.IsLegacy);
        }
        ApplyPendingSave();
    }

    /// <summary>Shows this character in the preview. Call when the tab becomes active.</summary>
    public void Rebuild()
    {
        if (_spec is null || _index is null) return;
        _error = null;
        _preview.Clear();
        foreach (var texture in _textures) texture.Dispose();
        _textures.Clear();
        ClearExport();
        ApplyStartupEquipment();

        char gender = _spec.Gender == "female" ? 'f' : 'm';
        ResolvedPart? headPart = null;
        foreach (var slot in ModelSlots)
        {
            if ((slot == AppearanceSlot.Head ? HeadOption() : OptionFor(slot)) is not { } option) continue;
            if (slot == AppearanceSlot.Hair && _hideHairUnderHelmet && (_equipment.ContainsKey("face") || HoodShownUp())) continue;
            if (PartResolver.Resolve(_index, option, gender, headPart?.Bodytype) is not { } part) continue;
            if (slot == AppearanceSlot.Head) headPart = part;
            if (slot == AppearanceSlot.Head)
            {
                AddHead(part);
                continue;
            }
            AddModel(part.ModelPath, LoadTexture(part, TintFor(slot)), slot.ToString());
        }

        var headAsset = HeadOption() is { } head ? _index.Appearances.FindAsset(head.AssetId)?.Asset : null;
        AddBodyAndEquipment(gender, headPart?.Bodytype, headAsset);
        _preview.Frame(this);
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
        DrawGallery();
    }

    private void DrawSpecChoice()
    {
        var catalog = _catalog!;
        string? newClass = null, newGender = null, newRace = null;
        bool newLegacy = _legacy;

        int step = Stepper.Before("class");
        if (ImGui.BeginCombo("##class", _text.Class(_class)))
        {
            foreach (var c in catalog.Classes) if (ImGui.Selectable(_text.Class(c), c == _class)) newClass = c;
            ImGui.EndCombo();
        }
        step += Stepper.After("class");
        if (step != 0) newClass = StepValue(catalog.Classes, _class, step);

        step = Stepper.Before("gender", 100);
        if (ImGui.BeginCombo("##gender", _text.Gender(_gender)))
        {
            foreach (var g in catalog.Genders(_class)) if (ImGui.Selectable(_text.Gender(g), g == _gender)) newGender = g;
            ImGui.EndCombo();
        }
        step += Stepper.After("gender");
        if (step != 0) newGender = StepValue(catalog.Genders(_class), _gender, step);
        ImGui.SameLine();
        step = Stepper.Before("race", 0, 60);
        if (ImGui.BeginCombo("##race", _text.Race(_race)))
        {
            foreach (var r in catalog.Races(_class, _gender)) if (ImGui.Selectable(_text.Race(r), r == _race)) newRace = r;
            ImGui.EndCombo();
        }
        step += Stepper.After("race");
        if (step != 0) newRace = StepValue(catalog.Races(_class, _gender), _race, step);
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

    // The value that comes step places after current in the list (wraps around).
    private static string StepValue(IReadOnlyList<string> values, string current, int step)
    {
        int at = Stepper.Move(values.Count, values.ToList().IndexOf(current), step, false);
        return at < 0 ? current : values[at];
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
        bool changed = false;
        if (slot == AppearanceSlot.Head)
        {
            changed = DrawBodyChoice(options, current);
            current = OptionFor(slot);
            string body = BodyOf(current) ?? "";
            options = options.Where(o => BodyOf(o) == body).ToList();
        }
        ImGui.Text($"{_text.Slot(slot)} ({options.Count})");
        string id = slot.ToString();
        bool optional = OptionalSlots.Contains(slot);
        int step = Stepper.Before(id);
        if (ImGui.BeginCombo($"##{slot}", current is null ? "(none)" : Label(current)))
        {
            if (optional && ImGui.Selectable("(none)", current is null))
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
        }
        step += Stepper.After(id);
        if (step != 0)
        {
            int at = Stepper.Move(options.Count, IndexOfKey(options, current), step, optional);
            if (at < 0) _selected.Remove(slot);
            else _selected[slot] = options[at].Key;
            changed = true;
            if (slot == AppearanceSlot.Head) ReapplyCompatibility();
        }
        if (changed) Rebuild();
        if (slot == AppearanceSlot.Head) DrawNpcHeadChoice();
    }

    private static int IndexOfKey(IReadOnlyList<CharacterOption> options, CharacterOption? option)
    {
        for (int i = 0; i < options.Count; i++)
            if (options[i].Key == option?.Key) return i;
        return -1;
    }

    // Body type code of a head ("bma"), the third part of its art name: head_human_bma_caucasian_a01.
    private string? BodyOf(CharacterOption? head) =>
        head is null || _index?.Appearances.FindAsset(head.AssetId)?.Asset.ArtName.Split('_') is not { Length: > 2 } parts ? null : parts[2];

    // Body size order: agile, athletic, strong, robust (the third letter of the code).
    private static int BodyOrder(string code) => code.Length >= 3 ? "anSfb".IndexOf(code[2], StringComparison.OrdinalIgnoreCase) : 9;

    // The "Body" choice that comes before the head list. The heads of other body types are hidden.
    // A new body type selects the head with the same look, or the first head of that body. Returns true when the head changed.
    private bool DrawBodyChoice(IReadOnlyList<CharacterOption> heads, CharacterOption? current)
    {
        var bodies = heads.Select(BodyOf).Where(b => b is not null).Select(b => b!).Distinct().OrderBy(BodyOrder).ToList();
        if (bodies.Count < 2) return false;

        string currentBody = BodyOf(current) ?? bodies[0];
        ImGui.Text(_text.BodyTypeTitle);
        int step = Stepper.Before("body");
        string? picked = null;
        if (ImGui.BeginCombo("##body", _text.BodyType(currentBody)))
        {
            foreach (string body in bodies)
                if (ImGui.Selectable($"{_text.BodyType(body)} ({heads.Count(h => BodyOf(h) == body)})##{body}", body == currentBody)) picked = body;
            ImGui.EndCombo();
        }
        step += Stepper.After("body");
        if (step != 0) picked = bodies[Stepper.Move(bodies.Count, bodies.IndexOf(currentBody), step, false)];
        if (picked is null || picked == currentBody) return false;

        // Same look: the art name without the body code (head_human_bma_caucasian_a01 gives head_human_caucasian_a01).
        string Look(CharacterOption h)
        {
            var parts = _index!.Appearances.FindAsset(h.AssetId)!.Value.Asset.ArtName.Split('_').ToList();
            if (parts.Count > 2) parts.RemoveAt(2);
            return string.Join('_', parts);
        }
        var candidates = heads.Where(h => BodyOf(h) == picked).ToList();
        var match = current is null ? null : candidates.FirstOrDefault(h => Look(h) == Look(current)) ?? candidates.FirstOrDefault(h => h.MaterialId == current.MaterialId);
        _selected[AppearanceSlot.Head] = (match ?? candidates[0]).Key;
        ReapplyCompatibility();
        return true;
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

        ImGui.Text($"{_text.Slot(slot)} ({options.Count})");
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
        if (colorSlot == AppearanceSlot.SkinColor && KeepNpcColors) return null;
        var color = _index?.Appearances.FindAsset(option.AssetId)?.Asset.RepresentativeColor;
        return color is null ? null : ParseColor(color);
    }

    // With an opacity map, the holes of the material go in the alpha channel and the preview draws the texture last.
    private Texture2D? LoadTexture(ResolvedPart part, Vector4? tint, Palette? primary = null, Palette? secondary = null)
    {
        Func<DdsImage, DdsImage>? cutOut = part.OpacityPath is { } opacity
            ? image => ImageColor.CutOut(image, _images.Get(_index!.FullPath(opacity)), part.AlphaTest)
            : null;
        var texture = LoadTexture(part.DiffusePath, part.MaskPath, tint, primary, secondary, cutOut, part.PaletteMapPath);
        if (texture is not null && cutOut is not null) _preview.MarkCutOut(texture);
        return texture;
    }

    // Builds a texture. <paramref name="tint"/> is the wanted average color of the masked area (see ImageColor.MatchAverage).
    // <paramref name="after"/> can add overlays (complexion, face paint) after the color change.
    private Texture2D? LoadTexture(string? diffusePath, string? maskPath, Vector4? tint, Palette? primary, Palette? secondary, Func<DdsImage, DdsImage>? after, string? paletteMapPath = null)
    {
        if (diffusePath is null || _index is null) return null;
        try
        {
            var image = _images.Get(_index.FullPath(diffusePath));
            DdsImage? mask = maskPath is null ? null : _images.Get(_index.FullPath(maskPath));
            if (tint is { } t) image = ImageColor.MatchAverage(image, mask, new Vector3(t.X, t.Y, t.Z));
            DdsImage? paletteMap = paletteMapPath is null || _index is null ? null : _images.Get(_index.FullPath(paletteMapPath));
            if (primary is not null || secondary is not null) image = PaletteTint.Apply(image, mask, primary, secondary, paletteMap);
            if (after is not null) image = after(image);
            var texture = TextureLoader.Create(_device, image);
            _textures.Add(texture);
            _imageOf[texture] = image;
            return texture;
        }
        catch (Exception e) when (e is GameFormatException or IOException)
        {
            _error = $"{Path.GetFileName(diffusePath)}: {e.Message}";
            return null;
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
