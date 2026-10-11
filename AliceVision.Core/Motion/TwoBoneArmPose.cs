using System.Numerics;

namespace AliceVision.Core.Motion;

/// <summary>A geometric pose in one shared model coordinate system.</summary>
/// <remarks>
/// Both swings rotate a direction from its original bind pose to its solved
/// direction in that coordinate system; LowerSwing is not relative to UpperSwing.
/// Applying them to a scene graph requires conversion to each bone's live parent
/// frame. Joint translations, joint scale and the AR anchor must stay unchanged.
/// </remarks>
public readonly record struct SolvedArm(
    Vector3 Elbow, Vector3 Wrist, Quaternion UpperSwing, Quaternion LowerSwing);

/// <summary>Bounded two-bone reach with a caller-selected outward elbow plane.</summary>
public static class TwoBoneArmPose
{
    /// <summary>
    /// Interpolates two valid solved joint orientations, not wrist points. A
    /// straight wrist path can cross the elbow-hint axis and rapidly flip its
    /// bend plane, even when its scalar timing curve is perfectly smooth.
    /// </summary>
    public static (Quaternion Upper, Quaternion Lower) BlendSwings(SolvedArm rest, SolvedArm raised, float raise)
    {
        if (!float.IsFinite(raise)) throw new ArgumentException("Arm blend must be finite.", nameof(raise));
        float weight = Math.Clamp(raise, 0, 1);
        return (Quaternion.Normalize(Quaternion.Slerp(UnitSwing(rest.UpperSwing), UnitSwing(raised.UpperSwing), weight)),
            Quaternion.Normalize(Quaternion.Slerp(UnitSwing(rest.LowerSwing), UnitSwing(raised.LowerSwing), weight)));
    }

    /// <param name="elbowHint">A point indicating the preferred side of the elbow, in the same frame as the other points.</param>
    public static SolvedArm Solve(
        Vector3 shoulder, Vector3 restElbow, Vector3 restWrist,
        Vector3 target, Vector3 elbowHint)
    {
        RequireFinite(shoulder); RequireFinite(restElbow); RequireFinite(restWrist);
        RequireFinite(target); RequireFinite(elbowHint);
        Vector3 bindUpper = restElbow - shoulder;
        Vector3 bindLower = restWrist - restElbow;
        double upperLength = Length(bindUpper), lowerLength = Length(bindLower);
        if (upperLength <= 0 || lowerLength <= 0)
            throw new ArgumentException("Arm bones must have a nonzero length.");

        double sum = upperLength + lowerLength;
        double epsilon = Math.Min(sum * 1e-5, Math.Min(upperLength, lowerLength) * 0.01);
        double minimum = Math.Abs(upperLength - lowerLength) + epsilon;
        double maximum = sum * 0.98;
        // Extremely unequal bones cannot reach the nominal 2% safety margin.
        // Still keep a nonzero bend within their physically possible interval.
        if (maximum <= minimum) maximum = sum - epsilon;

        Vector3 requested = target - shoulder;
        double requestedLength = Length(requested);
        Vector3 reachAxis = requestedLength > epsilon
            ? Unit(requested, requestedLength) : Unit(bindUpper, upperLength);
        double reach = Math.Clamp(requestedLength, minimum, maximum);
        Vector3 bend = Rejection(elbowHint - shoulder, reachAxis);
        if (Length(bend) <= epsilon) bend = Rejection(bindUpper, reachAxis);
        if (Length(bend) <= epsilon) bend = Perpendicular(reachAxis);
        bend = Unit(bend, Length(bend));

        double along = (upperLength * upperLength - lowerLength * lowerLength + reach * reach) / (2 * reach);
        double height = Math.Sqrt(Math.Max(0, upperLength * upperLength - along * along));
        Vector3 wrist = shoulder + reachAxis * (float)reach;
        Vector3 elbow = shoulder + reachAxis * (float)along + bend * (float)height;
        RequireFinite(elbow); RequireFinite(wrist);
        return new SolvedArm(elbow, wrist,
            FromTo(bindUpper, elbow - shoulder), FromTo(bindLower, wrist - elbow));
    }

