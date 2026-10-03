using Swtor.Formats;
using Swtor.Formats.Gom;

namespace Swtor.Assets;

/// <summary>Where a game object is stored: a bucket file (<see cref="BucketIndex"/> is the number in "N.bkt") or a prototype file.</summary>
public sealed record GomEntry(string Name, ulong Id, ulong ClassId, int BucketIndex, GomNodeInfo? Info, string? PrototypeFile);

/// <summary>
/// The game object database (systemgenerated/): about 726,000 objects with dotted names such as "itm.mat.craft" or "pcs.trooper.male.human".
/// Opening reads only the record headers (a few seconds). Objects are decoded on demand.
/// </summary>
public sealed class GomDatabase
{
    private readonly string _bucketFolder;
    private readonly Dictionary<string, GomEntry> _byName;
    private readonly object _cacheLock = new();
    private (int Index, GomBucketFile File)? _lastBucket;

    public IReadOnlyList<GomEntry> Entries { get; }

    public GomSchema Schema { get; }

    private GomDatabase(string folder, List<GomEntry> entries, GomSchema schema)
    {
        _bucketFolder = Path.Combine(folder, "buckets");
        Entries = entries;
        Schema = schema;
        _byName = new Dictionary<string, GomEntry>(entries.Count, StringComparer.Ordinal);
        foreach (var entry in entries) _byName.TryAdd(entry.Name, entry);
    }

    /// <summary>Reads the headers of all buckets and prototypes under <paramref name="root"/>/systemgenerated.</summary>
    public static GomDatabase Open(string root)
    {
        string folder = Path.Combine(root, "systemgenerated");
        var schema = GomSchema.Parse(File.ReadAllBytes(Path.Combine(folder, "client.gom")));

        var buckets = Directory.GetFiles(Path.Combine(folder, "buckets"), "*.bkt");
        var perBucket = new List<GomEntry>[buckets.Length];
        Parallel.For(0, buckets.Length, i =>
        {
            int number = int.Parse(Path.GetFileNameWithoutExtension(buckets[i]));
            var bucket = GomBucketFile.Open(File.ReadAllBytes(buckets[i]));
            perBucket[i] = bucket.Nodes.Select(n => new GomEntry(n.Name, n.Id, n.ClassId, number, n, null)).ToList();
        });

        var entries = perBucket.SelectMany(list => list).ToList();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(folder, "prototypes"), "*.node"))
        {
            var node = GomPrototypeFile.Parse(File.ReadAllBytes(file));
            entries.Add(new GomEntry(node.Name, node.Id, node.ClassId, -1, null, file));
        }
        return new GomDatabase(folder, entries, schema);
    }

    /// <summary>Finds an object by its exact name.</summary>
    public GomEntry? Find(string name) => _byName.GetValueOrDefault(name);

    /// <summary>Lists objects whose name starts with <paramref name="prefix"/>.</summary>
    public IEnumerable<GomEntry> WithPrefix(string prefix) =>
        Entries.Where(e => e.Name.StartsWith(prefix, StringComparison.Ordinal));

    /// <summary>
    /// Decodes every object that passes <paramref name="filter"/> and calls <paramref name="action"/> for each.
    /// Buckets are read once and processed in parallel, so <paramref name="action"/> must be safe to call from several threads.
    /// Objects that cannot be decoded are skipped.
    /// </summary>
    public void ForEachNode(Func<GomEntry, bool> filter, Action<GomEntry, GomNode> action)
    {
        var groups = Entries.Where(e => e.PrototypeFile is null && filter(e)).GroupBy(e => e.BucketIndex).ToList();
        Parallel.ForEach(groups, group =>
        {
            var bucket = GomBucketFile.Open(File.ReadAllBytes(Path.Combine(_bucketFolder, $"{group.Key}.bkt")));
            foreach (var entry in group)
            {
                GomNode node;
                try
                {
                    node = bucket.Decode(entry.Info!);
                }
                catch (GameFormatException)
                {
                    continue;
                }
                action(entry, node);
            }
        });
        foreach (var entry in Entries.Where(e => e.PrototypeFile is not null && filter(e))) action(entry, Decode(entry));
    }

    /// <summary>Decodes one object. Safe to call from several threads.</summary>
    public GomNode Decode(GomEntry entry)
    {
        if (entry.PrototypeFile is not null) return GomPrototypeFile.Parse(File.ReadAllBytes(entry.PrototypeFile));

        GomBucketFile bucket;
        lock (_cacheLock)
        {
            if (_lastBucket is { } last && last.Index == entry.BucketIndex)
            {
                bucket = last.File;
            }
            else
            {
                bucket = GomBucketFile.Open(File.ReadAllBytes(Path.Combine(_bucketFolder, $"{entry.BucketIndex}.bkt")));
                _lastBucket = (entry.BucketIndex, bucket);
            }
        }
        return bucket.Decode(entry.Info!);
    }
}
