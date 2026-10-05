namespace Swtor.Formats.Jka;

/// <summary>Part of the body that a surface of the Jedi Academy player model covers.</summary>
public enum JkaRegion { Hips, Torso, Head, LeftArm, RightArm, LeftHand, RightHand, LeftLeg, RightLeg }

/// <summary>Links the bones of a SWTOR mesh to the bones of the Jedi Academy humanoid skeleton.</summary>
public static class JkaBoneMap
{
    private static readonly (string Bone, float Fraction)[] None = [];

    /// <summary>
    /// The Jedi Academy bones that take the weight of a SWTOR bone, with the share of each (the shares add up to 1).
    /// Returns an empty list for bones that have no counterpart (hair, cloth, effects). The caller then finds another bone.
    /// </summary>
    public static IReadOnlyList<(string Bone, float Fraction)> Map(string swtorBone)
    {
        string n = swtorBone.ToLowerInvariant();
        switch (n)
        {
            case "bip01" or "root" or "rootspine" or "pelvis" or "master" or "grannyrootbone" or "skirt1" or "skirt2": return One("pelvis");
            case "lowerback": return One("lower_lumbar");
            case "chest": return One("upper_lumbar");
            case "chest1": return [("upper_lumbar", 0.5f), ("thoracic", 0.5f)];
            case "chest2": return One("thoracic");
            case "neck": return One("cervical");
            case "neck1": return [("cervical", 0.5f), ("cranium", 0.5f)];
            case "head": return One("cranium");
        }

        if (n.StartsWith("fc_", StringComparison.Ordinal)) return MapFace(n);

        char side;
        string part;
        if (n.StartsWith("left", StringComparison.Ordinal)) { side = 'l'; part = n[4..]; }
        else if (n.StartsWith("right", StringComparison.Ordinal)) { side = 'r'; part = n[5..]; }
        else return None;

        switch (part)
        {
            case "collar": return One($"{side}clavical");
            case "shoulder" or "blendshoulder": return One($"{side}humerus");
            case "shouldertwist1": return One($"{side}humerusX");
            case "elbow": return One($"{side}radius");
            case "ulna": return One($"{side}radiusX");
            case "wrist" or "weapon": return One($"{side}hand");
            case "hip": return One($"{side}femurYZ");
            case "hiptwist1": return One($"{side}femurX");
            case "knee": return One($"{side}tibia");
            case "ankle" or "toe": return One($"{side}talus");
        }

        // Fingers: JKA has a thumb (d1), index (d2) and little finger (d4) with two joints each.
        // The first SWTOR joint ("Finger", "Finger1") maps to joint 1, the last ("Finger2") to joint 2.
        string? finger = part switch
        {
            _ when part.StartsWith("thumbfinger", StringComparison.Ordinal) => "d1",
            _ when part.StartsWith("indexfinger", StringComparison.Ordinal) || part.StartsWith("middlefinger", StringComparison.Ordinal) => "d2",
            _ when part.StartsWith("ringfinger", StringComparison.Ordinal) || part.StartsWith("pinkfinger", StringComparison.Ordinal) => "d4",
            _ => null,
        };
        if (finger is null) return None;
        return One($"{side}_{finger}_j{(part.EndsWith('2') ? 2 : 1)}");
    }

    // Facial bones. JKA has only a few: face, jaw, eyes, brows and four lip bones.
    private static IReadOnlyList<(string Bone, float Fraction)> MapFace(string n) => n switch
    {
        "fc_jaw" or "fc_tongue" => One("jaw"),
        "fc_eye_left" => One("leye"),
        "fc_eye_right" => One("reye"),
        "fc_lip_left_bottom" => One("lblip2"),
        "fc_lip_right_bottom" => One("rblip2"),
        "fc_lip_left_top" => One("ltlip2"),
        "fc_lip_right_top" => One("rtlip2"),
        "fc_lip_center_bottom" => [("lblip2", 0.5f), ("rblip2", 0.5f)],
        "fc_lip_center_top" => [("ltlip2", 0.5f), ("rtlip2", 0.5f)],
        "fc_lip_left_corner" => [("lblip2", 0.5f), ("ltlip2", 0.5f)],
        "fc_lip_right_corner" => [("rblip2", 0.5f), ("rtlip2", 0.5f)],
        _ when n.StartsWith("fc_brow", StringComparison.Ordinal) => One("ceyebrow"),
        _ => One("face"),
    };

    private static (string Bone, float Fraction)[] One(string bone) => [(bone, 1f)];

    /// <summary>The region of the body that a bone of the Jedi Academy skeleton belongs to. Arm bones include the hand.</summary>
    public static JkaRegion RegionOf(string jkaBone, bool handSeparate)
    {
        string n = jkaBone.ToLowerInvariant();
        switch (n)
        {
            case "model_root" or "pelvis" or "motion" or "lower_lumbar" or "ltail" or "rtail" or "lfemuryz" or "rfemuryz": return JkaRegion.Hips;
            case "upper_lumbar" or "thoracic" or "lclavical" or "rclavical": return JkaRegion.Torso;
            case "cervical" or "cranium" or "face" or "jaw" or "leye" or "reye" or "ceyebrow" or "lblip2" or "rblip2" or "ltlip2" or "rtlip2": return JkaRegion.Head;
            case "lhumerus" or "lhumerusx" or "lradius" or "lradiusx": return JkaRegion.LeftArm;
            case "rhumerus" or "rhumerusx" or "rradius" or "rradiusx": return JkaRegion.RightArm;
            case "lfemurx" or "ltibia" or "ltalus": return JkaRegion.LeftLeg;
            case "rfemurx" or "rtibia" or "rtalus": return JkaRegion.RightLeg;
        }
        if (n == "lhand" || n.StartsWith("l_d", StringComparison.Ordinal) || n == "lhang_tag_bone") return handSeparate ? JkaRegion.LeftHand : JkaRegion.LeftArm;
        if (n == "rhand" || n.StartsWith("r_d", StringComparison.Ordinal) || n == "rhang_tag_bone") return handSeparate ? JkaRegion.RightHand : JkaRegion.RightArm;
        return JkaRegion.Hips;
    }

    /// <summary>Name of the main surface of a region in a Jedi Academy player model.</summary>
    public static string BaseSurface(JkaRegion region) => region switch
    {
        JkaRegion.Hips => "hips",
        JkaRegion.Torso => "torso",
        JkaRegion.Head => "head",
        JkaRegion.LeftArm => "l_arm",
        JkaRegion.RightArm => "r_arm",
        JkaRegion.LeftHand => "l_hand",
        JkaRegion.RightHand => "r_hand",
        JkaRegion.LeftLeg => "l_leg",
        _ => "r_leg",
    };

    /// <summary>The surface that holds the surface of this region in the hierarchy (the parent of the main surface).</summary>
    public static string? ParentSurface(JkaRegion region) => region switch
    {
        JkaRegion.Hips => null,
        JkaRegion.Torso or JkaRegion.LeftLeg or JkaRegion.RightLeg => "hips",
        JkaRegion.Head or JkaRegion.LeftArm or JkaRegion.RightArm => "torso",
        JkaRegion.LeftHand => "l_arm",
        _ => "r_arm",
    };
}
