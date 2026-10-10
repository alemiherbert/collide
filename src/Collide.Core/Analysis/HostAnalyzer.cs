using System.Globalization;
using Collide.Findings;
using Collide.Geometry;
using Collide.Model;
using Collide.Rules;

namespace Collide.Analysis;

/// <summary>
/// Reinforcement-to-concrete checks for one bar: containment (outside all concrete, unintended host penetration),
/// openings/recesses, prohibited zones and concrete cover (to exposed host faces and to voids).
/// Host solids are convex regions, so clipping and interior distances are exact; arcs are chord-approximated and the
/// chord error is subtracted from cover (conservative).
/// </summary>
internal sealed class HostAnalyzer
{
    private readonly PreparedModel _pm;
    private readonly RuleSet _rules;
    private readonly ToleranceSettings _tol;

    public HostAnalyzer(PreparedModel pm)
    {
        _pm = pm;
        _rules = pm.Rules;
        _tol = pm.Rules.Tolerances;
    }

    private sealed class CoverTrack
    {
        public double Cover = double.PositiveInfinity;
        public Vec3 Point;
        public Vec3 Normal;
        public double ChordError;
        public StructuralHost Host = null!;
        public FaceTag Tag;
    }

    public void Analyze(RebarBar bar, AnalysisSink sink)
    {
        var solid = _pm.Solids[bar.Id];
        var r = solid.Radius;
        var own = _pm.Model.FindHost(bar.HostId);
        if (own is null)
        {
            sink.Findings.Add(FindingFactory.Indeterminate(CheckKind.Containment, FindingOwner.Bars(bar.Id), [bar.Id], [],
                bar.HostId is null ? "Containment|no-host" : "Containment|host-missing",
                bar.HostId is null
                    ? $"Bar {bar.Id} has no host element, so containment and cover cannot be checked."
                    : $"Host {bar.HostId} of bar {bar.Id} is not in the model snapshot; containment and cover cannot be checked.",
                ["HostElement"]));
            return;
        }

        var hosts = _pm.HostsNear(solid.Bounds).Where(h => h.Id != own.Id).OrderBy(h => h.Id, StringComparer.Ordinal).Prepend(own).ToArray();
        var minReport = _tol.MinimumReportableLength.Millimetres;
        var outsideLength = 0.0;
        var maxProtrusion = double.NegativeInfinity;
        Vec3? outsideLocation = null;
        var unintended = new SortedDictionary<string, double>(StringComparer.Ordinal);
        var voidHits = new SortedDictionary<string, (double Length, Vec3 Location, HostVoid Void, StructuralHost Host)>(StringComparer.Ordinal);
        var covers = new Dictionary<(string, FaceTag), CoverTrack>();
        var approxHost = false;

        foreach (var seg in solid.Segments)
        {
            var len = seg.Length;
            if (len < 1e-9) continue;
            var uncovered = new List<(double A, double B)> { (0, 1) };
            var pieces = new List<(StructuralHost Host, double T0, double T1)>();
            foreach (var h in hosts)
            {
                if (!h.Region.ClipSegment(seg.A, seg.B, out var t0, out var t1, 1e-6) || t1 - t0 <= 1e-12) continue;
                pieces.Add((h, t0, t1));
                uncovered = Subtract(uncovered, t0, t1);
                if (h.Fidelity == GeometryFidelity.Approximate) approxHost = true;
            }

            foreach (var (a, b) in uncovered)
            {
                var l = (b - a) * len;
                if (l <= 1e-9) continue;
                outsideLength += l;
                foreach (var u in new[] { a, (a + b) / 2, b })
                {
                    var p = Vec3.Lerp(seg.A, seg.B, u);
                    var protrusion = own.Region.SignedDistance(p) + r;
                    if (protrusion > maxProtrusion)
                    {
                        maxProtrusion = protrusion;
                        outsideLocation = p;
                    }
                }
            }

            foreach (var (h, t0, t1) in pieces)
            {
                var p0 = Vec3.Lerp(seg.A, seg.B, t0);
                var p1 = Vec3.Lerp(seg.A, seg.B, t1);
                if (h.Id != own.Id && bar.PermittedExtensionHostIds is { } permitted && !permitted.Contains(h.Id))
                    unintended[h.Id] = unintended.GetValueOrDefault(h.Id) + (t1 - t0) * len;

                foreach (var v in h.Voids)
                {
                    var region = ConvexRegion.FromBox(v.Box);
                    if (region.ClipSegment(p0, p1, out var v0, out var v1) && (v1 - v0) * (t1 - t0) * len > 1e-9)
                    {
                        var key = $"{h.Id}/{v.Id}";
                        var hit = voidHits.GetValueOrDefault(key, (0, Vec3.Lerp(p0, p1, (v0 + v1) / 2), v, h));
                        voidHits[key] = (hit.Length + (v1 - v0) * (t1 - t0) * len, hit.Location, v, h);
                    }
                }

                TrackFaceCover(h, p0, p1, r, seg, t0, t1, bar.Path, covers);
                foreach (var v in h.Voids)
                {
                    var dist = SegmentMath.SegmentBoxDistance(p0, p1, v.Box, out var u);
                    if (dist <= 1e-9) continue;
                    var p = Vec3.Lerp(p0, p1, u);
                    var normal = (v.Box.ClosestPoint(p) - p).Normalized();
                    Track(covers, h, FaceTag.Opening, dist - r - seg.ChordError, p, normal, seg.ChordError);
                }
            }
        }

        var hostIds = FindingFactory.Sorted([own.Id]);
        var outside = outsideLength > minReport;
        if (outside)
        {
            sink.Findings.Add(new Finding
            {
                Check = CheckKind.Containment,
                Subtype = IssueSubtype.OutsideConcrete,
                Status = FindingStatus.Violation,
                Owner = FindingOwner.Bars(bar.Id),
                BarIds = [bar.Id],
                HostIds = hostIds,
                Location = outsideLocation,
                Measurements = [Measurement.Mm("lengthOutsideConcrete", outsideLength), Measurement.Mm("maxSurfaceProtrusion", maxProtrusion)],
                Exceedance = maxProtrusion,
                DecisionMargin = outsideLength,
                GeometryApproximate = approxHost,
                Notes = approxHost ? ["At least one host is an approximate (bounding-box) solid."] : [],
                Explanation = string.Create(CultureInfo.InvariantCulture,
                    $"{outsideLength:0} mm of bar {bar.Id} centreline lies outside every concrete host (own host {own.Id}); the bar surface protrudes up to {maxProtrusion:0.#} mm beyond the concrete."),
            });
        }

        foreach (var (hostId, length) in unintended)
        {
            if (length <= minReport) continue;
            sink.Findings.Add(new Finding
            {
                Check = CheckKind.Containment,
                Subtype = IssueSubtype.UnintendedHostPenetration,
                Status = FindingStatus.Violation,
                Owner = FindingOwner.Bars(bar.Id),
                BarIds = [bar.Id],
                HostIds = FindingFactory.Sorted([own.Id, hostId]),
                Measurements = [Measurement.Mm("lengthInHost", length)],
                Exceedance = length,
                Discriminator = hostId,
                Explanation = string.Create(CultureInfo.InvariantCulture,
                    $"Bar {bar.Id} extends {length:0} mm into host {hostId}, which is not among its permitted extension hosts."),
            });
        }

        foreach (var (key, hit) in voidHits)
        {
            if (hit.Length <= minReport) continue;
            sink.Findings.Add(new Finding
            {
                Check = CheckKind.Openings,
                Subtype = IssueSubtype.CrossesOpening,
                Status = FindingStatus.Violation,
                Owner = FindingOwner.Bars(bar.Id),
                BarIds = [bar.Id],
                HostIds = FindingFactory.Sorted([hit.Host.Id]),
                Location = hit.Location,
                Measurements = [Measurement.Mm("lengthInVoid", hit.Length)],
                Exceedance = hit.Length,
                Discriminator = key,
                Explanation = string.Create(CultureInfo.InvariantCulture,
                    $"Bar {bar.Id} passes {hit.Length:0} mm through {hit.Void.Kind.ToString().ToLowerInvariant()} {hit.Void.Id} of host {hit.Host.Id}{(hit.Void.Description is null ? "" : $" ({hit.Void.Description})")}."),
            });
        }

        if (!outside) EmitCover(bar, covers, sink);
        CheckProhibitedZones(bar, solid, sink);
    }

