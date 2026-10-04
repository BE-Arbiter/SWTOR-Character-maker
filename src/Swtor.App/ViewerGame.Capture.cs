using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Swtor.App;

// Test option for checking the rendering without a person at the screen.
// SWTOR_SHOT=<file.png> saves the window to a PNG file once a model is shown, then quits.
// SWTOR_CAM=yaw,pitch,distance,x,y,z sets the camera first (angles in radians).
public sealed partial class ViewerGame
{
    private const int CaptureDelayFrames = 500;
    private readonly string? _shotPath = Environment.GetEnvironmentVariable("SWTOR_SHOT");
    private int _shotFrames;

    private void CaptureForTest()
    {
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
