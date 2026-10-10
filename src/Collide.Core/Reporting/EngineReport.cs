using Collide.Analysis;
using Collide.Audit;
using Collide.Findings;
using Collide.Geometry;
using Collide.Model;
using Collide.Resolution;
using Collide.Rules;

namespace Collide.Reporting;

public sealed record ReportModelInfo(string ModelId, long Revision, string ContentHash, int Bars, int Hosts, int Sets, int Accessories);

public sealed record ReportRulesInfo(string Name, string? Revision, string DesignStandard, string? StandardVersion, string Hash, int RuleCount);

public sealed record ReportSummary
{
    public int TotalIssues { get; init; }
    public IReadOnlyDictionary<string, int> ByCategory { get; init; } = new SortedDictionary<string, int>();
    public IReadOnlyDictionary<string, int> BySeverity { get; init; } = new SortedDictionary<string, int>();
    public IReadOnlyDictionary<string, int> ByStatus { get; init; } = new SortedDictionary<string, int>();
    public int ConfirmedDefects { get; init; }
    public int SuspectedDefects { get; init; }
    public int Warnings { get; init; }
    public int Indeterminate { get; init; }
    public int AutoResolved { get; init; }
    public int RequiringApproval { get; init; }
    public int Unresolved { get; init; }
    public int SuppressedContacts { get; init; }
}

public sealed record ReportCandidate(
    string CandidateId,
    string Strategy,
    string Description,
    double Score,
    double MaxDisplacementMm,
    IReadOnlyList<ReportTranslation> Translations,
    bool RequiresApproval,
    IReadOnlyList<string> ApprovalReasons,
    IReadOnlyList<InvariantResult> Invariants,
    IReadOnlyList<Measurement> Before,
    IReadOnlyList<Measurement> After);

public sealed record ReportTranslation(string BarId, Vec3 TranslationMm);

public sealed record ReportRejected(string CandidateId, string Description, IReadOnlyList<string> Reasons);

public sealed record ReportOutcome
{
    public required string IssueId { get; init; }
    public required string Subtype { get; init; }
    public required string Decision { get; init; }
    public required string Reason { get; init; }
    public ReportCandidate? Selected { get; init; }
    public IReadOnlyList<ReportCandidate> Alternatives { get; init; } = [];
    public IReadOnlyList<ReportRejected> Rejected { get; init; } = [];
    public string? ExecutionStatus { get; init; }
    public string? ExecutionReason { get; init; }
    public string? CorrectionId { get; init; }
}

public sealed record ReportResolution
{
    public required string RunId { get; init; }
    public required bool DryRun { get; init; }
    public required string Mode { get; init; }
    public IReadOnlyList<ReportOutcome> Outcomes { get; init; } = [];
    public IReadOnlyList<CorrectionRecord> Corrections { get; init; } = [];
    public int IssuesBefore { get; init; }
    public int IssuesAfter { get; init; }
}

public sealed record ReportPerformance
{
    public double AnalysisMilliseconds { get; init; }
    public int BarsInModel { get; init; }
    public int BarsAnalysed { get; init; }
    public int SolidPrimitives { get; init; }
    public long CandidateBarPairs { get; init; }
    public long PrimitivePairsMeasured { get; init; }
    public double BroadPhasePruning { get; init; }
    public double? ResolutionMilliseconds { get; init; }
    public IReadOnlyDictionary<string, double> StageMilliseconds { get; init; } = new Dictionary<string, double>();
}

/// <summary>Complete machine-readable report (serialised with <see cref="ReportWriter"/>).</summary>
public sealed record EngineReport
{
    public string SchemaVersion { get; init; } = "1.0";
    public required DateTimeOffset GeneratedAt { get; init; }
    public required string Tool { get; init; }
    public required ReportModelInfo Model { get; init; }
    public required ReportRulesInfo Rules { get; init; }
    public required string GeometryKernel { get; init; }
    public required ReportSummary Summary { get; init; }
    public required IReadOnlyList<Issue> Issues { get; init; }
    public IReadOnlyList<SuppressedFinding> Suppressed { get; init; } = [];
    public int SuppressedTotal { get; init; }
    public IReadOnlyList<string> ChecksNotPerformed { get; init; } = [];
    public IReadOnlyList<string> Limitations { get; init; } = [];
    public ReportResolution? Resolution { get; init; }
    public IReadOnlyList<AuditEntry> Audit { get; init; } = [];
    public required ReportPerformance Performance { get; init; }
}

public static class ReportBuilder
{
    public const string ToolName = "Collide rebar clash engine";
    private const int MaxSuppressedListed = 500;

