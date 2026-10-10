namespace Collide.Model;

public enum BarRole
{
    Unknown,
    Longitudinal,
    Stirrup,
    Tie,
    Crosstie,

    /// <summary>Slab or wall distribution/mesh bar.</summary>
    Distribution,
    Bent,
    Starter,
    Dowel,
    OpeningTrimmer,
    Additional,
    Pile,
}

public enum HostCategory
{
    Unknown,
    Beam,
    Column,
    Slab,
    Wall,
    Footing,
    PileCap,
    Pile,
    BoundaryElement,
    CouplingBeam,
    Other,
}

/// <summary>Fidelity of the concrete geometry supplied for a host.</summary>
public enum GeometryFidelity
{
    /// <summary>Exact convex solid (e.g. planar faces of a prismatic member).</summary>
    Exact,

    /// <summary>Approximation (e.g. oriented bounding box of a non-convex solid). Findings depending on it are low confidence.</summary>
    Approximate,
}

public enum VoidKind
{
    Opening,
    Recess,
    Sleeve,
}

public enum ZoneKind
{
    /// <summary>No reinforcement may enter (e.g. embedded services corridor).</summary>
    NoReinforcement,

    /// <summary>Lap splices may not be located here (e.g. construction joint, maximum moment region).</summary>
    NoLapSplices,

    /// <summary>Region within which automated relocation is permitted (referenced by placement rules).</summary>
    PermittedAdjustment,
}

public enum AccessoryKind
{
    Coupler,
    MechanicalSplice,
    Anchor,
    Head,
    Other,
}

public enum BarEnd
{
    Start,
    End,
}

public enum AnchorageType
{
    Straight,
    Hooked,
    Headed,
}

public enum SpliceKind
{
    Lap,
    Mechanical,
}
