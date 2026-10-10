using Collide.Analysis;
using Collide.Audit;
using Collide.Findings;
using Collide.Geometry;
using Collide.Integration;
using Collide.Model;
using Collide.Monitoring;
using Collide.Presentation;
using Collide.Presentation.ViewModels;
using Collide.Resolution;
using Collide.Rules;
using Collide.Units;

namespace Collide.Presentation.Tests;

/// <summary>Pane behaviour against the real live monitor and an in-memory model (no Revit).</summary>
public class ClashPaneViewModelTests
{
    private sealed class RecordingInteraction(CorrectionService corrections) : IModelInteraction
    {
        public List<(string Action, string[] Ids)> Calls { get; } = [];

        public Task SelectElementsAsync(IReadOnlyCollection<string> elementIds, CancellationToken ct = default)
        {
            Calls.Add(("select", elementIds.ToArray()));
            return Task.CompletedTask;
        }

        public Task ZoomToElementsAsync(IReadOnlyCollection<string> elementIds, CancellationToken ct = default)
        {
            Calls.Add(("zoom", elementIds.ToArray()));
            return Task.CompletedTask;
        }

        public Task<ResolutionPlan?> InspectCorrectionsAsync(string issueId, CancellationToken ct = default) => corrections.InspectAsync(issueId, ct);
        public Task<ExecutionResult> ApplyCorrectionAsync(string issueId, string candidateId, CancellationToken ct = default) => corrections.ApplyAsync(issueId, candidateId, ct);
    }

    private static readonly RuleSource Src = new() { Document = "test" };

    private static RuleSet Rules() => new()
    {
        Name = "pane-test",
        DesignStandard = new DesignStandardInfo { Name = "test" },
        RequiredChecks = [],
        Cover = [new CoverRule { Id = "C", Source = Src, MinimumCover = Length.FromMillimetres(25), Verified = true }],
        ClearSpacing = [new ClearSpacingRule { Id = "S", Source = Src, AbsoluteMinimum = Length.FromMillimetres(25), Verified = true }],
        Placement = [new PlacementToleranceRule { Id = "P", Source = Src, MaximumDisplacement = Length.FromMillimetres(25), AllowedAxes = [Axis.Y] }],
        Resolution = new ResolutionPolicy { Mode = ResolutionMode.ProposeOnly },
    };

    private static StructuralHost Slab() => new()
    {
        Id = "S1",
        Name = "Slab 1",
        Category = HostCategory.Slab,
        Level = "L1",
        Region = ConvexRegion.FromBox(OrientedBox.FromMinMax(new Vec3(0, 0, 0), new Vec3(3000, 3000, 250))),
    };

    private static RebarBar Bar(string id, double y, double z = 50, string? mark = null) => new()
    {
        Id = id,
        ElementId = id,
        Mark = mark ?? id.ToUpperInvariant(),
        NominalDiameter = Length.FromMillimetres(20),
        Role = BarRole.Distribution,
        HostId = "S1",
        Layer = "B1",
        Path = PathFactory.Straight(new Vec3(100, y, z), new Vec3(2900, y, z)),
    };

    private sealed class Rig : IAsyncDisposable
    {
        public required InMemoryModelStore Store { get; init; }
        public required LiveClashMonitor Monitor { get; init; }
        public required ClashPaneViewModel Pane { get; init; }
        public required RecordingInteraction Interaction { get; init; }

        public async Task Idle()
        {
            var t = Monitor.WhenIdleAsync();
            Assert.Same(t, await Task.WhenAny(t, Task.Delay(TimeSpan.FromSeconds(20))));
        }

        public async ValueTask DisposeAsync()
        {
            Pane.Dispose();
            await Monitor.DisposeAsync();
        }
    }

    private static Rig Create(params RebarBar[] bars)
    {
        var store = new InMemoryModelStore(ReinforcementModel.Create("pane", bars, [Slab()]));
        var analyzer = new ModelAnalyzer(options: new AnalyzerOptions { MaxDegreeOfParallelism = 1 });
        var rules = Rules();
        var engine = new IncrementalDetectionEngine(analyzer, rules);
        var monitor = new LiveClashMonitor(engine, new InMemoryDeltaSource(store), new MonitorOptions { Debounce = TimeSpan.Zero, RetryDelay = TimeSpan.Zero });
        store.Changed += monitor.NotifyChanged;
        var corrections = new CorrectionService(engine, new ResolutionEngine(analyzer, new AuditLog()), () => store, DirectApiContext.Instance, () => "Test Engineer");
        var interaction = new RecordingInteraction(corrections);
        var pane = new ClashPaneViewModel(monitor, interaction, ImmediateDispatcher.Instance);
        return new Rig { Store = store, Monitor = monitor, Pane = pane, Interaction = interaction };
    }

    [Fact]
    public async Task PaneUpdatesAutomaticallyWhenAClashIsModelled()
    {
        await using var rig = Create(Bar("a", 1000));
        await rig.Monitor.StartAsync();
        await rig.Idle();
        Assert.Empty(rig.Pane.Issues);
        Assert.Contains("Validated revision", rig.Pane.ValidatedText);

        rig.Store.ApplyUserDelta(new ModelDelta { UpsertBars = [Bar("b", 1015)] }, added: ["b"]);
        await rig.Idle();

        var item = Assert.Single(rig.Pane.Issues);
        Assert.Equal("Parallel overlap", item.Title);
        Assert.True(item.IsNew);
        Assert.Equal(1, rig.Pane.HighCount);
        Assert.Equal(1, rig.Pane.NewCount);
        Assert.Equal(["a", "b", "S1"], item.ElementIds);
        Assert.Contains("A Ø20", item.Bars);
        Assert.Equal(MonitorStatus.Idle, rig.Pane.Status);
    }

