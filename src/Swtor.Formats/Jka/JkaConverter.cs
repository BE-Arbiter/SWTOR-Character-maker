using System.Numerics;
using Swtor.Formats.Gr2;

namespace Swtor.Formats.Jka;

/// <summary>A range of triangles of a SWTOR mesh that is exported as one unit, with the name of its texture.</summary>
public sealed class JkaSourcePiece
{
    /// <summary>"head", "hair", "facehair", "chest", "hand", "leg", "boot", "waist" or "bracer".</summary>
    public required string Slot { get; init; }

    public required Gr2Mesh Mesh { get; init; }
    public int StartTriangle { get; init; }
    public required int TriangleCount { get; init; }

    /// <summary>File name of the texture without folder and extension, for example "chest".</summary>
    public required string TextureName { get; init; }

    /// <summary>"eyes" for the eye material of a head, "skin" for the bare skin piece of an equipment item. Empty otherwise.</summary>
    public string Role { get; init; } = "";

    /// <summary>0 for the main model of a slot, 1 and more for attachments (shoulder pieces, back pieces).</summary>
    public int Index { get; init; }
}

/// <summary>A surface ready to be put in a .glm file.</summary>
/// <param name="Parent">Name of the surface that holds it in the hierarchy.</param>
public sealed record JkaSurfaceDraft(string Name, string Parent, string TextureName, JkaRegion Region, GlmSurface Surface);

/// <summary>
/// Turns SWTOR meshes into Jedi Academy surfaces: moves the vertices (<see cref="JkaRetarget"/>), changes the bone weights to the
/// bones of the humanoid skeleton, cuts each mesh by body region (torso, arms, legs, ...) and names the surfaces.
/// </summary>
public static class JkaConverter
{
    private const int MaxBonesPerSurface = 32;

    /// <summary>The game does not accept a surface with more vertices than this.</summary>
    public const int MaxVerticesPerSurface = 1000;

    /// <summary>
    /// The stencil shadow of the game keeps at most 32 edges per vertex (MAX_EDGE_DEFS in tr_shadows). Each triangle adds one edge
    /// to each of its corners, so a vertex can be used by 32 triangles at most. Above that the game breaks.
    /// </summary>
    public const int MaxTrianglesPerVertex = 32;
    private const float RelativeCutoff = 0.05f;

    /// <summary>
    /// Vertex budget of a whole model. A collision trace (saber damage) moves all vertices of the model into the Ghoul2 mini-heap
    /// (G2_MINIHEAP_SIZE, 256 KB in OpenJK, 512 KB in some builds) at 20 bytes each (numVerts * 5 * 4, see R_TransformEachSurface).
    /// Above the heap size the game stops with "Ran out of transform space for Ghoul2 Models".
    /// 18,000 vertices (360 KB) fit a heap of 512 KB. A 256 KB heap needs about 12,000.
    /// </summary>
    public const int MaxTotalVertices = 18000;

    // Surfaces below this size are never reduced (eyes, caps, tags).
    private const int MinVerticesToReduce = 150;

    // Vertices that the model gets besides the drafts: the tag surfaces of the template (one triangle each) and the empty main surfaces.
    // The budget is for the whole file, so the drafts get what is left (about 150 are used, this keeps a margin).
    private const int TemplateVertices = 200;

    // The head costs this many times more per collapse than the rest, so it only gives up vertices that nobody sees.
    private const float HeadCostScale = 4f;


    private static readonly string[] FallbackBones =
    [
        "pelvis", "lower_lumbar", "upper_lumbar", "thoracic", "cervical", "cranium", "lclavical", "rclavical", "lhumerus", "rhumerus",
        "lradius", "rradius", "lhand", "rhand", "lfemurYZ", "rfemurYZ", "ltibia", "rtibia", "ltalus", "rtalus",
    ];

    private sealed class MeshData
    {
        public required Vector3[] Positions;
        public required Vector3[] Normals;
        public required GlmWeight[][] Weights;
    }

