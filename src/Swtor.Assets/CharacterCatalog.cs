using Swtor.Formats.Gom;

namespace Swtor.Assets;

/// <summary>All character creator specs (one per class, gender and race), read from the game database.</summary>
public sealed class CharacterCatalog
{
    public IReadOnlyList<CharacterSpec> Specs { get; }

    private CharacterCatalog(List<CharacterSpec> specs) => Specs = specs;

    /// <summary>Decodes every "pcs." object of the database (about 200 objects).</summary>
    public static CharacterCatalog Load(GomDatabase database)
    {
        var specs = new List<CharacterSpec>();
        foreach (var entry in database.WithPrefix("pcs."))
        {
            if (CharacterSpec.FromNode(database.Decode(entry)) is { } spec) specs.Add(spec);
        }
        specs.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return new CharacterCatalog(specs);
    }

    public IReadOnlyList<string> Classes => Specs.Select(s => s.Class).Distinct().Order().ToList();

    public IReadOnlyList<string> Genders(string className) =>
        Specs.Where(s => s.Class == className).Select(s => s.Gender).Distinct().Order().ToList();

    public IReadOnlyList<string> Races(string className, string gender) =>
        Specs.Where(s => s.Class == className && s.Gender == gender).Select(s => s.Race).Distinct().Order().ToList();

    /// <summary>Finds a spec. Legacy specs are variants of a race for players of the legacy system.</summary>
    public CharacterSpec? Find(string className, string gender, string race, bool legacy) =>
        Specs.FirstOrDefault(s => s.Class == className && s.Gender == gender && s.Race == race && s.IsLegacy == legacy);
}
