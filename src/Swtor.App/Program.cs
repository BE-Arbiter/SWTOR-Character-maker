using Swtor.App;

// Optional arguments: a .gr2 file to open at start, then a color scheme guid to apply to it.
using var game = new ViewerGame(args.FirstOrDefault(), args.Skip(1).FirstOrDefault());
game.Run();
