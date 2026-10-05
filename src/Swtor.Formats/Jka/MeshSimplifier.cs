using System.Numerics;

namespace Swtor.Formats.Jka;

/// <summary>
/// Reduces the vertex count of a triangle mesh by edge collapse with quadric error (Garland and Heckbert).
/// A collapse moves one vertex onto a neighbor and keeps the attributes (UV, weights) of the neighbor, so nothing is interpolated.
/// Vertices on a boundary (open edge, so also texture seams and region borders) are only removed when they lie on a straight boundary.
/// </summary>
public static class MeshSimplifier
{
    // A collapse is refused when it turns a triangle by more than about 80 degrees.
    private const float MinNormalDot = 0.17f;

    /// <summary>Removes vertices until at most <paramref name="targetVertices"/> remain, or until no safe collapse is left.</summary>
    /// <param name="positions">Position of each vertex.</param>
    /// <param name="triangles">Three vertex numbers per triangle.</param>
    /// <param name="uvs">Texture coordinates. When given, a vertex on a texture seam only slides along a straight seam.</param>
    /// <param name="maxTrianglesPerVertex">A collapse is refused when the kept vertex would be used by more triangles. 0 for no limit.</param>
    /// <returns>New triangles (numbers in the new order) and, for each new vertex, its number in the input.</returns>
    public static (List<int> Triangles, List<int> Source) Simplify(IReadOnlyList<Vector3> positions, IReadOnlyList<int> triangles, int targetVertices,
        IReadOnlyList<Vector2>? uvs = null, int maxTrianglesPerVertex = 0)
    {
        int n = positions.Count, triCount = triangles.Count / 3;
        var tri = new int[triangles.Count];
        for (int i = 0; i < tri.Length; i++) tri[i] = triangles[i];
        var triAlive = new bool[triCount];
        Array.Fill(triAlive, true);

        var adjacent = new List<int>[n];
        for (int i = 0; i < n; i++) adjacent[i] = [];
        for (int t = 0; t < triCount; t++)
            for (int k = 0; k < 3; k++) adjacent[tri[t * 3 + k]].Add(t);

        var quadrics = new Quadric[n];
        for (int t = 0; t < triCount; t++)
        {
            var (a, b, c) = (positions[tri[t * 3]], positions[tri[t * 3 + 1]], positions[tri[t * 3 + 2]]);
            var cross = Vector3.Cross(b - a, c - a);
            float area2 = cross.Length();
            if (area2 < 1e-12f) continue;
            var normal = cross / area2;
            var q = Quadric.FromPlane(normal, -Vector3.Dot(normal, a), area2);
            for (int k = 0; k < 3; k++) quadrics[tri[t * 3 + k]].Add(q);
        }


        var alive = new bool[n];
        int aliveCount = 0;
        for (int v = 0; v < n; v++)
            if (adjacent[v].Count > 0) { alive[v] = true; aliveCount++; }

        var version = new int[n];
        var queue = new PriorityQueue<(int From, int To, int VersionFrom, int VersionTo), float>();
        var ring = new HashSet<int>();

        var uses = new Dictionary<int, int>();

        // Queues the moves of vertex v. An inner vertex may move onto any neighbor. A vertex on a boundary (open edge: texture seam
        // or border of the surface) may only slide along it, onto one of its two boundary neighbors, and only when it lies on the
        // line between them (in position and in UV). Other vertices stay.
        void Push(int v)
        {
            uses.Clear();
            foreach (int t in adjacent[v])
                for (int k = 0; k < 3; k++)
                {
                    int x = tri[t * 3 + k];
                    if (x != v) uses[x] = uses.GetValueOrDefault(x) + 1;
                }
            ring.Clear();
            foreach (var (x, count) in uses)
            {
                if (count > 2) return;
                if (count == 1) ring.Add(x);
            }
            bool boundary = ring.Count > 0;
            if (boundary && ring.Count != 2) return;
            if (boundary)
            {
                int a = ring.First(), b = ring.Last();
                if (!OnLine(positions[v], positions[a], positions[b]) || (uvs is not null && !OnLine(Uv3(uvs[v]), Uv3(uvs[a]), Uv3(uvs[b])))) return;
            }
            else foreach (int x in uses.Keys) ring.Add(x);
            foreach (int u in ring)
            {
                var q = quadrics[v];
                q.Add(quadrics[u]);
                queue.Enqueue((v, u, version[v], version[u]), q.Error(positions[u]));
            }
        }

        for (int v = 0; v < n; v++) if (alive[v]) Push(v);

        var touched = new List<int>();
        while (aliveCount > targetVertices && queue.TryDequeue(out var move, out _))
        {
            var (from, to, versionFrom, versionTo) = move;
            if (!alive[from] || !alive[to] || version[from] != versionFrom || version[to] != versionTo) continue;
            if (!CanCollapse(from, to, positions, tri, triAlive, adjacent)) continue;
            if (maxTrianglesPerVertex > 0 && TrianglesAfter(from, to, tri, adjacent) > maxTrianglesPerVertex) continue;

            touched.Clear();
            foreach (int t in adjacent[from].ToArray())
            {
                if (!triAlive[t]) continue;
                bool hasTarget = tri[t * 3] == to || tri[t * 3 + 1] == to || tri[t * 3 + 2] == to;
                if (hasTarget)
                {
                    triAlive[t] = false;
                    for (int k = 0; k < 3; k++) adjacent[tri[t * 3 + k]].Remove(t);
                    continue;
                }
                for (int k = 0; k < 3; k++) if (tri[t * 3 + k] == from) tri[t * 3 + k] = to;
                adjacent[to].Add(t);
            }
            alive[from] = false;
            aliveCount--;
            adjacent[from].Clear();
            quadrics[to].Add(quadrics[from]);

            foreach (int t in adjacent[to])
                for (int k = 0; k < 3; k++) touched.Add(tri[t * 3 + k]);
            version[to]++;
            foreach (int w in touched) version[w]++;
            Push(to);
            foreach (int w in touched.Distinct()) if (w != to) Push(w);
        }

        var newIndex = new int[n];
        Array.Fill(newIndex, -1);
        var source = new List<int>();
        var result = new List<int>();
        for (int t = 0; t < triCount; t++)
        {
            if (!triAlive[t]) continue;
            for (int k = 0; k < 3; k++)
            {
                int v = tri[t * 3 + k];
                if (newIndex[v] < 0) { newIndex[v] = source.Count; source.Add(v); }
                result.Add(newIndex[v]);
            }
        }
        return (result, source);
    }

