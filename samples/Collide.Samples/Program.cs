using Collide.Analysis;
using Collide.Audit;
using Collide.Classification;
using Collide.Geometry;
using Collide.Model;
using Collide.Reporting;
using Collide.Resolution;
using Collide.Rules;
using Collide.Units;

// Usage: dotnet run --project samples/Collide.Samples -- <repo root>
var root = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
var rules = RuleSetLoader.LoadFile(Path.Combine(root, "samples", "rules", "example-project-rules.json"));
var clock = new FixedClock(new DateTimeOffset(2026, 10, 10, 9, 0, 0, TimeSpan.Zero));
var model = DemoBuilding.Create();
var outDir = Path.Combine(root, "samples", "reports");
Directory.CreateDirectory(outDir);

// 1) Detection only, with the example rules as shipped (ProposeOnly policy; rules unverified → suspected findings).
var analyzer = new ModelAnalyzer(options: new AnalyzerOptions { MaxDegreeOfParallelism = 1 });
var state = DetectionState.Initialize(analyzer.Prepare(model, rules), analyzer);
var analysis = analyzer.Analyze(state.Prepared, AnalysisScope.All);
var issues = new DefectClassifier().Classify(analysis.Findings, model, rules);
var detection = ReportBuilder.Build(state, analysis, issues, clock);
File.WriteAllText(Path.Combine(outDir, "detection-report.json"), ReportWriter.ToJson(detection));
File.WriteAllText(Path.Combine(outDir, "detection-report.md"), ReportWriter.ToMarkdown(detection));

// 2) Resolution with automatic modification explicitly permitted (policy ApplyUnambiguous), on an in-memory model.
var permissive = rules with { Resolution = rules.Resolution with { Mode = ResolutionMode.ApplyUnambiguous } };
var store = new InMemoryModelStore(model);
var audit = new AuditLog(clock);
var engine = new ResolutionEngine(analyzer, audit, clock);
var before = DetectionState.Initialize(analyzer.Prepare(model, permissive), analyzer);
var run = engine.Run(before, store, new ResolutionOptions { RunId = "RUN-EXAMPLE" });
var after = analyzer.Analyze(run.FinalState.Prepared, AnalysisScope.All);
var resolution = ReportBuilder.Build(run.FinalState, after, run.IssuesAfter, clock, run, audit.Entries, engine.Executor.Ledger);
File.WriteAllText(Path.Combine(outDir, "resolution-report.json"), ReportWriter.ToJson(resolution));
File.WriteAllText(Path.Combine(outDir, "resolution-report.md"), ReportWriter.ToMarkdown(resolution));

Console.WriteLine($"Detection: {issues.Count} issues. Resolution: {run.Outcomes.Count(o => o.Execution?.Status == ExecutionStatus.Committed)} applied, " +
                  $"{run.Outcomes.Count(o => o.Plan.Decision == ResolutionDecision.RequiresApproval)} for approval, {run.IssuesAfter.Count} remaining. Reports in {outDir}");

/// <summary>A small synthetic building exercising the main detection and resolution paths.</summary>
internal static class DemoBuilding
{
    private static Vec3 P(double x, double y, double z) => new(x, y, z);

    private static StructuralHost Box(string id, HostCategory c, Vec3 min, Vec3 max, string level, IReadOnlyList<HostVoid>? voids = null) => new()
    {
        Id = id,
        Name = id,
        Category = c,
        Level = level,
        Region = ConvexRegion.FromBox(OrientedBox.FromMinMax(min, max), c == HostCategory.Wall
            ? (_, n) => n.Z > 0.7 ? FaceTag.Top : n.Z < -0.7 ? FaceTag.Bottom : n.Y > 0.7 ? FaceTag.Exterior : n.Y < -0.7 ? FaceTag.Interior : FaceTag.End
            : null),
        Voids = voids ?? [],
    };

    private static RebarBar Bar(string id, Vec3 a, Vec3 b, double d, BarRole role, string host, string? layer = null, string? mark = null, bool locked = false) => new()
    {
        Id = id,
        ElementId = id,
        Mark = mark ?? id.ToUpperInvariant(),
        NominalDiameter = Length.FromMillimetres(d),
        Role = role,
        HostId = host,
        Layer = layer,
        IsDesignLocked = locked,
        Path = PathFactory.Straight(a, b),
    };

