using Swtor.Assets;

namespace Swtor.Tests;

public class ArmorInfoTests
{
    [Theory]
    [InlineData("chest_armor01_heavy_bh_a02", ArmorWeight.Heavy, "bh")]
    [InlineData("chest_tight_med_ge_mtx12", ArmorWeight.Medium, "ge")]
    [InlineData("chest_invisible_light_sm_a05_c01", ArmorWeight.Light, "sm")]
    [InlineData("chest_robe05_mtx_sw_a01", ArmorWeight.Unknown, null)]
    [InlineData("chest_naked_caucasian_young_a01", ArmorWeight.Unknown, null)]
    [InlineData("chest_tight_light_xx_a01", ArmorWeight.Light, null)]
    public void ParsesWeightAndClass(string artName, ArmorWeight weight, string? classCode)
    {
        var info = ArmorInfo.Parse(artName);
        Assert.Equal(weight, info.Weight);
        Assert.Equal(classCode, info.ClassCode);
    }
}
