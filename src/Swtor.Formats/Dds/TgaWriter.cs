namespace Swtor.Formats.Dds;

/// <summary>Writes Targa (.tga) files, the texture format that Jedi Academy reads.</summary>
public static class TgaWriter
{
    /// <summary>
    /// Writes an uncompressed true color image. The rows go from bottom to top (origin at the bottom left),
    /// the layout that every Targa reader accepts. With <paramref name="alpha"/> false the file has 24 bits per pixel.
    /// </summary>
    public static byte[] Write(DdsImage image, bool alpha)
    {
        int bytesPerPixel = alpha ? 4 : 3;
        var data = new byte[18 + image.Width * image.Height * bytesPerPixel];
        data[2] = 2;                                   // image type: uncompressed true color
        data[12] = (byte)(image.Width & 0xFF);
        data[13] = (byte)(image.Width >> 8);
        data[14] = (byte)(image.Height & 0xFF);
        data[15] = (byte)(image.Height >> 8);
        data[16] = (byte)(bytesPerPixel * 8);
        data[17] = (byte)(alpha ? 8 : 0);              // alpha bits; bit 5 clear = origin at the bottom

        int o = 18;
        for (int y = image.Height - 1; y >= 0; y--)
            for (int x = 0; x < image.Width; x++)
            {
                int i = (y * image.Width + x) * 4;
                data[o++] = image.Rgba[i + 2];         // Targa stores blue, green, red
                data[o++] = image.Rgba[i + 1];
                data[o++] = image.Rgba[i];
                if (alpha) data[o++] = image.Rgba[i + 3];
            }
        return data;
    }

    /// <summary>Halves the image until neither side is larger than <paramref name="maxSize"/>. Averages 2x2 blocks.</summary>
    public static DdsImage Downscale(DdsImage image, int maxSize)
    {
        while (Math.Max(image.Width, image.Height) > maxSize && image.Width > 1 && image.Height > 1)
        {
            int w = image.Width / 2, h = image.Height / 2;
            var rgba = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    for (int c = 0; c < 4; c++)
                    {
                        int sum = 0;
                        for (int dy = 0; dy < 2; dy++)
                            for (int dx = 0; dx < 2; dx++)
                                sum += image.Rgba[((y * 2 + dy) * image.Width + x * 2 + dx) * 4 + c];
                        rgba[(y * w + x) * 4 + c] = (byte)((sum + 2) / 4);
                    }
            image = new DdsImage(w, h, rgba);
        }
        return image;
    }
}
