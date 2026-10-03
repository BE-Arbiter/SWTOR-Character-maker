using Swtor.App;

// Optional argument: a .gr2 file to open at start.
using var game = new ViewerGame(args.FirstOrDefault());
game.Run();
