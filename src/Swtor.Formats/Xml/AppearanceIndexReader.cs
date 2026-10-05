using System.Xml;

namespace Swtor.Formats.Xml;

/// <summary>
/// A material variant of an asset. <see cref="FileName"/> may contain the "[gen]" placeholder.
/// <see cref="ColorSchemeIds"/> lists the color schemes (see <see cref="ColorSchemeIndexReader"/>) allowed for this variant.
/// </summary>
public sealed record AssetMaterial(
    string Id, string Name, string FileName, IReadOnlyList<string> ColorSchemeIds,
    IReadOnlyList<MaterialOverride>? Overrides = null);

/// <summary>Replaces the material of one mesh slot (<see cref="Index"/>) with another material file. Used for eyes on heads.</summary>
public sealed record MaterialOverride(int Index, string FileName);

/// <summary>
/// One entry of a slot <c>index.xml</c> (art/dynamic/&lt;slot&gt;/index.xml):
/// a model, its extra model files and its material variants.
/// File names may contain the "[bt]" placeholder (body type, for example "bfa" or "bmn").
/// <see cref="SkinMaterialIndex"/> is the mesh piece that shows bare skin (it takes the skin material of the body for the slot), or -1.
/// <see cref="SkinMaterials"/> (heads only) gives the material of the bare body for each slot ("chest", "hand", "leg", "boot"), or null.
/// </summary>
public sealed record AppearanceAsset(
    string Id,
    string ArtName,
    string BaseFile,
    IReadOnlyList<string> Attachments,
    IReadOnlyList<AssetMaterial> Materials,
    IReadOnlyList<string> Bodytypes,
    string? RepresentativeColor = null,
    int SkinMaterialIndex = -1,
    IReadOnlyDictionary<string, string>? SkinMaterials = null);

public static class AppearanceIndexReader
{
    /// <summary>Reads all assets of an index.xml. Uses a forward-only reader, so large files stay cheap.</summary>
    public static List<AppearanceAsset> Read(Stream stream)
    {
        var assets = new List<AppearanceAsset>();
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, IgnoreWhitespace = true };
        using var xml = XmlReader.Create(stream, settings);

        try
        {
            while (xml.Read())
            {
                if (xml.NodeType == XmlNodeType.Element && xml.Name == "Asset")
                    assets.Add(ReadAsset(xml.ReadSubtree()));
            }
        }
        catch (XmlException e)
        {
            throw new GameFormatException($"Invalid index.xml: {e.Message}", e.LinePosition);
        }
        return assets;
    }

    private static AppearanceAsset ReadAsset(XmlReader xml)
    {
        string id = "", artName = "", baseFile = "";
        var attachments = new List<string>();
        var materials = new List<AssetMaterial>();
        var schemeLists = new List<List<string>>();
        var overrideLists = new List<List<MaterialOverride>>();
        string? representative = null;
        int skinIndex = -1;
        Dictionary<string, string>? skinMaterials = null;
        var bodytypes = new List<string>();

        // ReadElementContentAsString already moves to the next node, so Read() runs only when nothing was consumed.
        xml.Read(); // Moves to the <Asset> element.
        xml.Read();
        while (!xml.EOF)
        {
            if (xml.NodeType != XmlNodeType.Element)
            {
                xml.Read();
                continue;
            }
            switch (xml.Name)
            {
                case "ID": id = xml.ReadElementContentAsString(); break;
                case "ArtName": artName = xml.ReadElementContentAsString(); break;
                case "BaseFile": baseFile = xml.ReadElementContentAsString(); break;
                case "Bodytype": bodytypes.Add(xml.ReadElementContentAsString()); break;
                case "SkinMaterialIndex": skinIndex = int.TryParse(xml.ReadElementContentAsString(), out int s) ? s : -1; break;
                case "SkinMaterial":
                    if (xml.GetAttribute("slot") is { Length: > 0 } skinSlot)
                        (skinMaterials ??= [])[skinSlot] = xml.GetAttribute("filename") ?? "";
                    xml.Read();
                    break;
                case "Data":
                    // Skin, hair and eye color entries carry a color to show in menus: "r,g,b" in the range 0 to 1.
                    representative ??= xml.GetAttribute("RepresentativeColor");
                    xml.Read();
                    break;
                case "Attachment":
                    attachments.Add(xml.GetAttribute("filename") ?? "");
                    xml.Read();
                    break;
                case "Material":
                    schemeLists.Add([]);
                    overrideLists.Add([]);
                    materials.Add(new AssetMaterial(xml.GetAttribute("id") ?? "", xml.GetAttribute("name") ?? "", xml.GetAttribute("filename") ?? "", schemeLists[^1], overrideLists[^1]));
                    xml.Read();
                    break;
                case "MaterialOverride" when overrideLists.Count > 0:
                    if (int.TryParse(xml.GetAttribute("index"), out int overrideIndex))
                        overrideLists[^1].Add(new MaterialOverride(overrideIndex, xml.GetAttribute("filename") ?? ""));
                    xml.Read();
                    break;
                case "ColorScheme" when schemeLists.Count > 0:
                    // These elements follow their <Material> start tag, so they belong to the last material.
                    schemeLists[^1].Add(xml.GetAttribute("guid") ?? "");
                    xml.Read();
                    break;
                default: xml.Read(); break;
            }
        }
        return new AppearanceAsset(id, artName, baseFile, attachments, materials, bodytypes, representative, skinIndex, skinMaterials);
    }
}
