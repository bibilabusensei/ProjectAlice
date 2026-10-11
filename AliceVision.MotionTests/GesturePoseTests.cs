using System.Numerics;
using AliceVision.Core.Motion;

/// <summary>
/// Choreography regression checks on actual joint FK, rather than desired wrist
/// targets. Fixtures use generic human proportions, not private model geometry.
/// These are geometric checks, not a claim of motion-capture realism.
/// </summary>
internal static class GesturePoseTests
{
    public static IReadOnlyList<(string Name, Action Run)> Cases { get; } = new (string, Action)[]
    {
        ("first fingertip and forearm sweep goes outward on BOTH hands", OutwardFirstSweep),
        ("wave palms face forward on both calibrated hands", PalmsFaceForward),
        ("wave FK stays outside the torso and clear of a generic head capsule", OutsideLiftPath),
        ("wave FK preserves segment lengths and bounded elbow bend", WaveJointLengthsAndBend),
        ("wave FK has no single-frame upper lower or palm flip at 60 Hz", SmoothWavePath),
        ("welcome stays below shoulder level rather than surrendering", WelcomeBelowShoulders),
        ("welcome holds open palms without wrist oscillation", WelcomeStableHold),
        ("welcome to either hand wave blends LOCAL joints without T-pose reset", WelcomeTransition),
        ("head and chest gesture deltas join smoothly and return to idle", SecondaryGestureContinuity),
        ("walking knees flex backward in a +Y-up +Z-forward body frame", KneesFlexBackward),
        ("walking ankles counterrotate rather than tipping toes through the floor", WalkingAnkleOrientation),
        ("walking arm swing opposes the same-side thigh swing", OpposedArmAndLegSwing),
        ("gentle sway leaves both legs and feet planted", SwayHasNoLegPistons),
        ("arm choreography rejects nonfinite time and invalid handedness", InvalidMotionInput),
    };

    private static ArmMotionRig Rig(float outward, bool angledBind = false)
    {
        Vector3 shoulder = new(outward * 0.35f, 1.40f, 0);
        Vector3 upper = angledBind ? new(outward * 0.24f, -0.22f, 0.03f) : new(outward * 0.34f, 0, 0);
        Vector3 lower = angledBind ? new(outward * 0.20f, -0.20f, 0.03f) : new(outward * 0.29f, 0, 0);
        Vector3 elbow = shoulder + upper, wrist = elbow + lower;
        Quaternion handBind = Quaternion.CreateFromYawPitchRoll(outward * 0.27f, -0.10f, outward * 0.13f);
        return new(shoulder, elbow, wrist, outward, 0.20f,
            Vector3.Normalize(new Vector3(outward, 0.01f, 0.04f)), -Vector3.UnitY, handBind);
    }

    private static (Vector3 Elbow, Vector3 Wrist, Vector3 Fingers, Vector3 Normal) Fk(ArmMotionRig rig, ArmMotionPose pose)
    {
        Vector3 elbow = rig.Shoulder + Vector3.Transform(rig.Elbow - rig.Shoulder, pose.UpperSwing);
        Vector3 wrist = elbow + Vector3.Transform(rig.Wrist - rig.Elbow, pose.LowerSwing);
        Quaternion handDelta = Quaternion.Normalize(pose.HandInBody * Quaternion.Inverse(rig.HandBindInBody));
        Vector3 fingers = Vector3.Normalize(Vector3.Transform(rig.PalmLongitudinal, handDelta));
        Vector3 normal = Vector3.Normalize(Vector3.Transform(rig.PalmNormal, handDelta));
        return (elbow, wrist, fingers, normal);
    }

