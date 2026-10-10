using Collide.Analysis;
using Collide.Classification;
using Collide.Findings;
using Collide.Geometry;
using Collide.Model;
using Collide.Rules;
using Collide.Units;

namespace Collide.Core.Tests.Fixtures;

/// <summary>Synthetic geometry builders. All coordinates in millimetres.</summary>
internal static class Fx
{
    public static Length Mm(double v) => Length.FromMillimetres(v);
    public static Vec3 P(double x, double y, double z) => new(x, y, z);

    public static StructuralHost Box(string id, HostCategory category, Vec3 min, Vec3 max, string? level = "L1", IReadOnlyList<HostVoid>? voids = null)
    {
        Func<int, Vec3, FaceTag>? tags = category == HostCategory.Wall
            ? (_, n) => n.Z > 0.7 ? FaceTag.Top : n.Z < -0.7 ? FaceTag.Bottom : n.Y > 0.7 ? FaceTag.Exterior : n.Y < -0.7 ? FaceTag.Interior : FaceTag.End
            : null;
        return new StructuralHost
        {
            Id = id,
            Category = category,
            Name = id,
            Level = level,
            Region = ConvexRegion.FromBox(OrientedBox.FromMinMax(min, max), tags),
            Voids = voids ?? [],
        };
    }

    public static HostVoid Opening(string id, Vec3 min, Vec3 max, VoidKind kind = VoidKind.Opening) =>
        new(id, kind, OrientedBox.FromMinMax(min, max), $"{kind} {id}");

    public static RebarBar Bar(string id, Vec3 a, Vec3 b, double d, BarRole role, string host, string? layer = null, string? mark = null, string? setId = null) => new()
    {
        Id = id,
        ElementId = id.Split('#')[0],
        NominalDiameter = Mm(d),
        Role = role,
        HostId = host,
        Layer = layer,
        Mark = mark,
        SetId = setId,
        Path = PathFactory.Straight(a, b),
    };

    public static RebarBar Shaped(string id, BarPath path, double d, BarRole role, string host, string? layer = null) => new()
    {
        Id = id,
        ElementId = id.Split('#')[0],
        NominalDiameter = Mm(d),
        Role = role,
        HostId = host,
        Layer = layer,
        Path = path,
    };

    public static ReinforcementModel Build(IEnumerable<StructuralHost> hosts, IEnumerable<RebarBar> bars, IEnumerable<RebarSet>? sets = null,
        IEnumerable<PlacementZone>? zones = null, IEnumerable<SpliceDefinition>? splices = null, IEnumerable<Accessory>? accessories = null) =>
        ReinforcementModel.Create("synthetic", bars, hosts, sets, zones, accessories, splices);

    public static (AnalysisResult Result, IReadOnlyList<Issue> Issues) Detect(ReinforcementModel model, RuleSet? rules = null)
    {
        rules ??= TestRules.Standard();
        var analyzer = new ModelAnalyzer(options: new AnalyzerOptions { MaxDegreeOfParallelism = 2 });
        var result = analyzer.Analyze(model, rules);
        return (result, new DefectClassifier().Classify(result.Findings, model, rules));
    }

    public static Issue Single(IEnumerable<Issue> issues, IssueSubtype subtype) => Assert.Single(issues, i => i.Subtype == subtype);

    public static double M(Issue issue, string name) => Assert.Single(issue.Measurements, m => m.Name == name).Value;

    public static string Dump(IEnumerable<Issue> issues) =>
        string.Join("\n", issues.Select(i => $"{i.Subtype} [{string.Join(",", i.Bars.Select(b => b.Id))}] {string.Join("; ", i.Measurements)} :: {i.Explanation}"));

    public static IEnumerable<Issue> Defects(IEnumerable<Issue> issues) =>
        issues.Where(i => i.Status is IssueStatus.ConfirmedDefect or IssueStatus.SuspectedDefect or IssueStatus.Warning);

    // ---- standard hosts used by several fixtures

    /// <summary>3 m × 3 m × 250 mm slab at z ∈ [0, 250].</summary>
    public static StructuralHost Slab(string id = "S1", IReadOnlyList<HostVoid>? voids = null) =>
        Box(id, HostCategory.Slab, P(0, 0, 0), P(3000, 3000, 250), voids: voids);

    /// <summary>6 m beam, 300 wide, 500 deep: x ∈ [0, 6000], y ∈ [0, 300], z ∈ [0, 500].</summary>
    public static StructuralHost Beam(string id = "B1") => Box(id, HostCategory.Beam, P(0, 0, 0), P(6000, 300, 500)) with { Axis = (P(0, 150, 250), P(6000, 150, 250)) };
}

/// <summary>
/// Synthetic project rules for tests. Values are illustrative test values only, NOT design-code provisions.
/// </summary>
internal static class TestRules
{
    private static readonly RuleSource Spec = new() { Document = "Test project detailing specification", Clause = "T", Edition = "test" };

