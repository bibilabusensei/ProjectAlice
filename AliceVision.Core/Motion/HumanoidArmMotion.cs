using System.Numerics;

namespace AliceVision.Core.Motion;

/// <summary>Bind geometry in the character's body frame (+Y up, +Z forward).</summary>
public readonly record struct ArmMotionRig(Vector3 Shoulder, Vector3 Elbow, Vector3 Wrist,
    float Outward, float HeadHeight, Vector3 PalmLongitudinal, Vector3 PalmNormal, Quaternion HandBindInBody);

public readonly record struct ArmMotionPose(Quaternion UpperSwing, Quaternion LowerSwing,
    Quaternion HandInBody, float FingerCurl);

/// <summary>
/// Exact production arm choreography shared with the offline preview. No
/// bone-local axis guesses, hand-created coordinates from an AI, or translations.
/// </summary>
public static class HumanoidArmMotion
{
    public static ArmMotionPose Sample(ArmMotionRig rig, string action, double time)
    {
        if (!double.IsFinite(time)) throw new ArgumentOutOfRangeException(nameof(time));
        if (rig.Outward is not (-1 or 1) || !float.IsFinite(rig.HeadHeight))
            throw new ArgumentException("Invalid arm calibration.", nameof(rig));
        float upper = Vector3.Distance(rig.Shoulder, rig.Elbow);
        float lower = Vector3.Distance(rig.Elbow, rig.Wrist);
        float total = upper + lower;
        Vector3 hint = rig.Shoulder + new Vector3(rig.Outward * upper * 0.78f, -upper * 0.62f, upper * 0.40f);
        Vector3 restTarget = rig.Shoulder + Vector3.Normalize(new Vector3(rig.Outward * 0.13f, -0.97f, 0.16f)) * total * 0.93f;
        if (action == "walk")
            restTarget.Z += (rig.Outward > 0 ? -1 : 1) * total * 0.10f * (float)Math.Sin(time * Math.Tau / 1.25);
        // "dance" is deliberately a small sway, not an unvalidated dance routine.
        if (action == "dance")
        {
            float envelope = HumanoidGestureMotion.ActivityEnvelope(time, 4.5);
            restTarget.X += rig.Outward * total * 0.06f * envelope;
            restTarget.Z += rig.Outward * total * 0.025f * (float)Math.Sin(time * 2.4 - 0.3) * envelope;
        }
        var rest = TwoBoneArmPose.Solve(rig.Shoulder, rig.Elbow, rig.Wrist, restTarget, hint);
        Quaternion relaxedHand = Quaternion.Normalize(rest.LowerSwing * rig.HandBindInBody);
        var result = new ArmMotionPose(rest.UpperSwing, rest.LowerSwing, relaxedHand, 0.20f);
        if (action == "welcome")
        {
            // Waist/chest-level open palms, NOT two identical hands-up waves.
            double stagger = rig.Outward > 0 ? 0.12 : 0;
            float open = HumanoidGestureMotion.GesturePulse(time - stagger, 0.18, 1.18, 2.25, 3.75);
            Vector3 target = rig.Shoulder + new Vector3(rig.Outward * upper * 1.02f, -upper * 0.60f, upper * 0.62f);
            var offered = TwoBoneArmPose.Solve(rig.Shoulder, rig.Elbow, rig.Wrist, target, hint);
            var swings = TwoBoneArmPose.BlendSwings(rest, offered, open);
            Quaternion palm = TwoBoneArmPose.AlignBasis(rig.PalmLongitudinal, rig.PalmNormal,
                Vector3.Normalize(new Vector3(rig.Outward * 0.72f, 0.52f, 0.15f)),
                Vector3.Normalize(new Vector3(-rig.Outward * 0.20f, 0.45f, 0.87f))) * rig.HandBindInBody;
            return new(swings.Upper, swings.Lower, Quaternion.Normalize(Quaternion.Slerp(relaxedHand, palm, open)),
                0.20f - 0.08f * open);
        }
        bool waving = rig.Outward > 0 ? action == "wave_left" : action is "wave" or "wave_right" or "greet";
        // Side names are selected by the caller. The lateral sign only controls
        // chirality, so mirrored models can override the side using SampleWaveArm.
        return waving ? SampleWaveArm(rig, time, rest, relaxedHand, hint, upper, lower) : result;
    }

    public static ArmMotionPose SampleWaveArm(ArmMotionRig rig, double time)
    {
        // Explicit entry point for left/right semantic names on mirrored rigs.
        ArmMotionPose rest = Sample(rig, "idle", time);
        float upper = Vector3.Distance(rig.Shoulder, rig.Elbow), lower = Vector3.Distance(rig.Elbow, rig.Wrist);
        Vector3 hint = rig.Shoulder + new Vector3(rig.Outward * upper * 0.78f, -upper * 0.62f, upper * 0.40f);
        Vector3 elbow = rig.Shoulder + Vector3.Transform(rig.Elbow - rig.Shoulder, rest.UpperSwing);
        Vector3 wrist = elbow + Vector3.Transform(rig.Wrist - rig.Elbow, rest.LowerSwing);
        return SampleWaveArm(rig, time, new(elbow, wrist, rest.UpperSwing, rest.LowerSwing), rest.HandInBody, hint, upper, lower);
    }

    private static ArmMotionPose SampleWaveArm(ArmMotionRig rig, double time, SolvedArm rest,
        Quaternion relaxedHand, Vector3 hint, float upper, float lower)
    {
        WaveMotionSample wave = HumanoidGestureMotion.SampleWave(time);
        // Outside guide pose keeps the lift away from the face/chest. A
        // spherical Bezier of solved rotations avoids the old IK pole flip.
        Vector3 preparation = rig.Shoulder + new Vector3(rig.Outward * upper * 1.12f, -upper * 0.52f, upper * 0.35f);
        float height = Math.Clamp(rig.HeadHeight - lower * 0.12f, lower * 0.22f, lower * 0.50f);
        Vector3 raised = rig.Shoulder + new Vector3(rig.Outward * upper * 0.90f, height, upper * 0.34f);
        raised.X += rig.Outward * lower * wave.ForearmSwing;
        SolvedArm guide = TwoBoneArmPose.Solve(rig.Shoulder, rig.Elbow, rig.Wrist, preparation, hint);
        SolvedArm lifted = TwoBoneArmPose.Solve(rig.Shoulder, rig.Elbow, rig.Wrist, raised, hint);
        var a = TwoBoneArmPose.BlendSwings(rest, guide, wave.Raise);
        var b = TwoBoneArmPose.BlendSwings(guide, lifted, wave.Raise);
        Quaternion upperSwing = Quaternion.Normalize(Quaternion.Slerp(a.Upper, b.Upper, wave.Raise));
        Quaternion lowerSwing = Quaternion.Normalize(Quaternion.Slerp(a.Lower, b.Lower, wave.Raise));
        Quaternion palm = TwoBoneArmPose.AlignBasis(rig.PalmLongitudinal, rig.PalmNormal,
            Vector3.UnitY, Vector3.UnitZ) * rig.HandBindInBody;
        // +Z rotation sends +Y fingers towards -X. Mirror it so BOTH hands
        // start by sweeping away from the torso rather than towards the face.
        palm = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -rig.Outward * wave.WristSwing) * palm;
        return new(upperSwing, lowerSwing, Quaternion.Normalize(Quaternion.Slerp(relaxedHand, palm, wave.Raise)),
            0.20f - wave.Raise * 0.12f);
    }
}
