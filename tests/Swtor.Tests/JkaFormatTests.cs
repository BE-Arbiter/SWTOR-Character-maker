using System.Numerics;
using Swtor.Formats;
using Swtor.Formats.Dds;
using Swtor.Formats.Gr2;
using Swtor.Formats.Jka;

namespace Swtor.Tests;

public class JkaFormatTests
{
    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", name));

    private static GlmModel TwoSurfaceModel()
    {
        var model = new GlmModel { Name = "models/players/test/model", BoneCount = 53 };
        model.Surfaces.Add(new GlmSurfaceInfo { Name = "hips", Shader = "models/players/test/leg" });
        model.Surfaces.Add(new GlmSurfaceInfo { Name = "torso", Parent = 0, Flags = GlmSurfaceInfo.FlagOff });
        model.Surfaces[0].Children.Add(1);
        model.Lods.Add([TwoWeightTriangle(), JkaTestData.Triangle(3, 4, 5, 11)]);
        model.Lods.Add([JkaTestData.Triangle(1), JkaTestData.Triangle(12)]);
        return model;
    }

    private static GlmSurface TwoWeightTriangle()
    {
        var surface = JkaTestData.Triangle(1, 2);
        surface.Vertices[0].Weights = [new GlmWeight(1, 0.25f), new GlmWeight(2, 0.75f)];
        return surface;
    }

    [Fact]
    public void GlmWriter_Roundtrip_KeepsHierarchyGeometryAndWeights()
    {
        var original = TwoSurfaceModel();

        var read = GlmReader.Parse(GlmWriter.Write(original));

        Assert.Equal("models/players/test/model", read.Name);
        Assert.Equal("models/players/_humanoid/_humanoid", read.AnimationName);
        Assert.Equal(53, read.BoneCount);
        Assert.Equal(2, read.Lods.Count);
        Assert.Equal(["hips", "torso"], read.Surfaces.Select(s => s.Name));
        Assert.Equal([1], read.Surfaces[0].Children);
        Assert.Equal(0, read.Surfaces[1].Parent);
        Assert.Equal(GlmSurfaceInfo.FlagOff, read.Surfaces[1].Flags);
        Assert.Equal("models/players/test/leg", read.Surfaces[0].Shader);

        for (int lod = 0; lod < 2; lod++)
            for (int s = 0; s < 2; s++)
            {
                var a = original.Lods[lod][s];
                var b = read.Lods[lod][s];
                Assert.Equal(a.Triangles, b.Triangles);
                for (int v = 0; v < a.Vertices.Length; v++)
                {
                    Assert.Equal(a.Vertices[v].Position, b.Vertices[v].Position);
                    Assert.Equal(a.Vertices[v].Normal, b.Vertices[v].Normal);
                    Assert.Equal(a.Vertices[v].Uv, b.Vertices[v].Uv);
                    Assert.Equal(a.Vertices[v].Weights.Length, b.Vertices[v].Weights.Length);
                    for (int k = 0; k < a.Vertices[v].Weights.Length; k++)
                    {
                        Assert.Equal(a.Vertices[v].Weights[k].Bone, b.Vertices[v].Weights[k].Bone);
                        Assert.Equal(a.Vertices[v].Weights[k].Weight, b.Vertices[v].Weights[k].Weight, 1e-3f);
                    }
                }
            }
    }

    [Fact]
    public void GlmWriter_Weights_AddUpToOneAfterRounding()
    {
        var model = new GlmModel();
        model.Surfaces.Add(new GlmSurfaceInfo { Name = "hips" });
        var surface = JkaTestData.Triangle(1, 2, 3);
        model.Lods.Add([surface]);

        var read = GlmReader.Parse(GlmWriter.Write(model));

        Assert.All(read.Lods[0][0].Vertices, v => Assert.Equal(1f, v.Weights.Sum(w => w.Weight), 1e-4f));
    }

