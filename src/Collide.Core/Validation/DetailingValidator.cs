using System.Globalization;
using Collide.Analysis;
using Collide.Findings;
using Collide.Geometry;
using Collide.Model;
using Collide.Rules;
using Collide.Units;

namespace Collide.Validation;

/// <summary>
/// Code-compliance (detailing) checks, kept separate from geometric clash detection. Every requirement comes from an
/// explicit rule; when a required check has no applicable rule or input, the result is indeterminate.
/// </summary>
internal sealed class DetailingValidator
{
    private readonly PreparedModel _pm;
    private readonly RuleSet _rules;
    private readonly ToleranceSettings _tol;

    public DetailingValidator(PreparedModel pm)
    {
        _pm = pm;
        _rules = pm.Rules;
        _tol = pm.Rules.Tolerances;
    }

    // ---------------------------------------------------------------- per bar

    /// <summary>Returns false if the bar geometry is invalid (other per-bar checks are then skipped).</summary>
    public bool ValidateGeometry(RebarBar bar, AnalysisSink sink)
    {
        var problems = bar.Path.Validate(_tol.ContinuityTolerance.Millimetres).ToList();
        if (!(bar.NominalDiameter.Millimetres > 0) || !(bar.PhysicalDiameter.Millimetres > 0)) problems.Add("Bar diameter must be positive.");
        if (problems.Count == 0) return true;
        sink.Findings.Add(new Finding
        {
            Check = CheckKind.Geometry,
            Subtype = IssueSubtype.InvalidBarGeometry,
            Status = FindingStatus.Violation,
            Owner = FindingOwner.Bars(bar.Id),
            BarIds = [bar.Id],
            HostIds = FindingFactory.Sorted([bar.HostId]),
            Location = bar.Path.Start,
            Measurements = [Measurement.Count("problems", problems.Count)],
            Exceedance = problems.Count,
            Notes = problems,
            Explanation = $"Bar {bar.Id} has invalid geometry: {string.Join("; ", problems)}",
        });
        return false;
    }

    public void ValidateBendsAndHooks(RebarBar bar, AnalysisSink sink)
    {
        var host = _pm.Model.FindHost(bar.HostId);
        var ctx = FindingFactory.Ctx(_pm, bar, host);
        var d = bar.NominalDiameter.Millimetres;
        var elements = bar.Path.Elements;
        for (var i = 0; i < elements.Count; i++)
        {
            if (elements[i] is not ArcElement arc) continue;
            var inner = 2 * arc.Radius - d;
            var bend = RuleEngine.Find(_rules.Bends, ctx);
            if (bend is null)
            {
                if (_rules.IsRequired(CheckKind.Bend))
                {
                    sink.Findings.Add(FindingFactory.Indeterminate(CheckKind.Bend, FindingOwner.Bars(bar.Id), [bar.Id], [bar.HostId ?? ""],
                        $"Bend|{bar.Role}|Ø{d:0.#}|no-rule", $"No bend-diameter rule applies to Ø{d:0.#} {bar.Role} bars; bend geometry cannot be verified.",
                        ["BendRule"], $"bend{i}"));
                }
            }
            else
            {
                var required = bend.Rule.MinimumInnerDiameterFactor * d;
                if (inner < required - _tol.ComplianceTolerance.Millimetres)
                {
                    sink.Findings.Add(new Finding
                    {
                        Check = CheckKind.Bend,
                        Subtype = IssueSubtype.BendDiameterTooSmall,
                        Status = FindingStatus.Violation,
                        Owner = FindingOwner.Bars(bar.Id),
                        BarIds = [bar.Id],
                        HostIds = FindingFactory.Sorted([bar.HostId]),
                        Location = arc.PointAt(arc.Sweep / 2),
                        Measurements = [Measurement.Mm("innerBendDiameter", inner), Measurement.Mm("requiredInnerBendDiameter", required)],
                        Exceedance = required - inner,
                        DecisionMargin = required - inner,
                        Rule = RuleReference.Of(bend.Rule),
                        Discriminator = $"bend{i}",
                        Explanation = string.Create(CultureInfo.InvariantCulture,
                            $"Inside bend diameter {inner:0.#} mm at element {i} of bar {bar.Id} is below {bend.Rule.MinimumInnerDiameterFactor:0.##}·d = {required:0.#} mm (rule {bend.Rule.Id}: {bend.Rule.Source})."),
                    });
                }
            }

            if (arc.Kind == PathElementKind.Hook) ValidateHook(bar, ctx, i, arc, sink);
        }
    }

