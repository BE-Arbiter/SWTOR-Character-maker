using Swtor.Formats.Jka;

namespace Swtor.Tests;

public class JkaModelBuilderTests
{
    // A small reference model with the layout of the sample models: a first surface, the main surfaces, a tag, a cap and a content surface.
    private static GlmModel SmallModel()
    {
        var model = new GlmModel { BoneCount = 53 };
        void Add(string name, int parent, uint flags = 0)
        {
            model.Surfaces.Add(new GlmSurfaceInfo { Name = name, Parent = parent, Flags = flags });
            if (parent >= 0) model.Surfaces[parent].Children.Add(model.Surfaces.Count - 1);
        }
        Add("stupidtriangle_off", -1, GlmSurfaceInfo.FlagOff);
        Add("hips", 0);
        Add("torso", 1);
        Add("hips_cap_torso_off", 1, GlmSurfaceInfo.FlagOff);
        Add("*chestg", 2, GlmSurfaceInfo.FlagBolt);
        Add("bandolier", 2);
        Add("headb", 2);
        Add("*head_front", 6, GlmSurfaceInfo.FlagBolt);
        model.Lods.Add(model.Surfaces.Select((_, i) => JkaTestData.Triangle(1 + i)).ToArray());
        return model;
    }

    private static JkaSurfaceDraft Draft(string name, string parent, string texture, JkaRegion region, params int[] bones) =>
        new(name, parent, texture, region, JkaTestData.Triangle(bones));

    private static void AssertConsistentHierarchy(GlmModel model)
    {
        for (int i = 0; i < model.Surfaces.Count; i++)
        {
            int parent = model.Surfaces[i].Parent;
            if (parent >= 0) Assert.Contains(i, model.Surfaces[parent].Children);
            Assert.All(model.Surfaces[i].Children, c => Assert.Equal(i, model.Surfaces[c].Parent));
        }
        Assert.All(model.Lods, lod => Assert.Equal(model.Surfaces.Count, lod.Length));
        Assert.Equal(model.Surfaces.Count, model.Surfaces.Select(s => s.Name.ToLowerInvariant()).Distinct().Count());
    }

    [Fact]
    public void CreateNew_HasTheRootTheMainSurfacesAndAllTags()
    {
        var skeleton = JkaTestData.HumanoidSkeleton();
        var drafts = new[] { Draft("head", "head", "head", JkaRegion.Head, 15), Draft("torso", "torso", "chest", JkaRegion.Torso, 12) };

        var model = JkaModelBuilder.CreateNew(JkaTemplate.Standard, drafts, skeleton, "models/players/mine/model", "models/players/mine");

        string[] names = model.Surfaces.Select(s => s.Name).ToArray();
        Assert.Equal("stupidtriangle_off", names[0]);
        foreach (string main in new[] { "hips", "l_leg", "r_leg", "torso", "head", "l_arm", "r_arm", "l_hand", "r_hand" }) Assert.Contains(main, names);
        foreach (string tag in new[] { "*chestg", "*back", "*head_eyes", "*head_front", "*l_hand", "*r_hand", "*shldr_l", "*hips_cap_torso" }) Assert.Contains(tag, names);
        Assert.Equal("models/players/mine/model", model.Name);
        Assert.Equal(53, model.BoneCount);
        AssertConsistentHierarchy(model);
    }

    [Fact]
    public void CreateNew_HierarchyFollowsTheBody()
    {
        var model = JkaModelBuilder.CreateNew(JkaTemplate.Standard, [], JkaTestData.HumanoidSkeleton(), "m", "models/players/mine");

        string ParentOf(string name) => model.Surfaces[model.Surfaces[model.SurfaceIndex(name)].Parent].Name;
        Assert.Equal("stupidtriangle_off", ParentOf("hips"));
        Assert.Equal("hips", ParentOf("torso"));
        Assert.Equal("hips", ParentOf("l_leg"));
        Assert.Equal("torso", ParentOf("head"));
        Assert.Equal("torso", ParentOf("r_arm"));
        Assert.Equal("l_arm", ParentOf("l_hand"));
        Assert.Equal("head", ParentOf("*head_front"));
        Assert.Equal("torso", ParentOf("*chestg"));
        Assert.Equal("hips", ParentOf("*hips_cap_torso"));
    }

