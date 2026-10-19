using System.Numerics;

namespace Swtor.Formats.Jka;

/// <summary>
/// Finds triangles that nobody can see in the bind pose: the bare body under an armor, the inside layer of a shell.
/// A triangle is hidden when, from each of a few points on it, every one of 26 rays (the faces, edges and corners of a cube)
/// hits another triangle. One free ray keeps the triangle, so a gap at the neck or a sleeve opening keeps what lies behind it.
/// </summary>
public static class HiddenGeometry
{
    // Rays start this far from the surface (Jedi Academy units) so that a triangle does not hit itself or its flat neighbors.
    private const float RayStart = 0.02f;

    // Barycentric weights of the points that are tested on each triangle: the center and three points near the corners.
    private static readonly Vector3[] Samples =
    [
        new(1 / 3f, 1 / 3f, 1 / 3f), new(0.7f, 0.15f, 0.15f), new(0.15f, 0.7f, 0.15f), new(0.15f, 0.15f, 0.7f),
    ];

    private static readonly Vector3[] Directions = BuildDirections();

    private static Vector3[] BuildDirections()
    {
        var list = new List<Vector3>();
        for (int x = -1; x <= 1; x++)
            for (int y = -1; y <= 1; y++)
                for (int z = -1; z <= 1; z++)
                    if (x != 0 || y != 0 || z != 0) list.Add(Vector3.Normalize(new Vector3(x, y, z)));
        return [.. list];
    }

    /// <summary>For each surface, a flag per triangle: true when the triangle is hidden. Only surfaces with <paramref name="candidate"/> are tested; all surfaces hide others.</summary>
    public static bool[][] Find(IReadOnlyList<GlmSurface> surfaces, Func<int, bool> candidate)
    {
        // All triangles of the scene in one list.
        var a = new List<Vector3>();
        var b = new List<Vector3>();
        var c = new List<Vector3>();
        var owner = new List<(int Surface, int Triangle)>();
        for (int s = 0; s < surfaces.Count; s++)
        {
            var surface = surfaces[s];
            for (int t = 0; t < surface.Triangles.Length / 3; t++)
            {
                a.Add(surface.Vertices[surface.Triangles[t * 3]].Position);
                b.Add(surface.Vertices[surface.Triangles[t * 3 + 1]].Position);
                c.Add(surface.Vertices[surface.Triangles[t * 3 + 2]].Position);
                owner.Add((s, t));
            }
        }
        var bvh = new Bvh(a, b, c);

        var result = new bool[surfaces.Count][];
        for (int s = 0; s < surfaces.Count; s++) result[s] = new bool[surfaces[s].Triangles.Length / 3];
        var work = Enumerable.Range(0, owner.Count).Where(i => candidate(owner[i].Surface)).ToArray();
        Parallel.ForEach(work, i =>
        {
            foreach (var weights in Samples)
            {
                var point = a[i] * weights.X + b[i] * weights.Y + c[i] * weights.Z;
                if (!AllBlocked(bvh, point)) return;
            }
            var (surface, triangle) = owner[i];
            result[surface][triangle] = true;
        });
        return result;
    }

    private static bool AllBlocked(Bvh bvh, Vector3 point)
    {
        foreach (var direction in Directions)
            if (!bvh.Hits(point + direction * RayStart, direction)) return false;
        return true;
    }

    // Bounding volume hierarchy over triangles, for "does this ray hit anything" queries.
    private sealed class Bvh
    {
        private readonly Vector3[] _a, _e1, _e2;
        private readonly int[] _order;
        private readonly List<Node> _nodes = [];

        private struct Node
        {
            public Vector3 Min, Max;
            public int Left, Right;     // children, or -1 for a leaf
            public int Start, Count;    // triangles of a leaf, in _order
        }

