using System.Xml;

namespace Swtor.Formats.Xml;

/// <summary>A material variant of an asset. <see cref="FileName"/> may contain the "[gen]" placeholder.</summary>
public sealed record AssetMaterial(string Id, string Name, string FileName);

/// <summary>
/// One entry of a slot <c>index.xml</c> (art/dynamic/&lt;slot&gt;/index.xml):
/// a model, its extra model files and its material variants.
/// File names may contain the "[bt]" placeholder (body type, for example "bfa" or "bmn").
/// </summary>
public sealed record AppearanceAsset(
    string Id,
    string ArtName,
    string BaseFile,
    IReadOnlyList<string> Attachments,
    IReadOnlyList<AssetMaterial> Materials,
    IReadOnlyList<string> Bodytypes);

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
                case "Attachment":
                    attachments.Add(xml.GetAttribute("filename") ?? "");
                    xml.Read();
                    break;
                case "Material":
                    // The element also holds <ColorSchemes>. The loop skips them.
                    materials.Add(new AssetMaterial(xml.GetAttribute("id") ?? "", xml.GetAttribute("name") ?? "", xml.GetAttribute("filename") ?? ""));
                    xml.Read();
                    break;
                default: xml.Read(); break;
            }
        }
        return new AppearanceAsset(id, artName, baseFile, attachments, materials, bodytypes);
    }
}
