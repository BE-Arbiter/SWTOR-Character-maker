using System.Numerics;

namespace Swtor.Formats.Jka;

/// <summary>One bone influence on a vertex. <see cref="Bone"/> is an index into the .gla skeleton (not into the surface bone list).</summary>
public readonly record struct GlmWeight(int Bone, float Weight);

/// <summary>A vertex of a Jedi Academy mesh. Coordinates are in game units (Z up). One to four weights.</summary>
public sealed class GlmVertex
{
    public Vector3 Position;
    public Vector3 Normal;
    public Vector2 Uv;
    public GlmWeight[] Weights = [];
}

/// <summary>Triangles and vertices of one surface in one level of detail.</summary>
public sealed class GlmSurface
{
    public required GlmVertex[] Vertices { get; init; }

    /// <summary>Three vertex indices per triangle.</summary>
    public required int[] Triangles { get; init; }
}

/// <summary>Surface name, parent and flags. Same for all levels of detail.</summary>
public sealed class GlmSurfaceInfo
{
    /// <summary>Tag surface: the game reads its position and direction (for bolts such as a weapon in the hand).</summary>
    public const uint FlagBolt = 0x1;

    /// <summary>The surface is not drawn.</summary>
    public const uint FlagOff = 0x2;

    /// <summary>The surface and all its children are not drawn when it is off.</summary>
    public const uint FlagNoDescendants = 0x100;

    public required string Name { get; set; }
    public uint Flags { get; set; }
    public string Shader { get; set; } = "[nomaterial]";
    public int ShaderIndex { get; set; }
    public int Parent { get; set; } = -1;
    public List<int> Children { get; } = [];
}

/// <summary>A Jedi Academy mesh file (.glm). The bones live in the .gla file that <see cref="AnimationName"/> names.</summary>
public sealed class GlmModel
{
    public string Name { get; set; } = "model";
    public string AnimationName { get; set; } = "models/players/_humanoid/_humanoid";
    public int BoneCount { get; set; }
    public List<GlmSurfaceInfo> Surfaces { get; } = [];

    /// <summary>One list per level of detail. Each list has one entry per surface of <see cref="Surfaces"/>.</summary>
    public List<GlmSurface[]> Lods { get; } = [];

    public int SurfaceIndex(string name) => Surfaces.FindIndex(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}
