using System.Globalization;
using Collide.Findings;
using Collide.Geometry;
using Collide.Model;
using Collide.Rules;

namespace Collide.Analysis;

/// <summary>
/// Narrow phase for one bar pair: duplicates, laps, physical clashes (vs intentional contacts), unclassified contacts
/// and clear spacing. Only primitives whose inflated bounds overlap are measured.
/// </summary>
internal sealed class PairAnalyzer
{
    private readonly PreparedModel _pm;
    private readonly RuleSet _rules;
    private readonly ToleranceSettings _tol;
    private readonly double _ct;
    private readonly double _cosParallel;
    private readonly double _halfMargin;

    public PairAnalyzer(PreparedModel pm)
    {
        _pm = pm;
        _rules = pm.Rules;
        _tol = pm.Rules.Tolerances;
        _ct = _tol.ContactTolerance.Millimetres;
        _cosParallel = Math.Cos(_tol.ParallelAngleTolerance.Radians);
        _halfMargin = pm.SearchMargin / 2;
    }

    private readonly record struct Prox(int I, int J, SegmentProximity P);

    private readonly record struct LapGeometry(int SegA, int SegB, double Overlap, double Offset, Vec3 OffsetDirection, Vec3 EndA, Vec3 EndB, bool Declared);

    public void Analyze(RebarBar a, RebarBar b, AnalysisSink sink)
    {
        var sa = _pm.Solids[a.Id];
        var sb = _pm.Solids[b.Id];
        var prox = new List<Prox>();
        for (var i = 0; i < sa.Segments.Count; i++)
        {
            var boxA = sa.Segments[i].Bounds.Inflate(_halfMargin);
            for (var j = 0; j < sb.Segments.Count; j++)
            {
                if (!boxA.Intersects(sb.Segments[j].Bounds.Inflate(_halfMargin))) continue;
                prox.Add(new Prox(i, j, _pm.Kernel.Measure(sa.Segments[i], sb.Segments[j])));
            }
        }

        sink.PrimitivePairs += prox.Count;
        if (prox.Count == 0) return;
        sink.CandidatePairs++;

        var hostA = _pm.Model.FindHost(a.HostId);
        var hostB = _pm.Model.FindHost(b.HostId);
        var hostIds = FindingFactory.Sorted([a.HostId, b.HostId]);

        if (IsDuplicate(a, b, sa, sb, out var deviation))
        {
            sink.Findings.Add(new Finding
            {
                Check = CheckKind.Duplicate,
                Subtype = IssueSubtype.Duplicate,
                Status = FindingStatus.Violation,
                Owner = FindingOwner.Bars(a.Id, b.Id),
                BarIds = FindingFactory.Sorted([a.Id, b.Id]),
                HostIds = hostIds,
                Location = Mid(a.Path),
                Measurements = [Measurement.Mm("maxCentrelineDeviation", deviation), Measurement.Mm("barLength", a.Path.Length)],
                Exceedance = a.PhysicalDiameter.Millimetres,
                DecisionMargin = _tol.DuplicateTolerance.Millimetres - deviation,
                Explanation = string.Create(CultureInfo.InvariantCulture,
                    $"Bars {a.Id} and {b.Id} occupy the same position (max centreline deviation {deviation:0.###} mm ≤ duplicate tolerance {_tol.DuplicateTolerance}) with the same diameter and length."),
            });
            return;
        }

        var lap = DetectLap(a, b, sa, sb);
        if (lap is { } l)
        {
            EvaluateLap(a, b, hostA, hostB, l, sink);
            prox.RemoveAll(p => p.I == l.SegA && p.J == l.SegB);
            if (prox.Count == 0) return;
        }

        var worst = prox[0];
        foreach (var p in prox)
        {
            if (p.P.SurfaceDistance < worst.P.SurfaceDistance) worst = p;
        }

        var angleDeg = worst.P.AngleRadians * 180 / Math.PI;
        var signed = worst.P.SurfaceDistance;
        var separation = Separation(worst.P, sa.Segments[worst.I], sb.Segments[worst.J]);
        var contact = new ContactGeometry(a.Id, b.Id, worst.P.PointA, worst.P.PointB, separation, angleDeg);
        var location = Vec3.Lerp(worst.P.PointA, worst.P.PointB, 0.5);
        var parallel = Math.Cos(worst.P.AngleRadians) >= _cosParallel;

        if (signed < -_ct || Math.Abs(signed) <= _ct)
        {
            var penetration = Math.Max(0, -signed);
            var intent = MatchIntentionalContact(a, b, hostA, hostB, angleDeg, penetration);
            if (intent is not null)
            {
                sink.Suppressed.Add(new SuppressedFinding($"Clash|{a.Id}|{b.Id}", IssueSubtype.CrossingClash, FindingFactory.Sorted([a.Id, b.Id]),
                    $"Intentional contact: {intent.Description ?? intent.Id}", intent.Id, penetration, location));
                return;
            }

            if (signed < -_ct)
            {
                var subtype = ResolveClashSubtype(a, b, hostA, hostB, sa.Segments[worst.I], sb.Segments[worst.J], worst.P, parallel);
                var volume = _pm.Kernel.EstimateOverlapVolume(sa, worst.I, sb, worst.J);
                var clashing = prox.Count(p => p.P.SurfaceDistance < -_ct);
                var notes = new List<string>();
                if (worst.P.ErrorBound > 0) notes.Add($"Arc chord approximation: measurement error ≤ {worst.P.ErrorBound:0.###} mm.");
                if (worst.P.Method == ProximityMethod.FlatEndRefined) notes.Add("Near a free bar end: value is an exact lower bound of the flat-ended cylinder separation.");
                if (volume.Method == VolumeMethod.VoxelSampling) notes.Add($"Overlap volume estimated by voxel sampling ({volume.Samples} samples).");
                sink.Findings.Add(new Finding
                {
                    Check = CheckKind.Clash,
                    Subtype = subtype,
                    Status = FindingStatus.Violation,
                    Owner = FindingOwner.Bars(a.Id, b.Id),
                    BarIds = FindingFactory.Sorted([a.Id, b.Id]),
                    HostIds = hostIds,
                    Location = location,
                    Measurements =
                    [
                        Measurement.Mm("penetrationDepth", penetration),
                        Measurement.Mm("surfaceDistance", signed),
                        Measurement.Mm("centrelineDistance", worst.P.CentrelineDistance),
                        Measurement.Degrees("contactAngle", angleDeg),
                        Measurement.CubicMm("overlapVolume", volume.CubicMillimetres, volume.Method.ToString()),
                        Measurement.Count("clashingPrimitivePairs", clashing),
                    ],
                    Exceedance = penetration,
                    DecisionMargin = penetration - _ct,
                    ErrorBound = worst.P.ErrorBound,
                    GeometryApproximate = worst.P.ErrorBound > 0 || worst.P.Method == ProximityMethod.FlatEndRefined,
                    Contact = contact,
                    Notes = notes,
                    Explanation = string.Create(CultureInfo.InvariantCulture,
                        $"Physical bar solids intersect: penetration {penetration:0.##} mm exceeds the contact tolerance {_tol.ContactTolerance} (Ø{a.PhysicalDiameter.Millimetres:0.#} vs Ø{b.PhysicalDiameter.Millimetres:0.#}, contact angle {angleDeg:0.#}°). No intentional-contact rule or approved exception covers this pair."),
                });
                return;
            }

            if (!parallel)
            {
                sink.Findings.Add(new Finding
                {
                    Check = CheckKind.Clash,
                    Subtype = IssueSubtype.UnclassifiedContact,
                    Status = FindingStatus.Warning,
                    Owner = FindingOwner.Bars(a.Id, b.Id),
                    BarIds = FindingFactory.Sorted([a.Id, b.Id]),
                    HostIds = hostIds,
                    Location = location,
                    Measurements = [Measurement.Mm("surfaceDistance", signed), Measurement.Degrees("contactAngle", angleDeg)],
                    DecisionMargin = _ct - Math.Abs(signed),
                    ErrorBound = worst.P.ErrorBound,
                    Contact = contact,
                    Explanation = string.Create(CultureInfo.InvariantCulture,
                        $"Bars touch (surface distance {signed:0.###} mm, within ±{_tol.ContactTolerance}) at {angleDeg:0.#}°, but no intentional-contact rule covers this combination (roles {a.Role}/{b.Role}, layers {a.Layer ?? "?"}/{b.Layer ?? "?"})."),
                });
                return;
            }
        }

        EvaluateClearSpacing(a, b, hostA, hostB, sa, sb, prox, lap, sink);
    }

