using System.Globalization;
using Collide.Analysis;
using Collide.Findings;
using Collide.Model;
using Collide.Validation;

namespace Collide.Resolution;

/// <summary>
/// Validates a candidate (or an applied correction's actual post-state) by re-analysing the moved bars and every
/// neighbour/set/host whose findings could change, then evaluating the resolution safety invariants (Part 8).
/// </summary>
public sealed class ResolutionValidator
{
    private static readonly HashSet<CheckKind> InvariantChecks =
        [CheckKind.Cover, CheckKind.ClearSpacing, CheckKind.Lap, CheckKind.Anchorage, CheckKind.Containment, CheckKind.Clash];

    private readonly ModelAnalyzer _analyzer;

    public ResolutionValidator(ModelAnalyzer analyzer) => _analyzer = analyzer;

    public CandidateEvaluation Evaluate(CandidateCorrection candidate, Finding target, PreparedModel before, CancellationToken ct = default)
    {
        var rejections = new List<string>();
        var approvals = new List<string>();
        foreach (var mod in candidate.Modifications)
        {
            var bar = before.Model.FindBar(mod.BarId);
            if (bar is null)
            {
                rejections.Add($"Bar {mod.BarId} no longer exists.");
                continue;
            }

            if (bar.GeometryHash != mod.ExpectedBeforeHash) rejections.Add($"Bar {mod.BarId} changed since the candidate was generated.");
            if (Placement.Violation(before, bar, mod.Translation) is { } violation) rejections.Add(violation);
            if (Placement.RequiresApproval(before, bar, mod.Translation, out var reason)) approvals.Add(reason!);
        }

        if (rejections.Count > 0)
        {
            return new CandidateEvaluation
            {
                Candidate = candidate,
                Valid = false,
                Verifiable = false,
                RequiresApproval = true,
                RejectionReasons = rejections,
                ApprovalReasons = approvals,
                Before = target.Measurements,
            };
        }

        var afterBars = candidate.Modifications.Select(m => before.Bar(m.BarId).Translate(m.Translation)).ToArray();
        return EvaluateState(candidate, target, before, afterBars, approvals, ct);
    }

    /// <summary>
    /// Evaluates invariants for a given post-state of the moved bars (used for planning and, with the bars read back from
    /// the host model, for post-application validation).
    /// </summary>
    public CandidateEvaluation EvaluateState(CandidateCorrection candidate, Finding target, PreparedModel before, IReadOnlyList<RebarBar> afterBars,
        IReadOnlyList<string> approvalReasons, CancellationToken ct = default)
    {
        var movedIds = afterBars.Select(b => b.Id).ToHashSet(StringComparer.Ordinal);
        var after = before.WithModel(before.Model.WithBars(afterBars), ct);
        var scope = AnalysisScope.ForChanges(before, after, movedIds, []);
        var beforeResult = _analyzer.Analyze(before, scope, ct);
        var afterResult = _analyzer.Analyze(after, scope, ct);
        var beforeByFp = beforeResult.Findings.ToDictionary(f => f.Fingerprint, StringComparer.Ordinal);
        var afterByFp = afterResult.Findings.ToDictionary(f => f.Fingerprint, StringComparer.Ordinal);

        var newFindings = afterResult.Findings.Where(f => !beforeByFp.ContainsKey(f.Fingerprint) && f.Status != FindingStatus.Indeterminate).ToArray();
        var worsened = afterResult.Findings.Where(f => beforeByFp.TryGetValue(f.Fingerprint, out var b) && f.Exceedance > b.Exceedance + 1e-6).ToArray();
        var unverifiable = afterResult.Findings.Where(f => f.Status == FindingStatus.Indeterminate && InvariantChecks.Contains(f.Check) && f.BarIds.Any(movedIds.Contains)).ToArray();
        var targetResolved = !afterByFp.ContainsKey(target.Fingerprint);

        string Describe(IEnumerable<Finding> fs) => string.Join("; ", fs.Select(f => $"{f.Subtype} [{string.Join(",", f.BarIds)}]"));
        IEnumerable<Finding> Of(IEnumerable<Finding> fs, params CheckKind[] checks) => fs.Where(f => checks.Contains(f.Check));
        InvariantResult NoRegression(string id, string name, params CheckKind[] checks)
        {
            var bad = Of(newFindings, checks).Concat(Of(worsened, checks)).ToArray();
            return new InvariantResult(id, name, bad.Length == 0, bad.Length == 0 ? "No new or worsened findings." : "Regressions: " + Describe(bad));
        }

        var invariants = new List<InvariantResult>
        {
            new("INV-01", "Target issue resolved", targetResolved, targetResolved ? "Target finding absent after the change." : "Target finding still present."),
            NoRegression("INV-02", "No newly introduced prohibited clashes or containment problems", CheckKind.Clash, CheckKind.Duplicate, CheckKind.Containment, CheckKind.Openings, CheckKind.Zones),
            QuantitiesPreserved(before, after, afterBars),
            NoRegression("INV-04", "Concrete cover remains compliant", CheckKind.Cover),
            NoRegression("INV-05", "Minimum clear spacing remains compliant", CheckKind.ClearSpacing),
            AnchorageAndLaps(before, after, beforeResult, afterResult, movedIds, newFindings, worsened),
            NoRegression("INV-07", "Reinforcement remains within permitted placement regions", CheckKind.Containment, CheckKind.Openings, CheckKind.Zones),
            ValidGeometry(after, afterBars),
            UnaffectedUnchanged(before, after, movedIds),
            Reversible(candidate, before, afterBars),
            NoRegression("INV-11", "No other new or worsened findings", Enum.GetValues<CheckKind>()),
        };

        var rejections = invariants.Where(i => !i.Passed).Select(i => $"{i.Id} {i.Name}: {i.Evidence}").ToList();
        var approvals = approvalReasons.ToList();
        if (unverifiable.Length > 0)
            approvals.Add("Some invariant checks are indeterminate for the moved bars (missing rules/inputs): " + Describe(unverifiable));
        var valid = rejections.Count == 0;

        return new CandidateEvaluation
        {
            Candidate = candidate,
            Valid = valid,
            Verifiable = unverifiable.Length == 0,
            RequiresApproval = approvals.Count > 0,
            RejectionReasons = rejections,
            ApprovalReasons = approvals,
            Invariants = invariants,
            NewFindings = newFindings.Select(f => $"{f.Subtype} [{string.Join(",", f.BarIds)}]: {f.Explanation}").ToArray(),
            WorsenedFindings = worsened.Select(f => $"{f.Subtype} [{string.Join(",", f.BarIds)}]: exceedance {beforeByFp[f.Fingerprint].Exceedance:0.###} → {f.Exceedance:0.###}").ToArray(),
            Before = target.Measurements,
            After = AfterMeasurements(target, after, afterByFp),
            Score = valid ? Score(candidate, before) : double.PositiveInfinity,
            CheckedBarIds = scope.Bars.ToArray(),
        };
    }