    private static void OutwardFirstSweep()
    {
        foreach (float side in new[] { -1f, 1f })
        foreach (bool angled in new[] { false, true })
        {
            var rig = Rig(side, angled);
            var start = Fk(rig, HumanoidArmMotion.SampleWaveArm(rig, HumanoidGestureMotion.WristWaveStartsAtSeconds));
            foreach (double time in new[] { 1.25, 1.30, 1.35, 1.40, 1.50 })
            {
                var frame = Fk(rig, HumanoidArmMotion.SampleWaveArm(rig, time));
                Require(side * (frame.Fingers.X - start.Fingers.X) > 0,
                    $"Hand {side:+0;-0} starts inward at {time:F2} s.");
                Require(side * (frame.Wrist.X - start.Wrist.X) > 0,
                    $"Forearm {side:+0;-0} starts inward at {time:F2} s.");
            }
        }
    }

    private static void PalmsFaceForward()
    {
        foreach (float side in new[] { -1f, 1f })
        {
            var rig = Rig(side);
            for (int index = 60; index <= 198; index++)
            {
                var frame = Fk(rig, HumanoidArmMotion.SampleWaveArm(rig, index / 60.0));
                Require(Vector3.Dot(frame.Normal, Vector3.UnitZ) > 0.98f, "Raised palm turns away from the viewer.");
                Require(frame.Fingers.Y > 0.97f, "Wave fingers should remain broadly upright, not dangle sideways.");
            }
        }
    }

    private static void OutsideLiftPath()
    {
        foreach (float side in new[] { -1f, 1f })
        foreach (bool angled in new[] { false, true })
        {
            var rig = Rig(side, angled);
            float upper = Vector3.Distance(rig.Shoulder, rig.Elbow);
            Vector3 head = new(0, rig.Shoulder.Y + rig.HeadHeight, 0);
            for (int index = 0; index <= 252; index++)
            {
                double time = index / 60.0;
                var frame = Fk(rig, HumanoidArmMotion.SampleWaveArm(rig, time));
                Require(side * (frame.Wrist.X - rig.Shoulder.X) >= upper * 0.05f,
                    $"Wrist crossed medial of its shoulder at {time:F3} s.");
                Require(side * (frame.Elbow.X - rig.Shoulder.X) >= upper * 0.05f,
                    $"Elbow collapsed inward at {time:F3} s.");
                Require(DistanceToSegment(head, rig.Shoulder, frame.Elbow) > upper * 0.55f,
                    "Upper arm passes through the head clearance sphere.");
                Require(DistanceToSegment(head, frame.Elbow, frame.Wrist) > upper * 0.55f,
                    "Forearm passes through the head clearance sphere.");
                Vector3 fingerTip = frame.Wrist + frame.Fingers * upper * 0.45f;
                Require(DistanceToSegment(head, frame.Wrist, fingerTip) > upper * 0.55f,
                    "Hand passes through the head clearance sphere.");
                if (time is >= 1 and <= 3.3)
                {
                    Require(frame.Elbow.Y < frame.Wrist.Y - upper * 0.40f, "Greeting elbow should remain below its hand.");
                    Require(frame.Wrist.Y < head.Y, "Greeting wrist should not be raised above the head joint.");
                }
            }
        }
    }

    private static void WaveJointLengthsAndBend()
    {
        foreach (float side in new[] { -1f, 1f })
        foreach (bool angled in new[] { false, true })
        {
            var rig = Rig(side, angled);
            float upper = Vector3.Distance(rig.Shoulder, rig.Elbow), lower = Vector3.Distance(rig.Elbow, rig.Wrist);
            for (int index = 0; index <= 252; index++)
            {
                var frame = Fk(rig, HumanoidArmMotion.SampleWaveArm(rig, index / 60.0));
                Near(Vector3.Distance(rig.Shoulder, frame.Elbow), upper, 0.00002f);
                Near(Vector3.Distance(frame.Elbow, frame.Wrist), lower, 0.00002f);
                Vector3 upperDir = Vector3.Normalize(frame.Elbow - rig.Shoulder);
                Vector3 lowerDir = Vector3.Normalize(frame.Wrist - frame.Elbow);
                float bend = MathF.Acos(Math.Clamp(Vector3.Dot(upperDir, lowerDir), -1, 1));
                Require(bend > 0.12f && bend < 2.30f,
                    $"Elbow became locked straight or overfolded ({bend * 180 / MathF.PI:F1} degrees).");
            }
        }
    }

