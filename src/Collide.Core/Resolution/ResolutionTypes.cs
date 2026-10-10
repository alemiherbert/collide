using Collide.Findings;
using Collide.Geometry;
using Collide.Model;

namespace Collide.Resolution;

public enum ResolutionStrategy
{
    /// <summary>Translate one bar within its placement tolerance.</summary>
    TranslateSingle,

    /// <summary>Translate both bars of a pair by half the required separation each.</summary>
    TranslateBoth,

    /// <summary>Shift a bar along its set's distribution direction and taper the shift over neighbouring bars, preserving the bar count.</summary>
    LocalRespacing,
}

/// <summary>A candidate correction: a set of rigid bar translations. Never deletes bars or changes diameters or lengths.</summary>
public sealed record CandidateCorrection
{
    public required string Id { get; init; }
    public required string IssueId { get; init; }
    public required ResolutionStrategy Strategy { get; init; }
    public required string Description { get; init; }
    public required IReadOnlyList<BarModification> Modifications { get; init; }
    public required Vec3 Direction { get; init; }

    public double TotalDisplacementMm => Modifications.Sum(m => m.Translation.Length);
    public double MaxDisplacementMm => Modifications.Count == 0 ? 0 : Modifications.Max(m => m.Translation.Length);
    public IEnumerable<string> BarIds => Modifications.Select(m => m.BarId);
}

/// <summary>Result of one named safety invariant (Part 8) with its evidence.</summary>
public sealed record InvariantResult(string Id, string Name, bool Passed, string Evidence);

public sealed record CandidateEvaluation
{
    public required CandidateCorrection Candidate { get; init; }

    /// <summary>All hard constraints and invariants hold.</summary>
    public required bool Valid { get; init; }

    /// <summary>Every check relevant to the invariants was determinate (no missing rules/inputs).</summary>
    public required bool Verifiable { get; init; }

    public required bool RequiresApproval { get; init; }
    public IReadOnlyList<string> RejectionReasons { get; init; } = [];
    public IReadOnlyList<string> ApprovalReasons { get; init; } = [];
    public IReadOnlyList<InvariantResult> Invariants { get; init; } = [];
    public IReadOnlyList<string> NewFindings { get; init; } = [];
    public IReadOnlyList<string> WorsenedFindings { get; init; } = [];
    public IReadOnlyList<Measurement> Before { get; init; } = [];
    public IReadOnlyList<Measurement> After { get; init; } = [];
    public double Score { get; init; } = double.PositiveInfinity;

    /// <summary>Bars re-analysed for this candidate (moved bars and neighbours).</summary>
    public IReadOnlyList<string> CheckedBarIds { get; init; } = [];
}

public enum ResolutionDecision
{
    AutoApply,
    RequiresApproval,
    NoSafeResolution,
    NotEligible,
    AlreadyResolved,
}

public sealed record ResolutionPlan
{
    public required string IssueId { get; init; }
    public required IssueSubtype Subtype { get; init; }
    public required ResolutionDecision Decision { get; init; }
    public required string Reason { get; init; }

    /// <summary>Valid candidates, best first (ranked alternatives for engineering approval).</summary>
    public IReadOnlyList<CandidateEvaluation> Ranked { get; init; } = [];

    public IReadOnlyList<CandidateEvaluation> Rejected { get; init; } = [];
    public CandidateEvaluation? Selected { get; init; }
}

public enum ExecutionStatus
{
    Committed,
    RolledBack,
    StalePrecondition,
    AlreadyApplied,
    Reverted,
}

/// <summary>Everything needed to trace and revert an applied correction.</summary>
public sealed record CorrectionRecord
{
    public required string CorrectionId { get; init; }
    public required string IssueId { get; init; }
    public required string CandidateId { get; init; }
    public required string RunId { get; init; }
    public required ResolutionStrategy Strategy { get; init; }
    public required IReadOnlyList<BarModification> Modifications { get; init; }
    public required IReadOnlyList<BarModification> Inverse { get; init; }
    public required DateTimeOffset AppliedAt { get; init; }
    public string? ApprovedBy { get; init; }
    public IReadOnlyList<InvariantResult> PostValidation { get; init; } = [];
    public IReadOnlyList<Measurement> Before { get; init; } = [];
    public IReadOnlyList<Measurement> After { get; init; } = [];
    public ExecutionStatus Status { get; init; } = ExecutionStatus.Committed;
}

public sealed record ExecutionResult
{
    public required ExecutionStatus Status { get; init; }
    public required string Reason { get; init; }
    public CorrectionRecord? Correction { get; init; }

    /// <summary>Actual post-state of the moved bars as read back from the host model (committed only).</summary>
    public IReadOnlyList<RebarBar> ActualBars { get; init; } = [];

    public IReadOnlyList<InvariantResult> PostValidation { get; init; } = [];
}

public sealed record ResolutionOutcome
{
    public required ResolutionPlan Plan { get; init; }
    public ExecutionResult? Execution { get; init; }
}
