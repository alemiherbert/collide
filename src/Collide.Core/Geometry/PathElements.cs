namespace Collide.Geometry;

/// <summary>Role of a centreline element within a bar shape.</summary>
public enum PathElementKind
{
    Straight,
    Bend,
    Hook,
    HookExtension,
}

/// <summary>An immutable centreline element of a bar path.</summary>
public abstract class PathElement
{
    protected PathElement(PathElementKind kind) => Kind = kind;

    public PathElementKind Kind { get; }
    public abstract Vec3 Start { get; }
    public abstract Vec3 End { get; }
    public abstract double Length { get; }
    public abstract Vec3 StartTangent { get; }
    public abstract Vec3 EndTangent { get; }
    public abstract Aabb Bounds { get; }

    public abstract PathElement Translate(Vec3 offset);

    /// <summary>Appends tessellation points (excluding the start point) and returns the max chord deviation.</summary>
    internal abstract double AppendTessellation(double chordTolerance, List<Vec3> points);

    internal abstract bool IsWellFormed(out string? problem);
}

public sealed class LineElement : PathElement
{
    public LineElement(Vec3 start, Vec3 end, PathElementKind kind = PathElementKind.Straight) : base(kind)
    {
        StartPoint = start;
        EndPoint = end;
    }

    public Vec3 StartPoint { get; }
    public Vec3 EndPoint { get; }

    public override Vec3 Start => StartPoint;
    public override Vec3 End => EndPoint;
    public override double Length => Vec3.Distance(StartPoint, EndPoint);
    public Vec3 Direction => (EndPoint - StartPoint).Normalized();
    public override Vec3 StartTangent => Direction;
    public override Vec3 EndTangent => Direction;
    public override Aabb Bounds => Aabb.FromPoints(StartPoint, EndPoint);

    public override PathElement Translate(Vec3 offset) => new LineElement(StartPoint + offset, EndPoint + offset, Kind);

    internal override double AppendTessellation(double chordTolerance, List<Vec3> points)
    {
        points.Add(EndPoint);
        return 0;
    }

    internal override bool IsWellFormed(out string? problem)
    {
        problem = !StartPoint.IsFinite || !EndPoint.IsFinite ? "Line has non-finite coordinates."
            : Length < 1e-6 ? "Line has zero length."
            : null;
        return problem is null;
    }
}

/// <summary>
/// Circular arc: P(φ) = Center + Radius·(cos φ·U + sin φ·V), φ ∈ [0, Sweep].
/// Maps directly from a Revit Arc: U = cos t0·X + sin t0·Y, V = −sin t0·X + cos t0·Y, Sweep = t1 − t0.
/// </summary>
public sealed class ArcElement : PathElement
{
    public ArcElement(Vec3 center, double radius, Vec3 u, Vec3 v, double sweep, PathElementKind kind = PathElementKind.Bend) : base(kind)
    {
        Center = center;
        Radius = radius;
        U = u.Normalized();
        V = (v - U * Vec3.Dot(v, U)).Normalized();
        Sweep = sweep;
    }

    public Vec3 Center { get; }
    public double Radius { get; }
    public Vec3 U { get; }
    public Vec3 V { get; }
    public double Sweep { get; }

    public Vec3 Normal => Vec3.Cross(U, V);

    /// <summary>
    /// Arc from its centre, start and end points, travelling counter-clockwise about <paramref name="normal"/>
    /// (for a Revit Arc traversed from end point 0 to 1 this is <c>Arc.Normal</c>; when the chain traverses it reversed,
    /// pass the negated normal). A start equal to the end gives a full circle.
    /// </summary>
    public static ArcElement FromCenterStartEnd(Vec3 center, Vec3 start, Vec3 end, Vec3 normal, PathElementKind kind = PathElementKind.Bend)
    {
        var radius = Vec3.Distance(center, start);
        var u = (start - center).Normalized();
        var n = normal.Normalized();
        var v = Vec3.Cross(n, u).Normalized();
        var d = end - center;
        var sweep = Math.Atan2(Vec3.Dot(d, v), Vec3.Dot(d, u));
        if (sweep <= 1e-9) sweep += 2 * Math.PI;
        return new ArcElement(center, radius, u, v, sweep, kind);
    }

