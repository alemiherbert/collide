using Collide.Findings;

namespace Collide.Analysis;

/// <summary>
/// Immutable pairing of a prepared model with the complete set of raw findings for it. <see cref="Apply"/> re-analyses
/// only the scope affected by a change and replaces exactly the findings owned by that scope, so the result equals a
/// full re-analysis of the new model (verified by property tests).
/// </summary>
public sealed class DetectionState
{
    private DetectionState(PreparedModel prepared, IReadOnlyList<Finding> findings, IReadOnlyList<SuppressedFinding> suppressed, IReadOnlyList<LapPair> laps)
    {
        Prepared = prepared;
        Findings = findings;
        Suppressed = suppressed;
        Laps = laps;
    }

    public PreparedModel Prepared { get; }
    public IReadOnlyList<Finding> Findings { get; }
    public IReadOnlyList<SuppressedFinding> Suppressed { get; }
    public IReadOnlyList<LapPair> Laps { get; }

    public static DetectionState Initialize(PreparedModel prepared, ModelAnalyzer analyzer, CancellationToken ct = default)
    {
        var result = analyzer.Analyze(prepared, AnalysisScope.All, ct);
        return new DetectionState(prepared, result.Findings, result.Suppressed, result.Laps);
    }

    /// <summary>Recomputes the given scope on <paramref name="next"/> and merges the result.</summary>
    public (DetectionState State, AnalysisResult Partial) Recompute(PreparedModel next, AnalysisScope scope, ModelAnalyzer analyzer, CancellationToken ct = default)
    {
        var partial = analyzer.Analyze(next, scope, ct);
        var findings = Findings.Where(f => !scope.Owns(f)).Concat(partial.Findings)
            .OrderBy(f => f.Fingerprint, StringComparer.Ordinal).ToArray();
        var suppressed = Suppressed.Where(s => !s.BarIds.Any(scope.Bars.Contains)).Concat(partial.Suppressed)
            .OrderBy(s => s.Fingerprint, StringComparer.Ordinal).ThenBy(s => s.RuleOrExceptionId, StringComparer.Ordinal).ToArray();
        var laps = Laps.Where(l => !scope.Bars.Contains(l.BarA) && !scope.Bars.Contains(l.BarB)).Concat(partial.Laps)
            .Distinct().OrderBy(l => l.BarA, StringComparer.Ordinal).ThenBy(l => l.BarB, StringComparer.Ordinal).ToArray();
        if (scope.IsAll)
        {
            findings = partial.Findings.ToArray();
            suppressed = partial.Suppressed.ToArray();
            laps = partial.Laps.ToArray();
        }

        return (new DetectionState(next, findings, suppressed, laps), partial);
    }

    /// <summary>Applies changed bars/hosts: expands the scope over before and after models, then recomputes it.</summary>
    public (DetectionState State, AnalysisScope Scope) Apply(PreparedModel next, IEnumerable<string> changedBarIds, IEnumerable<string> changedHostIds,
        ModelAnalyzer analyzer, CancellationToken ct = default)
    {
        var scope = AnalysisScope.ForChanges(Prepared, next, changedBarIds, changedHostIds);
        return (Recompute(next, scope, analyzer, ct).State, scope);
    }
}
