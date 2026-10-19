using System.Numerics;

namespace Swtor.Formats.Jka;

/// <summary>
/// Reduces the vertex count of a triangle mesh by edge collapse with quadric error (Garland and Heckbert).
/// The mesh is welded by position for the topology: vertices at the same point (hard edges, texture seams) are one "point".
/// A collapse moves one point onto a neighbor point. Each copy of the removed point (one per set of triangles that share
/// its UV and weights) is replaced by the copy of the target that its triangles already touch. A copy with no such partner
/// stays and moves to the target position, with its own UV and weights. So the surface never opens along a seam.
/// Points on a true boundary (open edge, so also region borders) are only removed when they lie on a straight boundary.
/// </summary>
public static class MeshSimplifier
{
    // A collapse is refused when it turns a triangle by more than about 45 degrees.
    private const float MinNormalDot = 0.7f;

    // A boundary point may slide only when it is this close to the line between its two neighbors (fraction of the line length).
    // The surface on the other side of a border is simplified alone, so a large value opens cracks between the two.
    private const float BoundaryTolerance = 0.01f;


    // Edge length (Jedi Academy units) that is added to the size of an edge when the cost is divided by it, so tiny edges do not get a huge cost.
    private const float SizeFloor = 0.1f;

    // Largest deviation (Jedi Academy units) for a collapse of a hidden point to stay free.
    private const float HiddenMaxDeviation = 1.0f;

    // Positions closer than 1e-4 are the same point.
    private const float WeldScale = 1e4f;

