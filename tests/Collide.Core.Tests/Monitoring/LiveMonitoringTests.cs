using Collide.Analysis;
using Collide.Audit;
using Collide.Classification;
using Collide.Core.Tests.Detection;
using Collide.Core.Tests.Fixtures;
using Collide.Findings;
using Collide.Geometry;
using Collide.Model;
using Collide.Monitoring;
using Collide.Resolution;
using static Collide.Core.Tests.Fixtures.Fx;

namespace Collide.Core.Tests.Monitoring;

/// <summary>
/// Live detection driven by simulated change events. These tests exercise the Revit-independent pipeline only; they are
/// not evidence that Revit's DocumentChanged subscription or external events behave as assumed.
/// </summary>
public class LiveMonitoringTests
{
    private sealed class ManualScheduler : IDelayScheduler
    {
        private readonly List<TaskCompletionSource> _pending = [];

        public int Pending
        {
            get { lock (_pending) return _pending.Count; }
        }

        public Task Delay(TimeSpan delay, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ct.Register(() => tcs.TrySetCanceled());
            lock (_pending) _pending.Add(tcs);
            return tcs.Task;
        }

        public void ReleaseAll()
        {
            TaskCompletionSource[] all;
            lock (_pending)
            {
                all = _pending.ToArray();
                _pending.Clear();
            }

            foreach (var t in all) t.TrySetResult();
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        public required InMemoryModelStore Store { get; init; }
        public required InMemoryDeltaSource Source { get; init; }
        public required IncrementalDetectionEngine Engine { get; init; }
        public required LiveClashMonitor Monitor { get; init; }
        public List<MonitorUpdate> Updates { get; } = [];
        private long _seq;

        public long NextSequence() => Interlocked.Increment(ref _seq);

        public void Modify(params string[] ids) => Monitor.NotifyChanged(new ModelChange(NextSequence(), [], ids, [], ["edit"], ChangeOrigin.User));

        public async Task Idle()
        {
            var t = Monitor.WhenIdleAsync();
            Assert.Same(t, await Task.WhenAny(t, Task.Delay(TimeSpan.FromSeconds(20))));
        }

        public IReadOnlyList<Issue> Issues => Monitor.LastUpdate!.Issues;

        public ValueTask DisposeAsync() => Monitor.DisposeAsync();
    }

    private static Rig CreateRig(ReinforcementModel model, IDelayScheduler? scheduler = null, MonitorOptions? options = null)
    {
        var store = new InMemoryModelStore(model);
        var source = new InMemoryDeltaSource(store);
        var engine = new IncrementalDetectionEngine(new ModelAnalyzer(options: new AnalyzerOptions { MaxDegreeOfParallelism = 2 }), TestRules.Standard(),
            new FixedClock(new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero)));
        var monitor = new LiveClashMonitor(engine, source, options ?? new MonitorOptions { Debounce = TimeSpan.Zero, RetryDelay = TimeSpan.Zero }, scheduler);
        var rig = new Rig { Store = store, Source = source, Engine = engine, Monitor = monitor };
        monitor.Updated += (_, u) =>
        {
            lock (rig.Updates) rig.Updates.Add(u);
        };
        return rig;
    }

    private static RebarBar SlabBar(string id, double y, double d = 20) => Bar(id, P(100, y, 50), P(2900, y, 50), d, BarRole.Distribution, "S1", "B1");

    private static IReadOnlyList<string> FullScanIssueIds(ReinforcementModel model)
    {
        var rules = TestRules.Standard();
        var result = new ModelAnalyzer().Analyze(model, rules);
        return new DefectClassifier().Classify(result.Findings, model, rules).Select(i => i.Id).OrderBy(i => i, StringComparer.Ordinal).ToArray();
    }

    [Fact]
    public async Task AddingAConflictingBarRaisesANewIssueWithoutManualScan()
    {
        await using var rig = CreateRig(Build([Slab()], [SlabBar("a", 1000)]));
        await rig.Monitor.StartAsync();
        await rig.Idle();
        Assert.Empty(rig.Issues);

        rig.Store.ApplyUserDelta(new ModelDelta { UpsertBars = [SlabBar("b", 1015)] }, added: ["b"]);
        rig.Monitor.NotifyChanged(new ModelChange(rig.NextSequence(), ["b"], [], [], ["Create rebar"], ChangeOrigin.User));
        await rig.Idle();

        var issue = Assert.Single(rig.Issues);
        Assert.Equal(IssueSubtype.ParallelOverlap, issue.Subtype);
        var last = rig.Monitor.LastUpdate!;
        Assert.Equal([issue.Id], last.NewIssueIds);
        Assert.False(last.FullScan);
        Assert.True(last.BarsAnalysed <= 2); // incremental: only the affected region was recomputed
        Assert.Equal(2, rig.Source.ExtractCalls); // initial full scan + one incremental extraction
    }

