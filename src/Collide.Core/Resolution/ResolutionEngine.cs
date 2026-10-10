using System.Diagnostics;
using Collide.Analysis;
using Collide.Audit;
using Collide.Classification;
using Collide.Findings;
using Collide.Model;
using Collide.Rules;
using Serilog;

namespace Collide.Resolution;

public sealed record ResolutionOptions
{
    /// <summary>Plan and validate only; the model editor is never called.</summary>
    public bool DryRun { get; init; }

    /// <summary>Restrict the run to these issue ids (null = all).</summary>
    public IReadOnlyCollection<string>? IssueIds { get; init; }

    /// <summary>Explicit run id; defaults to a hash of the model content, rules and options (reproducible).</summary>
    public string? RunId { get; init; }
}

public sealed record ResolutionRunResult
{
    public required string RunId { get; init; }
    public required bool DryRun { get; init; }
    public required IReadOnlyList<Issue> IssuesBefore { get; init; }
    public required IReadOnlyList<Issue> IssuesAfter { get; init; }
    public required IReadOnlyList<ResolutionOutcome> Outcomes { get; init; }
    public required DetectionState FinalState { get; init; }
    public double ElapsedMilliseconds { get; init; }
}

/// <summary>Orchestrates planning, validation, (optional) execution and state refresh across all eligible issues.</summary>
public sealed class ResolutionEngine
{
    private readonly ModelAnalyzer _analyzer;
    private readonly DefectClassifier _classifier = new();
    private readonly ResolutionPlanner _planner;
    private readonly AuditLog _audit;
    private readonly ILogger _log;

    public ResolutionEngine(ModelAnalyzer analyzer, AuditLog audit, IClock? clock = null, ILogger? logger = null)
    {
        _analyzer = analyzer;
        _audit = audit;
        var validator = new ResolutionValidator(analyzer);
        _planner = new ResolutionPlanner(validator);
        Executor = new ResolutionExecutor(validator, audit, clock);
        _log = (logger ?? Serilog.Core.Logger.None).ForContext<ResolutionEngine>();
    }

    public ResolutionExecutor Executor { get; }
    public AuditLog Audit => _audit;

    public ResolutionPlan Plan(Issue issue, DetectionState state, CancellationToken ct = default) => _planner.Plan(issue, state, ct);

    public IReadOnlyList<Issue> Classify(DetectionState state) => _classifier.Classify(state.Findings, state.Prepared.Model, state.Prepared.Rules);

