namespace Collide.Geometry;

/// <summary>Plane with unit normal: points p satisfy Normal·p = Offset. Signed distance is positive on the normal side.</summary>
public readonly struct Plane
{
    public Plane(Vec3 normal, double offset)
    {
        var n = normal.Normalized();
        if (n == Vec3.Zero) throw new ArgumentException("Plane normal must be non-zero.", nameof(normal));
        var scale = normal.Length;
        Normal = n;
        Offset = offset / scale;
    }

    public Vec3 Normal { get; }
    public double Offset { get; }

    public static Plane FromPointNormal(Vec3 point, Vec3 normal)
    {
        var n = normal.Normalized();
        return new Plane(n, Vec3.Dot(n, point));
    }

    public double SignedDistance(Vec3 p) => Vec3.Dot(Normal, p) - Offset;

    public Vec3 Project(Vec3 p) => p - Normal * SignedDistance(p);

    public Plane Translate(Vec3 offset) => new(Normal, Offset + Vec3.Dot(Normal, offset));
}