    private static IReadOnlyList<Measurement> AfterMeasurements(Finding target, PreparedModel after, Dictionary<string, Finding> afterByFp)
    {
        if (afterByFp.TryGetValue(target.Fingerprint, out var still)) return still.Measurements;
        if (target.Contact is { } c && after.Solids.TryGetValue(c.FirstBarId, out var a) && after.Solids.TryGetValue(c.SecondBarId, out var b))
        {
            var min = double.PositiveInfinity;
            foreach (var sa in a.Segments)
            foreach (var sb in b.Segments)
                min = Math.Min(min, after.Kernel.Measure(sa, sb).SurfaceDistance);
            return [Measurement.Mm("surfaceDistance", min, "after correction")];
        }

        return [new Measurement("targetFindingPresent", 0, "bool", "after correction")];
    }

    private static InvariantResult QuantitiesPreserved(PreparedModel before, PreparedModel after, IReadOnlyList<RebarBar> afterBars)
    {
        var problems = new List<string>();
        if (before.Model.Bars.Count != after.Model.Bars.Count) problems.Add($"bar count {before.Model.Bars.Count} → {after.Model.Bars.Count}");
        foreach (var b in afterBars)
        {
            var original = before.Bar(b.Id);
            if (original.NominalDiameter != b.NominalDiameter || original.PhysicalDiameter != b.PhysicalDiameter) problems.Add($"diameter of {b.Id} changed");
            if (Math.Abs(original.Path.Length - b.Path.Length) > 1e-6) problems.Add($"length of {b.Id} changed");
            if (original.Role != b.Role || original.SetId != b.SetId || original.Mark != b.Mark) problems.Add($"identity data of {b.Id} changed");
        }

        foreach (var set in before.Model.Sets.Values)
        {
            var present = set.BarIds.Count(after.Model.Bars.ContainsKey);
            if (present != set.BarIds.Count(before.Model.Bars.ContainsKey)) problems.Add($"set {set.Id} bar count changed");
        }

        return new InvariantResult("INV-03", "Reinforcement quantities and bar diameters preserved", problems.Count == 0,
            problems.Count == 0 ? $"{before.Model.Bars.Count} bars; diameters and lengths of {afterBars.Count} moved bar(s) unchanged." : string.Join("; ", problems));
    }

