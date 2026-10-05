using System.Buffers.Binary;
using System.Text;

namespace Swtor.Formats.Jka;

/// <summary>
/// Writes Jedi Academy mesh files (.glm). The layout is the one of the files that the game tools write:
/// header, surface hierarchy, then for each level of detail the surfaces. Each surface holds its header,
/// triangles, vertices, texture coordinates and bone references, in this order.
/// </summary>
public static class GlmWriter
{
    private const int SurfaceHeaderSize = 40;
    private const int MaxBoneReferences = 32;
    private const int MaxWeights = 4;

    public static byte[] Write(GlmModel model)
    {
        int surfaceCount = model.Surfaces.Count;
        foreach (var lod in model.Lods)
            if (lod.Length != surfaceCount) throw new ArgumentException("Every level of detail needs one entry for each surface.");

        using var stream = new MemoryStream();
        var w = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);

        // Header. The offsets that are not known yet are written again at the end.
        w.Write("2LGM"u8);
        w.Write(6);
        WriteName(w, model.Name);
        WriteName(w, model.AnimationName);
        w.Write(0);                    // animIndex
        w.Write(model.BoneCount);
        w.Write(model.Lods.Count);
        w.Write(0);                    // ofsLODs, set later
        w.Write(surfaceCount);
        w.Write(0);                    // ofsSurfHierarchy, set later
        w.Write(0);                    // ofsEnd, set later

        // Hierarchy: offsets (relative to the end of the header), then the entries.
        var entrySizes = model.Surfaces.Select(s => 144 + 4 * s.Children.Count).ToArray();
        int offset = surfaceCount * 4;
        for (int i = 0; i < surfaceCount; i++)
        {
            w.Write(offset);
            offset += entrySizes[i];
        }
        int hierarchyOffset = (int)stream.Position;
        foreach (var s in model.Surfaces)
        {
            WriteName(w, s.Name);
            w.Write((int)s.Flags);
            WriteName(w, s.Shader);
            w.Write(s.ShaderIndex);
            w.Write(s.Parent);
            w.Write(s.Children.Count);
            foreach (int child in s.Children) w.Write(child);
        }

        int lodOffset = (int)stream.Position;
        foreach (var lod in model.Lods) WriteLod(w, stream, lod);
        int end = (int)stream.Position;

