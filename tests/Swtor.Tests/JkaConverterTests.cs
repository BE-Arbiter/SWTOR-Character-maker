using System.Numerics;
using Swtor.Formats.Gr2;
using Swtor.Formats.Jka;

namespace Swtor.Tests;

public class JkaConverterTests
{
    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", name));

    private static List<JkaSurfaceDraft> ConvertHead()
    {
        var jka = JkaTestData.HumanoidSkeleton();
        var retarget = new JkaRetarget(Gr2Skeleton.Parse(Fixture("bmanew_skeleton.gr2")), jka);
        var mesh = Gr2Reader.Parse(Fixture("head_skinned.gr2")).Meshes[0];
        var piece = new JkaSourcePiece { Slot = "head", Mesh = mesh, TriangleCount = mesh.Indices.Length / 3, TextureName = "head" };
        return JkaConverter.Convert([piece], retarget, jka);
    }

    [Fact]
    public void Retarget_Scale_MatchesTheNeckOfBothSkeletons()
    {
        var retarget = new JkaRetarget(Gr2Skeleton.Parse(Fixture("bmanew_skeleton.gr2")), JkaTestData.HumanoidSkeleton());

        // The neck of the SWTOR skeleton is at 0.168, the cervical bone is at 56.71.
        Assert.InRange(retarget.Scale, 320f, 350f);
    }

    [Fact]
    public void Convert_Head_MakesTheHeadSurfaceAtTheHeightOfTheJediAcademyHead()
    {
        var drafts = ConvertHead();

        // The fixture head has more than 1,000 vertices, so it is cut: "head", "head_2", ...
        Assert.All(drafts, d => Assert.InRange(d.Surface.Vertices.Length, 3, JkaConverter.MaxVerticesPerSurface));
        Assert.Equal(["head"], drafts.Select(d => d.Name).Take(1));
        Assert.Equal(drafts.Select((_, i) => i == 0 ? "head" : $"head_{i + 1}"), drafts.Select(d => d.Name));
        Assert.All(drafts, d => { Assert.Equal("head", d.Parent); Assert.Equal(JkaRegion.Head, d.Region); });
        var head = new GlmSurface { Vertices = drafts.SelectMany(d => d.Surface.Vertices).ToArray(), Triangles = [] };
        var heights = head.Vertices.Select(v => v.Position.Z).ToArray();
        Assert.InRange(heights.Min(), 54f, 62f);
        Assert.InRange(heights.Max(), 63f, 72f); // the fixture head has a tall crest
        // The Jedi Academy head is centered on the spine, and faces towards negative y.
        Assert.InRange(head.Vertices.Average(v => v.Position.X), -2f, 2f);
    }

    [Fact]
    public void Convert_Head_WeightsAreValidForTheFormat()
    {
        var head = ConvertHead()[0].Surface;
        var jka = JkaTestData.HumanoidSkeleton();

        Assert.All(head.Vertices, v =>
        {
            Assert.InRange(v.Weights.Length, 1, 4);
            Assert.Equal(1f, v.Weights.Sum(w => w.Weight), 1e-4f);
            Assert.All(v.Weights, w => Assert.InRange(w.Bone, 0, jka.Bones.Count - 1));
        });
        Assert.InRange(head.Vertices.SelectMany(v => v.Weights).Select(w => w.Bone).Distinct().Count(), 1, 32);
        // The head follows the cranium bone.
        int cranium = jka.IndexOf("cranium");
        Assert.True(head.Vertices.Count(v => v.Weights.OrderByDescending(w => w.Weight).First().Bone == cranium) > head.Vertices.Length / 3);
    }

    [Fact]
    public void Convert_Head_TrianglesTurnClockwiseLikeTheJediAcademyModels()
    {
        var head = ConvertHead()[0].Surface;

        int clockwise = 0;
        for (int t = 0; t < head.Triangles.Length; t += 3)
        {
            var a = head.Vertices[head.Triangles[t]];
            var b = head.Vertices[head.Triangles[t + 1]];
            var c = head.Vertices[head.Triangles[t + 2]];
            if (Vector3.Dot(Vector3.Cross(b.Position - a.Position, c.Position - a.Position), a.Normal + b.Normal + c.Normal) < 0) clockwise++;
        }
        Assert.True(clockwise > head.Triangles.Length / 3 * 0.95, $"{clockwise} of {head.Triangles.Length / 3} triangles are clockwise.");
    }

