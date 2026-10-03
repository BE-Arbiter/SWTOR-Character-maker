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

if (args.Length >= 1 && args[0] == "items")
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var itemDb = Swtor.Assets.GomDatabase.Open(DefaultRoot());
    string stbPath = Directory.EnumerateFiles(Path.Combine(DefaultRoot(), "fr-fr", "str"), "itm.stb").First();
    var itemNames = Swtor.Formats.Stb.StringTable.Parse(File.ReadAllBytes(stbPath));
    var catalog = Swtor.Assets.ItemCatalog.Load(itemDb, itemNames);
    Console.WriteLine($"{itemNames.Count} texts, {catalog.Count} items with an appearance in {sw.Elapsed}");
    var itemIndex = Swtor.Assets.AssetIndex.Load(DefaultRoot());
    foreach (string artName in new[] { "chest_armor01_heavy_bh_a02", "leg_armor01_heavy_bh_a02" })
        if (itemIndex.Appearances.AssetsOfSlot(artName.Split('_')[0]).FirstOrDefault(a => a.ArtName == artName) is { } asset)
            foreach (var item in catalog.ForAsset(long.Parse(asset.Id)).Where((_, i) => i % 9 == 0).Take(16)) Console.WriteLine($"  {artName}: {item.Name} (level {item.Level}, {item.QualityName})");
    return 0;
}

if (args.Length >= 1 && args[0] == "weapons")
{
    var weapons = Swtor.Assets.WeaponCatalog.Load(Swtor.Assets.GomDatabase.Open(DefaultRoot()));
    var weaponIndex = Swtor.Assets.AssetIndex.Load(DefaultRoot());
    Console.WriteLine($"{weapons.Items.Count} weapons");
    foreach (var w in weapons.Items.Where(w => weaponIndex.HasModel(w.ModelPath) && weaponIndex.FindTextures(w.ModelPath).Count == 0).Take(args.Contains("--missing") ? 12 : 0))
        Console.WriteLine($"  no texture: {w.ModelPath}");
    foreach (var g in weapons.Items.GroupBy(w => w.CombatType).OrderByDescending(g => g.Count()))
        Console.WriteLine($"  {g.Key}: {g.Count()} (model found {g.Count(w => weaponIndex.HasModel(w.ModelPath))}, textured {g.Count(w => weaponIndex.FindTextures(w.ModelPath).Count > 0)})");
    return 0;
}

if (args.Length >= 2 && args[0] == "char")
    return CharCommand(args);