    [Fact]
    public async Task MovingABarIntoAndOutOfAClashUpdatesAndResolvesTheIssue()
    {
        await using var rig = CreateRig(Build([Slab()], [SlabBar("a", 1000), SlabBar("b", 1100)]));
        await rig.Monitor.StartAsync();
        await rig.Idle();
        Assert.Empty(rig.Issues);

        rig.Store.ApplyUserDelta(new ModelDelta { UpsertBars = [rig.Store.Current.Bars["b"].Translate(new Vec3(0, -85, 0))] });
        rig.Modify("b");
        await rig.Idle();
        var clash = Single(rig.Issues, IssueSubtype.ParallelOverlap);

        rig.Store.ApplyUserDelta(new ModelDelta { UpsertBars = [rig.Store.Current.Bars["b"].Translate(new Vec3(0, 85, 0))] });
        rig.Modify("b");
        await rig.Idle();
        Assert.Empty(rig.Issues);
        Assert.Equal([clash.Id], rig.Monitor.LastUpdate!.ResolvedIssueIds);
    }

    [Fact]
    public async Task DeletingAnElementRemovesItsIssues()
    {
        await using var rig = CreateRig(Build([Slab()], [SlabBar("a", 1000), SlabBar("b", 1015), SlabBar("c", 1500)]));
        await rig.Monitor.StartAsync();
        await rig.Idle();
        Assert.Single(rig.Issues);

        rig.Store.ApplyUserDelta(new ModelDelta { RemovedElementIds = ["b"] }, deleted: ["b"]);
        rig.Monitor.NotifyChanged(new ModelChange(rig.NextSequence(), [], [], ["b"], ["Delete"], ChangeOrigin.User));
        await rig.Idle();
        Assert.Empty(rig.Issues);
        Assert.Single(rig.Monitor.LastUpdate!.ResolvedIssueIds);
        Assert.DoesNotContain("b", rig.Engine.LatestModel!.Model.Bars.Keys);
    }

    [Fact]
    public async Task ChangingHostGeometryRechecksCoverOfHostedBars()
    {
        await using var rig = CreateRig(Build([Slab()], [SlabBar("a", 1000)]));
        await rig.Monitor.StartAsync();
        await rig.Idle();
        Assert.Empty(rig.Issues);

        // Slab soffit raised by 30 mm: bar now has 10 mm bottom cover (25 mm required).
        var thinner = Box("S1", Collide.Model.HostCategory.Slab, P(0, 0, 30), P(3000, 3000, 250));
        rig.Store.ApplyUserDelta(new ModelDelta { UpsertHosts = [thinner] });
        rig.Modify("S1");
        await rig.Idle();
        var cover = Single(rig.Issues, IssueSubtype.InsufficientCover);
        Assert.Equal(10.0, M(cover, "providedCover"), 6);
    }

    [Fact]
    public async Task RapidChangesAreDebouncedIntoOneBatchAndDuplicatesIgnored()
    {
        var scheduler = new ManualScheduler();
        await using var rig = CreateRig(Build([Slab()], Enumerable.Range(0, 10).Select(i => SlabBar($"b{i}", 1000 + 100 * i)).ToArray()),
            scheduler, new MonitorOptions { Debounce = TimeSpan.FromMilliseconds(50), MaxLatency = TimeSpan.FromMinutes(1) });
        await rig.Monitor.StartAsync();
        await rig.Idle();
        Assert.Equal(1, rig.Source.ExtractCalls);

        var change = new ModelChange(rig.NextSequence(), [], ["b3"], [], ["drag"], ChangeOrigin.User);
        rig.Monitor.NotifyChanged(change);
        rig.Monitor.NotifyChanged(change); // duplicate notification (same sequence)
        for (var i = 0; i < 20; i++) rig.Modify($"b{i % 10}", "b3");
        await WaitUntil(() => scheduler.Pending > 0);
        rig.Modify("b9"); // arrives during the quiet period: debounce restarts
        await WaitUntil(() => scheduler.Pending > 0);
        scheduler.ReleaseAll();
        await WaitUntil(() => scheduler.Pending > 0 || rig.Monitor.WhenIdleAsync().IsCompleted);
        scheduler.ReleaseAll();
        await rig.Idle();

        Assert.Equal(2, rig.Source.ExtractCalls); // one batch for all 22 notifications
        var batch = rig.Source.Batches.Single();
        Assert.Equal(10, batch.UpsertedElementIds.Count); // each element once
        Assert.Equal(FullScanIssueIds(rig.Store.Current), rig.Issues.Select(i => i.Id).OrderBy(i => i, StringComparer.Ordinal));
    }

