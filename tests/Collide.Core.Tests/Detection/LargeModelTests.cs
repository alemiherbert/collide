using System.Diagnostics;
using Collide.Analysis;
using Collide.Classification;
using Collide.Core.Tests.Fixtures;
using Collide.Findings;
using Collide.Model;
using static Collide.Core.Tests.Fixtures.Fx;
using Xunit.Abstractions;

namespace Collide.Core.Tests.Detection;

/// <summary>Fixture 18: large model — performance, broad-phase pruning, duplicate detection and determinism.</summary>
public class LargeModelTests(ITestOutputHelper output)
{
    private const int ColumnCount = 800;

    /// <summary>
    /// A 12 m × 12 m slab with two-layer meshes at 100 mm top and bottom (4 × 120 = 480 bars, ~28,800 intentional
    /// mesh contacts), plus 800 columns of 12 bars each in their own hosts (9,600 bars), plus 25 injected duplicates:
    /// 10,105 bars in total.
    /// </summary>
    private static ReinforcementModel BuildLargeModel(out IReadOnlyList<string> duplicateIds)
    {
        var hosts = new List<StructuralHost> { Box("S1", HostCategory.Slab, P(0, 0, 0), P(12000, 12000, 250)) };
        var bars = new List<RebarBar>();
        for (var i = 0; i < 120; i++)
        {
            var c = 50 + 100 * i;
            bars.Add(Bar($"b1-{i:D3}", P(40, c, 46), P(11960, c, 46), 12, BarRole.Distribution, "S1", "B1"));
            bars.Add(Bar($"b2-{i:D3}", P(c, 40, 58), P(c, 11960, 58), 12, BarRole.Distribution, "S1", "B2"));
            bars.Add(Bar($"t2-{i:D3}", P(c, 40, 192), P(c, 11960, 192), 12, BarRole.Distribution, "S1", "T2"));
            bars.Add(Bar($"t1-{i:D3}", P(40, c, 204), P(11960, c, 204), 12, BarRole.Distribution, "S1", "T1"));
        }

        for (var k = 0; k < ColumnCount; k++)
        {
            var x0 = 20000 + (k % 40) * 1000;
            var y0 = (k / 40) * 1000;
            var id = $"C{k:D2}";
            hosts.Add(Box(id, HostCategory.Column, P(x0, y0, 0), P(x0 + 400, y0 + 400, 3000)));
            for (var j = 0; j < 12; j++)
            {
                var (x, y) = j switch
                {
                    < 4 => (60 + 93.33 * j, 60.0),
                    < 8 => (60 + 93.33 * (j - 4), 340.0),
                    10 => (60.0, 200.0),
                    11 => (340.0, 200.0),
                    _ => (j == 8 ? 60.0 : 340.0, 130.0),
                };
                bars.Add(Bar($"{id}-v{j:D2}", P(x0 + x, y0 + y, 60), P(x0 + x, y0 + y, 2940), 20, BarRole.Longitudinal, id));
            }
        }

        var dups = new List<string>();
        for (var d = 0; d < 25; d++)
        {
            var original = bars[d * 37 % bars.Count];
            var copy = original with { Id = original.Id + "-dup", ElementId = original.ElementId + "-dup" };
            bars.Add(copy);
            dups.Add(copy.Id);
        }

        duplicateIds = dups;
        return Build(hosts, bars);
    }

    [Fact]
    public void LargeModelIsAnalysedQuicklyWithBroadPhasePruningAndExactDuplicates()
    {
        var model = BuildLargeModel(out var duplicates);
        var rules = TestRules.Standard();
        var analyzer = new ModelAnalyzer();
        analyzer.Analyze(Build([Slab()], []), rules); // JIT warm-up

        var sw = Stopwatch.StartNew();
        var result = analyzer.Analyze(model, rules);
        sw.Stop();
        var issues = new DefectClassifier().Classify(result.Findings, model, rules);
        output.WriteLine($"{model.Bars.Count} bars, {result.Statistics.SolidPrimitives} primitives, {result.Statistics.CandidateBarPairs} candidate pairs, " +
                         $"{result.Statistics.PrimitivePairsMeasured} primitive pairs, pruning {result.Statistics.BroadPhasePruning:P3}, {sw.ElapsedMilliseconds} ms, " +
                         $"{result.Suppressed.Count} suppressed, {issues.Count} issues");

        // Duplicate detection: exactly the injected duplicates, each paired with its original.
        var dupIssues = issues.Where(i => i.Subtype == IssueSubtype.Duplicate).ToArray();
        Assert.Equal(duplicates.Count, dupIssues.Length);
        Assert.All(duplicates, d => Assert.Contains(dupIssues, i => i.Bars.Any(b => b.Id == d)));
        // No false clashes from the ~28,800 mesh crossings: all are intentional contacts.
        Assert.DoesNotContain(issues, i => i.Category == IssueCategory.RebarToRebar && i.Subtype != IssueSubtype.Duplicate);
        Assert.True(result.Suppressed.Count >= 4 * 120 * 120 / 2, $"{result.Suppressed.Count} suppressed contacts");
        // Broad phase must eliminate the overwhelming majority of the ~51 million possible pairs.
        Assert.True(model.Bars.Count > 10_000);
        Assert.True(result.Statistics.BroadPhasePruning > 0.99, $"pruning {result.Statistics.BroadPhasePruning:P2}");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), $"analysis took {sw.Elapsed}");
    }

    [Fact]
    public void RepeatedAnalysisOfALargeModelIsIdentical()
    {
        var model = BuildLargeModel(out _);
        var rules = TestRules.Standard();
        var a = new ModelAnalyzer(options: new AnalyzerOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }).Analyze(model, rules);
        var b = new ModelAnalyzer(options: new AnalyzerOptions { MaxDegreeOfParallelism = 1 }).Analyze(model, rules);
        Assert.Equal(a.Findings.Select(f => f.Fingerprint), b.Findings.Select(f => f.Fingerprint));
        Assert.Equal(a.Findings.Select(f => string.Join(";", f.Measurements)), b.Findings.Select(f => string.Join(";", f.Measurements)));
        Assert.Equal(a.Suppressed.Count, b.Suppressed.Count);
    }

    [Fact]
    public void IncrementalUpdateOfALargeModelTouchesOnlyTheAffectedRegion()
    {
        var model = BuildLargeModel(out _);
        var engine = new Collide.Monitoring.IncrementalDetectionEngine(new ModelAnalyzer(), TestRules.Standard());
        engine.InitializeNow(model);
        var bar = model.Bars["b1-060"];
        var sw = Stopwatch.StartNew();
        var update = engine.ApplyNow(new ModelDelta { UpsertBars = [bar.Translate(new Collide.Geometry.Vec3(0, 85, 0))] })!;
        sw.Stop();
        output.WriteLine($"incremental update: {update.BarsAnalysed} bars re-analysed in {sw.ElapsedMilliseconds} ms (of {model.Bars.Count})");
        Assert.True(update.BarsAnalysed < 50, $"{update.BarsAnalysed} bars re-analysed");
        var spacing = Assert.Single(update.Issues, i => i.Subtype == IssueSubtype.InsufficientClearSpacing && i.Bars.Any(b => b.Id == "b1-060"));
        Assert.Equal(3.0, M(spacing, "clearSpacing"), 6); // Ø12 bars now 15 mm apart (centres)
        Assert.Equal([spacing.Id], update.NewIssueIds);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"incremental update took {sw.Elapsed}");
    }
}