if (args.Length >= 2 && args[0] == "gom")
    return GomCommand(args);

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
    if (args[1] == "decode" && args.Length >= 4) // optional 5th argument "alpha" writes the alpha channel
    {
        var image = Swtor.Formats.Dds.DdsReader.Decode(File.ReadAllBytes(args[2]));
        using var output = File.Create(args[3]);
        output.Write(System.Text.Encoding.ASCII.GetBytes($"P6\n{image.Width} {image.Height}\n255\n"));
        bool alpha = args.Length >= 5 && args[4] == "alpha";
        for (int i = 0; i < image.Width * image.Height; i++)
        {
            if (alpha) output.Write([image.Rgba[i * 4 + 3], image.Rgba[i * 4 + 3], image.Rgba[i * 4 + 3]]);
            else output.Write(image.Rgba.AsSpan(i * 4, 3));
        }
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

// gom survey: decodes every object of every bucket and prototype file and reports failures.
// gom find <text>: lists objects whose name contains the text.
// gom dump <exact name>: prints one decoded object.
static int GomCommand(string[] args)
{
    string gomRoot = Path.Combine(DefaultRoot(), "systemgenerated");
    var bucketFiles = Directory.GetFiles(Path.Combine(gomRoot, "buckets"), "*.bkt");
    var nodeFiles = Directory.GetFiles(Path.Combine(gomRoot, "prototypes"), "*.node");

    if (args[1] == "survey")
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var errors = new System.Collections.Concurrent.ConcurrentDictionary<string, (int Count, string Example)>();
        long ok = 0, failed = 0;
        void Fail(string where, Exception e)
        {
            Interlocked.Increment(ref failed);
            string key = e is GameFormatException ? e.Message[..Math.Max(0, e.Message.LastIndexOf(" (offset", StringComparison.Ordinal))] : e.GetType().Name + ": " + e.Message;
            errors.AddOrUpdate(key, (1, where), (_, old) => (old.Count + 1, old.Example));
        }
        Parallel.ForEach(bucketFiles, file =>
        {
            Swtor.Formats.Gom.GomBucketFile bucket;
            try { bucket = Swtor.Formats.Gom.GomBucketFile.Open(File.ReadAllBytes(file)); }
            catch (Exception e) { Fail(file, e); return; }
            foreach (var info in bucket.Nodes)
            {
                try { bucket.Decode(info); Interlocked.Increment(ref ok); }
                catch (Exception e) when (e is GameFormatException or InvalidDataException or ArgumentException or IndexOutOfRangeException or ZstdSharp.ZstdException) { Fail($"{Path.GetFileName(file)}:{info.Name}", e); }
            }
        });
        Parallel.ForEach(nodeFiles, file =>
        {
            try { Swtor.Formats.Gom.GomPrototypeFile.Parse(File.ReadAllBytes(file)); Interlocked.Increment(ref ok); }
            catch (Exception e) when (e is GameFormatException or ArgumentException or IndexOutOfRangeException) { Fail(Path.GetFileName(file), e); }
        });
        Console.WriteLine($"decoded {ok}, failed {failed} in {sw.Elapsed}");
        foreach (var (msg, (n, ex)) in errors.OrderByDescending(e => e.Value.Count).Take(25)) Console.WriteLine($"  {n} x {msg}  e.g. {ex}");
        return failed == 0 ? 0 : 2;
    }

    if (args[1] == "enum" && args.Length >= 3)
    {
        var s2 = Swtor.Formats.Gom.GomSchema.Parse(File.ReadAllBytes(Path.Combine(gomRoot, "client.gom")));
        foreach (var e in s2.Enums.Values.Where(e => e.Names.Any(n => n.Contains(args[2], StringComparison.OrdinalIgnoreCase))))
            Console.WriteLine($"0x{e.Id:X}: {string.Join(", ", e.Names.Select((n, i) => $"{i}={n}"))}");
        return 0;
    }

    if (args[1] == "schema")
    {
        var s = Swtor.Formats.Gom.GomSchema.Parse(File.ReadAllBytes(Path.Combine(gomRoot, "client.gom")));
        Console.WriteLine($"{s.Enums.Count} enums, {s.Classes.Count} classes, {s.Fields.Count} fields");
        return 0;
    }

    if (args.Length >= 3 && args[1] is "find" or "dump")
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var db = Swtor.Assets.GomDatabase.Open(DefaultRoot());
        Console.WriteLine($"{db.Entries.Count} objects opened in {sw.Elapsed}");
        if (args[1] == "find")
        {
            foreach (var e in db.Entries.Where(e => e.Name.Contains(args[2], StringComparison.OrdinalIgnoreCase)).Take(50))
                Console.WriteLine($"{e.Name}  id 0x{e.Id:X}  class 0x{e.ClassId:X}");
            return 0;
        }
        var entry = db.Find(args[2]);
        if (entry is null) { Console.Error.WriteLine("Not found"); return 1; }
        Console.WriteLine($"{entry.Name}  id 0x{entry.Id:X}  class 0x{entry.ClassId:X}");
        PrintObject(db.Decode(entry).Object, 1, db.Schema);
        return 0;
    }
    return 1;
}

// Prints an object. When the schema is known, enum values show their names.
static void PrintObject(Swtor.Formats.Gom.GomObject obj, int indent, Swtor.Formats.Gom.GomSchema? schema = null)
{
    foreach (var f in obj.Fields)
    {
        var declared = schema is not null && schema.Fields.TryGetValue(f.Id, out var def) ? def.Type : null;
        PrintValue($"0x{f.Id:X} ({f.Type})", f.Value, declared, indent, schema);
    }
}