    private void ValidateHook(RebarBar bar, RuleContext ctx, int index, ArcElement arc, AnalysisSink sink)
    {
        var elements = bar.Path.Elements;
        var extension = 0.0;
        if (index + 1 < elements.Count && elements[index + 1].Kind == PathElementKind.HookExtension) extension = elements[index + 1].Length;
        else if (index > 0 && elements[index - 1].Kind == PathElementKind.HookExtension) extension = elements[index - 1].Length;
        var angle = arc.Sweep * 180 / Math.PI;
        var anyRule = RuleEngine.Find(_rules.Hooks, ctx);
        if (anyRule is null)
        {
            if (_rules.IsRequired(CheckKind.Hook))
            {
                sink.Findings.Add(FindingFactory.Indeterminate(CheckKind.Hook, FindingOwner.Bars(bar.Id), [bar.Id], [bar.HostId ?? ""],
                    $"Hook|{bar.Role}|no-rule", $"No hook rule applies to {bar.Role} bars; hook geometry cannot be verified.", ["HookRule"], $"hook{index}"));
            }

            return;
        }

        var match = RuleEngine.Find(_rules.Hooks, ctx, r => Math.Abs(r.HookAngle.Degrees - angle) <= r.AngleTolerance.Degrees);
        if (match is null)
        {
            sink.Findings.Add(new Finding
            {
                Check = CheckKind.Hook,
                Subtype = IssueSubtype.HookAngleNotPermitted,
                Status = FindingStatus.Violation,
                Owner = FindingOwner.Bars(bar.Id),
                BarIds = [bar.Id],
                HostIds = FindingFactory.Sorted([bar.HostId]),
                Location = arc.End,
                Measurements = [Measurement.Degrees("hookAngle", angle)],
                Exceedance = 1,
                Discriminator = $"hook{index}",
                Explanation = string.Create(CultureInfo.InvariantCulture, $"Hook of {angle:0.#}° on bar {bar.Id} matches no permitted hook angle for {bar.Role} bars."),
            });
            return;
        }

        var required = match.Rule.RequiredExtension(bar.NominalDiameter).Millimetres;
        if (extension >= required - _tol.ComplianceTolerance.Millimetres) return;
        sink.Findings.Add(new Finding
        {
            Check = CheckKind.Hook,
            Subtype = IssueSubtype.HookExtensionInsufficient,
            Status = FindingStatus.Violation,
            Owner = FindingOwner.Bars(bar.Id),
            BarIds = [bar.Id],
            HostIds = FindingFactory.Sorted([bar.HostId]),
            Location = arc.End,
            Measurements = [Measurement.Degrees("hookAngle", angle), Measurement.Mm("providedExtension", extension), Measurement.Mm("requiredExtension", required)],
            Exceedance = required - extension,
            DecisionMargin = required - extension,
            Rule = RuleReference.Of(match.Rule),
            Discriminator = $"hook{index}",
            Explanation = string.Create(CultureInfo.InvariantCulture,
                $"Hook extension {extension:0.#} mm on bar {bar.Id} is shorter than the required {required:0.#} mm for a {match.Rule.HookAngle} hook (rule {match.Rule.Id}: {match.Rule.Source})."),
        });
    }

