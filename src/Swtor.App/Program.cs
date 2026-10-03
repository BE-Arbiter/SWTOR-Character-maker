using Swtor.App;

// Optional arguments: a .gr2 file to open at start, then a color scheme guid to apply to it.
// "--character" starts on the Character tab.
var positional = args.Where(a => !a.StartsWith("--")).ToList();
using var game = new ViewerGame(positional.FirstOrDefault(), positional.Skip(1).FirstOrDefault(), args.Contains("--character"));
game.Run();
