using System.Numerics;
using Swtor.Formats.Gr2;

namespace Swtor.Formats.Jka;

/// <summary>
/// Moves the vertices of a SWTOR mesh into the bind pose of the Jedi Academy humanoid skeleton.
/// Both skeletons are in the files, so no joint is guessed:
/// <list type="bullet">
/// <item>All points are first converted to the game units of Jedi Academy: one uniform scale, and the axes
/// (SWTOR: x left, y up, z front; Jedi Academy: x left, y back, z up).</item>
/// <item>The spine, the neck and the head keep their shape. Only the curve of the SWTOR spine is straightened,
/// because the Jedi Academy spine is a straight line.</item>
/// <item>Each arm and leg is cut in segments (upper arm, forearm, hand, thigh, shin, foot). Each segment is moved,
/// turned and stretched so that its two joints land on the two matching joints of the Jedi Academy skeleton.
/// This puts the arms and legs in the pose of the Jedi Academy bind pose (the arms are lower and the legs are wider than in SWTOR).</item>
/// <item>A vertex follows the weighted mix of the segments that its SWTOR bones belong to.</item>
/// </list>
/// </summary>
public sealed class JkaRetarget
{
    /// <summary>Height of the sole of the foot in the Jedi Academy models. The lowest vertex of the sample models is at about 2.7.</summary>
    public const float SoleHeight = 2.7f;

    // Height of the sole under the SWTOR origin (the boots of the bare body reach y = -0.0003).
    private const float SwtorSole = -0.0003f;

    private sealed class Segment
    {
        public Vector3 From, To;          // joint positions in the converted SWTOR space
        public Vector3 Axis;              // unit vector from -> to
        public float Stretch = 1;         // length ratio (target / source) along the axis
        public Quaternion Turn = Quaternion.Identity;
        public Vector3 Target;            // position of the first joint in the Jedi Academy skeleton

        public Vector3 Apply(Vector3 p)
        {
            var v = p - From;
            v += Axis * ((Stretch - 1f) * Vector3.Dot(v, Axis));
            return Vector3.Transform(v, Turn) + Target;
        }
    }

    private readonly Gr2Skeleton _swtor;
    private readonly GlaSkeleton _jka;
    private readonly Dictionary<string, int> _segmentOf = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Segment> _segments = [];
    private readonly (float Height, float Depth)[] _spine;
    private readonly float _jkaDepth;

    /// <summary>The uniform scale from SWTOR units to Jedi Academy units.</summary>
    public float Scale { get; }

    public JkaRetarget(Gr2Skeleton swtor, GlaSkeleton jka, float? scale = null)
    {
        _swtor = swtor;
        _jka = jka;
        // The neck joint of both skeletons gives the scale. This also makes the head match the head of the Jedi Academy models.
        Scale = scale ?? JkaOrigin("cervical").Z / SwtorWorld("Neck").Y;
        _jkaDepth = jka.Bones[jka.IndexOf("pelvis")].Origin.Y;

        string[] spineBones = ["Root", "LowerBack", "Chest", "Chest1", "Chest2", "Neck", "Neck1", "Head"];
        _spine = spineBones.Select(b => { var p = Convert(SwtorWorld(b)); return (Height: p.Z, Depth: p.Y); }).OrderBy(x => x.Height).ToArray();

        foreach (char side in "lr")
        {
            string s = side == 'l' ? "Left" : "Right";
            string j = side.ToString();
            AddSegment([$"{s}Shoulder", $"{s}ShoulderTwist1", $"{s}BlendShoulder"], Convert(SwtorWorld($"{s}Shoulder")), Convert(SwtorWorld($"{s}Elbow")), JkaOrigin($"{j}humerus"), JkaOrigin($"{j}radius"), stretch: true);
            AddSegment([$"{s}Elbow", $"{s}Ulna"], Convert(SwtorWorld($"{s}Elbow")), Convert(SwtorWorld($"{s}Wrist")), JkaOrigin($"{j}radius"), JkaOrigin($"{j}hand"), stretch: true);
            AddSegment([$"{s}Wrist", $"{s}Weapon", .. Fingers(s)], Convert(SwtorWorld($"{s}Wrist")), Convert(SwtorWorld($"{s}MiddleFinger2")), JkaOrigin($"{j}hand"), JkaOrigin($"{j}_d2_j2"), stretch: false);
            AddSegment([$"{s}Hip", $"{s}HipTwist1"], Convert(SwtorWorld($"{s}Hip")), Convert(SwtorWorld($"{s}Knee")), JkaOrigin($"{j}femurYZ"), JkaOrigin($"{j}tibia"), stretch: true);
            AddSegment([$"{s}Knee"], Convert(SwtorWorld($"{s}Knee")), Convert(SwtorWorld($"{s}Ankle")), JkaOrigin($"{j}tibia"), JkaOrigin($"{j}talus"), stretch: true);

            // The foot stretches straight down so that the sole reaches the floor of the Jedi Academy models.
            var ankle = Convert(SwtorWorld($"{s}Ankle"));
            var sole = new Vector3(ankle.X, ankle.Y, Scale * SwtorSole);
            var talus = JkaOrigin($"{j}talus");
            AddSegment([$"{s}Ankle", $"{s}Toe"], ankle, sole, talus, new Vector3(talus.X, talus.Y, SoleHeight), stretch: true);
        }
    }

