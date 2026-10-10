namespace Collide.Geometry;

/// <summary>Semantic tag of a concrete face, used to select the applicable cover requirement.</summary>
public enum FaceTag
{
    Other,
    Top,
    Bottom,
    Side,
    End,
    Exterior,
    Interior,
    Opening,
}

/// <summary>
/// Whether a face is an exposed concrete surface (cover applies) or concealed by an adjoining element.
/// In Revit, <c>RebarHostData.GetCoverType(face)</c> returns null for concealed faces.
/// </summary>
public enum FaceExposure
{
    /// <summary>Unknown: the engine probes adjoining hosts at the measurement location.</summary>
    Unknown,
    Exposed,
    Concealed,
}

public sealed record RegionFace(Plane Plane, FaceTag Tag, string FaceId, FaceExposure Exposure = FaceExposure.Unknown);

/// <summary>
/// A convex solid described as the intersection of half-spaces (outward face normals).
/// Containment, clipping and interior distance-to-boundary are exact for convex solids.
/// </summary>
public sealed class ConvexRegion
{
    public ConvexRegion(IReadOnlyList<RegionFace> faces, IReadOnlyList<Vec3> vertices)
    {
        if (faces.Count < 4) throw new ArgumentException("A closed convex region needs at least four faces.", nameof(faces));
        if (vertices.Count < 4) throw new ArgumentException("A closed convex region needs at least four vertices.", nameof(vertices));
        Faces = faces.ToArray();
        Vertices = vertices.ToArray();
        Bounds = Aabb.FromPoints(Vertices);
    }

    public IReadOnlyList<RegionFace> Faces { get; }
    public IReadOnlyList<Vec3> Vertices { get; }
    public Aabb Bounds { get; }

    /// <summary>
    /// Creates a region from a box. Face tags default to Top/Bottom for faces whose normal is within 45° of ±Z
    /// and Side otherwise; supply <paramref name="tagSelector"/> for wall exterior/interior or member ends.
    /// </summary>
    public static ConvexRegion FromBox(OrientedBox box, Func<int, Vec3, FaceTag>? tagSelector = null, Func<int, FaceExposure>? exposure = null)
    {
        var planes = box.Planes();
        var faces = new RegionFace[6];
        string[] ids = ["+X", "-X", "+Y", "-Y", "+Z", "-Z"];
        for (var i = 0; i < 6; i++)
        {
            var n = planes[i].Normal;
            var tag = tagSelector?.Invoke(i, n) ?? DefaultTag(n);
            faces[i] = new RegionFace(planes[i], tag, ids[i], exposure?.Invoke(i) ?? FaceExposure.Unknown);
        }

        return new ConvexRegion(faces, box.Corners().ToArray());
    }

    public static FaceTag DefaultTag(Vec3 outwardNormal) =>
        outwardNormal.Z > 0.7071 ? FaceTag.Top : outwardNormal.Z < -0.7071 ? FaceTag.Bottom : FaceTag.Side;

    /// <summary>
    /// Max over face planes of the signed plane distance. Negative inside: its magnitude is then the exact
    /// distance to the boundary. Positive outside: a lower bound of the true exterior distance.
    /// </summary>
    public double SignedDistance(Vec3 p)
    {
        var max = double.NegativeInfinity;
        foreach (var f in Faces)
        {
            var d = f.Plane.SignedDistance(p);
            if (d > max) max = d;
        }

        return max;
    }

    public bool Contains(Vec3 p, double tolerance = 0) => SignedDistance(p) <= tolerance;

    /// <summary>
    /// Clips segment a→b against the region (Cyrus–Beck). Returns false if no part lies inside.
    /// Parameters are in [0, 1] along the segment.
    /// </summary>
    public bool ClipSegment(Vec3 a, Vec3 b, out double t0, out double t1, double tolerance = 0)
    {
        t0 = 0;
        t1 = 1;
        var d = b - a;
        foreach (var f in Faces)
        {
            var n = f.Plane.Normal;
            var num = f.Plane.Offset + tolerance - Vec3.Dot(n, a);
            var den = Vec3.Dot(n, d);
            if (Math.Abs(den) < 1e-15)
            {
                if (num < 0) return false;
                continue;
            }

            var t = num / den;
            if (den > 0)
            {
                if (t < t1) t1 = t;
            }
            else
            {
                if (t > t0) t0 = t;
            }

            if (t0 > t1) return false;
        }

        return true;
    }

    public ConvexRegion Translate(Vec3 offset) =>
        new(Faces.Select(f => f with { Plane = f.Plane.Translate(offset) }).ToArray(), Vertices.Select(v => v + offset).ToArray());

    /// <summary>Checks that every vertex lies on or inside every face plane (i.e. the description is consistent and convex).</summary>
    public bool IsConsistent(double tolerance) => Vertices.All(v => Faces.All(f => f.Plane.SignedDistance(v) <= tolerance));
}
