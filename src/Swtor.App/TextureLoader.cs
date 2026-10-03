using Microsoft.Xna.Framework.Graphics;
using Swtor.Formats.Dds;

namespace Swtor.App;

/// <summary>Creates GPU textures from decoded DDS images.</summary>
public static class TextureLoader
{
    /// <summary>Creates a texture with a full mip chain. Call on the game thread.</summary>
    public static Texture2D Create(GraphicsDevice device, DdsImage image)
    {
        var texture = new Texture2D(device, image.Width, image.Height, true, SurfaceFormat.Color);
        byte[] level = image.Rgba;
        int w = image.Width, h = image.Height;
        for (int mip = 0; mip < texture.LevelCount; mip++)
        {
            texture.SetData(mip, null, level, 0, w * h * 4);
            if (w == 1 && h == 1) break;
            (level, w, h) = Halve(level, w, h);
        }
        return texture;
    }

    // Averages each 2x2 pixel group. Odd sizes repeat the last row or column.
    private static (byte[] Data, int Width, int Height) Halve(byte[] src, int w, int h)
    {
        int nw = Math.Max(1, w / 2), nh = Math.Max(1, h / 2);
        var dst = new byte[nw * nh * 4];
        for (int y = 0; y < nh; y++)
        {
            int y0 = Math.Min(y * 2, h - 1), y1 = Math.Min(y * 2 + 1, h - 1);
            for (int x = 0; x < nw; x++)
            {
                int x0 = Math.Min(x * 2, w - 1), x1 = Math.Min(x * 2 + 1, w - 1);
                for (int c = 0; c < 4; c++)
                {
                    int sum = src[(y0 * w + x0) * 4 + c] + src[(y0 * w + x1) * 4 + c]
                        + src[(y1 * w + x0) * 4 + c] + src[(y1 * w + x1) * 4 + c];
                    dst[(y * nw + x) * 4 + c] = (byte)(sum / 4);
                }
            }
        }
        return (dst, nw, nh);
    }
}
