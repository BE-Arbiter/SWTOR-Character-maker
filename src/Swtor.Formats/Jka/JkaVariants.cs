namespace Swtor.Formats.Jka;

/// <summary>
/// Puts the surfaces of each skin below a variant of their main surface, like the models of the game. In <c>jedi_hf</c>,
/// <c>torso_off</c> is hidden and has the children <c>torsoa</c> ... <c>torsog</c>, one for each outfit, and the content of
/// an outfit hangs below its variant (<c>torsoa_belt</c>). The game needs this: when a skin turns <c>r_hand</c> off, it looks for
/// <c>r_handa</c> ... <c>r_handh</c> (<c>G_GetRootSurfNameWithVariant</c>). If none is shown, it thinks the hand was cut off and
/// does not draw the saber. Dismemberment cuts the variant and its children.
/// </summary>
public static class JkaVariants
{
    /// <summary>The game looks for 8 variants of a main surface (letters a to h).</summary>
    public const int GameVariants = 8;

    private const string Off = "*off";

    /// <summary>
    /// Organizes the model and its skins. For each main surface (<see cref="JkaModelBuilder.MainSurfaces"/>) that a skin hides:
    /// the main surface becomes a hidden surface of one triangle; each skin gets a variant <c>&lt;main&gt;&lt;letter&gt;</c>
    /// below it (the default skin gets <c>a</c>), shown in this skin only; the surfaces of the region that only one skin shows
    /// go below the variant of this skin. A main surface that every skin shows is not changed.
    /// The variant takes the geometry of the main surface (copied when two skins show it), else an existing variant of the
    /// skin (<c>r_handa</c>, <c>r_hand_2</c>), else a triangle of no area. Running it again changes nothing.
    /// At the end, the surfaces are put in hierarchy order (each parent before its children).
    /// </summary>
    /// <param name="skins">Skin name ("default", "officer", ...) and content, oldest first: this order gives the letters.
    /// The skins are changed in place.</param>
    /// <returns>One line for each change, for the log.</returns>
    public static List<string> Organize(GlmModel model, IReadOnlyList<(string Name, SkinFile Skin)> skins)
    {
        var log = new List<string>();
        if (skins.Count == 0) return log;
        foreach (string main in JkaModelBuilder.MainSurfaces)
        {
            int m = model.SurfaceIndex(main);
            if (m >= 0) OrganizeMain(model, m, skins, log);
        }
        Reorder(model);
        return log;
    }

