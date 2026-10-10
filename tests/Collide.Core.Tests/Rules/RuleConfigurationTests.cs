using System.Text.Json;
using Collide.Analysis;
using Collide.Audit;
using Collide.Classification;
using Collide.Core.Tests.Detection;
using Collide.Core.Tests.Fixtures;
using Collide.Findings;
using Collide.Model;
using Collide.Reporting;
using Collide.Resolution;
using Collide.Rules;
using static Collide.Core.Tests.Fixtures.Fx;

namespace Collide.Core.Tests.Rules;

public class RuleConfigurationTests
{
    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Collide.slnx"))) dir = dir.Parent;
        return Path.Combine([dir!.FullName, .. parts]);
    }

    [Fact]
    public void ExampleRuleFileLoadsValidatesAndIsUnverified()
    {
        var rules = RuleSetLoader.LoadFile(RepoFile("samples", "rules", "example-project-rules.json"));
        Assert.Equal("Example project rules", rules.Name);
        Assert.NotEmpty(rules.Cover);
        Assert.Equal(25, rules.Cover.Single(c => c.Id == "COV-SLAB").MinimumCover.Millimetres);
        Assert.Equal(Collide.Units.Length.FromMillimetres(20), rules.Project.MaximumAggregateSize);
        Assert.Equal(ResolutionMode.ProposeOnly, rules.Resolution.Mode);
        // Example values are not engineer-verified, so they can never produce "confirmed" compliance findings.
        Assert.All(rules.AllRules().Where(r => r is not PlacementToleranceRule and not IntentionalContactRule), r => Assert.False(r.Verified, r.Id));
        Assert.Empty(RuleSetLoader.Validate(rules));
    }

    [Fact]
    public void UnitlessLengthsAreRejected()
    {
        const string json = """
            { "name": "x", "designStandard": { "name": "s" },
              "cover": [ { "id": "C1", "source": { "document": "d" }, "minimumCover": 40 } ] }
            """;
        var ex = Assert.Throws<RuleConfigurationException>(() => RuleSetLoader.Load(json));
        Assert.Contains(ex.Errors, e => e.Contains("Bare numbers are not accepted"));
    }

    [Fact]
    public void ValidationReportsAllProblems()
    {
        const string json = """
            {
              "name": "bad", "designStandard": { "name": "s" },
              "cover": [ { "id": "C1", "source": { "document": "d" }, "minimumCover": "-5 mm" },
                         { "id": "C1", "source": { "document": "d" }, "minimumCover": "40 mm" } ],
              "clearSpacing": [ { "id": "S1", "source": { "document": "d" } } ],
              "lapLengths": [ { "id": "L1", "source": { "document": "d" } } ],
              "placement": [ { "id": "P1", "source": { "document": "d" }, "maximumDisplacement": "5 mm", "autoApplyMaximumDisplacement": "10 mm" } ],
              "exceptions": [ { "id": "E1", "approvalReference": "", "barIds": [] } ]
            }
            """;
        var ex = Assert.Throws<RuleConfigurationException>(() => RuleSetLoader.Load(json));
        Assert.Contains(ex.Errors, e => e.Contains("Duplicate rule id 'C1'"));
        Assert.Contains(ex.Errors, e => e.Contains("must be a positive length"));
        Assert.Contains(ex.Errors, e => e.Contains("defines no term"));
        Assert.Contains(ex.Errors, e => e.Contains("defines no table, factor or minimum"));
        Assert.Contains(ex.Errors, e => e.Contains("exceeds maximumDisplacement"));
        Assert.Contains(ex.Errors, e => e.Contains("must name at least one bar"));
    }

    [Fact]
    public void MissingRequiredSourceIsRejected()
    {
        const string json = """{ "name": "x", "designStandard": { "name": "s" }, "cover": [ { "id": "C1", "minimumCover": "40 mm" } ] }""";
        Assert.Throws<RuleConfigurationException>(() => RuleSetLoader.Load(json));
    }

    [Fact]
    public void RuleSetRoundTripsAndHashIsStable()
    {
        var rules = TestRules.Standard();
        var json = RuleSetLoader.ToJson(rules);
        var back = RuleSetLoader.Load(json);
        Assert.Equal(RuleSetLoader.Hash(rules), RuleSetLoader.Hash(back));
        Assert.NotEqual(RuleSetLoader.Hash(rules), RuleSetLoader.Hash(rules with { Revision = "2" }));
    }

    [Fact]
    public void MostSpecificRuleWins()
    {
        var src = new RuleSource { Document = "d" };
        IReadOnlyList<CoverRule> rules =
        [
            new CoverRule { Id = "general", Source = src, MinimumCover = Mm(30) },
            new CoverRule { Id = "slab", Source = src, Scope = new RuleScope { HostCategories = [HostCategory.Slab] }, MinimumCover = Mm(25) },
            new CoverRule { Id = "slab-bottom", Source = src, Scope = new RuleScope { HostCategories = [HostCategory.Slab], FaceTags = [Collide.Geometry.FaceTag.Bottom] }, MinimumCover = Mm(20) },
            new CoverRule { Id = "host-S9", Source = src, Scope = new RuleScope { HostIds = ["S9"] }, MinimumCover = Mm(50) },
        ];
        RuleContext Ctx(string host, Collide.Geometry.FaceTag face) => new() { HostCategory = HostCategory.Slab, HostId = host, FaceTag = face };
        Assert.Equal("slab-bottom", RuleEngine.Find(rules, Ctx("S1", Collide.Geometry.FaceTag.Bottom))!.Rule.Id);
        Assert.Equal("slab", RuleEngine.Find(rules, Ctx("S1", Collide.Geometry.FaceTag.Top))!.Rule.Id);
        Assert.Equal("host-S9", RuleEngine.Find(rules, Ctx("S9", Collide.Geometry.FaceTag.Bottom))!.Rule.Id);
        Assert.Equal("general", RuleEngine.Find(rules, new RuleContext { HostCategory = HostCategory.Beam })!.Rule.Id);
    }
}

