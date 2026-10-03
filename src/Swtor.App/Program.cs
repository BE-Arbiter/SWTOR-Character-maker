using Swtor.App;

// Optional arguments: a .gr2 file to open at start, then a color scheme guid to apply to it.
// "--character" starts on the Character tab. "--equip slot=art_name" equips an asset (repeat for several slots).
var equipment = new List<(string Slot, string ArtName)>();
var positional = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--equip" && i + 1 < args.Length && args[i + 1].Split('=', 2) is [var slot, var name])
    {
        equipment.Add((slot, name));
        i++;
    }
    else if (!args[i].StartsWith("--"))
    {
        positional.Add(args[i]);
    }
}
using var game = new ViewerGame(positional.FirstOrDefault(), positional.Skip(1).FirstOrDefault(), args.Contains("--character"), equipment);
game.Run();
