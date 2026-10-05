using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace Swtor.Formats.Jka;

/// <summary>One bone of a Jedi Academy skeleton (.gla file).</summary>
/// <param name="BasePose">Bind pose in model space. A 3x4 matrix, row major: three rows of (axis x, axis y, axis z, translation).</param>
/// <param name="BasePoseInverse">Inverse of the bind pose, same layout.</param>
public sealed record GlaBone(string Name, int Parent, float[] BasePose, float[] BasePoseInverse, int[] Children)
{
    /// <summary>Bone origin in model space (game units, Z up).</summary>
    public Vector3 Origin => new(BasePose[3], BasePose[7], BasePose[11]);
}

/// <summary>
/// The bone list of a Jedi Academy skeleton. Only the bind pose is read: animations are not needed to build a model.
/// </summary>
public sealed class GlaSkeleton
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static GlaSkeleton? _humanoid;

    public required string Name { get; init; }
    public required IReadOnlyList<GlaBone> Bones { get; init; }

    /// <summary>
    /// The bind pose of the Jedi Academy <c>_humanoid</c> skeleton (53 bones), embedded in this library
    /// (<c>jka_humanoid.json</c>, made with <see cref="ToJson"/>). Exports do not need the game files.
    /// </summary>
    public static GlaSkeleton Humanoid => _humanoid ??= LoadHumanoid();

    private static GlaSkeleton LoadHumanoid()
    {
        using var stream = typeof(GlaSkeleton).Assembly.GetManifestResourceStream("jka_humanoid.json")
            ?? throw new InvalidDataException("The embedded resource jka_humanoid.json is missing.");
        return JsonSerializer.Deserialize<GlaSkeleton>(stream, Json) ?? throw new InvalidDataException("The embedded skeleton is empty.");
    }

    /// <summary>The skeleton as JSON with one bone on each line.</summary>
    public string ToJson() =>
        $"{{\"name\":{JsonSerializer.Serialize(Name)},\"bones\":[\n"
        + string.Join(",\n", Bones.Select(b => "  " + JsonSerializer.Serialize(b, Json)))
        + "\n]}\n";

    /// <summary>Index of the bone with this name (case-insensitive), or -1.</summary>
    public int IndexOf(string name)
    {
        for (int i = 0; i < Bones.Count; i++)
            if (Bones[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    // Header: ident, version, name[64], scale, numFrames, ofsFrames, numBones, ofsCompBonePool, ofsSkel, ofsEnd.
    // After the header: one int offset per bone, relative to the end of the header. A bone is: name[64], flags, parent,
    // base pose (12 floats), inverse base pose (12 floats), numChildren, children[].
    public static GlaSkeleton Parse(ReadOnlySpan<byte> d)
    {
        if (d.Length < 100 || !d[..4].SequenceEqual("2LGA"u8))
            throw new GameFormatException("Not a Ghoul2 animation file (.gla)", 0);
        int version = Int(d, 4);
        if (version != 6) throw new GameFormatException($"Unsupported .gla version {version}", 4);

        string name = Text(d, 8);
        int numBones = Int(d, 84);
        const int headerSize = 100;
        if (numBones is < 1 or > 4096) throw new GameFormatException("Bad bone count", 84);
        if (headerSize + numBones * 4 > d.Length) throw new GameFormatException("Bad skeleton offset", headerSize);

        var bones = new GlaBone[numBones];
        for (int i = 0; i < numBones; i++)
        {
            int at = headerSize + Int(d, headerSize + i * 4);
            if (at < 0 || at + 64 + 8 + 96 + 4 > d.Length) throw new GameFormatException("Bone is outside the file", at);
            var pose = new float[12];
            var inverse = new float[12];
            for (int k = 0; k < 12; k++)
            {
                pose[k] = BinaryPrimitives.ReadSingleLittleEndian(d[(at + 72 + k * 4)..]);
                inverse[k] = BinaryPrimitives.ReadSingleLittleEndian(d[(at + 72 + 48 + k * 4)..]);
            }
            int childCount = Int(d, at + 72 + 96);
            if (childCount < 0 || at + 72 + 96 + 4 + childCount * 4 > d.Length) throw new GameFormatException("Bad child count", at);
            var children = new int[childCount];
            for (int k = 0; k < childCount; k++) children[k] = Int(d, at + 72 + 96 + 4 + k * 4);
            bones[i] = new GlaBone(Text(d, at), Int(d, at + 68), pose, inverse, children);
        }
        return new GlaSkeleton { Name = name, Bones = bones };
    }

    private static int Int(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadInt32LittleEndian(d[o..]);

    private static string Text(ReadOnlySpan<byte> d, int o)
    {
        var s = d.Slice(o, 64);
        int end = s.IndexOf((byte)0);
        return Encoding.ASCII.GetString(end < 0 ? s : s[..end]);
    }
}
