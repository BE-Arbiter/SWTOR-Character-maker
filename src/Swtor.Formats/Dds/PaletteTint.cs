using System.Numerics;
using Swtor.Formats.Xml;

namespace Swtor.Formats.Dds;

/// <summary>
/// Colors a garment texture with two palettes.
/// The red channel of the mask marks the primary color area, the green channel marks the secondary color area.
/// Other pixels keep the original diffuse color.
/// With the palette map (the "_h" texture) the result follows the Garment shader of the game, as the SWTOR Slicers Blender add-on
/// (ZG SWTOR Tools) writes it. See <see cref="DyeColor"/>. Without it, the result is an approximation (see below).
/// A palette with a representative color (the color that the game shows for it) is applied like this: each channel of the area
/// is multiplied by one factor, so that the average color of the area becomes the representative color and the detail of the
/// texture stays. A palette without it uses hue, saturation, brightness and contrast (see <see cref="Blend"/>).
/// The hue and saturation of the palette files do not mean "hue and saturation of the result": with them, a light grey dye came out pink.
/// </summary>
public static class PaletteTint
{
    /// <summary>
    /// Returns a tinted copy of <paramref name="diffuse"/>. Returns the same image when there is
    /// no mask or no palette. The mask may have another size (nearest-pixel sampling).
    /// </summary>
    public static DdsImage Apply(DdsImage diffuse, DdsImage? mask, Palette? primary, Palette? secondary, DdsImage? paletteMap = null)
    {
        if (mask is null || (primary is null && secondary is null)) return diffuse;
        if (paletteMap is not null) return ApplyGame(diffuse, mask, primary, secondary, paletteMap);

        var gainPrimary = Gain(diffuse, mask, primary, 0);
        var gainSecondary = Gain(diffuse, mask, secondary, 1);
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
                if (p1 > 0) Paint(ref r, ref g, ref b, primary!, gainPrimary, p1);
                if (p2 > 0) Paint(ref r, ref g, ref b, secondary!, gainSecondary, p2);
                result[o] = ToByte(r);
                result[o + 1] = ToByte(g);
                result[o + 2] = ToByte(b);
            }
        }
        return new DdsImage(diffuse.Width, diffuse.Height, result);
    }

    // The Garment shader. Per pixel: the weight of the palette areas is red + green of the mask (at most 1). The palette of the pixel is
    // the secondary one when green is more than red, otherwise the primary one. Inside the areas the color comes from the palette map
    // and the palette (DyeColor), the diffuse map is only used outside. A pixel whose palette is not chosen keeps the diffuse color.
    // The mask and the palette map may have another size than the diffuse map (nearest pixel).
    private static DdsImage ApplyGame(DdsImage diffuse, DdsImage mask, Palette? primary, Palette? secondary, DdsImage map)
    {
        var result = (byte[])diffuse.Rgba.Clone();
        for (int y = 0; y < diffuse.Height; y++)
        {
            int my = y * mask.Height / diffuse.Height, hy = y * map.Height / diffuse.Height;
            for (int x = 0; x < diffuse.Width; x++)
            {
                int m = (my * mask.Width + x * mask.Width / diffuse.Width) * 4;
                float mr = mask.Rgba[m] / 255f, mg = mask.Rgba[m + 1] / 255f;
                float weight = Math.Min(mr + mg, 1f);
                var palette = mr < mg ? secondary : primary;
                if (weight <= 0 || palette is null) continue;

                int h = (hy * map.Width + x * map.Width / diffuse.Width) * 4;
                var (r, g, b) = DyeColor(palette, map.Rgba[h] / 255f, map.Rgba[h + 1] / 255f, map.Rgba[h + 2] / 255f, map.Rgba[h + 3] / 255f);
                int o = (y * diffuse.Width + x) * 4;
                result[o] = ToByte(result[o] / 255f * (1 - weight) + r * weight);
                result[o + 1] = ToByte(result[o + 1] / 255f * (1 - weight) + g * weight);
                result[o + 2] = ToByte(result[o + 2] / 255f * (1 - weight) + b * weight);
            }
        }
        return new DdsImage(diffuse.Width, diffuse.Height, result);
    }

    /// <summary>
    /// The color that the game gives to a palette area, from one pixel of the palette map ("_h": red = ambient occlusion, green = hue,
    /// blue = saturation, alpha = lightness, all 0 to 1). The steps are the ones of the Garment shader of the ZG SWTOR Tools add-on:
    /// <code>
    /// hue        = fract(0.3923 * G - 0.09806 + Hue)
    /// saturation = clamp(S0 ^ Saturation * (1 - Saturation), 0, 1)       with S0 = 0.5882 * B
    /// lightness  = Brightness + (1 - Brightness) * Contrast * L0 ^ Contrast   with L0 = 0.70588 * A
    /// occlusion  = clamp(R * (1 + Brightness - Brightness * R), 0, 1)
    /// color      = HSL(hue, saturation, lightness * occlusion) as RGB
    /// </code>
    /// Hue, Saturation, Brightness and Contrast are the values of the palette file. The result is the color as the game shows it (no gamma change).
    /// </summary>
    public static (float R, float G, float B) DyeColor(Palette palette, float occlusion, float hueSample, float saturationSample, float lightnessSample)
    {
        float hue = Fract(0.3923f * hueSample - 0.09806f + palette.Hue);
        float saturation = Math.Clamp(MathF.Pow(0.5882f * saturationSample, palette.Saturation) * (1 - palette.Saturation), 0f, 1f);
        float baseLight = 0.70588f * lightnessSample;
        float lightness = palette.Brightness + (1 - palette.Brightness) * palette.Contrast * MathF.Pow(baseLight, palette.Contrast);
        float ao = Math.Clamp(occlusion * (1 + palette.Brightness - palette.Brightness * occlusion), 0f, 1f);
        return HslToRgb(hue, saturation, Math.Max(lightness * ao, 0f));
    }

    private static float Fract(float v) => v - MathF.Floor(v);

    // HSL to RGB through HSV, as the shader does it: V = L + S * (1 - |2L - 1|) / 2 and S(hsv) = 2 (V - L) / V.
    private static (float R, float G, float B) HslToRgb(float hue, float saturation, float lightness)
    {
        float v = lightness + saturation * (1 - MathF.Abs(2 * lightness - 1)) / 2;
        float s = v == 0 ? 0 : 2 * (v - lightness) / v;
        float h = (hue - MathF.Floor(hue)) * 6f;
        int sector = (int)h;
        float f = h - sector;
        float p = v * (1 - s), q = v * (1 - s * f), t = v * (1 - s * (1 - f));
        var (r, g, b) = sector switch
        {
            0 => (v, t, p),
            1 => (q, v, p),
            2 => (p, v, t),
            3 => (p, q, v),
            4 => (t, p, v),
            _ => (v, p, q),
        };
        return (Math.Clamp(r, 0f, 1f), Math.Clamp(g, 0f, 1f), Math.Clamp(b, 0f, 1f));
    }

    // Applies a palette to one pixel with the weight of the mask: by gain when there is one, otherwise by hue and brightness.
    private static void Paint(ref float r, ref float g, ref float b, Palette palette, Vector3? gain, float amount)
    {
        if (gain is not { } k)
        {
            Blend(ref r, ref g, ref b, palette, amount);
            return;
        }
        r = Math.Clamp(r * (1 + (k.X - 1) * amount), 0f, 1f);
        g = Math.Clamp(g * (1 + (k.Y - 1) * amount), 0f, 1f);
        b = Math.Clamp(b * (1 + (k.Z - 1) * amount), 0f, 1f);
    }

    // The factor per channel that gives the area its representative color. Null when the palette has none (or is a filler),
    // or when the area is empty. The area is the weight of one channel of the mask (0 red, 1 green). The mask may have another size.
    private static Vector3? Gain(DdsImage diffuse, DdsImage mask, Palette? palette, int channel)
    {
        if (palette?.Representative is not { } target || palette.IsPlaceholder) return null;
        double sumR = 0, sumG = 0, sumB = 0, sumW = 0;
        for (int y = 0; y < diffuse.Height; y++)
        {
            int my = y * mask.Height / diffuse.Height;
            for (int x = 0; x < diffuse.Width; x++)
            {
                float w = mask.Rgba[((my * mask.Width) + x * mask.Width / diffuse.Width) * 4 + channel] / 255f;
                if (w <= 0) continue;
                int o = (y * diffuse.Width + x) * 4;
                sumR += diffuse.Rgba[o] * w;
                sumG += diffuse.Rgba[o + 1] * w;
                sumB += diffuse.Rgba[o + 2] * w;
                sumW += w;
            }
        }
        if (sumW <= 0) return null;

        // The limit stops very dark areas from exploding.
        float Factor(float wanted, double sum) => Math.Clamp(wanted * 255f / Math.Max((float)(sum / sumW), 1f), 0f, 4f);
        return new Vector3(Factor(target.X, sumR), Factor(target.Y, sumG), Factor(target.Z, sumB));
    }

    /// <summary>
    /// Makes a palette from the four values of the game shader (a color that no palette file has). Its representative color is the
    /// result for a typical texel of a palette map (occlusion 1, G 0.42, B 0.8, A 0.43), like the representative colors of the game files.
    /// </summary>
    /// <param name="hue">0 to 1, added to the hue of the palette map.</param>
    /// <param name="saturation">0 (full color) to 1 (grey). The game value: a higher value gives less color.</param>
    /// <param name="brightness">About -1 to 1, neutral 0.</param>
    /// <param name="contrast">0 to about 3, neutral 1. With 0 the lightness is the brightness.</param>
    public static Palette Custom(float hue, float saturation, float brightness, float contrast)
    {
        var palette = new Palette("custom", hue, saturation, brightness, contrast);
        var (r, g, b) = DyeColor(palette, 1f, 0.42f, 0.8f, 0.43f);
        return palette with { Representative = new Vector3(r, g, b) };
    }

    /// <summary>
    /// The color of a palette for the swatches of the color pickers, as RGB in the range 0 to 1. It is the representative color that the
    /// palette file gives. Without it, it is the color that the palette gives to a mid-grey pixel.
    /// </summary>
    public static (float R, float G, float B) Swatch(Palette palette)
    {
        if (palette.Representative is { } color && !palette.IsPlaceholder)
            return (Math.Clamp(color.X, 0, 1), Math.Clamp(color.Y, 0, 1), Math.Clamp(color.Z, 0, 1));
        float r = 0.5f, g = 0.5f, b = 0.5f;
        Blend(ref r, ref g, ref b, palette, 1f);
        return (r, g, b);
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
