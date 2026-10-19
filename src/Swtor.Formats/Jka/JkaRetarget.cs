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
/// <item>Below the hips, the weight of the other bones (pelvis, skirt and cloth bones of robes) follows a mix of the two legs:
/// its own leg at the side, half of each leg in the middle. Without this, a robe skirt keeps the narrow SWTOR legs and the wide Jedi Academy legs go through it.</item>
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
    private readonly (int Thigh, int Lower)[] _legs = new (int, int)[2];
    private readonly float _hipHeight, _hipWidth, _kneeHeight;
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

        // The curve ends at the neck joint. Above it the offset is constant, so the head moves as one rigid piece:
        // with Neck1 and Head in the list, the chin and the nose (which lie at different heights) were shifted by different amounts.
        string[] spineBones = ["Root", "LowerBack", "Chest", "Chest1", "Chest2", "Neck"];
        _spine = spineBones.Select(b => { var p = Convert(SwtorWorld(b)); return (Height: p.Z, Depth: p.Y); }).OrderBy(x => x.Height).ToArray();

        foreach (char side in "lr")
        {
            string s = side == 'l' ? "Left" : "Right";
            string j = side.ToString();
            AddSegment([$"{s}Shoulder", $"{s}ShoulderTwist1", $"{s}BlendShoulder"], Convert(SwtorWorld($"{s}Shoulder")), Convert(SwtorWorld($"{s}Elbow")), JkaOrigin($"{j}humerus"), JkaOrigin($"{j}radius"), stretch: true);
            AddSegment([$"{s}Elbow", $"{s}Ulna"], Convert(SwtorWorld($"{s}Elbow")), Convert(SwtorWorld($"{s}Wrist")), JkaOrigin($"{j}radius"), JkaOrigin($"{j}hand"), stretch: true);
            AddSegment([$"{s}Wrist", $"{s}Weapon", .. Fingers(s)], Convert(SwtorWorld($"{s}Wrist")), Convert(SwtorWorld($"{s}MiddleFinger2")), JkaOrigin($"{j}hand"), JkaOrigin($"{j}_d2_j2"), stretch: false);
            int thigh = _segments.Count;
            AddSegment([$"{s}Hip", $"{s}HipTwist1"], Convert(SwtorWorld($"{s}Hip")), Convert(SwtorWorld($"{s}Knee")), JkaOrigin($"{j}femurYZ"), JkaOrigin($"{j}tibia"), stretch: true);
            AddSegment([$"{s}Knee"], Convert(SwtorWorld($"{s}Knee")), Convert(SwtorWorld($"{s}Ankle")), JkaOrigin($"{j}tibia"), JkaOrigin($"{j}talus"), stretch: true);

            // The foot stretches straight down so that the sole reaches the floor of the Jedi Academy models.
            var ankle = Convert(SwtorWorld($"{s}Ankle"));
            var sole = new Vector3(ankle.X, ankle.Y, Scale * SwtorSole);
            var talus = JkaOrigin($"{j}talus");
            AddSegment([$"{s}Ankle", $"{s}Toe"], ankle, sole, talus, new Vector3(talus.X, talus.Y, SoleHeight), stretch: true);
            _segments[^1].Turn = Quaternion.Concatenate(_segments[^1].Turn, FootYaw(ankle, Convert(SwtorWorld($"{s}Toe")), $"{j}talus"));
            // Skirts only (no bone): one segment from the knee to the sole. With the shin and the foot, a skirt would end at the
            // ankle, which is much higher in Jedi Academy (7.5) than in SWTOR (3.3), so robes would look short.
            AddSegment([], Convert(SwtorWorld($"{s}Knee")), sole, JkaOrigin($"{j}tibia"), new Vector3(talus.X, talus.Y, SoleHeight), stretch: true);
            _legs[side == 'l' ? 0 : 1] = (thigh, _segments.Count - 1);
        }

        var leftThigh = _segments[_legs[0].Thigh];
        _hipHeight = leftThigh.From.Z;
        _hipWidth = Math.Max(Math.Abs(leftThigh.From.X), 1e-3f);
        _kneeHeight = leftThigh.To.Z;
    }

    /// <summary>
    /// Rotation around the vertical axis that turns the SWTOR foot (ankle to toe) to the direction of the Jedi Academy foot bone.
    /// In the Jedi Academy bind pose the toes point about 11 degrees outward. The animations turn the foot from this direction,
    /// so a foot that points straight ahead looks turned inward in the game.
    /// </summary>
    private Quaternion FootYaw(Vector3 ankle, Vector3 toe, string talusBone)
    {
        // The x axis of a Jedi Academy bone (first column of the base pose) points along the bone: for the talus, to the toes.
        var pose = _jka.Bones[_jka.IndexOf(talusBone)].BasePose;
        var target = new Vector2(pose[0], pose[4]);
        var source = new Vector2(toe.X - ankle.X, toe.Y - ankle.Y);
        if (target.LengthSquared() < 1e-8f || source.LengthSquared() < 1e-8f) return Quaternion.Identity;
        float angle = MathF.Atan2(target.Y, target.X) - MathF.Atan2(source.Y, source.X);
        angle = MathF.IEEERemainder(angle, 2 * MathF.PI);
        return Quaternion.CreateFromAxisAngle(Vector3.UnitZ, Math.Clamp(angle, -0.5f, 0.5f));
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
            var centralTurn = Quaternion.Identity;
            if (q.Z < _hipHeight) central = LowerBody(q, central, out centralTurn);
            var centralNormal = Vector3.Transform(n, centralTurn);

            if (mesh.BoneWeights is null || mesh.BoneIndices is null)
            {
                positions[v] = central;
                normals[v] = centralNormal;
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
                    normal += w * centralNormal;
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
                normals[v] = centralNormal;
                continue;
            }
            positions[v] = position / total;
            normals[v] = normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : n;
        }
    }

    /// <summary>
    /// Moves a point below the hips that no leg bone holds (pelvis, robe skirt). Inputs: the converted point and its central position.
    /// Output: the new position, and the rotation for its normal.
    /// The point follows a mix of the two legs (<see cref="LeftShare"/>): its own leg at the side, half of each leg in the middle.
    /// The legs are mirror images, so in the middle their side offsets cancel and the two halves of a skirt stay joined at x = 0.
    /// The effect starts at the hip joint and is complete a quarter of the thigh lower, so the waist does not move.
    /// </summary>
    private Vector3 LowerBody(Vector3 q, Vector3 central, out Quaternion turn)
    {
        float down = Down(q);
        float left = LeftShare(q);
        var follow = Vector3.Lerp(FollowLeg(q, _legs[1], out var rightTurn), FollowLeg(q, _legs[0], out var leftTurn), left);

        turn = Quaternion.Slerp(Quaternion.Identity, Quaternion.Slerp(rightTurn, leftTurn, left), down);
        return Vector3.Lerp(central, follow, down);
    }

    // Position of a point moved by a leg: the thigh above the knee, the knee-to-sole segment below it.
    // Near the knee the two segments are mixed, so a skirt has no step there.
    private Vector3 FollowLeg(Vector3 q, (int Thigh, int Lower) leg, out Quaternion turn)
    {
        var a = _segments[leg.Thigh];
        var b = _segments[leg.Lower];
        float t = BelowKnee(q);
        turn = Quaternion.Slerp(a.Turn, b.Turn, t);
        return Vector3.Lerp(a.Apply(q), b.Apply(q), t);
    }

    // 0 at the hip joint, 1 from a quarter of the thigh lower.
    private float Down(Vector3 q) => Math.Clamp((_hipHeight - q.Z) / Math.Max(0.25f * (_hipHeight - _kneeHeight), 1e-3f), 0f, 1f);

    // Share of the left leg: 1 from the left hip joint outward, 0.5 in the middle, 0 from the right hip joint outward (left is +x).
    // A panel between the legs (the loincloth of a robe) is pushed by the leg that comes forward. With only the pelvis,
    // the thigh goes through it.
    private float LeftShare(Vector3 q) => Math.Clamp(0.5f + 0.5f * q.X / _hipWidth, 0f, 1f);

    // 0 above the knee, 1 below it, mixed in a band around the knee.
    private float BelowKnee(Vector3 q)
    {
        float band = 0.15f * (_kneeHeight - _segments[_legs[0].Lower].To.Z);
        return Math.Clamp((_kneeHeight + band - q.Z) / (2 * band), 0f, 1f);
    }

    /// <summary>
    /// How much a point below the hips that no leg bone holds (pelvis, robe skirt) follows the legs in the animations.
    /// Input: the vertex position in SWTOR space. Output: the share of the legs (0 = pelvis only, 1 = legs only, same factor as the
    /// bind pose move), the share of the left leg in it, and the share of the shin in each leg (0 = thigh, 1 = shin). Null above the hips.
    /// Without this, a robe skirt moves with the pelvis only and the legs go through it when the character walks or lunges.
    /// </summary>
    public (float Share, float Left, float Shin)? LegShare(Vector3 swtorPosition)
    {
        var q = Convert(swtorPosition);
        if (q.Z >= _hipHeight) return null;
        return (Down(q), LeftShare(q), BelowKnee(q));
    }
}
