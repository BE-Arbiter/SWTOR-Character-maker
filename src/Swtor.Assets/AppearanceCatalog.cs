using Swtor.Formats;
using Swtor.Formats.Xml;

namespace Swtor.Assets;

/// <summary>An asset that uses a model, with the body type the model file matched.</summary>
/// <param name="Slot">Folder name under art/dynamic, for example "chest" or "head".</param>
/// <param name="Gender">'f' or 'm' from the body type (for example "bfa" gives 'f'). Null if the model has no body type.</param>
public sealed record AppearanceMatch(AppearanceAsset Asset, string Slot, char? Gender, bool IsAttachment, string? Bodytype = null);

/// <summary>
/// Links model files to the assets and material variants from the slot index.xml files.
/// Only asset data is read here. Materials are read on demand.
/// </summary>
public sealed class AppearanceCatalog
{
    private readonly string _root;
    private readonly Dictionary<string, List<AppearanceMatch>> _byModel = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (AppearanceAsset Asset, string Slot)> _byId = new();
    private readonly Dictionary<string, List<AppearanceAsset>> _bySlot = new(StringComparer.OrdinalIgnoreCase);

    public int AssetCount { get; private set; }

    /// <summary>Slots that have an index.xml, for example "chest".</summary>
    public IReadOnlyList<string> Slots { get; private set; } = [];

    public AppearanceCatalog(string root)
    {
        _root = root;
        string dynamic = Path.Combine(root, "art", "dynamic");
        if (!Directory.Exists(dynamic)) return;

        var slots = new List<string>();
        foreach (var dir in Directory.EnumerateDirectories(dynamic))
        {
            string file = Path.Combine(dir, "index.xml");
            if (!File.Exists(file)) continue;
            string slot = Path.GetFileName(dir);
            using var stream = File.OpenRead(file);
            foreach (var asset in AppearanceIndexReader.Read(stream)) Add(asset, slot);
            slots.Add(slot);
        }
        Slots = slots;
    }

    /// <summary>Finds an asset by its numeric id (as written in index.xml). Returns null if unknown.</summary>
    public (AppearanceAsset Asset, string Slot)? FindAsset(long id) =>
        _byId.TryGetValue(id.ToString(System.Globalization.CultureInfo.InvariantCulture), out var found) ? found : null;

    /// <summary>Finds the first asset of a slot by its art name, for example ("chest", "chest_naked_caucasian_young_a01").</summary>
    public AppearanceAsset? FindAsset(string slot, string artName) =>
        _bySlot.TryGetValue(slot, out var list) ? list.FirstOrDefault(a => a.ArtName == artName) : null;

    /// <summary>All assets of a slot (the folder name under art/dynamic).</summary>
    public IReadOnlyList<AppearanceAsset> AssetsOfSlot(string slot) => _bySlot.TryGetValue(slot, out var list) ? list : [];

    /// <summary>Assets that use <paramref name="modelPath"/> (relative to the root). Empty if none.</summary>
    public IReadOnlyList<AppearanceMatch> Find(string modelPath) =>
        _byModel.TryGetValue(Normalize(modelPath), out var list) ? list : [];

    /// <summary>
    /// Reads the material file of an asset variant. Returns null when the file does not exist.
    /// "[gen]" in the file name is replaced by the gender letter ('m' when unknown). "[bt]" is replaced by the body type.
    /// </summary>
    public MaterialDef? ReadMaterial(AssetMaterial material, char? gender, string? bodytype = null)
    {
        string name = material.FileName.Replace("[gen]", (gender ?? 'm').ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("[bt]", bodytype ?? "", StringComparison.OrdinalIgnoreCase);
        string path = Path.Combine(_root, Normalize(name).Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(path) ? MaterialReader.Parse(File.ReadAllText(path)) : null;
    }

    private void Add(AppearanceAsset asset, string slot)
    {
        AssetCount++;
        _byId[asset.Id] = (asset, slot);
        if (!_bySlot.TryGetValue(slot, out var inSlot)) _bySlot[slot] = inSlot = [];
        inSlot.Add(asset);
        Register(asset.BaseFile, asset, slot, isAttachment: false);
        foreach (var attachment in asset.Attachments) Register(attachment, asset, slot, isAttachment: true);
    }

    // Replaces "[bt]" with each body type of the asset. A file without "[bt]" is registered once.
    private void Register(string file, AppearanceAsset asset, string slot, bool isAttachment)
    {
        if (file.Length == 0) return;
        if (!file.Contains("[bt]", StringComparison.OrdinalIgnoreCase))
        {
            AddMatch(file, new AppearanceMatch(asset, slot, null, isAttachment));
            return;
        }
        foreach (var bodytype in asset.Bodytypes)
        {
            char? gender = bodytype.Length >= 2 ? char.ToLowerInvariant(bodytype[1]) : null;
            AddMatch(file.Replace("[bt]", bodytype, StringComparison.OrdinalIgnoreCase), new AppearanceMatch(asset, slot, gender, isAttachment, bodytype));
        }
    }

    private void AddMatch(string file, AppearanceMatch match)
    {
        string key = Normalize(file);
        if (!_byModel.TryGetValue(key, out var list)) _byModel[key] = list = [];
        list.Add(match);
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');
}
