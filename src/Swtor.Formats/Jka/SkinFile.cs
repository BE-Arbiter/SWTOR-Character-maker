using System.Text;

namespace Swtor.Formats.Jka;

/// <summary>
/// A Jedi Academy skin file (<c>model_default.skin</c>): one line "surface,shader" for each surface.
/// Surface names are not case sensitive. The order of the lines is kept.
/// </summary>
public sealed class SkinFile
{
    private readonly List<(string Surface, string Shader)> _lines = [];

    public IReadOnlyList<(string Surface, string Shader)> Lines => _lines;

    public static SkinFile Parse(string text)
    {
        var skin = new SkinFile();
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            int comma = line.IndexOf(',');
            if (comma <= 0) continue;
            skin.Set(line[..comma].Trim(), line[(comma + 1)..].Trim());
        }
        return skin;
    }

    /// <summary>Sets the shader of a surface. Replaces the existing line or adds a new one at the end.</summary>
    public void Set(string surface, string shader)
    {
        int index = _lines.FindIndex(l => l.Surface.Equals(surface, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) _lines[index] = (_lines[index].Surface, shader);
        else _lines.Add((surface, shader));
    }

    /// <summary>Gives the line of a surface another surface name. Does nothing when the skin has no line for it.</summary>
    public void Rename(string surface, string newName)
    {
        int index = _lines.FindIndex(l => l.Surface.Equals(surface, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) _lines[index] = (newName, _lines[index].Shader);
    }

    public string? Get(string surface) =>
        _lines.Where(l => l.Surface.Equals(surface, StringComparison.OrdinalIgnoreCase)).Select(l => l.Shader).FirstOrDefault();

    public override string ToString()
    {
        var sb = new StringBuilder();
        foreach (var (surface, shader) in _lines) sb.Append(surface).Append(',').Append(shader).Append("\r\n");
        return sb.ToString();
    }
}
