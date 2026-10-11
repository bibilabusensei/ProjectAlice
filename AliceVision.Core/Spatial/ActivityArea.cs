using System.Collections.ObjectModel;
using System.Numerics;

namespace AliceVision.Core.Spatial;

/// <summary>
/// User-defined XZ polygon on one fixed AR plane. A whole character footprint, not just its center,
/// must fit. This geometry is not a substitute for current ARKit tracking/mesh obstruction checks.
/// </summary>
public sealed class ActivityArea
{
    private const double Epsilon = 1e-7;
    private readonly Vector2[] outer;
    private readonly Vector2[][] exclusions;
    public string PlaneId { get; }
    public long Revision { get; }
    public double PlaneY { get; }
    public IReadOnlyList<Vector2> Boundary { get; }
    public IReadOnlyList<IReadOnlyList<Vector2>> Holes { get; }
    public Vector2 MinBounds { get; }
    public Vector2 MaxBounds { get; }

    public ActivityArea(string planeId, long revision, double planeY, IReadOnlyList<Vector2> boundary,
        IReadOnlyList<IReadOnlyList<Vector2>>? holes = null)
    {
        if (string.IsNullOrWhiteSpace(planeId) || planeId.Length > 128 || planeId.Any(char.IsControl))
            throw new ArgumentException("活动区必须绑定一个明确的 AR 平面。", nameof(planeId));
        if (revision <= 0) throw new ArgumentOutOfRangeException(nameof(revision));
        if (!double.IsFinite(planeY) || Math.Abs(planeY) > 10000) throw new ArgumentOutOfRangeException(nameof(planeY));
        ArgumentNullException.ThrowIfNull(boundary);
        if (boundary.Count is < 3 or > 32) throw new ArgumentException("活动区边界须有 3 至 32 个顶点。", nameof(boundary));
        if (holes is { Count: > 8 }) throw new ArgumentException("禁行区数量超出上限。", nameof(holes));
        outer = boundary.ToArray();
        exclusions = holes?.Select(hole => hole is not null && hole.Count is >= 3 and <= 32
            ? hole.ToArray() : throw new ArgumentException("禁行区须有 3 至 32 个顶点。", nameof(holes))).ToArray() ?? [];
        ValidatePolygon(outer);
        Vector2 minimum = outer[0], maximum = outer[0];
        foreach (Vector2 point in outer) { minimum = Vector2.Min(minimum, point); maximum = Vector2.Max(maximum, point); }
        if ((double)maximum.X - minimum.X > 10 || (double)maximum.Y - minimum.Y > 10)
            throw new ArgumentException("活动区范围不能超过 10 米。", nameof(boundary));
        foreach (Vector2[] hole in exclusions)
        {
            ValidatePolygon(hole);
            if (hole.Any(point => !PointInside(point, outer)) || EdgesIntersect(outer, hole))
                throw new ArgumentException("禁行区须完整位于活动区内部且不能触碰边界。", nameof(holes));
        }
        for (int first = 0; first < exclusions.Length; first++)
            for (int second = first + 1; second < exclusions.Length; second++)
                if (EdgesIntersect(exclusions[first], exclusions[second]) ||
                    PointInside(exclusions[first][0], exclusions[second]) || PointInside(exclusions[second][0], exclusions[first]))
                    throw new ArgumentException("禁行区不能重叠、接触或嵌套。", nameof(holes));
        PlaneId = planeId; Revision = revision; PlaneY = planeY;
        MinBounds = minimum; MaxBounds = maximum;
        Boundary = new ReadOnlyCollection<Vector2>(outer);
        Holes = new ReadOnlyCollection<IReadOnlyList<Vector2>>(exclusions.Select(hole =>
            (IReadOnlyList<Vector2>)new ReadOnlyCollection<Vector2>(hole)).ToArray());
    }

