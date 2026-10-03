using Swtor.Formats;
using Swtor.Formats.Gr2;

// Usage:
//   swtor gr2 info <file>           Print the content of one model file.
//   swtor gr2 survey [root]         Parse every .gr2 file under root and report failures.
if (args.Length >= 1 && args[0] == "index")
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var index = Swtor.Assets.AssetIndex.Load(DefaultRoot(), new Progress<int>(n => Console.Write($"\r{n} files seen")), forceRescan: args.Contains("--rescan"));
    Console.WriteLine($"\n{index.Models.Count} models, {index.Textures.Count} textures in {sw.Elapsed}");
    return 0;
}

if (args.Length >= 2 && args[0] == "appearance")
    return AppearanceCommand(args);

if (args.Length >= 2 && args[0] == "dds")
    return DdsCommand(args);

if (args.Length < 2 || args[0] != "gr2")
{
    Console.Error.WriteLine("Usage: swtor gr2 info <file> | gr2 survey [root] | dds survey | dds decode <file> <out.ppm> | index [--rescan]");
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

static string DefaultRoot() => Environment.GetEnvironmentVariable("SWTOR_ASSETS") ?? @"C:\jka_tor_assets\resources";

// dds survey: reads every texture (header, then full decode of every 50th file) and counts formats.
// dds decode <file> <out.ppm>: writes the image as a PPM file, to view it.
static int DdsCommand(string[] args)
{
    if (args[1] == "decode" && args.Length >= 4)
    {
        var image = Swtor.Formats.Dds.DdsReader.Decode(File.ReadAllBytes(args[2]));
        using var output = File.Create(args[3]);
        output.Write(System.Text.Encoding.ASCII.GetBytes($"P6\n{image.Width} {image.Height}\n255\n"));
        for (int i = 0; i < image.Width * image.Height; i++) output.Write(image.Rgba.AsSpan(i * 4, 3));
        return 0;
    }

    var index = Swtor.Assets.AssetIndex.Load(DefaultRoot());
    var formats = new Dictionary<string, int>();
    var errors = new Dictionary<string, (int Count, string Example)>();
    int n = 0, decoded = 0;
    foreach (var texture in index.Textures)
    {
        string path = index.FullPath(texture);
        try
        {
            var bytes = File.ReadAllBytes(path);
            var info = Swtor.Formats.Dds.DdsReader.ReadInfo(bytes);
            string key = $"{info.Format}";
            formats[key] = formats.GetValueOrDefault(key) + 1;
            if (n++ % 50 == 0) { Swtor.Formats.Dds.DdsReader.Decode(bytes); decoded++; }
        }
        catch (GameFormatException e)
        {
            string key = e.Message[..e.Message.LastIndexOf(" (offset", StringComparison.Ordinal)];
            errors[key] = (errors.GetValueOrDefault(key).Count + 1, errors.TryGetValue(key, out var v) ? v.Example : texture);
        }
    }
    Console.WriteLine($"textures {index.Textures.Count}, fully decoded {decoded}");
    foreach (var (f, c) in formats.OrderByDescending(x => x.Value)) Console.WriteLine($"  {f}: {c}");
    foreach (var (msg, (c, ex)) in errors.OrderByDescending(x => x.Value.Count)) Console.WriteLine($"  ERROR {c} x {msg}  e.g. {ex}");
    return errors.Count == 0 ? 0 : 2;
}

// appearance find <model>: lists the assets and materials for one model (path relative to the root).
// appearance survey: checks, for every model with an asset, that the first material and its diffuse texture exist.
static int AppearanceCommand(string[] args)
{
    var index = Swtor.Assets.AssetIndex.Load(DefaultRoot());
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var catalog = index.Appearances;
    Console.WriteLine($"{catalog.AssetCount} assets in {catalog.Slots.Count} slots, loaded in {sw.Elapsed}");

    if (args[1] == "find" && args.Length >= 3)
    {
        foreach (var match in catalog.Find(args[2]))
        {
            Console.WriteLine($"{match.Asset.ArtName} (slot {match.Slot}, gender {match.Gender}, attachment {match.IsAttachment})");
            foreach (var material in match.Asset.Materials)
            {
                var def = catalog.ReadMaterial(material, match.Gender);
                Console.WriteLine($"  {material.Name}: {material.FileName} -> {def?.DiffuseMap ?? "(missing)"}");
            }
        }
        return 0;
    }

    int withAsset = 0, noMaterial = 0, noTexture = 0, ok = 0;
    string? exampleNoTexture = null;
    foreach (var model in index.Models)
    {
        var matches = catalog.Find(model);
        if (matches.Count == 0) continue;
        withAsset++;
        var first = matches[0];
        var def = first.Asset.Materials.Count > 0 ? catalog.ReadMaterial(first.Asset.Materials[0], first.Gender) : null;
        if (def is null) { noMaterial++; continue; }
        string? diffuse = def.DiffuseMap;
        if (diffuse is null || !File.Exists(index.FullPath(diffuse + ".dds"))) { noTexture++; exampleNoTexture ??= $"{model} -> {diffuse}"; continue; }
        ok++;
    }
    Console.WriteLine($"models {index.Models.Count}, with asset {withAsset}: texture found {ok}, no material {noMaterial}, texture missing {noTexture} (e.g. {exampleNoTexture})");
    return 0;
}
