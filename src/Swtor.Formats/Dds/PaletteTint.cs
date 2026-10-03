using Swtor.Formats.Xml;

namespace Swtor.Formats.Dds;

/// <summary>
/// Colors a garment texture with two palettes.
/// This is an approximation: the game shader is not known. The red channel of the mask marks the
/// primary color area, the green channel marks the secondary color area. Other pixels keep the
/// original diffuse color.
/// </summary>
public static class PaletteTint
{
    /// <summary>
    /// Returns a tinted copy of <paramref name="diffuse"/>. Returns the same image when there is
    /// no mask or no palette. The mask may have another size (nearest-pixel sampling).
    /// </summary>
    public static DdsImage Apply(DdsImage diffuse, DdsImage? mask, Palette? primary, Palette? secondary)
    {
        if (mask is null || (primary is null && secondary is null)) return diffuse;

        var result = (byte[])diffuse.Rgba.Clone();
        for (int y = 0; y < diffuse.Height; y++)
        {
            int my = y * mask.Height / diffuse.Height;
            for (int x = 0; x < diffuse.Width; x++)
            {
                int mx = x * mask.Width / diffuse.Width;
                int m = (my * mask.Width + mx) * 4;
                float p1 = primary is null ? 0 : mask.Rgba[m] / 255f;
                float p2 = secondary is null ? 0 : mask.Rgba[m + 1] / 255f;
                if (p1 <= 0 && p2 <= 0) continue;

                int o = (y * diffuse.Width + x) * 4;
                float r = result[o] / 255f, g = result[o + 1] / 255f, b = result[o + 2] / 255f;
                if (p1 > 0) Blend(ref r, ref g, ref b, primary!, p1);
                if (p2 > 0) Blend(ref r, ref g, ref b, secondary!, p2);
                result[o] = ToByte(r);
                result[o + 1] = ToByte(g);
                result[o + 2] = ToByte(b);
            }
        }
        return new DdsImage(diffuse.Width, diffuse.Height, result);
    }

    // Keeps the brightness of the pixel (with contrast and brightness from the palette) and replaces the color.
    private static void Blend(ref float r, ref float g, ref float b, Palette palette, float amount)
    {
        float luma = 0.299f * r + 0.587f * g + 0.114f * b;
        float level = Math.Clamp((luma - 0.5f) * palette.Contrast + 0.5f + palette.Brightness, 0f, 1f);
        var (hr, hg, hb) = HueColor(palette.Hue);
        float s = Math.Clamp(palette.Saturation, 0f, 1f);
        float tr = level * (1 - s + s * hr), tg = level * (1 - s + s * hg), tb = level * (1 - s + s * hb);
        r += (tr - r) * amount;
        g += (tg - g) * amount;
        b += (tb - b) * amount;
    }

    // Fully saturated color for a hue in the range 0 to 1 (the hue wraps around).
    private static (float R, float G, float B) HueColor(float hue)
    {
        float h = (hue - MathF.Floor(hue)) * 6f;
        float x = 1 - MathF.Abs(h % 2 - 1);
        return (int)h switch
        {
            0 => (1, x, 0),
            1 => (x, 1, 0),
            2 => (0, 1, x),
            3 => (0, x, 1),
            4 => (x, 0, 1),
            _ => (1, 0, x),
        };
    }

    private static byte ToByte(float v) => (byte)Math.Clamp((int)(v * 255f + 0.5f), 0, 255);
}
