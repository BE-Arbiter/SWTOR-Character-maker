using MonoGame.ImGuiNet;
using System.Numerics;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;
using ImGuiNET;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Swtor.Formats;
using Swtor.Formats.Gr2;

namespace Swtor.App;

/// <summary>Main window. Layout: menu bar on top, then explorer 25%, 3D preview 40%, details 35%.</summary>
public sealed class ViewerGame : Game
{
    private const float LeftShare = 0.25f, CenterShare = 0.40f;
    private const ImGuiWindowFlags PanelFlags = ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize
        | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBringToFrontOnFocus;

    private readonly GraphicsDeviceManager _graphics;
    private readonly FileExplorer _explorer = new();
    private ImGuiRenderer _gui = null!;
    private ModelPreview _preview = null!;
    private MouseState _mouse;
    private Microsoft.Xna.Framework.Rectangle _previewArea;

    private Gr2Model? _model;
    private string? _modelPath;
    private string? _error;
    private string? _pendingModelPath;
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
        _explorer.FileOpened += path => _pendingModelPath = path;
        _pendingModelPath = initialModel;
    }

    protected override void Initialize()
    {
        _gui = new ImGuiRenderer(this);
        _gui.RebuildFontAtlas();
        ImGui.GetIO().ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;
        _preview = new ModelPreview(GraphicsDevice);

        string root = Environment.GetEnvironmentVariable("SWTOR_ASSETS") ?? @"C:\jka_tor_assets\resources";
        _folderInput = root;
        _explorer.SetRoot(root);
        base.Initialize();
    }

    protected override void Update(GameTime gameTime)
    {
        var current = Mouse.GetState();
        // The preview is the only part of the window that is not an ImGui window.
        if (IsActive && !ImGui.GetIO().WantCaptureMouse && _previewArea.Contains(current.Position))
            _preview.HandleInput(_mouse, current);
        _mouse = current;

        if (_pendingModelPath is { } path)
        {
            _pendingModelPath = null;
            LoadModel(path);
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
        DrawPanel("Explorer", 0, top, leftWidth, height, _explorer.Draw);
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
        base.UnloadContent();
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
            ImGui.MenuItem("Explorer: click = select, double click = open", null, false, false);
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
        if (!exists) ImGui.TextColored(new Vector4(1, 0.5f, 0.4f, 1), "Folder not found");
        if (ImGui.Button("Open") && exists)
        {
            _explorer.SetRoot(_folderInput);
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
        if (_error is not null) ImGui.TextColored(new Vector4(1, 0.5f, 0.4f, 1), _error);
        if (_model is null)
        {
            ImGui.TextDisabled("Double click a .gr2 file in the explorer.");
            return;
        }

        ImGui.TextWrapped(_modelPath);
        ImGui.Text($"Format version {_model.Version}");
        ImGui.Text($"Materials: {string.Join(", ", _model.Materials)}");
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

    // Loads on the game thread. Parsing takes a few milliseconds per file.
    private void LoadModel(string path)
    {
        if (!path.EndsWith(".gr2", StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            _model = Gr2Reader.Parse(File.ReadAllBytes(path));
            _modelPath = path;
            _error = null;
            _preview.Load(_model);
        }
        catch (Exception e) when (e is GameFormatException or IOException)
        {
            _error = $"{Path.GetFileName(path)}: {e.Message}";
        }
    }
}
