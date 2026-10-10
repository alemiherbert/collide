namespace Collide.Findings;

/// <summary>The check that produced a finding. Geometric checks are separate from code-compliance checks.</summary>
public enum CheckKind
{
    // Geometric detection
    Clash,
    Duplicate,
    Containment,
    Openings,
    Zones,

    // Constructability
    ClearSpacing,
    Congestion,

    // Detailing / compliance
    Cover,
    Lap,
    Anchorage,
    Bend,
    Hook,
    TransverseSpacing,
    MaxSpacing,
    Restrictions,

    // Data
    Consistency,
    Geometry,
}

public enum IssueCategory
{
    RebarToRebar,
    RebarToConcrete,
    Constructability,
    Detailing,
    DataConsistency,
}

public enum IssueSubtype
{
    // Rebar-to-rebar (geometric)
    CrossingClash,
    ParallelOverlap,
    Duplicate,
    CollinearOverlap,
    LongitudinalTransverseConflict,
    JointConflict,
    InterMemberConflict,
    SlabLayerConflict,
    SlabOpeningReinforcementConflict,
    WallOpeningReinforcementConflict,
    WallBoundaryConflict,
    FoundationStarterConflict,
    LapGeometryConflict,
    HookBendConflict,
    AccessoryConflict,
    UnclassifiedContact,

    // Rebar-to-concrete
    OutsideConcrete,
    InsufficientCover,
    InsufficientCoverToOpening,
    CrossesOpening,
    InProhibitedZone,
    UnintendedHostPenetration,
    LapInProhibitedZone,

    // Constructability
    InsufficientClearSpacing,
    InsufficientAggregatePassage,
    Congestion,
    JointCongestion,

    // Detailing
    LapLengthInsufficient,
    LapGeometryInvalid,
    AnchorageInsufficient,
    BendDiameterTooSmall,
    HookExtensionInsufficient,
    HookAngleNotPermitted,
    TransverseSpacingExceeded,
    TransverseReinforcementMissing,
    MaxSpacingExceeded,
    DiameterNotPermitted,
    ShapeNotPermitted,
    InvalidBarGeometry,

    // Data consistency
    SetQuantityMismatch,
    InconsistentSetData,
    MarkInconsistency,

    // Missing design information
    RequirementUndefined,
}

public enum Severity
{
    Informational = 0,
    Low = 1,
    Medium = 2,
    High = 3,
    Critical = 4,
}

/// <summary>Raw outcome of a check.</summary>
public enum FindingStatus
{
    Violation,
    Warning,

    /// <summary>The check could not be completed because required design information or rules are missing.</summary>
    Indeterminate,
}

/// <summary>Classified status presented to users.</summary>
public enum IssueStatus
{
    /// <summary>Geometric fact measured exactly, or compliance failure against a verified rule with complete inputs.</summary>
    ConfirmedDefect,

    /// <summary>Likely defect, but a rule is unverified, inputs were inferred, or measurement uncertainty overlaps the threshold.</summary>
    SuspectedDefect,
    Warning,
    Indeterminate,
}

public enum Confidence
{
    Low,
    Medium,
    High,
}

/// <summary>What a finding is owned by; used to replace findings exactly when part of the model is re-analysed.</summary>
public enum OwnerKind
{
    Bars,
    Set,
    Host,
    Mark,
}
