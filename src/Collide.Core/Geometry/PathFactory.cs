namespace Collide.Geometry;

/// <summary>Builds bar paths from corner points, inserting tangent bend arcs of a given centreline radius.</summary>
public static class PathFactory
{
    public static BarPath Straight(Vec3 start, Vec3 end) => new([new LineElement(start, end)]);

    /// <summary>
    /// Creates a path through <paramref name="corners"/> with each corner filleted by an arc of centreline
    /// radius <paramref name="bendRadius"/>. <paramref name="legKinds"/> optionally tags each leg
    /// (n-1 entries); a corner adjacent to a <see cref="PathElementKind.HookExtension"/> leg becomes a hook arc.
    /// </summary>
    public static BarPath Polyline(IReadOnlyList<Vec3> corners, double bendRadius, IReadOnlyList<PathElementKind>? legKinds = null)
    {
        if (corners.Count < 2) throw new ArgumentException("Need at least two points.", nameof(corners));
        if (legKinds is not null && legKinds.Count != corners.Count - 1)
            throw new ArgumentException("legKinds must have one entry per leg.", nameof(legKinds));

        var legCount = corners.Count - 1;
        var legStart = new Vec3[legCount];
        var legEnd = new Vec3[legCount];
        for (var i = 0; i < legCount; i++)
        {
            legStart[i] = corners[i];
            legEnd[i] = corners[i + 1];
        }

        var arcs = new ArcElement?[legCount];
        for (var c = 1; c < corners.Count - 1; c++)
        {
            var p = corners[c];
            var d1 = (p - corners[c - 1]).Normalized();
            var d2 = (corners[c + 1] - p).Normalized();
            var cos = Math.Clamp(Vec3.Dot(d1, d2), -1, 1);
            var alpha = Math.Acos(cos);
            if (alpha < 1e-9 || bendRadius <= 0) continue;
            if (alpha > Math.PI - 1e-6) throw new ArgumentException($"Corner {c} reverses direction; use two smaller bends.");
            var tangentLength = bendRadius * Math.Tan(alpha / 2);
            if (tangentLength > Vec3.Distance(corners[c - 1], p) + 1e-9 || tangentLength > Vec3.Distance(p, corners[c + 1]) + 1e-9)
                throw new ArgumentException($"Bend radius {bendRadius} is too large for corner {c}.");
            var m = (d2 - d1 * Vec3.Dot(d2, d1)).Normalized();
            var s = p - d1 * tangentLength;
            var e = p + d2 * tangentLength;
            var center = s + m * bendRadius;
            var hookCorner = legKinds is not null && (legKinds[c - 1] == PathElementKind.HookExtension || legKinds[c] == PathElementKind.HookExtension);
            arcs[c - 1] = new ArcElement(center, bendRadius, -m, d1, alpha, hookCorner ? PathElementKind.Hook : PathElementKind.Bend);
            legEnd[c - 1] = s;
            legStart[c] = e;
        }

        var elements = new List<PathElement>();
        for (var i = 0; i < legCount; i++)
        {
            var kind = legKinds?[i] ?? PathElementKind.Straight;
            if (Vec3.Distance(legStart[i], legEnd[i]) > 1e-9) elements.Add(new LineElement(legStart[i], legEnd[i], kind));
            if (arcs[i] is { } arc) elements.Add(arc);
        }

        return new BarPath(elements);
    }

    /// <summary>
    /// Closed rectangular stirrup/tie in the plane spanned by <paramref name="u"/>, <paramref name="v"/>, centred on
    /// <paramref name="center"/>, with outer centreline dimensions <paramref name="width"/> × <paramref name="height"/>.
    /// Modelled as a closed loop (no hook overlap) with filleted corners.
    /// </summary>
    public static BarPath RectangularLoop(Vec3 center, Vec3 u, Vec3 v, double width, double height, double bendRadius)
    {
        var uu = u.Normalized();
        var vv = v.Normalized();
        var hw = width / 2;
        var hh = height / 2;
        // Start mid-way along the bottom leg so every corner is an interior corner of the polyline.
        var pts = new List<Vec3>
        {
            center - vv * hh,
            center + uu * hw - vv * hh,
            center + uu * hw + vv * hh,
            center - uu * hw + vv * hh,
            center - uu * hw - vv * hh,
            center - vv * hh,
        };
        return Polyline(pts, bendRadius);
    }
}