    private static void OrganizeMain(GlmModel model, int m, IReadOnlyList<(string Name, SkinFile Skin)> skins, List<string> log)
    {
        string main = model.Surfaces[m].Name;
        var region = Region(model, m);
        var shownBy = new Dictionary<int, List<int>>();
        foreach (int x in region.Prepend(m)) shownBy[x] = Enumerable.Range(0, skins.Count).Where(s => Shown(model, x, skins[s].Skin)).ToList();
        bool Exclusive(int x, int s) => shownBy[x].Count == 1 && shownBy[x][0] == s;
        if (shownBy[m].Count == skins.Count) return;
        if (shownBy[m].Count == 0 && !region.Any(x => shownBy[x].Count > 0 && shownBy[x].Count < skins.Count)) return;

        // The default skin gets "a", the other skins follow the order of the list. All main surfaces use the same order, so a skin
        // has the same letter everywhere (torsob, r_armb, r_handb), and a second run gives the same letters.
        var order = Enumerable.Range(0, skins.Count)
            .OrderBy(s => skins[s].Name.Equals("default", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(s => s).ToList();
        if (order.Count > GameVariants)
            log.Add($"{main}: {order.Count} skins, but the game only finds {GameVariants} variants. Skins after the 8th lose the saber.");

        // Choose the surface that becomes the variant of each skin.
        var taken = new HashSet<int>();
        var source = new int[skins.Count];
        foreach (int s in order)
        {
            source[s] = -1;
            if (shownBy[m].Contains(s)) continue; // A copy of the main surface, made below.
            var candidates = model.Surfaces[m].Children
                .Where(x => region.Contains(x) && !taken.Contains(x) && shownBy[x].Contains(s) && IsVariantName(main, model.Surfaces[x].Name))
                .OrderBy(x => Exclusive(x, s) ? 0 : 1).ThenBy(x => x);
            foreach (int x in candidates) { source[s] = x; taken.Add(x); break; }
        }

        var oldNames = source.Select(x => x >= 0 ? model.Surfaces[x].Name : "").ToArray();
        // Free the final names first: an existing variant can get the letter of another one.
        foreach (int s in order)
            if (source[s] >= 0) Rename(model, skins, source[s], $"__variant_{source[s]}");

        var variants = new int[skins.Count];
        for (int k = 0; k < order.Count; k++)
        {
            int s = order[k];
            string name = main + (char)('a' + k);
            if (model.SurfaceIndex(name) >= 0) Rename(model, skins, model.SurfaceIndex(name), $"{name}_old");
            string? shader;
            if (source[s] >= 0)
            {
                variants[s] = source[s];
                shader = null;
                Rename(model, skins, source[s], name);
                if (!oldNames[s].Equals(name, StringComparison.OrdinalIgnoreCase)) log.Add($"{oldNames[s]} -> {name}: variant of {main} for the skin {skins[s].Name}.");
            }
            else
            {
                bool copy = shownBy[m].Contains(s);
                shader = (copy ? ShaderIn(model, m, skins[s].Skin) : region.Where(x => Exclusive(x, s)).Select(x => ShaderIn(model, x, skins[s].Skin)).FirstOrDefault(sh => sh is not null))
                    ?? skins[s].Skin.Lines.Select(l => l.Shader).FirstOrDefault(sh => sh != Off) ?? model.Surfaces[m].Shader;
                variants[s] = AddSurface(model, new GlmSurfaceInfo { Name = name, Shader = model.Surfaces[m].Shader, Parent = m },
                    lod => copy ? model.Lods[lod][m] : Placeholder(model.Lods[lod][m]));
                log.Add($"{name}: variant of {main} for the skin {skins[s].Name} ({(copy ? "geometry of " + main : "empty")}).");
            }
            SetParent(model, variants[s], m);
            for (int t = 0; t < skins.Count; t++)
                if (t != s) skins[t].Skin.Set(name, Off);
                else if (shader is not null) skins[t].Skin.Set(name, shader);
        }

        // The content of one skin goes below its variant. Shared content stays below the main surface.
        foreach (int x in region)
        {
            if (variants.Contains(x)) continue;
            int owner = shownBy[x].Count == 1 ? shownBy[x][0] : -1;
            int parent = owner >= 0 ? variants[owner] : m;
            if (model.Surfaces[x].Parent != parent) log.Add($"{model.Surfaces[x].Name}: moved below {model.Surfaces[parent].Name}.");
            SetParent(model, x, parent);
        }

        // The main surface is hidden in the model, like "torso_off" in the game models. Its children are still drawn.
        var info = model.Surfaces[m];
        if ((info.Flags & GlmSurfaceInfo.FlagOff) == 0 || model.Lods[0][m].Triangles.Length != 3)
            for (int lod = 0; lod < model.Lods.Count; lod++) model.Lods[lod][m] = Placeholder(model.Lods[lod][m]);
        info.Flags |= GlmSurfaceInfo.FlagOff;
        foreach (var (_, skin) in skins) skin.Set(main, Off);
    }

    // The surfaces below a main surface, without tags and other main surfaces (and what hangs below them).
    private static List<int> Region(GlmModel model, int m)
    {
        var result = new List<int>();
        var stack = new Stack<int>(model.Surfaces[m].Children);
        while (stack.Count > 0)
        {
            int x = stack.Pop();
            var info = model.Surfaces[x];
            if ((info.Flags & GlmSurfaceInfo.FlagBolt) != 0 || JkaModelBuilder.MainSurfaces.Contains(info.Name, StringComparer.OrdinalIgnoreCase)) continue;
            result.Add(x);
            foreach (int c in info.Children) stack.Push(c);
        }
        result.Sort();
        return result;
    }

    // "r_handa" or "r_hand_2" for "r_hand".
    private static bool IsVariantName(string main, string name)
    {
        if (!name.StartsWith(main, StringComparison.OrdinalIgnoreCase) || name.Length == main.Length) return false;
        string rest = name[main.Length..];
        return (rest.Length == 1 && char.IsAsciiLetterLower(char.ToLowerInvariant(rest[0])))
            || (rest.Length > 1 && rest[0] == '_' && rest.AsSpan(1).IndexOfAnyExceptInRange('0', '9') < 0);
    }

    // A skin line shows the surface unless it is "*off". Without a line, the flags of the model decide.
    private static bool Shown(GlmModel model, int x, SkinFile skin)
    {
        string? shader = skin.Get(model.Surfaces[x].Name);
        return shader is null ? (model.Surfaces[x].Flags & GlmSurfaceInfo.FlagOff) == 0 : shader != Off;
    }

    private static string? ShaderIn(GlmModel model, int x, SkinFile skin)
    {
        string? shader = skin.Get(model.Surfaces[x].Name);
        return shader is null || shader == Off ? null : shader;
    }

    private static void Rename(GlmModel model, IReadOnlyList<(string Name, SkinFile Skin)> skins, int x, string name)
    {
        string old = model.Surfaces[x].Name;
        model.Surfaces[x].Name = name;
        foreach (var (_, skin) in skins) skin.Rename(old, name);
    }

    // One triangle of no area at the first vertex: the surface keeps a position and a bone but draws nothing.
    private static GlmSurface Placeholder(GlmSurface from)
    {
        var v = from.Vertices[0];
        var vertices = Enumerable.Range(0, 3).Select(_ => new GlmVertex { Position = v.Position, Normal = v.Normal, Uv = v.Uv, Weights = v.Weights }).ToArray();
        return new GlmSurface { Vertices = vertices, Triangles = [0, 1, 2] };
    }

    private static int AddSurface(GlmModel model, GlmSurfaceInfo info, Func<int, GlmSurface> geometry)
    {
        model.Surfaces.Add(info);
        int index = model.Surfaces.Count - 1;
        for (int lod = 0; lod < model.Lods.Count; lod++) model.Lods[lod] = [.. model.Lods[lod], geometry(lod)];
        model.Surfaces[info.Parent].Children.Add(index);
        return index;
    }

    private static void SetParent(GlmModel model, int x, int parent)
    {
        int old = model.Surfaces[x].Parent;
        if (old == parent) return;
        if (old >= 0) model.Surfaces[old].Children.Remove(x);
        model.Surfaces[x].Parent = parent;
        model.Surfaces[parent].Children.Add(x);
    }

    // Puts the surfaces in depth-first order from the roots, so that a parent comes before its children.
    private static void Reorder(GlmModel model)
    {
        var order = new List<int>();
        void Visit(int x)
        {
            order.Add(x);
            foreach (int c in model.Surfaces[x].Children) Visit(c);
        }
        for (int i = 0; i < model.Surfaces.Count; i++)
            if (model.Surfaces[i].Parent < 0) Visit(i);
        if (order.Count != model.Surfaces.Count) throw new InvalidOperationException("The surface hierarchy has a cycle or a lost surface.");
        if (order.Select((x, i) => x == i).All(b => b)) return;

        var newIndex = new int[order.Count];
        for (int i = 0; i < order.Count; i++) newIndex[order[i]] = i;
        var surfaces = order.Select(x => model.Surfaces[x]).ToList();
        foreach (var s in surfaces)
        {
            if (s.Parent >= 0) s.Parent = newIndex[s.Parent];
            for (int c = 0; c < s.Children.Count; c++) s.Children[c] = newIndex[s.Children[c]];
        }
        model.Surfaces.Clear();
        model.Surfaces.AddRange(surfaces);
        for (int lod = 0; lod < model.Lods.Count; lod++)
        {
            var old = model.Lods[lod];
            model.Lods[lod] = order.Select(x => old[x]).ToArray();
        }
    }
}
