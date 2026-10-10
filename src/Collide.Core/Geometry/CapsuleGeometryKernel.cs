namespace Collide.Geometry;

/// <summary>
/// Dependency-free kernel. Bars are modelled as tubes swept along their centreline (capsule primitives),
/// with flat-ended cylinders at free bar ends. Straight elements are exact; arcs are chord-approximated with a
/// reported error bound. Overlap volumes are analytic for parallel straight cylinders, otherwise voxel-sampled.
/// </summary>
public sealed class CapsuleGeometryKernel : IGeometryKernel
{
    private readonly int _voxelResolution;

    public CapsuleGeometryKernel(int voxelResolution = 24)
    {
        if (voxelResolution < 4) throw new ArgumentOutOfRangeException(nameof(voxelResolution));
        _voxelResolution = voxelResolution;
    }

    public string Name => "CapsuleGeometryKernel/1.0";

    public string Limitations =>
        "Bars are swept tubes along chord-approximated centrelines (arc chord error reported per measurement). " +
        "Free bar ends are flat discs; near-end separations are exact lower bounds (max of two one-sided exact cylinder distances). " +
        "Rib/deformation geometry is represented only through the model diameter. Overlap volume is analytic for parallel " +
        "straight cylinders and voxel-sampled otherwise.";

    public BarSolid CreateSolid(string id, BarPath path, double physicalDiameter, double chordTolerance) =>
        BarSolid.FromPath(id, path, physicalDiameter / 2, chordTolerance);

    public SegmentProximity Measure(in SolidSegment a, in SolidSegment b)
    {
        var dist = SegmentMath.ClosestPoints(a.A, a.B, b.A, b.B, out var s, out var t, out var c1, out var c2);
        var signed = dist - a.Radius - b.Radius;
        var da = a.Direction;
        var db = b.Direction;
        var angle = da == Vec3.Zero || db == Vec3.Zero ? 0 : Math.Acos(Math.Clamp(Math.Abs(Vec3.Dot(da, db)), 0, 1));
        var method = ProximityMethod.Capsule;
        var error = a.ChordError + b.ChordError;

        const double endEps = 1e-9;
        var atFreeEnd = (a.FreeStart && s <= endEps) || (a.FreeEnd && s >= 1 - endEps) ||
                        (b.FreeStart && t <= endEps) || (b.FreeEnd && t >= 1 - endEps);
        if (atFreeEnd && signed < Math.Max(a.Radius, b.Radius))
        {
            var sa = a;
            var sb = b;
            var axisBToSolidA = SegmentMath.MinimizeConvex(u =>
                SegmentMath.PointToBarSegment(Vec3.Lerp(sb.A, sb.B, u), sa.A, sa.B, sa.Radius, sa.FreeStart, sa.FreeEnd)).Value;
            var axisAToSolidB = SegmentMath.MinimizeConvex(u =>
                SegmentMath.PointToBarSegment(Vec3.Lerp(sa.A, sa.B, u), sb.A, sb.B, sb.Radius, sb.FreeStart, sb.FreeEnd)).Value;
            var axisPenetrates = axisBToSolidA <= 1e-12 || axisAToSolidB <= 1e-12;
            var refined = Math.Max(axisBToSolidA - b.Radius, axisAToSolidB - a.Radius);
            if (!axisPenetrates && refined > signed + 1e-9)
            {
                signed = refined;
                method = ProximityMethod.FlatEndRefined;
            }
        }

        return new SegmentProximity(signed, dist, c1, c2, s, t, angle, method, error);
    }

    public OverlapVolume EstimateOverlapVolume(BarSolid a, BarSolid b, Aabb region)
    {
        if (region.IsEmpty) return new OverlapVolume(0, VolumeMethod.None, 0);

        if (a.Segments.Count == 1 && b.Segments.Count == 1)
        {
            var sa = a.Segments[0];
            var sb = b.Segments[0];
            var dir = sa.Direction;
            if (dir != Vec3.Zero && Math.Abs(Vec3.Dot(dir, sb.Direction)) > Math.Cos(1e-4))
            {
                var p0 = Vec3.Dot(sb.A - sa.A, dir);
                var p1 = Vec3.Dot(sb.B - sa.A, dir);
                var overlap = Math.Max(0, Math.Min(sa.Length, Math.Max(p0, p1)) - Math.Max(0, Math.Min(p0, p1)));
                var offset = ((sb.A - sa.A) - dir * Vec3.Dot(sb.A - sa.A, dir)).Length;
                var area = SegmentMath.CircleIntersectionArea(sa.Radius, sb.Radius, offset);
                return new OverlapVolume(area * overlap, VolumeMethod.AnalyticParallelCylinders, 0);
            }
        }

        var n = _voxelResolution;
        var size = region.Size;
        if (size.X <= 0 || size.Y <= 0 || size.Z <= 0) return new OverlapVolume(0, VolumeMethod.VoxelSampling, 0);
        var cell = new Vec3(size.X / n, size.Y / n, size.Z / n);
        var segA = a.Segments.Where(s => s.Bounds.Intersects(region)).ToArray();
        var segB = b.Segments.Where(s => s.Bounds.Intersects(region)).ToArray();
        var inside = 0;
        for (var i = 0; i < n; i++)
        for (var j = 0; j < n; j++)
        for (var k = 0; k < n; k++)
        {
            var p = region.Min + new Vec3((i + 0.5) * cell.X, (j + 0.5) * cell.Y, (k + 0.5) * cell.Z);
            if (InAny(segA, p) && InAny(segB, p)) inside++;
        }

        var volume = inside * cell.X * cell.Y * cell.Z;
        return new OverlapVolume(volume, VolumeMethod.VoxelSampling, n * n * n);
    }

    private static bool InAny(SolidSegment[] segs, Vec3 p)
    {
        foreach (var s in segs)
        {
            if (SegmentMath.PointToBarSegment(p, s.A, s.B, s.Radius, s.FreeStart, s.FreeEnd) <= 0) return true;
        }

        return false;
    }
}