    private void TrackFaceCover(StructuralHost h, Vec3 p0, Vec3 p1, double r, in SolidSegment seg, double t0, double t1, BarPath path,
        Dictionary<(string, FaceTag), CoverTrack> covers)
    {
        // A whole chord of an arc is measured against the true arc (exact); a chord clipped by the host boundary falls
        // back to the chord with its sagitta subtracted (conservative).
        var arc = seg.Kind != PathElementKind.Straight && t0 <= 1e-9 && t1 >= 1 - 1e-9 ? path.Elements[seg.ElementIndex] as ArcElement : null;
        double phiA = 0, phiB = 0;
        if (arc is not null)
        {
            phiA = arc.ParameterOf(seg.A);
            phiB = arc.ParameterOf(seg.B);
            if (phiB < phiA) phiB += 2 * Math.PI;
        }

        foreach (var f in h.Region.Faces)
        {
            if (f.Exposure == FaceExposure.Concealed) continue;
            IEnumerable<(Vec3 P, double Sd)> candidates;
            var chordError = seg.ChordError;
            if (arc is not null)
            {
                var sd = arc.MaxSignedDistance(f.Plane, phiA, phiB, out var phi);
                candidates = [(arc.PointAt(phi), sd)];
                chordError = 0;
            }
            else
            {
                var sd0 = f.Plane.SignedDistance(p0);
                var sd1 = f.Plane.SignedDistance(p1);
                candidates = Math.Abs(sd0 - sd1) < 1e-6
                    ? [(p0, sd0), (Vec3.Lerp(p0, p1, 0.5), (sd0 + sd1) / 2), (p1, sd1)]
                    : [sd0 > sd1 ? (p0, sd0) : (p1, sd1)];
            }

            foreach (var (p, sd) in candidates)
            {
                if (f.Exposure == FaceExposure.Unknown && IsInterface(h, f, p, sd)) continue;
                Track(covers, h, f.Tag, -sd - r - chordError, p, f.Plane.Normal, chordError);
            }
        }
    }