    public bool ContainsWithClearance(Vector2 point, double radius)
    {
        if (!ValidPoint(point) || !ValidRadius(radius) || !PointInside(point, outer)) return false;
        if (!PointHasClearance(point, outer, radius)) return false;
        foreach (Vector2[] hole in exclusions)
            if (PointInside(point, hole) || !PointHasClearance(point, hole, radius)) return false;
        return true;
    }

    /// <summary>Exact 2D swept-circle boundary clearance, including arbitrarily thin holes/concave edges.</summary>
    public bool SegmentAllowed(Vector2 start, Vector2 end, double radius)
    {
        if (!ContainsWithClearance(start, radius) || !ContainsWithClearance(end, radius)) return false;
        if (!SegmentHasClearance(start, end, outer, radius)) return false;
        foreach (Vector2[] hole in exclusions)
            if (!SegmentHasClearance(start, end, hole, radius)) return false;
        return true;
    }

    /// <summary>Exact support-plane sweep; ARKit boundaries can have more vertices than user areas.</summary>
    public static bool SegmentInsideSupportBoundary(Vector2[] polygon, Vector2 start, Vector2 end, double radius)
    {
        if (polygon is null || polygon.Length is < 3 or > 2048 || polygon.Any(point => !ValidPoint(point)) ||
            !ValidPoint(start) || !ValidPoint(end) || !ValidRadius(radius)) return false;
        return PointInside(start, polygon) && PointInside(end, polygon) &&
            PointHasClearance(start, polygon, radius) && PointHasClearance(end, polygon, radius) &&
            SegmentHasClearance(start, end, polygon, radius);
    }

    private static void ValidatePolygon(Vector2[] polygon)
    {
        if (polygon.Any(point => !ValidPoint(point))) throw new ArgumentException("活动区坐标必须为有限数值。");
        double twiceArea = 0;
        for (int i = 0; i < polygon.Length; i++)
        {
            Vector2 first = polygon[i], second = polygon[(i + 1) % polygon.Length];
            if (DistanceSquared(first, second) < 1e-8) throw new ArgumentException("边界不能包含重复顶点或零长度边。");
            twiceArea += (double)first.X * second.Y - (double)second.X * first.Y;
            Vector2 previous = polygon[(i + polygon.Length - 1) % polygon.Length];
            // Adjacent edges may meet only at their shared endpoint; backtracking is invalid.
            if (Math.Abs(Cross(previous, first, second)) <= Epsilon &&
                ((double)previous.X - first.X) * (second.X - first.X) + ((double)previous.Y - first.Y) * (second.Y - first.Y) > 0)
                throw new ArgumentException("边界不能沿相邻边回折重叠。");
        }
        if (Math.Abs(twiceArea) <= 1e-6) throw new ArgumentException("活动区面积过小或退化。");
        for (int first = 0; first < polygon.Length; first++)
            for (int second = first + 1; second < polygon.Length; second++)
            {
                if (second == first + 1 || (first == 0 && second == polygon.Length - 1)) continue;
                if (SegmentsIntersect(polygon[first], polygon[(first + 1) % polygon.Length],
                    polygon[second], polygon[(second + 1) % polygon.Length]))
                    throw new ArgumentException("活动区边界不能自相交或自接触。");
            }
    }

    private static bool ValidPoint(Vector2 point) => float.IsFinite(point.X) && float.IsFinite(point.Y) &&
        Math.Abs(point.X) <= 10000 && Math.Abs(point.Y) <= 10000;
    private static bool ValidRadius(double radius) => double.IsFinite(radius) && radius is >= 0 and <= 2;

    private static bool PointInside(Vector2 point, Vector2[] polygon)
    {
        bool inside = false;
        for (int i = 0, previous = polygon.Length - 1; i < polygon.Length; previous = i++)
        {
            Vector2 first = polygon[previous], second = polygon[i];
            if (PointSegmentDistanceSquared(point, first, second) <= Epsilon * Epsilon) return false;
            if ((first.Y > point.Y) != (second.Y > point.Y) && point.X <
                ((double)second.X - first.X) * (point.Y - first.Y) / ((double)second.Y - first.Y) + first.X)
                inside = !inside;
        }
        return inside;
    }