    [Fact]
    public void ObsoleteAndOutOfOrderResultsAreDiscardedWithoutLosingTheirScope()
    {
        var model = Build([Slab()], [SlabBar("a", 1000), SlabBar("b", 1100), SlabBar("c", 2000)]);
        var engine = new IncrementalDetectionEngine(new ModelAnalyzer(), TestRules.Standard());
        engine.InitializeNow(model);

        var r1 = engine.Stage(new ModelDelta { UpsertBars = [model.Bars["b"].Translate(new Vec3(0, -85, 0))] })!; // creates a clash with a
        var r2 = engine.Stage(new ModelDelta { UpsertBars = [model.Bars["c"].Translate(new Vec3(0, -970, 0))] })!; // c (now y=1030) also clashes with b (y=1015)
        var c1 = engine.Compute(r1);
        var c2 = engine.Compute(r2);
        Assert.Null(engine.Commit(c1)); // out of order: r1 completed after r2 was staged
        var update = engine.Commit(c2)!;
        Assert.NotNull(update);
        // r2's scope includes r1's, so the committed result reflects both changes.
        Assert.Equal(FullScanIssueIds(engine.LatestModel!.Model), update.Issues.Select(i => i.Id).OrderBy(i => i, StringComparer.Ordinal));
        Assert.True(update.Issues.Count >= 2);
        Assert.Null(engine.Commit(c2)); // a result can be committed only once
    }

    [Fact]
    public void CancelledComputationLeavesStateConsistent()
    {
        var model = Build([Slab()], [SlabBar("a", 1000), SlabBar("b", 1100)]);
        var engine = new IncrementalDetectionEngine(new ModelAnalyzer(), TestRules.Standard());
        engine.InitializeNow(model);
        var before = engine.Issues;

        var staged = engine.Stage(new ModelDelta { UpsertBars = [model.Bars["b"].Translate(new Vec3(0, -85, 0))] })!;
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => engine.Compute(staged, cts.Token));
        Assert.Same(before, engine.Issues); // nothing half-applied