    private void EvaluateClearSpacing(RebarBar a, RebarBar b, StructuralHost? hostA, StructuralHost? hostB, BarSolid sa, BarSolid sb,
        List<Prox> prox, LapGeometry? lap, AnalysisSink sink)
    {
        Prox? governing = null;
        var governingOverlap = 0.0;
        foreach (var p in prox)
        {
            if (Math.Cos(p.P.AngleRadians) < _cosParallel) continue;
            var segA = sa.Segments[p.I];
            var segB = sb.Segments[p.J];
            var overlap = AxialOverlap(segA, segB);
            if (overlap < _tol.MinimumParallelOverlap.Millimetres) continue;
            if (governing is null || p.P.SurfaceDistance < governing.Value.P.SurfaceDistance)
            {
                governing = p;
                governingOverlap = overlap;
            }
        }

        if (governing is not { } g) return;
        var clear = g.P.SurfaceDistance;
        if (clear > _pm.SearchMargin) return;

        var owner = FindingOwner.Bars(a.Id, b.Id);
        var barIds = FindingFactory.Sorted([a.Id, b.Id]);
        var hostIds = FindingFactory.Sorted([a.HostId, b.HostId]);
        var req = SpacingRequirement.Evaluate(_pm, a, b, hostA, hostB);
        if (req.Rule is null)
        {
            if (_rules.IsRequired(CheckKind.ClearSpacing))
            {
                sink.Findings.Add(FindingFactory.Indeterminate(CheckKind.ClearSpacing, owner, barIds, hostIds,
                    $"ClearSpacing|{hostA?.Category}|{a.Role}/{b.Role}|no-rule",
                    $"No clear-spacing rule applies to parallel {a.Role}/{b.Role} bars in {hostA?.Category.ToString() ?? "an unknown host"}; spacing compliance cannot be determined.",
                    ["ClearSpacingRule"]));
            }

            return;
        }

        var rule = req.Rule;
        if (req.Required is null)
        {
            sink.Findings.Add(FindingFactory.Indeterminate(CheckKind.ClearSpacing, owner, barIds, hostIds,
                $"ClearSpacing|{rule.Id}|missing:MaximumAggregateSize",
                $"Rule {rule.Id} needs the maximum aggregate size, which is not defined for the project or host {hostA?.Id}; clear spacing and aggregate passage cannot be verified.",
                ["MaximumAggregateSize"]) with { Rule = RuleReference.Of(rule) });
            return;
        }

        var required = req.Required.Value;
        var gov = (Name: req.GoverningTerm, Aggregate: req.AggregateGoverns);
        if (clear >= required - _tol.ComplianceTolerance.Millimetres) return;

        var location = Vec3.Lerp(g.P.PointA, g.P.PointB, 0.5);
        sink.Findings.Add(new Finding
        {
            Check = CheckKind.ClearSpacing,
            Subtype = gov.Aggregate ? IssueSubtype.InsufficientAggregatePassage : IssueSubtype.InsufficientClearSpacing,
            Status = FindingStatus.Violation,
            Owner = owner,
            BarIds = barIds,
            HostIds = hostIds,
            Location = location,
            Measurements =
            [
                Measurement.Mm("clearSpacing", clear),
                Measurement.Mm("requiredClearSpacing", required, gov.Name),
                Measurement.Mm("parallelRunLength", governingOverlap),
            ],
            Exceedance = required - clear,
            DecisionMargin = required - clear,
            ErrorBound = g.P.ErrorBound,
            GeometryApproximate = g.P.ErrorBound > 0,
            Rule = RuleReference.Of(rule),
            Contact = new ContactGeometry(a.Id, b.Id, g.P.PointA, g.P.PointB, Separation(g.P, sa.Segments[g.I], sb.Segments[g.J]), g.P.AngleRadians * 180 / Math.PI),
            Explanation = string.Create(CultureInfo.InvariantCulture,
                $"Clear spacing {clear:0.##} mm between parallel bars is below the required {required:0.##} mm ({gov.Name}, rule {rule.Id}: {rule.Source}) over a {governingOverlap:0} mm parallel run. The bars do not intersect."),
        });
    }

