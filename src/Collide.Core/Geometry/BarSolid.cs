namespace Collide.Geometry;

/// <summary>
/// One primitive of a bar's physical solid: a segment of the centreline swept by the bar radius.
/// Free bar ends are flat discs; joints between primitives are spherical (exact for a smooth tube).
/// </summary>
public readonly struct SolidSegment
{
    public SolidSegment(Vec3 a, Vec3 b, double radius, int elementIndex, PathElementKind kind, double chordError, bool freeStart, bool freeEnd)
    {
        A = a;
        B = b;
        Radius = radius;
        ElementIndex = elementIndex;
        Kind = kind;
        ChordError = chordError;
        FreeStart = freeStart;
        FreeEnd = freeEnd;
        Bounds = Aabb.FromPoints(a, b).Inflate(radius + chordError);
    }

    public Vec3 A { get; }
    public Vec3 B { get; }
    public double Radius { get; }
    public int ElementIndex { get; }
    public PathElementKind Kind { get; }

    /// <summary>Maximum deviation of this chord from the true arc (0 for straight elements).</summary>
    public double ChordError { get; }

    public bool FreeStart { get; }
    public bool FreeEnd { get; }
    public Aabb Bounds { get; }
    public double Length => Vec3.Distance(A, B);
    public Vec3 Direction => (B - A).Normalized();
}

/// <summary>The physical solid of a bar or accessory, ready for proximity queries.</summary>
public sealed class BarSolid
{
    public BarSolid(string id, double radius, IReadOnlyList<SolidSegment> segments)
    {
        if (segments.Count == 0) throw new ArgumentException("A solid needs at least one segment.", nameof(segments));
        Id = id;
        Radius = radius;
        Segments = segments;
        var box = Aabb.Empty;
        var maxError = 0.0;
        foreach (var s in segments)
        {
            box = box.Union(s.Bounds);
            maxError = Math.Max(maxError, s.ChordError);
        }

        Bounds = box;
        MaxChordError = maxError;
    }

    public string Id { get; }
    public double Radius { get; }
    public IReadOnlyList<SolidSegment> Segments { get; }
    public Aabb Bounds { get; }
    public double MaxChordError { get; }

    public bool ContainsPoint(Vec3 p) => Segments.Any(s => SegmentMath.PointToBarSegment(p, s.A, s.B, s.Radius, s.FreeStart, s.FreeEnd) <= 0);

    public static BarSolid FromPath(string id, BarPath path, double radius, double chordTolerance)
    {
        var tess = path.Tessellate(chordTolerance);
        var closed = path.IsClosed();
        var segs = new SolidSegment[tess.Count];
        for (var i = 0; i < tess.Count; i++)
        {
            var t = tess[i];
            segs[i] = new SolidSegment(t.A, t.B, radius, t.ElementIndex, t.Kind, t.ChordError,
                freeStart: !closed && i == 0, freeEnd: !closed && i == tess.Count - 1);
        }

        return new BarSolid(id, radius, segs);
    }

    /// <summary>A cylinder (e.g. a coupler) with flat ends.</summary>
    public static BarSolid Cylinder(string id, Vec3 a, Vec3 b, double radius) =>
        new(id, radius, [new SolidSegment(a, b, radius, 0, PathElementKind.Straight, 0, true, true)]);
}
