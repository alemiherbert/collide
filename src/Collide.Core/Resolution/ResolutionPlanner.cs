using Collide.Analysis;
using Collide.Classification;
using Collide.Findings;
using Collide.Rules;

namespace Collide.Resolution;

/// <summary>
/// Plans a correction for one issue: generates candidates, validates each against hard constraints and invariants,
/// ranks the valid ones and decides. Automatic application is chosen only when the policy allows it and the best
/// candidate is valid, verifiable, within auto limits and unambiguous; otherwise ranked alternatives go to approval.
/// </summary>
public sealed class ResolutionPlanner
{
    private readonly ResolutionValidator _validator;

    public ResolutionPlanner(ResolutionValidator validator) => _validator = validator;

    public ResolutionPlan Plan(Issue issue, DetectionState state, CancellationToken ct = default)
    {
        var pm = state.Prepared;
        var policy = pm.Rules.Resolution;
        var targets = state.Findings.Where(f => issue.FindingFingerprints.Contains(f.Fingerprint)).ToArray();
        if (targets.Length == 0)
            return new ResolutionPlan { IssueId = issue.Id, Subtype = issue.Subtype, Decision = ResolutionDecision.AlreadyResolved, Reason = "The issue is no longer present in the current model." };
        if (!DefectClassifier.ResolvableSubtypes.Contains(issue.Subtype) || !issue.AutoResolutionPermitted)
            return new ResolutionPlan { IssueId = issue.Id, Subtype = issue.Subtype, Decision = ResolutionDecision.NotEligible, Reason = issue.AutoResolutionReason };

        var target = targets[0];
        var candidates = new CandidateGenerator(pm).Generate(issue, target);
        if (candidates.Count == 0)
        {
            return new ResolutionPlan
            {
                IssueId = issue.Id,
                Subtype = issue.Subtype,
                Decision = ResolutionDecision.NoSafeResolution,
                Reason = "No permitted modification can reach the required separation/cover within the placement tolerances of the involved bars. Model left unchanged; engineering review required.",
            };
        }

        var evaluations = candidates.Select(c => _validator.Evaluate(c, target, pm, ct)).ToArray();
        var ranked = evaluations.Where(e => e.Valid).OrderBy(e => e.Score).ThenBy(e => e.Candidate.Id, StringComparer.Ordinal).ToArray();
        var rejected = evaluations.Where(e => !e.Valid).OrderBy(e => e.Candidate.Id, StringComparer.Ordinal).ToArray();
        if (ranked.Length == 0)
        {
            var reasons = rejected.SelectMany(r => r.RejectionReasons).Distinct().Take(6);
            return new ResolutionPlan
            {
                IssueId = issue.Id,
                Subtype = issue.Subtype,
                Decision = ResolutionDecision.NoSafeResolution,
                Reason = $"All {rejected.Length} candidate(s) violate hard constraints or invariants: {string.Join(" | ", reasons)}. Model left unchanged; engineering review required.",
                Rejected = rejected,
            };
        }

        var best = ranked[0];
        var approval = new List<string>(best.ApprovalReasons);
        if (policy.Mode != ResolutionMode.ApplyUnambiguous) approval.Add($"Resolution policy is {policy.Mode}: automatic modification is not permitted.");
        if (policy.AutoApplySubtypes is { } allowed && !allowed.Contains(issue.Subtype)) approval.Add($"{issue.Subtype} is not in the policy's autoApplySubtypes.");
        if (ranked.Length > 1 && ranked[1].Score - best.Score < policy.AmbiguityMargin && !Equivalent(best.Candidate, ranked[1].Candidate))
            approval.Add($"Ambiguous: {ranked[1].Candidate.Id} scores within {policy.AmbiguityMargin} of {best.Candidate.Id} ({best.Score:0.####} vs {ranked[1].Score:0.####}).");

        return new ResolutionPlan
        {
            IssueId = issue.Id,
            Subtype = issue.Subtype,
            Decision = approval.Count == 0 ? ResolutionDecision.AutoApply : ResolutionDecision.RequiresApproval,
            Reason = approval.Count == 0
                ? $"{best.Candidate.Description} Valid, verifiable and unambiguous ({ranked.Length} valid, {rejected.Length} rejected)."
                : string.Join(" ", approval),
            Ranked = ranked,
            Rejected = rejected,
            Selected = best,
        };
    }

    private static bool Equivalent(CandidateCorrection a, CandidateCorrection b) =>
        a.Modifications.Count == b.Modifications.Count &&
        a.Modifications.Zip(b.Modifications).All(p => p.First.BarId == p.Second.BarId && (p.First.Translation - p.Second.Translation).Length < 1e-6);
}
