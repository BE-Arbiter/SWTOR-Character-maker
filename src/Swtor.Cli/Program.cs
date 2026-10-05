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



if (args.Length >= 3 && args[0] == "stb")
{
    var table = Swtor.Formats.Stb.StringTable.Parse(File.ReadAllBytes(Path.Combine(DefaultRoot(), "fr-fr", "str", args[1].Replace('/', Path.DirectorySeparatorChar))));
    foreach (var (id, text) in table.Entries().Where(e => args[2] == "*" || e.Text.Contains(args[2], StringComparison.OrdinalIgnoreCase)).Take(60)) Console.WriteLine($"{id}: {text.Replace('\n', ' ')}");
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
    var weaponCatalog = Swtor.Assets.WeaponCatalog.Load(itemDb);
    Console.WriteLine($"{weaponCatalog.Items.Count(w => catalog.ForWeaponKey(w.Key).Count > 0)} of {weaponCatalog.Items.Count} weapons have an item name");
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

// palettes [filter]: lists the garment palettes with the color that they give to a mid-grey pixel.
if (args.Length >= 1 && args[0] == "palettes")
{
    var colorCatalog = new Swtor.Assets.ColorCatalog(DefaultRoot());
    foreach (var pal in colorCatalog.Palettes.Where(x => args.Length < 2 || x.Name.Contains(args[1], StringComparison.OrdinalIgnoreCase)))
    {
        if (colorCatalog.ReadPalette(pal.Id) is not { } palette) continue;
        var (r, g, b) = Swtor.Formats.Dds.PaletteTint.Swatch(palette);
        Console.WriteLine($"{pal.Id} {pal.Name} {(int)(r * 255)} {(int)(g * 255)} {(int)(b * 255)} {(palette.IsPlaceholder ? "PLACEHOLDER" : Swtor.Formats.Dds.ColorNames.Describe(r, g, b))}");
    }
    return 0;
}

if (args.Length >= 2 && args[0] == "jka")
    return JkaCommand(args);

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

    // gr2 tris <folder> <name filter> <count...>: models of the folder (not .lod) whose triangle total is one of the counts.
    // Finds the SWTOR parts of an exported Jedi Academy model (in "No limit" mode the triangles are not reduced).
    case "tris" when args.Length >= 5:
        var counts = args.Skip(4).Select(int.Parse).ToHashSet();
        foreach (string file in Directory.EnumerateFiles(args[2], "*.gr2"))
        {
            string name = Path.GetFileName(file);
            if (name.EndsWith(".lod.gr2", StringComparison.OrdinalIgnoreCase) || !name.Contains(args[3], StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                int total = Gr2Reader.Parse(File.ReadAllBytes(file)).Meshes.Sum(m => m.Indices.Length / 3);
                if (counts.Contains(total)) Console.WriteLine($"{total}\t{name}");
            }
            catch (FormatException) { }
        }
        return 0;

    case "survey":
        return Survey(args.Length >= 3 ? args[2] : Path.Combine(Environment.GetEnvironmentVariable("SWTOR_ASSETS") ?? @"C:\jka_tor_assets\resources", "art"));

    default:
        Console.Error.WriteLine("Unknown command");
        return 1;
}

// Finds the .gla of a sample model. The name in the header is a game path below the folder that contains "models".
static Swtor.Formats.Jka.GlaSkeleton LoadSampleSkeleton(string animationName, string playersFolder)
{
    var dir = new DirectoryInfo(playersFolder);
    while (dir is not null && !dir.Name.Equals("models", StringComparison.OrdinalIgnoreCase)) dir = dir.Parent;
    string file = Path.Combine(dir?.Parent?.FullName ?? playersFolder, animationName.Replace('/', Path.DirectorySeparatorChar) + ".gla");
    return Swtor.Formats.Jka.GlaSkeleton.Parse(File.ReadAllBytes(file));
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

// jka bones <file.gla>: lists the bones with their bind pose origin.
// jka info <file.glm> [file.gla]: lists the surfaces, the hierarchy, the weights and the bounds.
static int JkaCommand(string[] args)
{
    if (args[1] == "bones" && args.Length >= 3)
    {
        var skeleton = Swtor.Formats.Jka.GlaSkeleton.Parse(File.ReadAllBytes(args[2]));
        Console.WriteLine($"{skeleton.Name}: {skeleton.Bones.Count} bones");
        for (int i = 0; i < skeleton.Bones.Count; i++)
        {
            var b = skeleton.Bones[i];
            Console.WriteLine($"{i,3} {b.Name,-18} parent {b.Parent,3}  origin {b.Origin.X,8:F2} {b.Origin.Y,8:F2} {b.Origin.Z,8:F2}");
        }
        return 0;
    }
    // jka heap <glm>: walks the surface tree like G2_TransformSurfaces and counts the vertices that the game transforms.
    if (args[1] == "heap" && args.Length >= 3)
    {
        var model = Swtor.Formats.Jka.GlmReader.Parse(File.ReadAllBytes(args[2]));
        var visits = new int[model.Surfaces.Count];
        long vertices = 0, used = model.Surfaces.Count * 8;
        bool failed = false;
        int heapSize = args.Length >= 4 ? int.Parse(args[3]) : 0;
        void Walk(int s, int depth)
        {
            if (depth > 200) { Console.WriteLine("cycle or depth > 200"); return; }
            var info = model.Surfaces[s];
            if ((info.Flags & 0x2) != 0 && (info.Flags & 0x100) != 0) return;
            visits[s]++;
            if (info.Flags == 0)
            {
                // The game allocates numVerts * 5 * 4 bytes per surface that is not off (R_TransformEachSurface).
                used += model.Lods[0][s].Vertices.Length * 20;
                if (heapSize > 0 && !failed && used > heapSize) { failed = true; Console.WriteLine($"a heap of {heapSize} bytes is full at surface [{s}] {info.Name}"); }
            }
            vertices += model.Lods[0][s].Vertices.Length;
            foreach (int c in info.Children) Walk(c, depth + 1);
        }
        Walk(0, 0);
        Console.WriteLine($"visited surface count {visits.Count(v => v > 0)} / {visits.Length}, visits total {visits.Sum()}, vertices transformed {vertices}, at 20 B = {vertices * 20 / 1024} KB");
        for (int i = 0; i < visits.Length; i++)
            if (visits[i] != 1) Console.WriteLine($"  [{i}] {model.Surfaces[i].Name}: visited {visits[i]} times, parent {model.Surfaces[i].Parent}");
        return 0;
    }
    // jka outliers <glm>: lists triangles with a very long edge (stray vertices).
    if (args[1] == "outliers" && args.Length >= 3)
    {
        var model = Swtor.Formats.Jka.GlmReader.Parse(File.ReadAllBytes(args[2]));
        for (int i = 0; i < model.Surfaces.Count; i++)
        {
            var g = model.Lods[0][i];
            if ((model.Surfaces[i].Flags & 1) != 0 || g.Triangles.Length == 0) continue;
            var edges = new List<float>();
            for (int t = 0; t < g.Triangles.Length; t += 3)
                for (int k = 0; k < 3; k++)
                    edges.Add(System.Numerics.Vector3.Distance(g.Vertices[g.Triangles[t + k]].Position, g.Vertices[g.Triangles[t + (k + 1) % 3]].Position));
            edges.Sort();
            float median = edges[edges.Count / 2];
            for (int t = 0; t < g.Triangles.Length; t += 3)
            {
                float longest = 0;
                for (int k = 0; k < 3; k++)
                    longest = Math.Max(longest, System.Numerics.Vector3.Distance(g.Vertices[g.Triangles[t + k]].Position, g.Vertices[g.Triangles[t + (k + 1) % 3]].Position));
                if (longest < 6 * median || longest < 4) continue;
                Console.WriteLine($"[{i}] {model.Surfaces[i].Name} tri {t / 3}: longest edge {longest:F1} (median {median:F2})");
                for (int k = 0; k < 3; k++)
                {
                    var v = g.Vertices[g.Triangles[t + k]];
                    Console.WriteLine($"    v{g.Triangles[t + k]} pos {v.Position.X:F1},{v.Position.Y:F1},{v.Position.Z:F1} uv {v.Uv.X:F2},{v.Uv.Y:F2} w {string.Join(" ", v.Weights.Select(w => $"{w.Bone}:{w.Weight:F2}"))}");
                }
            }
        }
        return 0;
    }
    // jka outliers <glm>: lists triangles with a very long edge (stray vertices).
    if (args[1] == "outliers" && args.Length >= 3)
    {
        var model = Swtor.Formats.Jka.GlmReader.Parse(File.ReadAllBytes(args[2]));
        for (int i = 0; i < model.Surfaces.Count; i++)
        {
            var g = model.Lods[0][i];
            if ((model.Surfaces[i].Flags & 1) != 0 || g.Triangles.Length == 0) continue;
            var edges = new List<float>();
            for (int t = 0; t < g.Triangles.Length; t += 3)
                for (int k = 0; k < 3; k++)
                    edges.Add(System.Numerics.Vector3.Distance(g.Vertices[g.Triangles[t + k]].Position, g.Vertices[g.Triangles[t + (k + 1) % 3]].Position));
            edges.Sort();
            float median = edges[edges.Count / 2];
            for (int t = 0; t < g.Triangles.Length; t += 3)
            {
                float longest = 0;
                for (int k = 0; k < 3; k++)
                    longest = Math.Max(longest, System.Numerics.Vector3.Distance(g.Vertices[g.Triangles[t + k]].Position, g.Vertices[g.Triangles[t + (k + 1) % 3]].Position));
                if (longest < 6 * median || longest < 4) continue;
                Console.WriteLine($"[{i}] {model.Surfaces[i].Name} tri {t / 3}: longest edge {longest:F1} (median {median:F2})");
                for (int k = 0; k < 3; k++)
                {
                    var v = g.Vertices[g.Triangles[t + k]];
                    Console.WriteLine($"    v{g.Triangles[t + k]} pos {v.Position.X:F1},{v.Position.Y:F1},{v.Position.Z:F1} uv {v.Uv.X:F2},{v.Uv.Y:F2} w {string.Join(" ", v.Weights.Select(w => $"{w.Bone}:{w.Weight:F2}"))}");
                }
            }
        }
        return 0;
    }
    // jka farweights <glm> [distance]: lists vertices that have a strong weight on a bone far from them (they stretch when animated).
    if (args[1] == "farweights" && args.Length >= 3)
    {
        var model = Swtor.Formats.Jka.GlmReader.Parse(File.ReadAllBytes(args[2]));
        var skeleton = Swtor.Formats.Jka.GlaSkeleton.Humanoid;
        float limit = args.Length >= 4 ? float.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 25f;
        for (int i = 0; i < model.Surfaces.Count; i++)
        {
            var g = model.Lods[0][i];
            if ((model.Surfaces[i].Flags & 1) != 0) continue;
            for (int v = 0; v < g.Vertices.Length; v++)
                foreach (var w in g.Vertices[v].Weights)
                {
                    float d = System.Numerics.Vector3.Distance(g.Vertices[v].Position, skeleton.Bones[w.Bone].Origin);
                    if (w.Weight >= 0.2f && d > limit)
                        Console.WriteLine(FormattableString.Invariant($"[{i}] {model.Surfaces[i].Name} v{v} pos {g.Vertices[v].Position.X:F1},{g.Vertices[v].Position.Y:F1},{g.Vertices[v].Position.Z:F1} bone {skeleton.Bones[w.Bone].Name} weight {w.Weight:F2} distance {d:F1}"));
                }
        }
        return 0;
    }
    // jka stretch <glm> [seeds]: poses the skeleton with random rotations and lists the vertices whose edges stretch the most.
    if (args[1] == "stretch" && args.Length >= 3)
    {
        var model = Swtor.Formats.Jka.GlmReader.Parse(File.ReadAllBytes(args[2]));
        var skeleton = Swtor.Formats.Jka.GlaSkeleton.Humanoid;
        int seeds = args.Length >= 4 ? int.Parse(args[3]) : 20;
        var hits = new Dictionary<(int Surface, int Vertex), (int Count, float Worst)>();
        for (int seed = 0; seed < seeds; seed++)
        {
            var random = new Random(seed);
            var world = new System.Numerics.Matrix4x4[skeleton.Bones.Count];
            var locals = new System.Numerics.Matrix4x4[world.Length];
            for (int b = 0; b < world.Length; b++)
            {
                var o = skeleton.Bones[b].Origin;
                var axis = System.Numerics.Vector3.Normalize(new System.Numerics.Vector3(random.NextSingle() - .5f, random.NextSingle() - .5f, random.NextSingle() - .5f));
                bool major = Array.IndexOf(new[] { "pelvis", "lower_lumbar", "upper_lumbar", "thoracic", "cervical", "cranium", "lclavical", "rclavical", "lhumerus", "rhumerus", "lradius", "rradius", "lhand", "rhand", "lfemurYZ", "rfemurYZ", "ltibia", "rtibia", "ltalus", "rtalus" }, skeleton.Bones[b].Name) >= 0;
                var local = major ? System.Numerics.Matrix4x4.CreateTranslation(-o) * System.Numerics.Matrix4x4.CreateFromAxisAngle(axis, 0.5f * random.NextSingle()) * System.Numerics.Matrix4x4.CreateTranslation(o) : System.Numerics.Matrix4x4.Identity;
                locals[b] = local;
            }
            System.Numerics.Matrix4x4 WorldOf(int b) => skeleton.Bones[b].Parent >= 0 ? locals[b] * WorldOf(skeleton.Bones[b].Parent) : locals[b];
            for (int b = 0; b < world.Length; b++) world[b] = WorldOf(b);
            for (int i = 0; i < model.Surfaces.Count; i++)
            {
                var g = model.Lods[0][i];
                if ((model.Surfaces[i].Flags & 1) != 0) continue;
                var moved = new System.Numerics.Vector3[g.Vertices.Length];
                for (int v = 0; v < moved.Length; v++)
                    foreach (var w in g.Vertices[v].Weights)
                        moved[v] += w.Weight * System.Numerics.Vector3.Transform(g.Vertices[v].Position, world[w.Bone]);
                for (int t = 0; t < g.Triangles.Length; t += 3)
                    for (int k = 0; k < 3; k++)
                    {
                        int a = g.Triangles[t + k], c = g.Triangles[t + (k + 1) % 3];
                        float before = System.Numerics.Vector3.Distance(g.Vertices[a].Position, g.Vertices[c].Position);
                        float after = System.Numerics.Vector3.Distance(moved[a], moved[c]);
                        if (after < 3 || after < 2.5f * Math.Max(before, 0.05f)) continue;
                        foreach (int v in new[] { a, c })
                        {
                            var old = hits.GetValueOrDefault((i, v));
                            hits[(i, v)] = (old.Count + 1, Math.Max(old.Worst, after));
                        }
                    }
            }
        }
        foreach (var (key, value) in hits.OrderByDescending(h => h.Value.Count).Take(30))
        {
            var v = model.Lods[0][key.Surface].Vertices[key.Vertex];
            Console.WriteLine(FormattableString.Invariant($"[{key.Surface}] {model.Surfaces[key.Surface].Name} v{key.Vertex} in {value.Count} stretched edges (worst {value.Worst:F1}) pos {v.Position.X:F1},{v.Position.Y:F1},{v.Position.Z:F1} w {string.Join(" ", v.Weights.Select(w => $"{skeleton.Bones[w.Bone].Name}:{w.Weight:F2}"))}"));
        }
        return 0;
    }
    // jka degenerate <glm>: lists invalid values, zero-area triangles and unused vertices.
    if (args[1] == "degenerate" && args.Length >= 3)
    {
        var model = Swtor.Formats.Jka.GlmReader.Parse(File.ReadAllBytes(args[2]));
        for (int i = 0; i < model.Surfaces.Count; i++)
        {
            var g = model.Lods[0][i];
            if ((model.Surfaces[i].Flags & 1) != 0) continue;
            int bad = 0, zero = 0, duplicate = 0, badNormal = 0;
            var used = new bool[g.Vertices.Length];
            var seen = new HashSet<(int, int, int)>();
            for (int v = 0; v < g.Vertices.Length; v++)
            {
                var p = g.Vertices[v].Position; var n = g.Vertices[v].Normal;
                if (!float.IsFinite(p.X + p.Y + p.Z) || p.Length() > 200) bad++;
                if (!float.IsFinite(n.X + n.Y + n.Z) || Math.Abs(n.Length() - 1) > 0.1f) badNormal++;
            }
            for (int t = 0; t < g.Triangles.Length; t += 3)
            {
                int a = g.Triangles[t], b = g.Triangles[t + 1], c = g.Triangles[t + 2];
                used[a] = used[b] = used[c] = true;
                var area = System.Numerics.Vector3.Cross(g.Vertices[b].Position - g.Vertices[a].Position, g.Vertices[c].Position - g.Vertices[a].Position).Length();
                if (a == b || b == c || a == c || area < 1e-6f) zero++;
                var key = (Math.Min(a, Math.Min(b, c)), a + b + c, a * b * c);
                if (!seen.Add(key)) duplicate++;
            }
            // The shadow code of the game keeps 32 edges per vertex (MAX_EDGE_DEFS in tr_shadows.cpp): one for each triangle of the vertex.
            var fan = new int[g.Vertices.Length];
            foreach (int index in g.Triangles) fan[index]++;
            for (int v = 0; v < fan.Length; v++)
                if (fan[v] > 32) Console.WriteLine($"[{i}] {model.Surfaces[i].Name} v{v} is in {fan[v]} triangles (limit 32) at {g.Vertices[v].Position}");
            int unused = used.Count(u => !u);
            if (bad + zero + duplicate + badNormal + unused > 0)
                Console.WriteLine($"[{i}] {model.Surfaces[i].Name}: {g.Vertices.Length} verts, bad position {bad}, bad normal {badNormal}, zero-area triangles {zero}, duplicate triangles {duplicate}, unused vertices {unused}");
        }
        return 0;
    }
    // jka reversed <glm>: lists triangles that turn the wrong way (they are culled in the game but still cast shadows).
    if (args[1] == "reversed" && args.Length >= 3)
    {
        var model = Swtor.Formats.Jka.GlmReader.Parse(File.ReadAllBytes(args[2]));
        var skeleton = Swtor.Formats.Jka.GlaSkeleton.Humanoid;
        for (int i = 0; i < model.Surfaces.Count; i++)
        {
            var g = model.Lods[0][i];
            if ((model.Surfaces[i].Flags & 1) != 0) continue;
            for (int t = 0; t < g.Triangles.Length; t += 3)
            {
                var va = g.Vertices[g.Triangles[t]]; var vb = g.Vertices[g.Triangles[t + 1]]; var vc = g.Vertices[g.Triangles[t + 2]];
                if (System.Numerics.Vector3.Dot(System.Numerics.Vector3.Cross(vb.Position - va.Position, vc.Position - va.Position), va.Normal + vb.Normal + vc.Normal) <= 0) continue;
                Console.WriteLine($"[{i}] {model.Surfaces[i].Name} tri {t / 3}");
                foreach (var v in new[] { va, vb, vc })
                    Console.WriteLine(FormattableString.Invariant($"    pos {v.Position.X:F1},{v.Position.Y:F1},{v.Position.Z:F1} uv {v.Uv.X:F2},{v.Uv.Y:F2} w {string.Join(" ", v.Weights.Select(w => $"{skeleton.Bones[w.Bone].Name}:{w.Weight:F2}"))}"));
            }
        }
        return 0;
    }
    // jka reduce <glm> [budget]: reduces the vertices of a finished model in place. The first run keeps the old file as model.glm.full.
    if (args[1] == "reduce" && args.Length >= 3)
    {
        var model = Swtor.Formats.Jka.GlmReader.Parse(File.ReadAllBytes(args[2]));
        int budget = args.Length >= 4 ? int.Parse(args[3]) : Swtor.Formats.Jka.JkaConverter.MaxTotalVertices;
        var (before, after) = Swtor.Formats.Jka.JkaConverter.Reduce(model, budget);
        if (after == before) { Console.WriteLine($"{before} vertices: already within {budget}."); return 0; }
        string backup = args[2] + ".full";
        if (!File.Exists(backup)) File.Copy(args[2], backup);
        File.WriteAllBytes(args[2], Swtor.Formats.Jka.GlmWriter.Write(model));
        Console.WriteLine($"{before} -> {after} vertices (backup: {backup}).");
        return 0;
    }
    // jka edges <glm>: rewrites a model so that no vertex is used by more than 32 triangles (GlmWriter copies such vertices).
    // The first run keeps the old file as model.glm.edges.
    if (args[1] == "edges" && args.Length >= 3)
    {
        var model = Swtor.Formats.Jka.GlmReader.Parse(File.ReadAllBytes(args[2]));
        int changed = 0;
        foreach (var lod in model.Lods)
            for (int i = 0; i < lod.Length; i++)
            {
                var limited = Swtor.Formats.Jka.JkaConverter.LimitTrianglesPerVertex(lod[i]);
                if (ReferenceEquals(limited, lod[i])) continue;
                Console.WriteLine($"{model.Surfaces[i].Name}: {lod[i].Vertices.Length} -> {limited.Vertices.Length} vertices");
                changed++;
            }
        if (changed == 0) { Console.WriteLine("No vertex is used by more than 32 triangles."); return 0; }
        string backup = args[2] + ".edges";
        if (!File.Exists(backup)) File.Copy(args[2], backup);
        File.WriteAllBytes(args[2], Swtor.Formats.Jka.GlmWriter.Write(model));
        Console.WriteLine($"{changed} surfaces fixed (backup: {backup}).");
        return 0;
    }
    // jka skinstats <glm>: for each model_*.skin, the surfaces that it draws and their triangles (a hidden surface still draws its children).
    if (args[1] == "skinstats" && args.Length >= 3)
    {
        var model = Swtor.Formats.Jka.GlmReader.Parse(File.ReadAllBytes(args[2]));
        string folder = Path.GetDirectoryName(Path.GetFullPath(args[2]))!;
        foreach (string file in Directory.GetFiles(folder, "model_*.skin").Order())
        {
            var skin = Swtor.Formats.Jka.SkinFile.Parse(File.ReadAllText(file));
            int surfaces = 0, triangles = 0;
            var textures = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < model.Surfaces.Count; i++)
            {
                var info = model.Surfaces[i];
                string? shader = skin.Get(info.Name);
                bool shown = shader is null ? (info.Flags & 0x3) == 0 : shader != "*off" && (info.Flags & 0x3) == 0;
                if (!shown) continue;
                surfaces++;
                int t = model.Lods[0][i].Triangles.Length / 3;
                triangles += t;
                string key = Path.GetFileNameWithoutExtension(shader ?? info.Shader);
                textures[key] = textures.GetValueOrDefault(key) + t;
            }
            Console.WriteLine($"{Path.GetFileName(file)}: {surfaces} surfaces, {triangles} triangles; " + string.Join(", ", textures.Select(t => $"{t.Key}:{t.Value}")));
        }
        return 0;
    }
    // jka variants <glm>: puts the surfaces of each skin below a variant of their main surface (r_hand -> r_handa, r_handb, ...).
    // The first run keeps the old files as *.variants.
    if (args[1] == "variants" && args.Length >= 3)
    {
        string folder = Path.GetDirectoryName(Path.GetFullPath(args[2]))!;
        foreach (string file in Directory.GetFiles(folder, "model_*.skin").Append(args[2]))
            if (!File.Exists(file + ".variants")) File.Copy(file, file + ".variants");
        var log = Swtor.Assets.JkaExporter.OrganizeVariants(args[2]);
        foreach (string line in log) Console.WriteLine(line);
        Console.WriteLine(log.Count == 0 ? "Nothing to change." : $"{log.Count} changes (backups: *.variants).");
        return 0;
    }
    // jka rename <glm> old=new...: renames surfaces in the model and in every model_*.skin of its folder.
    // The first run keeps the old files as *.rename.
    if (args[1] == "rename" && args.Length >= 4)
    {
        var model = Swtor.Formats.Jka.GlmReader.Parse(File.ReadAllBytes(args[2]));
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string pair in args.Skip(3))
        {
            int eq = pair.IndexOf('=');
            if (eq <= 0) { Console.Error.WriteLine($"Not old=new: {pair}"); return 1; }
            string from = pair[..eq], to = pair[(eq + 1)..];
            if (model.SurfaceIndex(from) < 0) { Console.Error.WriteLine($"No surface {from}."); return 1; }
            if (model.SurfaceIndex(to) >= 0) { Console.Error.WriteLine($"Surface {to} exists already."); return 1; }
            map[from] = to;
        }
        foreach (var surface in model.Surfaces)
            if (map.TryGetValue(surface.Name, out string? to)) surface.Name = to;

        string folder = Path.GetDirectoryName(Path.GetFullPath(args[2]))!;
        var files = new List<string> { args[2] };
        files.AddRange(Directory.GetFiles(folder, "model_*.skin"));
        foreach (string file in files)
            if (!File.Exists(file + ".rename")) File.Copy(file, file + ".rename");
        File.WriteAllBytes(args[2], Swtor.Formats.Jka.GlmWriter.Write(model));
        foreach (string file in files.Skip(1))
        {
            var skin = Swtor.Formats.Jka.SkinFile.Parse(File.ReadAllText(file));
            var renamed = new Swtor.Formats.Jka.SkinFile();
            foreach (var (surface, shader) in skin.Lines) renamed.Set(map.GetValueOrDefault(surface, surface), shader);
            File.WriteAllText(file, renamed.ToString());
        }
        Console.WriteLine($"{map.Count} surfaces renamed in {files.Count} files (backups: *.rename).");
        return 0;
    }
    if (args[1] == "info" && args.Length >= 3)
    {
        var model = Swtor.Formats.Jka.GlmReader.Parse(File.ReadAllBytes(args[2]));
        var bones = args.Length >= 4 ? Swtor.Formats.Jka.GlaSkeleton.Parse(File.ReadAllBytes(args[3])) : null;
        Console.WriteLine($"{model.Name}, anim {model.AnimationName}, {model.BoneCount} bones, {model.Surfaces.Count} surfaces, {model.Lods.Count} LODs");
        for (int i = 0; i < model.Surfaces.Count; i++)
        {
            var s = model.Surfaces[i];
            Console.WriteLine($"[{i}] {s.Name} flags 0x{s.Flags:X} shader '{s.Shader}' idx {s.ShaderIndex} parent {s.Parent} children {string.Join(",", s.Children)}");
            for (int lod = 0; lod < model.Lods.Count; lod++)
            {
                var g = model.Lods[lod][i];
                if (g.Vertices.Length == 0) { Console.WriteLine($"    lod{lod}: empty"); continue; }
                var min = new System.Numerics.Vector3(float.MaxValue); var max = new System.Numerics.Vector3(float.MinValue);
                var usage = new Dictionary<int, float>();
                var counts = new int[5];
                int outward = 0;
                for (int t = 0; t < g.Triangles.Length; t += 3)
                {
                    var va = g.Vertices[g.Triangles[t]]; var vb = g.Vertices[g.Triangles[t + 1]]; var vc = g.Vertices[g.Triangles[t + 2]];
                    if (System.Numerics.Vector3.Dot(System.Numerics.Vector3.Cross(vb.Position - va.Position, vc.Position - va.Position), va.Normal + vb.Normal + vc.Normal) > 0) outward++;
                }
                foreach (var v in g.Vertices)
                {
                    min = System.Numerics.Vector3.Min(min, v.Position); max = System.Numerics.Vector3.Max(max, v.Position);
                    counts[v.Weights.Length]++;
                    foreach (var w in v.Weights) usage[w.Bone] = usage.GetValueOrDefault(w.Bone) + w.Weight;
                }
                string top = string.Join(", ", usage.OrderByDescending(u => u.Value).Take(4).Select(u => $"{bones?.Bones[u.Key].Name ?? u.Key.ToString()}:{u.Value:F0}"));
                Console.WriteLine($"    lod{lod}: {g.Vertices.Length} verts, {g.Triangles.Length / 3} tris (ccw {outward}, {g.Triangles.Length / 3 - outward} cw), bounds {min:F1} .. {max:F1}, weights/vert 1:{counts[1]} 2:{counts[2]} 3:{counts[3]} 4:{counts[4]}, {usage.Count} bones; {top}");
            }
        }
        return 0;
    }
    // jka skeleton-data <file.gla> <out.json>: writes the bone table that is embedded as Jka/jka_humanoid.json.
    if (args[1] == "skeleton-data" && args.Length >= 4)
    {
        var gla = Swtor.Formats.Jka.GlaSkeleton.Parse(File.ReadAllBytes(args[2]));
        File.WriteAllText(args[3], gla.ToJson());
        Console.WriteLine($"{gla.Bones.Count} bones -> {args[3]}");
        return 0;
    }

    // jka template <players folder> <out.json> [sample model folder names...]: builds the template (root and tag surfaces) by vote
    // from sample models. Without names, every model of the folder is used; "jaesa" comes first so it wins the ties.
    if (args[1] == "template" && args.Length >= 4)
    {
        var names = args.Length > 4 ? args.Skip(4) : Directory.GetDirectories(args[2]).Select(Path.GetFileName).Where(n => !n!.StartsWith('_')).OrderBy(n => n != "jaesa").ThenBy(n => n)!;
        var sources = new List<(Swtor.Formats.Jka.GlmModel, Swtor.Formats.Jka.GlaSkeleton)>();
        foreach (string name in names)
        {
            string file = Path.Combine(args[2], name, "model.glm");
            if (!File.Exists(file)) continue;
            var glm = Swtor.Formats.Jka.GlmReader.Parse(File.ReadAllBytes(file));
            try { sources.Add((glm, LoadSampleSkeleton(glm.AnimationName, args[2]))); Console.WriteLine($"  {name}: {glm.Surfaces.Count} surfaces, {glm.AnimationName}"); }
            catch (Exception e) when (e is IOException or Swtor.Formats.GameFormatException) { Console.WriteLine($"  {name}: skipped ({e.Message})"); }
        }
        var template = Swtor.Formats.Jka.JkaTemplate.FromModels(sources);
        File.WriteAllText(args[3], template.ToJson());
        Console.WriteLine($"{template.Surfaces.Count} surfaces from {sources.Count} models, skeleton {template.AnimationName} -> {args[3]}");
        return 0;
    }

    // jka export <players folder> <name>: exports the default bare human male (head, hair and bare body) as a new model.
    if (args[1] == "export" && args.Length >= 4)
    {
        var index = Swtor.Assets.AssetIndex.Load(DefaultRoot());
        var parts = new List<Swtor.Assets.JkaExportPart>();
        string? bodytype = null;
        foreach (var (slot, art) in new[] { ("head", "head_human_bma_caucasian_a01"), ("hair", "hair_human_non_a01_m"), ("chest", "chest_naked_caucasian_young_a01"), ("hand", "hand_naked_caucasian_young_a01"), ("leg", "leg_naked_caucasian_young_a01"), ("boot", "boot_naked_caucasian_young_a01") })
        {
            var asset = index.Appearances.FindAsset(slot, art);
            if (asset is null) { Console.WriteLine($"asset {art} not found"); continue; }
            var part = asset.Materials.Select(m => Swtor.Assets.PartResolver.ResolveAsset(index, asset, m.Id, (char)109, bodytype ?? "bma")).FirstOrDefault(p => p?.DiffusePath is not null) ?? Swtor.Assets.PartResolver.ResolveAsset(index, asset, null, (char)109, bodytype ?? "bma");
            if (part is null) { Console.WriteLine($"no model for {art}"); continue; }
            bodytype ??= part.Bodytype;
            Swtor.Formats.Dds.DdsImage? Load(string? path) => path is null ? null : Swtor.Formats.Dds.DdsReader.Decode(File.ReadAllBytes(index.FullPath(path)));
            var eyes = part.Overrides is { } o && (o.TryGetValue(1, out var e1) ? e1 : o.TryGetValue(-1, out var e2) ? e2 : null) is { } eye ? Load(eye.DiffusePath) : null;
            parts.Add(new Swtor.Assets.JkaExportPart
            {
                Slot = slot, Model = Gr2Reader.Parse(File.ReadAllBytes(index.FullPath(part.ModelPath))),
                Attachments = part.Attachments.Select(a => Gr2Reader.Parse(File.ReadAllBytes(index.FullPath(a)))).ToList(),
                Texture = Load(part.DiffusePath), EyeTexture = slot == "head" ? eyes : null, Bodytype = part.Bodytype,
            });
        }
        var result = Swtor.Assets.JkaExporter.CreateNew(index, parts, args[3], new Swtor.Assets.JkaExportOptions { PlayersFolder = args[2] });
        Console.WriteLine($"{result.ModelPath}: {string.Join(", ", result.Surfaces)}");
        foreach (string note in result.Notes) Console.WriteLine(note);
        return 0;
    }

    // jka roundtrip <file.glm...>: reads each file, writes it again and compares the bytes.
    if (args[1] == "roundtrip" && args.Length >= 3)
    {
        foreach (string file in args.Skip(2))
        {
            var original = File.ReadAllBytes(file);
            var written = Swtor.Formats.Jka.GlmWriter.Write(Swtor.Formats.Jka.GlmReader.Parse(original));
            int diff = -1;
            for (int i = 0; i < Math.Min(original.Length, written.Length); i++) if (original[i] != written[i]) { diff = i; break; }
            Console.WriteLine($"{Path.GetFileName(Path.GetDirectoryName(file))}: {original.Length} -> {written.Length} bytes, " + (diff < 0 && original.Length == written.Length ? "identical" : $"first difference at {(diff < 0 ? Math.Min(original.Length, written.Length) : diff)}"));
            if (diff >= 0 || original.Length != written.Length)
            {
                var a = Swtor.Formats.Jka.GlmReader.Parse(original);
                var b = Swtor.Formats.Jka.GlmReader.Parse(written);
                int bad = 0;
                for (int s = 0; s < a.Surfaces.Count && bad < 5; s++)
                    for (int lod = 0; lod < a.Lods.Count; lod++)
                    {
                        var x = a.Lods[lod][s]; var y = b.Lods[lod][s];
                        string problem = null!;
                        if (x.Vertices.Length != y.Vertices.Length || !x.Triangles.SequenceEqual(y.Triangles)) problem = "geometry";
                        else
                            for (int v = 0; v < x.Vertices.Length && problem is null; v++)
                            {
                                var p = x.Vertices[v]; var q = y.Vertices[v];
                                if (p.Position != q.Position || p.Normal != q.Normal || p.Uv != q.Uv) problem = $"vertex {v} data";
                                else if (p.Weights.Length != q.Weights.Length) problem = $"vertex {v} weight count {p.Weights.Length} vs {q.Weights.Length}";
                                else for (int k = 0; k < p.Weights.Length; k++)
                                    if (p.Weights[k].Bone != q.Weights[k].Bone || Math.Abs(p.Weights[k].Weight - q.Weights[k].Weight) > 0.002f) { problem = $"vertex {v} weight {k}: {p.Weights[k]} vs {q.Weights[k]}"; break; }
                            }
                        if (problem is not null) { Console.WriteLine($"   surface {s} {a.Surfaces[s].Name} lod {lod}: {problem}"); bad++; }
                    }
                Console.WriteLine(bad == 0 ? "   same content (only the byte layout differs)" : "   CONTENT DIFFERS");
            }
        }
        return 0;
    }

    // jka skeleton <file.gr2>: lists the bones of a SWTOR skeleton with both matrices (translation row).
    if (args[1] == "skeleton" && args.Length >= 3)
    {
        var skeleton = Swtor.Formats.Gr2.Gr2Skeleton.Parse(File.ReadAllBytes(args[2]));
        for (int i = 0; i < skeleton.Bones.Count; i++)
        {
            var b = skeleton.Bones[i];
            string parentName = b.Parent >= 0 ? skeleton.Bones[b.Parent].Name : "-";
            var check = b.World * b.InverseBind;
            float error = Math.Abs(check.M11 - 1) + Math.Abs(check.M22 - 1) + Math.Abs(check.M33 - 1) + Math.Abs(check.M41) + Math.Abs(check.M42) + Math.Abs(check.M43);
            Console.WriteLine($"{i,3} {b.Name,-24} parent {parentName,-20} world {b.World.M41,8:F4} {b.World.M42,8:F4} {b.World.M43,8:F4}  x-axis {b.World.M11,6:F2} {b.World.M12,6:F2} {b.World.M13,6:F2}  err {error:E1}");
        }
        return 0;
    }

    // jka joints <gr2 files...>: for pairs of bones, prints the center of the vertices that both bones move.
    // That center is close to the joint between the two bones.
    if (args[1] == "joints" && args.Length >= 3)
    {
        string[][] pairs =
        [
            ["Pelvis", "LowerBack"], ["LowerBack", "Chest"], ["Chest", "Chest1"], ["Chest1", "Chest2"], ["Chest2", "Neck"], ["Neck", "Neck1"], ["Neck1", "Head"],
            ["Chest2", "LeftCollar"], ["LeftCollar", "LeftShoulder"], ["LeftShoulder", "LeftShoulderTwist1"], ["LeftShoulder", "LeftElbow"], ["LeftShoulderTwist1", "LeftElbow"],
            ["LeftElbow", "LeftUlna"], ["LeftElbow", "LeftWrist"], ["LeftUlna", "LeftWrist"],
            ["Pelvis", "LeftHip"], ["LeftHip", "LeftHipTwist1"], ["LeftHip", "LeftKnee"], ["LeftHipTwist1", "LeftKnee"], ["LeftKnee", "LeftAnkle"], ["LeftAnkle", "LeftToe"],
        ];
        var sums = pairs.Select(_ => (Sum: System.Numerics.Vector3.Zero, W: 0f)).ToArray();
        foreach (string file in args.Skip(2))
            foreach (var mesh in Gr2Reader.Parse(File.ReadAllBytes(file)).Meshes)
            {
                if (mesh.BoneWeights is null || mesh.BoneIndices is null) continue;
                var weightOf = new Dictionary<string, float>();
                for (int v = 0; v < mesh.VertexCount; v++)
                {
                    weightOf.Clear();
                    for (int k = 0; k < 4; k++)
                    {
                        float w = k switch { 0 => mesh.BoneWeights[v].X, 1 => mesh.BoneWeights[v].Y, 2 => mesh.BoneWeights[v].Z, _ => mesh.BoneWeights[v].W };
                        weightOf[mesh.Bones[mesh.BoneIndices[v * 4 + k]].Name] = weightOf.GetValueOrDefault(mesh.Bones[mesh.BoneIndices[v * 4 + k]].Name) + w;
                    }
                    for (int p = 0; p < pairs.Length; p++)
                    {
                        float w = Math.Min(weightOf.GetValueOrDefault(pairs[p][0]), weightOf.GetValueOrDefault(pairs[p][1]));
                        if (w > 0) sums[p] = (sums[p].Sum + mesh.Positions[v] * w, sums[p].W + w);
                    }
                }
            }
        for (int p = 0; p < pairs.Length; p++)
        {
            var c = sums[p].W > 0 ? sums[p].Sum / sums[p].W : default;
            Console.WriteLine($"{pairs[p][0],-20} {pairs[p][1],-20} mass {sums[p].W,7:F2}  {c.X,9:F5} {c.Y,9:F5} {c.Z,9:F5}");
        }
        return 0;
    }

    // jka measure <gr2 files...>: prints, for each bone, the weighted center of its vertices (SWTOR units).
    if (args[1] == "measure" && args.Length >= 3)
    {
        var sum = new Dictionary<string, (System.Numerics.Vector3 Sum, float Weight, System.Numerics.Vector3 Min, System.Numerics.Vector3 Max)>();
        foreach (string file in args.Skip(2))
        {
            foreach (var mesh in Gr2Reader.Parse(File.ReadAllBytes(file)).Meshes)
            {
                if (mesh.BoneWeights is null || mesh.BoneIndices is null) continue;
                for (int v = 0; v < mesh.VertexCount; v++)
                    for (int k = 0; k < 4; k++)
                    {
                        float w = k switch { 0 => mesh.BoneWeights[v].X, 1 => mesh.BoneWeights[v].Y, 2 => mesh.BoneWeights[v].Z, _ => mesh.BoneWeights[v].W };
                        if (w <= 0.001f) continue;
                        string name = mesh.Bones[mesh.BoneIndices[v * 4 + k]].Name;
                        var e = sum.TryGetValue(name, out var old) ? old : (default, 0f, new System.Numerics.Vector3(float.MaxValue), new System.Numerics.Vector3(float.MinValue));
                        sum[name] = (e.Sum + mesh.Positions[v] * w, e.Weight + w, System.Numerics.Vector3.Min(e.Min, mesh.Positions[v]), System.Numerics.Vector3.Max(e.Max, mesh.Positions[v]));
                    }
            }
        }
        foreach (var (name, e) in sum.OrderBy(x => x.Key))
        {
            var c = e.Sum / e.Weight;
            Console.WriteLine($"{name,-24} w {e.Weight,8:F1}  center {c.X,9:F5} {c.Y,9:F5} {c.Z,9:F5}  min {e.Min.X,8:F4} {e.Min.Y,8:F4} {e.Min.Z,8:F4}  max {e.Max.X,8:F4} {e.Max.Y,8:F4} {e.Max.Z,8:F4}");
        }
        return 0;
    }
    return 1;
}