        public Bvh(List<Vector3> a, List<Vector3> b, List<Vector3> c)
        {
            int n = a.Count;
            _a = [.. a];
            _e1 = new Vector3[n];
            _e2 = new Vector3[n];
            for (int i = 0; i < n; i++) { _e1[i] = b[i] - a[i]; _e2[i] = c[i] - a[i]; }
            _order = Enumerable.Range(0, n).ToArray();
            if (n > 0) Build(0, n);
        }

        private int Build(int start, int end)
        {
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            var centerMin = min;
            var centerMax = max;
            for (int i = start; i < end; i++)
            {
                int t = _order[i];
                var p0 = _a[t];
                var p1 = _a[t] + _e1[t];
                var p2 = _a[t] + _e2[t];
                min = Vector3.Min(min, Vector3.Min(p0, Vector3.Min(p1, p2)));
                max = Vector3.Max(max, Vector3.Max(p0, Vector3.Max(p1, p2)));
                var center = (p0 + p1 + p2) / 3;
                centerMin = Vector3.Min(centerMin, center);
                centerMax = Vector3.Max(centerMax, center);
            }
            int index = _nodes.Count;
            _nodes.Add(new Node { Min = min, Max = max, Left = -1, Right = -1, Start = start, Count = end - start });
            if (end - start <= 4) return index;

            var size = centerMax - centerMin;
            int axis = size.X >= size.Y && size.X >= size.Z ? 0 : size.Y >= size.Z ? 1 : 2;
            float Key(int t) => Axis(_a[t] + (_e1[t] + _e2[t]) / 3, axis);
            Array.Sort(_order, start, end - start, Comparer<int>.Create((x, y) => Key(x).CompareTo(Key(y))));
            int mid = (start + end) / 2;
            int left = Build(start, mid);
            int right = Build(mid, end);
            var node = _nodes[index];
            node.Left = left;
            node.Right = right;
            node.Count = 0;
            _nodes[index] = node;
            return index;
        }

        private static float Axis(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

        public bool Hits(Vector3 origin, Vector3 direction)
        {
            if (_nodes.Count == 0) return false;
            var inverse = new Vector3(1 / (direction.X == 0 ? 1e-12f : direction.X), 1 / (direction.Y == 0 ? 1e-12f : direction.Y), 1 / (direction.Z == 0 ? 1e-12f : direction.Z));
            Span<int> stack = stackalloc int[64];
            int top = 0;
            stack[top++] = 0;
            while (top > 0)
            {
                var node = _nodes[stack[--top]];
                if (!HitsBox(origin, inverse, node.Min, node.Max)) continue;
                if (node.Left < 0)
                {
                    for (int i = node.Start; i < node.Start + node.Count; i++)
                        if (HitsTriangle(origin, direction, _order[i])) return true;
                    continue;
                }
                stack[top++] = node.Left;
                stack[top++] = node.Right;
            }
            return false;
        }

        private static bool HitsBox(Vector3 origin, Vector3 inverse, Vector3 min, Vector3 max)
        {
            var t1 = (min - origin) * inverse;
            var t2 = (max - origin) * inverse;
            var near = Vector3.Min(t1, t2);
            var far = Vector3.Max(t1, t2);
            float tNear = MathF.Max(MathF.Max(near.X, near.Y), near.Z);
            float tFar = MathF.Min(MathF.Min(far.X, far.Y), far.Z);
            return tFar >= MathF.Max(tNear, 0);
        }

        // Moeller-Trumbore, both sides.
        private bool HitsTriangle(Vector3 origin, Vector3 direction, int t)
        {
            var p = Vector3.Cross(direction, _e2[t]);
            float det = Vector3.Dot(_e1[t], p);
            if (MathF.Abs(det) < 1e-9f) return false;
            float inverseDet = 1 / det;
            var s = origin - _a[t];
            float u = Vector3.Dot(s, p) * inverseDet;
            if (u < 0 || u > 1) return false;
            var q = Vector3.Cross(s, _e1[t]);
            float v = Vector3.Dot(direction, q) * inverseDet;
            if (v < 0 || u + v > 1) return false;
            return Vector3.Dot(_e2[t], q) * inverseDet > 1e-4f;
        }
    }
}