static void PrintValue(string label, object? value, Swtor.Formats.Gom.GomTypeDescriptor? declared, int indent, Swtor.Formats.Gom.GomSchema? schema)
{
    string pad = new(' ', indent * 2);
    switch (value)
    {
        case Swtor.Formats.Gom.GomObject inner:
            Console.WriteLine($"{pad}{label}:");
            PrintObject(inner, indent + 1, schema);
            break;
        case Swtor.Formats.Gom.GomList list:
            Console.WriteLine($"{pad}{label} list<{list.ItemType}> [{list.Items.Count}]");
            foreach (var item in list.Items.Take(DumpSettings.Limit)) PrintValue("-", item, declared?.Item, indent + 1, schema);
            if (list.Items.Count > DumpSettings.Limit) Console.WriteLine($"{pad}  ... {list.Items.Count - DumpSettings.Limit} more");
            break;
        case Swtor.Formats.Gom.GomMap map:
            Console.WriteLine($"{pad}{label} map<{map.KeyType},{map.ValueType}> [{map.Entries.Count}]");
            foreach (var e in map.Entries.Take(DumpSettings.Limit))
                PrintValue($"{Format(e.Key, declared?.Item, schema)} =>", e.Value, declared?.Value, indent + 1, schema);
            if (map.Entries.Count > DumpSettings.Limit) Console.WriteLine($"{pad}  ... {map.Entries.Count - DumpSettings.Limit} more");
            break;
        default:
            Console.WriteLine($"{pad}{label} {Format(value, declared, schema)}");
            break;
    }
}

static string Format(object? value, Swtor.Formats.Gom.GomTypeDescriptor? declared, Swtor.Formats.Gom.GomSchema? schema)
{
    if (value is Swtor.Formats.Gom.GomEnumValue e)
    {
        string? name = declared is { Type: Swtor.Formats.Gom.GomType.Enum } && schema is not null ? schema.EnumName(declared.ReferenceId, e.Value) : null;
        return name is null ? $"enum {e.Value}" : $"{name} ({e.Value})";
    }
    return value?.ToString() ?? "null";
}

// char list: lists all character specs. char spec <name>: prints the options of one spec with their art names.
static int CharCommand(string[] args)
{
    var db = Swtor.Assets.GomDatabase.Open(DefaultRoot());
    var index = Swtor.Assets.AssetIndex.Load(DefaultRoot());
    if (args[1] == "list")
    {
        foreach (var e in db.WithPrefix("pcs.").OrderBy(e => e.Name)) Console.WriteLine(e.Name);
        return 0;
    }
    var entry = args.Length >= 3 ? db.Find(args[2]) : null;
    if (entry is null) { Console.Error.WriteLine("Spec not found"); return 1; }

    var spec = Swtor.Assets.CharacterSpec.FromNode(db.Decode(entry))!;
    Console.WriteLine($"{spec.Name}: class {spec.Class}, gender {spec.Gender}, race {spec.Race}, legacy {spec.IsLegacy}, {spec.Options.Count} options");
    foreach (var group in spec.Options.GroupBy(o => o.Slot))
    {
        Console.WriteLine($"-- {group.Key} ({group.Count()})");
        foreach (var o in group.Take(args.Length >= 4 ? int.Parse(args[3]) : 5))
        {
            var found = index.Appearances.FindAsset(o.AssetId);
            var material = found?.Asset.Materials.FirstOrDefault(m => m.Id == o.MaterialId.ToString());
            Console.WriteLine($"   key {o.Key}: asset {o.AssetId} {found?.Asset.ArtName ?? "(unknown)"} [{found?.Slot}] material {o.MaterialId} {material?.Name} {found?.Asset.RepresentativeColor}");
        }
    }
    var first = spec.Options.First(o => o.Slot == Swtor.Assets.AppearanceSlot.Head);
    Console.WriteLine($"compatible with head option {first.Key}:");
    foreach (var (slot, keys) in spec.Compatible[first.Key]) Console.WriteLine($"   {slot}: {string.Join(",", keys.Take(20))}{(keys.Count > 20 ? "..." : "")}");
    return 0;
}

static class DumpSettings
{
    /// <summary>Maximum list or map entries printed by "gom dump". Set SWTOR_DUMP_LIMIT to change it.</summary>
    public static readonly int Limit = int.TryParse(Environment.GetEnvironmentVariable("SWTOR_DUMP_LIMIT"), out var limit) ? limit : 12;
}
