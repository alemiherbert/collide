using Collide.Findings;
using Collide.Model;
using Collide.Rules;

namespace Collide.Classification;

/// <summary>
/// Turns raw findings into user-facing issues by applying policy: category, severity (with rule overrides), status
/// (confirmed / suspected / warning / indeterminate), confidence, review requirement, auto-resolution eligibility and a
/// recommended action. A compliance finding is "confirmed" only when its rule is engineer-verified, all inputs are
/// present, nothing was inferred and the measurement uncertainty does not overlap the threshold.
/// </summary>
public sealed class DefectClassifier
{
    private static readonly HashSet<CheckKind> GeometricChecks =
        [CheckKind.Clash, CheckKind.Duplicate, CheckKind.Containment, CheckKind.Openings, CheckKind.Zones, CheckKind.Geometry];

    private static readonly HashSet<IssueSubtype> MovableClashSubtypes =
    [
        IssueSubtype.CrossingClash, IssueSubtype.ParallelOverlap, IssueSubtype.LongitudinalTransverseConflict, IssueSubtype.JointConflict,
        IssueSubtype.InterMemberConflict, IssueSubtype.SlabLayerConflict, IssueSubtype.SlabOpeningReinforcementConflict,
        IssueSubtype.WallOpeningReinforcementConflict, IssueSubtype.WallBoundaryConflict, IssueSubtype.FoundationStarterConflict,
        IssueSubtype.HookBendConflict, IssueSubtype.LapGeometryConflict,
    ];

    /// <summary>Subtypes the resolution planner knows how to correct by permitted bar relocation.</summary>
    public static readonly IReadOnlySet<IssueSubtype> ResolvableSubtypes = new HashSet<IssueSubtype>(MovableClashSubtypes)
    {
        IssueSubtype.InsufficientClearSpacing, IssueSubtype.InsufficientAggregatePassage, IssueSubtype.InsufficientCover, IssueSubtype.InsufficientCoverToOpening,
    };

    public static Severity DefaultSeverity(IssueSubtype subtype) => subtype switch
    {
        IssueSubtype.OutsideConcrete => Severity.Critical,
        IssueSubtype.CrossingClash or IssueSubtype.ParallelOverlap or IssueSubtype.Duplicate or IssueSubtype.CollinearOverlap or
            IssueSubtype.LongitudinalTransverseConflict or IssueSubtype.JointConflict or IssueSubtype.InterMemberConflict or
            IssueSubtype.SlabLayerConflict or IssueSubtype.SlabOpeningReinforcementConflict or IssueSubtype.WallOpeningReinforcementConflict or
            IssueSubtype.WallBoundaryConflict or IssueSubtype.FoundationStarterConflict or IssueSubtype.HookBendConflict or
            IssueSubtype.AccessoryConflict or IssueSubtype.InsufficientCover or IssueSubtype.InsufficientCoverToOpening or
            IssueSubtype.CrossesOpening or IssueSubtype.InProhibitedZone or IssueSubtype.LapLengthInsufficient or
            IssueSubtype.AnchorageInsufficient or IssueSubtype.TransverseReinforcementMissing or IssueSubtype.InvalidBarGeometry => Severity.High,
        IssueSubtype.UnclassifiedContact or IssueSubtype.SetQuantityMismatch or IssueSubtype.InconsistentSetData or IssueSubtype.MarkInconsistency => Severity.Low,
        _ => Severity.Medium,
    };

    public static IssueCategory CategoryOf(CheckKind check) => check switch
    {
        CheckKind.Clash or CheckKind.Duplicate => IssueCategory.RebarToRebar,
        CheckKind.Containment or CheckKind.Openings or CheckKind.Zones or CheckKind.Cover => IssueCategory.RebarToConcrete,
        CheckKind.ClearSpacing or CheckKind.Congestion => IssueCategory.Constructability,
        CheckKind.Consistency => IssueCategory.DataConsistency,
        _ => IssueCategory.Detailing,
    };

