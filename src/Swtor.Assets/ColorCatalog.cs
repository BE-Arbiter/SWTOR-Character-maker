using Swtor.Formats.Xml;

namespace Swtor.Assets;

/// <summary>A garment palette that can be listed and picked: asset id, art name and the palette file.</summary>
public sealed record PaletteEntry(string Id, string Name, string FileName);

/// <summary>
/// Color schemes and garment palettes from art/dynamic/colorscheme and art/dynamic/garmenthue.
/// A scheme gives two palettes (primary and secondary) for each equipment slot.
/// </summary>
public sealed class ColorCatalog
{
    private readonly string _root;
    private readonly Dictionary<string, ColorScheme> _schemes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PaletteEntry> _palettes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Palette?> _loaded = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>All garment palettes, sorted by name.</summary>
    public IReadOnlyList<PaletteEntry> Palettes { get; }

    public ColorCatalog(string root)
    {
        _root = root;
        string dynamic = Path.Combine(root, "art", "dynamic");

        string schemeIndex = Path.Combine(dynamic, "colorscheme", "index.xml");
        if (File.Exists(schemeIndex))
        {
            using var stream = File.OpenRead(schemeIndex);
            foreach (var scheme in ColorSchemeIndexReader.Read(stream)) _schemes[scheme.Guid] = scheme;
        }

        string paletteIndex = Path.Combine(dynamic, "garmenthue", "index.xml");
        if (File.Exists(paletteIndex))
        {
            using var stream = File.OpenRead(paletteIndex);
            foreach (var asset in AppearanceIndexReader.Read(stream))
                if (asset.BaseFile.Length > 0) _palettes[asset.Id] = new PaletteEntry(asset.Id, asset.ArtName, asset.BaseFile);
        }
        Palettes = _palettes.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public ColorScheme? FindScheme(string guid) => _schemes.GetValueOrDefault(guid);

    public PaletteEntry? FindPaletteEntry(string id) => _palettes.GetValueOrDefault(id);

    /// <summary>Reads a palette file by asset id. The result is cached. Null if the id or file is missing.</summary>
    public Palette? ReadPalette(string id)
    {
        lock (_loaded)
        {
            if (_loaded.TryGetValue(id, out var cached)) return cached;
            return _loaded[id] = ReadPaletteFile(id);
        }
    }

    private Palette? ReadPaletteFile(string id)
    {
        Palette? palette = null;
        if (_palettes.TryGetValue(id, out var entry))
        {
            string path = Path.Combine(_root, entry.FileName.Replace('\\', '/').TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path)) palette = PaletteReader.Parse(File.ReadAllText(path));
        }
        return palette;
    }
}