    [Fact]
    public void Standard_UsesOnlyBonesOfTheHumanoidSkeletonAndFlagsTagsAsBolts()
    {
        var bones = JkaTestData.HumanoidNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var template = JkaTemplate.Standard;

        Assert.Equal("models/players/_humanoid/_humanoid", template.AnimationName);
        Assert.Single(template.Surfaces, s => s.Parent.Length == 0);
        Assert.Equal("stupidtriangle_off", template.Surfaces[0].Name);
        Assert.All(template.Surfaces, s =>
        {
            Assert.Equal(3, s.Vertices.Length);
            Assert.Equal(3, s.Triangles.Length);
            Assert.All(s.Vertices.SelectMany(v => v.Bones), b => Assert.Contains(b, bones));
            if (s.Parent.Length > 0) Assert.Equal(GlmSurfaceInfo.FlagBolt, s.Flags);
        });
        Assert.Equal(template.Surfaces.Count, template.Surfaces.Select(s => s.Name.ToLowerInvariant()).Distinct().Count());
    }

    [Fact]
    public void EmbeddedHumanoidSkeleton_HasTheBonesTheExporterNeeds()
    {
        var skeleton = GlaSkeleton.Humanoid;

        Assert.Equal(53, skeleton.Bones.Count);
        Assert.Equal("models/players/_humanoid/_humanoid", skeleton.Name);
        foreach (string bone in new[] { "pelvis", "thoracic", "cranium", "lhumerus", "rhand", "ltibia", "rtalus", "face", "jaw" }) Assert.True(skeleton.IndexOf(bone) >= 0, bone);
        // The neck is at 56.71 in the sample skeleton, and the parent of every bone comes before it.
        Assert.InRange(skeleton.Bones[skeleton.IndexOf("cervical")].Origin.Z, 56f, 57.5f);
        for (int i = 1; i < skeleton.Bones.Count; i++) Assert.InRange(skeleton.Bones[i].Parent, 0, skeleton.Bones.Count - 1);
    }

    [Fact]
    public void Template_JsonRoundTrip()
    {
        var read = JkaTemplate.FromJson(JkaTemplate.Standard.ToJson());

        Assert.Equal(JkaTemplate.Standard.Surfaces.Select(s => s.Name), read.Surfaces.Select(s => s.Name));
        Assert.Equal(JkaTemplate.Standard.Surfaces[5].Vertices[1].Position, read.Surfaces[5].Vertices[1].Position);
    }

    [Fact]
    public void FromModels_TakesTheMostCommonGeometryAndParent_AndDropsRareTags()
    {
        var skeleton = JkaTestData.HumanoidSkeleton();
        GlmModel Sample(float x, string parent, bool extra)
        {
            var m = new GlmModel { AnimationName = "models/players/_humanoid/_humanoid" };
            m.Surfaces.Add(new GlmSurfaceInfo { Name = "stupidtriangle_off", Flags = GlmSurfaceInfo.FlagOff });
            m.Surfaces.Add(new GlmSurfaceInfo { Name = "torso", Parent = 0 });
            m.Surfaces.Add(new GlmSurfaceInfo { Name = "hips", Parent = 0 });
            m.Surfaces.Add(new GlmSurfaceInfo { Name = "*chestg", Parent = parent == "torso" ? 1 : 2, Flags = GlmSurfaceInfo.FlagBolt });
            if (extra) m.Surfaces.Add(new GlmSurfaceInfo { Name = "*mine", Parent = 1, Flags = GlmSurfaceInfo.FlagBolt });
            var tag = JkaTestData.Triangle(12);
            foreach (var v in tag.Vertices) v.Position = new System.Numerics.Vector3(x, 0, 50);
            m.Lods.Add(m.Surfaces.Select((_, i) => i >= 3 ? tag : JkaTestData.Triangle(1)).ToArray());
            return m;
        }

        var template = JkaTemplate.FromModels([(Sample(1, "torso", true), skeleton), (Sample(2, "torso", false), skeleton), (Sample(2, "hips", false), skeleton)]);

        Assert.Equal(["stupidtriangle_off", "*chestg"], template.Surfaces.Select(s => s.Name));
        Assert.Equal("torso", template.Surfaces[1].Parent);
        Assert.Equal(2f, template.Surfaces[1].Vertices[0].Position[0]);
        Assert.Equal(["upper_lumbar"], template.Surfaces[1].Vertices[0].Bones);
    }

    [Fact]
    public void CreateNew_MainSurfacesWithoutDraftAreHiddenPlaceholders_AndDraftsShowThem()
    {
        var drafts = new[] { Draft("torso", "torso", "chest", JkaRegion.Torso, 12) };

        var model = JkaModelBuilder.CreateNew(JkaTemplate.Standard, drafts, JkaTestData.HumanoidSkeleton(), "m", "models/players/mine");

        Assert.Equal(GlmSurfaceInfo.FlagOff, model.Surfaces[model.SurfaceIndex("l_hand")].Flags);
        Assert.Equal(0u, model.Surfaces[model.SurfaceIndex("torso")].Flags);
        Assert.Equal("models/players/mine/chest", model.Surfaces[model.SurfaceIndex("torso")].Shader);
        var placeholder = model.Lods[0][model.SurfaceIndex("l_hand")];
        Assert.Single(placeholder.Triangles, 0);
        Assert.Equal(3, placeholder.Vertices.Length);
    }

