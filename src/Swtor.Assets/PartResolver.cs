namespace Swtor.Assets;

/// <summary>Files needed to show one character part: a model and the textures of its material.</summary>
/// <param name="ModelPath">Relative path of the .gr2 file.</param>
/// <param name="DiffusePath">Relative path of the diffuse .dds file, or null.</param>
/// <param name="MaskPath">Relative path of the color mask .dds file, or null.</param>
public sealed record ResolvedPart(string ModelPath, string? DiffusePath, string? MaskPath);

/// <summary>Turns a creator option (asset id and material id) into files.</summary>
public static class PartResolver
{
    /// <summary>
    /// Finds the model and textures for an option. <paramref name="gender"/> is 'm' or 'f' and selects the body type
    /// that replaces "[bt]" in file names. Returns null if the asset is unknown or its model file does not exist.
    /// </summary>
    public static ResolvedPart? Resolve(AssetIndex index, CharacterOption option, char gender)
    {
        if (index.Appearances.FindAsset(option.AssetId) is not { } found) return null;
        var asset = found.Asset;
        if (asset.BaseFile.Length == 0) return null;

        // Try the body types of the right gender first, then the others.
        var bodytypes = asset.Bodytypes.Where(b => b.Length >= 2 && char.ToLowerInvariant(b[1]) == gender)
            .Concat(asset.Bodytypes.Where(b => b.Length < 2 || char.ToLowerInvariant(b[1]) != gender));
        string? model = null;
        char? matchedGender = null;
        string? matchedBodytype = null;
        foreach (var bodytype in asset.Bodytypes.Count == 0 ? [""] : bodytypes)
        {
            string candidate = asset.BaseFile.Replace("[bt]", bodytype, StringComparison.OrdinalIgnoreCase).Replace('\\', '/').TrimStart('/');
            if (!index.HasModel(candidate)) continue;
            model = candidate;
            matchedGender = bodytype.Length >= 2 ? char.ToLowerInvariant(bodytype[1]) : null;
            matchedBodytype = bodytype;
            break;
        }
        if (model is null) return null;

        // The option names one material. Without it, use the first one.
        var material = asset.Materials.FirstOrDefault(m => m.Id == option.MaterialId.ToString(System.Globalization.CultureInfo.InvariantCulture))
            ?? asset.Materials.FirstOrDefault();
        string? diffuse = null, mask = null;
        if (material is not null && index.Appearances.ReadMaterial(material, matchedGender ?? gender, matchedBodytype) is { } def)
        {
            diffuse = Existing(index, def.DiffuseMap);
            string? maskPath = def.TexturePath("PaletteMaskMap");
            mask = maskPath is not null && !maskPath.StartsWith("art/defaultassets", StringComparison.OrdinalIgnoreCase) ? Existing(index, maskPath) : null;
        }
        return new ResolvedPart(model, diffuse, mask);
    }

    private static string? Existing(AssetIndex index, string? texture)
    {
        if (texture is null) return null;
        string path = texture + ".dds";
        return File.Exists(index.FullPath(path)) ? path : null;
    }
}
