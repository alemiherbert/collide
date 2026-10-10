using System.Collections.Concurrent;
using System.Diagnostics;
using Collide.Findings;
using Collide.Geometry;
using Collide.Model;
using Collide.Rules;
using Collide.Validation;
using Serilog;

namespace Collide.Analysis;

public sealed record AnalyzerOptions
{
    /// <summary>Degree of parallelism for the narrow phase (pure computation on immutable data). 1 = sequential.</summary>
    public int MaxDegreeOfParallelism { get; init; } = Environment.ProcessorCount;
}

/// <summary>
/// Runs all geometric detection and detailing validation for a scope of a prepared model. Never modifies the model.
/// Output order is deterministic (sorted by fingerprint) regardless of parallelism.
/// </summary>
public sealed class ModelAnalyzer
{
    private readonly IGeometryKernel _kernel;
    private readonly AnalyzerOptions _options;
    private readonly ILogger _log;

    public ModelAnalyzer(IGeometryKernel? kernel = null, AnalyzerOptions? options = null, ILogger? logger = null)
    {
        _kernel = kernel ?? new CapsuleGeometryKernel();
        _options = options ?? new AnalyzerOptions();
        _log = (logger ?? Serilog.Core.Logger.None).ForContext<ModelAnalyzer>();
    }

    public IGeometryKernel Kernel => _kernel;

    public PreparedModel Prepare(ReinforcementModel model, RuleSet rules, CancellationToken ct = default) => PreparedModel.Create(model, rules, _kernel, ct);

    public AnalysisResult Analyze(ReinforcementModel model, RuleSet rules, CancellationToken ct = default) =>
        Analyze(Prepare(model, rules, ct), AnalysisScope.All, ct);

    public AnalysisResult Analyze(PreparedModel pm, AnalysisScope scope, CancellationToken ct = default)
    {
        var total = Stopwatch.StartNew();
        var stages = new Dictionary<string, double>();
        var model = pm.Model;
        var focusBars = (scope.IsAll ? model.Bars.Keys : scope.Bars.Where(model.Bars.ContainsKey)).ToArray();
        var focusSet = scope.IsAll ? null : new HashSet<string>(focusBars, StringComparer.Ordinal);

        // Pairs, host checks and per-bar detailing: parallel over focus bars with thread-local sinks.
        var sw = Stopwatch.StartNew();
        var sinks = new ConcurrentBag<AnalysisSink>();
        var parallel = new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Max(1, _options.MaxDegreeOfParallelism) };
        Parallel.ForEach(
            focusBars,
            parallel,
            () => (Sink: new AnalysisSink(), Pairs: new PairAnalyzer(pm), Host: new HostAnalyzer(pm), Detail: new DetailingValidator(pm)),
            (barId, _, local) =>
            {
                var bar = model.Bars[barId];
                var box = pm.Solids[barId].Bounds.Inflate(pm.SearchMargin / 2);
                foreach (var otherId in pm.BarIndex.Query(box))
                {
                    if (otherId == barId) continue;
                    var otherInFocus = focusSet is null || focusSet.Contains(otherId);
                    // Each pair is evaluated once: by the ordinal-smaller bar when both are in focus.
                    if (otherInFocus && string.CompareOrdinal(otherId, barId) < 0) continue;
                    var (a, b) = string.CompareOrdinal(barId, otherId) < 0 ? (bar, model.Bars[otherId]) : (model.Bars[otherId], bar);
                    local.Pairs.Analyze(a, b, local.Sink);
                }

                AnalyzeAccessories(pm, bar, local.Sink);
                if (local.Detail.ValidateGeometry(bar, local.Sink))
                {
                    local.Host.Analyze(bar, local.Sink);
                    local.Detail.ValidateBendsAndHooks(bar, local.Sink);
                    local.Detail.ValidateAnchorage(bar, local.Sink);
                    local.Detail.ValidateRestrictions(bar, local.Sink);
                }

                return local;
            },
            local => sinks.Add(local.Sink));
        stages["bars"] = sw.Elapsed.TotalMilliseconds;

        var sink = new AnalysisSink();
        foreach (var s in sinks) sink.Merge(s);

        sw.Restart();
        var detail = new DetailingValidator(pm);
        var sets = scope.IsAll ? model.Sets.Values : scope.Sets.Where(model.Sets.ContainsKey).Select(id => model.Sets[id]);
        foreach (var set in sets)
        {
            ct.ThrowIfCancellationRequested();
            detail.ValidateSet(set, sink);
        }

        var marks = model.Bars.Values.Where(b => b.Mark is not null && (scope.IsAll || scope.Marks.Contains(b.Mark)))
            .GroupBy(b => b.Mark!, StringComparer.Ordinal);
        foreach (var group in marks) detail.ValidateMark(group.Key, group.ToArray(), sink);

        var congestion = new CongestionAnalyzer(pm);
        var hosts = scope.IsAll ? model.Hosts.Values : scope.Hosts.Where(model.Hosts.ContainsKey).Select(id => model.Hosts[id]);
        foreach (var host in hosts)
        {
            ct.ThrowIfCancellationRequested();
            congestion.Analyze(host, sink);
            detail.ValidateTransverse(host, sink);
        }

        stages["sets-hosts"] = sw.Elapsed.TotalMilliseconds;

        var (findings, suppressed) = ApplyExceptions(pm.Rules, sink.Findings, sink.Suppressed);
        findings = findings.Where(scope.Owns)
            .GroupBy(f => f.Fingerprint, StringComparer.Ordinal).Select(g => g.First())
            .OrderBy(f => f.Fingerprint, StringComparer.Ordinal).ToList();
        suppressed = suppressed.OrderBy(s => s.Fingerprint, StringComparer.Ordinal).ThenBy(s => s.RuleOrExceptionId, StringComparer.Ordinal).ToList();
        var laps = sink.Laps.Distinct().OrderBy(l => l.BarA, StringComparer.Ordinal).ThenBy(l => l.BarB, StringComparer.Ordinal).ToList();