    public ResolutionRunResult Run(DetectionState state, IModelEditor? editor, ResolutionOptions? options = null, CancellationToken ct = default)
    {
        options ??= new ResolutionOptions();
        var sw = Stopwatch.StartNew();
        var rules = state.Prepared.Rules;
        var runId = options.RunId ?? "RUN-" + Finding.ShortHash($"{state.Prepared.Model.ContentHash()}|{RuleSetLoader.Hash(rules)}|{options.DryRun}|{string.Join(",", options.IssueIds ?? [])}");
        var before = Classify(state);
        _audit.Record(runId, AuditAction.RunStarted, $"Resolution run over {before.Count} issue(s); mode {rules.Resolution.Mode}{(options.DryRun ? ", dry run" : "")}.");

        var outcomes = new List<ResolutionOutcome>();
        foreach (var issue in before)
        {
            ct.ThrowIfCancellationRequested();
            if (options.IssueIds is not null && !options.IssueIds.Contains(issue.Id)) continue;
            if (!DefectClassifier.ResolvableSubtypes.Contains(issue.Subtype)) continue;

            var plan = _planner.Plan(issue, state, ct);
            foreach (var r in plan.Rejected)
                _audit.Record(runId, AuditAction.CandidateRejected, string.Join(" | ", r.RejectionReasons), issue.Id, r.Candidate.Id, barIds: r.Candidate.BarIds);
            foreach (var v in plan.Ranked)
                _audit.Record(runId, AuditAction.CandidateValid, $"{v.Candidate.Description} (score {v.Score:0.####})", issue.Id, v.Candidate.Id, barIds: v.Candidate.BarIds);
            _audit.Record(runId, plan.Decision == ResolutionDecision.AlreadyResolved ? AuditAction.IssueSkipped : AuditAction.DecisionMade,
                $"{plan.Decision}: {plan.Reason}", issue.Id, plan.Selected?.Candidate.Id);

            ExecutionResult? execution = null;
            if (plan.Decision == ResolutionDecision.AutoApply && !options.DryRun && editor is not null)
            {
                var target = state.Findings.First(f => issue.FindingFingerprints.Contains(f.Fingerprint));
                execution = Executor.Execute(plan.Selected!, target, state.Prepared, editor, runId, ct: ct);
                if (execution.Status == ExecutionStatus.Committed) state = Refresh(state, execution.ActualBars, ct);
            }

            outcomes.Add(new ResolutionOutcome { Plan = plan, Execution = execution });
        }

        // Detection-time eligibility is provisional; the plan's outcome is final. Issues the run could not correct
        // automatically are marked for engineering review with the planner's reason.
        var decided = outcomes.Where(o => o.Plan.Decision is ResolutionDecision.NoSafeResolution or ResolutionDecision.RequiresApproval)
            .ToDictionary(o => o.Plan.IssueId, o => o.Plan, StringComparer.Ordinal);
        var after = Classify(state).Select(i => decided.TryGetValue(i.Id, out var plan)
            ? i with { RequiresEngineeringReview = true, AutoResolutionReason = $"{plan.Decision}: {plan.Reason}" }
            : i).ToArray();
        _audit.Record(runId, AuditAction.RunCompleted,
            $"{outcomes.Count(o => o.Execution?.Status == ExecutionStatus.Committed)} correction(s) committed; {after.Length} issue(s) remain.");
        _log.Information("Resolution run {RunId}: {Before} issues before, {After} after", runId, before.Count, after.Length);
        return new ResolutionRunResult
        {
            RunId = runId,
            DryRun = options.DryRun,
            IssuesBefore = before,
            IssuesAfter = after,
            Outcomes = outcomes,
            FinalState = state,
            ElapsedMilliseconds = sw.Elapsed.TotalMilliseconds,
        };
    }

    /// <summary>
    /// Applies a specific candidate chosen by an engineer. The issue is re-planned against the current state so the
    /// approval can never apply a stale or no-longer-valid candidate.
    /// </summary>
    public (ExecutionResult Result, DetectionState State) ApplyApproved(DetectionState state, string issueId, string candidateId, string approvedBy,
        IModelEditor editor, string? runId = null, CancellationToken ct = default)
    {
        runId ??= "APPROVAL-" + Finding.ShortHash($"{issueId}|{candidateId}|{approvedBy}|{state.Prepared.Model.ContentHash()}");
        var issue = Classify(state).FirstOrDefault(i => i.Id == issueId);
        if (issue is null)
            return (new ExecutionResult { Status = ExecutionStatus.StalePrecondition, Reason = $"Issue {issueId} is not present in the current model." }, state);
        var plan = _planner.Plan(issue, state, ct);
        var chosen = plan.Ranked.FirstOrDefault(r => r.Candidate.Id == candidateId);
        if (chosen is null)
        {
            _audit.Record(runId, AuditAction.StalePrecondition, $"Candidate {candidateId} is not a valid candidate in the current model; nothing applied.", issueId, candidateId);
            return (new ExecutionResult { Status = ExecutionStatus.StalePrecondition, Reason = $"Candidate {candidateId} is no longer valid." }, state);
        }

        var target = state.Findings.First(f => issue.FindingFingerprints.Contains(f.Fingerprint));
        var result = Executor.Execute(chosen, target, state.Prepared, editor, runId, approvedBy, ct);
        return (result, result.Status == ExecutionStatus.Committed ? Refresh(state, result.ActualBars, ct) : state);
    }

    private DetectionState Refresh(DetectionState state, IReadOnlyList<RebarBar> actualBars, CancellationToken ct)
    {
        var next = state.Prepared.WithModel(state.Prepared.Model.WithBars(actualBars), ct);
        return state.Apply(next, actualBars.Select(b => b.Id), [], _analyzer, ct).State;
    }
}
