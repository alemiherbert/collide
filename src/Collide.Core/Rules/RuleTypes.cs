using Collide.Findings;
using Collide.Geometry;
using Collide.Model;
using Collide.Units;

namespace Collide.Rules;

/// <summary>Traceable origin of a requirement (code clause, project specification, approved RFI ...).</summary>
public sealed record RuleSource
{
    public required string Document { get; init; }
    public string? Clause { get; init; }
    public string? Edition { get; init; }
    public string? Note { get; init; }

    public override string ToString() =>
        Document + (Clause is null ? "" : $" §{Clause}") + (Edition is null ? "" : $" ({Edition})");
}

/// <summary>Attributes of the bar/host/face a rule is being looked up for.</summary>
public sealed record RuleContext
{
    public HostCategory? HostCategory { get; init; }
    public string? HostId { get; init; }
    public BarRole? Role { get; init; }
    public string? Mark { get; init; }
    public string? Layer { get; init; }
    public Length? NominalDiameter { get; init; }
    public FaceTag? FaceTag { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];

    public static RuleContext ForBar(RebarBar bar, StructuralHost? host, FaceTag? face = null) => new()
    {
        HostCategory = host?.Category,
        HostId = host?.Id ?? bar.HostId,
        Role = bar.Role,
        Mark = bar.Mark,
        Layer = bar.Layer,
        NominalDiameter = bar.NominalDiameter,
        FaceTag = face,
        Tags = bar.Tags,
    };

    public static RuleContext ForHost(StructuralHost host) => new() { HostCategory = host.Category, HostId = host.Id };
}

/// <summary>Applicability filter. Unspecified criteria match anything; specified criteria must all match.</summary>
public sealed record RuleScope
{
    public static RuleScope Any { get; } = new();

    public IReadOnlyList<HostCategory>? HostCategories { get; init; }
    public IReadOnlyList<string>? HostIds { get; init; }
    public IReadOnlyList<BarRole>? BarRoles { get; init; }
    public IReadOnlyList<string>? BarMarks { get; init; }
    public IReadOnlyList<string>? Layers { get; init; }
    public IReadOnlyList<FaceTag>? FaceTags { get; init; }
    public Length? MinimumDiameter { get; init; }
    public Length? MaximumDiameter { get; init; }
    public IReadOnlyList<string>? Tags { get; init; }

    public bool Matches(RuleContext ctx)
    {
        if (HostCategories is { Count: > 0 } && (ctx.HostCategory is null || !HostCategories.Contains(ctx.HostCategory.Value))) return false;
        if (HostIds is { Count: > 0 } && (ctx.HostId is null || !HostIds.Contains(ctx.HostId))) return false;
        if (BarRoles is { Count: > 0 } && (ctx.Role is null || !BarRoles.Contains(ctx.Role.Value))) return false;
        if (BarMarks is { Count: > 0 } && (ctx.Mark is null || !BarMarks.Contains(ctx.Mark))) return false;
        if (Layers is { Count: > 0 } && (ctx.Layer is null || !Layers.Contains(ctx.Layer))) return false;
        if (FaceTags is { Count: > 0 } && (ctx.FaceTag is null || !FaceTags.Contains(ctx.FaceTag.Value))) return false;
        if (MinimumDiameter is { } min && (ctx.NominalDiameter is null || ctx.NominalDiameter.Value < min - Length.FromMillimetres(1e-6))) return false;
        if (MaximumDiameter is { } max && (ctx.NominalDiameter is null || ctx.NominalDiameter.Value > max + Length.FromMillimetres(1e-6))) return false;
        if (Tags is { Count: > 0 } && !Tags.Any(t => ctx.Tags.Contains(t))) return false;
        return true;
    }

    /// <summary>Higher is more specific. Element-specific criteria outrank categorical ones.</summary>
    public int Specificity =>
        (HostIds is { Count: > 0 } ? 16 : 0) + (BarMarks is { Count: > 0 } ? 16 : 0) +
        (FaceTags is { Count: > 0 } ? 4 : 0) + (HostCategories is { Count: > 0 } ? 4 : 0) +
        (BarRoles is { Count: > 0 } ? 4 : 0) + (Layers is { Count: > 0 } ? 4 : 0) +
        (MinimumDiameter is not null || MaximumDiameter is not null ? 2 : 0) + (Tags is { Count: > 0 } ? 1 : 0);
}

public abstract record RuleBase
{
    public required string Id { get; init; }
    public string? Description { get; init; }
    public required RuleSource Source { get; init; }
    public RuleScope Scope { get; init; } = RuleScope.Any;

    /// <summary>
    /// Set when an engineer has verified the rule's values against the governing standard for this project.
    /// Violations of unverified rules are reported as suspected, not confirmed.
    /// </summary>
    public bool Verified { get; init; }
}

public sealed record CoverRule : RuleBase
{
    public required Length MinimumCover { get; init; }
}

public enum SpacingRelation
{
    Any,
    SameLayer,
    BetweenLayers,
}

/// <summary>Minimum clear spacing between parallel bars = max of the configured terms.</summary>
public sealed record ClearSpacingRule : RuleBase
{
    public double? BarDiameterFactor { get; init; }

    /// <summary>Added to the maximum aggregate size (requires that input).</summary>
    public Length? AggregateAllowance { get; init; }

    public double? AggregateFactor { get; init; }
    public Length? AbsoluteMinimum { get; init; }
    public SpacingRelation Relation { get; init; } = SpacingRelation.Any;
}