    /// <param name="maxTotalVertices">Vertex budget of the model, 0 for no limit. See <see cref="MaxTotalVertices"/>.</param>
    public static List<JkaSurfaceDraft> Convert(IReadOnlyList<JkaSourcePiece> pieces, JkaRetarget retarget, GlaSkeleton jka, int maxTotalVertices = MaxTotalVertices)
    {
        var meshes = new Dictionary<Gr2Mesh, MeshData>();
        foreach (var piece in pieces)
            if (!meshes.ContainsKey(piece.Mesh)) meshes[piece.Mesh] = Prepare(piece.Mesh, retarget, jka);

        var drafts = new List<JkaSurfaceDraft>();
        foreach (var piece in pieces) drafts.AddRange(ConvertPiece(piece, meshes[piece.Mesh], jka));

        var surfaces = drafts.Select(d => d.Surface).ToArray();
        // Over budget: find what nobody sees (the body under an armor). It is not removed, that would leave holes: it is reduced to
        // the vertices of its border. The head is not tested (the eyes lie inside it).
        bool[][]? hidden = maxTotalVertices > 0 && surfaces.Sum(s => s.Vertices.Length) > maxTotalVertices - TemplateVertices
            ? HiddenGeometry.Find(surfaces, i => drafts[i].Region != JkaRegion.Head) : null;
        ReduceSurfaces(surfaces, _ => false, i => drafts[i].Region == JkaRegion.Head ? HeadCostScale : 1f, maxTotalVertices > 0 ? maxTotalVertices - TemplateVertices : 0, hidden);
        for (int i = 0; i < drafts.Count; i++) drafts[i] = drafts[i] with { Surface = surfaces[i] };
        return drafts;
    }
    // Moves the vertices and converts the weights of a whole mesh.
    private static MeshData Prepare(Gr2Mesh mesh, JkaRetarget retarget, GlaSkeleton jka)
    {
        var positions = new Vector3[mesh.VertexCount];
        var normals = new Vector3[mesh.VertexCount];
        retarget.Transform(mesh, positions, normals);

        // For each bone of the mesh: the Jedi Academy bones that take its weight.
        var targets = mesh.Bones.Select(b => JkaBoneMap.Map(b.Name)
            .Select(t => (Index: jka.IndexOf(t.Bone), t.Fraction)).Where(t => t.Index >= 0).ToArray()).ToArray();

        // Below the hips, the weight of the pelvis and of bones without counterpart (robe skirt and cloth bones) is shared
        // between the pelvis and the two legs (JkaRetarget.LegShare), so that a skirt follows the legs.
        int pelvis = jka.IndexOf("pelvis");
        var legBones = new[] { (Thigh: jka.IndexOf("lfemurYZ"), Shin: jka.IndexOf("ltibia")), (Thigh: jka.IndexOf("rfemurYZ"), Shin: jka.IndexOf("rtibia")) };
        bool canShare = pelvis >= 0 && legBones.All(l => l.Thigh >= 0 && l.Shin >= 0);
        var pelvisOrNone = targets.Select(t => t.Length == 0 || (t.Length == 1 && t[0].Index == pelvis)).ToArray();

        var weights = new GlmWeight[mesh.VertexCount][];
        var accumulated = new Dictionary<int, float>();
        for (int v = 0; v < mesh.VertexCount; v++)
        {
            accumulated.Clear();
            if (mesh.BoneWeights is not null && mesh.BoneIndices is not null)
            {
                var share = canShare ? retarget.LegShare(mesh.Positions[v]) : null;
                var w4 = mesh.BoneWeights[v];
                for (int k = 0; k < 4; k++)
                {
                    float w = k switch { 0 => w4.X, 1 => w4.Y, 2 => w4.Z, _ => w4.W };
                    if (w <= 0) continue;
                    int bone = mesh.BoneIndices[v * 4 + k];
                    if (share is { } s && pelvisOrNone[bone])
                    {
                        float toLeg = w * s.Share;
                        accumulated[pelvis] = accumulated.GetValueOrDefault(pelvis) + w - toLeg;
                        for (int side = 0; side < 2; side++)
                        {
                            var leg = legBones[side];
                            float toSide = toLeg * (side == 0 ? s.Left : 1 - s.Left);
                            accumulated[leg.Thigh] = accumulated.GetValueOrDefault(leg.Thigh) + toSide * (1 - s.Shin);
                            accumulated[leg.Shin] = accumulated.GetValueOrDefault(leg.Shin) + toSide * s.Shin;
                        }
                        continue;
                    }
                    foreach (var (index, fraction) in targets[bone])
                        accumulated[index] = accumulated.GetValueOrDefault(index) + w * fraction;
                }
                foreach (int key in accumulated.Where(a => a.Value <= 0).Select(a => a.Key).ToList()) accumulated.Remove(key);
            }
            weights[v] = accumulated.Count == 0 ? [new GlmWeight(NearestBone(positions[v], jka), 1f)] : Normalize(accumulated);
        }
        return new MeshData { Positions = positions, Normals = normals, Weights = weights };
    }

