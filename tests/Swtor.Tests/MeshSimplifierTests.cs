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
        var (result, source, _) = MeshSimplifier.Simplify(positions, triangles, 100);

        Assert.True(source.Count <= 400 && source.Count < 250);
        Assert.All(result, i => Assert.InRange(i, 0, source.Count - 1));
        Assert.Equal(0, result.Count % 3);
        // The four corners stay.
        Assert.Equal(4, source.Count(v => positions[v].X is 0 or 19 && positions[v].Y is 0 or 19));
    }

    [Fact]
    public void Collapses_the_copies_of_a_seam_together_so_no_crack_opens()
    {
        // Two 10 x 10 grids side by side that touch at x = 9 with separate vertices (a seam).
        var (p1, t1) = Grid(10);
        var positions = new List<Vector3>(p1);
        positions.AddRange(p1.Select(p => p + new Vector3(9, 0, 0)));
        var triangles = new List<int>(t1);
        triangles.AddRange(t1.Select(i => i + 100));

        var (_, source, moved) = MeshSimplifier.Simplify(positions, triangles, 20);

        // The seam of each side is made of the same points, and it got shorter.
        var left = source.Select((v, k) => (v, k)).Where(e => e.v < 100 && moved[e.k].X == 9).Select(e => moved[e.k].Y).Distinct().Order().ToArray();
        var right = source.Select((v, k) => (v, k)).Where(e => e.v >= 100 && moved[e.k].X == 9).Select(e => moved[e.k].Y).Distinct().Order().ToArray();
        Assert.Equal(left, right);
        Assert.True(left.Length < 10);
    }

    [Fact]
    public void A_hidden_area_shrinks_to_its_border_and_the_visible_part_keeps_its_vertices()
    {
        // 12 x 12 grid, bumpy so that no collapse is free by the quadric error. The middle 6 x 6 squares are hidden.
        var (positions, triangles) = Grid(12);
        for (int i = 0; i < positions.Length; i++) positions[i].Z = (i * 7 % 5) * 0.2f;
        var hidden = new bool[triangles.Count / 3];
        for (int y = 3; y < 9; y++)
            for (int x = 3; x < 9; x++)
            {
                int square = y * 11 + x;
                hidden[square * 2] = hidden[square * 2 + 1] = true;
            }

        var (_, source, _) = MeshSimplifier.Simplify(positions, triangles, 0, maxError: 1e-9f, hiddenTriangles: hidden);

        // Vertices strictly inside the hidden area (x and y from 4 to 8) are gone, the rest stay.
        int inside = source.Count(v => v % 12 is >= 4 and <= 8 && v / 12 is >= 4 and <= 8);
        Assert.True(inside < 12); // of 25
        Assert.True(source.Count >= 144 - 25);
    }

    [Fact]
    public void Does_nothing_when_under_the_target()
    {
        var (positions, triangles) = Grid(5);
        var (result, source, _) = MeshSimplifier.Simplify(positions, triangles, 100);
        Assert.Equal(25, source.Count);
        Assert.Equal(triangles.Count, result.Count);
    }
}