    private void EvaluateLap(RebarBar a, RebarBar b, StructuralHost? hostA, StructuralHost? hostB, LapGeometry lap, AnalysisSink sink)
    {
        var ra = a.PhysicalDiameter.Millimetres / 2;
        var rb = b.PhysicalDiameter.Millimetres / 2;
        var clear = lap.Offset - ra - rb;
        var location = Vec3.Lerp(lap.EndA, lap.EndB, 0.5);
        var owner = FindingOwner.Bars(a.Id, b.Id);
        var barIds = FindingFactory.Sorted([a.Id, b.Id]);
        var hostIds = FindingFactory.Sorted([a.HostId, b.HostId]);
        var d = Math.Max(a.NominalDiameter.Millimetres, b.NominalDiameter.Millimetres);
        sink.Laps.Add(new LapPair(barIds[0], barIds[^1], lap.Overlap, lap.Offset, clear, lap.Declared, location));
        var inferredNote = lap.Declared ? "declared splice" : "lap inferred from geometry";

        if (clear < -_ct)
        {
            sink.Findings.Add(new Finding
            {
                Check = CheckKind.Clash,
                Subtype = IssueSubtype.LapGeometryConflict,
                Status = FindingStatus.Violation,
                Owner = owner,
                BarIds = barIds,
                HostIds = hostIds,
                Location = location,
                Measurements = [Measurement.Mm("penetrationDepth", -clear), Measurement.Mm("centreOffset", lap.Offset), Measurement.Mm("lapLength", lap.Overlap)],
                Exceedance = -clear,
                DecisionMargin = -clear - _ct,
                Inferred = !lap.Declared,
                Contact = new ContactGeometry(a.Id, b.Id, lap.EndB - lap.OffsetDirection * lap.Offset, lap.EndB, -lap.OffsetDirection, 0),
                Explanation = string.Create(CultureInfo.InvariantCulture,
                    $"Lapped bars interpenetrate by {-clear:0.##} mm over the {lap.Overlap:0} mm lap ({inferredNote}); a contact lap needs the bars side by side (centre offset ≥ {ra + rb:0.##} mm)."),
            });
        }
        else if (clear > _ct)
        {
            var match = RuleEngine.Find(_rules.NonContactLaps, [FindingFactory.Ctx(_pm, a, hostA), FindingFactory.Ctx(_pm, b, hostB)]);
            if (match is null)
            {
                if (_rules.IsRequired(CheckKind.Lap))
                {
                    sink.Findings.Add(FindingFactory.Indeterminate(CheckKind.Lap, owner, barIds, hostIds, $"Lap|{hostA?.Category}|non-contact|no-rule",
                        string.Create(CultureInfo.InvariantCulture, $"Non-contact lap (clear offset {clear:0.##} mm) but no non-contact lap rule applies; lap geometry cannot be verified."),
                        ["NonContactLapRule"], "noncontact"));
                }
            }
            else
            {
                var rule = match.Rule;
                var tooFar = rule.MaximumClearOffset is { } max && clear > max.Millimetres + _tol.ComplianceTolerance.Millimetres;
                var ratio = lap.Overlap > 0 ? lap.Offset / lap.Overlap : double.PositiveInfinity;
                var ratioBad = rule.MaximumOffsetToLapLengthRatio is { } rmax && ratio > rmax;
                if (tooFar || ratioBad)
                {
                    sink.Findings.Add(new Finding
                    {
                        Check = CheckKind.Lap,
                        Subtype = IssueSubtype.LapGeometryInvalid,
                        Status = FindingStatus.Violation,
                        Owner = owner,
                        BarIds = barIds,
                        HostIds = hostIds,
                        Location = location,
                        Measurements = [Measurement.Mm("clearOffset", clear), Measurement.Ratio("offsetToLapLength", ratio)],
                        Exceedance = tooFar ? clear - rule.MaximumClearOffset!.Value.Millimetres : 0,
                        DecisionMargin = tooFar ? clear - rule.MaximumClearOffset!.Value.Millimetres : double.PositiveInfinity,
                        Rule = RuleReference.Of(rule),
                        Inferred = !lap.Declared,
                        Explanation = string.Create(CultureInfo.InvariantCulture,
                            $"Non-contact lap bars are {clear:0.##} mm apart (offset/lap ratio {ratio:0.###}), beyond the limits of rule {rule.Id} ({rule.Source})."),
                    });
                }
            }
        }

        var lapRule = RuleEngine.Find(_rules.LapLengths, [FindingFactory.Ctx(_pm, a, hostA), FindingFactory.Ctx(_pm, b, hostB)]);
        if (lapRule is null)
        {
            if (_rules.IsRequired(CheckKind.Lap))
            {
                sink.Findings.Add(FindingFactory.Indeterminate(CheckKind.Lap, owner, barIds, hostIds, $"Lap|{hostA?.Category}|{a.Role}|no-rule",
                    $"A lap splice ({inferredNote}, {lap.Overlap:0} mm) was found but no lap-length rule applies; lap adequacy cannot be determined.",
                    ["LapLengthRule"], "length"));
            }
        }
        else
        {
            var required = lapRule.Rule.RequiredFor(Units.Length.FromMillimetres(d), out var basis);
            if (required is null)
            {
                sink.Findings.Add(FindingFactory.Indeterminate(CheckKind.Lap, owner, barIds, hostIds, $"Lap|{lapRule.Rule.Id}|Ø{d:0.#}",
                    $"Lap rule {lapRule.Rule.Id} has no requirement for Ø{d:0.#} ({basis}).", ["LapLengthRule.Table"], "length") with { Rule = RuleReference.Of(lapRule.Rule) });
            }
            else if (lap.Overlap < required.Value.Millimetres - _tol.ComplianceTolerance.Millimetres)
            {
                sink.Findings.Add(new Finding
                {
                    Check = CheckKind.Lap,
                    Subtype = IssueSubtype.LapLengthInsufficient,
                    Status = FindingStatus.Violation,
                    Owner = owner,
                    BarIds = barIds,
                    HostIds = hostIds,
                    Location = location,
                    Measurements = [Measurement.Mm("providedLapLength", lap.Overlap), Measurement.Mm("requiredLapLength", required.Value.Millimetres, basis)],
                    Exceedance = required.Value.Millimetres - lap.Overlap,
                    DecisionMargin = required.Value.Millimetres - lap.Overlap,
                    Rule = RuleReference.Of(lapRule.Rule),
                    Inferred = !lap.Declared,
                    Explanation = string.Create(CultureInfo.InvariantCulture,
                        $"Lap length {lap.Overlap:0} mm is less than the required {required.Value.Millimetres:0} mm ({basis}; rule {lapRule.Rule.Id}: {lapRule.Rule.Source}). {char.ToUpperInvariant(inferredNote[0])}{inferredNote[1..]}."),
                });
            }
        }

        foreach (var zone in _pm.Model.Zones.Values.Where(z => z.Kind == ZoneKind.NoLapSplices))
        {
            if (SegmentMath.SegmentBoxDistance(lap.EndA, lap.EndB, zone.Box, out _) > 1e-6) continue;
            sink.Findings.Add(new Finding
            {
                Check = CheckKind.Zones,
                Subtype = IssueSubtype.LapInProhibitedZone,
                Status = FindingStatus.Violation,
                Owner = owner,
                BarIds = barIds,
                HostIds = hostIds,
                Location = location,
                Measurements = [Measurement.Mm("lapLength", lap.Overlap)],
                Exceedance = lap.Overlap,
                Inferred = !lap.Declared,
                Discriminator = zone.Id,
                Explanation = $"Lap splice ({inferredNote}) lies within zone {zone.Id} where lap splices are not permitted ({zone.Description ?? zone.Kind.ToString()}).",
            });
        }
    }