    public void ValidateAnchorage(RebarBar bar, AnalysisSink sink)
    {
        foreach (var (req, inferred) in AnchorageRequirements(_pm, bar))
        {
            var target = _pm.Model.FindHost(req.TargetHostId);
            if (target is null)
            {
                sink.Findings.Add(FindingFactory.Indeterminate(CheckKind.Anchorage, FindingOwner.Bars(bar.Id), [bar.Id], [req.TargetHostId],
                    "Anchorage|target-missing", $"Anchorage host {req.TargetHostId} of bar {bar.Id} is not in the model.", ["AnchorageHost"], req.End.ToString()));
                continue;
            }

            var type = req.Type ?? AnchorageTypeAt(bar, req.End);
            var match = RuleEngine.Find(_rules.DevelopmentLengths, FindingFactory.Ctx(_pm, bar, target), r => r.AnchorageType == type);
            if (match is null)
            {
                if (_rules.IsRequired(CheckKind.Anchorage))
                {
                    sink.Findings.Add(FindingFactory.Indeterminate(CheckKind.Anchorage, FindingOwner.Bars(bar.Id), [bar.Id], [target.Id],
                        $"Anchorage|{target.Category}|{type}|no-rule",
                        $"No {type.ToString().ToLowerInvariant()} development-length rule applies to {bar.Role} bars anchored in {target.Category}; anchorage cannot be verified.",
                        ["DevelopmentLengthRule"], req.End.ToString()) with { Inferred = inferred });
                }

                continue;
            }

            var required = match.Rule.RequiredFor(bar.NominalDiameter, out var basis);
            if (required is null)
            {
                sink.Findings.Add(FindingFactory.Indeterminate(CheckKind.Anchorage, FindingOwner.Bars(bar.Id), [bar.Id], [target.Id],
                    $"Anchorage|{match.Rule.Id}|Ø{bar.NominalDiameter.Millimetres:0.#}", $"Development-length rule {match.Rule.Id} has no value for Ø{bar.NominalDiameter.Millimetres:0.#} ({basis}).",
                    ["DevelopmentLengthRule.Table"], req.End.ToString()) with { Rule = RuleReference.Of(match.Rule) });
                continue;
            }

            var provided = ProvidedEmbedment(_pm, bar, req.End, target);
            if (provided >= required.Value.Millimetres - _tol.ComplianceTolerance.Millimetres) continue;
            sink.Findings.Add(new Finding
            {
                Check = CheckKind.Anchorage,
                Subtype = IssueSubtype.AnchorageInsufficient,
                Status = FindingStatus.Violation,
                Owner = FindingOwner.Bars(bar.Id),
                BarIds = [bar.Id],
                HostIds = FindingFactory.Sorted([bar.HostId, target.Id]),
                Location = req.End == BarEnd.Start ? bar.Path.Start : bar.Path.End,
                Measurements =
                [
                    Measurement.Mm("providedEmbedment", provided, match.Rule.MeasurementBasis ?? "centreline length inside the anchoring host, from the bar end"),
                    Measurement.Mm("requiredDevelopmentLength", required.Value.Millimetres, basis),
                ],
                Exceedance = required.Value.Millimetres - provided,
                DecisionMargin = required.Value.Millimetres - provided,
                Rule = RuleReference.Of(match.Rule),
                Inferred = inferred,
                Discriminator = req.End.ToString(),
                Notes = inferred ? ["Anchorage requirement inferred: the bar end lies inside a host other than its own."] : [],
                Explanation = string.Create(CultureInfo.InvariantCulture,
                    $"{type} anchorage of bar {bar.Id} ({req.End} end) into {target.Id} provides {provided:0} mm; rule {match.Rule.Id} requires {required.Value.Millimetres:0} mm ({basis}; {match.Rule.Source})."),
            });
        }
    }

    /// <summary>Declared requirements plus, when enabled, requirements inferred from free ends lying inside another host.</summary>
    internal static IReadOnlyList<(AnchorageRequirement Requirement, bool Inferred)> AnchorageRequirements(PreparedModel pm, RebarBar bar)
    {
        var list = bar.Anchorages.Select(a => (a, false)).ToList();
        if (!pm.Rules.Tolerances.InferAnchorage || bar.Path.IsClosed()) return list;
        foreach (var end in new[] { BarEnd.Start, BarEnd.End })
        {
            if (list.Any(l => l.Item1.End == end)) continue;
            var p = end == BarEnd.Start ? bar.Path.Start : bar.Path.End;
            var host = pm.HostsContaining(p, -1e-6).Where(h => h.Id != bar.HostId).OrderBy(h => h.Id, StringComparer.Ordinal).FirstOrDefault();
            if (host is not null) list.Add((new AnchorageRequirement(end, host.Id), true));
        }

        return list;
    }

    internal static AnchorageType AnchorageTypeAt(RebarBar bar, BarEnd end)
    {
        var e = bar.Path.Elements;
        var near = end == BarEnd.Start ? e.Take(2) : e.Skip(Math.Max(0, e.Count - 2));
        return near.Any(x => x.Kind is PathElementKind.Hook or PathElementKind.HookExtension) ? AnchorageType.Hooked : AnchorageType.Straight;
    }

