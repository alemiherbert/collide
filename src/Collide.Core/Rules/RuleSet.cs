using Collide.Findings;
using Collide.Model;
using Collide.Units;

namespace Collide.Rules;

public sealed record DesignStandardInfo
{
    public required string Name { get; init; }
    public string? Version { get; init; }
    public string? Notes { get; init; }
}

/// <summary>Project-tunable numerical tolerances. These are not design-code values.</summary>
public sealed record ToleranceSettings
{
    /// <summary>Surfaces within ±this distance are "in contact"; deeper penetration is a clash.</summary>
    public Length ContactTolerance { get; init; } = Length.FromMillimetres(0.5);

    /// <summary>Provided ≥ required − this is compliant (absorbs unit-conversion noise).</summary>
    public Length ComplianceTolerance { get; init; } = Length.FromMillimetres(0.01);

    public Length DuplicateTolerance { get; init; } = Length.FromMillimetres(1);
    public Angle ParallelAngleTolerance { get; init; } = Angle.FromDegrees(2);
    public Length ArcChordTolerance { get; init; } = Length.FromMillimetres(0.1);

    /// <summary>Parallel bars must run alongside each other at least this far before clear spacing is checked.</summary>
    public Length MinimumParallelOverlap { get; init; } = Length.FromMillimetres(50);

    /// <summary>Lap inference: maximum centre-to-centre offset as a multiple of the larger nominal diameter.</summary>
    public double LapInferenceMaxOffsetFactor { get; init; } = 4.0;

    /// <summary>Hard clashes shallower than this are reported at reduced severity (modelling imprecision).</summary>
    public Length MinorPenetration { get; init; } = Length.FromMillimetres(2);

    public Length InterfaceProbeOffset { get; init; } = Length.FromMillimetres(1);
    public Length ContinuityTolerance { get; init; } = Length.FromMillimetres(0.1);
    public Length MinimumReportableLength { get; init; } = Length.FromMillimetres(1);
    public bool InferAnchorage { get; init; } = true;
    public bool InferLaps { get; init; } = true;
}

public sealed record ProjectParameters
{
    public Length? MaximumAggregateSize { get; init; }
    public string? ConcreteGrade { get; init; }
}

public enum ResolutionMode
{
    /// <summary>Never generate corrections.</summary>
    DetectOnly,

    /// <summary>Generate and validate candidates; never modify the model (the safe default).</summary>
    ProposeOnly,

    /// <summary>Apply a candidate automatically only when it is valid, verifiable, unambiguous and within auto limits.</summary>
    ApplyUnambiguous,
}

public sealed record ResolutionPolicy
{
    public ResolutionMode Mode { get; init; } = ResolutionMode.ProposeOnly;

    /// <summary>Minimum score gap between the best and second-best valid candidate for an automatic decision.</summary>
    public double AmbiguityMargin { get; init; } = 0.05;

    public Length DisplacementIncrement { get; init; } = Length.FromMillimetres(1);

    /// <summary>Preferred order of bar roles to move (earlier = moved first).</summary>
    public IReadOnlyList<BarRole> MovePreference { get; init; } =
    [
        BarRole.Distribution, BarRole.OpeningTrimmer, BarRole.Additional, BarRole.Crosstie, BarRole.Tie, BarRole.Stirrup,
        BarRole.Bent, BarRole.Longitudinal, BarRole.Dowel, BarRole.Starter, BarRole.Pile, BarRole.Unknown,
    ];

    public int MaxCandidatesPerIssue { get; init; } = 24;

    /// <summary>When set, only these subtypes may be auto-applied (others are proposed for approval).</summary>
    public IReadOnlyList<IssueSubtype>? AutoApplySubtypes { get; init; }
}

/// <summary>
/// Complete, traceable rule configuration. Design-code provisions are never hard-coded: each requirement is an explicit
/// rule with a source and scope. Checks listed in <see cref="RequiredChecks"/> report indeterminate results when no rule applies.
/// </summary>
public sealed record RuleSet
{
    public string SchemaVersion { get; init; } = "1.0";
    public required string Name { get; init; }
    public string? Revision { get; init; }
    public required DesignStandardInfo DesignStandard { get; init; }
    public ToleranceSettings Tolerances { get; init; } = new();
    public ProjectParameters Project { get; init; } = new();

    public IReadOnlyList<CheckKind> RequiredChecks { get; init; } =
        [CheckKind.Cover, CheckKind.ClearSpacing, CheckKind.Lap, CheckKind.Anchorage, CheckKind.Bend, CheckKind.Hook];

    public IReadOnlyList<CoverRule> Cover { get; init; } = [];
    public IReadOnlyList<ClearSpacingRule> ClearSpacing { get; init; } = [];
    public IReadOnlyList<MaxSpacingRule> MaxSpacing { get; init; } = [];
    public IReadOnlyList<LapLengthRule> LapLengths { get; init; } = [];
    public IReadOnlyList<NonContactLapRule> NonContactLaps { get; init; } = [];
    public IReadOnlyList<DevelopmentLengthRule> DevelopmentLengths { get; init; } = [];
    public IReadOnlyList<BendRule> Bends { get; init; } = [];
    public IReadOnlyList<HookRule> Hooks { get; init; } = [];
    public IReadOnlyList<TransverseSpacingRule> TransverseSpacing { get; init; } = [];
    public IReadOnlyList<DiameterRestrictionRule> Restrictions { get; init; } = [];
    public IReadOnlyList<CongestionRule> Congestion { get; init; } = [];
    public IReadOnlyList<PlacementToleranceRule> Placement { get; init; } = [];
    public IReadOnlyList<IntentionalContactRule> IntentionalContacts { get; init; } = [];
    public IReadOnlyList<ApprovedException> Exceptions { get; init; } = [];
    public IReadOnlyList<SeverityOverride> SeverityOverrides { get; init; } = [];
    public ResolutionPolicy Resolution { get; init; } = new();

    public bool IsRequired(CheckKind check) => RequiredChecks.Contains(check);

    public IEnumerable<RuleBase> AllRules() =>
        Cover.Cast<RuleBase>().Concat(ClearSpacing).Concat(MaxSpacing).Concat(LapLengths).Concat(NonContactLaps)
            .Concat(DevelopmentLengths).Concat(Bends).Concat(Hooks).Concat(TransverseSpacing).Concat(Restrictions)
            .Concat(Congestion).Concat(Placement).Concat(IntentionalContacts);
}