    // The strongest four weights, scaled to add up to 1. Weights below 5% of the strongest one are dropped.
    private static GlmWeight[] Normalize(Dictionary<int, float> weights)
    {
        var sorted = weights.OrderByDescending(w => w.Value).Take(4).ToList();
        float cutoff = sorted[0].Value * RelativeCutoff;
        sorted = sorted.Where((w, i) => i == 0 || w.Value >= cutoff).ToList();
        float total = sorted.Sum(w => w.Value);
        return sorted.Select(w => new GlmWeight(w.Key, w.Value / total)).ToArray();
    }

    // The fallback bone of a vertex without weight: the nearest bone origin of FallbackBones.
    // Above the hip joints, the leg bones are skipped. At the side of the hips their origin is nearer than the pelvis, so the top of
    // a robe skirt (cloth bones only) followed the thigh fully while the robe just below stayed on the pelvis: the skirt tore open.
    private static int NearestBone(Vector3 p, GlaSkeleton jka)
    {
        int best = 0;
        float bestDistance = float.MaxValue;
        int hip = jka.IndexOf("lfemurYZ");
        bool aboveHips = hip >= 0 && p.Z > jka.Bones[hip].Origin.Z;
        foreach (string name in FallbackBones)
        {
            if (aboveHips && IsLegBone(name)) continue;
            int index = jka.IndexOf(name);
            if (index < 0) continue;
            float d = Vector3.DistanceSquared(p, jka.Bones[index].Origin);
            if (d < bestDistance) (best, bestDistance) = (index, d);
        }
        return best;
    }

    private static bool IsLegBone(string name) => name.Contains("femur") || name.Contains("tibia") || name.Contains("talus");