    /// <summary>Removes vertices until at most <paramref name="targetVertices"/> remain, or until no safe collapse is left.</summary>
    /// <param name="positions">Position of each vertex.</param>
    /// <param name="triangles">Three vertex numbers per triangle.</param>
    /// <param name="maxTrianglesPerVertex">A collapse is refused when a kept vertex would be used by more triangles. 0 for no limit.</param>
    /// <param name="maxError">The reduction stops at the first collapse that costs more than this quadric error.</param>
    /// <param name="hiddenTriangles">Per triangle: true when nobody sees it. A point whose triangles are all hidden is removed first, at no cost (only the flip test stays), so a hidden area shrinks to the vertices of its border.</param>
    /// <param name="errorLog">When given, receives the error of every vertex that was removed (to choose a threshold for several meshes).</param>
    /// <returns>
    /// New triangles (numbers in the new order) and, for each new vertex, its number in the input and its position
    /// (a vertex that had no partner at a collapse is moved, so its position can differ from the input).
    /// </returns>
    public static (List<int> Triangles, List<int> Source, List<Vector3> Positions) Simplify(IReadOnlyList<Vector3> positions, IReadOnlyList<int> triangles,
        int targetVertices, int maxTrianglesPerVertex = 0, float maxError = float.PositiveInfinity, List<float>? errorLog = null, bool[]? hiddenTriangles = null)
    {
        int n = positions.Count, triCount = triangles.Count / 3;
        var tri = new int[triangles.Count];
        for (int i = 0; i < tri.Length; i++) tri[i] = triangles[i];
        var triAlive = new bool[triCount];
        Array.Fill(triAlive, true);

        // Per vertex (copy): its triangles. Per point: the triangles that touch any of its copies, and the copies.
        var vertexTriangles = new List<int>[n];
        for (int i = 0; i < n; i++) vertexTriangles[i] = [];
        for (int t = 0; t < triCount; t++)
            for (int k = 0; k < 3; k++) vertexTriangles[tri[t * 3 + k]].Add(t);

        var pos = new Vector3[n];
        for (int i = 0; i < n; i++) pos[i] = positions[i];

        // Welding: point number = number of the first vertex at that position.
        var weld = new int[n];
        var byKey = new Dictionary<(int, int, int), int>();
        for (int v = 0; v < n; v++)
        {
            if (vertexTriangles[v].Count == 0) { weld[v] = v; continue; }
            var p = pos[v];
            var key = ((int)MathF.Round(p.X * WeldScale), (int)MathF.Round(p.Y * WeldScale), (int)MathF.Round(p.Z * WeldScale));
            if (!byKey.TryGetValue(key, out int first)) byKey[key] = first = v;
            weld[v] = first;
        }
        var pointTriangles = new List<int>[n];
        var copies = new List<int>[n];
        for (int i = 0; i < n; i++) { pointTriangles[i] = []; copies[i] = []; }
        for (int v = 0; v < n; v++)
            if (vertexTriangles[v].Count > 0) copies[weld[v]].Add(v);
        for (int t = 0; t < triCount; t++)
            for (int k = 0; k < 3; k++)
            {
                var list = pointTriangles[weld[tri[t * 3 + k]]];
                if (!list.Contains(t)) list.Add(t);
            }

        var quadrics = new Quadric[n];
        for (int t = 0; t < triCount; t++)
        {
            var (a, b, c) = (pos[tri[t * 3]], pos[tri[t * 3 + 1]], pos[tri[t * 3 + 2]]);
            var cross = Vector3.Cross(b - a, c - a);
            float area2 = cross.Length();
            if (area2 < 1e-12f) continue;
            var normal = cross / area2;
            var q = Quadric.FromPlane(normal, -Vector3.Dot(normal, a), area2);
            for (int k = 0; k < 3; k++) quadrics[weld[tri[t * 3 + k]]].Add(q);
        }

        var pointAlive = new bool[n];
        var vertexAlive = new bool[n];
        int aliveCount = 0;
        for (int v = 0; v < n; v++)
        {
            if (vertexTriangles[v].Count == 0) continue;
            vertexAlive[v] = true;
            aliveCount++;
            pointAlive[weld[v]] = true;
        }

        var version = new int[n];
        var queue = new PriorityQueue<(int From, int To, int VersionFrom, int VersionTo), float>();
        var ring = new HashSet<int>();
        var uses = new Dictionary<int, int>();

        // Targets that point a may move onto. An inner point may move onto any neighbor. A point on a boundary (open edge: border of
        // the surface) may only slide along it, onto one of its two boundary neighbors, and only when it lies on the line between them.
        // Other points stay. A point that more than two triangles share on one edge (overlapping layers) stays too.
        void Candidates(int a, List<int> output)
        {
            output.Clear();
            uses.Clear();
            foreach (int t in pointTriangles[a])
                for (int k = 0; k < 3; k++)
                {
                    int x = weld[tri[t * 3 + k]];
                    if (x != a) uses[x] = uses.GetValueOrDefault(x) + 1;
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
                int p = ring.First(), q = ring.Last();
                if (!OnLine(pos[a], pos[p], pos[q])) return;
            }
            else foreach (int x in uses.Keys) ring.Add(x);
            output.AddRange(ring);
        }

        var candidates = new List<int>();
        void Push(int a)
        {
            Candidates(a, candidates);
            foreach (int b in candidates)
            {
                var q = quadrics[a];
                q.Add(quadrics[b]);
                // Cost in distance units, not area times distance squared: the quadric error makes small details (a buckle) look cheap,
                // so it only gives the average distance to the original planes (this catches slow drift). The deviation from the
                // planes of the triangles that are replaced catches a local jump.
                float rms = MathF.Sqrt(MathF.Max(q.Error(pos[b]), 0) / MathF.Max(q.Weight, 1e-12f));
                // The same distance is worse on a small detail than on a big panel: divide by the size of the edge (plus a floor).
                float deviation = Deviation(a, b, pos, tri, triAlive, pointTriangles, weld);
                // A hidden point is free, but only while it stays close to its triangles: the hidden layer lies just under the visible
                // one, and a stretched triangle would poke through it.
                bool hidden = hiddenTriangles is not null && deviation <= HiddenMaxDeviation && pointTriangles[a].TrueForAll(t => hiddenTriangles[t])
                    && MovesInward(a, b, pos, tri, triAlive, pointTriangles, weld);
                float cost = hidden ? 0f : MathF.Max(rms, deviation) / (Vector3.Distance(pos[a], pos[b]) + SizeFloor);
                queue.Enqueue((a, b, version[a], version[b]), cost);
            }
        }

        for (int a = 0; a < n; a++) if (pointAlive[a]) Push(a);

        var touched = new List<int>();
        var dying = new List<int>();
        var mates = new Dictionary<int, int>();
        while (aliveCount > targetVertices && queue.TryDequeue(out var move, out float error))
        {
            var (from, to, versionFrom, versionTo) = move;
            if (!pointAlive[from] || !pointAlive[to] || version[from] != versionFrom || version[to] != versionTo) continue;
            if (error > maxError) break;
            if (!CanCollapse(from, to, pos, tri, triAlive, pointTriangles, weld)) continue;

            // Triangles that hold both points disappear. A copy of "from" that is in one of them is replaced by the copy of "to"
            // in the same triangle.
            dying.Clear();
            mates.Clear();
            foreach (int t in pointTriangles[from])
            {
                int copyFrom = -1, copyTo = -1;
                for (int k = 0; k < 3; k++)
                {
                    int v = tri[t * 3 + k];
                    if (weld[v] == from) copyFrom = v;
                    else if (weld[v] == to) copyTo = v;
                }
                if (copyTo < 0) continue;
                dying.Add(t);
                mates.TryAdd(copyFrom, copyTo);
            }

            bool possible = true;
            if (maxTrianglesPerVertex > 0)
                foreach (var (w, x) in mates)
                {
                    int shared = 0;
                    foreach (int t in dying) if (tri[t * 3] == w || tri[t * 3 + 1] == w || tri[t * 3 + 2] == w) shared++;
                    if (vertexTriangles[x].Count + vertexTriangles[w].Count - 2 * shared > maxTrianglesPerVertex) { possible = false; break; }
                }
            if (!possible) continue;

            touched.Clear();
            foreach (int t in dying)
            {
                triAlive[t] = false;
                for (int k = 0; k < 3; k++)
                {
                    int v = tri[t * 3 + k];
                    vertexTriangles[v].Remove(t);
                    pointTriangles[weld[v]].Remove(t);
                }
            }
            foreach (int t in pointTriangles[from].ToArray())
            {
                for (int k = 0; k < 3; k++)
                {
                    int v = tri[t * 3 + k];
                    if (weld[v] != from || !mates.TryGetValue(v, out int x)) continue;
                    tri[t * 3 + k] = x;
                    vertexTriangles[v].Remove(t);
                    vertexTriangles[x].Add(t);
                }
                pointTriangles[to].Add(t);
            }
            foreach (int w in copies[from])
            {
                if (mates.ContainsKey(w)) continue;
                weld[w] = to;
                pos[w] = pos[to];
                copies[to].Add(w);
            }
            // A vertex with no triangle left is gone.
            foreach (int t in dying)
                for (int k = 0; k < 3; k++)
                {
                    int v = tri[t * 3 + k];
                    if (vertexAlive[v] && vertexTriangles[v].Count == 0) { vertexAlive[v] = false; aliveCount--; errorLog?.Add(error); }
                }
            copies[from].Clear();
            pointTriangles[from].Clear();
            pointAlive[from] = false;
            quadrics[to].Add(quadrics[from]);

            touched.Add(to);
            foreach (int t in pointTriangles[to])
                for (int k = 0; k < 3; k++) touched.Add(weld[tri[t * 3 + k]]);
            var changed = touched.Distinct().ToArray();
            foreach (int a in changed) version[a]++;
            foreach (int a in changed) if (pointAlive[a]) Push(a);
        }

        var newIndex = new int[n];
        Array.Fill(newIndex, -1);
        var source = new List<int>();
        var newPositions = new List<Vector3>();
        var result = new List<int>();
        for (int t = 0; t < triCount; t++)
        {
            if (!triAlive[t]) continue;
            for (int k = 0; k < 3; k++)
            {
                int v = tri[t * 3 + k];
                if (newIndex[v] < 0) { newIndex[v] = source.Count; source.Add(v); newPositions.Add(pos[v]); }
                result.Add(newIndex[v]);
            }
        }
        return (result, source, newPositions);
    }

