using System.Collections.Concurrent;
using Swtor.Formats.Gom;
using Swtor.Formats.Stb;

namespace Swtor.Assets;

/// <summary>One slot of an NPC look: an appearance asset and material, with the two garment palettes that dye it (0 when none).</summary>
public sealed record NpcPart(AppearanceSlot Slot, long AssetId, long MaterialId, long PrimaryPalette, long SecondaryPalette);

/// <summary>
/// The look of NPCs: an "npp.*" object of the game database. <see cref="Bodytype"/> is the body code ("bfn").
/// <see cref="Names"/> are the display names of the NPCs ("npc.*" objects) that use this look, the first one is the best label.
/// </summary>
public sealed record NpcLook(string Key, string Bodytype, IReadOnlyList<NpcPart> Parts, IReadOnlyList<string> Names, int NpcCount)
{
    /// <summary>Label for lists: the first NPC name, or the last two parts of the key when no NPC has a name.</summary>
    public string Label => Names.Count > 0 ? Names[0] : ShortKey;

    /// <summary>Key without the "npp." prefix.</summary>
    public string ShortKey => Key.StartsWith("npp.", StringComparison.Ordinal) ? Key[4..] : Key;

    /// <summary>Lower-case text that the filter searches: key and all NPC names.</summary>
    public string SearchText { get; } = (Key + " " + string.Join(' ', Names)).ToLowerInvariant();

    /// <summary>The part of a slot, or null.</summary>
    public NpcPart? Part(AppearanceSlot slot)
    {
        foreach (var part in Parts)
            if (part.Slot == slot) return part;
        return null;
    }

    /// <summary>The gender letter of the body type: 'f' or 'm'.</summary>
    public char Gender => Bodytype.Length >= 2 && char.ToLowerInvariant(Bodytype[1]) == 'f' ? 'f' : 'm';
}

/// <summary>
/// The NPCs of the game. An "npc.*" object names its look ("npp.*" object) by id; the look lists the appearance
/// asset, material and dye palettes of each slot, the same way the character creator does. Only looks that have a head are kept
/// (creatures and droids use other models).
/// </summary>
public sealed class NpcCatalog
{
    // Field ids are hashes of unknown names. They were found by reading the data.
    private const ulong NppBodytypeField = 0x40000001C0858140; // npp: string, body code
    private const ulong NppSlotsField = 0x4000000598BEDE70;    // npp: map slot enum -> list of parts
    private const ulong PartSlotField = 0x4000000316C18126;    // part: enum slot
    private const ulong PartAssetField = 0x4000000316C18127;   // part: asset id
    private const ulong PartMaterialField = 0x4000000316C18128; // part: material id
    private const ulong PartPrimaryField = 0x4000000316C1812A;  // part: palette id (garmenthue)
    private const ulong PartSecondaryField = 0x4000000316C1812B; // part: palette id
    private const ulong NpcClass = 0x400000000078E1BD;          // class id of "npc.*" objects
    private const ulong NpcNamesField = 0x40000013A787EE87;     // npc: map -> object with the name id
    private const ulong NpcNameIdField = 0x400000115CE87488;    // in a name object: id in the text table npc.stb
    private const ulong NpcLooksField = 0x400000082DE6DE61;     // npc: list of objects (body code, look)
    private const ulong NpcLookIdField = 0x4000000A731CD8BF;    // in a look object: id of the npp object

    /// <summary>Looks sorted by label.</summary>
    public IReadOnlyList<NpcLook> Looks { get; }

    private NpcCatalog(List<NpcLook> looks) => Looks = looks;

    /// <summary>
    /// Reads all looks and NPC names. Decodes about 100,000 objects, so call it from a background thread.
    /// <paramref name="names"/> is the text table "npc.stb" (may be null: NPCs then have no display name).
    /// </summary>
    public static NpcCatalog Load(GomDatabase database, StringTable? names)
    {
        var looks = new ConcurrentDictionary<ulong, (string Key, string Bodytype, List<NpcPart> Parts)>();
        database.ForEachNode(e => e.Name.StartsWith("npp.", StringComparison.Ordinal), (entry, node) =>
        {
            var parts = ReadParts(node.Object);
            if (parts.Count == 0 || !parts.Any(p => p.Slot == AppearanceSlot.Head)) return;
            looks[entry.Id] = (entry.Name, node.Object.Find(NppBodytypeField)?.Value as string ?? "", parts);
        });

        // Names of the NPCs, by look.
        var namesByLook = new ConcurrentDictionary<ulong, ConcurrentBag<string>>();
        var counts = new ConcurrentDictionary<ulong, int>();
        database.ForEachNode(e => e.ClassId == NpcClass, (entry, node) =>
        {
            string? name = null;
            if (names is not null && node.Object.Find(NpcNamesField)?.Value is GomMap map)
                foreach (var row in map.Entries)
                    if (row.Value is GomObject o && o.Find(NpcNameIdField)?.Value is long id && names.Get(id) is { Length: > 0 } text) name = text;
            if (node.Object.Find(NpcLooksField)?.Value is not GomList list) return;
            foreach (var item in list.Items)
            {
                if (item is not GomObject look || look.Find(NpcLookIdField)?.Value is not ulong lookId || !looks.ContainsKey(lookId)) continue;
                counts.AddOrUpdate(lookId, 1, (_, n) => n + 1);
                if (name is not null) namesByLook.GetOrAdd(lookId, _ => []).Add(name);
            }
        });

        var result = new List<NpcLook>(looks.Count);
        foreach (var (id, look) in looks)
        {
            var found = namesByLook.TryGetValue(id, out var bag)
                ? bag.GroupBy(n => n).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase).Select(g => g.Key).Take(12).ToList()
                : [];
            result.Add(new NpcLook(look.Key, look.Bodytype, look.Parts, found, counts.GetValueOrDefault(id)));
        }
        result.Sort((a, b) => string.Compare(a.Label, b.Label, StringComparison.CurrentCultureIgnoreCase) is var c && c != 0
            ? c : string.CompareOrdinal(a.Key, b.Key));
        return new NpcCatalog(result);
    }

    private static List<NpcPart> ReadParts(GomObject npp)
    {
        var parts = new List<NpcPart>();
        if (npp.Find(NppSlotsField)?.Value is not GomMap map) return parts;
        foreach (var row in map.Entries)
        {
            if (row.Value is not GomList list) continue;
            foreach (var item in list.Items)
            {
                if (item is not GomObject part) continue;
                // The slot is the map key; the field in the part can be missing.
                var slot = row.Key is GomEnumValue key ? (AppearanceSlot)key.Value
                    : part.Find(PartSlotField)?.Value is GomEnumValue inner ? (AppearanceSlot)inner.Value : AppearanceSlot.Unknown;
                if (part.Find(PartAssetField)?.Value is not long asset || asset == 0) continue;
                parts.Add(new NpcPart(slot, asset, part.Find(PartMaterialField)?.Value as long? ?? 0,
                    part.Find(PartPrimaryField)?.Value as long? ?? 0, part.Find(PartSecondaryField)?.Value as long? ?? 0));
                break; // One part per slot: the first one is the visible one.
            }
        }
        return parts;
    }
}
