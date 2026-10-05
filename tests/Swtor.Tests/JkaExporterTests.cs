using Swtor.Assets;

namespace Swtor.Tests;

public class JkaExporterTests
{
    [Theory]
    [InlineData("armure", true)]
    [InlineData("tenue_2", true)]
    [InlineData("red-1", true)]
    [InlineData("default", false)]
    [InlineData("Default", false)]
    [InlineData("deux mots", false)]
    [InlineData("a/b", false)]
    [InlineData("", false)]
    public void IsValidSkinName_AcceptsOneWordAndRefusesDefault(string name, bool valid) =>
        Assert.Equal(valid, JkaExporter.IsValidSkinName(name));

    [Fact]
    public void SkinPath_IsModelUnderscoreName()
    {
        Assert.Equal(Path.Combine("folder", "model_armure.skin"), JkaExporter.SkinPath("folder", "armure"));
    }
}