    // True when p is on the segment a-b, up to BoundaryTolerance of its length.
    private static bool OnLine(Vector3 p, Vector3 a, Vector3 b)
    {
        var ab = b - a;
        float length = ab.Length();
        if (length < 1e-9f) return true;
        if (Vector3.Cross(p - a, ab).Length() / length > BoundaryTolerance * length) return false;
        // Not a corner: the two edges at p must be nearly opposite (angle above about 160 degrees).
        var (pa, pb) = (a - p, b - p);
        float lengths = pa.Length() * pb.Length();
        return lengths < 1e-12f || Vector3.Dot(pa, pb) / lengths < -0.94f;
    }

    // True when moving point "from" onto "to" does not push the surface outward (it may pull it inward). The Jedi Academy triangles are
    // clockwise seen from outside, so the cross product points inward. A hidden layer that grows outward would poke through the
    // visible one, for example where the body is concave under a shoulder pad.
    private static bool MovesInward(int from, int to, Vector3[] pos, int[] tri, bool[] triAlive, List<int>[] pointTriangles, int[] weld)
    {
        var inward = Vector3.Zero;
        foreach (int t in pointTriangles[from])
        {
            if (!triAlive[t]) continue;
            int a = weld[tri[t * 3]], b = weld[tri[t * 3 + 1]], c = weld[tri[t * 3 + 2]];
            inward += Vector3.Cross(pos[b] - pos[a], pos[c] - pos[a]);
        }
        float length = inward.Length();
        return length < 1e-12f || Vector3.Dot(Vector3.Normalize(pos[to] - pos[from]), inward / length) >= -0.02f;
    }

