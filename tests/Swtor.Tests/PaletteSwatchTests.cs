using Swtor.Formats.Dds;
using Swtor.Formats.Xml;

namespace Swtor.Tests;

public class PaletteSwatchTests
{
    [Fact]
    public void ZeroSaturation_GivesGrey()
    {
        var (r, g, b) = PaletteTint.Swatch(new Palette("grey", 0.3f, 0f, 0f, 1f));
        Assert.Equal(r, g, 3);
        Assert.Equal(g, b, 3);
    }

    [Fact]
    public void RedHue_GivesMostlyRed()
    {
        var (r, g, b) = PaletteTint.Swatch(new Palette("red", 0f, 1f, 0f, 1f));
        Assert.True(r > g && r > b);
    }
}