    public static ReinforcementModel Create()
    {
        var hosts = new List<StructuralHost>
        {
            Box("C1", HostCategory.Column, P(0, 0, 0), P(400, 400, 3000), "Level 1"),
            Box("B1", HostCategory.Beam, P(400, 50, 2500), P(6000, 350, 3000), "Level 2"),
            Box("S1", HostCategory.Slab, P(7000, 0, 0), P(10000, 3000, 250), "Level 1",
                [new HostVoid("O1", VoidKind.Opening, OrientedBox.FromMinMax(P(8000, 1000, 0), P(8600, 1600, 250)), "Riser")]),
            Box("W1", HostCategory.Wall, P(11000, 0, 0), P(16000, 200, 3000), "Level 1",
                [new HostVoid("WIN1", VoidKind.Opening, OrientedBox.FromMinMax(P(13000, -10, 1000), P(14000, 210, 2000)), "Window")]),
        };

        var bars = new List<RebarBar>
        {
            // Beam-column joint: beam top bar passes 5 mm from a column bar (locked: design-critical).
            Bar("col-v1", P(60, 95, 60), P(60, 95, 2940), 25, BarRole.Longitudinal, "C1", locked: true),
            Bar("beam-t1", P(50, 100, 2900), P(5950, 100, 2900), 20, BarRole.Longitudinal, "B1", locked: true),
            // Slab: two bottom bars 15 mm apart (overlap), a trimmer clashing with a mesh bar, a bar through the opening,
            // a bar with insufficient bottom cover, and an exact duplicate.
            Bar("s-x1", P(7100, 500, 50), P(9900, 500, 50), 16, BarRole.Distribution, "S1", "B1"),
            Bar("s-x2", P(7100, 515, 50), P(9900, 515, 50), 16, BarRole.Distribution, "S1", "B1"),
            Bar("s-mesh", P(7100, 900, 50), P(9900, 900, 50), 12, BarRole.Distribution, "S1", "B1"),
            Bar("s-trim", P(7700, 910, 50), P(8900, 910, 50), 16, BarRole.OpeningTrimmer, "S1", "B1", locked: true),
            Bar("s-through", P(7100, 1300, 50), P(9900, 1300, 50), 12, BarRole.Distribution, "S1", "B1"),
            Bar("s-low", P(7100, 2500, 20), P(9900, 2500, 20), 12, BarRole.Distribution, "S1", "B1"),
            Bar("s-dup-a", P(7100, 2800, 50), P(9900, 2800, 50), 12, BarRole.Distribution, "S1", "B1"),
            Bar("s-dup-b", P(7100, 2800, 50), P(9900, 2800, 50), 12, BarRole.Distribution, "S1", "B1"),
            // Slab: 23 mm clear between parallel bars (25 mm required); one bar is locked, so moving the other 2 mm is
            // the single, unambiguous correction and is applied automatically.
            Bar("s-gap-a", P(7100, 2300, 50), P(9900, 2300, 50), 12, BarRole.Distribution, "S1", "B1", locked: true),
            Bar("s-gap-b", P(7100, 2335, 50), P(9900, 2335, 50), 12, BarRole.Distribution, "S1", "B1"),
            // Slab: second-layer bar resting on the bottom bars (intentional contact, not reported).
            Bar("s-y1", P(9600, 100, 64), P(9600, 700, 64), 12, BarRole.Distribution, "S1", "B2"),
            // Slab: lap of 300 mm where the example rule requires 50·d (≥ 600 mm table value for Ø12).
            Bar("s-lap-a", P(7100, 2000, 50), P(8600, 2000, 50), 12, BarRole.Distribution, "S1", "B1"),
            Bar("s-lap-b", P(8300, 2012, 50), P(9900, 2012, 50), 12, BarRole.Distribution, "S1", "B1"),
            // Wall: vertical bar through the window and one 4 mm into a trimmer.
            Bar("w-v-through", P(13500, 40, 50), P(13500, 40, 2950), 12, BarRole.Distribution, "W1", "EF-V"),
            Bar("w-v-near", P(12960, 40, 50), P(12960, 40, 2950), 12, BarRole.Distribution, "W1", "EF-V"),
            Bar("w-trim", P(12950, 40, 700), P(12950, 40, 2300), 16, BarRole.OpeningTrimmer, "W1", "EF-V", locked: true),
        };

        return ReinforcementModel.Create("demo-building", bars, hosts);
    }
}
