using System.Numerics;
using Swtor.Formats;
using Swtor.Formats.Gr2;

namespace Swtor.Tests;

public class Gr2ReaderTests
{
    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", name));

    [Fact]
    public void Parse_SkinnedHead_ReadsCountsAndNames()
    {
        var model = Gr2Reader.Parse(Fixture("head_skinned.gr2"));

        Assert.Equal(5, model.Version);
        Assert.Equal(["default"], model.Materials);
        var mesh = Assert.Single(model.Meshes);
        Assert.Equal("bmn_fenzeil_a02", mesh.Name);
        Assert.Equal(2377, mesh.VertexCount);
        Assert.Equal(3294 * 3, mesh.Indices.Length);
        Assert.Equal(22, mesh.Bones.Count);
        Assert.Equal("Head", mesh.Bones[3].Name);
        var piece = Assert.Single(mesh.Pieces);
        Assert.Equal((0, 3294, 0), (piece.StartTriangle, piece.TriangleCount, piece.MaterialIndex));
    }

    [Fact]
    public void Parse_SkinnedHead_VertexDataIsConsistent()
    {
        var model = Gr2Reader.Parse(Fixture("head_skinned.gr2"));
        var mesh = model.Meshes[0];

        Assert.All(mesh.Positions, p =>
        {
            Assert.InRange(p.X, model.BoundsMin.X - 1e-4f, model.BoundsMax.X + 1e-4f);
            Assert.InRange(p.Y, model.BoundsMin.Y - 1e-4f, model.BoundsMax.Y + 1e-4f);
            Assert.InRange(p.Z, model.BoundsMin.Z - 1e-4f, model.BoundsMax.Z + 1e-4f);
        });
        // Weights are stored as bytes, so the sum is 1 within rounding error.
        Assert.All(mesh.BoneWeights!, w => Assert.InRange(w.X + w.Y + w.Z + w.W, 0.98f, 1.02f));
        Assert.All(mesh.Normals!, n => Assert.InRange(n.Length(), 0.9f, 1.1f));
        Assert.All(mesh.UvSets[0], uv => Assert.True(float.IsFinite(uv.X) && float.IsFinite(uv.Y)));
    }

    [Fact]
    public void Parse_WrongMagic_Throws()
    {
        var data = Fixture("head_skinned.gr2");
        data[0] = (byte)'X';
        var e = Assert.Throws<GameFormatException>(() => Gr2Reader.Parse(data));
        Assert.Equal(0, e.Offset);
    }

    [Fact]
    public void Parse_Truncated_ThrowsInsteadOfCrashing()
    {
        var data = Fixture("head_skinned.gr2");
        Assert.Throws<GameFormatException>(() => Gr2Reader.Parse(data.AsSpan(0, data.Length / 2)));
    }
}