    /// <summary>Centreline length inside <paramref name="host"/>, measured from the given bar end until the bar first leaves it.</summary>
    internal static double ProvidedEmbedment(PreparedModel pm, RebarBar bar, BarEnd end, StructuralHost host)
    {
        var segs = pm.Solids[bar.Id].Segments;
        var ordered = end == BarEnd.Start
            ? segs.Select(s => (A: s.A, B: s.B))
            : segs.Reverse().Select(s => (A: s.B, B: s.A));
        var total = 0.0;
        var first = true;
        foreach (var (a, b) in ordered)
        {
            if (!host.Region.ClipSegment(a, b, out var t0, out var t1, 1e-6)) break;
            if (t0 > 1e-9) break;
            if (first && !host.Region.Contains(a, 1e-6)) return 0;
            first = false;
            total += (t1 - t0) * Vec3.Distance(a, b);
            if (t1 < 1 - 1e-9) break;
        }

        return total;
    }

    public void ValidateRestrictions(RebarBar bar, AnalysisSink sink)
    {
        var match = RuleEngine.Find(_rules.Restrictions, FindingFactory.Ctx(_pm, bar));
        if (match is null) return;
        var rule = match.Rule;
        if (rule.AllowedDiameters is { Count: > 0 } allowed && !allowed.Any(x => Math.Abs(x.Millimetres - bar.NominalDiameter.Millimetres) <= 0.05))
        {
            sink.Findings.Add(new Finding
            {
                Check = CheckKind.Restrictions,
                Subtype = IssueSubtype.DiameterNotPermitted,
                Status = FindingStatus.Violation,
                Owner = FindingOwner.Bars(bar.Id),
                BarIds = [bar.Id],
                HostIds = FindingFactory.Sorted([bar.HostId]),
                Measurements = [Measurement.Mm("nominalDiameter", bar.NominalDiameter.Millimetres)],
                Exceedance = 1,
                Rule = RuleReference.Of(rule),
                Explanation = $"Ø{bar.NominalDiameter.Millimetres:0.#} is not among the permitted diameters ({string.Join(", ", allowed.Select(x => x.ToString()))}) of rule {rule.Id}.",
            });
        }

        if (rule.AllowedShapeCodes is { Count: > 0 } shapes)
        {
            if (bar.ShapeCode is null)
            {
                sink.Findings.Add(FindingFactory.Indeterminate(CheckKind.Restrictions, FindingOwner.Bars(bar.Id), [bar.Id], [bar.HostId ?? ""],
                    $"Restrictions|{rule.Id}|no-shape", $"Bar shape code is unknown; shape restriction {rule.Id} cannot be checked.", ["ShapeCode"], "shape"));
            }
            else if (!shapes.Contains(bar.ShapeCode))
            {
                sink.Findings.Add(new Finding
                {
                    Check = CheckKind.Restrictions,
                    Subtype = IssueSubtype.ShapeNotPermitted,
                    Status = FindingStatus.Violation,
                    Owner = FindingOwner.Bars(bar.Id),
                    BarIds = [bar.Id],
                    HostIds = FindingFactory.Sorted([bar.HostId]),
                    Exceedance = 1,
                    Rule = RuleReference.Of(rule),
                    Discriminator = "shape",
                    Explanation = $"Shape code {bar.ShapeCode} is not permitted by rule {rule.Id} (allowed: {string.Join(", ", shapes)}).",
                });
            }
        }
    }

    // ---------------------------------------------------------------- per set

