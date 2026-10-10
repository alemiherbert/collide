using System.Globalization;
using System.Text.Json.Serialization;

namespace Collide.Geometry;

/// <summary>
/// A 3D vector/point. Coordinates are millimetres in the model's world frame.
/// Kept as raw doubles for performance inside the geometry kernel; strongly typed
/// <see cref="Units.Length"/> values are used at model, rule and report boundaries.
/// </summary>
public readonly struct Vec3 : IEquatable<Vec3>
{
    [JsonConstructor]
    public Vec3(double x, double y, double z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public double X { get; }
    public double Y { get; }
    public double Z { get; }

    public static Vec3 Zero => default;
    public static Vec3 UnitX => new(1, 0, 0);
    public static Vec3 UnitY => new(0, 1, 0);
    public static Vec3 UnitZ => new(0, 0, 1);

    public double this[int axis] => axis switch
    {
        0 => X,
        1 => Y,
        2 => Z,
        _ => throw new ArgumentOutOfRangeException(nameof(axis)),
    };

    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vec3 operator -(Vec3 a) => new(-a.X, -a.Y, -a.Z);
    public static Vec3 operator *(Vec3 a, double k) => new(a.X * k, a.Y * k, a.Z * k);
    public static Vec3 operator *(double k, Vec3 a) => new(a.X * k, a.Y * k, a.Z * k);
    public static Vec3 operator /(Vec3 a, double k) => new(a.X / k, a.Y / k, a.Z / k);

    public static double Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    public static Vec3 Cross(Vec3 a, Vec3 b) =>
        new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    public double LengthSquared => X * X + Y * Y + Z * Z;
    public double Length => Math.Sqrt(LengthSquared);

    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Z);

    /// <summary>Unit vector in the same direction, or <see cref="Zero"/> if the vector is degenerate.</summary>
    public Vec3 Normalized()
    {
        var l = Length;
        return l > 1e-12 ? this / l : Zero;
    }

    public static double Distance(Vec3 a, Vec3 b) => (a - b).Length;
    public static double DistanceSquared(Vec3 a, Vec3 b) => (a - b).LengthSquared;

    public static Vec3 Min(Vec3 a, Vec3 b) => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z));
    public static Vec3 Max(Vec3 a, Vec3 b) => new(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z));
    public static Vec3 Lerp(Vec3 a, Vec3 b, double t) => a + (b - a) * t;

    /// <summary>Any unit vector perpendicular to this (non-zero) vector.</summary>
    public Vec3 AnyPerpendicular()
    {
        var n = Normalized();
        var helper = Math.Abs(n.X) < 0.9 ? UnitX : UnitY;
        return Cross(n, helper).Normalized();
    }

    public bool ApproximatelyEquals(Vec3 other, double tolerance) => DistanceSquared(this, other) <= tolerance * tolerance;

    public bool Equals(Vec3 other) => X.Equals(other.X) && Y.Equals(other.Y) && Z.Equals(other.Z);
    public override bool Equals(object? obj) => obj is Vec3 v && Equals(v);
    public override int GetHashCode() => HashCode.Combine(X, Y, Z);
    public static bool operator ==(Vec3 a, Vec3 b) => a.Equals(b);
    public static bool operator !=(Vec3 a, Vec3 b) => !a.Equals(b);

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"({X:0.###}, {Y:0.###}, {Z:0.###})");
}
