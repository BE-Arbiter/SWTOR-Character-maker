using System.Xml.Linq;

namespace Swtor.Formats.Xml;

/// <summary>A game material (.mat file). Inputs are listed by semantic name, for example "DiffuseMap".</summary>
public sealed record MaterialDef(string Shader, IReadOnlyDictionary<string, string> Inputs)
{
    /// <summary>Path of the diffuse texture without extension, or null. Uses '/' separators and no leading slash.</summary>
    public string? DiffuseMap => TexturePath("DiffuseMap");

    /// <summary>Returns the texture path of an input, normalized: '/' separators, no leading slash, no extension.</summary>
    public string? TexturePath(string semantic) =>
        Inputs.TryGetValue(semantic, out var value) && value.Length > 0
            ? value.Replace('\\', '/').TrimStart('/')
            : null;
}

public static class MaterialReader
{
    /// <summary>Parses the text of a .mat file.</summary>
    public static MaterialDef Parse(string xml)
    {
        try
        {
            var root = XDocument.Parse(xml.TrimStart('﻿')).Root
                ?? throw new GameFormatException("Material has no root element", 0);
            var inputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var input in root.Elements("input"))
            {
                string? semantic = input.Element("semantic")?.Value;
                if (!string.IsNullOrEmpty(semantic)) inputs[semantic] = input.Element("value")?.Value ?? "";
            }
            return new MaterialDef(root.Element("Derived")?.Value ?? "", inputs);
        }
        catch (System.Xml.XmlException e)
        {
            throw new GameFormatException($"Invalid material: {e.Message}", e.LinePosition);
        }
    }
}
