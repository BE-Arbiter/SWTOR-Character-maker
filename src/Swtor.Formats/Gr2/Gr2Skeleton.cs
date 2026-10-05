using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace Swtor.Formats.Gr2;

/// <summary>One bone of a SWTOR skeleton file.</summary>
/// <param name="Parent">Index of the parent bone, or -1 for the root.</param>
/// <param name="Local">Matrix of the bone relative to its parent (row vectors, translation in the last row).</param>
/// <param name="World">Matrix of the bone in model space, in the bind pose (the inverse of the stored matrix; includes the root rotation of 180 degrees around Y that the files have).</param>
/// <param name="InverseBind">Inverse bind matrix, as stored in the file.</param>
public sealed record Gr2Bone2(string Name, int Parent, Matrix4x4 Local, Matrix4x4 World, Matrix4x4 InverseBind);

/// <summary>The bone list of a SWTOR skeleton (<c>art/dynamic/spec/bmanew_skeleton.gr2</c> and similar). Same "GAWB" container as the models.</summary>
public sealed class Gr2Skeleton
{
    private const uint Magic = 0x42574147; // "GAWB"
    private const int FirstBone = 0x80;
    private const int BoneSize = 144;

    public required IReadOnlyList<Gr2Bone2> Bones { get; init; }

    public int IndexOf(string name)
    {
        for (int i = 0; i < Bones.Count; i++)
            if (Bones[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    // Header: magic, version, ... u32 bone count at 0x1C. The bone table starts at 0x80.
    // Bone (144 bytes): u64 name pointer, i32 parent, u32 unknown, the local matrix and the inverse bind matrix (4x4 floats each).
    public static Gr2Skeleton Parse(ReadOnlySpan<byte> d)
    {
        if (d.Length < FirstBone || BinaryPrimitives.ReadUInt32LittleEndian(d) != Magic)
            throw new GameFormatException("Not a GAWB file", 0);
        int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(d[0x1C..]);
        if (count is < 1 or > 1024 || FirstBone + (long)count * BoneSize > d.Length)
            throw new GameFormatException("Bad bone count", 0x1C);

        var bones = new Gr2Bone2[count];
        for (int i = 0; i < count; i++)
        {
            int at = FirstBone + i * BoneSize;
            ulong namePointer = BinaryPrimitives.ReadUInt64LittleEndian(d[at..]);
            if (namePointer >= (ulong)d.Length) throw new GameFormatException("Name pointer is outside the file", at);
            int end = d[(int)namePointer..].IndexOf((byte)0);
            string name = Encoding.UTF8.GetString(d.Slice((int)namePointer, end < 0 ? d.Length - (int)namePointer : end));
            int parent = BinaryPrimitives.ReadInt32LittleEndian(d[(at + 8)..]);
            if (parent >= i) throw new GameFormatException("Parent bone comes after its child", at + 8);
            var local = ReadMatrix(d, at + 16);
            var inverse = ReadMatrix(d, at + 80);
            if (!Matrix4x4.Invert(inverse, out var world)) throw new GameFormatException("Inverse bind matrix is not invertible", at + 80);
            bones[i] = new Gr2Bone2(name, parent, local, world, inverse);
        }
        return new Gr2Skeleton { Bones = bones };
    }

    private static Matrix4x4 ReadMatrix(ReadOnlySpan<byte> d, int o)
    {
        Span<float> f = stackalloc float[16];
        for (int i = 0; i < 16; i++) f[i] = BinaryPrimitives.ReadSingleLittleEndian(d[(o + i * 4)..]);
        return new Matrix4x4(f[0], f[1], f[2], f[3], f[4], f[5], f[6], f[7], f[8], f[9], f[10], f[11], f[12], f[13], f[14], f[15]);
    }
}