    private LapGeometry? DetectLap(RebarBar a, RebarBar b, BarSolid sa, BarSolid sb)
    {
        var declared = _pm.Model.Splices.Values.Any(s => s.Kind == SpliceKind.Lap &&
            ((s.BarA == a.Id && s.BarB == b.Id) || (s.BarA == b.Id && s.BarB == a.Id)));
        if (!declared)
        {
            if (!_tol.InferLaps || !LapEligible(a.Role) || !LapEligible(b.Role)) return null;
            if (_pm.SetsByBar.TryGetValue(a.Id, out var setsA) && _pm.SetsByBar.TryGetValue(b.Id, out var setsB) && setsA.Intersect(setsB).Any()) return null;
        }

        var maxOffset = _tol.LapInferenceMaxOffsetFactor * Math.Max(a.NominalDiameter.Millimetres, b.NominalDiameter.Millimetres);
        LapGeometry? best = null;
        foreach (var ea in FreeStraightEnds(sa))
        foreach (var eb in FreeStraightEnds(sb))
        {
            var segA = sa.Segments[ea.Segment];
            var segB = sb.Segments[eb.Segment];
            var dirA = segA.Direction;
            if (Math.Abs(Vec3.Dot(dirA, segB.Direction)) < _cosParallel) continue;
            if (Vec3.Dot(ea.Outward, eb.Outward) > -_cosParallel) continue;
            var overlap = Vec3.Dot(ea.Point - eb.Point, ea.Outward);
            if (overlap <= 1e-6 || overlap > Math.Min(segA.Length, segB.Length) + 1e-6) continue;
            // A lap joins two bars end to end: each must continue beyond the overlap (full overlap is duplication, not a lap).
            if (overlap >= a.Path.Length - 1 || overlap >= b.Path.Length - 1) continue;
            var rel = eb.Point - segA.A;
            var offsetVec = rel - dirA * Vec3.Dot(rel, dirA);
            var offset = offsetVec.Length;
            if (!declared && offset > maxOffset) continue;
            var dir = offset > 1e-9 ? offsetVec / offset : dirA.AnyPerpendicular();
            if (best is null || overlap > best.Value.Overlap)
                best = new LapGeometry(ea.Segment, eb.Segment, overlap, offset, dir, ea.Point, eb.Point, declared);
        }

        return best;
    }

