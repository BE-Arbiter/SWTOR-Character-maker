using System.Collections.Concurrent;
using Swtor.Formats.Gom;
using Swtor.Formats.Stb;

namespace Swtor.Assets;

/// <summary>
/// An equippable item that shows an art asset. <see cref="Name"/> is the localized item name.
/// <see cref="Quality"/> is the index of the game enum value (higher is rarer) and <see cref="QualityName"/> its name.
/// <see cref="AppearanceName"/> is the name of the appearance object ("ipp.*"). The icon of the item is the file
/// <c>gfx/icons/&lt;AppearanceName&gt;.dds</c> (see <see cref="IconPath"/>).
/// </summary>
public sealed record ItemInfo(
    string Key, string Name, int Level, int Quality, string QualityName,
    AppearanceSlot Slot, long AssetId, long MaterialId, string? AppearanceName = null)
{
    /// <summary>Path of the icon below the asset root, with "/" separators, or null when the item has no appearance object. The file may not exist.</summary>
    public string? IconPath => AppearanceName is null ? null : $"gfx/icons/{AppearanceName}.dds";
}

/// <summary>
/// Items of the game that show armor or clothing, with their names. Each item points to an appearance
/// object ("ipp.*") which names the art asset and material. Built from the game database and the text table
/// of the game language (for example fr-fr/str/itm.stb).
/// </summary>
public sealed class ItemCatalog
{
    // Field ids are hashes of unknown names. They were found by reading the data.
    private const ulong AppearanceRefField = 0x40000002C7C48E76; // item: string, name of the appearance object
    private const ulong LevelField = 0x40000002C7C48E7C;         // item: item level
    private const ulong QualityField = 0x40000002C7C48E7D;       // item: enum quality
    private const ulong NameField = 0x4000000AB30B6C77;          // item: string "str.itm#<id>"
    private const ulong SlotField = 0x4000000316C18126;          // appearance: enum slot
    private const ulong AssetField = 0x4000000316C18127;         // appearance: asset id
    private const ulong MaterialField = 0x4000000316C18128;      // appearance: material id

    private readonly Dictionary<long, List<ItemInfo>> _byAsset;

    public int Count { get; }

    private readonly Dictionary<string, List<ItemInfo>> _byWeaponKey;

    private ItemCatalog(List<ItemInfo> items, List<(string Key, ItemInfo Item)> weaponItems)
    {
        Count = items.Count;
        _byAsset = items.GroupBy(i => i.AssetId).ToDictionary(g => g.Key, g => Sorted(g));
        _byWeaponKey = weaponItems.GroupBy(w => w.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => Sorted(g.Select(w => w.Item)), StringComparer.OrdinalIgnoreCase);
    }

    private static List<ItemInfo> Sorted(IEnumerable<ItemInfo> items) =>
        items.OrderBy(i => i.Level).ThenByDescending(i => i.Quality).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// Items that show the weapon appearance with this key (a key of the weapon table), lowest level first. Empty if none.
    /// The items have no art asset: <see cref="ItemInfo.AssetId"/> is 0.
    /// </summary>
    public IReadOnlyList<ItemInfo> ForWeaponKey(string key) => _byWeaponKey.TryGetValue(key, out var list) ? list : [];

    /// <summary>
    /// Items that show this art asset, lowest level first (then best quality). The first item is the one that introduced the look,
    /// so its name is the most descriptive. Empty if none.
    /// </summary>
    public IReadOnlyList<ItemInfo> ForAsset(long assetId) => _byAsset.TryGetValue(assetId, out var list) ? list : [];

    /// <summary>
    /// Builds the catalog. Takes a few seconds (it decodes about 200,000 objects), so call it from a background thread.
    /// <paramref name="names"/> is the text table for item names.
    /// </summary>
    public static ItemCatalog Load(GomDatabase database, StringTable names)
    {
        // First the appearance objects, then the items that refer to them by name.
        var appearances = new ConcurrentDictionary<string, (int Slot, long Asset, long Material)>(StringComparer.Ordinal);
        database.ForEachNode(e => e.Name.StartsWith("ipp.", StringComparison.Ordinal), (entry, node) =>
        {
            if (node.Object.Find(SlotField)?.Value is GomEnumValue slot && node.Object.Find(AssetField)?.Value is long asset)
                appearances[entry.Name] = (slot.Value, asset, node.Object.Find(MaterialField)?.Value as long? ?? 0);
        });

        database.Schema.Fields.TryGetValue(QualityField, out var qualityField);
        ulong qualityEnum = qualityField?.Type?.ReferenceId ?? 0;
        var items = new ConcurrentBag<ItemInfo>();
        var weaponItems = new ConcurrentBag<(string Key, ItemInfo Item)>();
        database.ForEachNode(e => e.Name.StartsWith("itm.", StringComparison.Ordinal), (entry, node) =>
        {
            var o = node.Object;
            if (o.Find(AppearanceRefField)?.Value is not string appearanceName) return;
            if (o.Find(NameField)?.Value is not string reference || names.GetByReference(reference) is not { } name) return;

            int quality = o.Find(QualityField)?.Value is GomEnumValue q ? q.Value : 0;
            string qualityName = database.Schema.EnumName(qualityEnum, quality) ?? "";
            int level = o.Find(LevelField)?.Value is long l ? (int)l : 0;
            if (appearances.TryGetValue(appearanceName, out var appearance))
                items.Add(new ItemInfo(entry.Name, name, level, quality, qualityName,
                    (AppearanceSlot)appearance.Slot, appearance.Asset, appearance.Material, appearanceName));
            else
                // Not an "ipp.*" object: the string is a key of the weapon table.
                weaponItems.Add((appearanceName, new ItemInfo(entry.Name, name, level, quality, qualityName, 0, 0, 0)));
        });
        return new ItemCatalog([.. items], [.. weaponItems]);
    }
}

/// <summary>Finds the text tables of the game language in the extracted data.</summary>
public static class TextTables
{
    /// <summary>
    /// Path of a text table such as "itm.stb" in the first language folder that has it ("fr-fr" and "en-us" are tried first).
    /// Returns null if no language folder has it.
    /// </summary>
    public static string? Find(string root, string fileName)
    {
        var candidates = new List<string> { "fr-fr", "en-us" };
        candidates.AddRange(Directory.EnumerateDirectories(root).Select(Path.GetFileName).Where(n => n is { Length: 5 } && n[2] == '-')!);
        foreach (string language in candidates.Distinct())
        {
            string path = Path.Combine(root, language, "str", fileName);
            if (File.Exists(path)) return path;
        }
        return null;
    }
}