    [Fact]
    public void GlmWriter_VertexInMoreThan32Triangles_IsCopied()
    {
        // A fan of 100 triangles around vertex 0, as a surface kept from an older export could have.
        var model = new GlmModel();
        model.Surfaces.Add(new GlmSurfaceInfo { Name = "hips" });
        var vertices = Enumerable.Range(0, 102).Select(i => new GlmVertex { Position = new Vector3(i, 0, 0), Weights = [new GlmWeight(0, 1f)] }).ToArray();
        var triangles = Enumerable.Range(0, 100).SelectMany(t => new[] { 0, t + 1, t + 2 }).ToArray();
        model.Lods.Add([new GlmSurface { Vertices = vertices, Triangles = triangles }]);

        var read = GlmReader.Parse(GlmWriter.Write(model)).Lods[0][0];

        Assert.Equal(300, read.Triangles.Length);
        Assert.All(read.Triangles.GroupBy(i => i), g => Assert.InRange(g.Count(), 1, JkaConverter.MaxTrianglesPerVertex));
        Assert.Equal(triangles.Select(i => vertices[i].Position), read.Triangles.Select(i => read.Vertices[i].Position));
    }

    [Fact]
    public void GlmWriter_MoreThan32BonesInOneSurface_Throws()
    {
        var model = new GlmModel();
        model.Surfaces.Add(new GlmSurfaceInfo { Name = "hips" });
        var vertices = Enumerable.Range(0, 33).Select(b => new GlmVertex { Weights = [new GlmWeight(b, 1f)] }).ToArray();
        model.Lods.Add([new GlmSurface { Vertices = vertices, Triangles = [0, 1, 2] }]);

        Assert.Throws<InvalidOperationException>(() => GlmWriter.Write(model));
    }

    [Fact]
    public void GlmReader_BadMagic_ThrowsWithOffset()
    {
        var bytes = GlmWriter.Write(TwoSurfaceModel());
        bytes[0] = (byte)'X';

        var error = Assert.Throws<GameFormatException>(() => GlmReader.Parse(bytes));

        Assert.Equal(0, error.Offset);
    }

    [Fact]
    public void GlmReader_TruncatedFile_ThrowsFormatException()
    {
        var bytes = GlmWriter.Write(TwoSurfaceModel());

        Assert.Throws<GameFormatException>(() => GlmReader.Parse(bytes.AsSpan(0, bytes.Length / 2)));
    }

    [Fact]
    public void Tga_Roundtrip_KeepsPixelsAndOrientation()
    {
        // 2x2 image. The top row is red and green, the bottom row is blue and white with alpha 128.
        byte[] rgba = [255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 255, 255, 255, 128];
        var image = new DdsImage(2, 2, rgba);

        var withAlpha = TgaReader.Decode(TgaWriter.Write(image, alpha: true));
        var withoutAlpha = TgaReader.Decode(TgaWriter.Write(image, alpha: false));

        Assert.Equal(rgba, withAlpha.Rgba);
        Assert.Equal(rgba.Where((_, i) => i % 4 != 3).ToArray(), withoutAlpha.Rgba.Where((_, i) => i % 4 != 3).ToArray());
        Assert.All(Enumerable.Range(0, 4), p => Assert.Equal(255, withoutAlpha.Rgba[p * 4 + 3]));
    }

    [Fact]
    public void Tga_Downscale_HalvesUntilItFits()
    {
        var image = new DdsImage(8, 4, Enumerable.Repeat((byte)100, 8 * 4 * 4).ToArray());

        var small = TgaWriter.Downscale(image, 2);

        Assert.Equal((2, 1), (small.Width, small.Height));
        Assert.All(small.Rgba, b => Assert.Equal(100, b));
    }

    [Fact]
    public void SkinFile_Set_ReplacesCaseInsensitiveAndKeepsOrder()
    {
        var skin = SkinFile.Parse("hips,a.tga\r\nTorso,b.tga\r\n\r\nhead,c.tga\r\n");

        skin.Set("TORSO", "new.tga");
        skin.Set("l_arm", "arm.tga");

        Assert.Equal("hips,a.tga\r\nTorso,new.tga\r\nhead,c.tga\r\nl_arm,arm.tga\r\n", skin.ToString());
        Assert.Equal("c.tga", skin.Get("HEAD"));
        Assert.Null(skin.Get("missing"));
    }

