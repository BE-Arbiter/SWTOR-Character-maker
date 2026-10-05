using System.Numerics;
using Swtor.Formats.Dds;

namespace Swtor.Tests;

public class ImageColorTests
{
    private static DdsImage Solid(byte r, byte g, byte b, byte a = 255, int width = 2) =>
        new(width, 1, Enumerable.Range(0, width).SelectMany(_ => new[] { r, g, b, a }).ToArray());

    [Fact]
    public void MatchAverage_MovesMaskedAverageToTarget_AndKeepsOtherPixels()
    {
        var image = Solid(100, 100, 100);
        var mask = new DdsImage(2, 1, [255, 0, 0, 255, 0, 0, 0, 255]); // First pixel masked, second not.

        var result = ImageColor.MatchAverage(image, mask, new Vector3(0.5f, 0.4f, 0.2f));

        Assert.InRange(result.Rgba[0], 126, 129);
        Assert.InRange(result.Rgba[1], 101, 103);
        Assert.InRange(result.Rgba[2], 50, 52);
        Assert.Equal([100, 100, 100, 255], result.Rgba.AsSpan(4, 4).ToArray());
        Assert.Equal(100, image.Rgba[0]); // The input is not changed.
    }

    [Fact]
    public void MatchAverage_EmptyMask_ReturnsSameImage()
    {
        var image = Solid(100, 100, 100);
        var mask = Solid(0, 0, 0);

        Assert.Same(image, ImageColor.MatchAverage(image, mask, new Vector3(1, 0, 0)));
    }

    [Fact]
    public void Multiply_WhiteKeepsPixel_BlackClears()
    {
        var result = ImageColor.Multiply(Solid(200, 100, 50), new DdsImage(2, 1, [255, 255, 255, 255, 0, 0, 0, 255]));

        Assert.Equal([200, 100, 50], result.Rgba.AsSpan(0, 3).ToArray());
        Assert.Equal([0, 0, 0], result.Rgba.AsSpan(4, 3).ToArray());
    }

    [Fact]
    public void AlphaOver_UsesOverlayAlpha()
    {
        var overlay = new DdsImage(2, 1, [255, 0, 0, 255, 255, 0, 0, 0]); // Opaque red, then fully transparent.

        var result = ImageColor.AlphaOver(Solid(10, 20, 30), overlay);

        Assert.Equal([255, 0, 0], result.Rgba.AsSpan(0, 3).ToArray());
        Assert.Equal([10, 20, 30], result.Rgba.AsSpan(4, 3).ToArray());
    }

    [Fact]
    public void CutOut_WhiteRedIsHole_BlackRedIsSolid()
    {
        var holes = new DdsImage(2, 1, [255, 0, 0, 255, 0, 0, 0, 255]);

        var result = ImageColor.CutOut(Solid(10, 20, 30, 128), holes, 0.5f);

        Assert.Equal([10, 20, 30, 0], result.Rgba.AsSpan(0, 4).ToArray());
        Assert.Equal([10, 20, 30, 255], result.Rgba.AsSpan(4, 4).ToArray());
    }
}