public sealed record MaxSpacingRule : RuleBase
{
    public required Length MaximumCentreSpacing { get; init; }
}

public sealed record DiameterLengthEntry(Length Diameter, Length Length);

/// <summary>Length requirement expressed as a table by diameter and/or a multiple of the nominal diameter with a floor.</summary>
public abstract record LengthRequirementRule : RuleBase
{
    public double? DiameterFactor { get; init; }
    public Length? Minimum { get; init; }
    public IReadOnlyList<DiameterLengthEntry>? Table { get; init; }

    /// <summary>How the provided length is measured (documented in reports).</summary>
    public string? MeasurementBasis { get; init; }

    public Length? RequiredFor(Length nominalDiameter, out string basis)
    {
        if (Table is { Count: > 0 })
        {
            var entry = Table.FirstOrDefault(e => Math.Abs(e.Diameter.Millimetres - nominalDiameter.Millimetres) <= 0.05);
            if (entry is not null)
            {
                basis = $"table entry for Ø{entry.Diameter.Millimetres:0.#}";
                return entry.Length;
            }
        }

        if (DiameterFactor is { } k)
        {
            var value = nominalDiameter * k;
            basis = $"{k:0.##}·d";
            if (Minimum is { } min && min > value)
            {
                basis += $", minimum {min}";
                return min;
            }

            return value;
        }

        if (Minimum is { } only && (Table is null || Table.Count == 0))
        {
            basis = "fixed minimum";
            return only;
        }

        basis = "no table entry or factor for this diameter";
        return null;
    }
}

public sealed record LapLengthRule : LengthRequirementRule;

public sealed record DevelopmentLengthRule : LengthRequirementRule
{
    public AnchorageType AnchorageType { get; init; } = AnchorageType.Straight;
}

public sealed record NonContactLapRule : RuleBase
{
    public Length? MaximumClearOffset { get; init; }
    public double? MaximumOffsetToLapLengthRatio { get; init; }
}

public sealed record BendRule : RuleBase
{
    /// <summary>Minimum inside bend diameter as a multiple of the nominal bar diameter.</summary>
    public required double MinimumInnerDiameterFactor { get; init; }
}

public sealed record HookRule : RuleBase
{
    public required Angle HookAngle { get; init; }
    public Angle AngleTolerance { get; init; } = Angle.FromDegrees(5);
    public double? ExtensionDiameterFactor { get; init; }
    public Length? MinimumExtension { get; init; }

    public Length RequiredExtension(Length nominalDiameter)
    {
        var byFactor = ExtensionDiameterFactor is { } k ? nominalDiameter * k : Length.Zero;
        return Length.Max(byFactor, MinimumExtension ?? Length.Zero);
    }
}

public enum TransverseZone
{
    Entire,
    EndZones,
}

public sealed record TransverseSpacingRule : RuleBase
{
    public required Length MaximumSpacing { get; init; }
    public TransverseZone Zone { get; init; } = TransverseZone.Entire;
    public Length? EndZoneLength { get; init; }
    public bool RequirePresence { get; init; } = true;
}

public sealed record DiameterRestrictionRule : RuleBase
{
    public IReadOnlyList<Length>? AllowedDiameters { get; init; }
    public IReadOnlyList<string>? AllowedShapeCodes { get; init; }
}

public sealed record CongestionRule : RuleBase
{
    /// <summary>Maximum steel volume / concrete volume within one cell.</summary>
    public required double MaximumSteelRatio { get; init; }

    public required Length CellSize { get; init; }
}

public enum AxisFrame
{
    Global,
    Host,
}

public enum Axis
{
    X,
    Y,
    Z,
}

/// <summary>Permitted automatic relocation of bars. Without a matching rule a bar is never moved automatically.</summary>
public sealed record PlacementToleranceRule : RuleBase
{
    public required Length MaximumDisplacement { get; init; }

    /// <summary>Moves larger than this (but within <see cref="MaximumDisplacement"/>) require engineering approval.</summary>
    public Length? AutoApplyMaximumDisplacement { get; init; }

    public AxisFrame Frame { get; init; } = AxisFrame.Global;
    public IReadOnlyList<Axis> AllowedAxes { get; init; } = [Axis.X, Axis.Y, Axis.Z];

    /// <summary>Moving a straight bar along its own axis shifts anchorage and laps; disallowed unless explicitly enabled.</summary>
    public bool AllowAlongBarAxis { get; init; }

    public IReadOnlyList<string>? PermittedZoneIds { get; init; }
}

/// <summary>Contacts that are intended (e.g. mesh layers resting on each other, bars tied into stirrup corners).</summary>
public sealed record IntentionalContactRule : RuleBase
{
    public IReadOnlyList<BarRole>? RolesA { get; init; }
    public IReadOnlyList<BarRole>? RolesB { get; init; }
    public bool RequireDifferentLayers { get; init; }
    public Angle? MinimumAngle { get; init; }
    public Angle? MaximumAngle { get; init; }
    public required Length MaximumPenetration { get; init; }
}

/// <summary>An approved detail that suppresses specific findings for specific bars.</summary>
public sealed record ApprovedException
{
    public required string Id { get; init; }
    public required string ApprovalReference { get; init; }
    public string? ApprovedBy { get; init; }
    public string? Date { get; init; }
    public required IReadOnlyList<string> BarIds { get; init; }
    public IReadOnlyList<IssueSubtype>? Subtypes { get; init; }
    public string? Note { get; init; }
}

public sealed record SeverityOverride(IssueSubtype Subtype, Severity Severity, string? Reason = null);
