namespace Collide.Geometry;

/// <summary>
/// Oriented box (centre, orthonormal axes, half extents). Used for openings, recesses, zones and as a
/// conservative stand-in for non-convex hosts when exact geometry is unavailable.
/// </summary>
public sealed class OrientedBox
{
    private const double OrthoTolerance = 1e-6;

    public OrientedBox(Vec3 center, Vec3 axisX, Vec3 axisY, Vec3 axisZ, Vec3 halfExtents)
    {
        var x = axisX.Normalized();
        var y = axisY.Normalized();
        var z = axisZ.Normalized();
        if (x == Vec3.Zero || y == Vec3.Zero || z == Vec3.Zero)
            throw new ArgumentException("Box axes must be non-zero.");
        if (Math.Abs(Vec3.Dot(x, y)) > OrthoTolerance || Math.Abs(Vec3.Dot(y, z)) > OrthoTolerance || Math.Abs(Vec3.Dot(x, z)) > OrthoTolerance)
            throw new ArgumentException("Box axes must be orthogonal.");
        if (halfExtents.X < 0 || halfExtents.Y < 0 || halfExtents.Z < 0 || !halfExtents.IsFinite)
            throw new ArgumentException("Box half extents must be finite and non-negative.");
        Center = center;
        AxisX = x;
        AxisY = y;
        AxisZ = z;
        HalfExtents = halfExtents;
    }

    public Vec3 Center { get; }
    public Vec3 AxisX { get; }
    public Vec3 AxisY { get; }
    public Vec3 AxisZ { get; }
    public Vec3 HalfExtents { get; }

    public static OrientedBox FromMinMax(Vec3 min, Vec3 max) =>
        new((min + max) * 0.5, Vec3.UnitX, Vec3.UnitY, Vec3.UnitZ, (max - min) * 0.5);

    public Vec3 Axis(int i) => i switch { 0 => AxisX, 1 => AxisY, 2 => AxisZ, _ => throw new ArgumentOutOfRangeException(nameof(i)) };

    public Vec3 ToLocal(Vec3 p)
    {
        var d = p - Center;
        return new Vec3(Vec3.Dot(d, AxisX), Vec3.Dot(d, AxisY), Vec3.Dot(d, AxisZ));
    }

    public Vec3 ToWorld(Vec3 local) => Center + AxisX * local.X + AxisY * local.Y + AxisZ * local.Z;

    public bool Contains(Vec3 p, double tolerance = 0)
    {
        var l = ToLocal(p);
        return Math.Abs(l.X) <= HalfExtents.X + tolerance &&
               Math.Abs(l.Y) <= HalfExtents.Y + tolerance &&
               Math.Abs(l.Z) <= HalfExtents.Z + tolerance;
    }

    /// <summary>Euclidean distance from a point to the solid box (0 inside).</summary>
    public double DistanceTo(Vec3 p)
    {
        var l = ToLocal(p);
        var dx = Math.Max(0, Math.Abs(l.X) - HalfExtents.X);
        var dy = Math.Max(0, Math.Abs(l.Y) - HalfExtents.Y);
        var dz = Math.Max(0, Math.Abs(l.Z) - HalfExtents.Z);
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    public Vec3 ClosestPoint(Vec3 p)
    {
        var l = ToLocal(p);
        return ToWorld(new Vec3(
            Math.Clamp(l.X, -HalfExtents.X, HalfExtents.X),
            Math.Clamp(l.Y, -HalfExtents.Y, HalfExtents.Y),
            Math.Clamp(l.Z, -HalfExtents.Z, HalfExtents.Z)));
    }

    public IEnumerable<Vec3> Corners()
    {
        for (var i = -1; i <= 1; i += 2)
        for (var j = -1; j <= 1; j += 2)
        for (var k = -1; k <= 1; k += 2)
            yield return ToWorld(new Vec3(i * HalfExtents.X, j * HalfExtents.Y, k * HalfExtents.Z));
    }

    public Aabb Bounds => Aabb.FromPoints(Corners());

    public OrientedBox Translate(Vec3 offset) => new(Center + offset, AxisX, AxisY, AxisZ, HalfExtents);

    public OrientedBox Inflate(double amount) =>
        new(Center, AxisX, AxisY, AxisZ, HalfExtents + new Vec3(amount, amount, amount));

    /// <summary>The six bounding planes with outward normals, ordered +X, -X, +Y, -Y, +Z, -Z.</summary>
    public IReadOnlyList<Plane> Planes()
    {
        var planes = new Plane[6];
        for (var axis = 0; axis < 3; axis++)
        {
            var a = Axis(axis);
            var h = HalfExtents[axis];
            planes[axis * 2] = Plane.FromPointNormal(Center + a * h, a);
            planes[axis * 2 + 1] = Plane.FromPointNormal(Center - a * h, -a);
        }

        return planes;
    }
}
