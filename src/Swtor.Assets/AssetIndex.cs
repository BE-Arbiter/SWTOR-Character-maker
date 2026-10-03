namespace Swtor.Assets;

/// <summary>
/// List of the models and textures in an extracted game folder.
/// Scanning 100k+ files takes time, so the result is saved in a cache file.
/// </summary>
public sealed class AssetIndex
{
    private const string CacheVersion = "1";

    /// <summary>Models (.gr2 without .lod.gr2) as paths relative to the root, with '/' separators. Sorted.</summary>
    public IReadOnlyList<string> Models { get; }

    /// <summary>Diffuse-capable textures (.dds, not .tiny.dds) as relative paths. Sorted.</summary>
    public IReadOnlyList<string> Textures { get; }

    public string Root { get; }

    private readonly Dictionary<string, List<string>> _texturesByPrefix;

    private AssetIndex(string root, List<string> models, List<string> textures)
    {
        Root = root;
        models.Sort(StringComparer.OrdinalIgnoreCase);
        textures.Sort(StringComparer.OrdinalIgnoreCase);
        Models = models;
        Textures = textures;

        // Key: the texture file name without the "_v01_d.dds" ending.
        _texturesByPrefix = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in textures)
        {
            string key = TexturePrefix(Path.GetFileName(t));
            if (!_texturesByPrefix.TryGetValue(key, out var list)) _texturesByPrefix[key] = list = [];
            list.Add(t);
        }
    }

    /// <summary>
    /// Loads the index from the cache, or scans the folder when the cache is missing or old.
    /// Call from a background thread.
    /// </summary>
    public static AssetIndex Load(string root, IProgress<int>? progress = null, bool forceRescan = false)
    {
        string cache = CachePath(root);
        string stamp = Stamp(root);
        if (!forceRescan && TryReadCache(cache, stamp, root) is { } cached) return cached;

        var models = new List<string>();
        var textures = new List<string>();
        int seen = 0;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        foreach (var path in Directory.EnumerateFiles(root, "*.*", options))
        {
            if (++seen % 5000 == 0) progress?.Report(seen);
            string name = Path.GetFileName(path);
            bool isModel = name.EndsWith(".gr2", StringComparison.OrdinalIgnoreCase)
                && !name.EndsWith(".lod.gr2", StringComparison.OrdinalIgnoreCase);
            bool isTexture = name.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)
                && !name.EndsWith(".tiny.dds", StringComparison.OrdinalIgnoreCase);
            if (!isModel && !isTexture) continue;
            string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            (isModel ? models : textures).Add(relative);
        }

        var index = new AssetIndex(root, models, textures);
        index.WriteCache(cache, stamp);
        return index;
    }

    /// <summary>
    /// Finds the textures of a model by file name: "<c>name.gr2</c>" matches "<c>name_v01_d.dds</c>".
    /// Returns an empty list when the game uses its appearance data instead (player parts).
    /// </summary>
    public IReadOnlyList<string> FindTextures(string modelPath)
    {
        string key = Path.GetFileNameWithoutExtension(modelPath);
        return _texturesByPrefix.TryGetValue(key, out var list) ? list : [];
    }

    /// <summary>Searches texture paths by text (case-insensitive). Returns at most <paramref name="limit"/> results.</summary>
    public IEnumerable<string> SearchTextures(string text, int limit) =>
        Textures.Where(t => t.Contains(text, StringComparison.OrdinalIgnoreCase)).Take(limit);

    public string FullPath(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

    // "head_x_v01_d.dds" -> "head_x". Other names keep their full stem.
    private static string TexturePrefix(string fileName)
    {
        string stem = Path.GetFileNameWithoutExtension(fileName);
        int v = stem.LastIndexOf("_v", StringComparison.Ordinal);
        return v > 0 && stem.EndsWith("_d", StringComparison.Ordinal) ? stem[..v] : stem;
    }

    private static string CachePath(string root)
    {
        string id = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(root.ToLowerInvariant())))[..12];
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SwtorCharacterMaker");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"index-{id}.txt");
    }

    // The cache is valid while version.txt of the game folder does not change.
    private static string Stamp(string root)
    {
        string file = Path.Combine(root, "version.txt");
        return CacheVersion + "|" + (File.Exists(file) ? File.ReadAllText(file).ReplaceLineEndings(";") : "");
    }

    private static AssetIndex? TryReadCache(string cache, string stamp, string root)
    {
        if (!File.Exists(cache)) return null;
        var models = new List<string>();
        var textures = new List<string>();
        using var reader = new StreamReader(cache);
        if (reader.ReadLine() != stamp) return null;
        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith("M|")) models.Add(line[2..]);
            else if (line.StartsWith("T|")) textures.Add(line[2..]);
        }
        return new AssetIndex(root, models, textures);
    }

    private void WriteCache(string cache, string stamp)
    {
        using var writer = new StreamWriter(cache);
        writer.WriteLine(stamp);
        foreach (var m in Models) writer.WriteLine("M|" + m);
        foreach (var t in Textures) writer.WriteLine("T|" + t);
    }
}
