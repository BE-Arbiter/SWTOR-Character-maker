namespace Swtor.Formats.Dds;

/// <summary>
/// Gives a plain English name to a color ("Black", "Navy", "Dark red"), so that dyes can be found by name.
/// The game has no text for single palettes in the extract, so the name comes from the color itself.
/// </summary>
public static class ColorNames
{
    /// <summary>Name of a color. The values are sRGB, 0 to 1.</summary>
    public static string Describe(float r, float g, float b)
    {
        float max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        float lightness = (max + min) / 2;
        float chroma = max - min;
        float saturation = chroma <= 0 ? 0 : chroma / (1 - Math.Abs(2 * lightness - 1));

        if (lightness < 0.10f) return "Black";
        if (lightness > 0.92f) return "White";
        if (saturation < 0.12f)
            return lightness switch { < 0.28f => "Dark gray", < 0.62f => "Gray", _ => "Light gray" };

        float hue = Hue(r, g, b, max, chroma);
        string baseName = hue switch
        {
            < 15 or >= 345 => "red",
            < 45 => lightness < 0.38f ? "brown" : "orange",
            < 70 => lightness < 0.38f ? "olive" : "yellow",
            < 165 => "green",
            < 200 => "teal",
            < 255 => lightness < 0.30f ? "navy" : "blue",
            < 290 => "purple",
            _ => lightness > 0.6f ? "pink" : "magenta",
        };

        string prefix = baseName is "navy" or "brown" or "olive" ? "" : lightness < 0.25f ? "dark " : lightness > 0.72f ? "light " : "";
        string name = prefix + baseName;
        return char.ToUpperInvariant(name[0]) + name[1..];
    }

    // Hue in degrees, 0 to 360.
    private static float Hue(float r, float g, float b, float max, float chroma)
    {
        if (chroma <= 0) return 0;
        float h = max == r ? (g - b) / chroma % 6 : max == g ? (b - r) / chroma + 2 : (r - g) / chroma + 4;
        h *= 60;
        return h < 0 ? h + 360 : h;
    }
}
