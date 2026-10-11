using System.Numerics;
using AliceVision.Core.Motion;

internal static class ArmPoseTests
{
    public static IReadOnlyList<(string Name, Action Run)> Cases { get; } = new (string, Action)[]
    {
        ("two-bone T-pose preserves lengths and targets a bent greeting arm", Greeting),
        ("two-bone A-pose does not require canonical bind axes", AngledBind),
        ("two-bone solve is invariant to scale rotation and translation", RotatedScaled),
        ("two-bone solve mirrors left and right outward elbow hints", Mirrored),
        ("two-bone far target keeps a 2 percent extension margin", FarReach),
        ("two-bone folded target is finite and respects minimum reach", Folded),
        ("two-bone collinear hint has a stable finite fallback", Collinear),
        ("two-bone very unequal lengths remain physically reachable", UnequalLengths),
        ("two-bone rejects zero bones and nonfinite metadata", InvalidInputs),
        ("two-bone swing handles parallel antiparallel and arbitrary directions", SwingCases),
        ("two-bone actual VRM proportions form a plausible bent greeting", ActualModelProportions),
        ("two-bone lower swing is absolute not upper-local", AbsoluteLowerSwing),
        ("palm alignment maps calibrated finger and normal axes", PalmBasis),
        ("palm alignment handles collinear normal fallback without NaN", PalmFallback),
        ("lift joint path avoids singular straight wrist interpolation", LiftPath),
        ("lift joint path stays smooth on mirrored rotated A-pose rigs", ArbitraryLiftPath),
        ("lift joint blend clamps weight and rejects nonfinite rotations", BlendValidation),
    };

    private static void Greeting()
    {
        var pose = Check(Vector3.Zero, Vector3.UnitX, Vector3.UnitX * 1.85f,
            new Vector3(0.65f, 0.65f, 0.14f), new Vector3(0.8f, -0.45f, 0.06f));
        Require(pose.Elbow.Y < pose.Wrist.Y - 0.2f, "Greeting forearm must point up, not remain nearly straight.");
        Require(pose.Elbow.X > 0.65f, "Elbow must stay outward instead of crossing the face.");
    }

    private static void AngledBind()
    {
        Check(new Vector3(0.2f, 1.4f, 0), new Vector3(0.65f, 0.8f, 0.1f),
            new Vector3(0.85f, 0.3f, 0.2f), new Vector3(0.75f, 1.7f, 0.18f), new Vector3(0.9f, 1.1f, 0.15f));
    }

    private static void RotatedScaled()
    {
        Vector3 s = Vector3.Zero, e = Vector3.UnitX, w = Vector3.UnitX * 1.85f;
        Vector3 t = new(0.65f, 0.65f, 0.14f), h = new(0.8f, -0.45f, 0.06f);
        var original = TwoBoneArmPose.Solve(s, e, w, t, h);
        Quaternion rotation = Quaternion.CreateFromYawPitchRoll(1.2f, -0.7f, 0.36f);
        Vector3 offset = new(-3.2f, 1.7f, 5.5f);
        Vector3 Transform(Vector3 point) => Vector3.Transform(point * 27.5f, rotation) + offset;
        var moved = Check(Transform(s), Transform(e), Transform(w), Transform(t), Transform(h));
        Close(moved.Elbow, Transform(original.Elbow), 0.0001f);
        Close(moved.Wrist, Transform(original.Wrist), 0.0001f);
    }

    private static void Mirrored()
    {
        Vector3 Mirror(Vector3 point) => new(-point.X, point.Y, point.Z);
        Vector3 s = new(0.4f, 1.6f, 0), e = new(1.4f, 1.6f, 0), w = new(2.25f, 1.6f, 0);
        Vector3 t = new(1.05f, 2.25f, 0.14f), h = new(1.2f, 1.15f, 0.06f);
        var left = Check(s, e, w, t, h);
        var right = Check(Mirror(s), Mirror(e), Mirror(w), Mirror(t), Mirror(h));
        Close(right.Elbow, Mirror(left.Elbow)); Close(right.Wrist, Mirror(left.Wrist));
    }

    private static void FarReach()
    {
        var pose = Check(Vector3.Zero, Vector3.UnitX, Vector3.UnitX * 1.85f,
            new Vector3(100, 0, 0), Vector3.UnitY);
        Close(pose.Wrist.Length(), 1.85f * 0.98f);
        Require(pose.Elbow.Y > 0.05f, "Fully stretched elbows must keep a safety bend.");
    }