    public void ValidateSet(RebarSet set, AnalysisSink sink)
    {
        var bars = set.BarIds.Select(_pm.Model.FindBar).Where(b => b is not null).Select(b => b!).ToArray();
        var owner = FindingOwner.Set(set.Id);
        var barIds = FindingFactory.Sorted(bars.Select(b => b.Id));
        if (set.DeclaredQuantity is { } declared && declared != bars.Length)
        {
            sink.Findings.Add(new Finding
            {
                Check = CheckKind.Consistency,
                Subtype = IssueSubtype.SetQuantityMismatch,
                Status = FindingStatus.Violation,
                Owner = owner,
                BarIds = barIds,
                HostIds = FindingFactory.Sorted([set.HostId]),
                SetIds = [set.Id],
                Measurements = [Measurement.Count("declaredQuantity", declared), Measurement.Count("barsPresent", bars.Length)],
                Exceedance = Math.Abs(declared - bars.Length),
                Explanation = $"Set {set.Id} declares {declared} bars but {bars.Length} are present in the model.",
            });
        }

        if (bars.Select(b => Math.Round(b.NominalDiameter.Millimetres, 2)).Distinct().Count() > 1)
        {
            sink.Findings.Add(new Finding
            {
                Check = CheckKind.Consistency,
                Subtype = IssueSubtype.InconsistentSetData,
                Status = FindingStatus.Violation,
                Owner = owner,
                BarIds = barIds,
                HostIds = FindingFactory.Sorted([set.HostId]),
                SetIds = [set.Id],
                Exceedance = 1,
                Explanation = $"Bars of set {set.Id} have different diameters ({string.Join(", ", bars.Select(b => b.NominalDiameter.ToString()).Distinct())}).",
            });
        }

        if (bars.Length < 2) return;
        var host = _pm.Model.FindHost(set.HostId ?? bars[0].HostId);
        var ctx = RuleContext.ForBar(bars[0], host);
        var rule = RuleEngine.Find(_rules.MaxSpacing, ctx);
        if (rule is null) return;
        var centres = bars.Select(b => (Bar: b, C: b.Path.Bounds.Center)).ToArray();
        var dir = set.DistributionDirection?.Normalized() ?? (centres[^1].C - centres[0].C).Normalized();
        if (dir == Vec3.Zero) return;
        var ordered = centres.OrderBy(c => Vec3.Dot(c.C, dir)).ToArray();
        var maxGap = 0.0;
        Vec3 at = default;
        for (var i = 1; i < ordered.Length; i++)
        {
            var gap = Vec3.Dot(ordered[i].C - ordered[i - 1].C, dir);
            if (gap > maxGap)
            {
                maxGap = gap;
                at = Vec3.Lerp(ordered[i].C, ordered[i - 1].C, 0.5);
            }
        }

        var max = rule.Rule.MaximumCentreSpacing.Millimetres;
        if (maxGap <= max + _tol.ComplianceTolerance.Millimetres) return;
        sink.Findings.Add(new Finding
        {
            Check = CheckKind.MaxSpacing,
            Subtype = IssueSubtype.MaxSpacingExceeded,
            Status = FindingStatus.Violation,
            Owner = owner,
            BarIds = barIds,
            HostIds = FindingFactory.Sorted([set.HostId]),
            SetIds = [set.Id],
            Location = at,
            Measurements = [Measurement.Mm("maxCentreSpacing", maxGap), Measurement.Mm("permittedCentreSpacing", max)],
            Exceedance = maxGap - max,
            DecisionMargin = maxGap - max,
            Rule = RuleReference.Of(rule.Rule),
            Explanation = string.Create(CultureInfo.InvariantCulture,
                $"Centre spacing in set {set.Id} reaches {maxGap:0.#} mm, above the {max:0.#} mm permitted by rule {rule.Rule.Id} ({rule.Rule.Source})."),
        });
    }

    // ---------------------------------------------------------------- per mark

    public void ValidateMark(string mark, IReadOnlyList<RebarBar> bars, AnalysisSink sink)
    {
        var diameters = bars.Select(b => Math.Round(b.NominalDiameter.Millimetres, 2)).Distinct().ToArray();
        var shapes = bars.Select(b => b.ShapeCode).Where(s => s is not null).Distinct().ToArray();
        if (diameters.Length <= 1 && shapes.Length <= 1) return;
        sink.Findings.Add(new Finding
        {
            Check = CheckKind.Consistency,
            Subtype = IssueSubtype.MarkInconsistency,
            Status = FindingStatus.Violation,
            Owner = FindingOwner.Mark(mark),
            BarIds = FindingFactory.Sorted(bars.Select(b => b.Id)),
            HostIds = FindingFactory.Sorted(bars.Select(b => b.HostId)),
            Exceedance = 1,
            Explanation = $"Bar mark {mark} is used for different diameters ({string.Join(", ", diameters)}) or shapes ({string.Join(", ", shapes)}).",
        });
    }

    // ---------------------------------------------------------------- per host