public class ReportTests
{
    [Fact]
    public void ReportContainsSummaryResolutionAuditAndRoundTripsAsJson()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 10, 10, 9, 0, 0, TimeSpan.Zero));
        var model = Build([Beam()], [
            Shaped("st", DetectionFixtureTests.Stirrup(1000), 10, BarRole.Stirrup, "B1"),
            Bar("side", P(50, 55, 250), P(5950, 55, 250), 20, BarRole.Longitudinal, "B1"),
            Bar("low", P(50, 150, 25), P(5950, 150, 25), 20, BarRole.Longitudinal, "B1") with { IsDesignLocked = true }]);
        var store = new InMemoryModelStore(model);
        var analyzer = new ModelAnalyzer(options: new AnalyzerOptions { MaxDegreeOfParallelism = 1 });
        var audit = new AuditLog(clock);
        var engine = new ResolutionEngine(analyzer, audit, clock);
        var state = DetectionState.Initialize(analyzer.Prepare(model, TestRules.Standard()), analyzer);
        var run = engine.Run(state, store);
        var analysis = analyzer.Analyze(run.FinalState.Prepared, AnalysisScope.All);
        var report = ReportBuilder.Build(run.FinalState, analysis, run.IssuesAfter, clock, run, audit.Entries, engine.Executor.Ledger);

        Assert.Equal(1, report.Summary.AutoResolved);
        Assert.True(report.Summary.TotalIssues >= 2);
        Assert.Single(report.Resolution!.Corrections);
        Assert.Contains(report.Resolution.Outcomes, o => o.Decision == "AutoApply" && o.ExecutionStatus == "Committed" && o.Selected!.Invariants.All(i => i.Passed));
        Assert.NotEmpty(report.Audit);

        var json = ReportWriter.ToJson(report);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("1.0", root.GetProperty("schemaVersion").GetString());
        Assert.True(root.GetProperty("issues").GetArrayLength() >= 2);
        var translation = root.GetProperty("resolution").GetProperty("corrections")[0].GetProperty("modifications")[0].GetProperty("translation");
        Assert.Equal(5.0, translation.GetProperty("y").GetDouble(), 9); // millimetres

        var md = ReportWriter.ToMarkdown(report);
        foreach (var heading in new[] { "## Summary", "## Issues", "## Resolution", "### Correction and rollback history", "### Audit trail", "## Performance", "## Limitations" })
            Assert.Contains(heading, md);

        // Identical inputs → identical report content (timestamps come from the injected clock).
        var clock2 = new FixedClock(new DateTimeOffset(2026, 10, 10, 9, 0, 0, TimeSpan.Zero));
        var audit2 = new AuditLog(clock2);
        var engine2 = new ResolutionEngine(analyzer, audit2, clock2);
        var run2 = engine2.Run(DetectionState.Initialize(analyzer.Prepare(model, TestRules.Standard()), analyzer), new InMemoryModelStore(model));
        var report2 = ReportBuilder.Build(run2.FinalState, analyzer.Analyze(run2.FinalState.Prepared, AnalysisScope.All), run2.IssuesAfter, clock2, run2, audit2.Entries, engine2.Executor.Ledger);
        Assert.Equal(Strip(ReportWriter.ToJson(report)), Strip(ReportWriter.ToJson(report2)));
    }

    /// <summary>Removes timing-dependent performance figures, which legitimately vary between runs.</summary>
    private static string Strip(string json)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        node.Remove("performance");
        return node.ToJsonString();
    }
}