    private static List<JkaSurfaceDraft> ConvertPiece(JkaSourcePiece piece, MeshData data, GlaSkeleton jka)
    {
        var mesh = piece.Mesh;
        var allowed = AllowedRegions(piece.Slot);
        bool handSeparate = allowed.Contains(JkaRegion.LeftHand);
        var regionOfBone = new Dictionary<int, JkaRegion>();
        JkaRegion RegionOf(int bone)
        {
            if (!regionOfBone.TryGetValue(bone, out var region))
                regionOfBone[bone] = region = JkaBoneMap.RegionOf(jka.Bones[bone].Name, handSeparate);
            return region;
        }

        // Winding: Jedi Academy front faces turn clockwise when seen from outside, with the normals pointing out.
        int counterClockwise = 0, clockwise = 0;
        for (int t = 0; t < piece.TriangleCount; t++)
        {
            int a = mesh.Indices[(piece.StartTriangle + t) * 3], b = mesh.Indices[(piece.StartTriangle + t) * 3 + 1], c = mesh.Indices[(piece.StartTriangle + t) * 3 + 2];
            float facing = Vector3.Dot(Vector3.Cross(data.Positions[b] - data.Positions[a], data.Positions[c] - data.Positions[a]),
                data.Normals[a] + data.Normals[b] + data.Normals[c]);
            if (facing > 0) counterClockwise++;
            else if (facing < 0) clockwise++;
        }
        bool flip = counterClockwise > clockwise;

        var groups = new Dictionary<JkaRegion, (List<int> Triangles, Dictionary<int, int> Map, List<int> Source)>();
        var scores = new float[Enum.GetValues<JkaRegion>().Length];
        for (int t = 0; t < piece.TriangleCount; t++)
        {
            Span<int> corner = [mesh.Indices[(piece.StartTriangle + t) * 3], mesh.Indices[(piece.StartTriangle + t) * 3 + 1], mesh.Indices[(piece.StartTriangle + t) * 3 + 2]];
            Array.Clear(scores);
            float x = 0;
            foreach (int v in corner)
            {
                foreach (var w in data.Weights[v]) scores[(int)RegionOf(w.Bone)] += w.Weight;
                x += data.Positions[v].X;
            }
            int best = 0;
            for (int r = 1; r < scores.Length; r++) if (scores[r] > scores[best]) best = r;
            var region = Resolve((JkaRegion)best, allowed, x >= 0);

            if (!groups.TryGetValue(region, out var group)) groups[region] = group = ([], [], []);
            if (flip) (corner[1], corner[2]) = (corner[2], corner[1]);
            foreach (int v in corner)
            {
                if (!group.Map.TryGetValue(v, out int mapped))
                {
                    mapped = group.Source.Count;
                    group.Map[v] = mapped;
                    group.Source.Add(v);
                }
                group.Triangles.Add(mapped);
            }
        }

        var uvs = mesh.UvSets.Count > 0 ? mesh.UvSets[0] : null;
        var result = new List<JkaSurfaceDraft>();
        foreach (var (region, group) in groups.OrderBy(g => g.Key))
        {
            string name = SurfaceName(piece, region);
            int part = 1;
            LimitTrianglesPerVertex(group.Triangles, group.Source);
            foreach (var (triangles, source) in SplitByVertexLimit(group.Triangles, group.Source))
            {
                var vertices = new GlmVertex[source.Count];
                for (int i = 0; i < vertices.Length; i++)
                {
                    int v = source[i];
                    vertices[i] = new GlmVertex
                    {
                        Position = data.Positions[v], Normal = data.Normals[v],
                        Uv = uvs is null ? Vector2.Zero : new Vector2(uvs[v].X, uvs[v].Y),
                        Weights = data.Weights[v],
                    };
                }
                LimitBones(vertices, jka);
                string partName = part++ == 1 ? name : $"{name}_{part - 1}";
                result.Add(new JkaSurfaceDraft(partName, JkaBoneMap.BaseSurface(region), piece.TextureName, region,
                    new GlmSurface { Vertices = vertices, Triangles = [.. triangles] }));
            }
        }
        return result;
    }

    /// <summary>
    /// Gives each vertex used by more than <see cref="MaxTrianglesPerVertex"/> triangles copies, so that no copy is used by more.
    /// Triangles are taken in order: the first 32 keep the vertex, the next 32 get a copy, and so on.
    /// </summary>
    /// <param name="triangles">Three vertex numbers per triangle, numbers are positions in <paramref name="source"/>. Changed in place.</param>
    /// <param name="source">For each vertex, the vertex number in the mesh. Copies are added at the end.</param>
    public static void LimitTrianglesPerVertex(List<int> triangles, List<int> source)
    {
        int count = source.Count;
        var uses = new int[count];
        foreach (int v in triangles) uses[v]++;

        var current = new int[count];
        var filled = new int[count];
        for (int v = 0; v < count; v++) current[v] = v;
        for (int i = 0; i < triangles.Count; i++)
        {
            int v = triangles[i];
            if (uses[v] <= MaxTrianglesPerVertex) continue;
            if (filled[v] == MaxTrianglesPerVertex)
            {
                current[v] = source.Count;
                source.Add(source[v]);
                filled[v] = 0;
            }
            filled[v]++;
            triangles[i] = current[v];
        }
    }

