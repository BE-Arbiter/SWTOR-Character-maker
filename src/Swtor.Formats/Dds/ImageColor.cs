using System.Numerics;

namespace Swtor.Formats.Dds;

/// <summary>
/// Color operations on decoded images. These are approximations of what the game shader does with palettes,
/// masks and overlays. The real shader is not known.
/// </summary>
public static class ImageColor
{
    /// <summary>
    /// Changes the average color of the masked area to <paramref name="target"/> and keeps the detail (shading, lines).
    /// The red channel of <paramref name="mask"/> is the weight of each pixel. Without a mask the whole image counts.
    /// Returns a new image. The input is not changed.
    /// </summary>
    public static DdsImage MatchAverage(DdsImage image, DdsImage? mask, Vector3 target)
    {
        var weights = Weights(image, mask);
        double sumR = 0, sumG = 0, sumB = 0, sumW = 0;
        for (int i = 0; i < weights.Length; i++)
        {
            float w = weights[i];
            if (w <= 0) continue;
            sumR += image.Rgba[i * 4] * w;
            sumG += image.Rgba[i * 4 + 1] * w;
            sumB += image.Rgba[i * 4 + 2] * w;
            sumW += w;
        }
        if (sumW <= 0) return image;

        // Color factor per channel. The limit stops dark images from exploding.
        var factor = new Vector3(
            Math.Clamp(target.X * 255f / Math.Max((float)(sumR / sumW), 1f), 0f, 4f),
            Math.Clamp(target.Y * 255f / Math.Max((float)(sumG / sumW), 1f), 0f, 4f),
            Math.Clamp(target.Z * 255f / Math.Max((float)(sumB / sumW), 1f), 0f, 4f));

        var result = (byte[])image.Rgba.Clone();
        for (int i = 0; i < weights.Length; i++)
        {
            float w = weights[i];
            if (w <= 0) continue;
            result[i * 4] = Mix(result[i * 4], factor.X, w);
            result[i * 4 + 1] = Mix(result[i * 4 + 1], factor.Y, w);
            result[i * 4 + 2] = Mix(result[i * 4 + 2], factor.Z, w);
        }
        return new DdsImage(image.Width, image.Height, result);
    }

    /// <summary>
    /// Scales all pixels so the brightest channel value becomes 255, but only if the image is dark (brightest value below 170).
    /// Normal textures stay unchanged. Used for glowing textures (Chiss eyes) that the game
    /// draws unlit and very bright, while the file holds a dark image. Returns a new image.
    /// </summary>
    public static DdsImage NormalizeBrightness(DdsImage image)
    {
        int max = 1;
        for (int i = 0; i < image.Rgba.Length; i += 4)
            max = Math.Max(max, Math.Max(image.Rgba[i], Math.Max(image.Rgba[i + 1], image.Rgba[i + 2])));
        if (max >= 170) return image;
        var result = (byte[])image.Rgba.Clone();
        for (int i = 0; i < result.Length; i += 4)
            for (int c = 0; c < 3; c++) result[i + c] = (byte)Math.Min(255, result[i + c] * 255 / max);
        return new DdsImage(image.Width, image.Height, result);
    }

    /// <summary>Multiplies the color of <paramref name="image"/> by an overlay (white keeps the pixel). Returns a new image.</summary>
    public static DdsImage Multiply(DdsImage image, DdsImage overlay)
    {
        var result = (byte[])image.Rgba.Clone();
        for (int y = 0; y < image.Height; y++)
        {
            int oy = y * overlay.Height / image.Height;
            for (int x = 0; x < image.Width; x++)
            {
                int o = (oy * overlay.Width + x * overlay.Width / image.Width) * 4;
                int p = (y * image.Width + x) * 4;
                for (int c = 0; c < 3; c++) result[p + c] = (byte)(result[p + c] * overlay.Rgba[o + c] / 255);
            }
        }
        return new DdsImage(image.Width, image.Height, result);
    }

    /// <summary>Draws an overlay over the image. The alpha channel of the overlay says how much it covers. Returns a new image.</summary>
    public static DdsImage AlphaOver(DdsImage image, DdsImage overlay)
    {
        var result = (byte[])image.Rgba.Clone();
        for (int y = 0; y < image.Height; y++)
        {
            int oy = y * overlay.Height / image.Height;
            for (int x = 0; x < image.Width; x++)
            {
                int o = (oy * overlay.Width + x * overlay.Width / image.Width) * 4;
                int p = (y * image.Width + x) * 4;
                int a = overlay.Rgba[o + 3];
                for (int c = 0; c < 3; c++) result[p + c] = (byte)((result[p + c] * (255 - a) + overlay.Rgba[o + c] * a) / 255);
            }
        }
        return new DdsImage(image.Width, image.Height, result);
    }

    // Weight of each pixel from the red channel of the mask (any size, nearest pixel). 1 everywhere without a mask.
    private static float[] Weights(DdsImage image, DdsImage? mask)
    {
        var weights = new float[image.Width * image.Height];
        for (int y = 0; y < image.Height; y++)
        {
            int my = mask is null ? 0 : y * mask.Height / image.Height;
            for (int x = 0; x < image.Width; x++)
            {
                weights[y * image.Width + x] = mask is null ? 1f : mask.Rgba[(my * mask.Width + x * mask.Width / image.Width) * 4] / 255f;
            }
        }
        return weights;
    }

    private static byte Mix(byte value, float factor, float weight) =>
        (byte)Math.Clamp((int)(value * (1 + (factor - 1) * weight) + 0.5f), 0, 255);
}
