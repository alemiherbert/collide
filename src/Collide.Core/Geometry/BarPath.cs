using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Collide.Geometry;

/// <summary>One straight piece of a tessellated centreline.</summary>
public readonly record struct TessellatedSegment(Vec3 A, Vec3 B, int ElementIndex, PathElementKind Kind, double ChordError);

/// <summary>An immutable bar centreline: an ordered chain of lines and arcs (bends, hooks).</summary>
public sealed class BarPath
{
    private readonly PathElement[] _elements;

    public BarPath(IEnumerable<PathElement> elements)
    {
        _elements = elements.ToArray();
        if (_elements.Length == 0) throw new ArgumentException("A bar path needs at least one element.", nameof(elements));
    }

    public IReadOnlyList<PathElement> Elements => _elements;

    public Vec3 Start => _elements[0].Start;
    public Vec3 End => _elements[^1].End;
    public double Length => _elements.Sum(e => e.Length);

    public Aabb Bounds
    {
        get
        {
            var box = Aabb.Empty;
            foreach (var e in _elements) box = box.Union(e.Bounds);
            return box;
        }
    }

    public bool IsClosed(double tolerance = 1e-3) => _elements.Length > 1 && Start.ApproximatelyEquals(End, tolerance);

    /// <summary>True when every element is a line and all are collinear within the angle tolerance.</summary>
    public bool IsStraight(double angleToleranceRadians = 1e-3)
    {
        if (_elements.Any(e => e is not LineElement)) return false;
        var dir = (End - Start).Normalized();
        if (dir == Vec3.Zero) return false;
        var cos = Math.Cos(angleToleranceRadians);
        return _elements.Cast<LineElement>().All(l => Vec3.Dot(l.Direction, dir) >= cos);
    }

    /// <summary>
    /// Direction used for movement constraints: the bar axis for straight bars, the plane normal for planar
    /// shapes (stirrups, ties), otherwise the start-to-end chord.
    /// </summary>
    public Vec3 PrimaryDirection
    {
        get
        {
            if (IsStraight()) return (End - Start).Normalized();
            var planeNormal = PlanarNormal();
            if (planeNormal != Vec3.Zero) return planeNormal;
            var chord = (End - Start).Normalized();
            return chord != Vec3.Zero ? chord : _elements[0].StartTangent;
        }
    }

    /// <summary>Normal of the plane containing the whole path, or zero if the path is not planar.</summary>
    public Vec3 PlanarNormal(double tolerance = 0.5)
    {
        var pts = Tessellate(0.5).SelectMany(s => new[] { s.A, s.B }).ToArray();
        var origin = pts[0];
        Vec3 normal = Vec3.Zero;
        for (var i = 1; i < pts.Length && normal == Vec3.Zero; i++)
        {
            for (var j = i + 1; j < pts.Length; j++)
            {
                var n = Vec3.Cross(pts[i] - origin, pts[j] - origin);
                if (n.Length > 1e-3)
                {
                    normal = n.Normalized();
                    break;
                }
            }
        }

        if (normal == Vec3.Zero) return Vec3.Zero;
        return pts.All(p => Math.Abs(Vec3.Dot(p - origin, normal)) <= tolerance) ? normal : Vec3.Zero;
    }

    public BarPath Translate(Vec3 offset) => new(_elements.Select(e => e.Translate(offset)));

    /// <summary>
    /// Splits the path into straight segments. Arcs are approximated by equal chords whose sagitta is at most
    /// <paramref name="chordTolerance"/>; the actual chord error of each segment is reported.
    /// </summary>
    public IReadOnlyList<TessellatedSegment> Tessellate(double chordTolerance)
    {
        var result = new List<TessellatedSegment>();
        var points = new List<Vec3>(8);
        for (var i = 0; i < _elements.Length; i++)
        {
            var e = _elements[i];
            points.Clear();
            points.Add(e.Start);
            var error = e.AppendTessellation(chordTolerance, points);
            for (var k = 1; k < points.Count; k++)
            {
                if (Vec3.DistanceSquared(points[k - 1], points[k]) < 1e-18) continue;
                result.Add(new TessellatedSegment(points[k - 1], points[k], i, e.Kind, error));
            }
        }

        return result;
    }

    /// <summary>Validates element well-formedness and chain continuity.</summary>
    public IReadOnlyList<string> Validate(double continuityTolerance)
    {
        var problems = new List<string>();
        for (var i = 0; i < _elements.Length; i++)
        {
            if (!_elements[i].IsWellFormed(out var problem)) problems.Add($"Element {i}: {problem}");
            if (i > 0)
            {
                var gap = Vec3.Distance(_elements[i - 1].End, _elements[i].Start);
                if (gap > continuityTolerance)
                    problems.Add(string.Create(CultureInfo.InvariantCulture, $"Gap of {gap:0.###} mm between elements {i - 1} and {i}."));
            }
        }

        return problems;
    }

    /// <summary>
    /// Canonical geometry hash. Coordinates are quantised to 1e-6 mm so that hashes are stable across runs
    /// and platforms; used to detect stale preconditions and verify rollback.
    /// </summary>
    public string GeometryHash(double physicalDiameter)
    {
        var sb = new StringBuilder();
        sb.Append(Q(physicalDiameter)).Append('|');
        foreach (var e in _elements)
        {
            switch (e)
            {
                case LineElement l:
                    sb.Append("L:").Append((int)l.Kind).Append(':').Append(Q(l.StartPoint)).Append(':').Append(Q(l.EndPoint)).Append(';');
                    break;
                case ArcElement a:
                    sb.Append("A:").Append((int)a.Kind).Append(':').Append(Q(a.Center)).Append(':').Append(Q(a.Radius)).Append(':')
                        .Append(Q(a.U * 1000)).Append(':').Append(Q(a.V * 1000)).Append(':').Append(Q(a.Sweep * 1000)).Append(';');
                    break;
            }
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16];
    }

    private static string Q(double v) => Math.Round(v * 1e6).ToString("0", CultureInfo.InvariantCulture);
    private static string Q(Vec3 v) => $"{Q(v.X)},{Q(v.Y)},{Q(v.Z)}";
}