    [Fact]
    public void CreateNew_ResultCanBeWrittenAndRead()
    {
        var drafts = new[]
        {
            Draft("head", "head", "head", JkaRegion.Head, 15, 17), Draft("head_hair", "head", "hair", JkaRegion.Head, 15),
            Draft("l_leg_boot", "l_leg", "boot", JkaRegion.LeftLeg, 6),
        };
        var model = JkaModelBuilder.CreateNew(JkaTemplate.Standard, drafts, JkaTestData.HumanoidSkeleton(), "models/players/mine/model", "models/players/mine");

        var read = GlmReader.Parse(GlmWriter.Write(model));

        Assert.Equal(model.Surfaces.Select(s => s.Name), read.Surfaces.Select(s => s.Name));
        Assert.Equal(model.Surfaces.Select(s => s.Parent), read.Surfaces.Select(s => s.Parent));
        AssertConsistentHierarchy(read);
    }

    [Fact]
    public void AddSurfaces_ReplacesSurfacesWithTheSameNameAndAddsTheOthersBelowTheirMainSurface()
    {
        var skeleton = JkaTestData.HumanoidSkeleton();
        var model = JkaModelBuilder.CreateNew(JkaTemplate.Standard, [Draft("torso", "torso", "old", JkaRegion.Torso, 12)], skeleton, "m", "models/players/mine");
        int before = model.Surfaces.Count;

        JkaModelBuilder.AddSurfaces(model,
        [
            Draft("torso", "torso", "chest", JkaRegion.Torso, 13),
            Draft("torso_waist", "torso", "waist", JkaRegion.Torso, 12),
            Draft("l_leg_boot", "l_leg", "boot", JkaRegion.LeftLeg, 6),
        ], skeleton, "models/players/mine");

        Assert.Equal(before + 2, model.Surfaces.Count);
        Assert.Equal("models/players/mine/chest", model.Surfaces[model.SurfaceIndex("torso")].Shader);
        Assert.Equal(13, model.Lods[0][model.SurfaceIndex("torso")].Vertices[0].Weights[0].Bone);
        Assert.Equal("torso", model.Surfaces[model.Surfaces[model.SurfaceIndex("torso_waist")].Parent].Name);
        Assert.Equal("l_leg", model.Surfaces[model.Surfaces[model.SurfaceIndex("l_leg_boot")].Parent].Name);
        AssertConsistentHierarchy(model);
    }

    [Fact]
    public void AddSurfaces_ToAModelWithoutTheMainSurface_CreatesIt()
    {
        var model = new GlmModel();
        model.Surfaces.Add(new GlmSurfaceInfo { Name = "hips" });
        model.Lods.Add([JkaTestData.Triangle(1)]);

        JkaModelBuilder.AddSurfaces(model, [Draft("head_hair", "head", "hair", JkaRegion.Head, 15)], JkaTestData.HumanoidSkeleton(), "models/players/mine");

        Assert.True(model.SurfaceIndex("torso") >= 0);
        Assert.True(model.SurfaceIndex("head") >= 0);
        Assert.Equal("head", model.Surfaces[model.Surfaces[model.SurfaceIndex("head_hair")].Parent].Name);
        Assert.Equal("torso", model.Surfaces[model.Surfaces[model.SurfaceIndex("head")].Parent].Name);
        AssertConsistentHierarchy(model);
    }

    [Fact]
    public void AddSurfaces_GivesTheNewSurfaceToEveryLevelOfDetail()
    {
        var model = SmallModel();
        model.Lods.Add(model.Lods[0].ToArray());

        JkaModelBuilder.AddSurfaces(model, [Draft("torso_waist", "torso", "waist", JkaRegion.Torso, 12)], JkaTestData.HumanoidSkeleton(), "models/players/mine");

        int index = model.SurfaceIndex("torso_waist");
        Assert.All(model.Lods, lod => Assert.NotNull(lod[index]));
        AssertConsistentHierarchy(model);
    }