    public static RuleSet Standard(bool verified = true, ResolutionMode mode = ResolutionMode.ApplyUnambiguous) => new()
    {
        Name = "test-rules",
        Revision = "1",
        DesignStandard = new DesignStandardInfo { Name = "Synthetic test standard", Version = "0" },
        Project = new ProjectParameters { MaximumAggregateSize = Fx.Mm(16) },
        Cover =
        [
            new CoverRule { Id = "COV-BEAM", Source = Spec, Verified = verified, Scope = new RuleScope { HostCategories = [HostCategory.Beam] }, MinimumCover = Fx.Mm(40) },
            new CoverRule { Id = "COV-COL", Source = Spec, Verified = verified, Scope = new RuleScope { HostCategories = [HostCategory.Column] }, MinimumCover = Fx.Mm(40) },
            new CoverRule { Id = "COV-SLAB-TB", Source = Spec, Verified = verified, Scope = new RuleScope { HostCategories = [HostCategory.Slab], FaceTags = [FaceTag.Top, FaceTag.Bottom] }, MinimumCover = Fx.Mm(25) },
            new CoverRule { Id = "COV-SLAB-OTHER", Source = Spec, Verified = verified, Scope = new RuleScope { HostCategories = [HostCategory.Slab] }, MinimumCover = Fx.Mm(30) },
            new CoverRule { Id = "COV-WALL", Source = Spec, Verified = verified, Scope = new RuleScope { HostCategories = [HostCategory.Wall] }, MinimumCover = Fx.Mm(30) },
            new CoverRule { Id = "COV-FOUND", Source = Spec, Verified = verified, Scope = new RuleScope { HostCategories = [HostCategory.Footing, HostCategory.PileCap] }, MinimumCover = Fx.Mm(50) },
            new CoverRule { Id = "COV-OPENING", Source = Spec, Verified = verified, Scope = new RuleScope { FaceTags = [FaceTag.Opening] }, MinimumCover = Fx.Mm(25) },
        ],
        ClearSpacing =
        [
            new ClearSpacingRule { Id = "SPC-1", Source = Spec, Verified = verified, BarDiameterFactor = 1.0, AggregateAllowance = Fx.Mm(5), AbsoluteMinimum = Fx.Mm(25), Relation = SpacingRelation.SameLayer },
        ],
        LapLengths = [new LapLengthRule { Id = "LAP-1", Source = Spec, Verified = verified, DiameterFactor = 40, Minimum = Fx.Mm(300) }],
        NonContactLaps = [new NonContactLapRule { Id = "LAP-NC", Source = Spec, Verified = verified, MaximumClearOffset = Fx.Mm(50) }],
        DevelopmentLengths =
        [
            new DevelopmentLengthRule { Id = "DEV-STRAIGHT", Source = Spec, Verified = verified, DiameterFactor = 35, AnchorageType = AnchorageType.Straight },
            new DevelopmentLengthRule { Id = "DEV-HOOKED", Source = Spec, Verified = verified, DiameterFactor = 20, AnchorageType = AnchorageType.Hooked },
        ],
        Bends =
        [
            new BendRule { Id = "BEND-LINK", Source = Spec, Verified = verified, Scope = new RuleScope { BarRoles = [BarRole.Stirrup, BarRole.Tie, BarRole.Crosstie] }, MinimumInnerDiameterFactor = 2 },
            new BendRule { Id = "BEND-MAIN", Source = Spec, Verified = verified, MinimumInnerDiameterFactor = 4 },
        ],
        Hooks = [new HookRule { Id = "HOOK-90", Source = Spec, Verified = verified, HookAngle = Angle.FromDegrees(90), ExtensionDiameterFactor = 12 }],
        Placement =
        [
            new PlacementToleranceRule
            {
                Id = "PLC-SLAB", Source = Spec, Scope = new RuleScope { HostCategories = [HostCategory.Slab], BarRoles = [BarRole.Distribution, BarRole.OpeningTrimmer] },
                MaximumDisplacement = Fx.Mm(25), AutoApplyMaximumDisplacement = Fx.Mm(15), AllowedAxes = [Axis.X, Axis.Y],
            },
            new PlacementToleranceRule
            {
                Id = "PLC-WALL", Source = Spec, Scope = new RuleScope { HostCategories = [HostCategory.Wall], BarRoles = [BarRole.Distribution] },
                MaximumDisplacement = Fx.Mm(20), AllowedAxes = [Axis.X, Axis.Z],
            },
            new PlacementToleranceRule
            {
                Id = "PLC-BEAM-LONG", Source = Spec, Scope = new RuleScope { HostCategories = [HostCategory.Beam], BarRoles = [BarRole.Longitudinal] },
                MaximumDisplacement = Fx.Mm(10), AllowedAxes = [Axis.Y, Axis.Z],
            },
        ],
        IntentionalContacts =
        [
            new IntentionalContactRule
            {
                Id = "IC-MESH", Description = "Orthogonal mesh layers resting on each other", Source = Spec,
                RolesA = [BarRole.Distribution, BarRole.OpeningTrimmer], RolesB = [BarRole.Distribution, BarRole.OpeningTrimmer],
                RequireDifferentLayers = true, MinimumAngle = Angle.FromDegrees(60), MaximumPenetration = Fx.Mm(0.5),
            },
            new IntentionalContactRule
            {
                Id = "IC-LINK", Description = "Longitudinal bar tied against a stirrup/tie", Source = Spec,
                RolesA = [BarRole.Longitudinal, BarRole.Starter], RolesB = [BarRole.Stirrup, BarRole.Tie, BarRole.Crosstie], MaximumPenetration = Fx.Mm(0.5),
            },
        ],
        Resolution = new ResolutionPolicy { Mode = mode },
    };
}
