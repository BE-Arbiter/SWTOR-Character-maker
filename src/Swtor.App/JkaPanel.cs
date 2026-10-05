using System.Numerics;
using System.Text.Json;
using ImGuiNET;
using Microsoft.Xna.Framework.Graphics;
using Swtor.Assets;
using Swtor.Formats;
using Swtor.Formats.Jka;

namespace Swtor.App;

/// <summary>
/// The "Jedi Academy" tab. Exports the character of the Character tab to a Jedi Academy player model (.glm) on the
/// humanoid skeleton: either as a new model in its own folder, or as new surfaces in an existing model.
/// </summary>
public sealed class JkaPanel
{
    private sealed record Settings(string PlayersFolder, int TextureSize, string AddPrefix, string NewName, bool UnlimitedVertices = false);

    private static readonly string SettingsFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SwtorCharacterMaker", "jka.json");

    private static readonly int[] TextureSizes = [512, 1024, 2048];

    private static readonly string[] VertexBudgets = [$"Standard game ({JkaConverter.MaxTotalVertices:N0} vertices)", "No limit (larger engine heap)"];

    private static readonly Dictionary<string, string> SlotLabels = new()
    {
        ["head"] = "Head", ["face"] = "Helmet", ["hair"] = "Hair", ["facehair"] = "Face hair", ["chest"] = "Chest (torso and arms)", ["hand"] = "Hands",
        ["leg"] = "Legs (hips and legs)", ["boot"] = "Boots", ["waist"] = "Waist", ["bracer"] = "Bracers",
    };

    private readonly GraphicsDevice _device;
    private readonly ModelPreview _preview;
    private readonly CharacterPanel _character;
    private readonly List<Texture2D> _textures = [];
    private readonly Dictionary<string, bool> _selected = [];

    private AssetIndex? _index;
    private string _playersFolder = DefaultPlayersFolder();
    private string _newName = "swtor_character";
    private string _addPrefix = "swtor_";
    private string _skinName = "";
    private int _textureSize = 1024;
    private bool _unlimitedVertices;
    private bool _addMode;
    private int _target;
    private string _customTarget = "";
    private List<string> _models = [];
    private string _scannedFolder = "\0";

    private Task<JkaExportResult>? _task;
    private string? _message;
    private bool _messageIsError;
    private string? _lastModel;

    public JkaPanel(GraphicsDevice device, ModelPreview preview, CharacterPanel character)
    {
        _device = device;
        _preview = preview;
        _character = character;
        LoadSettings();
    }

    public void SetIndex(AssetIndex? index) => _index = index;

    public void Draw()
    {
        PollTask();
        ImGui.TextWrapped("Exports the character of the Character tab to a Jedi Academy player model on the _humanoid skeleton. No game file is needed: put the models in any folder, or in the players folder of the game to install them.");
        ImGui.Separator();

        ImGui.Text("Players folder");
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText("##players", ref _playersFolder, 512)) SaveSettings();
        bool folderOk = IsUsableFolder(_playersFolder);
        bool folderExists = folderOk && Directory.Exists(_playersFolder);
        if (!folderOk) ImGui.TextColored(new Vector4(1, 0.5f, 0.4f, 1), "This path is not valid.");
        else if (!folderExists) ImGui.TextDisabled("The folder does not exist. It will be created.");
        ScanModels(folderExists);

        ImGui.Separator();
        if (ImGui.RadioButton("Create new Jedi Academy model", !_addMode)) _addMode = false;
        if (ImGui.RadioButton("Add surfaces to an existing model", _addMode)) _addMode = true;

        ImGui.Spacing();
        if (_addMode) DrawAddTarget();
        else DrawNewTarget();

        ImGui.Separator();
        ImGui.Text("What to export");
        var available = _character.AvailableExportSlots.ToList();
        if (available.Count == 0) ImGui.TextDisabled("Nothing to export yet. Open the Character tab first.");
        foreach (string slot in available)
        {
            bool on = _selected.GetValueOrDefault(slot, true);
            if (ImGui.Checkbox($"{SlotLabels.GetValueOrDefault(slot, slot)}##jka{slot}", ref on)) _selected[slot] = on;
        }

