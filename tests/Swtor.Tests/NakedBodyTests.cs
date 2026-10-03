using Swtor.Assets;

namespace Swtor.Tests;

public class NakedBodyTests
{
    [Theory]
    [InlineData("chest", "twilek", "head_twilek_bma_caucasian_a01", "chest_naked_twilek_young_a01")]
    [InlineData("leg", "cathar", null, "leg_naked_cathar_young_a01")]
    [InlineData("boot", "sith", "head_bloodsith_bma_non_a01", "boot_naked_bloodsith_young_a01")]
    [InlineData("chest", "human", "head_human_bma_asian_a01", "chest_naked_asian_young_a01")]
    [InlineData("hand", "chiss", "head_chiss_bma_non_a01", "hand_naked_caucasian_young_a01")]
    public void AssetName_ChoosesBodyByRaceOrHeadEthnicity(string slot, string race, string? head, string expected)
    {
        Assert.Equal(expected, NakedBody.AssetName(slot, race, head));
    }
}
