using System.Buffers.Binary;
using Swtor.Formats;
using Swtor.Formats.Dds;

namespace Swtor.Tests;

public class DdsReaderTests
{
    // Builds a 4x4 DDS file with one block of the given FourCC (or raw 32-bit BGRA when fourCc is null).
    private static byte[] MakeDds(string? fourCc, byte[] payload, int width = 4, int height = 4)
    {
        var d = new byte[128 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(d, 0x20534444);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(4), 124);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(12), (uint)height);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(16), (uint)width);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(28), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(76), 32);
        if (fourCc is not null)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(80), 4);
            System.Text.Encoding.ASCII.GetBytes(fourCc).CopyTo(d, 84);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(80), 0x41); // RGB + alpha
            BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(88), 32);
            BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(92), 0x00FF0000);
            BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(104), 0xFF000000);
        }
        payload.CopyTo(d, 128);
        return d;
    }

    [Fact]
    public void Decode_Bc1_SolidRed()
    {
        // Endpoint 0 = pure red (565: F800), endpoint 1 = black. All indices 0 -> endpoint 0.
        byte[] block = [0x00, 0xF8, 0x00, 0x00, 0, 0, 0, 0];
        var image = DdsReader.Decode(MakeDds("DXT1", block));

        Assert.Equal((4, 4), (image.Width, image.Height));
        for (int p = 0; p < 16; p++)
            Assert.Equal([255, 0, 0, 255], image.Rgba.AsSpan(p * 4, 4).ToArray());
    }

    [Fact]
    public void Decode_Bc3_UsesAlphaBlock()
    {
        // Alpha endpoints 255 and 0, all indices 0 -> alpha 255. Then 0 for index 1 in pixel 0.
        byte[] block = [255, 0, 0b001, 0, 0, 0, 0, 0, 0x00, 0xF8, 0x00, 0xF8, 0, 0, 0, 0];
        var image = DdsReader.Decode(MakeDds("DXT5", block));

        Assert.Equal(0, image.Rgba[3]);
        Assert.Equal(255, image.Rgba[7]);
        Assert.Equal(255, image.Rgba[0]);
    }

    [Fact]
    public void Decode_RawBgra_SwapsChannels()
    {
        byte[] pixels = new byte[4 * 4 * 4];
        pixels[0] = 10; pixels[1] = 20; pixels[2] = 30; pixels[3] = 40; // B, G, R, A
        var image = DdsReader.Decode(MakeDds(null, pixels));

        Assert.Equal([30, 20, 10, 40], image.Rgba.AsSpan(0, 4).ToArray());
    }

    [Fact]
    public void Decode_TruncatedData_Throws()
    {
        Assert.Throws<GameFormatException>(() => DdsReader.Decode(MakeDds("DXT1", [0, 0, 0, 0])));
    }

    [Fact]
    public void ReadInfo_UnknownFourCc_Throws()
    {
        Assert.Throws<GameFormatException>(() => DdsReader.ReadInfo(MakeDds("XXXX", new byte[16])));
    }
}
