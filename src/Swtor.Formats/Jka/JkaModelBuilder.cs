using System.Numerics;

namespace Swtor.Formats.Jka;

/// <summary>What <see cref="JkaModelBuilder.MergeSurfaces"/> did.</summary>
public sealed class JkaMergeResult
{
    /// <summary>The drafts that were added, with their final names.</summary>
    public List<JkaSurfaceDraft> Added { get; } = [];

    /// <summary>Surfaces of the model that are equal to a draft (same geometry and texture). The draft was skipped.</summary>
    public List<string> Kept { get; } = [];

    /// <summary>Surfaces of the model that had the name of a new, different draft. The draft was added with another name.</summary>
    public List<string> Replaced { get; } = [];
}

/// <summary>
/// Puts converted surfaces in a Jedi Academy mesh: either in a new model (<see cref="CreateNew"/>) or in an existing one
/// (<see cref="AddSurfaces"/>). The main surfaces of the body (hips, torso, head, arms, hands, legs) are always there,
/// because every other surface hangs below one of them.
/// </summary>
public static class JkaModelBuilder
{
    /// <summary>The main surfaces of the body, parents before children.</summary>
    public static readonly string[] MainSurfaces = ["hips", "l_leg", "r_leg", "torso", "head", "l_arm", "r_arm", "l_hand", "r_hand"];

    /// <summary>
    /// Makes a new model: the root and the tag surfaces of the <paramref name="template"/>, the nine main surfaces,
    /// then the drafts. The hierarchy is: root, hips, then legs and torso below the hips, then head and arms below the
    /// torso, then hands below the arms. Every tag hangs below its main surface.
    /// </summary>
    /// <param name="modelPath">Name written in the header, for example "models/players/mychar/model".</param>
    /// <param name="textureFolder">Game path of the folder of the textures, for example "models/players/mychar".</param>
    public static GlmModel CreateNew(JkaTemplate template, IReadOnlyList<JkaSurfaceDraft> drafts, GlaSkeleton skeleton, string modelPath, string textureFolder)
    {
        var model = new GlmModel { Name = modelPath, AnimationName = template.AnimationName, BoneCount = skeleton.Bones.Count };
        model.Lods.Add([]);

        foreach (var root in template.Surfaces.Where(s => s.Parent.Length == 0)) AddTemplateSurface(model, root, -1, skeleton);
        foreach (string name in MainSurfaces) EnsureBase(model, name, skeleton);
        foreach (var tag in template.Surfaces.Where(s => s.Parent.Length > 0))
            AddTemplateSurface(model, tag, EnsureBase(model, tag.Parent, skeleton), skeleton);

        AddSurfaces(model, drafts, skeleton, textureFolder);
        return model;
    }

    /// <summary>
    /// Adds the surfaces to <paramref name="model"/>. A surface with the name of an existing one replaces it.
    /// The new surfaces go below the main surface of their region (created when the model has none).
    /// All levels of detail get the same geometry.
    /// </summary>
    public static void AddSurfaces(GlmModel model, IReadOnlyList<JkaSurfaceDraft> drafts, GlaSkeleton skeleton, string textureFolder)
    {
        model.BoneCount = skeleton.Bones.Count;
        foreach (var draft in drafts)
        {
            string shader = $"{textureFolder}/{draft.TextureName}";
            int existing = model.SurfaceIndex(draft.Name);
            if (existing < 0 && draft.Name == draft.Parent) existing = EnsureBase(model, draft.Name, skeleton);
            if (existing >= 0)
            {
                var info = model.Surfaces[existing];
                info.Shader = shader;
                info.Flags &= ~GlmSurfaceInfo.FlagOff;
                for (int lod = 0; lod < model.Lods.Count; lod++) model.Lods[lod][existing] = draft.Surface;
                continue;
            }
            int parent = EnsureBase(model, draft.Parent, skeleton);
            AddSurface(model, new GlmSurfaceInfo { Name = draft.Name, Shader = shader, Parent = parent }, draft.Surface);
        }
    }