    /// <summary>A face is concealed at a location if the point just beyond it lies inside another host.</summary>
    private bool IsInterface(StructuralHost h, RegionFace f, Vec3 p, double sd)
    {
        var q = p - f.Plane.Normal * sd + f.Plane.Normal * _tol.InterfaceProbeOffset.Millimetres;
        return _pm.HostsContaining(q, 1e-6).Any(other => other.Id != h.Id);
    }

    private static void Track(Dictionary<(string, FaceTag), CoverTrack> covers, StructuralHost h, FaceTag tag, double cover, Vec3 p, Vec3 normal, double chordError)
    {
        if (!covers.TryGetValue((h.Id, tag), out var t)) covers[(h.Id, tag)] = t = new CoverTrack { Host = h, Tag = tag };
        if (cover >= t.Cover) return;
        t.Cover = cover;
        t.Point = p;
        t.Normal = normal;
        t.ChordError = chordError;
    }

    private void EmitCover(RebarBar bar, Dictionary<(string, FaceTag), CoverTrack> covers, AnalysisSink sink)
    {
        foreach (var t in covers.Values.OrderBy(c => c.Host.Id, StringComparer.Ordinal).ThenBy(c => c.Tag))
        {
            var discriminator = $"{t.Host.Id}:{t.Tag}";
            var match = RuleEngine.Find(_rules.Cover, FindingFactory.Ctx(_pm, bar, t.Host, t.Tag));
            if (match is null)
            {
                if (_rules.IsRequired(CheckKind.Cover))
                {
                    sink.Findings.Add(FindingFactory.Indeterminate(CheckKind.Cover, FindingOwner.Bars(bar.Id), [bar.Id], [t.Host.Id],
                        $"Cover|{t.Host.Category}|{t.Tag}|no-rule",
                        $"No cover requirement is defined for {t.Tag} faces of {t.Host.Category} hosts; cover compliance cannot be determined.",
                        [$"CoverRule({t.Host.Category},{t.Tag})"], discriminator));
                }

                continue;
            }

            var required = match.Rule.MinimumCover.Millimetres;
            if (t.Cover >= required - _tol.ComplianceTolerance.Millimetres) continue;
            var opening = t.Tag == FaceTag.Opening;
            sink.Findings.Add(new Finding
            {
                Check = CheckKind.Cover,
                Subtype = opening ? IssueSubtype.InsufficientCoverToOpening : IssueSubtype.InsufficientCover,
                Status = FindingStatus.Violation,
                Owner = FindingOwner.Bars(bar.Id),
                BarIds = [bar.Id],
                HostIds = [t.Host.Id],
                Location = t.Point,
                Measurements =
                [
                    Measurement.Mm("providedCover", t.Cover),
                    Measurement.Mm("requiredCover", required),
                    new Measurement("face", 0, "tag", t.Tag.ToString()),
                ],
                Exceedance = required - t.Cover,
                DecisionMargin = Math.Abs(required - t.Cover),
                ErrorBound = t.ChordError,
                GeometryApproximate = t.ChordError > 0 || t.Host.Fidelity == GeometryFidelity.Approximate,
                Rule = RuleReference.Of(match.Rule),
                FaceNormal = t.Normal,
                Discriminator = discriminator,
                Explanation = string.Create(CultureInfo.InvariantCulture,
                    $"Cover from bar {bar.Id} to the {t.Tag} face of {t.Host.Id} is {t.Cover:0.##} mm; rule {match.Rule.Id} requires {required:0.##} mm ({match.Rule.Source}).{(t.Cover < 0 ? " The bar surface is exposed." : "")}"),
            });
        }
    }

