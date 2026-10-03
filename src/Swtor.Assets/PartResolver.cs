using System.Globalization;
using Swtor.Formats.Xml;

namespace Swtor.Assets;

/// <summary>Files needed to show one character part: models and the textures of its material.</summary>
/// <param name="ModelPath">Relative path of the main .gr2 file.</param>
/// <param name="DiffusePath">Relative path of the diffuse .dds file, or null.</param>
/// <param name="MaskPath">Relative path of the color mask .dds file, or null.</param>
/// <param name="Bodytype">The body type used for the file names, for example "bma". Null if the asset has none.</param>
/// <param name="Attachments">Extra model files of the asset that exist (for example shoulder pieces). They share the material.</param>
public sealed record ResolvedPart(
    string ModelPath, string? DiffusePath, string? MaskPath, string? Bodytype, IReadOnlyList<string> Attachments);

/// <summary>Turns a creator option or an asset into files.</summary>
public static class PartResolver
{
    /// <summary>
    /// Finds the files for an option. <paramref name="gender"/> is 'm' or 'f'. Returns null if the asset is unknown
    /// or its model file does not exist.
    /// </summary>
    public static ResolvedPart? Resolve(AssetIndex index, CharacterOption option, char gender, string? preferredBodytype = null) =>
        index.Appearances.FindAsset(option.AssetId) is { } found
            ? ResolveAsset(index, found.Asset, option.MaterialId.ToString(CultureInfo.InvariantCulture), gender, preferredBodytype)
            : null;

    /// <summary>
    /// Finds the files for an asset and one of its materials (by material id; the first material if the id is not found).
    /// The body type is chosen in this order: <paramref name="preferredBodytype"/>, then body types of the right
    /// gender, then the others. The first one whose model file exists wins.
    /// </summary>
    public static ResolvedPart? ResolveAsset(AssetIndex index, AppearanceAsset asset, string? materialId, char gender, string? preferredBodytype = null)
    {
        if (asset.BaseFile.Length == 0) return null;

        IEnumerable<string> bodytypes = asset.Bodytypes.OrderBy(b =>
            b.Equals(preferredBodytype, StringComparison.OrdinalIgnoreCase) ? 0
            : b.Length >= 2 && char.ToLowerInvariant(b[1]) == gender ? 1 : 2);
        string? model = null, matchedBodytype = null;
        foreach (var bodytype in asset.Bodytypes.Count == 0 ? [""] : bodytypes)
        {
            string candidate = Expand(asset.BaseFile, bodytype);
            if (!index.HasModel(candidate)) continue;
            model = candidate;
            matchedBodytype = bodytype.Length > 0 ? bodytype : null;
            break;
        }
        if (model is null) return null;

        char? matchedGender = matchedBodytype is { Length: >= 2 } ? char.ToLowerInvariant(matchedBodytype[1]) : null;
        var attachments = asset.Attachments.Select(a => Expand(a, matchedBodytype ?? "")).Where(index.HasModel).ToList();

        var material = asset.Materials.FirstOrDefault(m => m.Id == materialId) ?? asset.Materials.FirstOrDefault();
        string? diffuse = null, mask = null;
        if (material is not null && index.Appearances.ReadMaterial(material, matchedGender ?? gender, matchedBodytype) is { } def)
        {
            diffuse = Existing(index, def.DiffuseMap);
            string? maskPath = def.TexturePath("PaletteMaskMap");
            mask = maskPath is not null && !maskPath.StartsWith("art/defaultassets", StringComparison.OrdinalIgnoreCase) ? Existing(index, maskPath) : null;
        }
        return new ResolvedPart(model, diffuse, mask, matchedBodytype, attachments);
    }

    /// <summary>Cheap check: true if a model file exists for the asset with a suitable body type (no material is read).</summary>
    public static bool HasModel(AssetIndex index, AppearanceAsset asset, char gender, string? preferredBodytype)
    {
        if (asset.Bodytypes.Count == 0) return index.HasModel(Expand(asset.BaseFile, ""));
        return asset.Bodytypes.Any(b => index.HasModel(Expand(asset.BaseFile, b))
            && (preferredBodytype is null || b.Equals(preferredBodytype, StringComparison.OrdinalIgnoreCase) || (b.Length >= 2 && char.ToLowerInvariant(b[1]) == gender)));
    }

    private static string Expand(string file, string bodytype) =>
        file.Replace("[bt]", bodytype, StringComparison.OrdinalIgnoreCase).Replace('\\', '/').TrimStart('/');

    private static string? Existing(AssetIndex index, string? texture)
    {
        if (texture is null) return null;
        string path = texture + ".dds";
        return File.Exists(index.FullPath(path)) ? path : null;
    }
}

/// <summary>Chooses the bare body parts (chest, hands, legs, boots) for a race.</summary>
public static class NakedBody
{
    private static readonly string[] Ethnicities = ["caucasian", "african", "asian"];

    /// <summary>
    /// Art name of the bare body asset in <paramref name="slot"/> ("chest", "hand", "leg" or "boot").
    /// A few races have their own body. Others use the ethnicity written in the name of the head asset
    /// (for example "head_human_bma_asian_a01" gives "asian"), or "caucasian".
    /// </summary>
    public static string AssetName(string slot, string race, string? headArtName)
    {
        string variant = race switch
        {
            "twilek" or "cathar" or "rattataki" => race,
            "sith" => "bloodsith",
            _ => EthnicityOf(headArtName),
        };
        return $"{slot}_naked_{variant}_young_a01";
    }

    private static string EthnicityOf(string? headArtName) =>
        headArtName?.Split('_').FirstOrDefault(p => Ethnicities.Contains(p)) ?? "caucasian";
}