    public void ValidateTransverse(StructuralHost host, AnalysisSink sink)
    {
        var ctx = RuleContext.ForHost(host) with { Role = BarRole.Stirrup };
        var match = RuleEngine.Find(_rules.TransverseSpacing, ctx);
        if (match is null) return;
        var rule = match.Rule;
        var (start, end, approxAxis) = host.Axis is { } ax ? (ax.Start, ax.End, false) : LongestAxis(host);
        var axisLength = Vec3.Distance(start, end);
        var dir = (end - start).Normalized();
        var hosted = _pm.BarsByHost.TryGetValue(host.Id, out var ids) ? ids : [];
        var positions = hosted.Select(_pm.Bar).Where(b => b.Role is BarRole.Stirrup or BarRole.Tie)
            .Select(b => (Id: b.Id, T: Vec3.Dot(b.Path.Bounds.Center - start, dir)))
            .OrderBy(p => p.T).ThenBy(p => p.Id, StringComparer.Ordinal).ToArray();
        var owner = FindingOwner.Host(host.Id);

        var zones = rule.Zone == TransverseZone.Entire
            ? new[] { ("member", 0.0, axisLength) }
            : new[] { ("start zone", 0.0, rule.EndZoneLength!.Value.Millimetres), ("end zone", axisLength - rule.EndZoneLength!.Value.Millimetres, axisLength) };
        foreach (var (name, z0, z1) in zones)
        {
            var inZone = positions.Where(p => p.T >= z0 - 1e-6 && p.T <= z1 + 1e-6).ToArray();
            if (inZone.Length == 0)
            {
                if (!rule.RequirePresence) continue;
                sink.Findings.Add(new Finding
                {
                    Check = CheckKind.TransverseSpacing,
                    Subtype = IssueSubtype.TransverseReinforcementMissing,
                    Status = FindingStatus.Violation,
                    Owner = owner,
                    HostIds = [host.Id],
                    Location = start + dir * ((z0 + z1) / 2),
                    Exceedance = z1 - z0,
                    Rule = RuleReference.Of(rule),
                    Discriminator = name,
                    GeometryApproximate = approxAxis,
                    Explanation = $"No stirrups or ties found in the {name} of {host.Id}; rule {rule.Id} requires transverse reinforcement at ≤ {rule.MaximumSpacing} ({rule.Source}).",
                });
                continue;
            }

            var maxGap = 0.0;
            Vec3 at = default;
            string[] pair = [];
            for (var i = 1; i < inZone.Length; i++)
            {
                var gap = inZone[i].T - inZone[i - 1].T;
                if (gap <= maxGap) continue;
                maxGap = gap;
                at = start + dir * ((inZone[i].T + inZone[i - 1].T) / 2);
                pair = [inZone[i - 1].Id, inZone[i].Id];
            }

            var max = rule.MaximumSpacing.Millimetres;
            if (maxGap <= max + _tol.ComplianceTolerance.Millimetres) continue;
            sink.Findings.Add(new Finding
            {
                Check = CheckKind.TransverseSpacing,
                Subtype = IssueSubtype.TransverseSpacingExceeded,
                Status = FindingStatus.Violation,
                Owner = owner,
                BarIds = FindingFactory.Sorted(pair),
                HostIds = [host.Id],
                Location = at,
                Measurements = [Measurement.Mm("maxSpacing", maxGap), Measurement.Mm("permittedSpacing", max)],
                Exceedance = maxGap - max,
                DecisionMargin = maxGap - max,
                Rule = RuleReference.Of(rule),
                Discriminator = name,
                GeometryApproximate = approxAxis,
                Notes = approxAxis ? ["Member axis taken as the longest bounding-box axis (no axis supplied)."] : [],
                Explanation = string.Create(CultureInfo.InvariantCulture,
                    $"Transverse reinforcement spacing reaches {maxGap:0} mm in the {name} of {host.Id}; rule {rule.Id} permits {max:0} mm ({rule.Source})."),
            });
        }
    }

    private static (Vec3 Start, Vec3 End, bool Approximate) LongestAxis(StructuralHost host)
    {
        var b = host.Bounds;
        var axis = b.LongestAxis;
        var c = b.Center;
        var half = b.Size[axis] / 2;
        var dir = axis == 0 ? Vec3.UnitX : axis == 1 ? Vec3.UnitY : Vec3.UnitZ;
        return (c - dir * half, c + dir * half, true);
    }

    internal static Length Mm(double v) => Length.FromMillimetres(v);
}
