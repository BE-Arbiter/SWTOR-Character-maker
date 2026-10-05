using System.Numerics;
using Swtor.Formats.Dds;
using Swtor.Formats.Xml;

namespace Swtor.Tests;

public class PaletteGainTests
{
    private static DdsImage Image(int width, int height, Func<int, byte[]> pixel)
    {
        var rgba = new byte[width * height * 4];
        for (int i = 0; i < width * height; i++) pixel(i).CopyTo(rgba, i * 4);
        return new DdsImage(width, height, rgba);
    }

    // The mask is red everywhere (primary area), or green everywhere (secondary area).
    private static DdsImage Mask(byte red, byte green) => Image(4, 4, _ => [red, green, 0, 255]);

    private static (double R, double G, double B) Average(DdsImage image)
    {
        double r = 0, g = 0, b = 0;
        int n = image.Width * image.Height;
        for (int i = 0; i < n; i++) { r += image.Rgba[i * 4]; g += image.Rgba[i * 4 + 1]; b += image.Rgba[i * 4 + 2]; }
        return (r / n, g / n, b / n);
    }

    [Fact]
    public void Apply_WithRepresentativeColor_MovesTheAverageOfTheAreaToThatColor()
    {
        var diffuse = Image(4, 4, i => [(byte)(100 + i * 5), (byte)(100 + i * 5), (byte)(100 + i * 5), 255]);
        var blue = new Palette("blue", 0.55f, 0.2f, 0.2f, 1.8f, new Vector3(0.126f, 0.292f, 0.685f));

        var result = PaletteTint.Apply(diffuse, Mask(255, 0), blue, null);

        var (r, g, b) = Average(result);
        Assert.InRange(r, 0.126 * 255 - 3, 0.126 * 255 + 3);
        Assert.InRange(g, 0.292 * 255 - 3, 0.292 * 255 + 3);
        Assert.InRange(b, 0.685 * 255 - 3, 0.685 * 255 + 3);
        // The detail stays: brighter pixels stay brighter.
        Assert.True(result.Rgba[15 * 4 + 2] > result.Rgba[0 * 4 + 2]);
    }

    [Fact]
    public void Apply_LightGreyDye_GivesGrey_NotPink()
    {
        // Same values as a real file: hue 0.93 and saturation 0.99 with a grey representative color.
        var diffuse = Image(4, 4, _ => [180, 150, 120, 255]);
        var grey = new Palette("grey", 0.93f, 0.994f, 0.695f, 0f, new Vector3(0.696f, 0.695f, 0.695f));

        var (r, g, b) = Average(PaletteTint.Apply(diffuse, Mask(255, 0), grey, null));

        Assert.InRange(Math.Abs(r - g), 0, 4);
        Assert.InRange(Math.Abs(g - b), 0, 4);
    }

    [Fact]
    public void Apply_PrimaryAndSecondaryAreasGetTheirOwnColor()
    {
        var diffuse = Image(4, 4, _ => [128, 128, 128, 255]);
        var mask = Image(4, 4, i => i < 8 ? [255, 0, 0, 255] : [0, 255, 0, 255]);
        var red = new Palette("r", 0, 1, 0, 1, new Vector3(0.8f, 0.1f, 0.1f));
        var green = new Palette("g", 0, 1, 0, 1, new Vector3(0.1f, 0.8f, 0.1f));

        var result = PaletteTint.Apply(diffuse, mask, red, green);

        Assert.True(result.Rgba[0] > result.Rgba[1]);          // first area: red
        Assert.True(result.Rgba[12 * 4 + 1] > result.Rgba[12 * 4]); // second area: green
    }

    [Fact]
    public void Apply_FillerPalette_FallsBackToTheHueMethod()
    {
        var diffuse = Image(4, 4, _ => [128, 128, 128, 255]);
        var filler = new Palette("f", 0f, 1f, 0f, 1f, new Vector3(0.3382f, 0.3363f, 0.3363f));

        var result = PaletteTint.Apply(diffuse, Mask(255, 0), filler, null);

        Assert.True(result.Rgba[0] > result.Rgba[2]); // hue 0 is red
    }
}