    public static EngineReport Build(
        DetectionState state,
        AnalysisResult analysis,
        IReadOnlyList<Issue> issues,
        IClock? clock = null,
        ResolutionRunResult? resolution = null,
        IReadOnlyList<AuditEntry>? audit = null,
        IReadOnlyDictionary<string, CorrectionRecord>? ledger = null)
    {
        var model = state.Prepared.Model;
        var rules = state.Prepared.Rules;
        var reported = resolution?.IssuesBefore ?? issues;
        var outcomes = resolution?.Outcomes ?? [];
        var autoResolved = outcomes.Count(o => o.Execution?.Status == ExecutionStatus.Committed);
        var approval = outcomes.Count(o => o.Plan.Decision == ResolutionDecision.RequiresApproval);
        var unresolved = resolution is null ? issues.Count : resolution.IssuesAfter.Count;

        return new EngineReport
        {
            GeneratedAt = (clock ?? SystemClock.Instance).UtcNow,
            Tool = $"{ToolName} {typeof(ReportBuilder).Assembly.GetName().Version}",
            Model = new ReportModelInfo(model.ModelId, model.Revision, model.ContentHash(), model.Bars.Count, model.Hosts.Count, model.Sets.Count, model.Accessories.Count),
            Rules = new ReportRulesInfo(rules.Name, rules.Revision, rules.DesignStandard.Name, rules.DesignStandard.Version, RuleSetLoader.Hash(rules), rules.AllRules().Count()),
            GeometryKernel = state.Prepared.Kernel.Name,
            Summary = new ReportSummary
            {
                TotalIssues = reported.Count,
                ByCategory = Count(reported, i => i.Category.ToString()),
                BySeverity = Count(reported, i => i.Severity.ToString()),
                ByStatus = Count(reported, i => i.Status.ToString()),
                ConfirmedDefects = reported.Count(i => i.Status == IssueStatus.ConfirmedDefect),
                SuspectedDefects = reported.Count(i => i.Status == IssueStatus.SuspectedDefect),
                Warnings = reported.Count(i => i.Status == IssueStatus.Warning),
                Indeterminate = reported.Count(i => i.Status == IssueStatus.Indeterminate),
                AutoResolved = autoResolved,
                RequiringApproval = approval,
                Unresolved = unresolved,
                SuppressedContacts = analysis.Suppressed.Count,
            },
            Issues = reported,
            Suppressed = analysis.Suppressed.Take(MaxSuppressedListed).ToArray(),
            SuppressedTotal = analysis.Suppressed.Count,
            ChecksNotPerformed = analysis.ChecksNotPerformed,
            Limitations =
            [
                state.Prepared.Kernel.Limitations,
                "Host concrete is represented by convex solids; non-convex hosts supplied as bounding boxes are flagged as approximate and their findings are low confidence.",
                "Faces with unknown exposure are treated as concealed only where another host is found 1 mm beyond the face at the measurement point.",
                "Laps and anchorage targets may be inferred from geometry; such findings are reported as suspected, not confirmed.",
                "Automated corrections are limited to rigid translations within placement tolerances; rerouting, re-bending and lap relocation are proposed only as engineering actions.",
            ],
            Resolution = resolution is null ? null : new ReportResolution
            {
                RunId = resolution.RunId,
                DryRun = resolution.DryRun,
                Mode = rules.Resolution.Mode.ToString(),
                Outcomes = outcomes.Select(ToOutcome).ToArray(),
                Corrections = (ledger?.Values ?? []).OrderBy(c => c.AppliedAt).ThenBy(c => c.CorrectionId, StringComparer.Ordinal).ToArray(),
                IssuesBefore = resolution.IssuesBefore.Count,
                IssuesAfter = resolution.IssuesAfter.Count,
            },
            Audit = audit ?? [],
            Performance = new ReportPerformance
            {
                AnalysisMilliseconds = Math.Round(analysis.Statistics.ElapsedMilliseconds, 2),
                BarsInModel = analysis.Statistics.BarsInModel,
                BarsAnalysed = analysis.Statistics.BarsAnalysed,
                SolidPrimitives = analysis.Statistics.SolidPrimitives,
                CandidateBarPairs = analysis.Statistics.CandidateBarPairs,
                PrimitivePairsMeasured = analysis.Statistics.PrimitivePairsMeasured,
                BroadPhasePruning = Math.Round(analysis.Statistics.BroadPhasePruning, 6),
                ResolutionMilliseconds = resolution is null ? null : Math.Round(resolution.ElapsedMilliseconds, 2),
                StageMilliseconds = analysis.Statistics.StageMilliseconds.ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value, 2)),
            },
        };
    }

    private static ReportOutcome ToOutcome(ResolutionOutcome o) => new()
    {
        IssueId = o.Plan.IssueId,
        Subtype = o.Plan.Subtype.ToString(),
        Decision = o.Plan.Decision.ToString(),
        Reason = o.Plan.Reason,
        Selected = o.Plan.Selected is { } s ? ToCandidate(s) : null,
        Alternatives = o.Plan.Ranked.Where(r => !ReferenceEquals(r, o.Plan.Selected)).Select(ToCandidate).ToArray(),
        Rejected = o.Plan.Rejected.Select(r => new ReportRejected(r.Candidate.Id, r.Candidate.Description, r.RejectionReasons)).ToArray(),
        ExecutionStatus = o.Execution?.Status.ToString(),
        ExecutionReason = o.Execution?.Reason,
        CorrectionId = o.Execution?.Correction?.CorrectionId,
    };

    private static ReportCandidate ToCandidate(CandidateEvaluation e) => new(
        e.Candidate.Id,
        e.Candidate.Strategy.ToString(),
        e.Candidate.Description,
        Math.Round(e.Score, 6),
        Math.Round(e.Candidate.MaxDisplacementMm, 4),
        e.Candidate.Modifications.Select(m => new ReportTranslation(m.BarId, m.Translation)).ToArray(),
        e.RequiresApproval,
        e.ApprovalReasons,
        e.Invariants,
        e.Before,
        e.After);

    private static IReadOnlyDictionary<string, int> Count(IEnumerable<Issue> issues, Func<Issue, string> key) =>
        new SortedDictionary<string, int>(issues.GroupBy(key).ToDictionary(g => g.Key, g => g.Count()), StringComparer.Ordinal);
}
