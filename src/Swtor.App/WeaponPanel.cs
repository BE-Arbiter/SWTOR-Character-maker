using System.Numerics;
using ImGuiNET;
using Microsoft.Xna.Framework.Graphics;
using Swtor.Assets;
using Swtor.Formats;
using Swtor.Formats.Dds;
using Swtor.Formats.Gr2;

namespace Swtor.App;

/// <summary>
/// The "Weapons" tab: browse the weapon appearances of the game by combat type and name,
/// and show the selected one in the preview.
/// </summary>
public sealed class WeaponPanel
{
    private readonly GraphicsDevice _device;
    private readonly ModelPreview _preview;
    private readonly List<Texture2D> _textures = [];

    private AssetIndex? _index;
    private WeaponCatalog? _catalog;
    private List<WeaponAppearance> _shown = [];
    private string _filter = "", _lastFilter = "\0";
    private string _type = "";
    private string _lastType = "\0";
    private WeaponAppearance? _selected;
    private string? _texturePath, _error;
    private int _triangles;

    public WeaponPanel(GraphicsDevice device, ModelPreview preview)
    {
        _device = device;
        _preview = preview;
    }

    public void SetData(AssetIndex? index, WeaponCatalog? catalog)
    {
        _index = index;
        _catalog = catalog;
        _lastFilter = "\0";
        if (_pendingKey is not null && catalog is not null)
        {
            _selected = catalog.Items.FirstOrDefault(i => i.Key.Equals(_pendingKey, StringComparison.OrdinalIgnoreCase));
            _pendingKey = null;
            Rebuild();
        }
    }

    private string? _pendingKey;

    /// <summary>Selects a weapon by its key once the catalog is loaded (used by the --weapon start-up argument).</summary>
    public void SelectByKey(string key) => _pendingKey = key;

    /// <summary>Shows the selected weapon in the preview. Call when the tab becomes active.</summary>
    public void Rebuild()
    {
        _preview.Clear();
        foreach (var texture in _textures) texture.Dispose();
        _textures.Clear();
        _error = null;
        _texturePath = null;
        _triangles = 0;
        if (_selected is null || _index is null) return;

        if (!_index.HasModel(_selected.ModelPath))
        {
            _error = "The model file is not in the extracted data.";
            return;
        }
        try
        {
            var model = Gr2Reader.Parse(File.ReadAllBytes(_index.FullPath(_selected.ModelPath)));
            _triangles = model.Meshes.Sum(m => m.Indices.Length / 3);
            _texturePath = FindTexture(_selected.ModelPath);
            Texture2D? texture = null;
            if (_texturePath is not null)
            {
                texture = TextureLoader.Create(_device, DdsReader.Decode(File.ReadAllBytes(_index.FullPath(_texturePath))));
                _textures.Add(texture);
            }
            _preview.Add(model, texture);
            _preview.Frame();
        }
        catch (Exception e) when (e is GameFormatException or IOException)
        {
            _error = e.Message;
        }
    }

    public void Draw()
    {
        if (_catalog is null || _index is null)
        {
            ImGui.TextDisabled("Loading the game database...");
            return;
        }

        DrawFilters();
        ImGui.Text($"{_shown.Count} of {_catalog.Items.Count} weapons");
        DrawList();
        DrawInfo();
    }

    private void DrawFilters()
    {
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##type", _type.Length == 0 ? "All types" : TypeLabel(_type)))
        {
            if (ImGui.Selectable("All types", _type.Length == 0)) _type = "";
            foreach (string type in _catalog!.CombatTypes)
                if (ImGui.Selectable($"{TypeLabel(type)} ({_catalog.Items.Count(i => i.CombatType == type)})", type == _type)) _type = type;
            ImGui.EndCombo();
        }
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##weaponfilter", "Filter by name...", ref _filter, 128);

        if (_filter == _lastFilter && _type == _lastType) return;
        _lastFilter = _filter;
        _lastType = _type;
        _shown = _catalog.Items
            .Where(i => (_type.Length == 0 || i.CombatType == _type) && (_filter.Length == 0 || i.Key.Contains(_filter, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    // The list is drawn by hand, so only the rows in view cost time (2,368 weapons).
    private void DrawList()
    {
        ImGui.BeginChild("##weapons", new Vector2(0, 260), true);
        float rowHeight = ImGui.GetTextLineHeightWithSpacing();
        float top = ImGui.GetCursorPosY();
        int first = Math.Max(0, (int)(ImGui.GetScrollY() / rowHeight) - 1);
        int last = Math.Min(_shown.Count, first + (int)(ImGui.GetWindowHeight() / rowHeight) + 3);
        for (int i = first; i < last; i++)
        {
            ImGui.SetCursorPos(new Vector2(ImGui.GetStyle().WindowPadding.X, top + i * rowHeight));
            var weapon = _shown[i];
            if (!ImGui.Selectable($"{weapon.Key}##{i}", weapon == _selected)) continue;
            _selected = weapon;
            Rebuild();
        }
        ImGui.SetCursorPos(new Vector2(0, top + _shown.Count * rowHeight));
        ImGui.Dummy(Vector2.Zero);
        ImGui.EndChild();
    }

    private void DrawInfo()
    {
        if (_selected is null)
        {
            ImGui.TextDisabled("Click a weapon in the list.");
            return;
        }
        if (_error is not null) ImGui.TextColored(new Vector4(1, 0.5f, 0.4f, 1), _error);
        ImGui.Separator();
        ImGui.TextWrapped(_selected.Key);
        ImGui.Text($"Type: {TypeLabel(_selected.CombatType)}");
        if (_selected.Label.Length > 0) ImGui.Text($"Label: {_selected.Label}");
        if (_selected.Socket.Length > 0) ImGui.Text($"Socket: {_selected.Socket}");
        if (_selected.Color.Length > 0) ImGui.Text($"Color: {_selected.Color}");
        ImGui.TextWrapped($"Model: {_selected.ModelPath}");
        if (_triangles > 0) ImGui.Text($"{_triangles} triangles");
        ImGui.TextWrapped(_texturePath is null ? "No texture found in the extracted data." : $"Texture: {_texturePath}");
    }

    // The material file with the model name gives the diffuse texture. Without it, search by name.
    private string? FindTexture(string modelPath)
    {
        string stem = Path.GetFileNameWithoutExtension(modelPath);
        string materialPath = Path.Combine(_index!.Root, "art", "shaders", "materials", stem + ".mat");
        if (File.Exists(materialPath))
        {
            string? diffuse = Swtor.Formats.Xml.MaterialReader.Parse(File.ReadAllText(materialPath)).DiffuseMap;
            if (diffuse is not null && File.Exists(_index.FullPath(diffuse + ".dds"))) return diffuse + ".dds";
        }
        return _index.FindTextures(modelPath).FirstOrDefault();
    }

    // "cbtType_lightsaber" becomes "Lightsaber".
    private static string TypeLabel(string type)
    {
        string text = type.StartsWith("cbtType_", StringComparison.Ordinal) ? type["cbtType_".Length..] : type;
        return text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
    }
}
