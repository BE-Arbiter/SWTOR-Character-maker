using Swtor.Formats;
using Swtor.Formats.Gr2;

// Usage:
//   swtor gr2 info <file>           Print the content of one model file.
//   swtor gr2 survey [root]         Parse every .gr2 file under root and report failures.
if (args.Length < 2 || args[0] != "gr2")
{
    Console.Error.WriteLine("Usage: swtor gr2 info <file> | swtor gr2 survey [root]");
    return 1;
}

switch (args[1])
{
    case "info" when args.Length >= 3:
        var model = Gr2Reader.Parse(File.ReadAllBytes(args[2]));
        Console.WriteLine($"version {model.Version}, bounds {model.BoundsMin} .. {model.BoundsMax}");
        Console.WriteLine($"materials: {string.Join(", ", model.Materials)}");
        foreach (var m in model.Meshes)
        {
            Console.WriteLine($"mesh '{m.Name}': {m.VertexCount} vertices, {m.Indices.Length / 3} triangles, flags {(int)m.Flags} ({m.Flags}), {m.UvSets.Count} UV sets");
            foreach (var p in m.Pieces)
                Console.WriteLine($"  piece: triangles {p.StartTriangle}+{p.TriangleCount}, material {p.MaterialIndex}");
            Console.WriteLine($"  bones: {string.Join(", ", m.Bones.Select(b => b.Name))}");
        }
        return 0;

    case "survey":
        return Survey(args.Length >= 3 ? args[2] : Path.Combine(Environment.GetEnvironmentVariable("SWTOR_ASSETS") ?? @"C:\jka_tor_assets\resources", "art"));

    default:
        Console.Error.WriteLine("Unknown command");
        return 1;
}

static int Survey(string root)
{
    int ok = 0, failed = 0;
    var errors = new Dictionary<string, (int Count, string Example)>();
    var layouts = new Dictionary<Gr2VertexFlags, int>();
    foreach (var path in Directory.EnumerateFiles(root, "*.gr2", SearchOption.AllDirectories))
    {
        try
        {
            var model = Gr2Reader.Parse(File.ReadAllBytes(path));
            foreach (var m in model.Meshes) layouts[m.Flags] = layouts.GetValueOrDefault(m.Flags) + 1;
            ok++;
        }
        catch (GameFormatException e)
        {
            failed++;
            string key = e.Message[..e.Message.LastIndexOf(" (offset", StringComparison.Ordinal)];
            errors[key] = (errors.GetValueOrDefault(key).Count + 1, errors.TryGetValue(key, out var v) ? v.Example : path);
        }
    }
    Console.WriteLine($"parsed {ok}, failed {failed}");
    foreach (var (flags, n) in layouts.OrderByDescending(l => l.Value)) Console.WriteLine($"  layout {(int)flags}: {n}");
    foreach (var (msg, (n, ex)) in errors.OrderByDescending(e => e.Value.Count)) Console.WriteLine($"  {n} x {msg}  e.g. {ex}");
    return failed == 0 ? 0 : 2;
}
