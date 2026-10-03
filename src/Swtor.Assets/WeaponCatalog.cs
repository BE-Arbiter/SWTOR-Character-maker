using Swtor.Formats.Gom;

namespace Swtor.Assets;

/// <summary>
/// One weapon appearance from the game database. <see cref="CombatType"/> is the name of the game enum value
/// (for example "cbtType_lightsaber"). <see cref="Socket"/> is the attachment point on the character.
/// </summary>
public sealed record WeaponAppearance(string Key, string CombatType, string ModelPath, string Socket, string Color, string Label);

/// <summary>All weapon appearances ("itmAppearanceDatatable" in the game database).</summary>
public sealed class WeaponCatalog
{
    // Field ids are hashes of unknown names. They were found by reading the data.
    private const ulong TableField = 0x40000005DE8974CC;  // map: appearance key -> appearance object
    private const ulong TypeField = 0x40000005DE8974BE;   // enum: combat type
    private const ulong ModelField = 0x40000005DE8974C0;  // string: model path
    private const ulong SocketField = 0x40000005DE8974C2; // string: socket name
    private const ulong ColorField = 0x40000005DE8974CA;  // string: blade or glow color
    private const ulong LabelField = 0x40000005F1979F14;  // string: short weapon label

    public IReadOnlyList<WeaponAppearance> Items { get; }

    /// <summary>Combat type names, sorted, for filters.</summary>
    public IReadOnlyList<string> CombatTypes { get; }

    private WeaponCatalog(List<WeaponAppearance> items)
    {
        Items = items;
        CombatTypes = items.Select(i => i.CombatType).Distinct().Order().ToList();
    }

    /// <summary>Reads the weapon table from the database. Returns an empty catalog if the table is missing.</summary>
    public static WeaponCatalog Load(GomDatabase database)
    {
        var items = new List<WeaponAppearance>();
        if (database.Find("itmAppearanceDatatable") is { } entry
            && database.Decode(entry).Object.Find(TableField)?.Value is GomMap table)
        {
            database.Schema.Fields.TryGetValue(TypeField, out var typeField);
            foreach (var row in table.Entries)
            {
                if (row.Key is not string key || row.Value is not GomObject o) continue;
                string type = o.Find(TypeField)?.Value is GomEnumValue e
                    ? database.Schema.EnumName(typeField?.Type?.ReferenceId ?? 0, e.Value) ?? $"type{e.Value}"
                    : "unknown";
                string model = (o.Find(ModelField)?.Value as string ?? "").Replace('\\', '/').TrimStart('/');
                items.Add(new WeaponAppearance(key, type, model, o.Find(SocketField)?.Value as string ?? "",
                    o.Find(ColorField)?.Value as string ?? "", o.Find(LabelField)?.Value as string ?? ""));
            }
        }
        items.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase));
        return new WeaponCatalog(items);
    }
}
