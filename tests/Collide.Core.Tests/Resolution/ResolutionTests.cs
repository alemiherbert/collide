using Collide.Analysis;
using Collide.Audit;
using Collide.Core.Tests.Detection;
using Collide.Core.Tests.Fixtures;
using Collide.Findings;
using Collide.Geometry;
using Collide.Model;
using Collide.Resolution;
using Collide.Rules;
using static Collide.Core.Tests.Fixtures.Fx;

namespace Collide.Core.Tests.Resolution;

public class ResolutionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 10, 0, 0, 0, TimeSpan.Zero);

    private sealed record Harness(InMemoryModelStore Store, ResolutionEngine Engine, AuditLog Audit, ModelAnalyzer Analyzer, RuleSet Rules)
    {
        public DetectionState State() => DetectionState.Initialize(Analyzer.Prepare(Store.Current, Rules), Analyzer);
    }

    private static Harness Create(ReinforcementModel model, RuleSet? rules = null)
    {
        var clock = new FixedClock(T0);
        var audit = new AuditLog(clock);
        var analyzer = new ModelAnalyzer(options: new AnalyzerOptions { MaxDegreeOfParallelism = 1 });
        return new Harness(new InMemoryModelStore(model), new ResolutionEngine(analyzer, audit, clock), audit, analyzer, rules ?? TestRules.Standard());
    }

    [Fact]
    public void BeamBarMovedOutOfStirrupLegAutomaticallyWithAllInvariantsAndAudit()
    {
        var stirrup = Shaped("st", DetectionFixtureTests.Stirrup(1000), 10, BarRole.Stirrup, "B1");
        var side = Bar("side", P(50, 55, 250), P(5950, 55, 250), 20, BarRole.Longitudinal, "B1");
        var h = Create(Build([Beam()], [stirrup, side]));
        var originalStirrup = h.Store.Current.Bars["st"].GeometryHash;

        var run = h.Engine.Run(h.State(), h.Store);

        var outcome = Assert.Single(run.Outcomes);
        Assert.Equal(ResolutionDecision.AutoApply, outcome.Plan.Decision);
        Assert.Equal(ExecutionStatus.Committed, outcome.Execution!.Status);
        var mod = Assert.Single(outcome.Execution.Correction!.Modifications);
        Assert.Equal("side", mod.BarId);
        Assert.Equal(new Vec3(0, 5, 0), mod.Translation); // exactly the 5 mm penetration, along +Y only (no move along the bar axis)
        Assert.Empty(run.IssuesAfter);
        Assert.Equal(originalStirrup, h.Store.Current.Bars["st"].GeometryHash); // unaffected bar untouched
        Assert.Equal(1, h.Store.CommitCount);

        // Every Part 8 invariant was evaluated on the actual post-state and passed.
        Assert.Equal(11, outcome.Execution.PostValidation.Count);
        Assert.All(outcome.Execution.PostValidation, i => Assert.True(i.Passed, $"{i.Id}: {i.Evidence}"));

        var actions = h.Audit.Entries.Select(e => e.Action).ToArray();
        Assert.Equal(
            [AuditAction.RunStarted, AuditAction.CandidateValid, AuditAction.DecisionMade, AuditAction.ExecutionStarted, AuditAction.ModificationApplied,
             AuditAction.PostValidationPassed, AuditAction.Committed, AuditAction.RunCompleted],
            actions);
        Assert.All(h.Audit.Entries, e => Assert.Equal(run.RunId, e.RunId));
    }

    private static RuleSet VerticalMovesOnly() => TestRules.Standard() with
    {
        Placement =
        [
            new PlacementToleranceRule
            {
                Id = "PLC-Z", Source = new RuleSource { Document = "test" }, Scope = new RuleScope { HostCategories = [HostCategory.Slab] },
                MaximumDisplacement = Mm(25), AllowedAxes = [Axis.Z],
            },
        ],
    };

    [Fact]
    public void SymmetricChoiceIsAmbiguousAndGoesToApproval()
    {
        // Two crossing Ø12 bars of equal role and diameter, 3 mm interpenetration: moving either one is equally good.
        var model = Build([Slab()], [
            Bar("a", P(1500, 100, 55), P(1500, 2900, 55), 12, BarRole.Distribution, "S1", "B2"),
            Bar("b", P(100, 1500, 46), P(2900, 1500, 46), 12, BarRole.Distribution, "S1", "B1")]);
        var h = Create(model, VerticalMovesOnly());
        var run = h.Engine.Run(h.State(), h.Store);

        var plan = Assert.Single(run.Outcomes).Plan;
        Assert.Equal(ResolutionDecision.RequiresApproval, plan.Decision);
        Assert.Contains("Ambiguous", plan.Reason);
        Assert.Equal(plan.Ranked[0].Score, plan.Ranked[1].Score, 9);
        Assert.Equal(0, h.Store.CommitCount); // nothing applied; ranked alternatives presented for approval
    }

    [Fact]
    public void F01_ParallelOverlapResolvedBySymmetricSplitWhenSingleMovesExceedTolerance()
    {
        // Separating the Ø20 bars to the 25 mm clear spacing needs 30 mm; a single bar may move only 25 mm,
        // so the unique valid correction moves each bar 15 mm (within the 15 mm auto-apply limit).
        var model = Build([Slab()], [
            Bar("a", P(100, 1000, 50), P(2900, 1000, 50), 20, BarRole.Distribution, "S1", "B1"),
            Bar("b", P(100, 1015, 50), P(2900, 1015, 50), 20, BarRole.Distribution, "S1", "B1")]);
        var h = Create(model);
        var run = h.Engine.Run(h.State(), h.Store);

        var outcome = Assert.Single(run.Outcomes);
        Assert.Equal(ResolutionDecision.AutoApply, outcome.Plan.Decision);
        Assert.Equal(ResolutionStrategy.TranslateBoth, outcome.Plan.Selected!.Candidate.Strategy);
        var mods = outcome.Execution!.Correction!.Modifications;
        Assert.Equal(new Vec3(0, -15, 0), mods.Single(m => m.BarId == "a").Translation);
        Assert.Equal(new Vec3(0, 15, 0), mods.Single(m => m.BarId == "b").Translation);
        Assert.Empty(run.IssuesAfter); // clash gone and the 25 mm clear spacing is met exactly
    }

    [Fact]
    public void F14_CorrectionThatCreatesAnotherClashIsRejectedAndAlternativeChosen()
    {
        var rules = VerticalMovesOnly();
        var model = Build([Slab()], [
            Bar("a", P(1500, 100, 55), P(1500, 2900, 55), 12, BarRole.Distribution, "S1", "B2"), // crosses b with 3 mm penetration
            Bar("b", P(100, 1500, 46), P(2900, 1500, 46), 12, BarRole.Distribution, "S1", "B1"),
            Bar("c", P(100, 1200, 68), P(2900, 1200, 68), 12, BarRole.Distribution, "S1", "T1"), // 1 mm above a today
        ]);
        var h = Create(model, rules);
        var state = h.State();
        var issue = Single(h.Engine.Classify(state), IssueSubtype.SlabLayerConflict);
        Assert.Equal(3.0, M(issue, "penetrationDepth"), 6);

        var plan = h.Engine.Plan(issue, state);
        // Moving 'a' up by 3 mm fixes a/b but drives 'a' 2 mm into 'c': rejected with that reason.
        var rejectedUp = Assert.Single(plan.Rejected, r => r.Candidate.Modifications is [{ BarId: "a" } m] && m.Translation.Z > 0);
        Assert.Contains(rejectedUp.RejectionReasons, r => r.Contains("INV-02") && r.Contains("[a,c]"));
        Assert.Contains(rejectedUp.NewFindings, f => f.Contains("[a,c]"));

        // Moving 'b' down 3 mm is valid, unambiguous and applied.
        Assert.Equal(ResolutionDecision.AutoApply, plan.Decision);
        var chosen = Assert.Single(plan.Selected!.Candidate.Modifications);
        Assert.Equal("b", chosen.BarId);
        Assert.Equal(new Vec3(0, 0, -3), chosen.Translation);

        var run = h.Engine.Run(state, h.Store);
        Assert.Equal(ExecutionStatus.Committed, run.Outcomes.Single().Execution!.Status);
        Assert.Empty(run.IssuesAfter);
    }

    [Fact]
    public void F15_NoSafeAutomatedResolutionLeavesModelUnchanged()
    {
        var model = Build([Beam()], [
            Bar("a", P(50, 150, 250), P(5950, 150, 250), 20, BarRole.Longitudinal, "B1") with { IsDesignLocked = true },
            Bar("b", P(50, 165, 250), P(5950, 165, 250), 20, BarRole.Longitudinal, "B1") with { IsDesignLocked = true }]);
        var h = Create(model);
        var hashBefore = h.Store.Current.ContentHash();
        var state = h.State();
        var issue = Single(h.Engine.Classify(state), IssueSubtype.ParallelOverlap);
        Assert.False(issue.AutoResolutionPermitted);
        Assert.True(issue.RequiresEngineeringReview);

        var run = h.Engine.Run(state, h.Store);
        Assert.Equal(ResolutionDecision.NotEligible, Assert.Single(run.Outcomes).Plan.Decision);
        Assert.Equal(hashBefore, h.Store.Current.ContentHash());
        Assert.Equal(0, h.Store.CommitCount);
        Assert.Single(run.IssuesAfter);
    }

    [Fact]
    public void F15b_ExceedingPlacementToleranceYieldsNoSafeResolution()
    {
        // Ø20 bars 15 mm apart in a beam: separating them to the 25 mm clear spacing needs 30 mm; tolerance is 10 mm.
        var model = Build([Beam()], [
            Bar("a", P(50, 150, 250), P(5950, 150, 250), 20, BarRole.Longitudinal, "B1"),
            Bar("b", P(50, 165, 250), P(5950, 165, 250), 20, BarRole.Longitudinal, "B1")]);
        var h = Create(model);
        var run = h.Engine.Run(h.State(), h.Store);
        var plan = Assert.Single(run.Outcomes).Plan;
        Assert.Equal(ResolutionDecision.NoSafeResolution, plan.Decision);
        Assert.Contains("engineering review", plan.Reason);
        Assert.Equal(0, h.Store.CommitCount);
    }

    private static Harness RespacingHarness(out DetectionState state, out Issue issue)
    {
        // Set of five slab bars at 150 mm; a locked trimmer 20 mm from s3 leaves only 6 mm clear (25 mm required).
        var setBars = Enumerable.Range(1, 5)
            .Select(i => Bar($"s{i}", P(100, 1000 + 150 * (i - 1), 50), P(2900, 1000 + 150 * (i - 1), 50), 12, BarRole.Distribution, "S1", "B1", setId: "SET1"))
            .ToArray();
        var trimmer = Bar("trim", P(700, 1320, 50), P(1900, 1320, 50), 16, BarRole.OpeningTrimmer, "S1", "B1") with { IsDesignLocked = true };
        var set = new RebarSet { Id = "SET1", ElementId = "SET1", HostId = "S1", Role = BarRole.Distribution, BarIds = setBars.Select(b => b.Id).ToArray(), DeclaredQuantity = 5 };
        var h = Create(Build([Slab()], [.. setBars, trimmer], sets: [set]));
        state = h.State();
        issue = Single(h.Engine.Classify(state), IssueSubtype.InsufficientClearSpacing);
        return h;
    }

    [Fact]
    public void LocalRespacingPreservesBarCountAndNeedsApprovalBeyondAutoLimit()
    {
        var h = RespacingHarness(out var state, out var issue);
        Assert.Equal(6.0, M(issue, "clearSpacing"), 6);
        var plan = h.Engine.Plan(issue, state);
        Assert.Equal(ResolutionDecision.RequiresApproval, plan.Decision); // 19 mm move > 15 mm auto-apply limit
        Assert.Contains("auto-apply limit", plan.Reason);
        var respacing = plan.Ranked.Where(r => r.Candidate.Strategy == ResolutionStrategy.LocalRespacing).ToArray();
        Assert.NotEmpty(respacing);
        Assert.All(respacing, r =>
        {
            Assert.Equal(19, r.Candidate.Modifications.Single(m => m.BarId == "s3").Translation.Length, 6);
            Assert.Contains(r.Invariants, i => i.Id == "INV-03" && i.Passed);
        });
    }

    [Fact]
    public void F20_InjectedFailureMidCorrectionRollsBackCompletely()
    {
        var h = RespacingHarness(out var state, out var issue);
        var plan = h.Engine.Plan(issue, state);
        var multi = plan.Ranked.First(r => r.Candidate.Modifications.Count >= 3);
        var before = h.Store.Current;
        h.Store.FailOn = (_, n) => n == 2; // second translation throws

        var (result, after) = h.Engine.ApplyApproved(state, issue.Id, multi.Candidate.Id, "J. Engineer", h.Store);

        Assert.Equal(ExecutionStatus.RolledBack, result.Status);
        Assert.Contains("Injected failure", result.Reason);
        Assert.Same(before, h.Store.Current); // no partial state: the committed snapshot is untouched
        Assert.Equal(before.ContentHash(), h.Store.Current.ContentHash());
        Assert.Same(state, after);
        Assert.Empty(h.Engine.Executor.Ledger);
        var actions = h.Audit.Entries.Select(e => e.Action).ToArray();
        Assert.Contains(AuditAction.ApprovalRecorded, actions);
        Assert.Contains(AuditAction.ExecutionFailed, actions);
        Assert.Contains(AuditAction.RolledBack, actions);
        Assert.Contains(AuditAction.RollbackVerified, actions);
        Assert.DoesNotContain(AuditAction.Committed, actions);

        // With the fault removed the same approved candidate applies cleanly.
        h.Store.FailOn = null;
        var (ok, _) = h.Engine.ApplyApproved(state, issue.Id, multi.Candidate.Id, "J. Engineer", h.Store);
        Assert.Equal(ExecutionStatus.Committed, ok.Status);
        Assert.Equal("J. Engineer", ok.Correction!.ApprovedBy);
        Assert.Equal(5, h.Store.Current.Sets["SET1"].BarIds.Count(h.Store.Current.Bars.ContainsKey));
    }

    [Fact]
    public void HostThatAppliesDifferentGeometryIsDetectedAndRolledBack()
    {
        var stirrup = Shaped("st", DetectionFixtureTests.Stirrup(1000), 10, BarRole.Stirrup, "B1");
        var side = Bar("side", P(50, 55, 250), P(5950, 55, 250), 20, BarRole.Longitudinal, "B1");
        var h = Create(Build([Beam()], [stirrup, side]));
        var before = h.Store.Current;
        h.Store.ActualTranslation = m => m.Translation * 0.5; // e.g. host snapping/constraints

        var run = h.Engine.Run(h.State(), h.Store);
        var exec = run.Outcomes.Single().Execution!;
        Assert.Equal(ExecutionStatus.RolledBack, exec.Status);
        Assert.Contains("different geometry", exec.Reason);
        Assert.Same(before, h.Store.Current);
        Assert.Contains(h.Audit.Entries, e => e.Action == AuditAction.HostResultMismatch);
    }

    [Fact]
    public void DryRunNeverCallsTheEditor()
    {
        var stirrup = Shaped("st", DetectionFixtureTests.Stirrup(1000), 10, BarRole.Stirrup, "B1");
        var side = Bar("side", P(50, 55, 250), P(5950, 55, 250), 20, BarRole.Longitudinal, "B1");
        var h = Create(Build([Beam()], [stirrup, side]));
        var run = h.Engine.Run(h.State(), h.Store, new ResolutionOptions { DryRun = true });
        var outcome = Assert.Single(run.Outcomes);
        Assert.Equal(ResolutionDecision.AutoApply, outcome.Plan.Decision); // what would be applied
        Assert.Null(outcome.Execution);
        Assert.Equal(0, h.Store.CommitCount);
        Assert.True(run.DryRun);
    }

    [Fact]
    public void RepeatedRunsAreIdempotentAndReproducible()
    {
        ReinforcementModel Model() => Build([Beam()], [
            Shaped("st", DetectionFixtureTests.Stirrup(1000), 10, BarRole.Stirrup, "B1"),
            Bar("side", P(50, 55, 250), P(5950, 55, 250), 20, BarRole.Longitudinal, "B1")]);

        var h1 = Create(Model());
        var first = h1.Engine.Run(h1.State(), h1.Store);
        var second = h1.Engine.Run(h1.State(), h1.Store);
        Assert.Single(first.Outcomes, o => o.Execution?.Status == ExecutionStatus.Committed);
        Assert.Empty(second.Outcomes); // nothing left to correct: no duplicate correction
        Assert.Equal(1, h1.Store.CommitCount);

        // Re-executing the same validated candidate directly is detected as already applied.
        var h2 = Create(Model());
        var state = h2.State();
        var issue = h2.Engine.Classify(state).Single();
        var plan = h2.Engine.Plan(issue, state);
        var target = state.Findings.First(f => issue.FindingFingerprints.Contains(f.Fingerprint));
        Assert.Equal(ExecutionStatus.Committed, h2.Engine.Executor.Execute(plan.Selected!, target, state.Prepared, h2.Store, "r").Status);
        Assert.Equal(ExecutionStatus.AlreadyApplied, h2.Engine.Executor.Execute(plan.Selected!, target, state.Prepared, h2.Store, "r").Status);
        Assert.Equal(1, h2.Store.CommitCount);

        // Identical inputs → identical decisions, ids and audit sequence.
        var h3 = Create(Model());
        var third = h3.Engine.Run(h3.State(), h3.Store);
        Assert.Equal(first.RunId, third.RunId);
        Assert.Equal(first.Outcomes.Select(o => o.Plan.Selected!.Candidate.Id), third.Outcomes.Select(o => o.Plan.Selected!.Candidate.Id));
        Assert.Equal(h1.Audit.Entries.Take(8).Select(e => (e.Action, e.Message)), h3.Audit.Entries.Select(e => (e.Action, e.Message)));
    }

    [Fact]
    public void AppliedCorrectionCanBeRevertedExactly()
    {
        var h = Create(Build([Beam()], [
            Shaped("st", DetectionFixtureTests.Stirrup(1000), 10, BarRole.Stirrup, "B1"),
            Bar("side", P(50, 55, 250), P(5950, 55, 250), 20, BarRole.Longitudinal, "B1")]));
        var original = h.Store.Current.ContentHash();
        var run = h.Engine.Run(h.State(), h.Store);
        var record = run.Outcomes.Single().Execution!.Correction!;
        Assert.NotEqual(original, h.Store.Current.ContentHash());

        var revert = h.Engine.Executor.Revert(record, h.Store, "revert-run");
        Assert.Equal(ExecutionStatus.Reverted, revert.Status);
        Assert.Equal(original, h.Store.Current.ContentHash());
        Assert.Equal(ExecutionStatus.Reverted, h.Engine.Executor.Ledger[record.CorrectionId].Status);

        // Reverting twice is refused (bars are no longer in the corrected state).
        Assert.Equal(ExecutionStatus.StalePrecondition, h.Engine.Executor.Revert(record, h.Store, "revert-run").Status);
    }

    [Fact]
    public void StalePreconditionPreventsApplyingOverConcurrentEdit()
    {
        var h = Create(Build([Beam()], [
            Shaped("st", DetectionFixtureTests.Stirrup(1000), 10, BarRole.Stirrup, "B1"),
            Bar("side", P(50, 55, 250), P(5950, 55, 250), 20, BarRole.Longitudinal, "B1")]));
        var state = h.State();
        var issue = h.Engine.Classify(state).Single();
        var plan = h.Engine.Plan(issue, state);
        // The user edits the bar after planning.
        h.Store.ApplyUserDelta(new ModelDelta { UpsertBars = [h.Store.Current.Bars["side"].Translate(new Vec3(0, 0, 1))] }, modified: ["side"]);
        var target = state.Findings.First(f => issue.FindingFingerprints.Contains(f.Fingerprint));
        var result = h.Engine.Executor.Execute(plan.Selected!, target, state.Prepared, h.Store, "r");
        Assert.Equal(ExecutionStatus.StalePrecondition, result.Status);
        Assert.Contains(h.Audit.Entries, e => e.Action == AuditAction.StalePrecondition);
    }

    [Fact]
    public void ProposeOnlyPolicyNeverModifies()
    {
        var h = Create(Build([Beam()], [
            Shaped("st", DetectionFixtureTests.Stirrup(1000), 10, BarRole.Stirrup, "B1"),
            Bar("side", P(50, 55, 250), P(5950, 55, 250), 20, BarRole.Longitudinal, "B1")]), TestRules.Standard(mode: ResolutionMode.ProposeOnly));
        var run = h.Engine.Run(h.State(), h.Store);
        var plan = run.Outcomes.Single().Plan;
        Assert.Equal(ResolutionDecision.RequiresApproval, plan.Decision);
        Assert.Contains("ProposeOnly", plan.Reason);
        Assert.Equal(0, h.Store.CommitCount);
    }
}