        ImGui.Separator();
        ImGui.SetNextItemWidth(120);
        int sizeIndex = Math.Max(0, Array.IndexOf(TextureSizes, _textureSize));
        if (ImGui.Combo("Largest texture side", ref sizeIndex, TextureSizes.Select(s => s.ToString()).ToArray(), TextureSizes.Length))
        {
            _textureSize = TextureSizes[sizeIndex];
            SaveSettings();
        }
        int budgetIndex = _unlimitedVertices ? 1 : 0;
        ImGui.SetNextItemWidth(260);
        if (ImGui.Combo("Vertex budget", ref budgetIndex, VertexBudgets, VertexBudgets.Length))
        {
            _unlimitedVertices = budgetIndex == 1;
            SaveSettings();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("The standard game stops (\"Ran out of transform space\") above about 12,800 (256 KB heap) or 25,000 (512 KB heap) vertices per model.\nChoose \"No limit\" only with an engine that has a larger G2_MINIHEAP_SIZE.");
        if (_addMode)
        {
            ImGui.SetNextItemWidth(120);
            if (ImGui.InputText("Texture prefix", ref _addPrefix, 32)) SaveSettings();
        }

        ImGui.Spacing();
        var chosen = available.Where(s => _selected.GetValueOrDefault(s, true)).ToList();
        string? problem = !folderOk ? "Enter a valid output folder."
            : _index is null ? "The asset index is not loaded yet."
            : chosen.Count == 0 ? "Choose at least one part."
            : _addMode && SelectedModel() is null ? "Choose a model."
            : _addMode && _skinName.Trim().Length > 0 && !JkaExporter.IsValidSkinName(_skinName.Trim()) ? "The skin name is not valid: letters, digits, '_' or '-', not \"default\"."
            : !_addMode && (_newName.Length == 0 || _newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) ? "The name is not valid."
            : null;
        bool busy = _task is not null;
        if (problem is not null || busy) ImGui.BeginDisabled();
        if (ImGui.Button(_addMode ? "Add to model" : "Create model", new Vector2(-1, 0))) StartExport(chosen);
        if (problem is not null || busy) ImGui.EndDisabled();
        if (busy) ImGui.TextDisabled("Exporting...");
        else if (problem is not null) ImGui.TextDisabled(problem);

        if (_message is not null) ImGui.TextColored(_messageIsError ? new Vector4(1, 0.5f, 0.4f, 1) : new Vector4(0.5f, 0.9f, 0.5f, 1), _message);
        DrawPreviewButtons();
    }

