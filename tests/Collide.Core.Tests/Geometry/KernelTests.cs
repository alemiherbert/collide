using Collide.Geometry;

namespace Collide.Core.Tests.Geometry;

public class KernelTests
{
    private static readonly CapsuleGeometryKernel Kernel = new();

    private static SolidSegment Seg(Vec3 a, Vec3 b, double r, bool freeStart = false, bool freeEnd = false) =>
        new(a, b, r, 0, PathElementKind.Straight, 0, freeStart, freeEnd);

    [Fact]
    public void ParallelOverlappingBarsReportPenetrationDepth()
    {
        // Two Ø20 bars whose centrelines are 15 mm apart: surfaces overlap by 5 mm.
        var a = Seg(new Vec3(0, 0, 0), new Vec3(1000, 0, 0), 10);
        var b = Seg(new Vec3(0, 15, 0), new Vec3(1000, 15, 0), 10);
        var p = Kernel.Measure(a, b);
        Assert.Equal(-5, p.SurfaceDistance, 9);
        Assert.Equal(5, p.Penetration, 9);
        Assert.Equal(15, p.CentrelineDistance, 9);
        Assert.Equal(0, p.AngleRadians, 9);
        // Representative location in the middle of the overlap, not an arbitrary endpoint.
        Assert.Equal(500, p.PointA.X, 6);
    }

    [Fact]
    public void CrossingCentrelinesInPlanAtDifferentElevationsDoNotCollide()
    {
        // Centrelines cross in plan view, but are 25 mm apart vertically; Ø12 + Ø12 leaves a 13 mm gap.
        var a = Seg(new Vec3(-500, 0, 0), new Vec3(500, 0, 0), 6);
        var b = Seg(new Vec3(0, -500, 25), new Vec3(0, 500, 25), 6);
        var p = Kernel.Measure(a, b);
        Assert.Equal(13, p.SurfaceDistance, 9);
        Assert.Equal(25, p.CentrelineDistance, 9);
        Assert.Equal(Math.PI / 2, p.AngleRadians, 9);
    }

    [Fact]
    public void FlatFreeEndIsNotTreatedAsHemisphere()
    {
        // Bar A (Ø20) ends at x=100 (flat cut). Perpendicular bar B (Ø20) runs along Y at x=115.
        // Capsule model would report −5 mm (false clash); the true flat-ended cylinder gap is +5 mm.
        var a = Seg(new Vec3(0, 0, 0), new Vec3(100, 0, 0), 10, freeStart: true, freeEnd: true);
        var b = Seg(new Vec3(115, -500, 0), new Vec3(115, 500, 0), 10);
        var p = Kernel.Measure(a, b);
        Assert.Equal(ProximityMethod.FlatEndRefined, p.Method);
        Assert.Equal(5, p.SurfaceDistance, 6);
    }

    [Fact]
    public void FlatEndRefinementDoesNotHideRealEndContact()
    {
        // Bar A ends at x=100; bar B axis at x=105 so A's end face is inside B: a real collision.
        var a = Seg(new Vec3(0, 0, 0), new Vec3(100, 0, 0), 10, freeStart: true, freeEnd: true);
        var b = Seg(new Vec3(105, -500, 0), new Vec3(105, 500, 0), 10);
        var p = Kernel.Measure(a, b);
        Assert.True(p.SurfaceDistance < 0);
    }

    [Fact]
    public void RandomisedSegmentDistanceMatchesDenseSamplingAndIsSymmetric()
    {
        var rng = new Random(12345);
        for (var trial = 0; trial < 400; trial++)
        {
            var p1 = RandomPoint(rng, 200);
            var q1 = trial % 7 == 0 ? p1 + new Vec3(1e-14, 0, 0) : RandomPoint(rng, 200);
            var p2 = RandomPoint(rng, 200);
            // Every fifth trial uses exactly parallel segments, the numerically delicate case.
            var q2 = trial % 5 == 0 ? p2 + (q1 - p1) * (0.3 + rng.NextDouble()) : RandomPoint(rng, 200);
            var d = SegmentMath.ClosestPoints(p1, q1, p2, q2, out var s, out var t, out var c1, out var c2);
            var dSwap = SegmentMath.ClosestPoints(p2, q2, p1, q1, out _, out _, out _, out _);

            Assert.Equal(d, dSwap, 6);
            Assert.InRange(s, 0, 1);
            Assert.InRange(t, 0, 1);
            Assert.Equal(d, Vec3.Distance(c1, c2), 9);

            var sampled = double.MaxValue;
            const int n = 200;
            for (var i = 0; i <= n; i++)
            for (var j = 0; j <= n; j++)
                sampled = Math.Min(sampled, Vec3.Distance(Vec3.Lerp(p1, q1, i / (double)n), Vec3.Lerp(p2, q2, j / (double)n)));

            // Exact result must never exceed any sampled distance, and dense sampling must get close to it.
            Assert.True(d <= sampled + 1e-9, $"trial {trial}: exact {d} > sampled {sampled}");
            Assert.True(sampled - d < 4.0, $"trial {trial}: sampling gap {sampled - d}");
        }
    }

