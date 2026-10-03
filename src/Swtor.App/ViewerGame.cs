using System.Numerics;
using ImGuiNET;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using MonoGame.ImGuiNet;
using Swtor.Assets;
using Swtor.Formats;
using Swtor.Formats.Dds;
using Swtor.Formats.Gr2;
using Swtor.Formats.Xml;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;

namespace Swtor.App;

/// <summary>Main window. Layout: menu bar on top, then explorer 18%, 3D preview 57%, details 25%.</summary>
public sealed partial class ViewerGame : Game
{
    private const float LeftShare = 0.18f, CenterShare = 0.57f;
    private const ImGuiWindowFlags PanelFlags = ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize
        | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBringToFrontOnFocus;
    private static readonly Vector4 ErrorColor = new(1, 0.5f, 0.4f, 1);

    private readonly GraphicsDeviceManager _graphics;
    private readonly FileExplorer _explorer = new();
    private ImGuiRenderer _gui = null!;
    private ModelPreview _preview = null!;
    private MouseState _mouse;
    private Microsoft.Xna.Framework.Rectangle _previewArea;

    // Asset index. The scan runs on a background thread and hands the result over in _readyIndex.
    private AssetIndex? _index;
    private volatile AssetIndex? _readyIndex;
    private volatile string? _indexError;
    private volatile int _scanned;
    private bool _scanning;
    private string _root = "";

    // Current model and texture.
    private Gr2Model? _model;
    private string? _modelPath;
    private IReadOnlyList<string> _textureCandidates = [];
    private IReadOnlyList<AppearanceMatch> _matches = [];
    private int _matchIndex, _variantIndex;
    private Texture2D? _texture;
    private IntPtr _textureId;
    private string? _texturePath;
    private string? _error;
    private string? _pendingModel;
    private string? _pendingTexture;
    private string? _pendingMask;
    private DdsImage? _baseImage, _maskImage;
    private string? _schemeId, _primaryPalette, _secondaryPalette;
    private string _textureQuery = "";
    private List<string> _textureResults = [];

    // Character creator and the game database. The database opens on a background thread.
    private enum ActiveTab { None, Model, Character }
    private CharacterPanel _character = null!;
    private ActiveTab _activeTab = ActiveTab.Model;
    private bool _showModelTab, _showCharacterTab;
    private GomDatabase? _gom;
    private volatile CharacterCatalog? _readyCatalog;
    private volatile GomDatabase? _readyGom;
    private volatile string? _gomError;

    private bool _openFolderRequested;
    private bool _popupOpen = true;
    private string _folderInput = "";

