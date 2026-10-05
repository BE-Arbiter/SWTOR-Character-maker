using System.Numerics;
using Swtor.Formats.Jka;
using Xunit;

namespace Swtor.Tests;

public class MeshSimplifierTests
{
    // A flat grid of n x n vertices with a closed border.
    private static (Vector3[] Positions, List<int> Triangles) Grid(int n)
    {
        var positions = new Vector3[n * n];
        for (int y = 0; y < n; y++) for (int x = 0; x < n; x++) positions[y * n + x] = new Vector3(x, y, 0);
        var triangles = new List<int>();
        for (int y = 0; y < n - 1; y++)
            for (int x = 0; x < n - 1; x++)
            {
                int a = y * n + x, b = a + 1, c = a + n, d = c + 1;
                triangles.AddRange([a, b, c, b, d, c]);
            }
        return (positions, triangles);
    }

    [Fact]
    public void Reduces_a_flat_grid_and_keeps_the_border()
    {
        var (positions, triangles) = Grid(20);
        var (result, source) = MeshSimplifier.Simplify(positions, triangles, 100);

        Assert.True(source.Count <= 400 && source.Count < 250);
        Assert.All(result, i => Assert.InRange(i, 0, source.Count - 1));
        Assert.Equal(0, result.Count % 3);
        // The four corners stay.
        Assert.Equal(4, source.Count(v => positions[v].X is 0 or 19 && positions[v].Y is 0 or 19));
    }

    [Fact]
    public void Does_nothing_when_under_the_target()
    {
        var (positions, triangles) = Grid(5);
        var (result, source) = MeshSimplifier.Simplify(positions, triangles, 100);
        Assert.Equal(25, source.Count);
        Assert.Equal(triangles.Count, result.Count);
    }
}