static string DefaultRoot() => Environment.GetEnvironmentVariable("SWTOR_ASSETS") ?? @"C:\jka_tor_assets\resources";

// dds survey: reads every texture (header, then full decode of every 50th file) and counts formats.
// dds decode <file> <out.ppm>: writes the image as a PPM file, to view it.
static int DdsCommand(string[] args)
{
    // dds dyecheck <slot> <art name> [materials to try]: applies the default color schemes of each material to the item with the game formula
    // and compares the average color of the dyed areas with the diffuse texture (the artist's preview). Prints one line per scheme.
    if (args[1] == "dyecheck" && args.Length >= 4)
    {
        var idx = Swtor.Assets.AssetIndex.Load(DefaultRoot());
        var asset = idx.Appearances.FindAsset(args[2], args[3]);
        if (asset is null) { Console.WriteLine("asset not found"); return 1; }
        foreach (var material in asset.Materials.Take(args.Length >= 5 ? int.Parse(args[4]) : 3))
        {
            var part = Swtor.Assets.PartResolver.ResolveAsset(idx, asset, material.Id, 'm', "bma");
            if (part?.DiffusePath is null || part.MaskPath is null || part.PaletteMapPath is null) { Console.WriteLine($"{material.Name}: no mask or palette map"); continue; }
            var diff = Swtor.Formats.Dds.DdsReader.Decode(File.ReadAllBytes(idx.FullPath(part.DiffusePath)));
            var mask = Swtor.Formats.Dds.DdsReader.Decode(File.ReadAllBytes(idx.FullPath(part.MaskPath)));
            var map = Swtor.Formats.Dds.DdsReader.Decode(File.ReadAllBytes(idx.FullPath(part.PaletteMapPath)));
            foreach (string schemeId in material.ColorSchemeIds.Take(4))
            {
                var scheme = idx.Colors.FindScheme(schemeId);
                if (scheme is null || !scheme.Slots.TryGetValue(args[2], out var pair)) continue;
                var p1 = idx.Colors.ReadPalette(pair.Primary); var p2 = idx.Colors.ReadPalette(pair.Secondary);
                var dyed = Swtor.Formats.Dds.PaletteTint.Apply(diff, mask, p1, p2, map);
                var avgGame = new double[3]; var avgDiff = new double[3]; long count = 0;
                for (int i = 0; i < diff.Width * diff.Height; i++)
                {
                    int mi = ((i / diff.Width) * mask.Height / diff.Height * mask.Width + (i % diff.Width) * mask.Width / diff.Width) * 4;
                    if (mask.Rgba[mi] <= 128 && mask.Rgba[mi + 1] <= 128) continue;
                    count++;
                    for (int c = 0; c < 3; c++) { avgGame[c] += dyed.Rgba[i * 4 + c]; avgDiff[c] += diff.Rgba[i * 4 + c]; }
                }
                Console.WriteLine($"{material.Name} / {scheme.Name}: formula ({avgGame[0] / count:F0},{avgGame[1] / count:F0},{avgGame[2] / count:F0}) vs diffuse ({avgDiff[0] / count:F0},{avgDiff[1] / count:F0},{avgDiff[2] / count:F0})  palettes {pair.Primary}/{pair.Secondary}");
            }
        }
        return 0;
    }
    // dds dyefind <slot> <art name> <material id> <bodytype> <target.tga>: finds the palettes that give the colors of an exported texture.
    // Each palette is applied to both mask areas; the mean color error is measured per area (primary: red mask, secondary: green).
    // Prints the 5 best palettes of each area. Used to rebuild a lost character from its Jedi Academy export.
    if (args[1] == "dyefind" && args.Length >= 7)
    {
        var idx = Swtor.Assets.AssetIndex.Load(DefaultRoot());
        var asset = idx.Appearances.FindAsset(args[2], args[3]);
        if (asset is null) { Console.WriteLine("asset not found"); return 1; }
        string bt = args[5];
        var part = Swtor.Assets.PartResolver.ResolveAsset(idx, asset, args[4], bt[1], bt);
        if (part?.DiffusePath is null || part.MaskPath is null) { Console.WriteLine("no diffuse or mask"); return 1; }
        var diff = Swtor.Formats.Dds.DdsReader.Decode(File.ReadAllBytes(idx.FullPath(part.DiffusePath)));
        var mask = Swtor.Formats.Dds.DdsReader.Decode(File.ReadAllBytes(idx.FullPath(part.MaskPath)));
        // A 8th argument "nomap" uses the approximation without palette map (older exports).
        var map = part.PaletteMapPath is null || (args.Length >= 8 && args[7] == "nomap") ? null : Swtor.Formats.Dds.DdsReader.Decode(File.ReadAllBytes(idx.FullPath(part.PaletteMapPath)));
        var target = Swtor.Formats.Dds.TgaReader.Decode(File.ReadAllBytes(args[6]));

        // Pixels of the target in each area (nearest source pixel), on a grid of at most 256x256.
        int step = Math.Max(1, target.Width / 256);
        var areas = new List<(int Target, int Source)>[2] { [], [] };
        for (int y = 0; y < target.Height; y += step)
            for (int x = 0; x < target.Width; x += step)
            {
                int sx = x * diff.Width / target.Width, sy = y * diff.Height / target.Height;
                int mi = ((sy * mask.Height / diff.Height) * mask.Width + sx * mask.Width / diff.Width) * 4;
                int r = mask.Rgba[mi], g = mask.Rgba[mi + 1];
                if (Math.Max(r, g) <= 128) continue;
                areas[g > r ? 1 : 0].Add(((y * target.Width + x) * 4, (sy * diff.Width + sx) * 4));
            }
        Console.WriteLine($"{areas[0].Count} primary and {areas[1].Count} secondary sample pixels");

        var scores = new List<(double Error, string Id)>[2] { [], [] };
        for (int a = 0; a < 2; a++)
        {
            double error = 0;
            foreach (var (t, s) in areas[a])
                for (int c = 0; c < 3; c++) error += Math.Abs(diff.Rgba[s + c] - target.Rgba[t + c]);
            Console.WriteLine($"{(a == 0 ? "primary" : "secondary")} area, undyed diffuse: mean error {error / Math.Max(1, areas[a].Count) / 3:F1}");
        }
        foreach (var entry in idx.Colors.Palettes)
        {
            var palette = idx.Colors.ReadPalette(entry.Id);
            if (palette is null) continue;
            var dyed = Swtor.Formats.Dds.PaletteTint.Apply(diff, mask, palette, palette, map);
            for (int a = 0; a < 2; a++)
            {
                if (areas[a].Count == 0) continue;
                double error = 0;
                foreach (var (t, s) in areas[a])
                    for (int c = 0; c < 3; c++) error += Math.Abs(dyed.Rgba[s + c] - target.Rgba[t + c]);
                scores[a].Add((error / areas[a].Count / 3, entry.Id));
            }
        }
        for (int a = 0; a < 2; a++)
        {
            Console.WriteLine(a == 0 ? "primary:" : "secondary:");
            foreach (var (error, id) in scores[a].OrderBy(s => s.Error).Take(5))
                Console.WriteLine($"   {id}  {idx.Colors.FindPaletteEntry(id)?.Name}  mean error {error:F1}");
        }
        return 0;
    }
    // dds stats <file.dds> [mask.dds]: per channel minimum, mean and maximum (0 to 255). With a mask, only where its red (then green) channel is above 128.
    if (args[1] == "stats" && args.Length >= 3)
    {
        var img = Swtor.Formats.Dds.DdsReader.Decode(File.ReadAllBytes(args[2]));
        var msk = args.Length >= 4 ? Swtor.Formats.Dds.DdsReader.Decode(File.ReadAllBytes(args[3])) : null;
        foreach (int area in msk is null ? new[] { -1 } : new[] { 0, 1 })
        {
            var min = new int[4]; var max = new int[4]; var sum = new double[4]; long count = 0;
            Array.Fill(min, 255);
            for (int y = 0; y < img.Height; y++)
                for (int x = 0; x < img.Width; x++)
                {
                    if (msk is not null && msk.Rgba[((y * msk.Height / img.Height) * msk.Width + x * msk.Width / img.Width) * 4 + area] <= 128) continue;
                    count++;
                    for (int c = 0; c < 4; c++) { int v = img.Rgba[(y * img.Width + x) * 4 + c]; min[c] = Math.Min(min[c], v); max[c] = Math.Max(max[c], v); sum[c] += v; }
                }
            Console.WriteLine($"{(area < 0 ? "all" : area == 0 ? "primary" : "secondary")}: {count} pixels of {img.Width}x{img.Height}");
            if (count > 0) for (int c = 0; c < 4; c++) Console.WriteLine($"   {"RGBA"[c]}: min {min[c]} mean {sum[c] / count:F0} max {max[c]}");
        }
        return 0;
    }
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

    // appearance asset <slot> <art name> [gender m|f] [bodytype]: prints the files of one asset.
    if (args[1] == "asset" && args.Length >= 4)
    {
        var found = catalog.FindAsset(args[2], args[3]);
        if (found is null) { Console.WriteLine("not found"); return 1; }
        var part = Swtor.Assets.PartResolver.ResolveAsset(index, found, null, args.Length >= 5 ? args[4][0] : 'm', args.Length >= 6 ? args[5] : null);
        Console.WriteLine(part is null ? "no model" : $"{part.ModelPath}  diffuse {part.DiffusePath}  mask {part.MaskPath}  body {part.Bodytype}  attachments {string.Join(",", part.Attachments)}");
        return 0;
    }

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

    // gom grep <text> [name prefix]: lists the objects that have a string value or a number that contains the text.
    if (args[1] == "grep" && args.Length >= 3)
    {
        var gdb = Swtor.Assets.GomDatabase.Open(DefaultRoot());
        string prefix = args.Length >= 4 ? args[3] : "";
        bool Has(object? v) => v switch
        {
            string str => str.Contains(args[2], StringComparison.OrdinalIgnoreCase),
            long n => n.ToString().Contains(args[2]),
            ulong u => u.ToString().Contains(args[2]),
            Swtor.Formats.Gom.GomObject o => o.Fields.Any(f => Has(f.Value)),
            Swtor.Formats.Gom.GomList l => l.Items.Any(Has),
            Swtor.Formats.Gom.GomMap m => m.Entries.Any(e => Has(e.Key) || Has(e.Value)),
            _ => false,
        };
        var found = new System.Collections.Concurrent.ConcurrentBag<string>();
        gdb.ForEachNode(e => e.Name.StartsWith(prefix, StringComparison.Ordinal), (e, node) => { if (Has(node.Object)) found.Add(e.Name); });
        foreach (string name in found.OrderBy(n => n).Take(60)) Console.WriteLine(name);
        Console.WriteLine($"{found.Count} objects");
        return 0;
    }

    if (args.Length >= 3 && args[1] is "find" or "dump")
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var db = Swtor.Assets.GomDatabase.Open(DefaultRoot());
        Console.WriteLine($"{db.Entries.Count} objects opened in {sw.Elapsed}");
        if (args[1] == "find")
        {
            foreach (var e in db.Entries.Where(e => e.Name.Contains(args[2], StringComparison.OrdinalIgnoreCase)).Take(args.Length >= 4 ? int.Parse(args[3]) : 50))
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