    private static bool LapEligible(BarRole role) => role is not (BarRole.Stirrup or BarRole.Tie or BarRole.Crosstie);

    private static IEnumerable<(int Segment, Vec3 Point, Vec3 Outward)> FreeStraightEnds(BarSolid s)
    {
        var first = s.Segments[0];
        if (first.FreeStart && first.Kind == PathElementKind.Straight) yield return (0, first.A, -first.Direction);
        var last = s.Segments[^1];
        if (last.FreeEnd && last.Kind == PathElementKind.Straight) yield return (s.Segments.Count - 1, last.B, last.Direction);
    }

    private bool IsDuplicate(RebarBar a, RebarBar b, BarSolid sa, BarSolid sb, out double deviation)
    {
        deviation = double.PositiveInfinity;
        var tol = _tol.DuplicateTolerance.Millimetres;
        if (Math.Abs(a.PhysicalDiameter.Millimetres - b.PhysicalDiameter.Millimetres) > tol) return false;
        if (Math.Abs(a.Path.Length - b.Path.Length) > tol) return false;
        deviation = Math.Max(MaxDeviation(sa, sb), MaxDeviation(sb, sa));
        return deviation <= tol;
    }

    private static double MaxDeviation(BarSolid x, BarSolid y)
    {
        var max = 0.0;
        foreach (var s in x.Segments)
        {
            foreach (var p in new[] { s.A, Vec3.Lerp(s.A, s.B, 0.5), s.B })
            {
                var best = double.PositiveInfinity;
                foreach (var t in y.Segments) best = Math.Min(best, SegmentMath.PointSegmentDistance(p, t.A, t.B, out _));
                max = Math.Max(max, best);
            }
        }

        return max;
    }

