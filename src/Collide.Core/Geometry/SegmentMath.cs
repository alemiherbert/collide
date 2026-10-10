namespace Collide.Geometry;

/// <summary>Exact closest-point and distance routines for segments and simple solids.</summary>
public static class SegmentMath
{
    private const double Eps = 1e-12;

    /// <summary>
    /// Closest points between segments p1→q1 and p2→q2 (Ericson, Real-Time Collision Detection §5.1.9).
    /// For (near-)parallel overlapping segments the closest pair is taken at the middle of the overlap so the
    /// reported location is representative rather than an arbitrary endpoint.
    /// </summary>
    public static double ClosestPoints(Vec3 p1, Vec3 q1, Vec3 p2, Vec3 q2, out double s, out double t, out Vec3 c1, out Vec3 c2)
    {
        var d1 = q1 - p1;
        var d2 = q2 - p2;
        var r = p1 - p2;
        var a = d1.LengthSquared;
        var e = d2.LengthSquared;
        var f = Vec3.Dot(d2, r);

        if (a <= Eps && e <= Eps)
        {
            s = t = 0;
            c1 = p1;
            c2 = p2;
            return Vec3.Distance(c1, c2);
        }

        if (a <= Eps)
        {
            s = 0;
            t = Math.Clamp(f / e, 0, 1);
        }
        else
        {
            var c = Vec3.Dot(d1, r);
            if (e <= Eps)
            {
                t = 0;
                s = Math.Clamp(-c / a, 0, 1);
            }
            else
            {
                var b = Vec3.Dot(d1, d2);
                var denom = a * e - b * b;
                if (denom > 1e-12 * a * e)
                {
                    s = Math.Clamp((b * f - c * e) / denom, 0, 1);
                }
                else
                {
                    // Parallel: choose the middle of the projected overlap of segment 2 onto segment 1.
                    var sp = Math.Clamp(Vec3.Dot(p2 - p1, d1) / a, 0, 1);
                    var sq = Math.Clamp(Vec3.Dot(q2 - p1, d1) / a, 0, 1);
                    s = 0.5 * (sp + sq);
                }

                t = (b * s + f) / e;
                if (t < 0)
                {
                    t = 0;
                    s = Math.Clamp(-c / a, 0, 1);
                }
                else if (t > 1)
                {
                    t = 1;
                    s = Math.Clamp((b - c) / a, 0, 1);
                }
            }
        }

        c1 = p1 + d1 * s;
        c2 = p2 + d2 * t;
        return Vec3.Distance(c1, c2);
    }

    public static double PointSegmentDistance(Vec3 p, Vec3 a, Vec3 b, out double t)
    {
        var ab = b - a;
        var len2 = ab.LengthSquared;
        t = len2 <= Eps ? 0 : Math.Clamp(Vec3.Dot(p - a, ab) / len2, 0, 1);
        return Vec3.Distance(p, a + ab * t);
    }

    /// <summary>
    /// Distance from a point to the solid of a bar segment with radius r. A free end is a flat disc
    /// (real bars are cut square); a joined end is a hemisphere (the swept tube continues into the next element).
    /// </summary>
    public static double PointToBarSegment(Vec3 q, Vec3 a, Vec3 b, double r, bool flatStart, bool flatEnd)
    {
        var axis = b - a;
        var len = axis.Length;
        if (len < 1e-12) return Math.Max(0, Vec3.Distance(q, a) - r);
        var dir = axis / len;
        var w = q - a;
        var s = Vec3.Dot(w, dir);
        if (s < 0)
        {
            if (!flatStart) return Math.Max(0, Vec3.Distance(q, a) - r);
            var rho = (w - dir * s).Length;
            var ex = Math.Max(0, rho - r);
            return Math.Sqrt(s * s + ex * ex);
        }

        if (s > len)
        {
            if (!flatEnd) return Math.Max(0, Vec3.Distance(q, b) - r);
            var rho = (w - dir * s).Length;
            var ax = s - len;
            var ex = Math.Max(0, rho - r);
            return Math.Sqrt(ax * ax + ex * ex);
        }

        var radial = (w - dir * s).Length;
        return Math.Max(0, radial - r);
    }

    /// <summary>
    /// Minimises a convex function on [lo, hi] by golden-section search. Exact (to ~1e-12 of the interval) for
    /// convex functions such as the distance from a moving point on a segment to a convex solid.
    /// </summary>
    public static (double X, double Value) MinimizeConvex(Func<double, double> f, double lo = 0, double hi = 1, int iterations = 80)
    {
        const double invPhi = 0.6180339887498949;
        var a = lo;
        var b = hi;
        var c = b - invPhi * (b - a);
        var d = a + invPhi * (b - a);
        var fc = f(c);
        var fd = f(d);
        for (var i = 0; i < iterations && b - a > 1e-13; i++)
        {
            if (fc <= fd)
            {
                b = d;
                d = c;
                fd = fc;
                c = b - invPhi * (b - a);
                fc = f(c);
            }
            else
            {
                a = c;
                c = d;
                fc = fd;
                d = a + invPhi * (b - a);
                fd = f(d);
            }
        }

        var x = fc <= fd ? c : d;
        var best = Math.Min(fc, fd);
        var fl = f(lo);
        var fh = f(hi);
        if (fl < best) return (lo, fl);
        if (fh < best) return (hi, fh);
        return (x, best);
    }

    /// <summary>Distance from segment p→q (centreline, no radius) to an oriented box; 0 if they intersect.</summary>
    public static double SegmentBoxDistance(Vec3 p, Vec3 q, OrientedBox box, out double atParameter)
    {
        var (x, value) = MinimizeConvex(u => box.DistanceTo(Vec3.Lerp(p, q, u)));
        atParameter = x;
        return value;
    }

    /// <summary>Area of the intersection of two circles (lens) with radii r1, r2 and centre distance d.</summary>
    public static double CircleIntersectionArea(double r1, double r2, double d)
    {
        if (d >= r1 + r2) return 0;
        if (d <= Math.Abs(r1 - r2)) return Math.PI * Math.Pow(Math.Min(r1, r2), 2);
        var a1 = Math.Acos(Math.Clamp((d * d + r1 * r1 - r2 * r2) / (2 * d * r1), -1, 1));
        var a2 = Math.Acos(Math.Clamp((d * d + r2 * r2 - r1 * r1) / (2 * d * r2), -1, 1));
        var k = (-d + r1 + r2) * (d + r1 - r2) * (d - r1 + r2) * (d + r1 + r2);
        return r1 * r1 * a1 + r2 * r2 * a2 - 0.5 * Math.Sqrt(Math.Max(0, k));
    }
}
