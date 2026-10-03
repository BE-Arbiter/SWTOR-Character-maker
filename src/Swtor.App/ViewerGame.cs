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
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;

namespace Swtor.App;

/// <summary>Main window. Layout: menu bar on top, then explorer 25%, 3D preview 40%, details 35%.</summary>
public sealed class ViewerGame : Game
{
    private const float LeftShare = 0.25f, CenterShare = 0.40f;
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
    private Texture2D? _texture;
    private IntPtr _textureId;
    private string? _texturePath;
    private string? _error;
    private string? _pendingModel;
    private string? _pendingTexture;
    private string _textureQuery = "";
    private List<string> _textureResults = [];

    private bool _openFolderRequested;
    private bool _popupOpen = true;
    private string _folderInput = "";

    public ViewerGame(string? initialModel = null)
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
        _explorer.FileSelected += path => _pendingModel = path;
        _initialModel = initialModel;
    }

    private string? _initialModel;

    protected override void Initialize()
    {
        _gui = new ImGuiRenderer(this);
        _gui.RebuildFontAtlas();
        ImGui.GetIO().ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;
        _preview = new ModelPreview(GraphicsDevice);

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
            if (_initialModel is not null)
            {
                _pendingModel = Path.GetRelativePath(_root, _initialModel).Replace('\\', '/');
                _initialModel = null;
            }
        }
        if (_scanning) _explorer.Status = _indexError ?? $"Indexing the asset folder... {_scanned} files seen.\nThis happens once. The result is cached.";

        if (_pendingModel is { } model)
        {
            _pendingModel = null;
            LoadModel(model);
        }
        if (_pendingTexture is { } texture)
        {
            _pendingTexture = null;
            ApplyTexture(texture.Length == 0 ? null : texture);
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
            if (ImGui.BeginTabItem("Model"))
            {
                DrawModelInfo();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Character"))
            {
                ImGui.TextDisabled("Reserved: race, body, head, armor, colors.");
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
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
        ImGui.Text("Texture");
        if (_textureCandidates.Count == 0)
            ImGui.TextDisabled("No texture with the same name. Player parts use appearance data (not read yet).");

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
            ApplyTexture(_textureCandidates.Count > 0 ? _textureCandidates[0] : null);
        }
        catch (Exception e) when (e is GameFormatException or IOException)
        {
            _error = $"{Path.GetFileName(relative)}: {e.Message}";
        }
    }

    private void ApplyTexture(string? relative)
    {
        _preview.Texture = null;
        if (_texture is not null)
        {
            _gui.UnbindTexture(_textureId);
            _texture.Dispose();
            _texture = null;
        }
        _texturePath = null;
        if (relative is null || _index is null) return;

        try
        {
            var image = DdsReader.Decode(File.ReadAllBytes(_index.FullPath(relative)));
            _texture = TextureLoader.Create(GraphicsDevice, image);
            _textureId = _gui.BindTexture(_texture);
            _preview.Texture = _texture;
            _texturePath = relative;
        }
        catch (Exception e) when (e is GameFormatException or IOException)
        {
            _error = $"{Path.GetFileName(relative)}: {e.Message}";
        }
    }
}