        var bytes = stream.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(148), lodOffset);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(156), hierarchyOffset);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(160), end);
        return bytes;
    }

    // LOD: int ofsEnd, then one int offset per surface (relative to the end of the ofsEnd field), then the surfaces.
    private static void WriteLod(BinaryWriter w, MemoryStream stream, GlmSurface[] surfaces)
    {
        int lodStart = (int)stream.Position;
        w.Write(0); // ofsEnd, set later

        var bodies = new byte[surfaces.Length][];
        for (int i = 0; i < surfaces.Length; i++) bodies[i] = BuildSurface(surfaces[i], i);

        int offset = surfaces.Length * 4;
        for (int i = 0; i < surfaces.Length; i++)
        {
            w.Write(offset);
            offset += bodies[i].Length;
        }
        for (int i = 0; i < surfaces.Length; i++)
        {
            // ofsHeader is the distance back to the start of the file.
            int surfaceStart = (int)stream.Position;
            BinaryPrimitives.WriteInt32LittleEndian(bodies[i].AsSpan(8), -surfaceStart);
            w.Write(bodies[i]);
        }
        int lodEnd = (int)stream.Position;
        long keep = stream.Position;
        stream.Position = lodStart;
        w.Write(lodEnd - lodStart);
        stream.Position = keep;
    }

    // Surface header (40 bytes), triangles, vertices, texture coordinates, bone references. See GlmReader for the fields.
    // Every surface goes through the triangles-per-vertex limit here, so no path (new model, added surfaces, surfaces kept
    // from an existing model, reduced model) can write a vertex that breaks the stencil shadow of the game.
    private static byte[] BuildSurface(GlmSurface surface, int index)
    {
        surface = JkaConverter.LimitTrianglesPerVertex(surface);
        var vertices = surface.Vertices;
        if (vertices.Length > JkaConverter.MaxVerticesPerSurface)
            throw new InvalidOperationException($"A surface has {vertices.Length} vertices. The game allows {JkaConverter.MaxVerticesPerSurface}.");
        var boneRefs = CollectBoneReferences(vertices);

        using var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        int triangleBytes = surface.Triangles.Length * 4;
        int vertexBytes = vertices.Sum(v => (28 + v.Weights.Length + 3) & ~3);
        int ofsTriangles = SurfaceHeaderSize;
        int ofsVertices = ofsTriangles + triangleBytes;
        int ofsBoneRefs = ofsVertices + vertexBytes + vertices.Length * 8;
        int ofsEnd = ofsBoneRefs + boneRefs.Count * 4;

        w.Write(0);                          // ident
        w.Write(index);
        w.Write(0);                          // ofsHeader, set by the caller when the position is known
        w.Write(vertices.Length);
        w.Write(ofsVertices);
        w.Write(surface.Triangles.Length / 3);
        w.Write(ofsTriangles);
        w.Write(boneRefs.Count);
        w.Write(ofsBoneRefs);
        w.Write(ofsEnd);

        foreach (int t in surface.Triangles) w.Write(t);

        var refIndex = new Dictionary<int, int>();
        for (int i = 0; i < boneRefs.Count; i++) refIndex[boneRefs[i]] = i;
        foreach (var v in vertices)
        {
            var weights = Quantize(v.Weights);
            w.Write(v.Normal.X); w.Write(v.Normal.Y); w.Write(v.Normal.Z);
            w.Write(v.Position.X); w.Write(v.Position.Y); w.Write(v.Position.Z);

            uint packed = (uint)(weights.Length - 1) << 30;
            for (int k = 0; k < weights.Length; k++)
            {
                packed |= (uint)refIndex[weights[k].Bone] << (k * 5);
                packed |= (uint)(weights[k].Raw >> 8) << (20 + 2 * k);
            }
            w.Write(packed);
            for (int k = 0; k < weights.Length; k++) w.Write((byte)(weights[k].Raw & 0xFF));
            for (int pad = (28 + weights.Length + 3 & ~3) - (28 + weights.Length); pad > 0; pad--) w.Write((byte)0);
        }
        foreach (var v in vertices) { w.Write(v.Uv.X); w.Write(v.Uv.Y); }
        foreach (int bone in boneRefs) w.Write(bone);
        return ms.ToArray();
    }

    // Distinct bones of a surface in order of first use. The format stores each bone reference in 5 bits.
    private static List<int> CollectBoneReferences(GlmVertex[] vertices)
    {
        var refs = new List<int>();
        var seen = new HashSet<int>();
        foreach (var v in vertices)
            foreach (var weight in v.Weights)
                if (seen.Add(weight.Bone)) refs.Add(weight.Bone);
        if (refs.Count > MaxBoneReferences)
            throw new InvalidOperationException($"A surface uses {refs.Count} bones. The format allows {MaxBoneReferences}.");
        return refs;
    }

    private readonly record struct QuantizedWeight(int Bone, int Raw);

    // Weights have 10 bits (0 to 1023) and must add up to 1023. The largest weight takes the rounding error.
    private static QuantizedWeight[] Quantize(GlmWeight[] weights)
    {
        if (weights.Length is < 1 or > MaxWeights) throw new InvalidOperationException("A vertex needs one to four weights.");
        float total = weights.Sum(x => x.Weight);
        var result = new QuantizedWeight[weights.Length];
        int sum = 0, largest = 0;
        for (int i = 0; i < weights.Length; i++)
        {
            int raw = total > 0 ? (int)MathF.Round(weights[i].Weight / total * 1023f) : (i == 0 ? 1023 : 0);
            result[i] = new QuantizedWeight(weights[i].Bone, Math.Clamp(raw, 0, 1023));
            sum += result[i].Raw;
            if (result[i].Raw > result[largest].Raw) largest = i;
        }
        result[largest] = result[largest] with { Raw = result[largest].Raw + 1023 - sum };
        return result;
    }

    private static void WriteName(BinaryWriter w, string name)
    {
        var bytes = new byte[64];
        int length = Encoding.ASCII.GetBytes(name, bytes.AsSpan(0, 63));
        if (length < name.Length) throw new ArgumentException($"Name is too long: {name}");
        w.Write(bytes);
    }
}