    [Fact]
    public void Convert_Head_KeepsTheTextureCoordinates()
    {
        var mesh = Gr2Reader.Parse(Fixture("head_skinned.gr2")).Meshes[0];
        var head = ConvertHead()[0].Surface;

        // Every converted vertex has the UV of some source vertex. V is not flipped.
        var sources = mesh.UvSets[0].ToHashSet();
        Assert.All(head.Vertices, v => Assert.Contains(v.Uv, sources));
    }

    [Theory]
    [InlineData("head", "", 0, JkaRegion.Head, "head")]
    [InlineData("head", "eyes", 0, JkaRegion.Head, "head_eyes")]
    [InlineData("hair", "", 0, JkaRegion.Head, "head_hair")]
    [InlineData("facehair", "", 0, JkaRegion.Head, "head_facehair")]
    [InlineData("chest", "", 0, JkaRegion.Torso, "torso")]
    [InlineData("chest", "", 0, JkaRegion.RightArm, "r_arm")]
    [InlineData("chest", "", 2, JkaRegion.Torso, "torso_chest2")]
    [InlineData("hand", "", 0, JkaRegion.LeftHand, "l_hand")]
    [InlineData("leg", "", 0, JkaRegion.Hips, "hips")]
    [InlineData("leg", "", 0, JkaRegion.LeftLeg, "l_leg")]
    [InlineData("boot", "", 0, JkaRegion.RightLeg, "r_leg_boot")]
    [InlineData("waist", "", 0, JkaRegion.Hips, "hips_waist")]
    [InlineData("bracer", "", 0, JkaRegion.LeftArm, "l_arm_bracer")]
    public void SurfaceName_FollowsSlotAndRegion(string slot, string role, int index, JkaRegion region, string expected)
    {
        var mesh = Gr2Reader.Parse(Fixture("head_skinned.gr2")).Meshes[0];
        var piece = new JkaSourcePiece { Slot = slot, Mesh = mesh, TriangleCount = 1, TextureName = "x", Role = role, Index = index };

        Assert.Equal(expected, JkaConverter.SurfaceName(piece, region));
    }

    [Fact]
    public void SplitByVertexLimit_KeepsSmallSurfacesAndCutsLargeOnes()
    {
        // A strip of 1,500 triangles: triangle t uses vertices t, t+1, t+2, so neighbours share two vertices.
        var triangles = new List<int>();
        for (int t = 0; t < 1500; t++) triangles.AddRange([t, t + 1, t + 2]);
        var source = Enumerable.Range(0, 1502).Select(i => i + 5000).ToList();

        var small = JkaConverter.SplitByVertexLimit([0, 1, 2], [7, 8, 9]);
        var parts = JkaConverter.SplitByVertexLimit(triangles, source);

        Assert.Single(small);
        Assert.True(parts.Count >= 2);
        Assert.All(parts, p => Assert.InRange(p.Source.Count, 3, JkaConverter.MaxVerticesPerSurface));
        Assert.Equal(1500, parts.Sum(p => p.Triangles.Count / 3));
        Assert.All(parts, p => Assert.All(p.Triangles, i => Assert.InRange(i, 0, p.Source.Count - 1)));
        // The corners of every triangle point to the same mesh vertices as before.
        var rebuilt = parts.SelectMany(p => p.Triangles.Select(i => p.Source[i])).ToList();
        Assert.Equal(triangles.Select(i => source[i]), rebuilt);
    }

    [Fact]
    public void LimitTrianglesPerVertex_CopiesTheCenterOfALargeFan()
    {
        // A fan of 100 triangles around vertex 0 (mesh vertex 500).
        var triangles = new List<int>();
        for (int t = 0; t < 100; t++) triangles.AddRange([0, t + 1, t + 2]);
        var source = Enumerable.Range(0, 102).Select(i => i + 500).ToList();
        var before = triangles.Select(i => source[i]).ToList();

        JkaConverter.LimitTrianglesPerVertex(triangles, source);

        Assert.Equal(102 + 3, source.Count); // 100 uses: the vertex and 3 copies
        Assert.All(triangles.GroupBy(i => i), g => Assert.InRange(g.Count(), 1, JkaConverter.MaxTrianglesPerVertex));
        Assert.Equal(before, triangles.Select(i => source[i]));
    }

    [Fact]
    public void Convert_Head_UsesNoVertexInMoreThan32Triangles()
    {
        foreach (var draft in ConvertHead())
            Assert.All(draft.Surface.Triangles.GroupBy(i => i), g => Assert.InRange(g.Count(), 1, JkaConverter.MaxTrianglesPerVertex));
    }
}
