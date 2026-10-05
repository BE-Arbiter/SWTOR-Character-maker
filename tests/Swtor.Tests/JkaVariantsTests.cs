using Swtor.Formats.Jka;

namespace Swtor.Tests;

public class JkaVariantsTests
{
    private static JkaSurfaceDraft Draft(string name, string parent, string texture, JkaRegion region, params int[] bones) =>
        new(name, parent, texture, region, JkaTestData.Triangle(bones));

    // A model exported twice: default skin with a hand and a glove piece, then a second skin with another hand.
    private static (GlmModel Model, List<(string Name, SkinFile Skin)> Skins) TwoSkins()
    {
        var skeleton = JkaTestData.HumanoidSkeleton();
        var model = JkaModelBuilder.CreateNew(JkaTemplate.Standard,
            [Draft("r_hand", "r_hand", "hand", JkaRegion.RightHand, 28), Draft("r_hand_hand1", "r_hand", "hand", JkaRegion.RightHand, 28),
             Draft("torso", "torso", "chest", JkaRegion.Torso, 12)],
            skeleton, "m", "models/players/mine");
        JkaModelBuilder.MergeSurfaces(model, [Draft("r_hand", "r_hand", "hand_2", JkaRegion.RightHand, 29)], skeleton, "models/players/mine",
            i => Path.GetFileName(model.Surfaces[i].Shader));
        var @default = SkinFile.Parse("r_hand,hand.tga\nr_hand_hand1,hand.tga\ntorso,chest.tga\nr_handa,*off\n");
        var officer = SkinFile.Parse("r_hand,*off\nr_hand_hand1,*off\ntorso,chest.tga\nr_handa,hand_2.tga\n");
        return (model, [("default", @default), ("officer", officer)]);
    }

    [Fact]
    public void Organize_PutsOneVariantPerSkinBelowTheHiddenMainSurface()
    {
        var (model, skins) = TwoSkins();
        int triangles = model.Lods[0][model.SurfaceIndex("r_hand")].Triangles.Length;

        JkaVariants.Organize(model, skins);

        var main = model.Surfaces[model.SurfaceIndex("r_hand")];
        Assert.NotEqual(0u, main.Flags & GlmSurfaceInfo.FlagOff);
        Assert.Equal(3, model.Lods[0][model.SurfaceIndex("r_hand")].Triangles.Length);
        string ParentOf(string name) => model.Surfaces[model.Surfaces[model.SurfaceIndex(name)].Parent].Name;
        Assert.Equal("r_hand", ParentOf("r_handa"));
        Assert.Equal("r_hand", ParentOf("r_handb"));
        Assert.Equal("r_handa", ParentOf("r_hand_hand1"));
        Assert.Equal("r_hand", ParentOf("*r_hand"));
        // The default skin keeps its hand geometry in r_handa; the second skin's hand is r_handb.
        Assert.Equal(triangles, model.Lods[0][model.SurfaceIndex("r_handa")].Triangles.Length);
        Assert.Equal("hand.tga", skins[0].Skin.Get("r_handa"));
        Assert.Equal("*off", skins[0].Skin.Get("r_handb"));
        Assert.Equal("hand_2.tga", skins[1].Skin.Get("r_handb"));
        Assert.Equal("*off", skins[1].Skin.Get("r_handa"));
        Assert.All(skins, s => Assert.Equal("*off", s.Skin.Get("r_hand")));
        // The torso is shown by both skins: not changed.
        Assert.Equal(0u, model.Surfaces[model.SurfaceIndex("torso")].Flags);
        Assert.Equal(-1, model.SurfaceIndex("torsoa"));
        // Parents come before their children.
        Assert.All(Enumerable.Range(0, model.Surfaces.Count), i => Assert.True(model.Surfaces[i].Parent < i));
    }

    [Fact]
    public void Organize_SecondRun_ChangesNothing()
    {
        var (model, skins) = TwoSkins();
        JkaVariants.Organize(model, skins);
        byte[] first = GlmWriter.Write(model);
        string skinText = string.Join("|", skins.Select(s => s.Skin.ToString()));

        var log = JkaVariants.Organize(model, skins);

        Assert.Empty(log);
        Assert.Equal(first, GlmWriter.Write(model));
        Assert.Equal(skinText, string.Join("|", skins.Select(s => s.Skin.ToString())));
    }
}
