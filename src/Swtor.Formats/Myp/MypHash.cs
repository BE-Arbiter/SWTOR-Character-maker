using System.Buffers.Binary;
using System.Text;

namespace Swtor.Formats.Myp;

/// <summary>
/// Hash of a file name in a .tor archive. The archive stores no names, only this 64-bit hash.
/// It is the "hashlittle2" function of Bob Jenkins (lookup3) with both seeds at 0, over the ASCII bytes of the path.
/// The high 32 bits are the secondary value (b), the low 32 bits are the primary value (c).
/// This is the order of the archive tables (checked against real files).
/// </summary>
public static class MypHash
{
    /// <summary>Hashes a path such as <c>/resources/art/dynamic/head/model/head.gr2</c>. Case matters: the game uses lower case.</summary>
    public static ulong Compute(string path) => Compute(Encoding.ASCII.GetBytes(path));

    public static ulong Compute(ReadOnlySpan<byte> key)
    {
        uint a, b, c;
        a = b = c = 0xDEADBEEFu + (uint)key.Length;

        while (key.Length > 12)
        {
            a += BinaryPrimitives.ReadUInt32LittleEndian(key);
            b += BinaryPrimitives.ReadUInt32LittleEndian(key[4..]);
            c += BinaryPrimitives.ReadUInt32LittleEndian(key[8..]);
            Mix(ref a, ref b, ref c);
            key = key[12..];
        }

        if (key.Length == 0) return ((ulong)b << 32) | c;

        // The last 1 to 12 bytes fill a, b and c in little-endian order, zero padded.
        Span<byte> tail = stackalloc byte[12];
        key.CopyTo(tail);
        a += BinaryPrimitives.ReadUInt32LittleEndian(tail);
        b += BinaryPrimitives.ReadUInt32LittleEndian(tail[4..]);
        c += BinaryPrimitives.ReadUInt32LittleEndian(tail[8..]);
        Final(ref a, ref b, ref c);
        return ((ulong)b << 32) | c;
    }

    private static uint Rot(uint x, int k) => (x << k) | (x >> (32 - k));

    private static void Mix(ref uint a, ref uint b, ref uint c)
    {
        a -= c; a ^= Rot(c, 4); c += b;
        b -= a; b ^= Rot(a, 6); a += c;
        c -= b; c ^= Rot(b, 8); b += a;
        a -= c; a ^= Rot(c, 16); c += b;
        b -= a; b ^= Rot(a, 19); a += c;
        c -= b; c ^= Rot(b, 4); b += a;
    }

    private static void Final(ref uint a, ref uint b, ref uint c)
    {
        c ^= b; c -= Rot(b, 14);
        a ^= c; a -= Rot(c, 11);
        b ^= a; b -= Rot(a, 25);
        c ^= b; c -= Rot(b, 16);
        a ^= c; a -= Rot(c, 4);
        b ^= a; b -= Rot(a, 14);
        c ^= b; c -= Rot(b, 24);
    }
}