    /// <summary>Same as the list form, for a finished surface. Returns the surface itself when no vertex is over the limit.</summary>
    public static GlmSurface LimitTrianglesPerVertex(GlmSurface surface)
    {
        var triangles = surface.Triangles.ToList();
        var source = Enumerable.Range(0, surface.Vertices.Length).ToList();
        LimitTrianglesPerVertex(triangles, source);
        if (source.Count == surface.Vertices.Length) return surface;
        return new GlmSurface { Vertices = source.Select(v => surface.Vertices[v]).ToArray(), Triangles = [.. triangles] };
    }

    /// <summary>
    /// Cuts a surface in parts of at most <see cref="MaxVerticesPerSurface"/> vertices. Triangles are taken in order, and a part
    /// ends when the next triangle would need more vertices. A vertex used by two parts is copied into both.
    /// </summary>
    /// <param name="triangles">Three vertex numbers per triangle, numbers are positions in <paramref name="source"/>.</param>
    /// <param name="source">For each vertex, the vertex number in the mesh.</param>
    /// <returns>For each part: its triangles (numbers inside the part) and its list of mesh vertex numbers.</returns>
    public static List<(List<int> Triangles, List<int> Source)> SplitByVertexLimit(List<int> triangles, List<int> source)
    {
        if (source.Count <= MaxVerticesPerSurface) return [(triangles, source)];

        var parts = new List<(List<int>, List<int>)>();
        var current = (Triangles: new List<int>(), Source: new List<int>());
        var map = new Dictionary<int, int>();
        for (int t = 0; t < triangles.Count; t += 3)
        {
            int missing = 0;
            for (int k = 0; k < 3; k++) if (!map.ContainsKey(triangles[t + k])) missing++;
            if (current.Source.Count + missing > MaxVerticesPerSurface)
            {
                parts.Add(current);
                current = ([], []);
                map = [];
            }
            for (int k = 0; k < 3; k++)
            {
                int v = triangles[t + k];
                if (!map.TryGetValue(v, out int mapped))
                {
                    mapped = current.Source.Count;
                    map[v] = mapped;
                    current.Source.Add(source[v]);
                }
                current.Triangles.Add(mapped);
            }
        }
        parts.Add(current);
        return parts;
    }

    /// <summary>Reduces the vertices of a finished model (first level of detail) to the budget. Tags and the head are not changed.</summary>
    /// <returns>The vertex count before and after.</returns>
    public static (int Before, int After) Reduce(GlmModel model, int maxTotalVertices = MaxTotalVertices)
    {
        var lod = model.Lods[0];
        int before = lod.Sum(s => s.Vertices.Length);
        ReduceSurfaces(lod, i => (model.Surfaces[i].Flags & GlmSurfaceInfo.FlagBolt) != 0, i => model.Surfaces[i].Name.StartsWith("head", StringComparison.OrdinalIgnoreCase) ? HeadCostScale : 1f, maxTotalVertices);
        return (before, lod.Sum(s => s.Vertices.Length));
    }

