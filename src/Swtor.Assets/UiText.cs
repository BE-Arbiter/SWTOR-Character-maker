using Swtor.Formats.Stb;

namespace Swtor.Assets;

/// <summary>
/// Names of classes, races, appearance slots and body types in the game language, read from the text tables of the extract
/// (fr-fr/str/gui/*.stb). When a table or a text is missing, the methods return an English name made from the code.
/// The tables have ids only, so the ids of the texts are listed here. They were found by reading the tables.
/// </summary>
public sealed class UiText
{
    private const long CharacterCreate = 836015384166400; // id base of gui/charactercreate.stb
    private const long ClassNames = 836036859002880;      // gui/classnames.stb
    private const long RaceNames = 835993909329968;       // gui/backgroundnames.stb
    private const long EquipSlots = 2073124879204352;     // gui/equipslot.stb

    private static readonly Dictionary<string, long> ClassIds = new()
    {
        ["sith_warrior"] = ClassNames + 1, ["sith_inquisitor"] = ClassNames + 2, ["bounty_hunter"] = ClassNames + 3,
        ["imperial_agent"] = ClassNames + 4, ["jedi_knight"] = ClassNames + 5, ["jedi_consular"] = ClassNames + 6,
        ["smuggler"] = ClassNames + 7, ["trooper"] = ClassNames + 8,
    };

    private static readonly Dictionary<string, long> RaceIds = new()
    {
        ["human"] = RaceNames + 2, ["zabrak"] = RaceNames + 3, ["cyborg"] = RaceNames + 4, ["sith"] = RaceNames + 5,
        ["twilek"] = RaceNames + 6, ["rattataki"] = RaceNames + 7, ["chiss"] = RaceNames + 8, ["mirialan"] = RaceNames + 9,
        ["miralukan"] = RaceNames + 10, ["cathar"] = 835993909330177, ["togruta"] = 835993909330178, ["nautolan"] = 835993909330179,
    };

    private static readonly Dictionary<AppearanceSlot, long> SlotIds = new()
    {
        [AppearanceSlot.Head] = CharacterCreate + 27, [AppearanceSlot.Complexion] = CharacterCreate + 28,
        [AppearanceSlot.Hair] = CharacterCreate + 30, [AppearanceSlot.HairColor] = CharacterCreate + 31,
        [AppearanceSlot.SkinColor] = CharacterCreate + 104, [AppearanceSlot.EyeColor] = CharacterCreate + 105,
        [AppearanceSlot.FacePaint] = CharacterCreate + 106,
    };

    private static readonly Dictionary<string, long> EquipSlotIds = new()
    {
        ["chest"] = EquipSlots + 3, ["leg"] = EquipSlots + 4, ["bracer"] = EquipSlots + 5,
        ["waist"] = EquipSlots + 6, ["hand"] = EquipSlots + 7, ["boot"] = EquipSlots + 8,
    };

    private readonly StringTable? _create, _classes, _races, _equip;

    /// <summary>A UiText without tables: every method gives the English fallback.</summary>
    public static UiText Empty { get; } = new(null, null, null, null);

    private UiText(StringTable? create, StringTable? classes, StringTable? races, StringTable? equip)
    {
        _create = create;
        _classes = classes;
        _races = races;
        _equip = equip;
    }

    /// <summary>Reads the tables of the first language folder that has them. Missing tables are skipped.</summary>
    public static UiText Load(string root)
    {
        static StringTable? Read(string root, string name)
        {
            try
            {
                return TextTables.Find(root, name) is { } path ? StringTable.Parse(File.ReadAllBytes(path)) : null;
            }
            catch (Exception e) when (e is IOException or Swtor.Formats.GameFormatException or UnauthorizedAccessException)
            {
                return null;
            }
        }
        return new UiText(Read(root, Path.Combine("gui", "charactercreate.stb")), Read(root, Path.Combine("gui", "classnames.stb")),
            Read(root, Path.Combine("gui", "backgroundnames.stb")), Read(root, Path.Combine("gui", "equipslot.stb")));
    }

    /// <summary>"sith_warrior" gives "Guerrier Sith" with the French tables.</summary>
    public string Class(string code) => ClassIds.TryGetValue(code, out long id) && _classes?.Get(id) is { } text ? text : Pretty(code);

    /// <summary>Race code of a spec ("twilek", "zabrak_imp"). The part after the first underscore (faction of a legacy race) is added in brackets.</summary>
    public string Race(string code)
    {
        string[] parts = code.Split('_');
        if (!RaceIds.TryGetValue(parts[0], out long id) || _races?.Get(id) is not { } text) return Pretty(code);
        return parts.Length > 1 ? $"{text} ({string.Join(' ', parts.Skip(1))})" : text;
    }

    /// <summary>"male" gives "Homme" and "female" gives "Femme" with the French tables.</summary>
    public string Gender(string code) =>
        _create?.Get(CharacterCreate + (code == "female" ? 1 : 107)) is { } text ? text : Pretty(code);

    /// <summary>Name of an appearance slot, for example "Forme de la tête" for the head. Slots without a game text give an English name.</summary>
    public string Slot(AppearanceSlot slot) =>
        SlotIds.TryGetValue(slot, out long id) && _create?.Get(id) is { } text ? SentenceCase(text) : SplitWords(slot.ToString());

    /// <summary>Name of an equipment slot folder ("chest" gives "Torse").</summary>
    public string EquipSlot(string slot) =>
        EquipSlotIds.TryGetValue(slot, out long id) && _equip?.Get(id) is { } text ? text : Pretty(slot);

    /// <summary>Title of the body type choice ("Type de corps").</summary>
    public string BodyTypeTitle => _create?.Get(CharacterCreate + 18) is { } text ? text : "Body type";

    /// <summary>
    /// Name of a body type from its code in art names ("bma", "bfs"). The third letter tells the build:
    /// "a" agile, "n" athletic, "s" strong, "f" or "b" robust. This was checked by measuring the naked body models.
    /// </summary>
    public string BodyType(string code)
    {
        (long offset, string english) = code.Length >= 3 ? char.ToLowerInvariant(code[2]) switch
        {
            'a' => (23L, "Agile"),
            'n' => (24L, "Athletic"),
            's' => (25L, "Strong"),
            'f' or 'b' => (26L, "Robust"),
            _ => (0L, code),
        } : (0L, code);
        return offset != 0 && _create?.Get(CharacterCreate + offset) is { } text ? text : english;
    }

    /// <summary>Turns "FORME DE LA TÊTE" into "Forme de la tête". Texts that are not all capitals stay as they are.</summary>
    public static string SentenceCase(string text) =>
        text.Length > 0 && text == text.ToUpperInvariant()
            ? char.ToUpperInvariant(text[0]) + text[1..].ToLowerInvariant()
            : text;

    // "sith_inquisitor" becomes "Sith Inquisitor".
    private static string Pretty(string text) =>
        string.Join(' ', text.Split('_').Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..]));

    // "SkinColor" becomes "Skin color".
    private static string SplitWords(string text)
    {
        var chars = new List<char>();
        foreach (char c in text)
        {
            if (chars.Count > 0 && char.IsUpper(c)) chars.Add(' ');
            chars.Add(chars.Count == 0 ? c : char.ToLowerInvariant(c));
        }
        return new string(chars.ToArray());
    }
}
