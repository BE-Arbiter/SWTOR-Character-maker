using System.Numerics;

namespace Swtor.Formats.Gr2;

/// <summary>Parsed SWTOR model file ("GAWB" format, used for .gr2 files).</summary>
public sealed record Gr2Model(
    int Version,
    Vector3 BoundsMin,
    Vector3 BoundsMax,
    IReadOnlyList<Gr2Mesh> Meshes,
    IReadOnlyList<string> Materials);

/// <summary>One mesh. Vertex arrays that the file does not store are null.</summary>
public sealed class Gr2Mesh
{
    public required string Name { get; init; }
    public required Gr2VertexFlags Flags { get; init; }
    public required int VertexCount { get; init; }
    public required Vector3[] Positions { get; init; }
    public Vector3[]? Normals { get; init; }

    /// <summary>XYZ is the tangent. W is the handedness sign (+1 or -1).</summary>
    public Vector4[]? Tangents { get; init; }
    public Vector4[]? Colors { get; init; }

    /// <summary>UV sets in file order. Index 0 is the main set.</summary>
    public required IReadOnlyList<Vector2[]> UvSets { get; init; }

    /// <summary>Four weights per vertex. Each weight is in the range 0 to 1.</summary>
    public Vector4[]? BoneWeights { get; init; }

    /// <summary>Four indices per vertex (flat array). Each index points into <see cref="Bones"/>.</summary>
    public byte[]? BoneIndices { get; init; }

    /// <summary>Triangle list. Three indices per triangle.</summary>
    public required ushort[] Indices { get; init; }
    public required IReadOnlyList<Gr2Piece> Pieces { get; init; }
    public required IReadOnlyList<Gr2Bone> Bones { get; init; }
}

/// <summary>A range of triangles that use one material.</summary>
public readonly record struct Gr2Piece(
    int StartTriangle,
    int TriangleCount,
    int MaterialIndex,
    Vector3 BoundsMin,
    Vector3 BoundsMax);

/// <summary>Entry of the mesh bone table. The skeleton file holds the bone hierarchy.</summary>
public readonly record struct Gr2Bone(string Name, Vector3 BoundsMin, Vector3 BoundsMax);

/// <summary>Vertex attribute bits from the mesh header.</summary>
[Flags]
public enum Gr2VertexFlags
{
    Position = 1,

    // Bits 2, 4, 8 and 32 always occur together: normal, tangent and main UV.
    Core = 2 | 4 | 8 | 32,
    Color = 16,
    Uv2 = 64,
    Uv3 = 128,
    Skin = 256,
}
