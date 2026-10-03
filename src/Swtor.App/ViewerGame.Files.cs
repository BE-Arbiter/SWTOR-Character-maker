using ImGuiNET;
using Swtor.Assets;

namespace Swtor.App;

// Dialogs to save and load the character. ImGui has no file dialog, so the path is typed or picked from the saved files.
public sealed partial class ViewerGame
{
    private static readonly System.Numerics.Vector4 DialogError = new(1, 0.5f, 0.4f, 1);

    private void DrawCharacterFileDialogs()
    {
        if (_fileDialogRequest != FileDialog.None)
        {
            _characterPath = Path.Combine(CharacterFolder, _fileDialogRequest == FileDialog.Save ? "character.json" : "");
            _dialogMessage = null;
            _fileDialogOpen = true;
            ImGui.OpenPopup(_fileDialogRequest == FileDialog.Save ? "Save character" : "Load character");
            _fileDialogRequest = FileDialog.None;
        }

        if (ImGui.BeginPopupModal("Save character", ref _fileDialogOpen, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.Text("File to write (JSON):");
            ImGui.SetNextItemWidth(640);
            ImGui.InputText("##savepath", ref _characterPath, 512);
            if (_dialogMessage is not null) ImGui.TextColored(DialogError, _dialogMessage);
            if (ImGui.Button("Save") && SaveCharacter(_characterPath)) ImGui.CloseCurrentPopup();
            ImGui.SameLine();
            if (ImGui.Button("Cancel")) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }

        if (ImGui.BeginPopupModal("Load character", ref _fileDialogOpen, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.Text($"Saved characters in {CharacterFolder}");
            if (ImGui.BeginListBox("##files", new System.Numerics.Vector2(640, 160)))
            {
                if (Directory.Exists(CharacterFolder))
                    foreach (string file in Directory.EnumerateFiles(CharacterFolder, "*.json").Order())
                        if (ImGui.Selectable(Path.GetFileName(file), file == _characterPath)) _characterPath = file;
                ImGui.EndListBox();
            }
            ImGui.SetNextItemWidth(640);
            ImGui.InputText("##loadpath", ref _characterPath, 512);
            if (_dialogMessage is not null) ImGui.TextColored(DialogError, _dialogMessage);
            if (ImGui.Button("Load") && LoadCharacter(_characterPath)) ImGui.CloseCurrentPopup();
            ImGui.SameLine();
            if (ImGui.Button("Cancel")) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
    }

    private bool SaveCharacter(string path)
    {
        try
        {
            _character.ToSave()?.Save(path);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            _dialogMessage = e.Message;
            return false;
        }
    }

    private bool LoadCharacter(string path)
    {
        try
        {
            _character.Apply(CharacterSave.Load(path));
            _showCharacterTab = true;
            if (_character.LoadError is { } error)
            {
                _dialogMessage = error;
                return false;
            }
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or NotSupportedException)
        {
            _dialogMessage = e.Message;
            return false;
        }
    }
}
