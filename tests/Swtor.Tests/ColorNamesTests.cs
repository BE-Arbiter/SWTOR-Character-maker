using System.Numerics;
using Swtor.Formats.Dds;
using Swtor.Formats.Xml;

namespace Swtor.Tests;

public class ColorNamesTests
{
    [Theory]
    [InlineData(17, 17, 17, "Black")]
    [InlineData(25, 24, 23, "Black")]
    [InlineData(17, 35, 77, "Navy")]
    [InlineData(40, 90, 200, "Blue")]
    [InlineData(255, 255, 255, "White")]
    [InlineData(131, 131, 131, "Gray")]
    [InlineData(190, 190, 190, "Light gray")]
    [InlineData(149, 17, 32, "Red")]
    [InlineData(110, 8, 8, "Dark red")]
    [InlineData(100, 60, 30, "Brown")]
    [InlineData(60, 130, 40, "Green")]
    public void Describe_NamesTheClassicColors(int r, int g, int b, string expected) =>
        Assert.Equal(expected, ColorNames.Describe(r / 255f, g / 255f, b / 255f));

    [Fact]
    public void PaletteReader_ReadsTheRepresentativeColor_AndSwatchUsesIt()
    {
        var palette = PaletteReader.Parse("<Palette><Name>x</Name><Hue>0.1</Hue><Saturation>1</Saturation><Brightness>0</Brightness><Contrast>1</Contrast>" +
            "<Representativecolor>0.068, 0.138, 0.303</Representativecolor></Palette>");

        Assert.Equal(new Vector3(0.068f, 0.138f, 0.303f), palette.Representative);
        Assert.Equal((0.068f, 0.138f, 0.303f), PaletteTint.Swatch(palette));
        Assert.False(palette.IsPlaceholder);
    }

    [Fact]
    public void PaletteReader_RecognizesTheFillerPalettes()
    {
        var palette = PaletteReader.Parse("<Palette><Name>x</Name><Hue>0.93</Hue><Saturation>0.99</Saturation><Brightness>0</Brightness><Contrast>1</Contrast>" +
            "<Representativecolor>0.33820003263195125, 0.33631133068984748, 0.33631133068984748</Representativecolor></Palette>");

        Assert.True(palette.IsPlaceholder);
    }

    [Fact]
    public void PaletteReader_WithoutRepresentativeColor_KeepsTheComputedSwatch()
    {
        var palette = PaletteReader.Parse("<Palette><Name>x</Name><Hue>0</Hue><Saturation>1</Saturation><Brightness>0</Brightness><Contrast>1</Contrast></Palette>");

        Assert.Null(palette.Representative);
        Assert.True(PaletteTint.Swatch(palette).R > PaletteTint.Swatch(palette).B);
    }

    [Fact]
    public void Custom_SwatchFollowsTheShaderValues()
    {
        // Full saturation and a hue of 0.6 (blue) at neutral brightness and contrast.
        var blue = PaletteTint.Swatch(PaletteTint.Custom(0.6f, 0f, 0f, 1f));
        // Brightness near 1 gives white, saturation 1 (game value) gives grey.
        var white = PaletteTint.Swatch(PaletteTint.Custom(0f, 1f, 0.95f, 1f));
        var grey = PaletteTint.Swatch(PaletteTint.Custom(0.6f, 1f, 0f, 1f));

        Assert.True(blue.B > blue.R + 0.1f && blue.B > blue.G);
        Assert.True(white.R > 0.9f && white.G > 0.9f && white.B > 0.9f);
        Assert.Equal(grey.R, grey.B, 0.01f);
    }
}
