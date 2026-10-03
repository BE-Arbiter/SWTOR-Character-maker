using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace Swtor.Formats.Gr2;

/// <summary>
/// Reads the SWTOR "GAWB" model format. The file is a memory image:
/// pointers are 64-bit offsets from the start of the file.
/// </summary>
public static class Gr2Reader
{
    private const uint Magic = 0x42574147; // "GAWB" read as little-endian
    private const int MeshHeaderSize = 64;
    private const int PieceSize = 48;
    private const int BoneSize = 32;

    /// <summary>Parses a complete file. Throws <see cref="GameFormatException"/> on invalid data.</summary>
    public static Gr2Model Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 0x80 || ReadU32(data, 0) != Magic)
            throw new GameFormatException("Not a GAWB file", 0);

        int version = (int)ReadU32(data, 4);
        int meshCount = ReadU16(data, 0x18);
        int materialCount = ReadU16(data, 0x1A);
        var boundsMin = ReadVec3(data, 0x20);
        var boundsMax = ReadVec3(data, 0x30);
        int meshTable = Pointer(data, 0x58, meshCount * MeshHeaderSize);
        int materialTable = Pointer(data, 0x60, materialCount * 8);

        var materials = new string[materialCount];
        for (int i = 0; i < materialCount; i++)
            materials[i] = ReadString(data, Pointer(data, materialTable + i * 8, 1));

        var meshes = new Gr2Mesh[meshCount];
        for (int i = 0; i < meshCount; i++)
            meshes[i] = ParseMesh(data, meshTable + i * MeshHeaderSize, materialCount);

        return new Gr2Model(version, boundsMin, boundsMax, meshes, materials);
    }

    // Mesh header (64 bytes): name ptr, pad, u16 pieceCount, u16 boneCount, u32 flags,
    // u32 stride, u32 vertexCount, u32 indexCount, then four pointers:
    // vertices, pieces, indices, bones.
    private static Gr2Mesh ParseMesh(ReadOnlySpan<byte> d, int h, int materialCount)
    {
        string name = ReadString(d, Pointer(d, h, 1));
        int pieceCount = ReadU16(d, h + 12);
        int boneCount = ReadU16(d, h + 14);
        var flags = (Gr2VertexFlags)ReadU32(d, h + 16);
        int stride = (int)ReadU32(d, h + 20);
        int vertexCount = (int)ReadU32(d, h + 24);
        int indexCount = (int)ReadU32(d, h + 28);

        if (indexCount % 3 != 0)
            throw new GameFormatException("Index count is not a multiple of 3", h + 28);
        if ((flags & Gr2VertexFlags.Position) == 0)
            throw new GameFormatException("Mesh has no position data", h + 16);
        if (stride != ExpectedStride(flags))
            throw new GameFormatException($"Unknown vertex layout: flags {(int)flags}, stride {stride}", h + 16);

        int vertexOffset = Pointer(d, h + 32, checked(vertexCount * stride));
        int pieceOffset = Pointer(d, h + 40, pieceCount * PieceSize);
        int indexOffset = Pointer(d, h + 48, indexCount * 2);
        int boneOffset = Pointer(d, h + 56, boneCount * BoneSize);

        var vertices = ReadVertices(d.Slice(vertexOffset, vertexCount * stride), flags, stride, vertexCount);

        var indices = new ushort[indexCount];
        for (int i = 0; i < indexCount; i++)
        {
            ushort index = ReadU16(d, indexOffset + i * 2);
            if (index >= vertexCount)
                throw new GameFormatException("Index is outside the vertex range", indexOffset + i * 2);
            indices[i] = index;
        }

        // Piece: u32 startTriangle, u32 triangleCount, u32 materialIndex, u32 unknown, bbox min, bbox max.
        var pieces = new Gr2Piece[pieceCount];
        for (int i = 0; i < pieceCount; i++)
        {
            int p = pieceOffset + i * PieceSize;
            var piece = new Gr2Piece(
                (int)ReadU32(d, p), (int)ReadU32(d, p + 4), (int)ReadU32(d, p + 8),
                ReadVec3(d, p + 16), ReadVec3(d, p + 32));
            if ((long)piece.StartTriangle + piece.TriangleCount > indexCount / 3)
                throw new GameFormatException("Piece is outside the index range", p);
            if (piece.MaterialIndex >= materialCount)
                throw new GameFormatException("Piece uses an unknown material", p + 8);
            pieces[i] = piece;
        }

        // Bone: name ptr (64 bit), bbox min, bbox max of the vertices that bone moves.
        var bones = new Gr2Bone[boneCount];
        for (int i = 0; i < boneCount; i++)
        {
            int b = boneOffset + i * BoneSize;
            bones[i] = new Gr2Bone(ReadString(d, Pointer(d, b, 1)), ReadVec3(d, b + 8), ReadVec3(d, b + 20));
        }

        if (vertices.BoneIndices is { } boneIndices)
        {
            foreach (byte bi in boneIndices)
                if (bi >= boneCount)
                    throw new GameFormatException("Bone index is outside the bone table", vertexOffset);
        }

        return new Gr2Mesh
        {
            Name = name, Flags = flags, VertexCount = vertexCount,
            Positions = vertices.Positions, Normals = vertices.Normals, Tangents = vertices.Tangents,
            Colors = vertices.Colors, UvSets = vertices.UvSets,
            BoneWeights = vertices.BoneWeights, BoneIndices = vertices.BoneIndices,
            Indices = indices, Pieces = pieces, Bones = bones,
        };
    }

    // Attribute order in each vertex: position, skin, normal, tangent, color, UV sets.
    // Bits 2, 4, 8 and 32 always occur together, so we treat them as one group.
    private static int ExpectedStride(Gr2VertexFlags f)
    {
        int size = 12;
        if ((f & Gr2VertexFlags.Skin) != 0) size += 8;
        if ((f & Gr2VertexFlags.Core) == Gr2VertexFlags.Core) size += 12;
        if ((f & Gr2VertexFlags.Color) != 0) size += 4;
        if ((f & Gr2VertexFlags.Uv2) != 0) size += 4;
        if ((f & Gr2VertexFlags.Uv3) != 0) size += 4;
        return size;
    }

    private sealed record VertexData(
        Vector3[] Positions, Vector3[]? Normals, Vector4[]? Tangents, Vector4[]? Colors,
        List<Vector2[]> UvSets, Vector4[]? BoneWeights, byte[]? BoneIndices);

    private static VertexData ReadVertices(ReadOnlySpan<byte> v, Gr2VertexFlags f, int stride, int count)
    {
        bool skin = (f & Gr2VertexFlags.Skin) != 0;
        bool core = (f & Gr2VertexFlags.Core) == Gr2VertexFlags.Core;
        bool color = (f & Gr2VertexFlags.Color) != 0;
        int extraUv = ((f & Gr2VertexFlags.Uv2) != 0 ? 1 : 0) + ((f & Gr2VertexFlags.Uv3) != 0 ? 1 : 0);

        var positions = new Vector3[count];
        var weights = skin ? new Vector4[count] : null;
        var boneIndices = skin ? new byte[count * 4] : null;
        var normals = core ? new Vector3[count] : null;
        var tangents = core ? new Vector4[count] : null;
        var colors = color ? new Vector4[count] : null;
        var uvSets = new List<Vector2[]>();
        for (int i = 0; i < (core ? 1 : 0) + extraUv; i++) uvSets.Add(new Vector2[count]);

        for (int i = 0; i < count; i++)
        {
            var s = v.Slice(i * stride, stride);
            int o = 12;
            positions[i] = ReadVec3(s, 0);
            if (skin)
            {
                weights![i] = new Vector4(s[o], s[o + 1], s[o + 2], s[o + 3]) / 255f;
                s.Slice(o + 4, 4).CopyTo(boneIndices.AsSpan(i * 4, 4));
                o += 8;
            }
            if (core)
            {
                normals![i] = new Vector3(Unorm(s[o]), Unorm(s[o + 1]), Unorm(s[o + 2]));
                // Tangent W is stored as 0 or 255.
                tangents![i] = new Vector4(Unorm(s[o + 4]), Unorm(s[o + 5]), Unorm(s[o + 6]), s[o + 7] > 127 ? 1f : -1f);
                o += 8;
            }
            if (color)
            {
                colors![i] = new Vector4(s[o], s[o + 1], s[o + 2], s[o + 3]) / 255f;
                o += 4;
            }
            for (int u = 0; u < uvSets.Count; u++)
            {
                uvSets[u][i] = new Vector2(ReadHalf(s, o), ReadHalf(s, o + 2));
                o += 4;
            }
        }

        return new VertexData(positions, normals, tangents, colors, uvSets, weights, boneIndices);
    }

    private static float Unorm(byte b) => b / 255f * 2f - 1f;

    private static float ReadHalf(ReadOnlySpan<byte> d, int o) =>
        (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(d[o..]));

    private static uint ReadU32(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadUInt32LittleEndian(d[o..]);

    private static ushort ReadU16(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadUInt16LittleEndian(d[o..]);

    private static Vector3 ReadVec3(ReadOnlySpan<byte> d, int o) =>
        new(BinaryPrimitives.ReadSingleLittleEndian(d[o..]),
            BinaryPrimitives.ReadSingleLittleEndian(d[(o + 4)..]),
            BinaryPrimitives.ReadSingleLittleEndian(d[(o + 8)..]));

    // Reads a 64-bit pointer and checks that `size` bytes at the target are inside the file.
    private static int Pointer(ReadOnlySpan<byte> d, int at, int size)
    {
        ulong target = BinaryPrimitives.ReadUInt64LittleEndian(d[at..]);
        if (target + (ulong)Math.Max(size, 0) > (ulong)d.Length)
            throw new GameFormatException("Pointer is outside the file", at);
        return (int)target;
    }

    private static string ReadString(ReadOnlySpan<byte> d, int offset)
    {
        int end = d[offset..].IndexOf((byte)0);
        if (end < 0) throw new GameFormatException("String has no terminator", offset);
        return Encoding.UTF8.GetString(d.Slice(offset, end));
    }
}