    private static void SmoothWavePath()
    {
        foreach (float side in new[] { -1f, 1f })
        foreach (bool angled in new[] { false, true })
        {
            var rig = Rig(side, angled);
            var prior = HumanoidArmMotion.SampleWaveArm(rig, 0);
            for (int index = 1; index <= 252; index++)
            {
                var current = HumanoidArmMotion.SampleWaveArm(rig, index / 60.0);
                Require(Angle(prior.UpperSwing, current.UpperSwing) < 6, "Upper arm snapped within one 60 Hz frame.");
                Require(Angle(prior.LowerSwing, current.LowerSwing) < 8, "Forearm snapped within one 60 Hz frame.");
                Require(Angle(prior.HandInBody, current.HandInBody) < 8, "Wrist flipped within one 60 Hz frame.");
                prior = current;
            }
        }
    }

    private static void WelcomeBelowShoulders()
    {
        foreach (float side in new[] { -1f, 1f })
        foreach (bool angled in new[] { false, true })
        {
            var rig = Rig(side, angled);
            float upper = Vector3.Distance(rig.Shoulder, rig.Elbow);
            for (int index = 0; index <= 252; index++)
            {
                var frame = Fk(rig, HumanoidArmMotion.Sample(rig, "welcome", index / 60.0));
                Require(frame.Wrist.Y < rig.Shoulder.Y - upper * 0.30f,
                    "Welcome reverted to a head-level hands-up pose.");
                Require(side * (frame.Wrist.X - rig.Shoulder.X) > upper * 0.05f,
                    "Welcome hands crossed the torso instead of opening outside.");
            }
        }
    }

    private static void WelcomeStableHold()
    {
        foreach (float side in new[] { -1f, 1f })
        {
            var rig = Rig(side);
            var start = HumanoidArmMotion.Sample(rig, "welcome", 1.35);
            for (double time = 1.35; time <= 2.20; time += 0.01)
            {
                var current = HumanoidArmMotion.Sample(rig, "welcome", time);
                Require(Angle(start.UpperSwing, current.UpperSwing) < 0.05f, "Welcome upper arm oscillates while offering palms.");
                Require(Angle(start.LowerSwing, current.LowerSwing) < 0.05f, "Welcome forearm oscillates like a two-handed wave.");
                Require(Angle(start.HandInBody, current.HandInBody) < 0.05f, "Welcome wrist must hold rather than wave.");
            }
        }
    }

    private static void WelcomeTransition()
    {
        foreach (string next in new[] { "wave_left", "wave_right" })
        foreach (float side in new[] { -1f, 1f })
        {
            var rig = Rig(side);
            var start = ToLocals(HumanoidArmMotion.Sample(rig, "welcome", 1.60));
            LocalArm prior = start;
            for (int index = 0; index <= 60; index++)
            {
                double time = index / 60.0;
                var target = ToLocals(HumanoidArmMotion.Sample(rig, next, time));
                float blend = HumanoidGestureMotion.SmoothStep01(time / HumanoidGestureMotion.TransitionDuration(next));
                var locals = new LocalArm(Quaternion.Slerp(start.Upper, target.Upper, blend),
                    Quaternion.Slerp(start.Lower, target.Lower, blend), Quaternion.Slerp(start.Hand, target.Hand, blend));
                Quaternion upperWorld = locals.Upper;
                Quaternion lowerWorld = Quaternion.Normalize(upperWorld * locals.Lower);
                Quaternion handWorld = Quaternion.Normalize(lowerWorld * locals.Hand);
                var frame = Fk(rig, new(upperWorld, lowerWorld, handWorld, 0.10f));
                Require(side * (frame.Wrist.X - rig.Shoulder.X) > -0.01f,
                    "Action switch swings the hand across the torso.");
                Require(frame.Elbow.Y < rig.Shoulder.Y + 0.01f, "Action switch passes through a raised T-pose.");
                float upperStep = Angle(prior.Upper, locals.Upper), lowerStep = Angle(prior.Lower, locals.Lower);
                Require(upperStep < 8 && lowerStep < 8,
                    $"{next}/{side:+0;-0} action switch jumps {upperStep:F2}/{lowerStep:F2} degrees at {time:F3} s.");
                prior = locals;
            }
        }
    }

