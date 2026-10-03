using System.Buffers.Binary;

namespace Swtor.Formats.Dds;

public enum DdsFormat
{
    Bc1, Bc2, Bc3, Bc4, Bc5,
    Bgra32, Bgrx32, Bgr24, L8, A8,
}

/// <summary>Size and layout of a DDS file.</summary>
public readonly record struct DdsInfo(int Width, int Height, int MipCount, DdsFormat Format, int DataOffset);

/// <summary>Image in 8-bit RGBA, row by row from the top. Four bytes per pixel.</summary>
public sealed record DdsImage(int Width, int Height, byte[] Rgba);

/// <summary>Reads the DDS files of the game and decodes the largest mip level to RGBA.</summary>
public static class DdsReader
{
    private const uint Magic = 0x20534444; // "DDS "
    private const uint FourCcDxt1 = 0x31545844, FourCcDxt3 = 0x33545844, FourCcDxt5 = 0x35545844;
    private const uint FourCcAti1 = 0x31495441, FourCcAti2 = 0x32495441, FourCcBc4u = 0x55344342, FourCcBc5u = 0x55354342;

    /// <summary>Reads only the header. Throws <see cref="GameFormatException"/> for unsupported formats.</summary>
    public static DdsInfo ReadInfo(ReadOnlySpan<byte> d)
    {
        if (d.Length < 128 || U32(d, 0) != Magic || U32(d, 4) != 124)
            throw new GameFormatException("Not a DDS file", 0);

        int height = (int)U32(d, 12), width = (int)U32(d, 16);
        int mips = Math.Max(1, (int)U32(d, 28));
        uint pfFlags = U32(d, 80), fourCc = U32(d, 84), bits = U32(d, 88);
        uint rMask = U32(d, 92), aMask = U32(d, 104);
        if (width <= 0 || height <= 0 || width > 16384 || height > 16384)
            throw new GameFormatException("Invalid image size", 12);

        DdsFormat format;
        if ((pfFlags & 4) != 0) // DDPF_FOURCC
        {
            format = fourCc switch
            {
                FourCcDxt1 => DdsFormat.Bc1,
                FourCcDxt3 => DdsFormat.Bc2,
                FourCcDxt5 => DdsFormat.Bc3,
                FourCcAti1 or FourCcBc4u => DdsFormat.Bc4,
                FourCcAti2 or FourCcBc5u => DdsFormat.Bc5,
                _ => throw new GameFormatException($"Unsupported DDS FourCC 0x{fourCc:X8}", 84),
            };
        }
        else if ((pfFlags & 0x40) != 0) // DDPF_RGB
        {
            format = bits switch
            {
                32 => (pfFlags & 1) != 0 && aMask != 0 ? DdsFormat.Bgra32 : DdsFormat.Bgrx32,
                24 => DdsFormat.Bgr24,
                8 => DdsFormat.L8,
                _ => throw new GameFormatException($"Unsupported DDS bit depth {bits}", 88),
            };
            if (bits == 32 && rMask != 0x00FF0000)
                throw new GameFormatException("Unsupported DDS channel order", 92);
        }
        else if ((pfFlags & 2) != 0 && bits == 8) format = DdsFormat.A8; // DDPF_ALPHA
        else if ((pfFlags & 0x20000) != 0 && bits == 8) format = DdsFormat.L8; // DDPF_LUMINANCE
        else throw new GameFormatException("Unsupported DDS pixel format", 80);

        return new DdsInfo(width, height, mips, format, 128);
    }

    /// <summary>Decodes the first (largest) mip level.</summary>
    public static DdsImage Decode(ReadOnlySpan<byte> d)
    {
        var info = ReadInfo(d);
        var data = d[info.DataOffset..];
        int w = info.Width, h = info.Height;
        var rgba = new byte[w * h * 4];

        switch (info.Format)
        {
            case DdsFormat.Bc1 or DdsFormat.Bc2 or DdsFormat.Bc3 or DdsFormat.Bc4 or DdsFormat.Bc5:
                DecodeBlocks(data, info.Format, w, h, rgba);
                break;
            default:
                DecodeRaw(data, info.Format, w, h, rgba);
                break;
        }
        return new DdsImage(w, h, rgba);
    }

    private static void DecodeRaw(ReadOnlySpan<byte> src, DdsFormat format, int w, int h, byte[] rgba)
    {
        int bytes = format switch { DdsFormat.Bgra32 or DdsFormat.Bgrx32 => 4, DdsFormat.Bgr24 => 3, _ => 1 };
        if (src.Length < w * h * bytes) throw new GameFormatException("DDS data is too short", 128);
        for (int i = 0; i < w * h; i++)
        {
            var s = src.Slice(i * bytes, bytes);
            int o = i * 4;
            switch (format)
            {
                case DdsFormat.Bgra32: (rgba[o], rgba[o + 1], rgba[o + 2], rgba[o + 3]) = (s[2], s[1], s[0], s[3]); break;
                case DdsFormat.Bgrx32: (rgba[o], rgba[o + 1], rgba[o + 2], rgba[o + 3]) = (s[2], s[1], s[0], 255); break;
                case DdsFormat.Bgr24: (rgba[o], rgba[o + 1], rgba[o + 2], rgba[o + 3]) = (s[2], s[1], s[0], 255); break;
                case DdsFormat.A8: (rgba[o], rgba[o + 1], rgba[o + 2], rgba[o + 3]) = (255, 255, 255, s[0]); break;
                default: (rgba[o], rgba[o + 1], rgba[o + 2], rgba[o + 3]) = (s[0], s[0], s[0], 255); break;
            }
        }
    }