        var result = new AnalysisResult
        {
            ModelRevision = model.Revision,
            Scope = scope,
            Findings = findings,
            Suppressed = suppressed,
            Laps = laps,
            ChecksNotPerformed = ChecksNotPerformed(pm.Rules),
            Statistics = new AnalysisStatistics
            {
                BarsInModel = model.Bars.Count,
                BarsAnalysed = focusBars.Length,
                SolidPrimitives = pm.Solids.Values.Sum(s => s.Segments.Count),
                CandidateBarPairs = sink.CandidatePairs,
                PrimitivePairsMeasured = sink.PrimitivePairs,
                FindingCount = findings.Count,
                ElapsedMilliseconds = total.Elapsed.TotalMilliseconds,
                StageMilliseconds = stages,
            },
        };
        _log.Debug("Analysed {Bars} of {Total} bars (revision {Revision}): {Findings} findings, {Pairs} candidate pairs in {Ms:0.0} ms",
            focusBars.Length, model.Bars.Count, model.Revision, findings.Count, sink.CandidatePairs, total.Elapsed.TotalMilliseconds);
        return result;
    }

    private static void AnalyzeAccessories(PreparedModel pm, RebarBar bar, AnalysisSink sink)
    {
        if (pm.AccessorySolids.Count == 0) return;
        var solid = pm.Solids[bar.Id];
        var ct = pm.Rules.Tolerances.ContactTolerance.Millimetres;
        foreach (var acc in pm.Model.Accessories.Values)
        {
            if (acc.AttachedBarIds.Contains(bar.Id)) continue;
            var accSolid = pm.AccessorySolids[acc.Id];
            if (!accSolid.Bounds.Intersects(solid.Bounds)) continue;
            SegmentProximity? worst = null;
            foreach (var s in solid.Segments)
            {
                var p = pm.Kernel.Measure(s, accSolid.Segments[0]);
                if (worst is null || p.SurfaceDistance < worst.Value.SurfaceDistance) worst = p;
            }

            if (worst is not { } w || w.SurfaceDistance >= -ct) continue;
            sink.Findings.Add(new Finding
            {
                Check = CheckKind.Clash,
                Subtype = IssueSubtype.AccessoryConflict,
                Status = FindingStatus.Violation,
                Owner = FindingOwner.Bars(bar.Id),
                BarIds = [bar.Id],
                HostIds = FindingFactory.Sorted([bar.HostId, acc.HostId]),
                Location = Vec3.Lerp(w.PointA, w.PointB, 0.5),
                Measurements = [Measurement.Mm("penetrationDepth", w.Penetration), new Measurement("accessory", 0, "id", acc.Id)],
                Exceedance = w.Penetration,
                DecisionMargin = w.Penetration - ct,
                GeometryApproximate = acc.GeometryApproximate,
                Discriminator = acc.Id,
                Contact = new ContactGeometry(bar.Id, acc.Id, w.PointA, w.PointB, (w.PointA - w.PointB).Normalized(), w.AngleRadians * 180 / Math.PI),
                Notes = acc.GeometryApproximate ? ["Accessory geometry is approximate (bounding cylinder)."] : [],
                Explanation = $"Bar {bar.Id} intersects {acc.Kind.ToString().ToLowerInvariant()} {acc.Id} (element {acc.ElementId}) by {w.Penetration:0.##} mm; the accessory is not attached to this bar.",
            });
        }
    }

    private static (List<Finding>, List<SuppressedFinding>) ApplyExceptions(RuleSet rules, List<Finding> findings, List<SuppressedFinding> suppressed)
    {
        if (rules.Exceptions.Count == 0) return (findings, suppressed);
        var kept = new List<Finding>(findings.Count);
        var supp = new List<SuppressedFinding>(suppressed);
        foreach (var f in findings)
        {
            var ex = rules.Exceptions.FirstOrDefault(e =>
                f.BarIds.Count > 0 && e.BarIds.All(f.BarIds.Contains) && (e.Subtypes is null || e.Subtypes.Contains(f.Subtype)));
            if (ex is null)
            {
                kept.Add(f);
                continue;
            }

            supp.Add(new SuppressedFinding(f.Fingerprint, f.Subtype, f.BarIds, $"Approved exception {ex.ApprovalReference}{(ex.ApprovedBy is null ? "" : $" by {ex.ApprovedBy}")}",
                ex.Id, f.Exceedance, f.Location));
        }

        return (kept, supp);
    }

    private static IReadOnlyList<string> ChecksNotPerformed(RuleSet rules)
    {
        var list = new List<string>();
        if (rules.Congestion.Count == 0) list.Add("Congestion: no congestion rule configured.");
        if (rules.MaxSpacing.Count == 0) list.Add("Maximum bar spacing: no rule configured.");
        if (rules.TransverseSpacing.Count == 0) list.Add("Transverse/confinement spacing: no rule configured.");
        if (rules.Restrictions.Count == 0) list.Add("Diameter/shape restrictions: no rule configured.");
        foreach (var check in new[] { CheckKind.Cover, CheckKind.ClearSpacing, CheckKind.Lap, CheckKind.Anchorage, CheckKind.Bend, CheckKind.Hook })
        {
            if (!rules.IsRequired(check)) list.Add($"{check}: not listed in requiredChecks; missing rules for it are not reported as indeterminate.");
        }

        return list;
    }
}
