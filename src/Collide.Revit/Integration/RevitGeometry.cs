using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Collide.Geometry;
using Collide.Model;
using Serilog;
using Plane = Collide.Geometry.Plane;

namespace Collide.Revit.Integration;

/// <summary>Converts Revit geometry to engine geometry. Pure conversion: no document reads beyond the objects passed in.</summary>
internal static class RevitGeometry
{
    private const double ConnectTolMm = 0.5;

    /// <summary>
    /// Converts a centreline chain (as returned by GetTransformedCenterlineCurves) into a bar path, orienting each curve
    /// so the chain is continuous. Hook curves are tagged when the bar has a hook at that end (assumed layout:
    /// [extension, bend, …] at the start, […, bend, extension] at the end — verify in Revit, see the integration plan).
    /// </summary>
    public static BarPath ToPath(IList<Curve> curves, bool hookAtStart, bool hookAtEnd, ILogger log, string barId)
    {
        var elements = new List<PathElement>();
        Vec3? previousEnd = null;
        for (var i = 0; i < curves.Count; i++)
        {
            var curve = curves[i];
            var p0 = RevitUnits.ToMm(curve.GetEndPoint(0));
            var p1 = RevitUnits.ToMm(curve.GetEndPoint(1));
            var reversed = false;
            if (previousEnd is { } prev)
                reversed = Vec3.Distance(prev, p1) + 1e-9 < Vec3.Distance(prev, p0);
            else if (curves.Count > 1)
            {
                var n0 = RevitUnits.ToMm(curves[1].GetEndPoint(0));
                var n1 = RevitUnits.ToMm(curves[1].GetEndPoint(1));
                reversed = Math.Min(Vec3.Distance(p0, n0), Vec3.Distance(p0, n1)) + 1e-9 < Math.Min(Vec3.Distance(p1, n0), Vec3.Distance(p1, n1));
            }

            var (start, end) = reversed ? (p1, p0) : (p0, p1);
            if (previousEnd is { } pe && Vec3.Distance(pe, start) > ConnectTolMm)
                log.Warning("Centreline of bar {Bar} is discontinuous at curve {Index} ({Gap:0.###} mm)", barId, i, Vec3.Distance(pe, start));

            switch (curve)
            {
                case Line:
                    elements.Add(new LineElement(start, end));
                    break;
                case Arc arc:
                    var normal = RevitUnits.ToDirection(arc.Normal);
                    elements.Add(ArcElement.FromCenterStartEnd(RevitUnits.ToMm(arc.Center), start, end, reversed ? -normal : normal));
                    break;
                default:
                    // Splines and other curves: tessellated by Revit (approximation; recorded in the log).
                    log.Information("Bar {Bar} has a {Type} centreline element; using Revit's tessellation", barId, curve.GetType().Name);
                    var pts = curve.Tessellate().Select(RevitUnits.ToMm).ToList();
                    if (reversed) pts.Reverse();
                    for (var k = 1; k < pts.Count; k++) elements.Add(new LineElement(pts[k - 1], pts[k]));
                    break;
            }

            previousEnd = end;
        }

        TagHooks(elements, hookAtStart, hookAtEnd);
        return new BarPath(elements);
    }

    private static void TagHooks(List<PathElement> e, bool start, bool end)
    {
        if (start && e.Count >= 3 && e[0] is LineElement s0 && e[1] is ArcElement s1)
        {
            e[0] = new LineElement(s0.StartPoint, s0.EndPoint, PathElementKind.HookExtension);
            e[1] = new ArcElement(s1.Center, s1.Radius, s1.U, s1.V, s1.Sweep, PathElementKind.Hook);
        }

        if (end && e.Count >= 3 && e[^1] is LineElement e0 && e[^2] is ArcElement e1)
        {
            e[^1] = new LineElement(e0.StartPoint, e0.EndPoint, PathElementKind.HookExtension);
            e[^2] = new ArcElement(e1.Center, e1.Radius, e1.U, e1.V, e1.Sweep, PathElementKind.Hook);
        }
    }

    public static IEnumerable<Solid> Solids(GeometryElement? geometry)
    {
        if (geometry is null) yield break;
        foreach (var obj in geometry)
        {
            switch (obj)
            {
                case Solid { Volume: > 1e-9 } solid:
                    yield return solid;
                    break;
                case GeometryInstance instance:
                    foreach (var s in Solids(instance.GetInstanceGeometry())) yield return s;
                    break;
            }
        }
    }