    public Vec3 PointAt(double phi) => Center + (U * Math.Cos(phi) + V * Math.Sin(phi)) * Radius;
    public Vec3 TangentAt(double phi) => (V * Math.Cos(phi) - U * Math.Sin(phi)).Normalized();

    public override Vec3 Start => PointAt(0);
    public override Vec3 End => PointAt(Sweep);
    public override double Length => Radius * Sweep;
    public override Vec3 StartTangent => TangentAt(0);
    public override Vec3 EndTangent => TangentAt(Sweep);

    public override Aabb Bounds
    {
        get
        {
            var box = Aabb.FromPoints(Start, End);
            for (var axis = 0; axis < 3; axis++)
            {
                // Extremes of Radius·(cos φ·U[axis] + sin φ·V[axis]) occur at φ = atan2(V, U) and φ + π.
                var phi = Math.Atan2(V[axis], U[axis]);
                foreach (var candidate in new[] { phi, phi + Math.PI, phi - Math.PI, phi + 2 * Math.PI })
                {
                    if (candidate > 0 && candidate < Sweep) box = box.Include(PointAt(candidate));
                }
            }

            return box;
        }
    }

    public override PathElement Translate(Vec3 offset) => new ArcElement(Center + offset, Radius, U, V, Sweep, Kind);

    /// <summary>Arc parameter φ ∈ [0, 2π) of a point on (or near) the arc.</summary>
    public double ParameterOf(Vec3 p)
    {
        var d = p - Center;
        var phi = Math.Atan2(Vec3.Dot(d, V), Vec3.Dot(d, U));
        return phi < -1e-12 ? phi + 2 * Math.PI : Math.Max(0, phi);
    }

    /// <summary>
    /// Exact maximum of the plane's signed distance over the sub-arc φ ∈ [phi0, phi1]:
    /// sd(φ) = sd(centre) + A·cos φ + B·sin φ with A = R·(n·U), B = R·(n·V).
    /// </summary>
    public double MaxSignedDistance(Plane plane, double phi0, double phi1, out double atPhi)
    {
        var a = Radius * Vec3.Dot(plane.Normal, U);
        var b = Radius * Vec3.Dot(plane.Normal, V);
        var c = plane.SignedDistance(Center);
        double Sd(double phi) => c + a * Math.Cos(phi) + b * Math.Sin(phi);
        atPhi = phi0;
        var best = Sd(phi0);
        if (Sd(phi1) > best)
        {
            best = Sd(phi1);
            atPhi = phi1;
        }

        var peak = Math.Atan2(b, a);
        foreach (var candidate in new[] { peak, peak + 2 * Math.PI, peak - 2 * Math.PI })
        {
            if (candidate > phi0 && candidate < phi1 && Sd(candidate) > best)
            {
                best = Sd(candidate);
                atPhi = candidate;
            }
        }

        return best;
    }

    /// <summary>Number of equal chords needed so the sagitta does not exceed <paramref name="chordTolerance"/>.</summary>
    public int ChordCount(double chordTolerance)
    {
        if (chordTolerance <= 0) throw new ArgumentOutOfRangeException(nameof(chordTolerance));
        if (chordTolerance >= Radius) return Math.Max(1, (int)Math.Ceiling(Sweep / (Math.PI / 2)));
        var maxHalfAngle = Math.Acos(1 - chordTolerance / Radius);
        return Math.Max(1, (int)Math.Ceiling(Sweep / (2 * maxHalfAngle)));
    }

    public double ChordError(int chords) => Radius * (1 - Math.Cos(Sweep / (2.0 * chords)));

    internal override double AppendTessellation(double chordTolerance, List<Vec3> points)
    {
        var n = ChordCount(chordTolerance);
        for (var i = 1; i <= n; i++) points.Add(PointAt(Sweep * i / n));
        return ChordError(n);
    }

    internal override bool IsWellFormed(out string? problem)
    {
        problem = !Center.IsFinite || !double.IsFinite(Radius) ? "Arc has non-finite geometry."
            : Radius <= 1e-6 ? "Arc radius must be positive."
            : Sweep <= 1e-9 || Sweep > 2 * Math.PI + 1e-9 ? "Arc sweep must be in (0, 2π]."
            : U == Vec3.Zero || V == Vec3.Zero ? "Arc basis vectors are degenerate."
            : null;
        return problem is null;
    }
}