    public IReadOnlyList<Issue> Classify(IEnumerable<Finding> findings, ReinforcementModel model, RuleSet rules)
    {
        var issues = new List<Issue>();
        var consolidated = new Dictionary<string, List<Finding>>(StringComparer.Ordinal);
        foreach (var f in findings)
        {
            if (f.Status == FindingStatus.Indeterminate && f.ConsolidationKey is { } key)
            {
                if (!consolidated.TryGetValue(key, out var list)) consolidated[key] = list = [];
                list.Add(f);
                continue;
            }

            issues.Add(Build(f, [f], model, rules, "RC-" + Finding.ShortHash(f.Fingerprint)));
        }

        foreach (var (key, group) in consolidated)
        {
            var first = group.OrderBy(g => g.Fingerprint, StringComparer.Ordinal).First();
            var bars = group.SelectMany(g => g.BarIds).Distinct().OrderBy(b => b, StringComparer.Ordinal).ToArray();
            var hosts = group.SelectMany(g => g.HostIds).Where(h => h.Length > 0).Distinct().OrderBy(h => h, StringComparer.Ordinal).ToArray();
            var merged = first with
            {
                BarIds = bars,
                HostIds = hosts,
                MissingInputs = group.SelectMany(g => g.MissingInputs).Distinct().OrderBy(m => m, StringComparer.Ordinal).ToArray(),
                Explanation = group.Count == 1 ? first.Explanation : $"{first.Explanation} Affects {bars.Length} bar(s) in {group.Count} location(s).",
            };
            issues.Add(Build(merged, group, model, rules, "RC-" + Finding.ShortHash("consolidated|" + key)));
        }

        return issues
            .OrderByDescending(i => i.Severity)
            .ThenBy(i => i.Category)
            .ThenBy(i => i.Subtype)
            .ThenBy(i => i.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static Issue Build(Finding f, IReadOnlyList<Finding> sources, ReinforcementModel model, RuleSet rules, string id)
    {
        var severity = DefaultSeverity(f.Subtype);
        var penetration = f.Measurements.FirstOrDefault(m => m.Name == "penetrationDepth")?.Value;
        if (f.Check == CheckKind.Clash && f.Subtype != IssueSubtype.LapGeometryConflict && penetration is { } pen &&
            pen <= rules.Tolerances.MinorPenetration.Millimetres && severity == Severity.High)
            severity = Severity.Medium;
        if (f.Subtype == IssueSubtype.InsufficientCover && f.Measurements.FirstOrDefault(m => m.Name == "providedCover")?.Value < 0)
            severity = Severity.Critical;
        var overrideRule = rules.SeverityOverrides.LastOrDefault(o => o.Subtype == f.Subtype);
        if (overrideRule is not null) severity = overrideRule.Severity;

        var uncertain = f.DecisionMargin <= f.ErrorBound;
        var status = f.Status switch
        {
            FindingStatus.Indeterminate => IssueStatus.Indeterminate,
            FindingStatus.Warning => IssueStatus.Warning,
            _ when f.Check == CheckKind.Consistency => IssueStatus.ConfirmedDefect,
            _ when GeometricChecks.Contains(f.Check) => !uncertain && !f.Inferred ? IssueStatus.ConfirmedDefect : IssueStatus.SuspectedDefect,
            _ => f.Rule is { Verified: true } && !uncertain && !f.Inferred && f.MissingInputs.Count == 0 ? IssueStatus.ConfirmedDefect : IssueStatus.SuspectedDefect,
        };

        var confidence = f.Status == FindingStatus.Indeterminate ? Confidence.High
            : uncertain ? Confidence.Low
            : f.GeometryApproximate || f.Inferred ? Confidence.Medium
            : Confidence.High;

        var (permitted, reason) = Eligibility(f, status, confidence, model, rules);
        var review = !(permitted && confidence == Confidence.High && status == IssueStatus.ConfirmedDefect && severity < Severity.Critical);

        var bars = f.BarIds.Select(model.FindBar).Where(b => b is not null).Select(b => b!)
            .Select(b => new BarInfo(b.Id, b.ElementId, b.Mark, b.NominalDiameter.Millimetres, b.ShapeCode, b.Role.ToString(), b.HostId, b.Layer)).ToArray();
        var hosts = f.HostIds.Select(model.FindHost).Where(h => h is not null).Select(h => h!)
            .Select(h => new HostInfo(h.Id, h.Category.ToString(), h.Name, h.Level)).ToArray();
        var levels = hosts.Select(h => h.Level).Where(l => l is not null).Distinct().ToArray();
        var zone = f.Subtype is IssueSubtype.JointConflict or IssueSubtype.JointCongestion && hosts.Length >= 2
            ? $"Joint {string.Join("/", hosts.Select(h => h.Name ?? h.Id))}"
            : string.Join(", ", hosts.Select(h => model.FindHost(h.Id)?.Zone).Where(z => z is not null).Distinct());

        var limitations = new List<string>(f.Notes);
        if (f.Inferred) limitations.Add("Based on an inferred relationship (not declared in the model).");
        if (uncertain && f.Status == FindingStatus.Violation) limitations.Add($"Measurement uncertainty ({f.ErrorBound:0.###} mm) overlaps the decision threshold.");
        if (status == IssueStatus.SuspectedDefect && f.Rule is { Verified: false }) limitations.Add($"Rule {f.Rule.RuleId} is not marked as engineer-verified; reported as suspected, not confirmed.");

        return new Issue
        {
            Id = id,
            Category = CategoryOf(f.Check),
            Subtype = f.Subtype,
            Check = f.Check,
            Severity = severity,
            Status = status,
            Confidence = confidence,
            RequiresEngineeringReview = review,
            AutoResolutionPermitted = permitted,
            AutoResolutionReason = reason,
            Explanation = f.Explanation,
            RecommendedAction = RecommendedAction(f),
            Bars = bars,
            Hosts = hosts,
            AffectedBarCount = f.BarIds.Count,
            Location = f.Location,
            LevelOrZone = string.Join("; ", levels.Concat(zone.Length > 0 ? [zone] : Array.Empty<string>())) is { Length: > 0 } lz ? lz : null,
            Measurements = f.Measurements,
            Rule = f.Rule,
            MissingInputs = f.MissingInputs,
            Limitations = limitations,
            FindingFingerprints = sources.Select(s => s.Fingerprint).OrderBy(s => s, StringComparer.Ordinal).ToArray(),
        };
    }

    private static (bool, string) Eligibility(Finding f, IssueStatus status, Confidence confidence, ReinforcementModel model, RuleSet rules)
    {
        if (!ResolvableSubtypes.Contains(f.Subtype))
            return (false, f.Subtype switch
            {
                IssueSubtype.Duplicate or IssueSubtype.CollinearOverlap => "Removing or merging reinforcement changes quantities; requires engineering decision.",
                IssueSubtype.OutsideConcrete => "Trimming or rerouting a bar changes its length or anchorage; requires engineering decision.",
                IssueSubtype.LapLengthInsufficient or IssueSubtype.AnchorageInsufficient => "Lengthening laps or anchorage changes the design; never adjusted automatically.",
                IssueSubtype.RequirementUndefined => "Design information is missing; supply the rule or input first.",
                _ => "No automated strategy for this issue type.",
            });
        if (status is IssueStatus.Indeterminate or IssueStatus.Warning) return (false, "Not a determinate defect.");
        if (confidence == Confidence.Low) return (false, "Measurement uncertainty overlaps the threshold; confirm before correcting.");
        if (rules.Resolution.Mode == ResolutionMode.DetectOnly) return (false, "Resolution policy is DetectOnly.");
        var movable = f.BarIds.Select(model.FindBar).Where(b => b is not null).Select(b => b!)
            .Where(b => !b.IsDesignLocked && RuleEngine.Find(rules.Placement, RuleContext.ForBar(b, model.FindHost(b.HostId))) is not null).ToArray();
        if (movable.Length == 0) return (false, "No involved bar is covered by a placement-tolerance rule (or all are design-locked).");
        return (true, $"Candidate relocation of {string.Join(", ", movable.Select(b => b.Id))} within placement tolerances.");
    }

    private static string RecommendedAction(Finding f) => f.Subtype switch
    {
        IssueSubtype.Duplicate => "Confirm which bar is intended and delete the duplicate after engineering review; check schedules for double counting.",
        IssueSubtype.CollinearOverlap => "Review whether the overlapping bars are a mis-modelled lap or duplicated reinforcement; correct the layout.",
        IssueSubtype.ParallelOverlap or IssueSubtype.CrossingClash or IssueSubtype.InterMemberConflict =>
            "Relocate one bar within its placement tolerance, or adjust the layout; verify cover and spacing after the change.",
        IssueSubtype.LongitudinalTransverseConflict => "Move the longitudinal bar into the stirrup/tie corner or adjust the transverse bar dimensions so bars sit inside the links.",
        IssueSubtype.JointConflict => "Coordinate beam and column bar positions in the joint (layer offsets, cranking, or alternate bar positions) with the engineer.",
        IssueSubtype.JointCongestion => "Reduce congestion in the joint (bundling, larger bars at wider spacing, or mechanical anchorage) subject to engineering approval.",
        IssueSubtype.SlabLayerConflict => "Separate the slab layers: check layer assignment and the vertical position of each mesh.",
        IssueSubtype.SlabOpeningReinforcementConflict => "Shift the slab bar or the trimmer bar so they no longer occupy the same layer position; keep trimmer anchorage.",
        IssueSubtype.WallOpeningReinforcementConflict => "Re-space wall bars around the opening or offset the trimmer bars into the correct layer.",
        IssueSubtype.WallBoundaryConflict => "Coordinate wall, boundary element and coupling beam bar positions; adjust layers or crank bars.",
        IssueSubtype.FoundationStarterConflict => "Re-position starter bars or foundation mesh bars so starters pass between mesh bars with required clearance.",
        IssueSubtype.LapGeometryConflict => "Offset one lapped bar sideways so the bars sit side by side (contact lap).",
        IssueSubtype.HookBendConflict => "Rotate or re-orient the hook/bend, or relocate the bar, subject to anchorage requirements.",
        IssueSubtype.AccessoryConflict => "Relocate the bar or the coupler/anchor so they do not intersect.",
        IssueSubtype.UnclassifiedContact => "Confirm the contact is intended; if so add an intentional-contact rule or approved exception.",
        IssueSubtype.OutsideConcrete => "Trim, reroute or re-host the bar so it stays within concrete with the required cover.",
        IssueSubtype.InsufficientCover or IssueSubtype.InsufficientCoverToOpening => "Move the bar inward to provide the required cover, or adjust bar dimensions.",
        IssueSubtype.CrossesOpening => "Stop the bar at the opening with required cover and provide trimmer bars as designed.",
        IssueSubtype.InProhibitedZone => "Relocate the bar out of the prohibited zone.",
        IssueSubtype.UnintendedHostPenetration => "Confirm the bar is meant to extend into this element; otherwise shorten or reroute it.",
        IssueSubtype.LapInProhibitedZone => "Move the lap splice out of the zone (subject to lap length and anchorage requirements).",
        IssueSubtype.InsufficientClearSpacing or IssueSubtype.InsufficientAggregatePassage => "Increase bar spacing within placement tolerances or revise the bar arrangement.",
        IssueSubtype.Congestion => "Review the arrangement for concrete placement: consider bundling, larger bars at wider spacing, or staggering laps.",
        IssueSubtype.LapLengthInsufficient => "Increase the lap length to the required value (design change: engineering approval required).",
        IssueSubtype.LapGeometryInvalid => "Bring non-contact lap bars within the permitted offset or provide a contact lap.",
        IssueSubtype.AnchorageInsufficient => "Extend the bar or provide a hook/head to achieve the required anchorage (engineering approval required).",
        IssueSubtype.BendDiameterTooSmall => "Use a bend radius not smaller than the required mandrel diameter.",
        IssueSubtype.HookExtensionInsufficient or IssueSubtype.HookAngleNotPermitted => "Change the hook type to one with a permitted angle and extension.",
        IssueSubtype.TransverseSpacingExceeded => "Reduce stirrup/tie spacing in the affected zone to the permitted maximum.",
        IssueSubtype.TransverseReinforcementMissing => "Provide transverse reinforcement in the zone as required.",
        IssueSubtype.MaxSpacingExceeded => "Add bars or reduce spacing to the permitted maximum.",
        IssueSubtype.DiameterNotPermitted or IssueSubtype.ShapeNotPermitted => "Use a permitted bar diameter/shape for this member.",
        IssueSubtype.InvalidBarGeometry => "Recreate the bar with valid, continuous geometry.",
        IssueSubtype.SetQuantityMismatch or IssueSubtype.InconsistentSetData or IssueSubtype.MarkInconsistency => "Correct the bar data so marks, quantities and diameters are consistent.",
        IssueSubtype.RequirementUndefined => $"Provide the missing design information: {string.Join(", ", f.MissingInputs)}.",
        _ => "Review with the engineer of record.",
    };
}
