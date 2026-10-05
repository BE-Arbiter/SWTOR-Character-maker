using System.Text;
using Swtor.Formats.Myp;

namespace Swtor.Assets;

/// <summary>Maps the name hashes of .tor archives back to paths. A .tor file keeps no names, so they come from outside.</summary>
public sealed class TorNames
{
    private readonly Dictionary<ulong, string> _byHash = [];

    public int Count => _byHash.Count;

    public string? Find(ulong hash) => _byHash.GetValueOrDefault(hash);

    /// <summary>Adds a path such as <c>art/dynamic/head/index.xml</c> or <c>/resources/art/...</c>. The stored form always starts with <c>/resources/</c>.</summary>
    public void Add(string path)
    {
        path = path.Trim().Replace('\\', '/');
        if (path.Length == 0) return;
        if (!path.StartsWith('/')) path = "/" + path;
        if (!path.StartsWith("/resources/", StringComparison.Ordinal)) path = "/resources" + path;
        _byHash[MypHash.Compute(path)] = path;
    }

    /// <summary>
    /// Adds every file of an extracted tree (the folder that holds <c>art</c>, <c>gamedata</c>, ...).
    /// Walks the whole tree: use it only for an explicit command, never at viewer start.
    /// </summary>
    public int AddTree(string root)
    {
        int before = Count;
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            Add(Path.GetRelativePath(root, file));
        return Count - before;
    }

    /// <summary>
    /// Adds the names of a text file. A line is a path, or a line of the community hash lists
    /// (<c>primary#secondary#path#crc</c>), where the third field is the path.
    /// </summary>
    public int AddList(string file)
    {
        int before = Count;
        foreach (string line in File.ReadLines(file))
        {
            string[] parts = line.Split('#');
            Add(parts.Length >= 3 ? parts[2] : line);
        }
        return Count - before;
    }
}

/// <summary>Result of an extraction run.</summary>
public sealed record TorExtractResult(int Archives, int Files, int Named, int Unnamed, int Skipped, int Failed, long Bytes);

/// <summary>Unpacks .tor archives to a folder tree that <see cref="AssetIndex"/> can read.</summary>
public static class TorExtractor
{
    private const string UnknownFolder = "_unknown";

    /// <summary>
    /// Extracts the files of the given archives. A file with a known name goes to <paramref name="outRoot"/> under its
    /// path without the leading <c>/resources/</c>, so <paramref name="outRoot"/> becomes the asset root. Other files go to
    /// <c>_unknown/&lt;hash&gt;.&lt;extension&gt;</c>. A file already on disk with the right size is skipped, so a run can restart.
    /// </summary>
    /// <param name="filter">Keeps only the named files that contain this text (unnamed files are skipped when set).</param>
    /// <param name="progress">Called after each archive with its file name.</param>
    public static TorExtractResult Extract(IEnumerable<string> archives, string outRoot, TorNames names,
        string? filter = null, int parallelism = 4, IProgress<string>? progress = null, Action<string>? onError = null)
    {
        int files = 0, named = 0, unnamed = 0, skipped = 0, failed = 0, done = 0;
        long bytes = 0;
        string fullRoot = Path.GetFullPath(outRoot);
        Directory.CreateDirectory(fullRoot);

        Parallel.ForEach(archives.ToList(), new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, parallelism) }, torPath =>
        {
            try
            {
                using var archive = MypArchive.Open(torPath);
                foreach (var entry in archive.Entries)
                {
                    string? name = names.Find(entry.Hash);
                    if (filter is not null && (name is null || !name.Contains(filter, StringComparison.OrdinalIgnoreCase))) continue;
                    try
                    {
                        string? target = name is null ? null : SafeTarget(fullRoot, name);
                        if (target is not null ? File.Exists(target) && new FileInfo(target).Length == entry.UncompressedSize : UnknownExists(fullRoot, entry))
                        {
                            Interlocked.Increment(ref skipped);
                            continue;
                        }
                        byte[] data = archive.Read(entry);
                        target ??= Path.Combine(fullRoot, UnknownFolder, $"{entry.Hash:X16}{GuessExtension(data)}");
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        File.WriteAllBytes(target, data);
                        Interlocked.Add(ref bytes, data.Length);
                        Interlocked.Increment(ref files);
                        if (name is null) Interlocked.Increment(ref unnamed); else Interlocked.Increment(ref named);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
                    {
                        Interlocked.Increment(ref failed);
                        onError?.Invoke($"{Path.GetFileName(torPath)} {entry.Hash:X16}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
            {
                Interlocked.Increment(ref failed);
                onError?.Invoke($"{torPath}: {ex.Message}");
            }
            Interlocked.Increment(ref done);
            progress?.Report(Path.GetFileName(torPath));
        });

        return new TorExtractResult(done, files, named, unnamed, skipped, failed, bytes);
    }

    /// <summary>True if a file for this entry is already in the unknown folder (its extension depends on the data, so match the hash only).</summary>
    private static bool UnknownExists(string fullRoot, MypEntry entry)
    {
        string folder = Path.Combine(fullRoot, UnknownFolder);
        return Directory.Exists(folder) && Directory.EnumerateFiles(folder, $"{entry.Hash:X16}.*").Any()
            || File.Exists(Path.Combine(folder, $"{entry.Hash:X16}"));
    }

    /// <summary>Output path of a name, or null if it would leave the output folder (a name list can hold ".." parts).</summary>
    private static string? SafeTarget(string fullRoot, string name)
    {
        const string prefix = "/resources/";
        string relative = name.StartsWith(prefix, StringComparison.Ordinal) ? name[prefix.Length..] : name.TrimStart('/');
        string full = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        return full.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    /// <summary>Guesses a file extension from the first bytes, for files with no known name. Empty when unsure.</summary>
    public static string GuessExtension(ReadOnlySpan<byte> data)
    {
        if (data.StartsWith("DDS "u8)) return ".dds";
        if (data.StartsWith("GAWB"u8)) return ".gr2";
        if (data.StartsWith("DBLB"u8)) return ".bkt";
        if (data.StartsWith("RIFF"u8)) return data.Length >= 12 && data[8..12].SequenceEqual("WAVE"u8) ? ".wav" : ".riff";
        if (data.StartsWith("BKHD"u8)) return ".bnk";
        if (data.StartsWith("OggS"u8)) return ".ogg";
        if (data.StartsWith("<"u8)) return ".xml";
        if (data.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47 })) return ".png";
        if (data.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF })) return ".jpg";
        if (data.Length > 0 && data.Length < 1 << 20 && IsText(data)) return ".txt";
        return "";
    }

    private static bool IsText(ReadOnlySpan<byte> data)
    {
        foreach (byte b in data) if (b < 9 || (b > 13 && b < 32)) return false;
        return true;
    }
}
