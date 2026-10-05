using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Swtor.Assets;
using Swtor.Formats.Jka;

namespace Swtor.App;

// Test option for checking the rendering without a person at the screen.
// SWTOR_SHOT=<file.png> saves the window to a PNG file once a model is shown, then quits.
// SWTOR_CAM=yaw,pitch,distance,x,y,z sets the camera first (angles in radians).
// SWTOR_JKA_EXPORT="players folder|model name[|sound set[|full[|skin name]]]" exports the character of the Character tab as a new Jedi Academy model, then quits.
// With "players folder|@model.glm" the surfaces are added to that model instead.
public sealed partial class ViewerGame
{
    private const int CaptureDelayFrames = 500;
    private readonly string? _shotPath = Environment.GetEnvironmentVariable("SWTOR_SHOT");
    private int _shotFrames;
    private readonly string? _jkaExportTest = Environment.GetEnvironmentVariable("SWTOR_JKA_EXPORT");

    private void JkaExportForTest()
    {
        if (_jkaExportTest is null || _index is null || !_character.AvailableExportSlots.Any()) return;
        string[] values = _jkaExportTest.Split('|');
        var parts = _character.ExportParts(CharacterPanel.ExportSlots);
        var options = new JkaExportOptions
        {
            PlayersFolder = values[0], VoiceSet = values.Length > 2 && values[2].Length > 0 ? values[2] : null,
            TexturePrefix = values[1].StartsWith('@') ? "swtor_" : "",
            MaxTotalVertices = values.Length > 3 && values[3] == "full" ? 0 : JkaConverter.MaxTotalVertices,
            SkinName = values.Length > 4 ? values[4] : null,
            Character = _character.ToSave(),
        };
        var result = values[1].StartsWith('@') ? JkaExporter.AddToModel(_index, parts, values[1][1..], options) : JkaExporter.CreateNew(_index, parts, values[1], options);
        Console.WriteLine($"{result.ModelPath}: {string.Join(", ", result.Surfaces)}");
        Exit();
    }

    private void CaptureForTest()
    {
        JkaExportForTest();
        if (_shotPath is null || !_preview.HasModel) return;
        _shotFrames++;
        if (_shotFrames >= CaptureDelayFrames - 60 && Environment.GetEnvironmentVariable("SWTOR_CAM")?.Split(',') is { Length: 6 } c
            && c.Select(s => float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0f).ToArray() is var v)
        {
            _preview.SetCamera(v[0], v[1], v[2], new Vector3(v[3], v[4], v[5]));
        }
        if (_shotFrames < CaptureDelayFrames) return;

        int width = GraphicsDevice.PresentationParameters.BackBufferWidth, height = GraphicsDevice.PresentationParameters.BackBufferHeight;
        var pixels = new Color[width * height];
        GraphicsDevice.GetBackBufferData(pixels);
        using var texture = new Texture2D(GraphicsDevice, width, height);
        texture.SetData(pixels);
        using var stream = File.Create(_shotPath);
        texture.SaveAsPng(stream, width, height);
        Exit();
    }
}