    /// <summary>
    /// Simplifies the surfaces until their vertices fit the budget. Every surface of <see cref="MinVerticesToReduce"/> vertices or more
    /// is reduced with the same error threshold (scaled by <paramref name="costScale"/>), chosen so that the total fits.
    /// </summary>
    private static void ReduceSurfaces(GlmSurface[] surfaces, Func<int, bool> isProtected, Func<int, float> costScale, int maxTotalVertices, bool[][]? hidden = null)
    {
        if (maxTotalVertices <= 0) return;
        int total = surfaces.Sum(s => s.Vertices.Length);
        if (total <= maxTotalVertices) return;
        bool Reducible(int i) => !isProtected(i) && surfaces[i].Vertices.Length >= MinVerticesToReduce;
        var reducible = Enumerable.Range(0, surfaces.Length).Where(Reducible).ToArray();
        if (reducible.Length == 0) return;

        // One error threshold for all surfaces: the cheapest collapses go first, wherever they are. A flat part loses many
        // vertices, a curved part (a shoulder pad) loses few. The same ratio for every surface made curved pads fall in.
        var positions = new Vector3[surfaces.Length][];
        var errors = new List<float>();
        foreach (int i in reducible)
        {
            positions[i] = surfaces[i].Vertices.Select(v => v.Position).ToArray();
            var own = new List<float>();
            MeshSimplifier.Simplify(positions[i], surfaces[i].Triangles, 0, MaxTrianglesPerVertex, errorLog: own, hiddenTriangles: hidden?[i]);
            errors.AddRange(own.Select(e => e * costScale(i)));
        }
        errors.Sort();
        if (errors.Count == 0) return;
        var original = (GlmSurface[])surfaces.Clone();

        // Applies a threshold to every surface and returns the new vertex count.
        int Apply(float threshold)
        {
            foreach (int i in reducible)
            {
                var surface = original[i];
                var (triangles, source, moved) = MeshSimplifier.Simplify(positions[i], surface.Triangles, 0, MaxTrianglesPerVertex, threshold / costScale(i), hiddenTriangles: hidden?[i]);
                var vertices = new GlmVertex[source.Count];
                for (int k = 0; k < vertices.Length; k++)
                {
                    var old = surface.Vertices[source[k]];
                    // A vertex that moved onto a neighbor keeps its own UV and weights.
                    vertices[k] = old.Position == moved[k] ? old : new GlmVertex { Position = moved[k], Normal = old.Normal, Uv = old.Uv, Weights = old.Weights };
                }
                surfaces[i] = new GlmSurface { Vertices = vertices, Triangles = [.. triangles] };
            }
            return surfaces.Sum(s => s.Vertices.Length);
        }

        // The game does not accept more vertices than the budget, so the budget is a hard limit and the threshold is the smallest
        // one that fits (bisection: the vertex count falls when the threshold grows). The order of collapses is not exactly sorted by
        // error, so the guess from the log can fall short.
        float high = errors[^1] * 1.001f, low = 0;
        float guess = errors[Math.Min(total - maxTotalVertices, errors.Count) - 1];
        if (Apply(guess) <= maxTotalVertices) high = guess; else low = guess;
        if (low > 0 && Apply(high) > maxTotalVertices) return; // Not reachable: keep the most reduced version.
        for (int step = 0; step < 14; step++)
        {
            float mid = (low + high) / 2;
            if (Apply(mid) <= maxTotalVertices) high = mid; else low = mid;
        }
        Apply(high);
    }


    /// <summary>Regions that the pieces of a slot can fill. A piece never makes surfaces outside of them.</summary>
    public static JkaRegion[] AllowedRegions(string slot) => slot switch
    {
        "head" or "face" or "hair" or "facehair" => [JkaRegion.Head],
        "chest" => [JkaRegion.Torso, JkaRegion.LeftArm, JkaRegion.RightArm],
        "hand" => [JkaRegion.LeftHand, JkaRegion.RightHand],
        "leg" => [JkaRegion.Hips, JkaRegion.LeftLeg, JkaRegion.RightLeg],
        "boot" => [JkaRegion.LeftLeg, JkaRegion.RightLeg],
        "waist" => [JkaRegion.Hips, JkaRegion.Torso],
        "bracer" => [JkaRegion.LeftArm, JkaRegion.RightArm],
        _ => Enum.GetValues<JkaRegion>(),
    };