    private static bool PointHasClearance(Vector2 point, Vector2[] boundary, double radius)
    {
        double minimum = (radius + Epsilon) * (radius + Epsilon);
        for (int i = 0; i < boundary.Length; i++)
            if (PointSegmentDistanceSquared(point, boundary[i], boundary[(i + 1) % boundary.Length]) < minimum) return false;
        return true;
    }

    private static bool SegmentHasClearance(Vector2 start, Vector2 end, Vector2[] boundary, double radius)
    {
        double minimum = (radius + Epsilon) * (radius + Epsilon);
        for (int i = 0; i < boundary.Length; i++)
            if (SegmentDistanceSquared(start, end, boundary[i], boundary[(i + 1) % boundary.Length]) < minimum) return false;
        return true;
    }

    private static bool EdgesIntersect(Vector2[] first, Vector2[] second)
    {
        for (int i = 0; i < first.Length; i++)
            for (int j = 0; j < second.Length; j++)
                if (SegmentsIntersect(first[i], first[(i + 1) % first.Length], second[j], second[(j + 1) % second.Length])) return true;
        return false;
    }

    private static double SegmentDistanceSquared(Vector2 first, Vector2 second, Vector2 third, Vector2 fourth)
    {
        if (SegmentsIntersect(first, second, third, fourth)) return 0;
        return Math.Min(Math.Min(PointSegmentDistanceSquared(first, third, fourth), PointSegmentDistanceSquared(second, third, fourth)),
            Math.Min(PointSegmentDistanceSquared(third, first, second), PointSegmentDistanceSquared(fourth, first, second)));
    }

    private static double PointSegmentDistanceSquared(Vector2 point, Vector2 start, Vector2 end)
    {
        double dx = (double)end.X - start.X, dy = (double)end.Y - start.Y;
        double length = dx * dx + dy * dy;
        if (length <= 1e-20) return DistanceSquared(point, start);
        double t = Math.Clamp((((double)point.X - start.X) * dx + ((double)point.Y - start.Y) * dy) / length, 0, 1);
        double x = point.X - (start.X + t * dx), y = point.Y - (start.Y + t * dy);
        return x * x + y * y;
    }

    private static bool SegmentsIntersect(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
    {
        double first = Cross(a, b, c), second = Cross(a, b, d), third = Cross(c, d, a), fourth = Cross(c, d, b);
        if (((first > Epsilon && second < -Epsilon) || (first < -Epsilon && second > Epsilon)) &&
            ((third > Epsilon && fourth < -Epsilon) || (third < -Epsilon && fourth > Epsilon))) return true;
        return (Math.Abs(first) <= Epsilon && PointSegmentDistanceSquared(c, a, b) <= Epsilon * Epsilon) ||
            (Math.Abs(second) <= Epsilon && PointSegmentDistanceSquared(d, a, b) <= Epsilon * Epsilon) ||
            (Math.Abs(third) <= Epsilon && PointSegmentDistanceSquared(a, c, d) <= Epsilon * Epsilon) ||
            (Math.Abs(fourth) <= Epsilon && PointSegmentDistanceSquared(b, c, d) <= Epsilon * Epsilon);
    }

    private static double Cross(Vector2 a, Vector2 b, Vector2 c) => ((double)b.X - a.X) * ((double)c.Y - a.Y) -
        ((double)b.Y - a.Y) * ((double)c.X - a.X);
    private static double DistanceSquared(Vector2 first, Vector2 second) =>
        ((double)first.X - second.X) * ((double)first.X - second.X) + ((double)first.Y - second.Y) * ((double)first.Y - second.Y);
}
