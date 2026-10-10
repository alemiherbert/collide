using Collide.Geometry;
using Collide.Units;

namespace Collide.Model;

/// <summary>
/// One physical bar. In Revit a Rebar element is a set of bars; each included bar position becomes one
/// <see cref="RebarBar"/> with <see cref="ElementId"/> = the Rebar element id and <see cref="PositionIndex"/> = the bar position.
/// </summary>
public sealed record RebarBar
{
    public required string Id { get; init; }
    public required string ElementId { get; init; }
    public int PositionIndex { get; init; }
    public string? SetId { get; init; }
    public string? Mark { get; init; }
    public string? ShapeCode { get; init; }

    /// <summary>Nominal diameter: the basis for code rules (cover is to the bar surface, lap/anchorage are multiples of this).</summary>
    public required Length NominalDiameter { get; init; }

    /// <summary>Model diameter including deformations (Revit <c>RebarBarType.BarModelDiameter</c>). Used for physical clash geometry.</summary>
    public Length? ModelDiameter { get; init; }

    public BarRole Role { get; init; }

    /// <summary>Reinforcement layer label (e.g. "B1", "B2", "T1", "EF-V"). Required by layer-based intentional-contact rules.</summary>
    public string? Layer { get; init; }

    public string? HostId { get; init; }
    public required BarPath Path { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Design intent locked: never moved automatically.</summary>
    public bool IsDesignLocked { get; init; }

    /// <summary>Hosts the bar is intended to extend into (anchorage/continuity). Null = unknown, so extensions are not judged.</summary>
    public IReadOnlyList<string>? PermittedExtensionHostIds { get; init; }

    /// <summary>Declared anchorage requirements. Further requirements can be inferred from geometry (bar ends inside other hosts).</summary>
    public IReadOnlyList<AnchorageRequirement> Anchorages { get; init; } = [];

    public Length PhysicalDiameter => ModelDiameter ?? NominalDiameter;

    public string GeometryHash => Path.GeometryHash(PhysicalDiameter.Millimetres);

    public RebarBar Translate(Vec3 offset) => this with { Path = Path.Translate(offset) };
}

public sealed record AnchorageRequirement(BarEnd End, string TargetHostId, AnchorageType? Type = null);

/// <summary>A rebar set (Revit Rebar element with several bar positions).</summary>
public sealed record RebarSet
{
    public required string Id { get; init; }
    public required string ElementId { get; init; }
    public string? HostId { get; init; }
    public BarRole Role { get; init; }
    public required IReadOnlyList<string> BarIds { get; init; }

    /// <summary>Quantity declared by the design/layout (Revit <c>Rebar.Quantity</c>). Compared against the bars actually present.</summary>
    public int? DeclaredQuantity { get; init; }

    public string? LayoutRule { get; init; }
    public Length? Spacing { get; init; }

    /// <summary>Unit direction along which the set is distributed, if known.</summary>
    public Vec3? DistributionDirection { get; init; }
}

public sealed record HostVoid(string Id, VoidKind Kind, OrientedBox Box, string? Description = null);

/// <summary>A concrete element that hosts reinforcement.</summary>
public sealed record StructuralHost
{
    public required string Id { get; init; }
    public required HostCategory Category { get; init; }
    public string? Name { get; init; }
    public string? Level { get; init; }
    public string? Zone { get; init; }
    public required ConvexRegion Region { get; init; }
    public GeometryFidelity Fidelity { get; init; } = GeometryFidelity.Exact;
    public IReadOnlyList<HostVoid> Voids { get; init; } = [];

    /// <summary>Member axis for beams/columns (used by transverse-spacing checks). Falls back to the longest bounding-box axis.</summary>
    public (Vec3 Start, Vec3 End)? Axis { get; init; }

    /// <summary>Orthonormal local axes (X, Y, Z) of the host, used by placement rules with <c>frame: Host</c>. Null = global axes.</summary>
    public IReadOnlyList<Vec3>? LocalAxes { get; init; }

    /// <summary>Host-specific maximum aggregate size; overrides the project value when present.</summary>
    public Length? MaximumAggregateSize { get; init; }

    public string? ConcreteGrade { get; init; }

    public Aabb Bounds => Region.Bounds;
}

public sealed record PlacementZone
{
    public required string Id { get; init; }
    public required ZoneKind Kind { get; init; }
    public required OrientedBox Box { get; init; }
    public string? HostId { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<BarRole> ExemptRoles { get; init; } = [];
    public IReadOnlyList<string> ExemptBarIds { get; init; } = [];
}

/// <summary>Coupler, mechanical splice, anchor or head, modelled as a cylinder along its axis.</summary>
public sealed record Accessory
{
    public required string Id { get; init; }
    public required string ElementId { get; init; }
    public required AccessoryKind Kind { get; init; }
    public required Vec3 AxisStart { get; init; }
    public required Vec3 AxisEnd { get; init; }
    public required Length OuterDiameter { get; init; }
    public IReadOnlyList<string> AttachedBarIds { get; init; } = [];
    public string? HostId { get; init; }

    /// <summary>True when the geometry came from a bounding box rather than the accessory's actual solid.</summary>
    public bool GeometryApproximate { get; init; }
}

/// <summary>A splice declared by the model/design (laps can also be inferred from geometry).</summary>
public sealed record SpliceDefinition(string Id, string BarA, string BarB, SpliceKind Kind);