    // A triangle that falls in a region the slot cannot fill goes to the nearest allowed region.
    private static JkaRegion Resolve(JkaRegion region, JkaRegion[] allowed, bool left)
    {
        if (allowed.Contains(region)) return region;
        var leg = left ? JkaRegion.LeftLeg : JkaRegion.RightLeg;
        var arm = left ? JkaRegion.LeftArm : JkaRegion.RightArm;
        var hand = left ? JkaRegion.LeftHand : JkaRegion.RightHand;
        JkaRegion[] preference = region switch
        {
            JkaRegion.Head => [JkaRegion.Torso, JkaRegion.Hips],
            JkaRegion.Torso => [JkaRegion.Hips, JkaRegion.Head, arm],
            JkaRegion.Hips => [JkaRegion.Torso, leg],
            JkaRegion.LeftArm => [JkaRegion.LeftHand, JkaRegion.Torso],
            JkaRegion.RightArm => [JkaRegion.RightHand, JkaRegion.Torso],
            JkaRegion.LeftHand => [JkaRegion.LeftArm, JkaRegion.Torso],
            JkaRegion.RightHand => [JkaRegion.RightArm, JkaRegion.Torso],
            _ => [JkaRegion.Hips, JkaRegion.Torso],
        };
        foreach (var candidate in preference) if (allowed.Contains(candidate)) return candidate;
        foreach (var candidate in new[] { leg, arm, hand }) if (allowed.Contains(candidate)) return candidate;
        return allowed[0];
    }

    /// <summary>
    /// Surface name: the main surface of a region when the slot is the one that normally fills it
    /// (chest for torso and arms, hand for hands, leg for hips and legs, head for the head), otherwise "region_slot".
    /// </summary>
    public static string SurfaceName(JkaSourcePiece piece, JkaRegion region)
    {
        string baseName = JkaBoneMap.BaseSurface(region);
        string suffix = piece.Index > 0 ? piece.Index.ToString() : "";
        if (piece.Slot == "head" && piece.Role == "eyes") return $"head_eyes{suffix}";
        if (piece.Role == "skin") return $"{baseName}_{piece.Slot}_skin{suffix}";
        bool primary = piece.Slot switch
        {
            "head" => region == JkaRegion.Head,
            "chest" => region is JkaRegion.Torso or JkaRegion.LeftArm or JkaRegion.RightArm,
            "hand" => region is JkaRegion.LeftHand or JkaRegion.RightHand,
            "leg" => region is JkaRegion.Hips or JkaRegion.LeftLeg or JkaRegion.RightLeg,
            _ => false,
        };
        return primary && piece.Index == 0 ? baseName : $"{baseName}_{piece.Slot}{suffix}";
    }

    // A surface can use 32 bones at most. The least used bones give their weight to the nearest ancestor that stays.
    private static void LimitBones(GlmVertex[] vertices, GlaSkeleton jka)
    {
        while (true)
        {
            var usage = new Dictionary<int, float>();
            foreach (var v in vertices)
                foreach (var w in v.Weights) usage[w.Bone] = usage.GetValueOrDefault(w.Bone) + w.Weight;
            if (usage.Count <= MaxBonesPerSurface) return;

            int weakest = usage.OrderBy(u => u.Value).First().Key;
            int replacement = jka.Bones[weakest].Parent;
            while (replacement >= 0 && (replacement == weakest || !usage.ContainsKey(replacement))) replacement = jka.Bones[replacement].Parent;
            if (replacement < 0) replacement = usage.Where(u => u.Key != weakest).OrderByDescending(u => u.Value).First().Key;

            foreach (var v in vertices)
            {
                if (!v.Weights.Any(w => w.Bone == weakest)) continue;
                var merged = new Dictionary<int, float>();
                foreach (var w in v.Weights)
                {
                    int bone = w.Bone == weakest ? replacement : w.Bone;
                    merged[bone] = merged.GetValueOrDefault(bone) + w.Weight;
                }
                v.Weights = merged.Select(m => new GlmWeight(m.Key, m.Value)).ToArray();
            }
        }
    }
}
