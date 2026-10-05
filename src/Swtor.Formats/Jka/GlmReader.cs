using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace Swtor.Formats.Jka;

/// <summary>
/// Reads Jedi Academy mesh files (.glm, ident "2LGM", version 6). All offsets are relative, see the comments.
/// </summary>
public static class GlmReader
{
    private const int HeaderSize = 164;
    private const int SurfaceHeaderSize = 40;

    public static GlmModel Parse(ReadOnlySpan<byte> d)
    {
        if (d.Length < HeaderSize || !d[..4].SequenceEqual("2LGM"u8))
            throw new GameFormatException("Not a Ghoul2 mesh file (.glm)", 0);
        int version = Int(d, 4);
        if (version != 6) throw new GameFormatException($"Unsupported .glm version {version}", 4);

        // Header: ident, version, name[64], animName[64], animIndex, numBones, numLODs, ofsLODs,
        // numSurfaces, ofsSurfHierarchy, ofsEnd.
        var model = new GlmModel { Name = Text(d, 8), AnimationName = Text(d, 72), BoneCount = Int(d, 140) };
        int lodCount = Int(d, 144), lodOffset = Int(d, 148), surfaceCount = Int(d, 152);
        if (surfaceCount is < 0 or > 1024 || lodCount is < 0 or > 16) throw new GameFormatException("Bad surface or LOD count", 144);

        // Hierarchy: one int offset per surface (relative to the end of the header), then the entries:
        // name[64], flags, shader[64], shaderIndex, parent, numChildren, children[].
        for (int i = 0; i < surfaceCount; i++)
        {
            int at = HeaderSize + Int(d, HeaderSize + i * 4);
            Check(d, at, 144, "surface hierarchy");
            var info = new GlmSurfaceInfo
            {
                Name = Text(d, at), Flags = (uint)Int(d, at + 64), Shader = Text(d, at + 68),
                ShaderIndex = Int(d, at + 132), Parent = Int(d, at + 136),
            };
            int children = Int(d, at + 140);
            Check(d, at + 144, children * 4, "child list");
            for (int k = 0; k < children; k++) info.Children.Add(Int(d, at + 144 + k * 4));
            model.Surfaces.Add(info);
        }

        // LOD: int ofsEnd, then one int offset per surface (relative to the end of the ofsEnd field).
        int lodAt = lodOffset;
        for (int lod = 0; lod < lodCount; lod++)
        {
            Check(d, lodAt, 4 + surfaceCount * 4, "LOD");
            int table = lodAt + 4;
            var surfaces = new GlmSurface[surfaceCount];
            for (int i = 0; i < surfaceCount; i++)
                surfaces[i] = ReadSurface(d, table + Int(d, table + i * 4));
            model.Lods.Add(surfaces);
            lodAt += Int(d, lodAt);
        }
        return model;
    }

    // Surface header (40 bytes): ident, thisSurfaceIndex, ofsHeader (negative, back to the file header),
    // numVerts, ofsVerts, numTriangles, ofsTriangles, numBoneReferences, ofsBoneReferences, ofsEnd.
    // Offsets are relative to the start of the surface.
    private static GlmSurface ReadSurface(ReadOnlySpan<byte> d, int s)
    {
        Check(d, s, SurfaceHeaderSize, "surface");
        int vertexCount = Int(d, s + 12), vertexOffset = s + Int(d, s + 16);
        int triangleCount = Int(d, s + 20), triangleOffset = s + Int(d, s + 24);
        int boneRefCount = Int(d, s + 28), boneRefOffset = s + Int(d, s + 32);
        Check(d, triangleOffset, triangleCount * 12, "triangles");
        Check(d, boneRefOffset, boneRefCount * 4, "bone references");
        if (vertexCount < 0) throw new GameFormatException("Negative vertex count", s + 12);

        var boneRefs = new int[boneRefCount];
        for (int i = 0; i < boneRefCount; i++) boneRefs[i] = Int(d, boneRefOffset + i * 4);

        var triangles = new int[triangleCount * 3];
        for (int i = 0; i < triangles.Length; i++)
        {
            triangles[i] = Int(d, triangleOffset + i * 4);
            if ((uint)triangles[i] >= (uint)vertexCount) throw new GameFormatException("Triangle index is outside the vertex range", triangleOffset + i * 4);
        }

        // Vertex: normal, position, packed word, then one byte per weight, padded to a multiple of 4 bytes.
        // Texture coordinates (8 bytes each) follow all vertices.
        // Packed word: bits 30-31 = weight count - 1; bits 5*i = bone reference of weight i;
        // bits 20 + 2*i = the two high bits of weight i (the low 8 bits are in the byte array). Weights have 10 bits.
        var vertices = new GlmVertex[vertexCount];
        int p = vertexOffset;
        for (int i = 0; i < vertexCount; i++)
        {
            Check(d, p, 28, "vertex");
            uint packed = (uint)Int(d, p + 24);
            int count = (int)(packed >> 30) + 1;
            Check(d, p + 28, count, "vertex weights");
            var weights = new GlmWeight[count];
            for (int w = 0; w < count; w++)
            {
                int reference = (int)(packed >> (w * 5)) & 31;
                if (reference >= boneRefCount) throw new GameFormatException("Bone reference is outside the surface list", p + 24);
                int raw = d[p + 28 + w] | (((int)(packed >> (20 + 2 * w)) & 3) << 8);
                weights[w] = new GlmWeight(boneRefs[reference], raw / 1023f);
            }
            vertices[i] = new GlmVertex { Normal = Vec3(d, p), Position = Vec3(d, p + 12), Weights = weights };
            p += (28 + count + 3) & ~3;
        }
        Check(d, p, vertexCount * 8, "texture coordinates");
        for (int i = 0; i < vertexCount; i++)
            vertices[i].Uv = new Vector2(BinaryPrimitives.ReadSingleLittleEndian(d[(p + i * 8)..]), BinaryPrimitives.ReadSingleLittleEndian(d[(p + i * 8 + 4)..]));

        return new GlmSurface { Vertices = vertices, Triangles = triangles };
    }

    private static void Check(ReadOnlySpan<byte> d, int at, int size, string what)
    {
        if (at < 0 || size < 0 || (long)at + size > d.Length) throw new GameFormatException($"{what} is outside the file", at);
    }

    private static int Int(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadInt32LittleEndian(d[o..]);

    private static Vector3 Vec3(ReadOnlySpan<byte> d, int o) =>
        new(BinaryPrimitives.ReadSingleLittleEndian(d[o..]), BinaryPrimitives.ReadSingleLittleEndian(d[(o + 4)..]), BinaryPrimitives.ReadSingleLittleEndian(d[(o + 8)..]));

    private static string Text(ReadOnlySpan<byte> d, int o)
    {
        var s = d.Slice(o, 64);
        int end = s.IndexOf((byte)0);
        return Encoding.ASCII.GetString(end < 0 ? s : s[..end]);
    }
}