    public ViewerGame(string? initialModel = null, string? initialScheme = null, bool startOnCharacter = false,
        IEnumerable<(string Slot, string ArtName)>? equipment = null, string? loadPath = null)
    {
        _graphics = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = 1600,
            PreferredBackBufferHeight = 900,
            GraphicsProfile = GraphicsProfile.HiDef,
        };
        IsMouseVisible = true;
        Window.AllowUserResizing = true;
        Window.Title = "SWTOR Character Maker";
        _explorer.FileSelected += path =>
        {
            _pendingModel = path;
            _showModelTab = true;
        };
        _initialModel = initialModel;
        _initialScheme = initialScheme;
        _showCharacterTab = startOnCharacter;
        _startupEquipment = equipment?.ToList();
        _startupLoadPath = loadPath;
        if (loadPath is not null) _showCharacterTab = true;
    }

    private string? _initialModel, _initialScheme;
    private List<(string Slot, string ArtName)>? _startupEquipment;
    private readonly string? _startupLoadPath;

    // Save and load dialogs.
    private enum FileDialog { None, Save, Load }
    private FileDialog _fileDialogRequest;
    private bool _fileDialogOpen = true;
    private string _characterPath = "";
    private string? _dialogMessage;
    private static readonly string CharacterFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SWTOR Character Maker", "Characters");

    protected override void Initialize()
    {
        _gui = new ImGuiRenderer(this);
        _gui.RebuildFontAtlas();
        ImGui.GetIO().ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;
        _preview = new ModelPreview(GraphicsDevice);
        _character = new CharacterPanel(GraphicsDevice, _preview);
        if (_startupEquipment is not null) _character.SetStartupEquipment(_startupEquipment);
        if (_startupLoadPath is not null) LoadCharacter(_startupLoadPath);

        _folderInput = Environment.GetEnvironmentVariable("SWTOR_ASSETS") ?? @"C:\jka_tor_assets\resources";
        StartIndexing(_folderInput, rescan: false);
        base.Initialize();
    }

    protected override void Update(GameTime gameTime)
    {
        var current = Mouse.GetState();
        // The preview is the only part of the window that is not an ImGui window.
        if (IsActive && !ImGui.GetIO().WantCaptureMouse && _previewArea.Contains(current.Position))
            _preview.HandleInput(_mouse, current);
        _mouse = current;

        if (_readyIndex is { } ready)
        {
            _readyIndex = null;
            _index = ready;
            _scanning = false;
            _explorer.SetIndex(ready);
            StartGomLoading(ready.Root);
            if (_initialModel is not null)
            {
                _pendingModel = Path.GetRelativePath(_root, _initialModel).Replace('\\', '/');
                _initialModel = null;
            }
        }
        if (_readyCatalog is { } catalog)
        {
            _readyCatalog = null;
            _gom = _readyGom;
            _character.SetData(_index, catalog);
            if (_activeTab == ActiveTab.Character) _character.Rebuild();
        }
        if (_scanning) _explorer.Status = _indexError ?? $"Indexing the asset folder... {_scanned} files seen.\nThis happens once. The result is cached.";

        if (_pendingModel is { } model)
        {
            _pendingModel = null;
            LoadModel(model);
            if (_initialScheme is not null && _matches.Count > 0)
            {
                SelectScheme(_initialScheme);
                _initialScheme = null;
            }
        }
        if (_pendingTexture is { } texture)
        {
            _pendingTexture = null;
            ApplyTexture(texture.Length == 0 ? null : texture, _pendingMask);
            _pendingMask = null;
        }
        base.Update(gameTime);
    }

    protected override void Draw(GameTime gameTime)
    {
        GraphicsDevice.Clear(new Color(20, 21, 26));
        _gui.BeforeLayout(gameTime);

        DrawMenu();
        float top = ImGui.GetFrameHeight();
        float width = GraphicsDevice.Viewport.Width, height = GraphicsDevice.Viewport.Height - top;
        float leftWidth = MathF.Round(width * LeftShare), centerWidth = MathF.Round(width * CenterShare);

        _previewArea = new Microsoft.Xna.Framework.Rectangle((int)leftWidth, (int)top, (int)centerWidth, (int)height);
        DrawPanel("Models", 0, top, leftWidth, height, _explorer.Draw);
        DrawPanel("Details", leftWidth + centerWidth, top, width - leftWidth - centerWidth, height, DrawDetails);

        // The 3D view goes first. ImGui draws on top of it in AfterLayout.
        _preview.Draw(_previewArea);
        DrawOpenFolderDialog();
        DrawCharacterFileDialogs();
        _gui.AfterLayout();
        base.Draw(gameTime);
    }

    protected override void UnloadContent()
    {
        _preview.Dispose();
        _texture?.Dispose();
        base.UnloadContent();
    }

    private void StartIndexing(string root, bool rescan)
    {
        if (!Directory.Exists(root))
        {
            _explorer.Status = $"Folder not found: {root}\nUse File > Open asset folder.";
            return;
        }
        _root = root;
        _scanning = true;
        _scanned = 0;
        _indexError = null;
        Task.Run(() =>
        {
            try
            {
                _readyIndex = AssetIndex.Load(root, new Progress<int>(n => _scanned = n), rescan);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _indexError = e.Message;
            }
        });
    }

    // Opens the game database and reads the character specs. Takes a few seconds, so it runs in the background.
    private void StartGomLoading(string root)
    {
        _gomError = null;
        Task.Run(() =>
        {
            try
            {
                var db = GomDatabase.Open(root);
                _readyGom = db;
                _readyCatalog = CharacterCatalog.Load(db);
            }
            catch (Exception e) when (e is IOException or GameFormatException or UnauthorizedAccessException)
            {
                _gomError = e.Message;
            }
        });
    }

    private static void DrawPanel(string title, float x, float y, float w, float h, Action content)
    {
        ImGui.SetNextWindowPos(new Vector2(x, y));
        ImGui.SetNextWindowSize(new Vector2(w, h));
        if (ImGui.Begin(title, PanelFlags)) content();
        ImGui.End();
    }

    private void DrawMenu()
    {
        if (!ImGui.BeginMainMenuBar()) return;
        if (ImGui.BeginMenu("File"))
        {
            if (ImGui.MenuItem("Open asset folder...")) _openFolderRequested = true;
            ImGui.Separator();
            if (ImGui.MenuItem("Save character...", null, false, _character.ToSave() is not null)) _fileDialogRequest = FileDialog.Save;
            if (ImGui.MenuItem("Load character...")) _fileDialogRequest = FileDialog.Load;
            ImGui.Separator();
            if (ImGui.MenuItem("Rebuild index", null, false, !_scanning && _root.Length > 0)) StartIndexing(_root, rescan: true);
            ImGui.Separator();
            if (ImGui.MenuItem("Quit")) Exit();
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu("View"))
        {
            bool wireframe = _preview.Wireframe, cull = _preview.CullBackfaces, grid = _preview.ShowGrid;
            if (ImGui.MenuItem("Wireframe", null, ref wireframe)) _preview.Wireframe = wireframe;
            if (ImGui.MenuItem("Backface culling", null, ref cull)) _preview.CullBackfaces = cull;
            if (ImGui.MenuItem("Grid", null, ref grid)) _preview.ShowGrid = grid;
            ImGui.Separator();
            if (ImGui.MenuItem("Reset camera")) _preview.ResetCamera();
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu("Help"))
        {
            ImGui.MenuItem("Preview: left drag = orbit, right drag = pan, wheel = zoom", null, false, false);
            ImGui.MenuItem("Models: click a model to show it", null, false, false);
            ImGui.EndMenu();
        }
        ImGui.EndMainMenuBar();
    }

    private void DrawOpenFolderDialog()
    {
        if (_openFolderRequested)
        {
            ImGui.OpenPopup("Open asset folder");
            _popupOpen = true;
            _openFolderRequested = false;
        }
        if (!ImGui.BeginPopupModal("Open asset folder", ref _popupOpen, ImGuiWindowFlags.AlwaysAutoResize)) return;

        ImGui.Text("Path to the extracted game folder (the one with art/, gamedata/, ...)");
        ImGui.SetNextItemWidth(600);
        ImGui.InputText("##path", ref _folderInput, 512);
        bool exists = Directory.Exists(_folderInput);
        if (!exists) ImGui.TextColored(ErrorColor, "Folder not found");
        if (ImGui.Button("Open") && exists)
        {
            StartIndexing(_folderInput, rescan: false);
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel")) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    private void DrawDetails()
    {
        if (ImGui.BeginTabBar("##details"))
        {
            // The preview shows what the active tab describes. It is rebuilt when the tab changes.
            var active = ActiveTab.None;
            var modelFlags = _showModelTab ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
            _showModelTab = false;
            bool modelOpen = true;
            if (ImGui.BeginTabItem("Model", ref modelOpen, modelFlags))
            {
                active = ActiveTab.Model;
                DrawModelInfo();
                ImGui.EndTabItem();
            }
            var characterFlags = _showCharacterTab ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
            _showCharacterTab = false;
            bool characterOpen = true;
            if (ImGui.BeginTabItem("Character", ref characterOpen, characterFlags))
            {
                active = ActiveTab.Character;
                _character.Draw();
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();

            if (active != ActiveTab.None && active != _activeTab)
            {
                _activeTab = active;
                if (active == ActiveTab.Character) _character.Rebuild();
                else if (_model is not null) _preview.Load(_model);
                if (active == ActiveTab.Model && _baseImage is not null) RebuildTexture();
            }
        }
    }

    private void DrawModelInfo()
    {
        if (_error is not null) ImGui.TextColored(ErrorColor, _error);
        if (_model is null)
        {
            ImGui.TextDisabled("Click a model in the list.");
            return;
        }

        ImGui.TextWrapped(_modelPath);
        ImGui.Text($"Format version {_model.Version}");
        ImGui.Text($"Materials: {string.Join(", ", _model.Materials)}");
        DrawTextureSection();
        foreach (var mesh in _model.Meshes)
        {
            ImGui.Separator();
            ImGui.Text(mesh.Name);
            ImGui.Text($"{mesh.VertexCount} vertices, {mesh.Indices.Length / 3} triangles");
            ImGui.Text($"Vertex flags {(int)mesh.Flags}: {mesh.Flags}");
            ImGui.Text($"UV sets: {mesh.UvSets.Count}");
            if (ImGui.TreeNode($"Pieces ({mesh.Pieces.Count})##{mesh.Name}"))
            {
                foreach (var p in mesh.Pieces)
                    ImGui.Text($"triangles {p.StartTriangle}..{p.StartTriangle + p.TriangleCount}, material {_model.Materials[p.MaterialIndex]}");
                ImGui.TreePop();
            }
            if (ImGui.TreeNode($"Bones ({mesh.Bones.Count})##{mesh.Name}"))
            {
                foreach (var b in mesh.Bones) ImGui.BulletText(b.Name);
                ImGui.TreePop();
            }
        }
    }

    private void DrawTextureSection()
    {
        ImGui.Separator();
        DrawAppearance();
        DrawColors();
        ImGui.Text("Texture");

        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##texture", _texturePath ?? "(none)"))
        {
            if (ImGui.Selectable("(none)", _texturePath is null)) _pendingTexture = "";
            foreach (var candidate in _textureCandidates)
                if (ImGui.Selectable(candidate, candidate == _texturePath)) _pendingTexture = candidate;
            ImGui.EndCombo();
        }

        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint("##tsearch", "Search any texture by name...", ref _textureQuery, 128))
            _textureResults = _index is null || _textureQuery.Length < 3 ? [] : _index.SearchTextures(_textureQuery, 40).ToList();
        if (_textureResults.Count > 0 && ImGui.BeginListBox("##tresults", new Vector2(-1, 110)))
        {
            foreach (var t in _textureResults)
                if (ImGui.Selectable(t, t == _texturePath)) _pendingTexture = t;
            ImGui.EndListBox();
        }

        if (_texture is not null)
        {
            float size = Math.Min(ImGui.GetContentRegionAvail().X, 256);
            ImGui.Image(_textureId, new Vector2(size, size));
            ImGui.TextDisabled($"{_texture.Width}x{_texture.Height}");
        }
    }

    // Assets from index.xml that use this model, and the material variants of the chosen asset.
    private void DrawAppearance()
    {
        if (_matches.Count == 0)
        {
            ImGui.TextDisabled("No asset (index.xml) uses this model.");
            return;
        }

        var match = _matches[_matchIndex];
        ImGui.Text($"Asset ({_matches.Count}) - slot {match.Slot}{(match.IsAttachment ? ", attachment" : "")}");
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##asset", match.Asset.ArtName))
        {
            for (int i = 0; i < _matches.Count; i++)
                if (ImGui.Selectable($"{_matches[i].Asset.ArtName}##a{i}", i == _matchIndex)) SelectAsset(i);
            ImGui.EndCombo();
        }

        var variants = match.Asset.Materials;
        if (variants.Count == 0) return;
        ImGui.Text($"Material variant ({variants.Count})");
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##variant", Path.GetFileNameWithoutExtension(variants[_variantIndex].FileName)))
        {
            for (int i = 0; i < variants.Count; i++)
                if (ImGui.Selectable($"{Path.GetFileNameWithoutExtension(variants[i].FileName)}##v{i}", i == _variantIndex))
                    SelectVariant(i);
            ImGui.EndCombo();
        }
    }

    private void SelectAsset(int index)
    {
        _matchIndex = index;
        _variantIndex = 0;
        ApplyFirstUsableVariant();
    }

    private void SelectVariant(int index)
    {
        _variantIndex = index;
        _pendingTexture = VariantTexture(_matches[_matchIndex], index) ?? "";
        _pendingMask = VariantMask(_matches[_matchIndex], index);
        _schemeId = null;
    }

    // Tries the variants of the current asset in order. Stops at the first one that has a texture file.
    private void ApplyFirstUsableVariant()
    {
        var match = _matches[_matchIndex];
        for (int i = 0; i < match.Asset.Materials.Count; i++)
        {
            if (VariantTexture(match, i) is not { } texture) continue;
            _variantIndex = i;
            ApplyTexture(texture, VariantMask(match, i));
            return;
        }
        ApplyTexture(null);
    }

    // Relative path of the diffuse texture of one material variant, or null if the material or file is missing.
    private string? VariantTexture(AppearanceMatch match, int variant)
    {
        if (_index is null) return null;
        var material = _index.Appearances.ReadMaterial(match.Asset.Materials[variant], match.Gender, match.Bodytype);
        string? diffuse = material?.DiffuseMap;
        if (diffuse is null) return null;
        string path = diffuse + ".dds";
        return File.Exists(_index.FullPath(path)) ? path : null;
    }

    // Loads on the game thread. Parsing takes a few milliseconds per file.
    private void LoadModel(string relative)
    {
        if (_index is null || !relative.EndsWith(".gr2", StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            _model = Gr2Reader.Parse(File.ReadAllBytes(_index.FullPath(relative)));
            _modelPath = relative;
            _error = null;
            _preview.Load(_model);
            _textureCandidates = _index.FindTextures(relative);
            _matches = _index.Appearances.Find(relative);
            _matchIndex = _variantIndex = 0;
            _schemeId = _primaryPalette = _secondaryPalette = null;
            if (_matches.Count > 0) ApplyFirstUsableVariant();
            else ApplyTexture(_textureCandidates.Count > 0 ? _textureCandidates[0] : null);
        }
        catch (Exception e) when (e is GameFormatException or IOException)
        {
            _error = $"{Path.GetFileName(relative)}: {e.Message}";
        }
    }

    // Loads the diffuse texture and its color mask (if any), then builds the GPU texture.
    private void ApplyTexture(string? relative, string? maskRelative = null)
    {
        _baseImage = null;
        _maskImage = null;
        _texturePath = null;
        if (relative is not null && _index is not null)
        {
            try
            {
                _baseImage = DdsReader.Decode(File.ReadAllBytes(_index.FullPath(relative)));
                _texturePath = relative;
                if (maskRelative is not null) _maskImage = DdsReader.Decode(File.ReadAllBytes(_index.FullPath(maskRelative)));
            }
            catch (Exception e) when (e is GameFormatException or IOException)
            {
                _error = $"{Path.GetFileName(relative)}: {e.Message}";
            }
        }
        RebuildTexture();
    }

    // Applies the chosen palettes to the loaded images and uploads the result.
    private void RebuildTexture()
    {
        _preview.Texture = null;
        if (_texture is not null)
        {
            _gui.UnbindTexture(_textureId);
            _texture.Dispose();
            _texture = null;
        }
        if (_baseImage is null || _index is null) return;

        var colors = _index.Colors;
        var primary = _primaryPalette is null ? null : colors.ReadPalette(_primaryPalette);
        var secondary = _secondaryPalette is null ? null : colors.ReadPalette(_secondaryPalette);
        var image = PaletteTint.Apply(_baseImage, _maskImage, primary, secondary);
        _texture = TextureLoader.Create(GraphicsDevice, image);
        _textureId = _gui.BindTexture(_texture);
        _preview.Texture = _texture;
    }

    // Relative path of the palette mask of a variant, or null. The default black/white masks are ignored.
    private string? VariantMask(AppearanceMatch match, int variant)
    {
        if (_index is null) return null;
        var material = _index.Appearances.ReadMaterial(match.Asset.Materials[variant], match.Gender, match.Bodytype);
        string? mask = material?.TexturePath("PaletteMaskMap");
        if (mask is null || mask.StartsWith("art/defaultassets", StringComparison.OrdinalIgnoreCase)) return null;
        string path = mask + ".dds";
        return File.Exists(_index.FullPath(path)) ? path : null;
    }

    // Color schemes that suit the current material variant, plus manual palette pickers.
    private void DrawColors()
    {
        if (_index is null || _matches.Count == 0) return;
        var match = _matches[_matchIndex];
        var variants = match.Asset.Materials;
        if (variants.Count == 0) return;
        var colors = _index.Colors;

        ImGui.Text("Colors (approximate)");
        if (_maskImage is null)
        {
            ImGui.TextDisabled("This material has no color mask. Colors would have no effect.");
            return;
        }

        var schemeIds = variants[_variantIndex].ColorSchemeIds;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##scheme", SchemeLabel(_schemeId)))
        {
            if (ImGui.Selectable("(none)", _schemeId is null)) SelectScheme(null);
            foreach (var id in schemeIds)
            {
                var scheme = colors.FindScheme(id);
                if (scheme is not null && ImGui.Selectable($"{scheme.Name}##s{id}", id == _schemeId)) SelectScheme(id);
            }
            ImGui.EndCombo();
        }

        DrawPaletteCombo("Primary", "##primary", ref _primaryPalette);
        DrawPaletteCombo("Secondary", "##secondary", ref _secondaryPalette);
    }

    private string SchemeLabel(string? id) =>
        id is null ? "(no color scheme)" : _index?.Colors.FindScheme(id)?.Name ?? id;

    private void DrawPaletteCombo(string label, string id, ref string? selected)
    {
        var colors = _index!.Colors;
        ImGui.SetNextItemWidth(-60);
        if (ImGui.BeginCombo(id, selected is null ? "(none)" : colors.FindPaletteEntry(selected)?.Name ?? selected))
        {
            if (ImGui.Selectable("(none)", selected is null)) { selected = null; RebuildTexture(); }
            foreach (var palette in colors.Palettes)
            {
                if (!ImGui.Selectable($"{palette.Name}##{id}{palette.Id}", palette.Id == selected)) continue;
                selected = palette.Id;
                _schemeId = null;
                RebuildTexture();
            }
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        ImGui.Text(label);
    }

    // Takes the two palettes that the scheme gives for the slot of the current model.
    private void SelectScheme(string? id)
    {
        _schemeId = id;
        if (id is null)
        {
            _primaryPalette = _secondaryPalette = null;
        }
        else if (_index!.Colors.FindScheme(id) is { } scheme && scheme.Slots.TryGetValue(_matches[_matchIndex].Slot, out var palettes))
        {
            _primaryPalette = palettes.Primary;
            _secondaryPalette = palettes.Secondary;
        }
        RebuildTexture();
    }
}