    private static Vector3 Uv3(Vector2 uv) => new(uv.X, uv.Y, 0);

    // True when p is on the segment a-b, up to 15% of its length.
    private static bool OnLine(Vector3 p, Vector3 a, Vector3 b)
    {
        var ab = b - a;
        float length = ab.Length();
        if (length < 1e-9f) return true;
        if (Vector3.Cross(p - a, ab).Length() / length > 0.15f * length) return false;
        // Not a corner: the two edges at p must be nearly opposite (angle above about 160 degrees).
        var (pa, pb) = (a - p, b - p);
        float lengths = pa.Length() * pb.Length();
        return lengths < 1e-12f || Vector3.Dot(pa, pb) / lengths < -0.94f;
    }

    // Number of triangles that use vertex "to" after the collapse. The triangles that hold both vertices disappear.
    private static int TrianglesAfter(int from, int to, int[] tri, List<int>[] adjacent)
    {
        int shared = 0;
        foreach (int t in adjacent[from])
            if (tri[t * 3] == to || tri[t * 3 + 1] == to || tri[t * 3 + 2] == to) shared++;
        return adjacent[to].Count + adjacent[from].Count - 2 * shared;
    }

    // Refuses a collapse that flips or crushes the triangles around the removed vertex.
    private static bool CanCollapse(int from, int to, IReadOnlyList<Vector3> positions, int[] tri, bool[] triAlive, List<int>[] adjacent)
    {
        foreach (int t in adjacent[from])
        {
            if (!triAlive[t]) continue;
            int a = tri[t * 3], b = tri[t * 3 + 1], c = tri[t * 3 + 2];
            if (a == to || b == to || c == to) continue;
            var before = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
            if (a == from) a = to; else if (b == from) b = to; else c = to;
            var after = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
            float lengths = before.Length() * after.Length();
            if (lengths < 1e-20f || Vector3.Dot(before, after) / lengths < MinNormalDot) return false;
        }
        return true;
    }

    // Sum of squared distances to a set of planes, stored as a symmetric 4x4 matrix.
    private struct Quadric
    {
        private double _a, _b, _c, _d, _e, _f, _g, _h, _i, _j;

        public static Quadric FromPlane(Vector3 n, float d, float weight)
        {
            double x = n.X, y = n.Y, z = n.Z, w = weight;
            return new Quadric
            {
                _a = w * x * x, _b = w * x * y, _c = w * x * z, _d = w * x * d,
                _e = w * y * y, _f = w * y * z, _g = w * y * d,
                _h = w * z * z, _i = w * z * d, _j = w * d * d,
            };
        }

        public void Add(Quadric q)
        {
            _a += q._a; _b += q._b; _c += q._c; _d += q._d; _e += q._e;
            _f += q._f; _g += q._g; _h += q._h; _i += q._i; _j += q._j;
        }

        public readonly float Error(Vector3 p)
        {
            double x = p.X, y = p.Y, z = p.Z;
            return (float)(_a * x * x + 2 * _b * x * y + 2 * _c * x * z + 2 * _d * x
                + _e * y * y + 2 * _f * y * z + 2 * _g * y + _h * z * z + 2 * _i * z + _j);
        }
    }
}