    /// <summary>
    /// Adds the drafts to an existing model and keeps all its surfaces. A draft is skipped when a surface of the model has the same
    /// geometry and the same texture. A draft whose name is taken gets a free name (see <see cref="FreeName"/>).
    /// Only a hidden main surface without content (one triangle, see <see cref="EnsureBase"/>) is replaced.
    /// </summary>
    /// <param name="textureOf">Texture name (file name without extension) that a surface of the model shows, null when unknown.</param>
    public static JkaMergeResult MergeSurfaces(GlmModel model, IReadOnlyList<JkaSurfaceDraft> drafts, GlaSkeleton skeleton, string textureFolder, Func<int, string?> textureOf)
    {
        model.BoneCount = skeleton.Bones.Count;
        int original = model.Surfaces.Count;
        var result = new JkaMergeResult();
        var added = result.Added;
        var renamed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var draft in drafts)
        {
            // The writer copies the vertices used by more than 32 triangles, so a surface read back from a file has them copied.
            var geometry = JkaConverter.LimitTrianglesPerVertex(draft.Surface);
            int identical = -1;
            for (int i = 0; i < original && identical < 0; i++)
                if ((model.Surfaces[i].Flags & (GlmSurfaceInfo.FlagBolt | GlmSurfaceInfo.FlagOff)) == 0
                    && string.Equals(textureOf(i), draft.TextureName, StringComparison.OrdinalIgnoreCase)
                    && SameGeometry(model.Lods[0][i], geometry)) identical = i;
            if (identical >= 0)
            {
                result.Kept.Add(model.Surfaces[identical].Name);
                continue;
            }

            string shader = $"{textureFolder}/{draft.TextureName}";
            int existing = model.SurfaceIndex(draft.Name);
            if (existing < 0 && draft.Name == draft.Parent) existing = EnsureBase(model, draft.Name, skeleton);
            if (existing >= 0 && IsPlaceholder(model, existing))
            {
                var info = model.Surfaces[existing];
                info.Shader = shader;
                info.Flags &= ~GlmSurfaceInfo.FlagOff;
                for (int lod = 0; lod < model.Lods.Count; lod++) model.Lods[lod][existing] = draft.Surface;
                added.Add(draft);
                continue;
            }

            if (existing >= 0) result.Replaced.Add(model.Surfaces[existing].Name);
            string name = FreeName(model, draft.Name, renamed);
            if (name != draft.Name) renamed[draft.Name] = name;
            int parent = EnsureBase(model, draft.Parent, skeleton);
            AddSurface(model, new GlmSurfaceInfo { Name = name, Shader = shader, Parent = parent }, draft.Surface);
            added.Add(draft with { Name = name });
        }
        result.Replaced.RemoveAll(r => result.Kept.Contains(r, StringComparer.OrdinalIgnoreCase));
        return result;
    }

    /// <summary>
    /// A free surface name for a draft whose name is taken. A main surface (<c>r_hand</c>, <c>torso</c>, ...) gets a letter:
    /// <c>r_handa</c> to <c>r_handh</c>. When a skin turns the main surface off, the game looks for these variants
    /// (<c>G_GetRootSurfNameWithVariant</c>, 8 variants). With another name it thinks that the limb was cut off and,
    /// for a hand, does not draw the saber. A part of a split surface (<c>r_hand_2</c>) follows its first part (<c>r_handa_2</c>).
    /// Other names get <c>name_2</c>, <c>name_3</c>, ...
    /// </summary>
    private static string FreeName(GlmModel model, string name, Dictionary<string, string> renamed)
    {
        int underscore = name.LastIndexOf('_');
        if (underscore > 0 && name.AsSpan(underscore + 1).IndexOfAnyExceptInRange('0', '9') < 0
            && renamed.TryGetValue(name[..underscore], out string? first))
        {
            string part = first + name[underscore..];
            if (model.SurfaceIndex(part) < 0) return part;
        }
        if (MainSurfaces.Contains(name, StringComparer.OrdinalIgnoreCase))
            for (char letter = 'a'; letter <= 'h'; letter++)
                if (model.SurfaceIndex(name + letter) < 0) return name + letter;
        string free = name;
        for (int k = 2; model.SurfaceIndex(free) >= 0; k++) free = $"{name}_{k}";
        return free;
    }

    // A main surface that EnsureBase made: hidden, one triangle. A main surface with variants below it (JkaVariants) is not one:
    // its only children are tags and other main surfaces.
    private static bool IsPlaceholder(GlmModel model, int index) =>
        (model.Surfaces[index].Flags & GlmSurfaceInfo.FlagOff) != 0 && MainSurfaces.Contains(model.Surfaces[index].Name, StringComparer.OrdinalIgnoreCase)
        && model.Lods[0][index].Triangles.Length == 3
        && model.Surfaces[index].Children.All(c => (model.Surfaces[c].Flags & GlmSurfaceInfo.FlagBolt) != 0
            || MainSurfaces.Contains(model.Surfaces[c].Name, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// True when both surfaces have the same triangles and, vertex by vertex, the same position, texture coordinates and weights.
    /// The tolerances cover the rounding of the file: weights have 10 bits.
    /// </summary>
    public static bool SameGeometry(GlmSurface a, GlmSurface b)
    {
        if (a.Vertices.Length != b.Vertices.Length || !a.Triangles.AsSpan().SequenceEqual(b.Triangles)) return false;
        for (int i = 0; i < a.Vertices.Length; i++)
        {
            var (va, vb) = (a.Vertices[i], b.Vertices[i]);
            if (Vector3.DistanceSquared(va.Position, vb.Position) > 1e-6f || Vector2.DistanceSquared(va.Uv, vb.Uv) > 1e-8f) return false;
            if (va.Weights.Length != vb.Weights.Length) return false;
            for (int k = 0; k < va.Weights.Length; k++)
                if (va.Weights[k].Bone != vb.Weights[k].Bone || MathF.Abs(va.Weights[k].Weight - vb.Weights[k].Weight) > 3f / 1023f) return false;
        }
        return true;
    }

    /// <summary>
    /// The surface lines for a skin file: one line for each draft, with the game path of the texture file (with the extension).
    /// </summary>
    public static IEnumerable<(string Surface, string Shader)> SkinLines(IEnumerable<JkaSurfaceDraft> drafts, string textureFolder, string extension) =>
        drafts.Select(d => (d.Name, $"{textureFolder}/{d.TextureName}{extension}"));

    private static int EnsureBase(GlmModel model, string name, GlaSkeleton skeleton)
    {
        int existing = model.SurfaceIndex(name);
        if (existing >= 0) return existing;

        var region = Enum.GetValues<JkaRegion>().First(r => JkaBoneMap.BaseSurface(r) == name);
        string? parentName = JkaBoneMap.ParentSurface(region);
        int parent = parentName is null
            ? model.Surfaces.FindIndex(s => s.Parent < 0)
            : EnsureBase(model, parentName, skeleton);

        // A hidden surface with one triangle of no area. It has no shader and is never drawn.
        string boneName = region switch
        {
            JkaRegion.Hips => "pelvis", JkaRegion.Torso => "thoracic", JkaRegion.Head => "cranium",
            JkaRegion.LeftArm => "lhumerus", JkaRegion.RightArm => "rhumerus", JkaRegion.LeftHand => "lhand", JkaRegion.RightHand => "rhand",
            JkaRegion.LeftLeg => "ltibia", _ => "rtibia",
        };
        int bone = Math.Max(skeleton.IndexOf(boneName), 0);
        var origin = skeleton.Bones[bone].Origin;
        var vertices = Enumerable.Range(0, 3).Select(_ => new GlmVertex { Position = origin, Normal = Vector3.UnitZ, Weights = [new GlmWeight(bone, 1f)] }).ToArray();
        return AddSurface(model, new GlmSurfaceInfo { Name = name, Flags = GlmSurfaceInfo.FlagOff, Parent = parent },
            new GlmSurface { Vertices = vertices, Triangles = [0, 1, 2] });
    }

    private static int AddTemplateSurface(GlmModel model, JkaTemplateSurface source, int parent, GlaSkeleton skeleton) =>
        AddSurface(model, new GlmSurfaceInfo { Name = source.Name, Flags = source.Flags, Shader = "", Parent = parent },
            new GlmSurface { Vertices = JkaTemplate.ToVertices(source, skeleton), Triangles = source.Triangles });

    private static int AddSurface(GlmModel model, GlmSurfaceInfo info, GlmSurface geometry)
    {
        model.Surfaces.Add(info);
        int index = model.Surfaces.Count - 1;
        for (int lod = 0; lod < model.Lods.Count; lod++)
        {
            var old = model.Lods[lod];
            var grown = new GlmSurface[old.Length + 1];
            old.CopyTo(grown, 0);
            grown[^1] = geometry;
            model.Lods[lod] = grown;
        }
        if (info.Parent >= 0) model.Surfaces[info.Parent].Children.Add(index);
        return index;
    }
}
