namespace Swtor.Assets;

/// <summary>Armor weight, as written in the art name of an armor asset.</summary>
public enum ArmorWeight { Unknown, Light, Medium, Heavy }

/// <summary>
/// Weight and class of an armor asset, read from its art name. Art names look like
/// "chest_armor01_heavy_bh_a02": slot, style, weight ("light", "med", "heavy"), class code, number.
/// Class code "ge" is a generic look for every class. Names without a weight or a known class code (naked bodies, underwear) give
/// <see cref="ArmorWeight.Unknown"/> and a null <see cref="ClassCode"/>.
/// </summary>
public sealed record ArmorInfo(ArmorWeight Weight, string? ClassCode)
{
    /// <summary>Class codes found in art names, with the class they stand for. They were checked against item names.</summary>
    public static readonly IReadOnlyDictionary<string, string> Classes = new Dictionary<string, string>
    {
        ["ge"] = "Generic",
        ["bh"] = "Bounty Hunter",
        ["tr"] = "Trooper",
        ["sw"] = "Sith Warrior",
        ["ss"] = "Sith Inquisitor",
        ["sm"] = "Smuggler",
        ["sp"] = "Imperial Agent",
        ["jk"] = "Jedi Knight",
        ["jw"] = "Jedi Consular",
    };

    public static ArmorInfo Parse(string artName)
    {
        string[] parts = artName.Split('_');
        for (int i = 1; i < parts.Length; i++)
        {
            var weight = parts[i].ToLowerInvariant() switch
            {
                "light" => ArmorWeight.Light,
                "med" => ArmorWeight.Medium,
                "heavy" => ArmorWeight.Heavy,
                _ => ArmorWeight.Unknown,
            };
            if (weight == ArmorWeight.Unknown) continue;
            string? code = i + 1 < parts.Length && Classes.ContainsKey(parts[i + 1].ToLowerInvariant()) ? parts[i + 1].ToLowerInvariant() : null;
            return new ArmorInfo(weight, code);
        }
        return new ArmorInfo(ArmorWeight.Unknown, null);
    }
}
