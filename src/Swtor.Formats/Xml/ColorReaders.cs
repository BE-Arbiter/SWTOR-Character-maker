using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Swtor.Formats.Xml;

/// <summary>Primary and secondary garment palette (asset ids) for one equipment slot.</summary>
public readonly record struct SlotPalettes(string Primary, string Secondary);

/// <summary>
/// A color scheme: for each slot ("chest", "boot", ...) the ids of two garment palettes.
/// Materials list the schemes that suit them (<see cref="AssetMaterial.ColorSchemeIds"/>).
/// </summary>
public sealed record ColorScheme(string Guid, string Name, IReadOnlyDictionary<string, SlotPalettes> Slots);

/// <summary>
/// A garment palette (art/dynamic/garmenthue/*.xml). Hue and saturation define the color.
/// Brightness is an offset (about -0.5 to 0.9). Contrast is a factor (0 to 3).
/// </summary>
public sealed record Palette(string Name, float Hue, float Saturation, float Brightness, float Contrast);

public static class ColorSchemeIndexReader
{
    /// <summary>Reads art/dynamic/colorscheme/index.xml.</summary>
    public static List<ColorScheme> Read(Stream stream)
    {
        var schemes = new List<ColorScheme>();
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, IgnoreWhitespace = true };
        using var xml = XmlReader.Create(stream, settings);
        try
        {
            // Only <ColorScheme> elements with child elements are schemes.
            // (Material entries in other index files use an empty <ColorScheme guid="..."/> element.)
            xml.MoveToContent();
            while (!xml.EOF)
            {
                if (xml.NodeType == XmlNodeType.Element && xml.Name == "ColorScheme" && !xml.IsEmptyElement)
                    schemes.Add(ReadScheme((XElement)XNode.ReadFrom(xml)));
                else
                    xml.Read();
            }
        }
        catch (XmlException e)
        {
            throw new GameFormatException($"Invalid colorscheme index: {e.Message}", e.LinePosition);
        }
        return schemes;
    }

    private static ColorScheme ReadScheme(XElement element)
    {
        var slots = new Dictionary<string, SlotPalettes>(StringComparer.OrdinalIgnoreCase);
        foreach (var slot in element.Elements("slot"))
        {
            string? name = (string?)slot.Attribute("name");
            if (name is not null)
                slots[name] = new SlotPalettes((string?)slot.Attribute("primary") ?? "", (string?)slot.Attribute("secondary") ?? "");
        }
        return new ColorScheme(element.Element("Guid")?.Value ?? "", element.Element("Name")?.Value ?? "", slots);
    }
}

public static class PaletteReader
{
    /// <summary>Parses a garment palette file.</summary>
    public static Palette Parse(string xml)
    {
        try
        {
            var root = XDocument.Parse(xml.TrimStart('﻿')).Root
                ?? throw new GameFormatException("Palette has no root element", 0);
            return new Palette(
                root.Element("Name")?.Value ?? "",
                Number(root, "Hue"), Number(root, "Saturation"), Number(root, "Brightness"), Number(root, "Contrast", 1f));
        }
        catch (XmlException e)
        {
            throw new GameFormatException($"Invalid palette: {e.Message}", e.LinePosition);
        }
    }

    private static float Number(XElement root, string name, float fallback = 0f) =>
        float.TryParse(root.Element(name)?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
}