    [Fact]
    public void SurfaceDistanceIsInvariantUnderRigidMotion()
    {
        var rng = new Random(99);
        for (var trial = 0; trial < 200; trial++)
        {
            var a = Seg(RandomPoint(rng, 300), RandomPoint(rng, 300), 5 + rng.NextDouble() * 10, true, true);
            var b = Seg(RandomPoint(rng, 300), RandomPoint(rng, 300), 5 + rng.NextDouble() * 10, true, true);
            var before = Kernel.Measure(a, b).SurfaceDistance;

            var axis = RandomPoint(rng, 1).Normalized();
            if (axis == Vec3.Zero) axis = Vec3.UnitZ;
            var angle = rng.NextDouble() * Math.PI * 2;
            var shift = RandomPoint(rng, 5000);
            Vec3 Move(Vec3 p) => Rotate(p, axis, angle) + shift;
            var a2 = Seg(Move(a.A), Move(a.B), a.Radius, true, true);
            var b2 = Seg(Move(b.A), Move(b.B), b.Radius, true, true);
            var after = Kernel.Measure(a2, b2).SurfaceDistance;
            Assert.Equal(before, after, 6);
        }
    }

    [Fact]
    public void ParallelCylinderOverlapVolumeIsAnalytic()
    {
        var a = BarSolid.Cylinder("a", new Vec3(0, 0, 0), new Vec3(1000, 0, 0), 10);
        var b = BarSolid.Cylinder("b", new Vec3(200, 15, 0), new Vec3(1200, 15, 0), 10);
        var v = Kernel.EstimateOverlapVolume(a, b, a.Bounds);
        var expected = SegmentMath.CircleIntersectionArea(10, 10, 15) * 800;
        Assert.Equal(VolumeMethod.AnalyticParallelCylinders, v.Method);
        Assert.Equal(expected, v.CubicMillimetres, 6);
    }

    [Fact]
    public void CrossingCylinderOverlapVolumeApproximatesSteinmetzSolid()
    {
        // Two perpendicular Ø20 cylinders with intersecting axes: exact intersection volume 16r³/3.
        const double r = 10;
        var a = BarSolid.Cylinder("a", new Vec3(-100, 0, 0), new Vec3(100, 0, 0), r);
        var b = BarSolid.Cylinder("b", new Vec3(0, -100, 0), new Vec3(0, 100, 0), r);
        var region = new Aabb(new Vec3(-r, -r, -r), new Vec3(r, r, r));
        var v = new CapsuleGeometryKernel(voxelResolution: 40).EstimateOverlapVolume(a, b, region);
        var exact = 16 * r * r * r / 3;
        Assert.Equal(VolumeMethod.VoxelSampling, v.Method);
        Assert.InRange(v.CubicMillimetres, exact * 0.97, exact * 1.03);
    }

    [Fact]
    public void LensAreaLimits()
    {
        Assert.Equal(0, SegmentMath.CircleIntersectionArea(10, 10, 20), 12);
        Assert.Equal(Math.PI * 25, SegmentMath.CircleIntersectionArea(10, 5, 2), 9);
        Assert.Equal(Math.PI * 100, SegmentMath.CircleIntersectionArea(10, 10, 0), 9);
    }

    [Fact]
    public void SegmentToBoxDistanceIsExactForAxisAlignedCase()
    {
        var box = OrientedBox.FromMinMax(new Vec3(0, 0, 0), new Vec3(100, 100, 100));
        var d = SegmentMath.SegmentBoxDistance(new Vec3(-50, 150, 50), new Vec3(150, 150, 50), box, out _);
        Assert.Equal(50, d, 6);
        var through = SegmentMath.SegmentBoxDistance(new Vec3(-50, 50, 50), new Vec3(150, 50, 50), box, out _);
        Assert.Equal(0, through, 9);
    }

    internal static Vec3 RandomPoint(Random rng, double scale) =>
        new((rng.NextDouble() * 2 - 1) * scale, (rng.NextDouble() * 2 - 1) * scale, (rng.NextDouble() * 2 - 1) * scale);

    private static Vec3 Rotate(Vec3 v, Vec3 k, double theta)
    {
        // Rodrigues' rotation formula.
        var c = Math.Cos(theta);
        var s = Math.Sin(theta);
        return v * c + Vec3.Cross(k, v) * s + k * (Vec3.Dot(k, v) * (1 - c));
    }
}