    private static void Folded()
    {
        var unequal = Check(Vector3.Zero, Vector3.UnitX, Vector3.UnitX * 1.85f,
            Vector3.Zero, new Vector3(0, 1, 0));
        Require(unequal.Wrist.Length() > 0.15f, "Minimum reach must avoid impossible folding.");
        var equal = Check(Vector3.Zero, Vector3.UnitX, Vector3.UnitX * 2,
            Vector3.Zero, Vector3.UnitY);
        Require(equal.Wrist.Length() > 0 && equal.Wrist.Length() < 0.001f, "Equal bones may approach a folded pose without dividing by zero.");
    }

    private static void Collinear()
    {
        var first = Check(Vector3.Zero, Vector3.UnitX, Vector3.UnitX * 1.85f,
            Vector3.UnitX, Vector3.UnitX * 2);
        var repeat = TwoBoneArmPose.Solve(Vector3.Zero, Vector3.UnitX, Vector3.UnitX * 1.85f,
            Vector3.UnitX, Vector3.UnitX * 2);
        Close(first.Elbow, repeat.Elbow);
    }

    private static void UnequalLengths() => Check(Vector3.Zero, Vector3.UnitX * 1000,
        Vector3.UnitX * 1001, Vector3.UnitY * 10, new Vector3(500, 500, 0));