    private void CheckProhibitedZones(RebarBar bar, BarSolid solid, AnalysisSink sink)
    {
        foreach (var zone in _pm.Model.Zones.Values)
        {
            if (zone.Kind != ZoneKind.NoReinforcement) continue;
            if (zone.ExemptRoles.Contains(bar.Role) || zone.ExemptBarIds.Contains(bar.Id)) continue;
            if (!zone.Box.Bounds.Intersects(solid.Bounds)) continue;
            var region = ConvexRegion.FromBox(zone.Box);
            var intrusion = double.NegativeInfinity;
            var inside = 0.0;
            Vec3? at = null;
            foreach (var seg in solid.Segments)
            {
                var dist = SegmentMath.SegmentBoxDistance(seg.A, seg.B, zone.Box, out var u);
                if (solid.Radius - dist > intrusion)
                {
                    intrusion = solid.Radius - dist;
                    at = Vec3.Lerp(seg.A, seg.B, u);
                }

                if (region.ClipSegment(seg.A, seg.B, out var t0, out var t1)) inside += (t1 - t0) * seg.Length;
            }

            if (intrusion <= _tol.ContactTolerance.Millimetres) continue;
            sink.Findings.Add(new Finding
            {
                Check = CheckKind.Zones,
                Subtype = IssueSubtype.InProhibitedZone,
                Status = FindingStatus.Violation,
                Owner = FindingOwner.Bars(bar.Id),
                BarIds = [bar.Id],
                HostIds = FindingFactory.Sorted([bar.HostId]),
                Location = at,
                Measurements = [Measurement.Mm("lengthInZone", inside), Measurement.Mm("surfaceIntrusion", intrusion)],
                Exceedance = intrusion,
                Discriminator = zone.Id,
                Explanation = string.Create(CultureInfo.InvariantCulture,
                    $"Bar {bar.Id} enters zone {zone.Id}, where reinforcement is not permitted ({zone.Description ?? "prohibited zone"}); intrusion {intrusion:0.#} mm, {inside:0} mm of centreline inside."),
            });
        }
    }

    private static List<(double A, double B)> Subtract(List<(double A, double B)> intervals, double t0, double t1)
    {
        var result = new List<(double, double)>(intervals.Count + 1);
        foreach (var (a, b) in intervals)
        {
            if (t1 <= a || t0 >= b)
            {
                result.Add((a, b));
                continue;
            }

            if (t0 > a) result.Add((a, t0));
            if (t1 < b) result.Add((t1, b));
        }

        return result;
    }
}