    private IntentionalContactRule? MatchIntentionalContact(RebarBar a, RebarBar b, StructuralHost? hostA, StructuralHost? hostB, double angleDeg, double penetration)
    {
        var ctxA = FindingFactory.Ctx(_pm, a, hostA);
        var ctxB = FindingFactory.Ctx(_pm, b, hostB);
        IntentionalContactRule? best = null;
        foreach (var rule in _rules.IntentionalContacts)
        {
            if (penetration > rule.MaximumPenetration.Millimetres + 1e-9) continue;
            if (!rule.Scope.Matches(ctxA) || !rule.Scope.Matches(ctxB)) continue;
            var rolesOk = (RoleIn(rule.RolesA, a.Role) && RoleIn(rule.RolesB, b.Role)) || (RoleIn(rule.RolesA, b.Role) && RoleIn(rule.RolesB, a.Role));
            if (!rolesOk) continue;
            if (rule.RequireDifferentLayers && (a.Layer is null || b.Layer is null || a.Layer == b.Layer)) continue;
            if (rule.MinimumAngle is { } min && angleDeg < min.Degrees - 1e-9) continue;
            if (rule.MaximumAngle is { } max && angleDeg > max.Degrees + 1e-9) continue;
            if (best is null || rule.Scope.Specificity > best.Scope.Specificity) best = rule;
        }

        return best;
    }