    private static void InvalidInputs()
    {
        var valid = new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitX * 2, Vector3.UnitY, Vector3.UnitZ };
        for (int index = 0; index < valid.Length; index++)
        {
            var bad = valid.ToArray(); bad[index] = new Vector3(float.NaN, 0, 0);
            Throws(() => TwoBoneArmPose.Solve(bad[0], bad[1], bad[2], bad[3], bad[4]));
            bad[index] = new Vector3(0, float.PositiveInfinity, 0);
            Throws(() => TwoBoneArmPose.Solve(bad[0], bad[1], bad[2], bad[3], bad[4]));
        }
        Throws(() => TwoBoneArmPose.Solve(Vector3.Zero, Vector3.Zero, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ));
        Throws(() => TwoBoneArmPose.Solve(Vector3.Zero, Vector3.UnitX, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ));
        Throws(() => TwoBoneArmPose.FromTo(Vector3.Zero, Vector3.UnitX));
    }

    private static void SwingCases()
    {
        foreach (var (first, second) in new[] {
            (Vector3.UnitX, Vector3.UnitX), (Vector3.UnitY, -Vector3.UnitY),
            (new Vector3(1, 2, 3), new Vector3(-2, 1, 0.7f)), (Vector3.UnitX, -Vector3.UnitX) })
        {
            Quaternion swing = TwoBoneArmPose.FromTo(first, second);
            Close(swing.Length(), 1);
            Close(Vector3.Transform(Vector3.Normalize(first), swing), Vector3.Normalize(second));
        }
    }

    private static void ActualModelProportions()
    {
        // Ratios only: no private geometry, textures or model data are embedded.
        Vector3 shoulder = new(0, 1.6f, 0), elbow = shoulder + Vector3.UnitX * 0.245f;
        Vector3 wrist = elbow + Vector3.UnitX * 0.212f;
        var pose = Check(shoulder, elbow, wrist,
            shoulder + new Vector3(0.16f, 0.17f, 0.025f), shoulder + new Vector3(0.24f, -0.08f, 0.02f));
        Vector3 up = Vector3.Normalize(pose.Elbow - shoulder), forearm = Vector3.Normalize(pose.Wrist - pose.Elbow);
        float bend = MathF.Acos(Math.Clamp(Vector3.Dot(up, forearm), -1, 1));
        Require(bend > 1.3f && bend < 2.5f, "A hello pose should visibly bend the elbow.");
    }

    private static void AbsoluteLowerSwing()
    {
        Vector3 s = Vector3.Zero, e = new(0.7f, -0.7f, 0), w = new(1.2f, -1.35f, 0.2f);
        var pose = Check(s, e, w, new Vector3(0.65f, 0.65f, 0.14f), new Vector3(0.8f, -0.45f, 0.06f));
        Close(Vector3.Transform(Vector3.Normalize(w - e), pose.LowerSwing), Vector3.Normalize(pose.Wrist - pose.Elbow));
    }

    private static void PalmBasis()
    {
        Quaternion bindRotation = Quaternion.CreateFromYawPitchRoll(1.5f, -0.4f, 0.25f);
        Vector3 bindLongitudinal = Vector3.Transform(Vector3.UnitX, bindRotation);
        Vector3 bindNormal = Vector3.Transform(Vector3.UnitZ, bindRotation);
        Quaternion align = TwoBoneArmPose.AlignBasis(bindLongitudinal, bindNormal, Vector3.UnitY, Vector3.UnitZ);
        Close(align.Length(), 1);
        Close(Vector3.Transform(bindLongitudinal, align), Vector3.UnitY);
        Close(Vector3.Transform(bindNormal, align), Vector3.UnitZ);
        var antiparallel = TwoBoneArmPose.AlignBasis(Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitY, -Vector3.UnitZ);
        Close(Vector3.Transform(Vector3.UnitY, antiparallel), -Vector3.UnitY);
        Close(Vector3.Transform(Vector3.UnitZ, antiparallel), -Vector3.UnitZ);
    }

    private static void PalmFallback()
    {
        Quaternion align = TwoBoneArmPose.AlignBasis(Vector3.UnitX, Vector3.UnitX,
            Vector3.UnitY, Vector3.UnitY);
        Close(align.Length(), 1); Close(Vector3.Transform(Vector3.UnitX, align), Vector3.UnitY);
        Throws(() => TwoBoneArmPose.AlignBasis(Vector3.UnitX, new Vector3(float.NaN, 0, 0), Vector3.UnitY, Vector3.UnitZ));
    }

    private static void LiftPath()
    {
        // Generic human arm proportions reproduce the singular hint-plane
        // crossing; this is not a fixture containing private model geometry.
        var path = SampleLift(Vector3.Zero, new Vector3(-1.16f, 0, 0), new Vector3(-2.16f, 0, 0), -1);
        Require(path.SafeUpperDegrees < 3 && path.SafeLowerDegrees < 7,
            $"Joint blend jumped {path.SafeUpperDegrees:F2}/{path.SafeLowerDegrees:F2} degrees in one 60 Hz frame.");
        Require(path.FormerLowerDegrees > 40,
            "Regression no longer exercises the former nearly collinear wrist/hint path.");
        Require(path.MaximumWristDistance <= 2.16f + 0.0001f, "Lift must not extend beyond the original two bones.");
    }

    private static void ArbitraryLiftPath()
    {
        foreach (float outward in new[] { -1f, 1f })
        {
            // A-pose bind vectors have forward offset; rotating a model frame
            // must not change this safe joint path or depend on local X/Y axes.
            Quaternion rotation = Quaternion.CreateFromYawPitchRoll(-0.6f, 0.8f, 1.1f);
            Vector3 shoulder = new(3, -2, 4);
            Vector3 elbow = shoulder + new Vector3(outward * 0.8f, -0.75f, 0.13f);
            Vector3 wrist = elbow + new Vector3(outward * 0.65f, -0.7f, 0.11f);
            var nativeFrame = SampleLift(shoulder, elbow, wrist, outward);
            var rotatedFrame = SampleLift(Vector3.Transform(shoulder, rotation), Vector3.Transform(elbow, rotation),
                Vector3.Transform(wrist, rotation), outward, rotation);
            Require(nativeFrame.SafeUpperDegrees < 4 && nativeFrame.SafeLowerDegrees < 8, "A-pose lift must not snap.");
            Close(rotatedFrame.SafeUpperDegrees, nativeFrame.SafeUpperDegrees, 0.02f);
            Close(rotatedFrame.SafeLowerDegrees, nativeFrame.SafeLowerDegrees, 0.02f);
        }
    }

    private static (float SafeUpperDegrees, float SafeLowerDegrees, float FormerLowerDegrees, float MaximumWristDistance)
        SampleLift(Vector3 shoulder, Vector3 elbow, Vector3 wrist, float outward, Quaternion? modelFrameRotation = null)
    {
        float upperLength = (elbow - shoulder).Length(), lowerLength = (wrist - elbow).Length();
        float sum = upperLength + lowerLength;
        Quaternion frame = modelFrameRotation ?? Quaternion.Identity;
        Vector3 FrameVector(Vector3 vector) => Vector3.Transform(vector, frame);
        Vector3 rest = shoulder + FrameVector(Vector3.Normalize(new(outward * 0.15f, -0.98f, 0.10f)) * sum * 0.985f);
        Vector3 raised = shoulder + FrameVector(new(outward * upperLength * 0.62f, lowerLength * 0.87f, upperLength * 0.30f));
        Vector3 hint = shoulder + FrameVector(new(outward * upperLength * 0.8f, -upperLength * 0.65f, upperLength * 0.20f));
        SolvedArm restPose = TwoBoneArmPose.Solve(shoulder, elbow, wrist, rest, hint);
        float maxUpper = 0, maxLower = 0, formerMaxLower = 0, maxDistance = 0;
        Quaternion? lastUpper = null, lastLower = null, lastFormerLower = null;
        for (int index = 0; index <= 252; index++)
        {
            WaveMotionSample sample = HumanoidGestureMotion.SampleWave(index / 60.0);
            Vector3 lateral = FrameVector(new(outward * lowerLength * sample.ForearmSwing, 0, 0));
            SolvedArm raisedPose = TwoBoneArmPose.Solve(shoulder, elbow, wrist, raised + lateral, hint);
            var swings = TwoBoneArmPose.BlendSwings(restPose, raisedPose, sample.Raise);
            if (lastUpper is { } previousUpper) maxUpper = MathF.Max(maxUpper, Angle(previousUpper, swings.Upper));
            if (lastLower is { } previousLower) maxLower = MathF.Max(maxLower, Angle(previousLower, swings.Lower));
            lastUpper = swings.Upper; lastLower = swings.Lower;
            Vector3 solvedElbow = shoulder + Vector3.Transform(elbow - shoulder, swings.Upper);
            Vector3 solvedWrist = solvedElbow + Vector3.Transform(wrist - elbow, swings.Lower);
            Close((solvedElbow - shoulder).Length(), upperLength, 0.00005f);
            Close((solvedWrist - solvedElbow).Length(), lowerLength, 0.00005f);
            maxDistance = MathF.Max(maxDistance, (solvedWrist - shoulder).Length());
            SolvedArm former = TwoBoneArmPose.Solve(shoulder, elbow, wrist,
                Vector3.Lerp(rest, raised, sample.Raise) + lateral, hint);
            if (lastFormerLower is { } prior) formerMaxLower = MathF.Max(formerMaxLower, Angle(prior, former.LowerSwing));
            lastFormerLower = former.LowerSwing;
        }
        return (maxUpper, maxLower, formerMaxLower, maxDistance);

        static float Angle(Quaternion first, Quaternion second) =>
            (float)(2 * Math.Acos(Math.Clamp((double)MathF.Abs(Quaternion.Dot(first, second)), -1, 1)) * 180 / Math.PI);
    }

    private static void BlendValidation()
    {
        SolvedArm rest = TwoBoneArmPose.Solve(Vector3.Zero, Vector3.UnitX, Vector3.UnitX * 1.85f,
            new Vector3(0.65f, 0.65f, 0.14f), new Vector3(0.8f, -0.45f, 0.06f));
        SolvedArm raised = TwoBoneArmPose.Solve(Vector3.Zero, Vector3.UnitX, Vector3.UnitX * 1.85f,
            new Vector3(0.7f, 0.8f, 0.14f), new Vector3(0.8f, -0.45f, 0.06f));
        var low = TwoBoneArmPose.BlendSwings(rest, raised, -1);
        var high = TwoBoneArmPose.BlendSwings(rest, raised, 2);
        Close(Vector3.Transform(Vector3.UnitX, low.Upper), Vector3.Transform(Vector3.UnitX, rest.UpperSwing));
        Close(Vector3.Transform(Vector3.UnitX, high.Lower), Vector3.Transform(Vector3.UnitX, raised.LowerSwing));
        Throws(() => TwoBoneArmPose.BlendSwings(rest, raised, float.NaN));
        Throws(() => TwoBoneArmPose.BlendSwings(rest, raised, float.PositiveInfinity));
        Throws(() => TwoBoneArmPose.BlendSwings(rest with { UpperSwing = default }, raised, 0.5f));
        Throws(() => TwoBoneArmPose.BlendSwings(rest, raised with { LowerSwing = new Quaternion(float.NaN, 0, 0, 1) }, 0.5f));
    }

    private static SolvedArm Check(Vector3 s, Vector3 e, Vector3 w, Vector3 target, Vector3 hint)
    {
        var pose = TwoBoneArmPose.Solve(s, e, w, target, hint);
        float tolerance = MathF.Max(0.00002f, (e - s).Length() * 0.00002f);
        Close((pose.Elbow - s).Length(), (e - s).Length(), tolerance);
        Close((pose.Wrist - pose.Elbow).Length(), (w - e).Length(), tolerance);
        Close(pose.UpperSwing.Length(), 1); Close(pose.LowerSwing.Length(), 1);
        Close(Vector3.Transform(Vector3.Normalize(e - s), pose.UpperSwing), Vector3.Normalize(pose.Elbow - s));
        Close(Vector3.Transform(Vector3.Normalize(w - e), pose.LowerSwing), Vector3.Normalize(pose.Wrist - pose.Elbow));
        return pose;
    }

    private static void Close(Vector3 actual, Vector3 expected, float tolerance = 0.00002f) =>
        Require(Vector3.Distance(actual, expected) <= tolerance, $"Expected {expected}, got {actual}.");

    private static void Close(float actual, float expected, float tolerance = 0.00002f) =>
        Require(float.IsFinite(actual) && MathF.Abs(actual - expected) <= tolerance, $"Expected {expected}, got {actual}.");

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Throws(Action run)
    {
        try { run(); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException("Invalid arm metadata was accepted.");
    }
}