    // Largest distance between the target point and the plane of a triangle that the removed point leaves.
    private static float Deviation(int from, int to, Vector3[] pos, int[] tri, bool[] triAlive, List<int>[] pointTriangles, int[] weld)
    {
        float worst = 0;
        foreach (int t in pointTriangles[from])
        {
            if (!triAlive[t]) continue;
            int a = weld[tri[t * 3]], b = weld[tri[t * 3 + 1]], c = weld[tri[t * 3 + 2]];
            if (a == to || b == to || c == to) continue;
            var cross = Vector3.Cross(pos[b] - pos[a], pos[c] - pos[a]);
            float length = cross.Length();
            if (length < 1e-12f) continue;
            worst = MathF.Max(worst, MathF.Abs(Vector3.Dot(cross / length, pos[to] - pos[from])));
        }
        return worst;
    }

    // Refuses a collapse that flips or crushes the triangles around the removed point.
    private static bool CanCollapse(int from, int to, Vector3[] pos, int[] tri, bool[] triAlive, List<int>[] pointTriangles, int[] weld)
    {
        foreach (int t in pointTriangles[from])
        {
            if (!triAlive[t]) continue;
            int a = weld[tri[t * 3]], b = weld[tri[t * 3 + 1]], c = weld[tri[t * 3 + 2]];
            if (a == to || b == to || c == to) continue;
            var before = Vector3.Cross(pos[b] - pos[a], pos[c] - pos[a]);
            if (a == from) a = to; else if (b == from) b = to; else c = to;
            var after = Vector3.Cross(pos[b] - pos[a], pos[c] - pos[a]);
            float lengths = before.Length() * after.Length();
            if (lengths < 1e-20f || Vector3.Dot(before, after) / lengths < MinNormalDot) return false;
        }
        return true;
    }

    // Sum of squared distances to a set of planes, stored as a symmetric 4x4 matrix.
    private struct Quadric
    {
        private double _a, _b, _c, _d, _e, _f, _g, _h, _i, _j, _w;

        public static Quadric FromPlane(Vector3 n, float d, float weight)
        {
            double x = n.X, y = n.Y, z = n.Z, w = weight;
            return new Quadric
            {
                _a = w * x * x, _b = w * x * y, _c = w * x * z, _d = w * x * d,
                _e = w * y * y, _f = w * y * z, _g = w * y * d,
                _h = w * z * z, _i = w * z * d, _j = w * d * d, _w = w,
            };
        }

        public void Add(Quadric q)
        {
            _a += q._a; _b += q._b; _c += q._c; _d += q._d; _e += q._e;
            _f += q._f; _g += q._g; _h += q._h; _i += q._i; _j += q._j; _w += q._w;
        }

        public readonly float Weight => (float)_w;

        public readonly float Error(Vector3 p)
        {
            double x = p.X, y = p.Y, z = p.Z;
            return (float)(_a * x * x + 2 * _b * x * y + 2 * _c * x * z + 2 * _d * x
                + _e * y * y + 2 * _f * y * z + 2 * _g * y + _h * z * z + 2 * _i * z + _j);
        }
    }
}