    private static InvariantResult AnchorageAndLaps(PreparedModel before, PreparedModel after, AnalysisResult beforeResult, AnalysisResult afterResult,
        HashSet<string> movedIds, IReadOnlyList<Finding> newFindings, IReadOnlyList<Finding> worsened)
    {
        var problems = new List<string>();
        problems.AddRange(newFindings.Concat(worsened).Where(f => f.Check is CheckKind.Lap or CheckKind.Anchorage).Select(f => $"{f.Subtype} [{string.Join(",", f.BarIds)}]"));
        foreach (var lap in beforeResult.Laps.Where(l => movedIds.Contains(l.BarA) || movedIds.Contains(l.BarB)))
        {
            var kept = afterResult.Laps.FirstOrDefault(l => l.BarA == lap.BarA && l.BarB == lap.BarB);
            if (kept is null) problems.Add($"lap {lap.BarA}/{lap.BarB} no longer exists");
            else if (kept.OverlapLengthMm < lap.OverlapLengthMm - 1e-6) problems.Add($"lap {lap.BarA}/{lap.BarB} shortened {lap.OverlapLengthMm:0.#} → {kept.OverlapLengthMm:0.#} mm");
        }

        foreach (var id in movedIds)
        {
            var b0 = before.Bar(id);
            var b1 = after.Bar(id);
            foreach (var (req, _) in DetailingValidator.AnchorageRequirements(before, b0))
            {
                var host = before.Model.FindHost(req.TargetHostId);
                if (host is null) continue;
                var p0 = DetailingValidator.ProvidedEmbedment(before, b0, req.End, host);
                var p1 = DetailingValidator.ProvidedEmbedment(after, b1, req.End, host);
                if (p1 < p0 - 1e-6) problems.Add(string.Create(CultureInfo.InvariantCulture, $"anchorage of {id} ({req.End}) into {host.Id} reduced {p0:0.#} → {p1:0.#} mm"));
            }
        }

        return new InvariantResult("INV-06", "Anchorage and lap requirements remain satisfied", problems.Count == 0,
            problems.Count == 0 ? "Laps preserved with no shortening; anchorage embedment not reduced; no new lap/anchorage findings." : string.Join("; ", problems));
    }

    private static InvariantResult ValidGeometry(PreparedModel after, IReadOnlyList<RebarBar> afterBars)
    {
        var tol = after.Rules.Tolerances.ContinuityTolerance.Millimetres;
        var problems = afterBars.SelectMany(b => b.Path.Validate(tol).Select(p => $"{b.Id}: {p}")).ToArray();
        return new InvariantResult("INV-08", "All modified bars retain valid geometry", problems.Length == 0,
            problems.Length == 0 ? "Paths continuous and well-formed." : string.Join("; ", problems));
    }

    private static InvariantResult UnaffectedUnchanged(PreparedModel before, PreparedModel after, HashSet<string> movedIds)
    {
        var changed = after.Model.Bars.Values.Where(b => !movedIds.Contains(b.Id) &&
            (!before.Model.Bars.TryGetValue(b.Id, out var o) || (!ReferenceEquals(o, b) && o.GeometryHash != b.GeometryHash))).Select(b => b.Id).ToArray();
        return new InvariantResult("INV-09", "Unaffected reinforcement remains unchanged", changed.Length == 0,
            changed.Length == 0 ? $"{after.Model.Bars.Count - movedIds.Count} other bar(s) unchanged." : "Changed: " + string.Join(", ", changed));
    }

    private static InvariantResult Reversible(CandidateCorrection candidate, PreparedModel before, IReadOnlyList<RebarBar> afterBars)
    {
        var problems = new List<string>();
        foreach (var bar in afterBars)
        {
            var mod = candidate.Modifications.First(m => m.BarId == bar.Id);
            var restored = bar.Translate(mod.Inverse().Translation);
            if (restored.GeometryHash != before.Bar(bar.Id).GeometryHash)
            {
                // Hashes are quantised at 1e-6 mm; accept a sub-micron round-trip difference.
                var dev = restored.Path.Tessellate(1).Zip(before.Bar(bar.Id).Path.Tessellate(1), (x, y) => Math.Max(Geometry.Vec3.Distance(x.A, y.A), Geometry.Vec3.Distance(x.B, y.B))).DefaultIfEmpty().Max();
                if (dev > 1e-6) problems.Add($"{bar.Id} does not round-trip (deviation {dev:0.######} mm)");
            }
        }

        return new InvariantResult("INV-10", "Every modification can be reverted", problems.Count == 0,
            problems.Count == 0 ? "Inverse translations restore the original geometry." : string.Join("; ", problems));
    }

    /// <summary>Lower is better: normalised displacement, number of bars moved, move preference by role, then smaller diameter.</summary>
    private static double Score(CandidateCorrection candidate, PreparedModel pm)
    {
        var maxTotal = 0.0;
        var preference = pm.Rules.Resolution.MovePreference;
        var priority = 0.0;
        var diameter = 0.0;
        foreach (var m in candidate.Modifications)
        {
            var bar = pm.Bar(m.BarId);
            maxTotal += Placement.RuleFor(pm, bar)?.MaximumDisplacement.Millimetres ?? 1;
            var idx = preference.ToList().IndexOf(bar.Role);
            priority += (idx < 0 ? preference.Count : idx) / (double)Math.Max(1, preference.Count);
            diameter += bar.NominalDiameter.Millimetres / 100.0; // prefer moving the smaller bar
        }

        return candidate.TotalDisplacementMm / Math.Max(1e-9, maxTotal)
               + 0.15 * (candidate.Modifications.Count - 1)
               + 0.10 * priority
               + 0.05 * diameter;
    }
}