    [Fact]
    public async Task SeverityFilterAndGroupingApply()
    {
        // High: parallel overlap; Medium: clear spacing; plus a cover issue (High) on another bar.
        await using var rig = Create(Bar("a", 1000), Bar("b", 1015), Bar("c", 1500), Bar("d", 1530), Bar("e", 2500, z: 25));
        await rig.Monitor.StartAsync();
        await rig.Idle();
        Assert.Equal(3, rig.Pane.Issues.Count);

        rig.Pane.MinimumSeverity = Severity.High;
        Assert.Equal(2, rig.Pane.Issues.Count);
        Assert.All(rig.Pane.Issues, i => Assert.True(i.Severity >= Severity.High));

        rig.Pane.MinimumSeverity = Severity.Informational;
        rig.Pane.Grouping = IssueGrouping.Category;
        Assert.Equal(["Constructability", "Rebar to concrete", "Rebar to rebar"], rig.Pane.Groups.Select(g => g.Key));
        rig.Pane.Grouping = IssueGrouping.Severity;
        Assert.Equal(["High", "Medium"], rig.Pane.Groups.Select(g => g.Key));
        Assert.Equal(2, rig.Pane.Groups[0].Count);
    }

    [Fact]
    public async Task StaleIssuesAreMarkedWhileTheirRegionIsRecalculated()
    {
        await using var rig = Create(Bar("a", 1000), Bar("b", 1015));
        await rig.Monitor.StartAsync();
        await rig.Idle();
        var id = Assert.Single(rig.Pane.Issues).Id;

        rig.Pane.Apply(new MonitorUpdate
        {
            Status = MonitorStatus.Pending,
            Scope = MonitoringScope.EntireModel,
            Issues = rig.Monitor.LastUpdate!.Issues,
            StaleIssueIds = [id],
            PendingElementCount = 1,
        });
        Assert.True(rig.Pane.Issues.Single().IsStale);
        Assert.Equal(1, rig.Pane.StaleCount);
        Assert.Contains("waiting to recalculate", rig.Pane.StatusText);
    }

    [Fact]
    public async Task SelectAndZoomForwardElementIds()
    {
        await using var rig = Create(Bar("a", 1000), Bar("b", 1015));
        await rig.Monitor.StartAsync();
        await rig.Idle();
        Assert.False(rig.Pane.SelectElementsCommand.CanExecute(null));
        rig.Pane.SelectedIssue = rig.Pane.Issues.Single();
        Assert.True(rig.Pane.SelectElementsCommand.CanExecute(null));

        await rig.Pane.SelectElementsCommand.ExecuteAsync(null);
        await rig.Pane.ZoomToElementsCommand.ExecuteAsync(null);
        Assert.Equal([("select", new[] { "a", "b", "S1" }), ("zoom", new[] { "a", "b", "S1" })], rig.Interaction.Calls.Select(c => (c.Action, c.Ids)).ToArray());
    }

    [Fact]
    public async Task InspectingAndApprovingACorrectionResolvesTheIssueThroughLiveRevalidation()
    {
        // Clear spacing 15 mm < 25 mm; ProposeOnly policy: corrections are never applied without approval.
        await using var rig = Create(Bar("a", 1000), Bar("b", 1035));
        await rig.Monitor.StartAsync();
        await rig.Idle();
        rig.Pane.SelectedIssue = rig.Pane.Issues.Single();

        await rig.Pane.InspectCorrectionsCommand.ExecuteAsync(null);
        Assert.NotEmpty(rig.Pane.Corrections);
        Assert.Contains("Requires approval", rig.Pane.ActionMessage);
        Assert.Equal(0, rig.Store.CommitCount);
        var recommended = Assert.Single(rig.Pane.Corrections, c => c.IsRecommended);
        Assert.Contains(recommended.Invariants, i => i.StartsWith("✓ INV-01"));

        rig.Pane.SelectedCorrection = recommended;
        await rig.Pane.ApplyCorrectionCommand.ExecuteAsync(null);
        Assert.Contains("Applied", rig.Pane.ActionMessage);
        await rig.Idle(); // the add-in's own transaction is picked up by the live monitor

        Assert.Empty(rig.Pane.Issues);
        Assert.Equal(1, rig.Pane.ResolvedCount);
        Assert.Single(rig.Pane.RecentlyResolved);
        Assert.Contains("after correction", rig.Pane.ValidatedText);
        Assert.Equal(1, rig.Store.CommitCount);
    }

    [Fact]
    public async Task PauseResumeTogglesMonitoring()
    {
        await using var rig = Create(Bar("a", 1000));
        await rig.Monitor.StartAsync();
        await rig.Idle();
        rig.Pane.PauseResumeCommand.Execute(null);
        Assert.True(rig.Pane.IsPaused);
        Assert.Equal(MonitorStatus.Paused, rig.Monitor.Status);
        rig.Store.ApplyUserDelta(new ModelDelta { UpsertBars = [Bar("b", 1015)] }, added: ["b"]);
        await rig.Idle();
        Assert.Empty(rig.Pane.Issues);

        rig.Pane.PauseResumeCommand.Execute(null);
        await rig.Idle();
        Assert.False(rig.Pane.IsPaused);
        Assert.Single(rig.Pane.Issues);
    }
}