    private void DrawNewTarget()
    {
        ImGui.Text("Name of the new model (folder name)");
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText("##name", ref _newName, 64)) SaveSettings();
        string file = Path.Combine(_playersFolder, _newName, "model.glm");
        if (File.Exists(file)) ImGui.TextColored(new Vector4(1, 0.8f, 0.4f, 1), "This model exists. Its files will be replaced.");
        else ImGui.TextDisabled(file);
    }

    private void DrawAddTarget()
    {
        ImGui.Text("Model to change");
        _target = Math.Clamp(_target, 0, Math.Max(0, _models.Count - 1));
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##target", _models.Count == 0 ? "(none found)" : _models[_target]))
        {
            for (int i = 0; i < _models.Count; i++)
                if (ImGui.Selectable($"{_models[i]}##t{i}", i == _target)) _target = i;
            ImGui.EndCombo();
        }
        ImGui.Text("Or the path of another .glm file");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##customglm", ref _customTarget, 512);
        if (_customTarget.Length > 0 && !File.Exists(_customTarget)) ImGui.TextColored(new Vector4(1, 0.5f, 0.4f, 1), "File not found.");
        ImGui.Text("Name of the new skin (empty: next number)");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##skinname", ref _skinName, 40);
        string skin = _skinName.Trim();
        if (SelectedModel() is { } model && skin.Length > 0 && JkaExporter.IsValidSkinName(skin))
        {
            string folder = Path.GetDirectoryName(Path.GetFullPath(model))!;
            string file = JkaExporter.SkinPath(folder, skin);
            if (File.Exists(file)) ImGui.TextColored(new Vector4(1, 0.8f, 0.4f, 1), $"{Path.GetFileName(file)} exists. It will be replaced.");
            else ImGui.TextDisabled($"{Path.GetFileName(file)}, in the game: model {Path.GetFileName(folder)}/{skin}");
        }
        ImGui.TextWrapped("Only the changed parts are added: parts that the model already has are skipped, existing surfaces and textures are kept. The new skin shows this character. The old file is kept as model.glm.bak.");
    }

    private void DrawPreviewButtons()
    {
        ImGui.Separator();
        if (_lastModel is not null && File.Exists(_lastModel) && ImGui.Button("Show the exported model", new Vector2(-1, 0))) ShowModel(_lastModel);
        if (_addMode && SelectedModel() is { } model && ImGui.Button("Show the selected model", new Vector2(-1, 0))) ShowModel(model);
        if (ImGui.Button("Show the character", new Vector2(-1, 0))) _character.Rebuild();
    }

    private void ShowModel(string path)
    {
        try
        {
            foreach (var texture in _textures) texture.Dispose();
            _textures.Clear();
            JkaModelView.Load(_preview, _device, path, _textures);
            _message = null;
        }
        catch (Exception e) when (e is GameFormatException or IOException)
        {
            (_message, _messageIsError) = ($"{Path.GetFileName(path)}: {e.Message}", true);
        }
    }

    // The file of the "add" mode: the typed path when there is one, otherwise the model chosen in the list.
    private string? SelectedModel() =>
        _customTarget.Length > 0 ? (File.Exists(_customTarget) ? _customTarget : null)
        : _models.Count == 0 ? null : Path.Combine(_playersFolder, _models[Math.Clamp(_target, 0, _models.Count - 1)], "model.glm");

    private void StartExport(List<string> slots)
    {
        var parts = _character.ExportParts(slots);
        var save = _character.ToSave();
        var index = _index!;
        string players = _playersFolder;
        bool add = _addMode;
        string name = _newName, prefix = _addPrefix, skin = _skinName.Trim();
        string? target = SelectedModel();
        int size = _textureSize;
        int vertexBudget = _unlimitedVertices ? 0 : JkaConverter.MaxTotalVertices;
        _message = null;
        _task = Task.Run(() => add
            ? JkaExporter.AddToModel(index, parts, target!, new JkaExportOptions { PlayersFolder = players, TexturePrefix = prefix, MaxTextureSize = size, MaxTotalVertices = vertexBudget, SkinName = skin, Character = save })
            : JkaExporter.CreateNew(index, parts, name, new JkaExportOptions { PlayersFolder = players, MaxTextureSize = size, MaxTotalVertices = vertexBudget, Character = save }));
    }

    private void PollTask()
    {
        if (_task is not { IsCompleted: true } task) return;
        _task = null;
        try
        {
            var result = task.GetAwaiter().GetResult();
            _lastModel = result.ModelPath;
            (_message, _messageIsError) = ($"Done: {result.ModelPath}\n{string.Join("\n", result.Notes)}\nSurfaces: {string.Join(", ", result.Surfaces)}", false);
            _models = [];
            _scannedFolder = "\0";
        }
        catch (Exception e) when (e is IOException or GameFormatException or InvalidDataException or ArgumentException or UnauthorizedAccessException or InvalidOperationException)
        {
            (_message, _messageIsError) = (e.Message, true);
        }
    }

    // The models of the players folder: the folders that have a model.glm. The folders that start with "_" are skeletons.
    // A folder that the export can write: an absolute path without bad characters. Missing folders are created.
    private static bool IsUsableFolder(string path)
    {
        try { return path.Length > 0 && Path.IsPathFullyQualified(path) && Path.GetFullPath(path).Length > 3; }
        catch (ArgumentException) { return false; }
    }

    private void ScanModels(bool folderOk)
    {
        if (_scannedFolder == _playersFolder) return;
        _scannedFolder = _playersFolder;
        _models = folderOk
            ? Directory.GetDirectories(_playersFolder).Where(d => !Path.GetFileName(d).StartsWith('_') && File.Exists(Path.Combine(d, "model.glm")))
                .Select(d => Path.GetFileName(d)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList()
            : [];
    }

    private static string DefaultPlayersFolder()
    {
        return Environment.GetEnvironmentVariable("JKA_PLAYERS") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SWTOR Character Maker", "Jedi Academy", "models", "players");
    }

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsFile) || JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsFile)) is not { } s) return;
            if (s.PlayersFolder.Length > 0 && Environment.GetEnvironmentVariable("JKA_PLAYERS") is null) _playersFolder = s.PlayersFolder;
            _textureSize = TextureSizes.Contains(s.TextureSize) ? s.TextureSize : _textureSize;
            _addPrefix = s.AddPrefix;
            _newName = s.NewName;
            _unlimitedVertices = s.UnlimitedVertices;
        }
        catch (Exception e) when (e is IOException or JsonException) { }
    }

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
            File.WriteAllText(SettingsFile, JsonSerializer.Serialize(new Settings(_playersFolder, _textureSize, _addPrefix, _newName, _unlimitedVertices)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
