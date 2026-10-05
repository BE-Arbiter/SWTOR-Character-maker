using System.Numerics;
using Swtor.Formats.Jka;

namespace Swtor.Tests;

/// <summary>Test data for the Jedi Academy export: the embedded _humanoid skeleton and small surfaces.</summary>
internal static class JkaTestData
{
    public static GlaSkeleton HumanoidSkeleton() => GlaSkeleton.Humanoid;

    public static IEnumerable<string> HumanoidNames => GlaSkeleton.Humanoid.Bones.Select(b => b.Name);

    /// <summary>A surface with one triangle. Each vertex follows the given bones with equal weights.</summary>
    public static GlmSurface Triangle(params int[] bones)
    {
        GlmVertex Vertex(float x) => new()
        {
            Position = new Vector3(x, 0, 40), Normal = Vector3.UnitZ, Uv = new Vector2(x, 1 - x),
            Weights = bones.Select(b => new GlmWeight(b, 1f / bones.Length)).ToArray(),
        };
        return new GlmSurface { Vertices = [Vertex(0), Vertex(0.5f), Vertex(1)], Triangles = [0, 1, 2] };
    }
}