    private static bool RoleIn(IReadOnlyList<BarRole>? roles, BarRole role) => roles is null || roles.Count == 0 || roles.Contains(role);

    private IssueSubtype ResolveClashSubtype(RebarBar a, RebarBar b, StructuralHost? hostA, StructuralHost? hostB,
        in SolidSegment segA, in SolidSegment segB, in SegmentProximity p, bool parallel)
    {
        if (parallel && p.CentrelineDistance <= _tol.DuplicateTolerance.Millimetres) return IssueSubtype.CollinearOverlap;
        var cats = new HashSet<HostCategory>();
        if (hostA is not null) cats.Add(hostA.Category);
        if (hostB is not null) cats.Add(hostB.Category);
        var roles = new[] { a.Role, b.Role };
        var differentHosts = a.HostId != b.HostId;
        bool Has(params HostCategory[] c) => c.Any(cats.Contains);
        bool Role(params BarRole[] r) => roles.Any(r.Contains);

        if (Role(BarRole.Starter, BarRole.Dowel, BarRole.Pile) || (differentHosts && Has(HostCategory.Footing, HostCategory.PileCap, HostCategory.Pile)))
            return IssueSubtype.FoundationStarterConflict;
        if (Role(BarRole.OpeningTrimmer))
        {
            var trimmerHost = a.Role == BarRole.OpeningTrimmer ? hostA : hostB;
            if (trimmerHost?.Category == HostCategory.Slab) return IssueSubtype.SlabOpeningReinforcementConflict;
            if (trimmerHost?.Category == HostCategory.Wall) return IssueSubtype.WallOpeningReinforcementConflict;
        }

        if (Has(HostCategory.BoundaryElement, HostCategory.CouplingBeam)) return IssueSubtype.WallBoundaryConflict;
        if (differentHosts)
        {
            var jointCats = cats.All(c => c is HostCategory.Beam or HostCategory.Column or HostCategory.Slab);
            return jointCats && (cats.Contains(HostCategory.Column) || cats.Contains(HostCategory.Beam))
                ? IssueSubtype.JointConflict
                : IssueSubtype.InterMemberConflict;
        }

        var transverse = new[] { BarRole.Stirrup, BarRole.Tie, BarRole.Crosstie };
        if ((a.Role == BarRole.Longitudinal && transverse.Contains(b.Role)) || (b.Role == BarRole.Longitudinal && transverse.Contains(a.Role)))
            return IssueSubtype.LongitudinalTransverseConflict;
        if (segA.Kind != PathElementKind.Straight || segB.Kind != PathElementKind.Straight) return IssueSubtype.HookBendConflict;
        if (Has(HostCategory.Slab) && a.Layer != b.Layer) return IssueSubtype.SlabLayerConflict;
        return parallel ? IssueSubtype.ParallelOverlap : IssueSubtype.CrossingClash;
    }

    internal static Vec3 Separation(in SegmentProximity p, in SolidSegment a, in SolidSegment b)
    {
        var d = (p.PointA - p.PointB).Normalized();
        if (d != Vec3.Zero) return d;
        d = Vec3.Cross(a.Direction, b.Direction).Normalized();
        return d != Vec3.Zero ? d : a.Direction.AnyPerpendicular();
    }

    private static double AxialOverlap(in SolidSegment a, in SolidSegment b)
    {
        var dir = a.Direction;
        if (dir == Vec3.Zero) return 0;
        var p0 = Vec3.Dot(b.A - a.A, dir);
        var p1 = Vec3.Dot(b.B - a.A, dir);
        return Math.Max(0, Math.Min(a.Length, Math.Max(p0, p1)) - Math.Max(0, Math.Min(p0, p1)));
    }

    private static Vec3 Mid(BarPath path) => path.Bounds.Center;
}
