using System.Numerics;
using System.Text;
using System.Text.Json;

namespace Swtor.Formats.Jka;

/// <summary>One vertex of a template surface. Bones are named, so the template does not depend on the bone order of a .gla file.</summary>
public sealed record JkaTemplateVertex(float[] Position, float[] Normal, float[] Uv, string[] Bones, float[] Weights);

/// <summary>A tag or root surface of the template. It is always one triangle. <see cref="Parent"/> is empty for the root.</summary>
public sealed record JkaTemplateSurface(string Name, uint Flags, string Parent, int[] Triangles, JkaTemplateVertex[] Vertices);

/// <summary>
/// The fixed part of every exported player model: the first surface ("stupidtriangle_off") and the tag surfaces
/// ("*name") that the game reads for bolts and dismemberment. The data comes from the sample models (see
/// <see cref="FromModels"/>) and is embedded in the library, so an export needs no reference model.
/// The main surfaces (hips, torso, ...) are not in the template: <see cref="JkaModelBuilder"/> makes them.
/// </summary>
public sealed class JkaTemplate
{
    private const string ResourceName = "jka_template.json";
    private static readonly string[] BaseSurfaces = ["hips", "l_leg", "r_leg", "torso", "head", "l_arm", "r_arm", "l_hand", "r_hand"];
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static JkaTemplate? _standard;

    public int Version { get; init; } = 1;
    public required string AnimationName { get; init; }
    public required IReadOnlyList<JkaTemplateSurface> Surfaces { get; init; }

    /// <summary>The template that is embedded in this library.</summary>
    public static JkaTemplate Standard => _standard ??= LoadStandard();

    private static JkaTemplate LoadStandard()
    {
        using var stream = typeof(JkaTemplate).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidDataException($"The embedded resource {ResourceName} is missing.");
        return JsonSerializer.Deserialize<JkaTemplate>(stream, Json) ?? throw new InvalidDataException("The embedded template is empty.");
    }

    public static JkaTemplate FromJson(string json) => JsonSerializer.Deserialize<JkaTemplate>(json, Json) ?? throw new InvalidDataException("The template is empty.");

    /// <summary>The template as JSON with one surface on each line.</summary>
    public string ToJson()
    {
        var sb = new StringBuilder();
        sb.Append($"{{\"version\":{Version},\"animationName\":{JsonSerializer.Serialize(AnimationName)},\"surfaces\":[\n");
        sb.AppendJoin(",\n", Surfaces.Select(s => "  " + JsonSerializer.Serialize(s, Json)));
        sb.Append("\n]}\n");
        return sb.ToString();
    }

    /// <summary>
    /// Builds a template from sample models by vote. A surface goes in when at least <paramref name="quorum"/> models have it
    /// as a tag (flag "bolt") or as the root. For each surface, the most common geometry wins (position, bones and weights
    /// rounded to 0.01), and the most common parent wins. A tie goes to the model that comes first in the list.
    /// Models whose tag is not one triangle are ignored for that tag.
    /// </summary>
    public static JkaTemplate FromModels(IReadOnlyList<(GlmModel Model, GlaSkeleton Skeleton)> sources, int quorum = 2)
    {
        var order = new List<string>();
        var byName = new Dictionary<string, List<(JkaTemplateSurface Surface, string Signature)>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (model, skeleton) in sources)
            for (int i = 0; i < model.Surfaces.Count; i++)
            {
                var info = model.Surfaces[i];
                bool root = info.Parent < 0 && (info.Flags & GlmSurfaceInfo.FlagOff) != 0;
                if (!root && (info.Flags & GlmSurfaceInfo.FlagBolt) == 0) continue;
                var geometry = model.Lods[0][i];
                if (geometry.Vertices.Length != 3 || geometry.Triangles.Length != 3) continue;

                string parent = root ? "" : StandardParent(model.Surfaces[info.Parent].Name);
                var vertices = geometry.Vertices.Select(v => ToTemplate(v, skeleton)).ToArray();
                var surface = new JkaTemplateSurface(info.Name, info.Flags, parent, geometry.Triangles, vertices);
                if (!byName.TryGetValue(info.Name, out var list))
                {
                    byName[info.Name] = list = [];
                    order.Add(info.Name);
                }
                list.Add((surface, Signature(surface)));
            }

        var result = new List<JkaTemplateSurface>();
        foreach (string name in order)
        {
            var candidates = byName[name];
            if (candidates.Count < quorum) continue;
            string signature = MostCommon(candidates.Select(c => c.Signature));
            string parent = MostCommon(candidates.Select(c => c.Surface.Parent));
            result.Add(candidates.First(c => c.Signature == signature).Surface with { Parent = parent });
        }

        // The root comes first. OrderBy is stable, so the other surfaces keep their order.
        result = result.OrderBy(s => s.Parent.Length == 0 ? 0 : 1).ToList();
        string animation = MostCommon(sources.Select(s => s.Model.AnimationName));
        return new JkaTemplate { AnimationName = animation, Surfaces = result };
    }

    // The first value with the highest count (the list order breaks ties).
    private static string MostCommon(IEnumerable<string> values)
    {
        var list = values.ToList();
        return list.GroupBy(v => v).OrderByDescending(g => g.Count()).ThenBy(g => list.IndexOf(g.Key)).First().Key;
    }

    private static JkaTemplateVertex ToTemplate(GlmVertex v, GlaSkeleton skeleton) => new(
        [v.Position.X, v.Position.Y, v.Position.Z], [v.Normal.X, v.Normal.Y, v.Normal.Z], [v.Uv.X, v.Uv.Y],
        v.Weights.Select(w => skeleton.Bones[w.Bone].Name).ToArray(), v.Weights.Select(w => w.Weight).ToArray());

    private static string Signature(JkaTemplateSurface s) =>
        string.Join(',', s.Triangles) + "/" + string.Join('|', s.Vertices.Select(v => string.Join(',', v.Position.Concat(v.Normal).Concat(v.Uv).Concat(v.Weights).Select(f => Math.Round(f, 2).ToString("F2", System.Globalization.CultureInfo.InvariantCulture)))
            + ":" + string.Join(',', v.Bones)));

    // Maps the parent of a tag in a sample model to one of the nine main surfaces.
    private static string StandardParent(string parent)
    {
        string p = parent.ToLowerInvariant();
        if (Array.IndexOf(BaseSurfaces, p) >= 0) return p;
        foreach (string prefix in BaseSurfaces)
            if (p.StartsWith(prefix, StringComparison.Ordinal)) return prefix;
        return p.StartsWith("head", StringComparison.Ordinal) ? "head" : "torso";
    }

    /// <summary>Makes the vertices of a surface for a skeleton. Throws when the skeleton lacks a bone of the template.</summary>
    internal static GlmVertex[] ToVertices(JkaTemplateSurface surface, GlaSkeleton skeleton) =>
        surface.Vertices.Select(v => new GlmVertex
        {
            Position = new Vector3(v.Position[0], v.Position[1], v.Position[2]),
            Normal = new Vector3(v.Normal[0], v.Normal[1], v.Normal[2]),
            Uv = new Vector2(v.Uv[0], v.Uv[1]),
            Weights = v.Bones.Select((b, i) => new GlmWeight(
                skeleton.IndexOf(b) is var bone and >= 0 ? bone : throw new InvalidDataException($"The skeleton has no bone '{b}' (needed by the surface '{surface.Name}')."),
                v.Weights[i])).ToArray(),
        }).ToArray();
}