    [Fact]
    public void Gr2Skeleton_BodyTypeFile_HasBindPoseInMeshSpace()
    {
        var skeleton = Gr2Skeleton.Parse(Fixture("bmanew_skeleton.gr2"));

        Assert.Equal(140, skeleton.Bones.Count);
        Vector3 Position(string name) => new(skeleton.Bones[skeleton.IndexOf(name)].World.M41, skeleton.Bones[skeleton.IndexOf(name)].World.M42, skeleton.Bones[skeleton.IndexOf(name)].World.M43);
        // The meshes have the left side at positive x, the head high up and the front at positive z.
        Assert.True(Position("LeftHip").X > 0.005f);
        Assert.Equal(-Position("LeftHip").X, Position("RightHip").X, 1e-4f);
        Assert.InRange(Position("Neck").Y, 0.15f, 0.19f);
        Assert.True(Position("Head").Y > Position("Neck").Y);
        Assert.True(Position("LeftToe").Z > Position("LeftAnkle").Z);
        // The inverse bind matrix is the inverse of the world matrix.
        var bone = skeleton.Bones[skeleton.IndexOf("LeftKnee")];
        var identity = bone.World * bone.InverseBind;
        Assert.Equal(1f, identity.M11, 1e-4f);
        Assert.Equal(0f, identity.M41, 1e-4f);
    }

    [Fact]
    public void BoneMap_NamesOfSwtorBones_GiveBonesOfTheHumanoidSkeleton()
    {
        var humanoid = JkaTestData.HumanoidNames.ToHashSet();
        var skeleton = Gr2Skeleton.Parse(Fixture("bmanew_skeleton.gr2"));

        int mapped = 0;
        foreach (var bone in skeleton.Bones)
        {
            var targets = JkaBoneMap.Map(bone.Name);
            Assert.All(targets, t => Assert.Contains(t.Bone, humanoid));
            if (targets.Count > 0) Assert.Equal(1f, targets.Sum(t => t.Fraction), 1e-5f);
            mapped += targets.Count > 0 ? 1 : 0;
        }
        Assert.True(mapped > 90, $"Only {mapped} of the SWTOR bones have a counterpart.");
    }

    [Theory]
    [InlineData("LeftShoulder", "lhumerus")]
    [InlineData("RightShoulderTwist1", "rhumerusX")]
    [InlineData("LeftUlna", "lradiusX")]
    [InlineData("RightPinkFinger2", "r_d4_j2")]
    [InlineData("LeftMiddleFinger1", "l_d2_j1")]
    [InlineData("LeftHipTwist1", "lfemurX")]
    [InlineData("RightAnkle", "rtalus")]
    [InlineData("Head", "cranium")]
    [InlineData("fc_jaw", "jaw")]
    public void BoneMap_KnownBones(string swtorBone, string expected)
    {
        var target = Assert.Single(JkaBoneMap.Map(swtorBone));

        Assert.Equal(expected, target.Bone);
    }

    [Fact]
    public void BoneMap_BonesWithoutCounterpart_GiveNothing()
    {
        Assert.Empty(JkaBoneMap.Map("vfx_head"));
        Assert.Empty(JkaBoneMap.Map("LeftWhatever"));
    }

    [Fact]
    public void BoneMap_Regions_SeparateHandsOnlyWhenAsked()
    {
        Assert.Equal(JkaRegion.LeftArm, JkaBoneMap.RegionOf("lhand", handSeparate: false));
        Assert.Equal(JkaRegion.LeftHand, JkaBoneMap.RegionOf("l_d2_j1", handSeparate: true));
        Assert.Equal(JkaRegion.Torso, JkaBoneMap.RegionOf("thoracic", handSeparate: false));
        Assert.Equal(JkaRegion.RightLeg, JkaBoneMap.RegionOf("rtibia", handSeparate: false));
    }
}