public class GarmentShaderTests
{
    private static DdsImage Solid(int w, int h, byte r, byte g, byte b, byte a) =>
        new(w, h, Enumerable.Range(0, w * h).SelectMany(_ => new[] { r, g, b, a }).ToArray());

    // The values of the files garmenthue_dye_h66_p (light grey) and garmenthue_dye_h23_p (blue).
    private static readonly Palette LightGrey = new("h66", 0.93292683f, 0.994152f, 0.69540314f, 0f, new Vector3(0.696f, 0.695f, 0.695f));
    private static readonly Palette Blue = new("h23", 0.55f, 0.19997928f, 0.20820174f, 1.8426f, new Vector3(0.1256f, 0.2920f, 0.6846f));

    [Fact]
    public void DyeColor_LightGreyDye_IsGreyWithTheBrightnessOfThePalette()
    {
        // Contrast 0 makes the lightness equal to the brightness. The saturation is nearly 0 (1 - 0.994).
        var (r, g, b) = PaletteTint.DyeColor(LightGrey, 1f, 0.42f, 0.8f, 0.45f);

        Assert.InRange(r, 0.685f, 0.705f);
        Assert.InRange(Math.Abs(r - g), 0f, 0.01f);
        Assert.InRange(Math.Abs(g - b), 0f, 0.01f);
    }

    [Fact]
    public void DyeColor_BlueDye_IsBlue_AndMatchesTheRepresentativeColorOfTheFile()
    {
        // The sample is a typical neutral pixel of a dyeable area (hue 0.067 before the palette shift).
        var (r, g, b) = PaletteTint.DyeColor(Blue, 1f, 0.42f, 0.80f, 0.43f);

        Assert.True(b > g && g > r);
        Assert.InRange(b, 0.6f, 0.75f);
        Assert.InRange(r, 0.08f, 0.18f);
    }

    [Fact]
    public void Dye_LowAmbientOcclusion_DarkensTheColor()
    {
        var (_, _, brightB) = PaletteTint.DyeColor(Blue, 1f, 0.42f, 0.8f, 0.43f);
        var (_, _, darkB) = PaletteTint.DyeColor(Blue, 0.3f, 0.42f, 0.8f, 0.43f);

        Assert.True(darkB < brightB);
    }

    [Fact]
    public void Apply_WithPaletteMap_UsesTheMapInsideTheAreasAndTheDiffuseOutside()
    {
        var diffuse = Solid(2, 1, 200, 10, 10, 255);
        var map = Solid(2, 1, 255, 107, 204, 160);
        // Left pixel: primary area. Right pixel: no area.
        var mask = new DdsImage(2, 1, [255, 0, 0, 255, 0, 0, 0, 255]);

        var result = PaletteTint.Apply(diffuse, mask, LightGrey, null, map);

        Assert.InRange(result.Rgba[0], 165, 190);              // light grey: not the red of the diffuse
        Assert.InRange(Math.Abs(result.Rgba[0] - result.Rgba[1]), 0, 3);
        Assert.Equal(new byte[] { 200, 10, 10, 255 }, result.Rgba[4..8]); // outside: the diffuse color
    }

    [Fact]
    public void Apply_WithPaletteMap_GreenMaskUsesTheSecondaryPalette()
    {
        var diffuse = Solid(2, 1, 100, 100, 100, 255);
        var map = Solid(2, 1, 255, 107, 204, 160);
        var mask = new DdsImage(2, 1, [255, 0, 0, 255, 0, 255, 0, 255]);

        var result = PaletteTint.Apply(diffuse, mask, LightGrey, Blue, map);

        Assert.True(result.Rgba[2] - result.Rgba[0] < 5);      // primary: grey
        Assert.True(result.Rgba[4 + 2] > result.Rgba[4] + 60); // secondary: blue
    }
}