    private static LocalArm ToLocals(ArmMotionPose pose) => new(pose.UpperSwing,
        Quaternion.Normalize(Quaternion.Inverse(pose.UpperSwing) * pose.LowerSwing),
        Quaternion.Normalize(Quaternion.Inverse(pose.LowerSwing) * pose.HandInBody));

    private readonly record struct LocalArm(Quaternion Upper, Quaternion Lower, Quaternion Hand);

    private static void SecondaryGestureContinuity()
    {
        foreach (string action in new[] { "wave_left", "wave_right", "greet", "welcome", "nod", "bow", "look", "dance" })
        foreach (string bone in new[] { "head", "neck", "spine", "chest" })
        {
            double end = HumanoidGestureMotion.Duration(action);
            Vector3 prior = HumanoidGestureMotion.SampleBoneAngles(bone, action, 0, 10);
            for (int index = 1; index <= (int)Math.Ceiling(end * 60); index++)
            {
                double time = index / 60.0;
                Vector3 current = HumanoidGestureMotion.SampleBoneAngles(bone, action, time, 10 + time);
                float step = Vector3.Distance(prior, current);
                Require(step < 0.0175f, $"{action}/{bone} rotates {step * 180 / MathF.PI:F2} degrees in one frame (over 60 degrees/sec).");
                Require(current.Length() < 0.30f, $"{action}/{bone} secondary rotation is excessive.");
                prior = current;
            }
            Vector3 final = HumanoidGestureMotion.SampleBoneAngles(bone, action, end, 10 + end);
            Vector3 idle = HumanoidGestureMotion.SampleBoneAngles(bone, "idle", end, 10 + end);
            Require(Vector3.Distance(final, idle) < 0.000001f, $"{action}/{bone} does not return to idle.");
            foreach (double boundary in new[] { 0.18, 0.20, 0.25, 0.80, 0.85, 1.00, 1.10, 1.18, 1.30, 1.40, 1.55,
                1.65, 1.85, 1.90, 2.10, 2.20, 2.25, 2.40, 2.50, 2.55, 3.20, 3.30, 3.75, end })
            {
                const double h = 0.0001;
                Vector3 before = HumanoidGestureMotion.SampleBoneAngles(bone, action, boundary - h, 10 + boundary - h);
                Vector3 at = HumanoidGestureMotion.SampleBoneAngles(bone, action, boundary, 10 + boundary);
                Vector3 after = HumanoidGestureMotion.SampleBoneAngles(bone, action, boundary + h, 10 + boundary + h);
                Require(Vector3.Distance(before, after) < 0.0002f, $"{action}/{bone} position jumps at {boundary:F2} s.");
                Require((before - 2 * at + after).Length() / h < 0.001f,
                    $"{action}/{bone} velocity jumps at {boundary:F2} s.");
            }
        }
    }

    private static void KneesFlexBackward()
    {
        foreach (string bone in new[] { "leftLowerLeg", "rightLowerLeg" })
        {
            bool visiblyFlexes = false;
            for (int index = 0; index < 150; index++)
            {
                Vector3 angles = HumanoidGestureMotion.SampleBoneAngles(bone, "walk", index / 60.0, 10 + index / 60.0);
                Require(angles.X >= 0, $"{bone} bends forward through the kneecap.");
                Vector3 shin = Vector3.Transform(-Vector3.UnitY,
                    Quaternion.CreateFromAxisAngle(Vector3.UnitX, angles.X));
                Require(shin.Z <= 0.000001f, "Knee flexion must send a downwards shin backwards (-Z), not forward (+Z).");
                visiblyFlexes |= shin.Z < -0.1f;
            }
            Require(visiblyFlexes, "Walking knee does not flex at all.");
        }
    }