    private static void DecodeBlocks(ReadOnlySpan<byte> src, DdsFormat format, int w, int h, byte[] rgba)
    {
        int blockSize = format is DdsFormat.Bc1 or DdsFormat.Bc4 ? 8 : 16;
        int bw = (w + 3) / 4, bh = (h + 3) / 4;
        if (src.Length < bw * bh * blockSize) throw new GameFormatException("DDS data is too short", 128);

        Span<byte> block = stackalloc byte[64]; // 4x4 pixels, RGBA
        for (int by = 0; by < bh; by++)
        {
            for (int bx = 0; bx < bw; bx++)
            {
                var b = src.Slice((by * bw + bx) * blockSize, blockSize);
                switch (format)
                {
                    case DdsFormat.Bc1: DecodeColor(b, block, true); break;
                    case DdsFormat.Bc2: DecodeColor(b[8..], block, false); DecodeAlphaExplicit(b, block); break;
                    case DdsFormat.Bc3: DecodeColor(b[8..], block, false); DecodeAlphaInterpolated(b, block, 3); break;
                    case DdsFormat.Bc4:
                        // One channel, shown as grey.
                        DecodeAlphaInterpolated(b, block, 0);
                        for (int p = 0; p < 16; p++) { block[p * 4 + 1] = block[p * 4 + 2] = block[p * 4]; block[p * 4 + 3] = 255; }
                        break;
                    default: // Bc5: two channels (normal map X and Y). Z is rebuilt.
                        DecodeAlphaInterpolated(b, block, 0);
                        DecodeAlphaInterpolated(b[8..], block, 1);
                        for (int p = 0; p < 16; p++) block[p * 4 + 2] = RebuildZ(block[p * 4], block[p * 4 + 1]);
                        for (int p = 0; p < 16; p++) block[p * 4 + 3] = 255;
                        break;
                }
                for (int py = 0; py < 4 && by * 4 + py < h; py++)
                    for (int px = 0; px < 4 && bx * 4 + px < w; px++)
                        block.Slice((py * 4 + px) * 4, 4).CopyTo(rgba.AsSpan(((by * 4 + py) * w + bx * 4 + px) * 4, 4));
            }
        }
    }

    private static byte RebuildZ(byte x, byte y)
    {
        float nx = x / 255f * 2 - 1, ny = y / 255f * 2 - 1;
        float z = MathF.Sqrt(Math.Max(0, 1 - nx * nx - ny * ny));
        return (byte)(z * 127.5f + 127.5f);
    }

    // Color part of BC1/BC2/BC3: two RGB565 endpoints and 2 bits per pixel.
    // In BC1 only, endpoint0 <= endpoint1 selects the 3-color mode with transparent black.
    private static void DecodeColor(ReadOnlySpan<byte> b, Span<byte> block, bool bc1)
    {
        ushort c0 = BinaryPrimitives.ReadUInt16LittleEndian(b), c1 = BinaryPrimitives.ReadUInt16LittleEndian(b[2..]);
        Span<byte> palette = stackalloc byte[16];
        Expand565(c0, palette[..4]);
        Expand565(c1, palette.Slice(4, 4));
        if (c0 > c1 || !bc1)
        {
            for (int i = 0; i < 3; i++)
            {
                palette[8 + i] = (byte)((2 * palette[i] + palette[4 + i]) / 3);
                palette[12 + i] = (byte)((palette[i] + 2 * palette[4 + i]) / 3);
            }
            palette[11] = palette[15] = 255;
        }
        else
        {
            for (int i = 0; i < 3; i++) palette[8 + i] = (byte)((palette[i] + palette[4 + i]) / 2);
            palette[11] = 255;
            palette[12] = palette[13] = palette[14] = palette[15] = 0;
        }

        uint indices = BinaryPrimitives.ReadUInt32LittleEndian(b[4..]);
        for (int p = 0; p < 16; p++)
            palette.Slice((int)((indices >> (p * 2)) & 3) * 4, 4).CopyTo(block.Slice(p * 4, 4));
    }

    private static void Expand565(ushort c, Span<byte> rgba)
    {
        int r = c >> 11, g = (c >> 5) & 63, b = c & 31;
        rgba[0] = (byte)((r << 3) | (r >> 2));
        rgba[1] = (byte)((g << 2) | (g >> 4));
        rgba[2] = (byte)((b << 3) | (b >> 2));
        rgba[3] = 255;
    }

    // BC2 alpha: 4 bits per pixel.
    private static void DecodeAlphaExplicit(ReadOnlySpan<byte> b, Span<byte> block)
    {
        for (int p = 0; p < 16; p++)
        {
            int v = (b[p / 2] >> ((p & 1) * 4)) & 15;
            block[p * 4 + 3] = (byte)(v * 17);
        }
    }

    // BC3 alpha and BC4/BC5 channels: two 8-bit endpoints and 3 bits per pixel.
    private static void DecodeAlphaInterpolated(ReadOnlySpan<byte> b, Span<byte> block, int channel)
    {
        int a0 = b[0], a1 = b[1];
        Span<byte> palette = stackalloc byte[8];
        palette[0] = (byte)a0;
        palette[1] = (byte)a1;
        if (a0 > a1)
        {
            for (int i = 1; i <= 6; i++) palette[i + 1] = (byte)(((7 - i) * a0 + i * a1) / 7);
        }
        else
        {
            for (int i = 1; i <= 4; i++) palette[i + 1] = (byte)(((5 - i) * a0 + i * a1) / 5);
            palette[6] = 0;
            palette[7] = 255;
        }

        ulong bits = 0;
        for (int i = 0; i < 6; i++) bits |= (ulong)b[2 + i] << (8 * i);
        for (int p = 0; p < 16; p++) block[p * 4 + channel] = palette[(int)((bits >> (p * 3)) & 7)];
    }

    private static uint U32(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadUInt32LittleEndian(d[o..]);
}