        var later = engine.Stage(new ModelDelta())!; // a later (empty) change still carries the pending scope
        var update = engine.Commit(engine.Compute(later))!;
        Assert.Single(update.Issues, i => i.Subtype == IssueSubtype.ParallelOverlap);
    }

    [Fact]
    public async Task RecoversAfterExtractionFailure()
    {
        await using var rig = CreateRig(Build([Slab()], [SlabBar("a", 1000)]));
        await rig.Monitor.StartAsync();
        await rig.Idle();

        rig.Store.ApplyUserDelta(new ModelDelta { UpsertBars = [SlabBar("b", 1015)] });
        rig.Source.FailNext = new InvalidOperationException("Revit API context unavailable");
        rig.Modify("b");
        await WaitUntil(() => rig.Updates.Any(u => u.Status == MonitorStatus.Error));
        await rig.Idle(); // retried automatically (RetryDelay = 0) with the re-queued batch

        Assert.Contains(rig.Updates, u => u.Status == MonitorStatus.Error && u.Error!.Contains("API context unavailable"));
        Assert.Single(rig.Issues, i => i.Subtype == IssueSubtype.ParallelOverlap);
        Assert.Null(rig.Monitor.LastError);
    }

    [Fact]
    public async Task PausedMonitorAccumulatesChangesAndProcessesThemOnResume()
    {
        await using var rig = CreateRig(Build([Slab()], [SlabBar("a", 1000)]));
        await rig.Monitor.StartAsync();
        await rig.Idle();
        rig.Monitor.Pause();

        rig.Store.ApplyUserDelta(new ModelDelta { UpsertBars = [SlabBar("b", 1015)] });
        rig.Modify("b");
        await rig.Idle();
        Assert.Equal(1, rig.Source.ExtractCalls);
        Assert.Equal(MonitorStatus.Paused, rig.Monitor.Status);
        Assert.Empty(rig.Issues);

        rig.Monitor.Resume();
        await rig.Idle();
        Assert.Single(rig.Issues);
    }

    [Fact]
    public async Task OwnCorrectionIsRecheckedButNeverTriggersFurtherModification()
    {
        var model = Build([Beam()], [
            Shaped("st", DetectionFixtureTests.Stirrup(1000), 10, Collide.Model.BarRole.Stirrup, "B1"),
            Bar("side", P(50, 55, 250), P(5950, 55, 250), 20, Collide.Model.BarRole.Longitudinal, "B1")]);
        await using var rig = CreateRig(model);
        rig.Store.Changed += rig.Monitor.NotifyChanged; // like Revit's DocumentChanged subscription
        await rig.Monitor.StartAsync();
        await rig.Idle();
        Assert.Single(rig.Issues);

        var analyzer = new ModelAnalyzer();
        var resolution = new ResolutionEngine(analyzer, new AuditLog());
        var run = resolution.Run(DetectionState.Initialize(analyzer.Prepare(rig.Store.Current, TestRules.Standard()), analyzer), rig.Store);
        Assert.Equal(ExecutionStatus.Committed, run.Outcomes.Single().Execution!.Status);
        await rig.Idle();

        Assert.Empty(rig.Issues); // the affected region was re-validated after the correction
        Assert.True(rig.Monitor.LastUpdate!.IncludesSelfChanges);
        Assert.Equal(1, rig.Store.CommitCount); // no recursive modification
    }

    [Fact]
    public void IncrementalResultsEqualFullAnalysisForRandomEditSequences()
    {
        var rng = new Random(4242);
        var rules = TestRules.Standard();
        var analyzer = new ModelAnalyzer(options: new AnalyzerOptions { MaxDegreeOfParallelism = 1 });
        for (var trial = 0; trial < 6; trial++)
        {
            var bars = Enumerable.Range(0, 30).Select(i => RandomBar($"r{i}", rng)).ToList();
            var hosts = new List<StructuralHost> { Slab(voids: [Opening("O1", P(1200, 1200, 0), P(1500, 1500, 250))]) };
            var model = Build(hosts, bars);
            var engine = new IncrementalDetectionEngine(analyzer, rules);
            engine.InitializeNow(model);
            var current = model;
            for (var step = 0; step < 12; step++)
            {
                var delta = RandomDelta(current, rng, step);
                current = current.Apply(delta);
                engine.ApplyNow(delta);
                var full = new DefectClassifier().Classify(analyzer.Analyze(current, rules).Findings, current, rules);
                Assert.Equal(full.Select(i => (i.Id, i.Severity)), engine.Issues.Select(i => (i.Id, i.Severity)));
                Assert.Equal(
                    full.Select(i => string.Join(";", i.Measurements)),
                    engine.Issues.Select(i => string.Join(";", i.Measurements)));
            }
        }
    }

    private static RebarBar RandomBar(string id, Random rng)
    {
        var alongX = rng.NextDouble() < 0.5;
        var c = 300 + rng.NextDouble() * 2400;
        var z = 40 + rng.NextDouble() * 30;
        var d = new[] { 10.0, 12, 16, 20 }[rng.Next(4)];
        var layer = alongX ? "B1" : "B2";
        return alongX
            ? Bar(id, P(100 + rng.NextDouble() * 400, c, z), P(2400 + rng.NextDouble() * 500, c, z), d, BarRole.Distribution, "S1", layer)
            : Bar(id, P(c, 100 + rng.NextDouble() * 400, z), P(c, 2400 + rng.NextDouble() * 500, z), d, BarRole.Distribution, "S1", layer);
    }

    private static ModelDelta RandomDelta(ReinforcementModel model, Random rng, int step)
    {
        var ids = model.Bars.Keys.ToArray();
        switch (rng.Next(4))
        {
            case 0:
                return new ModelDelta { UpsertBars = [RandomBar($"n{step}", rng)] };
            case 1 when ids.Length > 0:
                return new ModelDelta { RemovedElementIds = [ids[rng.Next(ids.Length)]] };
            case 2:
                var slab = model.Hosts["S1"];
                var dz = rng.Next(2) == 0 ? 0 : 20;
                return new ModelDelta { UpsertHosts = [Box("S1", Collide.Model.HostCategory.Slab, P(0, 0, dz), P(3000, 3000, 250), voids: slab.Voids)] };
            default:
                if (ids.Length == 0) return new ModelDelta();
                var bar = model.Bars[ids[rng.Next(ids.Length)]];
                return new ModelDelta { UpsertBars = [bar.Translate(new Vec3(rng.NextDouble() * 60 - 30, rng.NextDouble() * 60 - 30, rng.NextDouble() * 10 - 5))] };
        }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not met.");
            await Task.Delay(5);
        }
    }
}