    /// <summary>
    /// Builds the host's concrete region from its largest solid. The supporting planes of planar faces (planes with every
    /// vertex on their inner side) bound the convex outline exactly; faces that are not supporting are explained either by
    /// the given voids (openings) — fidelity stays Exact — or the host is flagged Approximate.
    /// Exposure comes from Revit's cover settings: <c>GetCoverType(face)</c> is null for concealed faces.
    /// </summary>
    public static (ConvexRegion Region, GeometryFidelity Fidelity, string? Note) HostRegion(
        Element host, Solid solid, IReadOnlyList<HostVoid> voids, Func<Vec3, FaceTag> tagOf)
    {
        var hostData = RebarHostData.GetRebarHostData(host);
        var vertices = new List<Vec3>();
        foreach (Edge edge in solid.Edges)
            vertices.AddRange(edge.Tessellate().Select(RevitUnits.ToMm));

        var faces = new List<(Plane Plane, Face Face)>();
        var nonPlanar = false;
        foreach (Face face in solid.Faces)
        {
            if (face is PlanarFace pf)
                faces.Add((Plane.FromPointNormal(RevitUnits.ToMm(pf.Origin), RevitUnits.ToDirection(pf.FaceNormal)), face));
            else
                nonPlanar = true;
        }

        const double tol = 0.5; // mm
        var supporting = new List<RegionFace>();
        var nonSupporting = new List<Face>();
        foreach (var (plane, face) in faces)
        {
            if (vertices.All(v => plane.SignedDistance(v) <= tol))
            {
                if (supporting.Any(s => Vec3.Dot(s.Plane.Normal, plane.Normal) > 1 - 1e-9 && Math.Abs(s.Plane.Offset - plane.Offset) <= tol)) continue;
                var exposure = hostData is null ? FaceExposure.Unknown
                    : face.Reference is { } reference && hostData.GetCoverType(reference) is not null ? FaceExposure.Exposed
                    : face.Reference is null ? FaceExposure.Unknown : FaceExposure.Concealed;
                supporting.Add(new RegionFace(plane, tagOf(plane.Normal), $"F{supporting.Count}", exposure));
            }
            else
            {
                nonSupporting.Add(face);
            }
        }

        var explained = nonSupporting.All(f =>
        {
            var bbox = f.GetBoundingBox();
            var mid = f.Evaluate((bbox.Min + bbox.Max) / 2);
            var p = RevitUnits.ToMm(mid);
            return voids.Any(v => v.Box.Inflate(1.0).Contains(p));
        });

        if (supporting.Count >= 4 && vertices.Count >= 4)
        {
            var region = new ConvexRegion(supporting, vertices);
            if (!nonPlanar && explained) return (region, GeometryFidelity.Exact, null);
            return (region, GeometryFidelity.Approximate,
                nonPlanar ? "Host has curved faces; represented by the convex region of its planar supporting faces."
                          : "Host is not convex (re-entrant faces not explained by openings); represented by its convex outline.");
        }

        var box = Aabb.FromPoints(vertices);
        return (ConvexRegion.FromBox(OrientedBox.FromMinMax(box.Min, box.Max), (_, n) => tagOf(n)), GeometryFidelity.Approximate,
            "Host geometry could not be bounded by planar faces; represented by its bounding box.");
    }

    /// <summary>Oriented box of an element's bounding box (respecting the box transform).</summary>
    public static OrientedBox? BoxOf(Element element)
    {
        var bb = element.get_BoundingBox(null);
        if (bb is null) return null;
        var t = bb.Transform;
        var min = bb.Min;
        var max = bb.Max;
        var centreLocal = (min + max) / 2;
        var half = (max - min) / 2;
        var centre = RevitUnits.ToMm(t.OfPoint(centreLocal));
        var halfMm = new Vec3(Math.Abs(Units.Length.FromFeet(half.X).Millimetres), Math.Abs(Units.Length.FromFeet(half.Y).Millimetres), Math.Abs(Units.Length.FromFeet(half.Z).Millimetres));
        return new OrientedBox(centre, RevitUnits.ToDirection(t.BasisX), RevitUnits.ToDirection(t.BasisY), RevitUnits.ToDirection(t.BasisZ), halfMm);
    }
}
