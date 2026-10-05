using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Swtor.Assets;
using Swtor.Formats;
using Swtor.Formats.Myp;
using ZstdSharp;

namespace Swtor.Tests;

public class MypTests
{
    [Fact]
    public void Hash_MatchesValuesFoundInARealArchive()
    {
        // Both values were read from the tables of swtor_main_art_dynamic_head_1.tor.
        Assert.Equal(0x58C216D26EAF2B8AUL, MypHash.Compute("/resources/art/dynamic/head/index.xml"));
        Assert.Equal(0xAE434BCF2074B64AUL, MypHash.Compute("/resources/art/dynamic/head/model/bmn_fenzeil_a02.gr2"));
    }

    [Fact]
    public void Hash_EmptyNameIsTheSeed() => Assert.Equal(0xDEADBEEFDEADBEEFUL, MypHash.Compute(""));

    // Builds an archive with one table: header, then per file a 36 byte file header and its data, then the table.
    private static byte[] Build(params (string Name, byte[] Stored, int Size, ushort Compression)[] files)
    {
        const int capacity = 4;
        var ms = new MemoryStream();
        ms.Write(new byte[32]);
        var entries = new List<(long Offset, int Stored, int Size, ulong Hash, ushort Compression)>();
        foreach (var f in files)
        {
            entries.Add((ms.Position, f.Stored.Length, f.Size, MypHash.Compute(f.Name), f.Compression));
            ms.Write(new byte[36]);
            ms.Write(f.Stored);
        }
        long table = ms.Position;
        var block = new byte[12 + capacity * 34];
        BinaryPrimitives.WriteInt32LittleEndian(block, entries.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            var e = block.AsSpan(12 + i * 34, 34);
            BinaryPrimitives.WriteInt64LittleEndian(e, entries[i].Offset);
            BinaryPrimitives.WriteInt32LittleEndian(e[8..], 36);
            BinaryPrimitives.WriteInt32LittleEndian(e[12..], entries[i].Stored);
            BinaryPrimitives.WriteInt32LittleEndian(e[16..], entries[i].Size);
            BinaryPrimitives.WriteUInt64LittleEndian(e[20..], entries[i].Hash);
            BinaryPrimitives.WriteUInt16LittleEndian(e[32..], entries[i].Compression);
        }
        ms.Write(block);
        var bytes = ms.ToArray();
        Encoding.ASCII.GetBytes("MYP\0").CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 6);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 0xFD23EC43);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(12), table);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(20), capacity);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24), entries.Count);
        return bytes;
    }

    private static byte[] Zlib(byte[] data)
    {
        var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data);
        return ms.ToArray();
    }

    private static string WriteTemp(byte[] data)
    {
        string path = Path.Combine(Path.GetTempPath(), $"swtor_test_{Guid.NewGuid():N}.tor");
        File.WriteAllBytes(path, data);
        return path;
    }

    [Fact]
    public void Archive_ReadsStoredZstdAndZlibFiles()
    {
        byte[] plain = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("hello swtor ", 50)));
        using var compressor = new Compressor();
        byte[] zstd = compressor.Wrap(plain).ToArray();
        string path = WriteTemp(Build(
            ("/resources/a.txt", plain, plain.Length, 0),
            ("/resources/b.txt", zstd, plain.Length, 1),
            ("/resources/c.txt", Zlib(plain), plain.Length, 1)));
        try
        {
            using var archive = MypArchive.Open(path);
            Assert.Equal(3, archive.Entries.Count);
            Assert.Equal(6u, archive.Version);
            foreach (var entry in archive.Entries) Assert.Equal(plain, archive.Read(entry));
            Assert.Equal(MypHash.Compute("/resources/b.txt"), archive.Entries[1].Hash);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Archive_RejectsWrongMagic()
    {
        string path = WriteTemp(new byte[64]);
        try { Assert.Throws<GameFormatException>(() => MypArchive.Open(path)); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Archive_RejectsDataOutsideTheFile()
    {
        byte[] data = Build(("/resources/a.txt", [1, 2, 3], 3, 0));
        byte[] cut = data[..(data.Length - 12 - 4 * 34 + 20)]; // table cut in the middle
        string path = WriteTemp(cut);
        try { Assert.Throws<GameFormatException>(() => MypArchive.Open(path)); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Extractor_WritesNamedFilesUnderTheRootAndOthersToUnknown()
    {
        byte[] known = Encoding.ASCII.GetBytes("<?xml version='1.0'?><a/>");
        byte[] other = "DDS "u8.ToArray();
        string tor = WriteTemp(Build(
            ("/resources/art/x/index.xml", known, known.Length, 0),
            ("/resources/secret.dds", other, other.Length, 0),
            ("/resources/../evil.txt", [1], 1, 0)));
        string outRoot = Path.Combine(Path.GetTempPath(), $"swtor_out_{Guid.NewGuid():N}");
        try
        {
            var names = new TorNames();
            names.Add("art/x/index.xml");
            names.Add("/resources/../evil.txt");
            var result = TorExtractor.Extract([tor], outRoot, names);

            Assert.Equal(3, result.Files);
            Assert.Equal(known, File.ReadAllBytes(Path.Combine(outRoot, "art", "x", "index.xml")));
            Assert.True(File.Exists(Path.Combine(outRoot, "_unknown", $"{MypHash.Compute("/resources/secret.dds"):X16}.dds")));
            // The name with ".." must not leave the output folder.
            Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(outRoot)!, "evil.txt")));

            var again = TorExtractor.Extract([tor], outRoot, names);
            Assert.Equal(3, again.Skipped);
        }
        finally
        {
            File.Delete(tor);
            if (Directory.Exists(outRoot)) Directory.Delete(outRoot, true);
        }
    }
}
