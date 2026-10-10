using Collide.Geometry;

namespace Collide.Core.Tests.Geometry;

public class PathTests
{
    [Fact]
    public void FilletedPolylineIsTangentContinuousAndValid()
    {
        var path = PathFactory.Polyline([new Vec3(0, 0, 0), new Vec3(500, 0, 0), new Vec3(500, 400, 0)], bendRadius: 40);
        Assert.Empty(path.Validate(1e-6));
        Assert.Equal(3, path.Elements.Count);
        var arc = Assert.IsType<ArcElement>(path.Elements[1]);
        Assert.Equal(Math.PI / 2, arc.Sweep, 9);
        // Tangent continuity at both arc ends.
        Assert.True(Vec3.Dot(path.Elements[0].EndTangent, arc.StartTangent) > 1 - 1e-9);
        Assert.True(Vec3.Dot(arc.EndTangent, path.Elements[2].StartTangent) > 1 - 1e-9);
        // Length: two legs shortened by R·tan(45°)=40 each plus the quarter arc.
        Assert.Equal(460 + 360 + Math.PI / 2 * 40, path.Length, 9);
    }

    [Fact]
    public void ArcTessellationRespectsChordTolerance()
    {
        var arc = new ArcElement(Vec3.Zero, 100, Vec3.UnitX, Vec3.UnitY, Math.PI);
        foreach (var tol in new[] { 1.0, 0.1, 0.01 })
        {
            var n = arc.ChordCount(tol);
            Assert.True(arc.ChordError(n) <= tol + 1e-12);
            Assert.True(n == 1 || arc.ChordError(n - 1) > tol, "chord count should be minimal");
        }
    }

    [Fact]
    public void ArcBoundsMatchDenseSampling()
    {
        var rng = new Random(7);
        for (var trial = 0; trial < 100; trial++)
        {
            var u = KernelTests.RandomPoint(rng, 1).Normalized();
            var v = u.AnyPerpendicular();
            var arc = new ArcElement(KernelTests.RandomPoint(rng, 100), 10 + rng.NextDouble() * 90, u, v, 0.1 + rng.NextDouble() * (2 * Math.PI - 0.1));
            var sampled = Aabb.Empty;
            for (var i = 0; i <= 5000; i++) sampled = sampled.Include(arc.PointAt(arc.Sweep * i / 5000));
            var exact = arc.Bounds;
            Assert.True(exact.Min.ApproximatelyEquals(sampled.Min, 0.01) && exact.Max.ApproximatelyEquals(sampled.Max, 0.01),
                $"trial {trial}: {exact} vs {sampled}");
        }
    }

    [Fact]
    public void ClosedStirrupHasNoFreeEnds()
    {
        var loop = PathFactory.RectangularLoop(new Vec3(0, 0, 0), Vec3.UnitY, Vec3.UnitZ, 200, 400, 20);
        Assert.True(loop.IsClosed());
        var solid = BarSolid.FromPath("s", loop, 5, 0.1);
        Assert.DoesNotContain(solid.Segments, s => s.FreeStart || s.FreeEnd);
        Assert.Equal(1.0, Math.Abs(Vec3.Dot(loop.PrimaryDirection, Vec3.UnitX)), 9);
    }

    [Fact]
    public void GeometryHashIsStableAndSensitiveToMovement()
    {
        var a = PathFactory.Straight(new Vec3(0, 0, 0), new Vec3(1000, 0, 0));
        var b = PathFactory.Straight(new Vec3(0, 0, 0), new Vec3(1000, 0, 0));
        Assert.Equal(a.GeometryHash(20), b.GeometryHash(20));
        Assert.NotEqual(a.GeometryHash(20), a.Translate(new Vec3(0, 0.001, 0)).GeometryHash(20));
        Assert.NotEqual(a.GeometryHash(20), a.GeometryHash(16));
    }

    [Fact]
    public void ValidationReportsGapsAndDegenerateElements()
    {
        var broken = new BarPath([
            new LineElement(new Vec3(0, 0, 0), new Vec3(100, 0, 0)),
            new LineElement(new Vec3(105, 0, 0), new Vec3(200, 0, 0)),
            new LineElement(new Vec3(200, 0, 0), new Vec3(200, 0, 0)),
        ]);
        var problems = broken.Validate(0.01);
        Assert.Contains(problems, p => p.Contains("Gap"));
        Assert.Contains(problems, p => p.Contains("zero length"));
    }

    [Fact]
    public void ConvexRegionClipsSegmentsExactly()
    {
        var region = ConvexRegion.FromBox(OrientedBox.FromMinMax(new Vec3(0, 0, 0), new Vec3(100, 50, 20)));
        Assert.True(region.ClipSegment(new Vec3(-100, 25, 10), new Vec3(200, 25, 10), out var t0, out var t1));
        Assert.Equal(100.0 / 300, t0, 12);
        Assert.Equal(200.0 / 300, t1, 12);
        Assert.False(region.ClipSegment(new Vec3(-100, 80, 10), new Vec3(200, 80, 10), out _, out _));
        Assert.Equal(-10, region.SignedDistance(new Vec3(50, 25, 10)), 12);
        Assert.Contains(region.Faces, f => f.Tag == FaceTag.Top);
        Assert.Contains(region.Faces, f => f.Tag == FaceTag.Bottom);
        Assert.True(region.IsConsistent(1e-9));
    }
}
