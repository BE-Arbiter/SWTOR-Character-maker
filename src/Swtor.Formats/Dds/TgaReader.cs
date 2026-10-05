namespace Swtor.Formats.Dds;

/// <summary>Reads Targa (.tga) files: true color images with 24 or 32 bits, plain or run-length coded.</summary>
public static class TgaReader
{
    public static DdsImage Decode(ReadOnlySpan<byte> d)
    {
        if (d.Length < 18) throw new GameFormatException("Targa file is too short", 0);
        int idLength = d[0], colorMapType = d[1], type = d[2];
        int width = d[12] | d[13] << 8, height = d[14] | d[15] << 8, bits = d[16];
        bool topDown = (d[17] & 0x20) != 0;
        if (colorMapType != 0 || (type != 2 && type != 10)) throw new GameFormatException($"Unsupported Targa type {type}", 2);
        if (bits is not (24 or 32) || width == 0 || height == 0) throw new GameFormatException($"Unsupported Targa size or depth ({width}x{height}, {bits} bits)", 12);

        int bytesPerPixel = bits / 8, count = width * height;
        var rgba = new byte[count * 4];
        int pos = 18 + idLength;

        void Put(int pixel, ReadOnlySpan<byte> bgra)
        {
            int y = pixel / width, x = pixel % width;
            int row = topDown ? y : height - 1 - y;
            int o = (row * width + x) * 4;
            rgba[o] = bgra[2];
            rgba[o + 1] = bgra[1];
            rgba[o + 2] = bgra[0];
            rgba[o + 3] = bytesPerPixel == 4 ? bgra[3] : (byte)255;
        }

        int written = 0;
        while (written < count)
        {
            if (type == 2)
            {
                if (pos + bytesPerPixel > d.Length) throw new GameFormatException("Targa data is too short", pos);
                Put(written++, d.Slice(pos, bytesPerPixel));
                pos += bytesPerPixel;
                continue;
            }
            if (pos >= d.Length) throw new GameFormatException("Targa data is too short", pos);
            int header = d[pos++];
            int run = (header & 0x7F) + 1;
            if (written + run > count) throw new GameFormatException("Targa run is outside the image", pos - 1);
            if ((header & 0x80) != 0)
            {
                if (pos + bytesPerPixel > d.Length) throw new GameFormatException("Targa data is too short", pos);
                for (int i = 0; i < run; i++) Put(written++, d.Slice(pos, bytesPerPixel));
                pos += bytesPerPixel;
            }
            else
            {
                if (pos + run * bytesPerPixel > d.Length) throw new GameFormatException("Targa data is too short", pos);
                for (int i = 0; i < run; i++) { Put(written++, d.Slice(pos, bytesPerPixel)); pos += bytesPerPixel; }
            }
        }
        return new DdsImage(width, height, rgba);
    }
}
