using System.Text;
using Swtor.Formats.Dds;
using Swtor.Formats.Xml;

namespace Swtor.Tests;

public class ColorTests
{
    [Fact]
    public void ColorSchemeIndex_ReadsSlotsAndIgnoresEmptyReferences()
    {
        const string xml = """
            <Assets>
              <Asset>
                <Materials><Material id="1"><ColorSchemes><ColorScheme guid="5" /></ColorSchemes></Material></Materials>
                <CustomData>
                  <ColorScheme>
                    <Name>scheme_a</Name>
                    <Guid>42</Guid>
                    <slot name="chest" primary="10" secondary="11" />
                    <slot name="boot" primary="12" secondary="13" />
                  </ColorScheme>
                </CustomData>
              </Asset>
            </Assets>
            """;
        var schemes = ColorSchemeIndexReader.Read(new MemoryStream(Encoding.UTF8.GetBytes(xml)));

        var scheme = Assert.Single(schemes);
        Assert.Equal(("42", "scheme_a"), (scheme.Guid, scheme.Name));
        Assert.Equal(new SlotPalettes("10", "11"), scheme.Slots["chest"]);
        Assert.Equal(new SlotPalettes("12", "13"), scheme.Slots["BOOT"]);
    }

    [Fact]
    public void AppearanceIndex_ReadsColorSchemeIdsPerMaterial()
    {
        const string xml = """
            <Assets><Asset><ID>1</ID><BaseFile>/a.gr2</BaseFile>
              <Materials>
                <Material id="1" name="a" filename="/a.mat"><ColorSchemes><ColorScheme guid="7" /><ColorScheme guid="8" /></ColorSchemes></Material>
                <Material id="2" name="b" filename="/b.mat"><ColorSchemes /></Material>
              </Materials></Asset></Assets>
            """;
        var asset = Assert.Single(AppearanceIndexReader.Read(new MemoryStream(Encoding.UTF8.GetBytes(xml))));

        Assert.Equal(["7", "8"], asset.Materials[0].ColorSchemeIds);
        Assert.Empty(asset.Materials[1].ColorSchemeIds);
    }

    [Fact]
    public void PaletteReader_ReadsValues()
    {
        var palette = PaletteReader.Parse("<Palette><Name>x</Name><Hue>0.5</Hue><Saturation>0.25</Saturation><Brightness>-0.1</Brightness><Contrast>2</Contrast></Palette>");

        Assert.Equal(new Palette("x", 0.5f, 0.25f, -0.1f, 2f), palette);
    }

    [Fact]
    public void Tint_ColorsOnlyMaskedPixels()
    {
        // Two pixels: the first is in the primary area, the second is outside the mask.
        var diffuse = new DdsImage(2, 1, [128, 128, 128, 255, 128, 128, 128, 255]);
        var mask = new DdsImage(2, 1, [255, 0, 255, 255, 0, 0, 0, 255]);
        var red = new Palette("red", 0f, 1f, 0f, 1f);

        var result = PaletteTint.Apply(diffuse, mask, red, null);

        Assert.True(result.Rgba[0] > 100 && result.Rgba[1] < 10 && result.Rgba[2] < 10); // Red.
        Assert.Equal([128, 128, 128, 255], result.Rgba.AsSpan(4, 4).ToArray()); // Unchanged.
        Assert.Equal(128, diffuse.Rgba[1]); // The input image is not modified.
    }

    [Fact]
    public void Tint_WithoutMaskOrPalette_ReturnsSameImage()
    {
        var diffuse = new DdsImage(1, 1, [1, 2, 3, 4]);

        Assert.Same(diffuse, PaletteTint.Apply(diffuse, null, new Palette("p", 0, 1, 0, 1), null));
        Assert.Same(diffuse, PaletteTint.Apply(diffuse, diffuse, null, null));
    }
}