    private static void SwayHasNoLegPistons()
    {
        foreach (string bone in new[] { "leftUpperLeg", "rightUpperLeg", "leftLowerLeg", "rightLowerLeg", "leftFoot", "rightFoot" })
            for (double time = 0; time <= 4.5; time += 0.025)
                Require(HumanoidGestureMotion.SampleBoneAngles(bone, "dance", time, 10 + time) == Vector3.Zero,
                    "A planted gentle sway must not pump the legs independently.");
    }

    private static void WalkingAnkleOrientation()
    {
        foreach (string side in new[] { "left", "right" })
            for (int index = 0; index < 150; index++)
            {
                double time = index / 60.0;
                float thigh = HumanoidGestureMotion.SampleBoneAngles(side + "UpperLeg", "walk", time, 10 + time).X;
                float knee = HumanoidGestureMotion.SampleBoneAngles(side + "LowerLeg", "walk", time, 10 + time).X;
                float ankle = HumanoidGestureMotion.SampleBoneAngles(side + "Foot", "walk", time, 10 + time).X;
                Near(thigh + knee + ankle, 0, 0.000001f);
                Vector3 forward = Vector3.Transform(Vector3.UnitZ,
                    Quaternion.CreateFromAxisAngle(Vector3.UnitX, thigh + knee + ankle));
                Require(Vector3.Distance(forward, Vector3.UnitZ) < 0.000001f,
                    "Walking foot should preserve its bind pitch rather than tipping with its knee.");
            }
    }

    private static void OpposedArmAndLegSwing()
    {
        foreach (float outward in new[] { -1f, 1f })
        {
            string side = outward > 0 ? "left" : "right";
            var rig = Rig(outward);
            float restWristForward = Fk(rig, HumanoidArmMotion.Sample(rig, "idle", 0)).Wrist.Z;
            foreach (double time in new[] { 0.15, 0.30, 0.45, 0.78, 0.93, 1.08 })
            {
                float thighAngle = HumanoidGestureMotion.SampleBoneAngles(side + "UpperLeg", "walk", time, 10 + time).X;
                float thighForward = Vector3.Transform(-Vector3.UnitY,
                    Quaternion.CreateFromAxisAngle(Vector3.UnitX, thighAngle)).Z;
                float wristForward = Fk(rig, HumanoidArmMotion.Sample(rig, "walk", time)).Wrist.Z - restWristForward;
                Require(thighForward * wristForward < 0,
                    "Arm and same-side leg swing together like a puppet rather than counterbalancing.");
            }
        }
    }

    private static void InvalidMotionInput()
    {
        var rig = Rig(1);
        foreach (double time in new[] { double.NaN, double.NegativeInfinity, double.PositiveInfinity })
        {
            Reject(() => HumanoidArmMotion.Sample(rig, "wave_left", time));
            Reject(() => HumanoidArmMotion.SampleWaveArm(rig, time));
        }
        Reject(() => HumanoidArmMotion.Sample(rig with { Outward = 0 }, "idle", 1));
    }

    private static float DistanceToSegment(Vector3 point, Vector3 first, Vector3 second)
    {
        Vector3 direction = second - first;
        float t = Math.Clamp(Vector3.Dot(point - first, direction) / direction.LengthSquared(), 0, 1);
        return Vector3.Distance(point, first + direction * t);
    }

    private static float Angle(Quaternion first, Quaternion second) =>
        (float)(2 * Math.Acos(Math.Clamp((double)MathF.Abs(Quaternion.Dot(Quaternion.Normalize(first), Quaternion.Normalize(second))), -1, 1)) * 180 / Math.PI);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Near(float actual, float expected, float tolerance) =>
        Require(float.IsFinite(actual) && MathF.Abs(actual - expected) <= tolerance, $"Expected {expected}, got {actual}.");

    private static void Reject(Action run)
    {
        try { run(); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException("Invalid choreography input was accepted.");
    }
}
