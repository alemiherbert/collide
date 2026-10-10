using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Collide.Geometry;
using Collide.Rules;

namespace Collide.Findings;

/// <summary>A measured quantity attached to a finding. Lengths are always millimetres.</summary>
public sealed record Measurement(string Name, double Value, string Unit, string? Note = null)
{
    public static Measurement Mm(string name, double value, string? note = null) => new(name, Math.Round(value, 4), "mm", note);
    public static Measurement Ratio(string name, double value, string? note = null) => new(name, Math.Round(value, 6), "ratio", note);
    public static Measurement Count(string name, int value) => new(name, value, "count");
    public static Measurement Degrees(string name, double value) => new(name, Math.Round(value, 3), "deg");
    public static Measurement CubicMm(string name, double value, string? note = null) => new(name, Math.Round(value, 2), "mm3", note);

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Name} = {Value:0.###} {Unit}");
}

public sealed record RuleReference(string RuleId, string RuleKind, string Source, bool Verified)
{
    public static RuleReference Of(RuleBase rule) => new(rule.Id, rule.GetType().Name, rule.Source.ToString(), rule.Verified);
}

/// <summary>Keys that own a finding, so re-analysis of part of the model replaces exactly the findings it recomputes.</summary>
public sealed record FindingOwner(OwnerKind Kind, IReadOnlyList<string> Keys)
{
    public static FindingOwner Bars(params string[] ids) => new(OwnerKind.Bars, ids.Distinct().OrderBy(i => i, StringComparer.Ordinal).ToArray());
    public static FindingOwner Set(string id) => new(OwnerKind.Set, [id]);
    public static FindingOwner Host(string id) => new(OwnerKind.Host, [id]);
    public static FindingOwner Mark(string mark) => new(OwnerKind.Mark, [mark]);

    public override string ToString() => $"{Kind}:{string.Join(",", Keys)}";
}

/// <summary>Contact geometry for pair findings: closest points and the direction that separates the first bar from the second.</summary>
public sealed record ContactGeometry(string FirstBarId, string SecondBarId, Vec3 PointOnFirst, Vec3 PointOnSecond, Vec3 SeparationDirection, double AngleDegrees);

/// <summary>
/// Raw output of one check, before policy (severity, confidence, review, eligibility) is applied by the
/// <see cref="Classification.DefectClassifier"/>. Immutable.
/// </summary>
public sealed record Finding
{
    public required CheckKind Check { get; init; }
    public required IssueSubtype Subtype { get; init; }
    public required FindingStatus Status { get; init; }
    public required FindingOwner Owner { get; init; }
    public IReadOnlyList<string> BarIds { get; init; } = [];
    public IReadOnlyList<string> HostIds { get; init; } = [];
    public IReadOnlyList<string> SetIds { get; init; } = [];
    public Vec3? Location { get; init; }
    public IReadOnlyList<Measurement> Measurements { get; init; } = [];

    /// <summary>How far beyond the limit (mm, or ratio for congestion). Used to detect worsened findings after a correction.</summary>
    public double Exceedance { get; init; }

    /// <summary>|measured − threshold| (mm). Compared with <see cref="ErrorBound"/> to judge confidence.</summary>
    public double DecisionMargin { get; init; } = double.PositiveInfinity;

    /// <summary>Upper bound of the measurement error (e.g. arc chord approximation), mm.</summary>
    public double ErrorBound { get; init; }

    public bool GeometryApproximate { get; init; }

    /// <summary>The finding relies on an inferred relationship (inferred lap, inferred anchorage target).</summary>
    public bool Inferred { get; init; }

    public RuleReference? Rule { get; init; }
    public required string Explanation { get; init; }
    public IReadOnlyList<string> MissingInputs { get; init; } = [];
    public IReadOnlyList<string> Notes { get; init; } = [];
    public ContactGeometry? Contact { get; init; }

    /// <summary>Outward normal of the governing concrete face, for cover/containment corrections.</summary>
    public Vec3? FaceNormal { get; init; }

    public string Discriminator { get; init; } = "";

    /// <summary>Indeterminate findings with the same key are consolidated into one issue.</summary>
    public string? ConsolidationKey { get; init; }

    /// <summary>Identity independent of measured values, so the same problem keeps its id while geometry changes.</summary>
    public string Fingerprint =>
        $"{Check}|{Subtype}|{Owner}|B:{string.Join(",", BarIds)}|H:{string.Join(",", HostIds)}|R:{Rule?.RuleId}|{Discriminator}";

    public static string ShortHash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..10];
}

public sealed record BarInfo(string Id, string ElementId, string? Mark, double NominalDiameterMm, string? ShapeCode, string Role, string? HostId, string? Layer);

public sealed record HostInfo(string Id, string Category, string? Name, string? Level);

/// <summary>A classified, user-facing issue. Immutable; ids are deterministic for identical inputs and rules.</summary>
public sealed record Issue
{
    public required string Id { get; init; }
    public required IssueCategory Category { get; init; }
    public required IssueSubtype Subtype { get; init; }
    public required CheckKind Check { get; init; }
    public required Severity Severity { get; init; }
    public required IssueStatus Status { get; init; }
    public required Confidence Confidence { get; init; }
    public required bool RequiresEngineeringReview { get; init; }
    public required bool AutoResolutionPermitted { get; init; }
    public required string AutoResolutionReason { get; init; }
    public required string Explanation { get; init; }
    public required string RecommendedAction { get; init; }
    public IReadOnlyList<BarInfo> Bars { get; init; } = [];
    public IReadOnlyList<HostInfo> Hosts { get; init; } = [];
    public int AffectedBarCount { get; init; }
    public Vec3? Location { get; init; }
    public string? LevelOrZone { get; init; }
    public IReadOnlyList<Measurement> Measurements { get; init; } = [];
    public RuleReference? Rule { get; init; }
    public IReadOnlyList<string> MissingInputs { get; init; } = [];
    public IReadOnlyList<string> Limitations { get; init; } = [];

    /// <summary>Fingerprints of the raw findings this issue was built from.</summary>
    public IReadOnlyList<string> FindingFingerprints { get; init; } = [];

    public IEnumerable<string> ElementIds => Bars.Select(b => b.ElementId).Concat(Hosts.Select(h => h.Id)).Distinct();
}