    [Fact]
    public void MergeSurfaces_SkipsIdenticalSurfacesAndRenamesChangedOnes()
    {
        // First export: head, chest and gloves. Second export: the same head, another chest and other gloves.
        var skeleton = JkaTestData.HumanoidSkeleton();
        var model = JkaModelBuilder.CreateNew(JkaTemplate.Standard,
            [Draft("head", "head", "head", JkaRegion.Head, 15), Draft("torso", "torso", "chest", JkaRegion.Torso, 12), Draft("l_hand", "l_hand", "hand", JkaRegion.LeftHand, 41)],
            skeleton, "m", "models/players/mine");
        model = GlmReader.Parse(GlmWriter.Write(model));
        int before = model.Surfaces.Count;
        string? TextureOf(int i) => Path.GetFileName(model.Surfaces[i].Shader);

        var merge = JkaModelBuilder.MergeSurfaces(model,
        [
            Draft("head", "head", "head", JkaRegion.Head, 15),
            Draft("torso", "torso", "chest_2", JkaRegion.Torso, 13),
            Draft("l_hand", "l_hand", "hand_2", JkaRegion.LeftHand, 42),
            Draft("r_hand", "r_hand", "hand_2", JkaRegion.RightHand, 28),
        ], skeleton, "models/players/mine", TextureOf);

        Assert.Equal(["torsoa", "l_handa", "r_hand"], merge.Added.Select(d => d.Name));
        Assert.Equal(["head"], merge.Kept);
        Assert.Equal(["torso", "l_hand"], merge.Replaced);
        Assert.Equal(before + 2, model.Surfaces.Count);
        // The surfaces of the first export are kept.
        Assert.Equal("models/players/mine/chest", model.Surfaces[model.SurfaceIndex("torso")].Shader);
        Assert.Equal(12, model.Lods[0][model.SurfaceIndex("torso")].Vertices[0].Weights[0].Bone);
        Assert.Equal("torso", model.Surfaces[model.Surfaces[model.SurfaceIndex("torsoa")].Parent].Name);
        // r_hand was a hidden placeholder: it is filled, not copied.
        Assert.Equal(0u, model.Surfaces[model.SurfaceIndex("r_hand")].Flags);
        AssertConsistentHierarchy(model);
    }

    [Fact]
    public void MergeSurfaces_SameGeometryWithAnotherTexture_IsAdded()
    {
        var skeleton = JkaTestData.HumanoidSkeleton();
        var model = JkaModelBuilder.CreateNew(JkaTemplate.Standard, [Draft("torso", "torso", "chest", JkaRegion.Torso, 12)], skeleton, "m", "models/players/mine");

        var merge = JkaModelBuilder.MergeSurfaces(model, [Draft("torso", "torso", "chest_2", JkaRegion.Torso, 12)], skeleton, "models/players/mine",
            i => Path.GetFileName(model.Surfaces[i].Shader));

        Assert.Equal(["torsoa"], merge.Added.Select(d => d.Name));
    }

    [Fact]
    public void MergeSurfaces_MainSurfaceVariants_UseLettersAndSplitPartsFollow()
    {
        // The game finds r_handa..r_handh when a skin turns r_hand off. Any other name hides the saber.
        var skeleton = JkaTestData.HumanoidSkeleton();
        var model = JkaModelBuilder.CreateNew(JkaTemplate.Standard,
            [Draft("r_hand", "r_hand", "hand", JkaRegion.RightHand, 28), Draft("l_arm_chest1", "l_arm", "chest", JkaRegion.LeftArm, 40)],
            skeleton, "m", "models/players/mine");
        string? TextureOf(int i) => Path.GetFileName(model.Surfaces[i].Shader);

        var first = JkaModelBuilder.MergeSurfaces(model,
            [Draft("r_hand", "r_hand", "hand_2", JkaRegion.RightHand, 28), Draft("r_hand_2", "r_hand", "hand_2", JkaRegion.RightHand, 29),
             Draft("l_arm_chest1", "l_arm", "chest_2", JkaRegion.LeftArm, 40)],
            skeleton, "models/players/mine", TextureOf);
        var second = JkaModelBuilder.MergeSurfaces(model, [Draft("r_hand", "r_hand", "hand_3", JkaRegion.RightHand, 28)],
            skeleton, "models/players/mine", TextureOf);

        Assert.Equal(["r_handa", "r_handa_2", "l_arm_chest1_2"], first.Added.Select(d => d.Name));
        Assert.Equal(["r_handb"], second.Added.Select(d => d.Name));
        Assert.Equal("r_hand", model.Surfaces[model.Surfaces[model.SurfaceIndex("r_handb")].Parent].Name);
        Assert.Equal("r_hand", model.Surfaces[model.Surfaces[model.SurfaceIndex("r_handa_2")].Parent].Name);
        AssertConsistentHierarchy(model);
    }

    [Fact]
    public void SkinLines_UseTheTexturePathWithTheExtension()
    {
        var lines = JkaModelBuilder.SkinLines([Draft("l_arm", "torso", "chest", JkaRegion.LeftArm, 40)], "models/players/mine", ".tga").ToList();

        Assert.Equal([("l_arm", "models/players/mine/chest.tga")], lines);
    }
}
