namespace Collide.Geometry;

/// <summary>Axis-aligned bounding box. Used only for broad-phase filtering, never as proof of a clash.</summary>
public readonly struct Aabb
{
    public Aabb(Vec3 min, Vec3 max)
    {
        Min = min;
        Max = max;
    }

    public Vec3 Min { get; }
    public Vec3 Max { get; }

    public static Aabb Empty => new(
        new Vec3(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity),
        new Vec3(double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity));

    public bool IsEmpty => Min.X > Max.X || Min.Y > Max.Y || Min.Z > Max.Z;

    public Vec3 Center => (Min + Max) * 0.5;
    public Vec3 Size => IsEmpty ? Vec3.Zero : Max - Min;

    public static Aabb FromPoints(Vec3 a, Vec3 b) => new(Vec3.Min(a, b), Vec3.Max(a, b));

    public static Aabb FromPoints(IEnumerable<Vec3> points)
    {
        var box = Empty;
        foreach (var p in points) box = box.Include(p);
        return box;
    }

    public Aabb Include(Vec3 p) => new(Vec3.Min(Min, p), Vec3.Max(Max, p));

    public Aabb Union(Aabb other)
    {
        if (IsEmpty) return other;
        if (other.IsEmpty) return this;
        return new Aabb(Vec3.Min(Min, other.Min), Vec3.Max(Max, other.Max));
    }

    public Aabb Intersection(Aabb other) => new(Vec3.Max(Min, other.Min), Vec3.Min(Max, other.Max));

    public Aabb Inflate(double amount)
    {
        if (IsEmpty) return this;
        var d = new Vec3(amount, amount, amount);
        return new Aabb(Min - d, Max + d);
    }

    public bool Intersects(in Aabb other) =>
        Min.X <= other.Max.X && Max.X >= other.Min.X &&
        Min.Y <= other.Max.Y && Max.Y >= other.Min.Y &&
        Min.Z <= other.Max.Z && Max.Z >= other.Min.Z;

    public bool Contains(Vec3 p, double tolerance = 0) =>
        p.X >= Min.X - tolerance && p.X <= Max.X + tolerance &&
        p.Y >= Min.Y - tolerance && p.Y <= Max.Y + tolerance &&
        p.Z >= Min.Z - tolerance && p.Z <= Max.Z + tolerance;

    public double SurfaceArea
    {
        get
        {
            if (IsEmpty) return 0;
            var s = Size;
            return 2 * (s.X * s.Y + s.Y * s.Z + s.Z * s.X);
        }
    }

    public int LongestAxis
    {
        get
        {
            var s = Size;
            return s.X >= s.Y && s.X >= s.Z ? 0 : s.Y >= s.Z ? 1 : 2;
        }
    }

    public override string ToString() => $"[{Min} .. {Max}]";
}
