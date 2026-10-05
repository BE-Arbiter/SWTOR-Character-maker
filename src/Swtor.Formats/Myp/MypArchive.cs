using System.Buffers.Binary;
using System.IO.Compression;
using ZstdSharp;

namespace Swtor.Formats.Myp;

/// <summary>One file in a .tor archive. <see cref="Hash"/> is the <see cref="MypHash"/> of its path.</summary>
public readonly record struct MypEntry(
    long Offset, int HeaderLength, int CompressedSize, int UncompressedSize, ulong Hash, uint Crc, MypCompression Compression)
{
    /// <summary>Position of the first data byte.</summary>
    public long DataOffset => Offset + HeaderLength;
}

/// <summary>Stored (0) or compressed (1). Version 6 archives use zstd for 1. The reader also accepts zlib, found by the first bytes.</summary>
public enum MypCompression : ushort { None = 0, Compressed = 1 }

/// <summary>
/// Reader of a .tor archive (the "MYP" container of Mythic, used by SWTOR).
/// Layout: 32 byte header (magic "MYP\0", u32 version, u32 0xFD23EC43, u64 first table offset, u32 entries per table,
/// u32 total entries), then data and tables mixed in the file. A table is: u32 used count, u64 next table offset,
/// then "capacity" entries of 34 bytes (u64 offset, u32 header length, u32 compressed size, u32 size, u64 name hash,
/// u32 crc, u16 compression). An entry with offset 0 is empty. The file data starts after a small per-file header.
/// The archive keeps no names: see <see cref="MypHash"/>.
/// </summary>
public sealed class MypArchive : IDisposable
{
    private const uint Magic = 0x0050594D;       // "MYP\0"
    private const uint ByteOrderMark = 0xFD23EC43;
    private const uint ZstdMagic = 0xFD2FB528;
    private const int HeaderSize = 32;
    private const int TableHeaderSize = 12;
    private const int EntrySize = 34;

    private readonly Stream _stream;
    private readonly string _path;

    public IReadOnlyList<MypEntry> Entries { get; }
    public uint Version { get; }

    private MypArchive(Stream stream, string path, uint version, List<MypEntry> entries)
    {
        _stream = stream;
        _path = path;
        Version = version;
        Entries = entries;
    }

    /// <summary>Opens an archive and reads all its tables (a few KB per 1,000 files). The archive owns the stream.</summary>
    public static MypArchive Open(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.RandomAccess);
        try
        {
            Span<byte> header = stackalloc byte[HeaderSize];
            ReadExactly(stream, header, 0, path);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic) throw Error(path, "not a MYP archive", 0);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header[8..]) != ByteOrderMark) throw Error(path, "bad byte order mark", 8);
            uint version = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
            long tableOffset = BinaryPrimitives.ReadInt64LittleEndian(header[12..]);
            int capacity = BinaryPrimitives.ReadInt32LittleEndian(header[20..]);
            int total = BinaryPrimitives.ReadInt32LittleEndian(header[24..]);
            if (capacity <= 0 || capacity > 1_000_000) throw Error(path, "bad table capacity", 20);

            var entries = new List<MypEntry>(Math.Max(total, 0));
            var block = new byte[TableHeaderSize + capacity * EntrySize];
            // A broken file could point to itself: stop after more tables than the entry count allows.
            for (int guard = 0; tableOffset != 0 && guard <= total + 1; guard++)
            {
                if (tableOffset < 0 || tableOffset + block.Length > stream.Length) throw Error(path, "table outside the file", tableOffset);
                ReadExactly(stream, block, tableOffset, path);
                long next = BinaryPrimitives.ReadInt64LittleEndian(block.AsSpan(4));
                for (int i = 0; i < capacity; i++)
                {
                    var e = block.AsSpan(TableHeaderSize + i * EntrySize, EntrySize);
                    long offset = BinaryPrimitives.ReadInt64LittleEndian(e);
                    if (offset == 0) continue;
                    var entry = new MypEntry(
                        offset,
                        BinaryPrimitives.ReadInt32LittleEndian(e[8..]),
                        BinaryPrimitives.ReadInt32LittleEndian(e[12..]),
                        BinaryPrimitives.ReadInt32LittleEndian(e[16..]),
                        BinaryPrimitives.ReadUInt64LittleEndian(e[20..]),
                        BinaryPrimitives.ReadUInt32LittleEndian(e[28..]),
                        (MypCompression)BinaryPrimitives.ReadUInt16LittleEndian(e[32..]));
                    if (entry.HeaderLength < 0 || entry.CompressedSize < 0 || entry.UncompressedSize < 0
                        || entry.DataOffset + entry.CompressedSize > stream.Length)
                        throw Error(path, "file data outside the archive", offset);
                    entries.Add(entry);
                }
                tableOffset = next;
            }
            return new MypArchive(stream, path, version, entries);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Returns the uncompressed bytes of one file. Not thread-safe: use one archive per thread.</summary>
    public byte[] Read(in MypEntry entry)
    {
        var raw = new byte[entry.CompressedSize];
        ReadExactly(_stream, raw, entry.DataOffset, _path);
        switch (entry.Compression)
        {
            case MypCompression.None:
                return raw;
            case MypCompression.Compressed:
                var result = new byte[entry.UncompressedSize];
                if (raw.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(raw) == ZstdMagic)
                {
                    using var decompressor = new Decompressor();
                    if (decompressor.Unwrap(raw, result) != result.Length) throw Error(_path, "zstd size differs from the table", entry.DataOffset);
                    return result;
                }
                using (var zlib = new ZLibStream(new MemoryStream(raw), CompressionMode.Decompress))
                {
                    try { zlib.ReadExactly(result); }
                    catch (EndOfStreamException) { throw Error(_path, "zlib data too short", entry.DataOffset); }
                    catch (InvalidDataException) { throw Error(_path, "bad zlib data", entry.DataOffset); }
                }
                return result;
            default:
                throw Error(_path, $"unknown compression {(ushort)entry.Compression}", entry.Offset);
        }
    }

    private static void ReadExactly(Stream stream, Span<byte> buffer, long offset, string path)
    {
        stream.Position = offset;
        try { stream.ReadExactly(buffer); }
        catch (EndOfStreamException) { throw Error(path, "file too short", offset); }
    }

    private static GameFormatException Error(string path, string message, long offset) => new($"{path}: {message}", offset);

    public void Dispose() => _stream.Dispose();
}