    private static IEnumerable<string> Fingers(string side) =>
        new[] { "Thumb", "Index", "Middle", "Ring", "Pink" }.SelectMany(f => new[] { $"{side}{f}Finger", $"{side}{f}Finger1", $"{side}{f}Finger2" });

    private void AddSegment(IEnumerable<string> swtorBones, Vector3 from, Vector3 to, Vector3 targetFrom, Vector3 targetTo, bool stretch)
    {
        var axis = Vector3.Normalize(to - from);
        var targetAxis = Vector3.Normalize(targetTo - targetFrom);
        var segment = new Segment
        {
            From = from, To = to, Axis = axis, Target = targetFrom,
            Stretch = stretch ? Math.Clamp((targetTo - targetFrom).Length() / (to - from).Length(), 0.6f, 1.6f) : 1f,
            Turn = RotationBetween(axis, targetAxis),
        };
        _segments.Add(segment);
        foreach (string bone in swtorBones) _segmentOf[bone] = _segments.Count - 1;
    }

    private static Quaternion RotationBetween(Vector3 a, Vector3 b)
    {
        float dot = Math.Clamp(Vector3.Dot(a, b), -1f, 1f);
        if (dot > 0.99999f) return Quaternion.Identity;
        var axis = Vector3.Cross(a, b);
        if (axis.LengthSquared() < 1e-12f) axis = Vector3.Normalize(Vector3.Cross(a, Math.Abs(a.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY));
        return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.Acos(dot));
    }

    private Vector3 SwtorWorld(string bone)
    {
        int index = _swtor.IndexOf(bone);
        if (index < 0) throw new InvalidDataException($"The SWTOR skeleton has no bone '{bone}'.");
        var m = _swtor.Bones[index].World;
        return new Vector3(m.M41, m.M42, m.M43);
    }

    private Vector3 JkaOrigin(string bone)
    {
        int index = _jka.IndexOf(bone);
        if (index < 0) throw new InvalidDataException($"The Jedi Academy skeleton has no bone '{bone}'.");
        return _jka.Bones[index].Origin;
    }

    /// <summary>Scale and axes only: SWTOR (x left, y up, z front) to Jedi Academy (x left, y back, z up).</summary>
    private Vector3 Convert(Vector3 p) => new(p.X * Scale, -p.Z * Scale, p.Y * Scale);

    private float SpineDepth(float height)
    {
        if (height <= _spine[0].Height) return _spine[0].Depth;
        for (int i = 1; i < _spine.Length; i++)
            if (height <= _spine[i].Height)
            {
                float t = (height - _spine[i - 1].Height) / Math.Max(_spine[i].Height - _spine[i - 1].Height, 1e-6f);
                return _spine[i - 1].Depth + t * (_spine[i].Depth - _spine[i - 1].Depth);
            }
        return _spine[^1].Depth;
    }

    /// <summary>
    /// Converts all vertices of a mesh. <paramref name="positions"/> and <paramref name="normals"/> receive the result
    /// (same length as the mesh). The weights of the mesh decide which segments move each vertex.
    /// </summary>
    public void Transform(Gr2Mesh mesh, Span<Vector3> positions, Span<Vector3> normals)
    {
        var boneSegment = new int[mesh.Bones.Count];
        for (int i = 0; i < boneSegment.Length; i++)
            boneSegment[i] = _segmentOf.TryGetValue(mesh.Bones[i].Name, out int s) ? s : -1;

        for (int v = 0; v < mesh.VertexCount; v++)
        {
            var q = Convert(mesh.Positions[v]);
            var n = mesh.Normals is null ? Vector3.UnitZ : Convert(mesh.Normals[v]) / Scale;
            var central = new Vector3(q.X, q.Y - SpineDepth(q.Z) + _jkaDepth, q.Z);

            if (mesh.BoneWeights is null || mesh.BoneIndices is null)
            {
                positions[v] = central;
                normals[v] = n;
                continue;
            }

            // Mix of the central transform and the segment transforms, by the weights of the SWTOR bones.
            Vector3 position = Vector3.Zero, normal = Vector3.Zero;
            float total = 0;
            var weights = mesh.BoneWeights[v];
            for (int k = 0; k < 4; k++)
            {
                float w = k switch { 0 => weights.X, 1 => weights.Y, 2 => weights.Z, _ => weights.W };
                if (w <= 0) continue;
                int segment = boneSegment[mesh.BoneIndices[v * 4 + k]];
                if (segment < 0)
                {
                    position += w * central;
                    normal += w * n;
                }
                else
                {
                    position += w * _segments[segment].Apply(q);
                    normal += w * Vector3.Transform(n, _segments[segment].Turn);
                }
                total += w;
            }
            if (total <= 0)
            {
                positions[v] = central;
                normals[v] = n;
                continue;
            }
            positions[v] = position / total;
            normals[v] = normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : n;
        }
    }
}