    /// <summary>Shortest unit quaternion between arbitrary finite, nonzero directions.</summary>
    public static Quaternion FromTo(Vector3 from, Vector3 to)
    {
        double fromLength = Length(from), toLength = Length(to);
        if (fromLength <= 0 || toLength <= 0)
            throw new ArgumentException("Swing directions must have a nonzero length.");
        Vector3 first = Unit(from, fromLength), second = Unit(to, toLength);
        float dot = Math.Clamp(Vector3.Dot(first, second), -1, 1);
        if (dot >= 1 - 1e-7f) return Quaternion.Identity;
        if (dot <= -1 + 1e-7f)
        {
            Vector3 axis = Perpendicular(first);
            axis = Unit(axis, Length(axis));
            return new Quaternion(axis, 0);
        }
        Vector3 cross = Vector3.Cross(first, second);
        return Quaternion.Normalize(new Quaternion(cross, 1 + dot));
    }

    /// <summary>Aligns a calibrated palm/finger direction and plane normal without assuming local bone axes.</summary>
    public static Quaternion AlignBasis(
        Vector3 bindLongitudinal, Vector3 bindNormal,
        Vector3 targetLongitudinal, Vector3 targetNormal)
    {
        double bindLength = Length(bindLongitudinal), targetLength = Length(targetLongitudinal);
        if (bindLength <= 0 || targetLength <= 0)
            throw new ArgumentException("Basis directions must have a nonzero length.");
        Vector3 bindAxis = Unit(bindLongitudinal, bindLength), targetAxis = Unit(targetLongitudinal, targetLength);
        Vector3 firstNormal = PlaneNormal(bindNormal, bindAxis), secondNormal = PlaneNormal(targetNormal, targetAxis);
        Quaternion swing = FromTo(bindAxis, targetAxis);
        Vector3 swungNormal = Vector3.Transform(firstNormal, swing);
        float sine = Vector3.Dot(targetAxis, Vector3.Cross(swungNormal, secondNormal));
        float cosine = Math.Clamp(Vector3.Dot(swungNormal, secondNormal), -1, 1);
        Quaternion twist = Quaternion.CreateFromAxisAngle(targetAxis, MathF.Atan2(sine, cosine));
        return Quaternion.Normalize(twist * swing);

        static Vector3 PlaneNormal(Vector3 supplied, Vector3 axis)
        {
            Vector3 projected = Rejection(supplied, axis);
            double suppliedLength = Length(supplied), projectedLength = Length(projected);
            if (suppliedLength <= 0 || projectedLength <= suppliedLength * 1e-6)
            {
                projected = Perpendicular(axis); projectedLength = Length(projected);
            }
            return Unit(projected, projectedLength);
        }
    }

    private static Vector3 Rejection(Vector3 vector, Vector3 axis)
    {
        RequireFinite(vector);
        return vector - axis * Vector3.Dot(vector, axis);
    }

    private static Vector3 Perpendicular(Vector3 unit)
    {
        // Choose the least aligned basis, so antiparallel/folded poses are stable.
        Vector3 basis = MathF.Abs(unit.X) <= MathF.Abs(unit.Y) && MathF.Abs(unit.X) <= MathF.Abs(unit.Z)
            ? Vector3.UnitX : MathF.Abs(unit.Y) <= MathF.Abs(unit.Z) ? Vector3.UnitY : Vector3.UnitZ;
        return Vector3.Cross(unit, basis);
    }

    private static Vector3 Unit(Vector3 vector, double length) => new(
        (float)(vector.X / length), (float)(vector.Y / length), (float)(vector.Z / length));

    private static Quaternion UnitSwing(Quaternion swing)
    {
        if (!float.IsFinite(swing.X) || !float.IsFinite(swing.Y) || !float.IsFinite(swing.Z) || !float.IsFinite(swing.W))
            throw new ArgumentException("Arm swings must be finite.");
        double length = Math.Sqrt((double)swing.X * swing.X + (double)swing.Y * swing.Y +
            (double)swing.Z * swing.Z + (double)swing.W * swing.W);
        if (length <= 0) throw new ArgumentException("Arm swings must have a nonzero length.");
        return new Quaternion((float)(swing.X / length), (float)(swing.Y / length),
            (float)(swing.Z / length), (float)(swing.W / length));
    }

    private static double Length(Vector3 vector)
    {
        RequireFinite(vector);
        return Math.Sqrt((double)vector.X * vector.X + (double)vector.Y * vector.Y + (double)vector.Z * vector.Z);
    }

    private static void RequireFinite(Vector3 vector)
    {
        if (!float.IsFinite(vector.X) || !float.IsFinite(vector.Y) || !float.IsFinite(vector.Z))
            throw new ArgumentException("Arm pose coordinates must be finite.");
    }
}
