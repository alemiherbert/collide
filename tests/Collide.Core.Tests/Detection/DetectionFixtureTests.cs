using Collide.Core.Tests.Fixtures;
using Collide.Findings;
using Collide.Geometry;
using Collide.Model;
using Collide.Rules;
using static Collide.Core.Tests.Fixtures.Fx;

namespace Collide.Core.Tests.Detection;

/// <summary>Synthetic fixtures 1–13, 16, 17 and 19 (detection and classification).</summary>
public class DetectionFixtureTests
{
    private static RebarBar SlabBarX(string id, double y, double z, double d = 20, string layer = "B1", double x0 = 100, double x1 = 2900) =>
        Bar(id, P(x0, y, z), P(x1, y, z), d, BarRole.Distribution, "S1", layer);

    [Fact]
    public void F01_ParallelBarsOverlappingPhysically()
    {
        var model = Build([Slab()], [SlabBarX("a", 1000, 50), SlabBarX("b", 1015, 50)]);
        var (result, issues) = Detect(model);

        var issue = Assert.Single(issues);
        Assert.Equal(IssueSubtype.ParallelOverlap, issue.Subtype);
        Assert.Equal(IssueCategory.RebarToRebar, issue.Category);
        Assert.Equal(Severity.High, issue.Severity);
        Assert.Equal(IssueStatus.ConfirmedDefect, issue.Status);
        Assert.Equal(Confidence.High, issue.Confidence);
        Assert.Equal(5.0, M(issue, "penetrationDepth"), 6);
        Assert.Equal(15.0, M(issue, "centrelineDistance"), 6);
        var lens = SegmentMath.CircleIntersectionArea(10, 10, 15) * 2800;
        Assert.Equal(lens, M(issue, "overlapVolume"), 0);
        Assert.True(issue.AutoResolutionPermitted);
        Assert.False(issue.RequiresEngineeringReview);
        Assert.Equal(["a", "b"], issue.Bars.Select(b => b.Id));
        Assert.Equal("L1", issue.LevelOrZone);
        Assert.Empty(result.Laps); // full overlap is not a lap
    }

    [Fact]
    public void F02_CentrelinesCrossInPlanButSolidsDoNotCollide()
    {
        var a = SlabBarX("a", 1500, 50, d: 12, layer: "B1");
        var b = Bar("b", P(1500, 100, 75), P(1500, 2900, 75), 12, BarRole.Distribution, "S1", "B2");
        var (result, issues) = Detect(Build([Slab()], [a, b]));

        Assert.Empty(Defects(issues));
        Assert.Empty(result.Suppressed); // 13 mm gap: not even a contact
        Assert.Equal(1, result.Statistics.CandidateBarPairs); // measured, not skipped
    }

    [Fact]
    public void F03_PerpendicularBarsIntentionallyTouchingInDifferentLayers()
    {
        var a = SlabBarX("a", 1500, 46, d: 12, layer: "B1");
        var b = Bar("b", P(1500, 100, 58), P(1500, 2900, 58), 12, BarRole.Distribution, "S1", "B2");
        var (result, issues) = Detect(Build([Slab()], [a, b]));

        Assert.Empty(issues);
        var s = Assert.Single(result.Suppressed);
        Assert.Equal("IC-MESH", s.RuleOrExceptionId);
        Assert.Equal(0, s.PenetrationMm, 6);
    }

    [Fact]
    public void F03b_SameContactInTheSameLayerIsNotIntentional()
    {
        var a = SlabBarX("a", 1500, 46, d: 12, layer: "B1");
        var b = Bar("b", P(1500, 100, 58), P(1500, 2900, 58), 12, BarRole.Distribution, "S1", "B1");
        var (_, issues) = Detect(Build([Slab()], [a, b]));
        var issue = Single(issues, IssueSubtype.UnclassifiedContact);
        Assert.Equal(IssueStatus.Warning, issue.Status);
        Assert.Equal(Severity.Low, issue.Severity);
    }

    [Fact]
    public void F04_InsufficientClearSpacingWithoutIntersection()
    {
        var (_, issues) = Detect(Build([Slab()], [SlabBarX("a", 1000, 50), SlabBarX("b", 1035, 50)]));

        var issue = Assert.Single(issues);
        Assert.Equal(IssueSubtype.InsufficientClearSpacing, issue.Subtype);
        Assert.Equal(IssueCategory.Constructability, issue.Category);
        Assert.Equal(Severity.Medium, issue.Severity);
        Assert.Equal(15.0, M(issue, "clearSpacing"), 6);
        Assert.Equal(25.0, M(issue, "requiredClearSpacing"), 6);
        Assert.Equal("SPC-1", issue.Rule!.RuleId);
        Assert.Equal(IssueStatus.ConfirmedDefect, issue.Status);
        Assert.True(issue.AutoResolutionPermitted);
    }

    [Fact]
    public void F04b_AggregatePassageGovernsWithLargeAggregate()
    {
        var rules = TestRules.Standard() with { Project = new ProjectParameters { MaximumAggregateSize = Mm(32) } };
        var (_, issues) = Detect(Build([Slab()], [SlabBarX("a", 1000, 50), SlabBarX("b", 1050, 50)]), rules);
        var issue = Single(issues, IssueSubtype.InsufficientAggregatePassage);
        Assert.Equal(30.0, M(issue, "clearSpacing"), 6);
        Assert.Equal(37.0, M(issue, "requiredClearSpacing"), 6);
    }

    [Fact]
    public void F04c_UnverifiedRuleYieldsSuspectedNotConfirmed()
    {
        var (_, issues) = Detect(Build([Slab()], [SlabBarX("a", 1000, 50), SlabBarX("b", 1035, 50)]), TestRules.Standard(verified: false));
        var issue = Single(issues, IssueSubtype.InsufficientClearSpacing);
        Assert.Equal(IssueStatus.SuspectedDefect, issue.Status);
        Assert.True(issue.RequiresEngineeringReview);
        Assert.Contains(issue.Limitations, l => l.Contains("not marked as engineer-verified"));
    }

    [Fact]
    public void F05_ConcreteCoverViolation()
    {
        var bar = Bar("a", P(50, 30, 30), P(5950, 30, 30), 20, BarRole.Longitudinal, "B1");
        var (_, issues) = Detect(Build([Beam()], [bar]));

        var cover = issues.Where(i => i.Subtype == IssueSubtype.InsufficientCover).ToArray();
        Assert.Equal(2, cover.Length); // bottom and side faces
        Assert.All(cover, i =>
        {
            Assert.Equal(20.0, M(i, "providedCover"), 6);
            Assert.Equal(40.0, M(i, "requiredCover"), 6);
            Assert.Equal(Severity.High, i.Severity);
            Assert.Equal("COV-BEAM", i.Rule!.RuleId);
            Assert.True(i.AutoResolutionPermitted);
        });
        Assert.Contains(cover, i => i.Measurements.Any(m => m.Name == "face" && m.Note == "Bottom"));
        Assert.Contains(cover, i => i.Measurements.Any(m => m.Name == "face" && m.Note == "Side"));
    }

    [Fact]
    public void F06_BarExtendsBeyondItsHost()
    {
        var bar = Bar("a", P(50, 150, 250), P(6100, 150, 250), 20, BarRole.Longitudinal, "B1");
        var (_, issues) = Detect(Build([Beam()], [bar]));

        var issue = Assert.Single(issues);
        Assert.Equal(IssueSubtype.OutsideConcrete, issue.Subtype);
        Assert.Equal(Severity.Critical, issue.Severity);
        Assert.Equal(100.0, M(issue, "lengthOutsideConcrete"), 6);
        Assert.Equal(110.0, M(issue, "maxSurfaceProtrusion"), 6);
        Assert.False(issue.AutoResolutionPermitted);
        Assert.True(issue.RequiresEngineeringReview);
    }

    [Fact]
    public void F07_DuplicateReinforcementInSamePosition()
    {
        var (_, issues) = Detect(Build([Slab()], [SlabBarX("a", 1000, 50), SlabBarX("b", 1000, 50)]));
        var issue = Assert.Single(issues);
        Assert.Equal(IssueSubtype.Duplicate, issue.Subtype);
        Assert.Equal(Severity.High, issue.Severity);
        Assert.Equal(0, M(issue, "maxCentrelineDeviation"), 9);
        Assert.False(issue.AutoResolutionPermitted);
        Assert.Contains("quantities", issue.AutoResolutionReason);
    }

    internal static BarPath Stirrup(double x) => PathFactory.RectangularLoop(P(x, 150, 250), Vec3.UnitY, Vec3.UnitZ, 210, 410, bendRadius: 15);

    [Fact]
    public void F08_BeamLongitudinalBarCollidesWithStirrup()
    {
        var stirrup = Shaped("st", Stirrup(1000), 10, BarRole.Stirrup, "B1");
        var corner = Bar("corner", P(50, 60, 60), P(5950, 60, 60), 20, BarRole.Longitudinal, "B1");
        var clashing = Bar("side", P(50, 55, 250), P(5950, 55, 250), 20, BarRole.Longitudinal, "B1");
        var (result, issues) = Detect(Build([Beam()], [stirrup, corner, clashing]));
        Assert.True(issues.Count == 1, Dump(issues));

        var issue = Assert.Single(issues);
        Assert.Equal(IssueSubtype.LongitudinalTransverseConflict, issue.Subtype);
        Assert.Equal(5.0, M(issue, "penetrationDepth"), 6);
        Assert.Equal(Severity.High, issue.Severity);
        Assert.Equal(["side", "st"], issue.Bars.Select(b => b.Id));
        // The corner bar sits in the bend: intentional contact (chord approximation ≤ 0.1 mm).
        var s = Assert.Single(result.Suppressed);
        Assert.Equal("IC-LINK", s.RuleOrExceptionId);
        Assert.InRange(s.PenetrationMm, 0, 0.1);
    }

    [Fact]
    public void F09_BeamReinforcementConflictsWithColumnReinforcementAtJoint()
    {
        var column = Box("C1", HostCategory.Column, P(0, 0, 0), P(400, 400, 3000));
        var beam = Box("B1", HostCategory.Beam, P(400, 50, 2500), P(6000, 350, 3000));
        var colBar = Bar("col", P(60, 95, 60), P(60, 95, 2940), 25, BarRole.Longitudinal, "C1");
        var beamBar = Bar("beam", P(50, 100, 2900), P(5950, 100, 2900), 20, BarRole.Longitudinal, "B1");
        var (_, issues) = Detect(Build([column, beam], [colBar, beamBar]));

        var joint = Single(issues, IssueSubtype.JointConflict);
        Assert.Equal(17.5, M(joint, "penetrationDepth"), 6);
        Assert.Equal(Severity.High, joint.Severity);
        Assert.Contains("Joint", joint.LevelOrZone);
        Assert.Equal(["B1", "C1"], joint.Hosts.Select(h => h.Id));

        // Interface faces between beam and column are concealed: no spurious cover findings.
        Assert.DoesNotContain(issues, i => i.Subtype == IssueSubtype.InsufficientCover);

        // The beam bar ends inside the column: anchorage is inferred and checked (350 mm < 35·d = 700 mm).
        var anchorage = Single(issues, IssueSubtype.AnchorageInsufficient);
        Assert.Equal(350.0, M(anchorage, "providedEmbedment"), 6);
        Assert.Equal(IssueStatus.SuspectedDefect, anchorage.Status); // inferred, so not confirmed
        Assert.Equal(Confidence.Medium, anchorage.Confidence);
    }

    [Fact]
    public void F10_SlabReinforcementConflictsWithOpeningReinforcement()
    {
        var slab = Slab(voids: [Opening("O1", P(1000, 1000, 0), P(1600, 1600, 250))]);
        var mesh = SlabBarX("mesh", 900, 50, d: 12);
        var trimmer = Bar("trim", P(700, 910, 50), P(1900, 910, 50), 16, BarRole.OpeningTrimmer, "S1", "B1");
        var crossing = SlabBarX("cross", 1300, 50, d: 12);
        var (_, issues) = Detect(Build([slab], [mesh, trimmer, crossing]));

        var conflict = Single(issues, IssueSubtype.SlabOpeningReinforcementConflict);
        Assert.Equal(4.0, M(conflict, "penetrationDepth"), 6);
        var through = Single(issues, IssueSubtype.CrossesOpening);
        Assert.Equal(600.0, M(through, "lengthInVoid"), 6);
        Assert.Equal(["cross"], through.Bars.Select(b => b.Id));
        Assert.False(through.AutoResolutionPermitted);
    }

    [Fact]
    public void F11_WallReinforcementConflictsAroundWindowOpening()
    {
        var wall = Box("W1", HostCategory.Wall, P(0, 0, 0), P(5000, 200, 3000), voids: [Opening("WIN", P(2000, -10, 1000), P(3000, 210, 2000))]);
        var throughBar = Bar("v2500", P(2500, 40, 50), P(2500, 40, 2950), 12, BarRole.Distribution, "W1", "EF-V");
        var nearBar = Bar("v1960", P(1960, 40, 50), P(1960, 40, 2950), 12, BarRole.Distribution, "W1", "EF-V");
        var trimmer = Bar("trim", P(1950, 40, 700), P(1950, 40, 2300), 16, BarRole.OpeningTrimmer, "W1", "EF-V");
        var (_, issues) = Detect(Build([wall], [throughBar, nearBar, trimmer]));

        var conflict = Single(issues, IssueSubtype.WallOpeningReinforcementConflict);
        Assert.Equal(4.0, M(conflict, "penetrationDepth"), 6);
        var through = Single(issues, IssueSubtype.CrossesOpening);
        Assert.Equal(1000.0, M(through, "lengthInVoid"), 6);
        Assert.DoesNotContain(issues, i => i.Subtype is IssueSubtype.InsufficientCover or IssueSubtype.InsufficientCoverToOpening);
    }

    [Fact]
    public void F12_ColumnStarterBarsConflictWithFootingReinforcement()
    {
        var footing = Box("F1", HostCategory.Footing, P(0, 0, 0), P(2000, 2000, 600));
        var column = Box("C1", HostCategory.Column, P(800, 800, 600), P(1200, 1200, 3600));
        var mesh = Bar("mesh", P(60, 850, 542), P(1940, 850, 542), 16, BarRole.Distribution, "F1", "T1");
        var starter = Bar("starter", P(850, 850, 100), P(850, 850, 1400), 20, BarRole.Starter, "F1");
        var (_, issues) = Detect(Build([footing, column], [mesh, starter]));

        var conflict = Single(issues, IssueSubtype.FoundationStarterConflict);
        Assert.Equal(18.0, M(conflict, "penetrationDepth"), 6);
        Assert.Equal(Severity.High, conflict.Severity);
        // Starter anchorage into the column is inferred and sufficient (800 mm ≥ 700 mm); no cover issues at the concealed interface.
        Assert.DoesNotContain(issues, i => i.Subtype is IssueSubtype.AnchorageInsufficient or IssueSubtype.InsufficientCover);
    }

    [Fact]
    public void F13_InvalidLapSpliceAndAnchorageGeometry()
    {
        // (a) Contact lap of 300 mm where 40·d = 640 mm is required.
        var a = Bar("lapA", P(100, 1000, 50), P(1500, 1000, 50), 16, BarRole.Distribution, "S1", "B1");
        var b = Bar("lapB", P(1200, 1016, 50), P(2900, 1016, 50), 16, BarRole.Distribution, "S1", "B1");
        // (b) Declared non-contact lap whose bars are 60 mm apart (limit 50 mm).
        var c = Bar("ncA", P(100, 2000, 50), P(1500, 2000, 50), 16, BarRole.Distribution, "S1", "B1");
        var d = Bar("ncB", P(800, 2076, 50), P(2900, 2076, 50), 16, BarRole.Distribution, "S1", "B1");
        // (c) 90° hook whose extension (70 mm) is below 12·d = 144 mm.
        var hook = Shaped("hook", PathFactory.Polyline([P(100, 2500, 50), P(1000, 2500, 50), P(1000, 2500, 150)], 30,
            [PathElementKind.Straight, PathElementKind.HookExtension]), 12, BarRole.Distribution, "S1", "B1");
        var model = Build([Slab()], [a, b, c, d, hook], splices: [new SpliceDefinition("SP1", "ncA", "ncB", SpliceKind.Lap)]);
        var (result, issues) = Detect(model);

        var lap = Assert.Single(issues, i => i.Subtype == IssueSubtype.LapLengthInsufficient && i.Bars.Any(x => x.Id == "lapA"));
        Assert.Equal(300.0, M(lap, "providedLapLength"), 6);
        Assert.Equal(640.0, M(lap, "requiredLapLength"), 6);
        Assert.Equal(IssueStatus.SuspectedDefect, lap.Status); // inferred lap
        Assert.False(lap.AutoResolutionPermitted);

        var nc = Single(issues, IssueSubtype.LapGeometryInvalid);
        Assert.Equal(60.0, M(nc, "clearOffset"), 6);
        Assert.Equal(IssueStatus.ConfirmedDefect, nc.Status); // declared splice, verified rule

        var hookIssue = Single(issues, IssueSubtype.HookExtensionInsufficient);
        Assert.Equal(70.0, M(hookIssue, "providedExtension"), 6);
        Assert.Equal(144.0, M(hookIssue, "requiredExtension"), 6);
        Assert.Contains(result.Laps, l => l is { BarA: "lapA", BarB: "lapB", Declared: false });
    }

    [Fact]
    public void F13b_DeclaredAnchorageTooShort()
    {
        var column = Box("C1", HostCategory.Column, P(0, 0, 0), P(400, 400, 3000));
        var beam = Box("B1", HostCategory.Beam, P(400, 50, 2500), P(6000, 350, 3000));
        var bar = Bar("beam", P(250, 200, 2900), P(5950, 200, 2900), 20, BarRole.Longitudinal, "B1") with
        {
            Anchorages = [new AnchorageRequirement(BarEnd.Start, "C1")],
        };
        var (_, issues) = Detect(Build([column, beam], [bar]));
        var issue = Single(issues, IssueSubtype.AnchorageInsufficient);
        Assert.Equal(150.0, M(issue, "providedEmbedment"), 6);
        Assert.Equal(700.0, M(issue, "requiredDevelopmentLength"), 6);
        Assert.Equal(IssueStatus.ConfirmedDefect, issue.Status);
        Assert.False(issue.AutoResolutionPermitted);
    }

    [Fact]
    public void F16_MissingDesignInformationIsIndeterminateNotCompliant()
    {
        var rules = TestRules.Standard() with
        {
            Cover = [],
            LapLengths = [],
            Project = new ProjectParameters(), // no aggregate size, but SPC-1 has an aggregate term
        };
        var bars = new[]
        {
            SlabBarX("a", 1000, 50), SlabBarX("b", 1060, 50), // parallel: spacing needs the aggregate size
            Bar("lapA", P(100, 2000, 50), P(1500, 2000, 50), 16, BarRole.Distribution, "S1", "B1"),
            Bar("lapB", P(1200, 2016, 50), P(2900, 2016, 50), 16, BarRole.Distribution, "S1", "B1"),
        };
        var (_, issues) = Detect(Build([Slab()], bars), rules);

        Assert.All(issues, i => Assert.Equal(IssueStatus.Indeterminate, i.Status));
        Assert.All(issues, i => Assert.False(i.AutoResolutionPermitted));
        Assert.All(issues, i => Assert.NotEmpty(i.MissingInputs));
        Assert.Contains(issues, i => i.Check == CheckKind.Cover && i.MissingInputs.Any(m => m.StartsWith("CoverRule")));
        Assert.Contains(issues, i => i.Check == CheckKind.ClearSpacing && i.MissingInputs.Contains("MaximumAggregateSize"));
        Assert.Contains(issues, i => i.Check == CheckKind.Lap && i.MissingInputs.Contains("LapLengthRule"));
        // Indeterminate cover findings for all four bars on the same face type are consolidated into one issue.
        var bottom = Assert.Single(issues, i => i.Check == CheckKind.Cover && i.Explanation.Contains("Bottom"));
        Assert.Equal(4, bottom.AffectedBarCount);
    }

    [Theory]
    [InlineData(19.51, false)] // surface distance −0.49 mm: within the 0.5 mm contact tolerance
    [InlineData(19.49, true)] //  surface distance −0.51 mm: a clash
    public void F17_ContactToleranceBoundary(double centreDistance, bool expectClash)
    {
        var a = SlabBarX("a", 1500, 60, layer: "B1");
        var b = Bar("b", P(1500, 100, 60 + centreDistance), P(1500, 2900, 60 + centreDistance), 20, BarRole.Distribution, "S1", "B1");
        var (_, issues) = Detect(Build([Slab()], [a, b]));
        if (expectClash)
        {
            var clash = Single(issues, IssueSubtype.CrossingClash);
            Assert.Equal(0.51, M(clash, "penetrationDepth"), 6);
            Assert.Equal(Severity.Medium, clash.Severity); // below the 2 mm minor-penetration threshold
        }
        else
        {
            Assert.Equal(IssueSubtype.UnclassifiedContact, Assert.Single(issues).Subtype);
        }
    }

    [Theory]
    [InlineData(25.000, false)]
    [InlineData(24.995, false)] // within the 0.01 mm compliance tolerance
    [InlineData(24.985, true)]
    public void F17_ClearSpacingBoundary(double clear, bool expectViolation)
    {
        var (_, issues) = Detect(Build([Slab()], [SlabBarX("a", 1000, 50), SlabBarX("b", 1000 + 20 + clear, 50)]));
        Assert.Equal(expectViolation, issues.Any(i => i.Subtype == IssueSubtype.InsufficientClearSpacing));
    }

    [Fact]
    public void F17_CoverBoundaryAfterRevitUnitConversion()
    {
        // Positions supplied in Revit internal feet, converted at the adapter boundary: exactly 40 mm cover passes.
        static double Ft(double mm) => Units.Length.FromFeet(mm / 304.8).Millimetres;
        var ok = Bar("ok", P(Ft(50), Ft(150), Ft(50)), P(Ft(5950), Ft(150), Ft(50)), 20, BarRole.Longitudinal, "B1");
        var (_, okIssues) = Detect(Build([Beam()], [ok]));
        Assert.DoesNotContain(okIssues, i => i.Subtype == IssueSubtype.InsufficientCover);

        var bad = Bar("bad", P(50, 150, 49.985), P(5950, 150, 49.985), 20, BarRole.Longitudinal, "B1");
        var (_, badIssues) = Detect(Build([Beam()], [bad]));
        var cover = Single(badIssues, IssueSubtype.InsufficientCover);
        Assert.Equal(39.985, M(cover, "providedCover"), 6);
    }

    [Fact]
    public void F19_IntentionalIntersectionsAreNotFlagged()
    {
        var stirrup = Shaped("st", Stirrup(1000), 10, BarRole.Stirrup, "B1");
        var corners = new[]
        {
            Bar("c1", P(50, 60, 60), P(5950, 60, 60), 20, BarRole.Longitudinal, "B1"),
            Bar("c2", P(50, 240, 60), P(5950, 240, 60), 20, BarRole.Longitudinal, "B1"),
            Bar("c3", P(50, 60, 440), P(5950, 60, 440), 20, BarRole.Longitudinal, "B1"),
            Bar("c4", P(50, 240, 440), P(5950, 240, 440), 20, BarRole.Longitudinal, "B1"),
        };
        var beamModel = Build([Beam()], [stirrup, .. corners]);
        var (beamResult, beamIssues) = Detect(beamModel);
        Assert.Empty(beamIssues);
        Assert.Equal(4, beamResult.Suppressed.Count(s => s.RuleOrExceptionId == "IC-LINK"));

        // Slab: two-layer mesh contact, an adequate contact lap (700 mm ≥ 640 mm) and a clash covered by an approved exception.
        var slabBars = new List<RebarBar>
        {
            SlabBarX("x1", 500, 46, 12, "B1"),
            Bar("y1", P(1500, 100, 58), P(1500, 1000, 58), 12, BarRole.Distribution, "S1", "B2"),
            Bar("lapA", P(100, 2000, 50), P(1500, 2000, 50), 16, BarRole.Distribution, "S1", "B1"),
            Bar("lapB", P(800, 2016, 50), P(2900, 2016, 50), 16, BarRole.Distribution, "S1", "B1"),
            SlabBarX("e1", 2600, 50), SlabBarX("e2", 2615, 50),
        };
        var rules = TestRules.Standard() with
        {
            Exceptions = [new ApprovedException { Id = "EX-1", ApprovalReference = "RFI-042", ApprovedBy = "Engineer of record", BarIds = ["e1", "e2"] }],
        };
        var (slabResult, slabIssues) = Detect(Build([Slab()], slabBars), rules);
        Assert.True(slabIssues.Count == 0, Dump(slabIssues));
        Assert.Contains(slabResult.Suppressed, s => s.RuleOrExceptionId == "IC-MESH");
        Assert.Contains(slabResult.Suppressed, s => s.RuleOrExceptionId == "EX-1" && s.Subtype == IssueSubtype.ParallelOverlap);
        Assert.Contains(slabResult.Laps, l => l.BarA == "lapA" && Math.Abs(l.OverlapLengthMm - 700) < 1e-6);
    }

    [Fact]
    public void IssueIdsAreDeterministicAcrossRunsAndParallelism()
    {
        var model = Build([Slab()], [SlabBarX("a", 1000, 50), SlabBarX("b", 1015, 50), SlabBarX("c", 1100, 50), SlabBarX("d", 1100, 50)]);
        var rules = TestRules.Standard();
        var seq = new Analysis.ModelAnalyzer(options: new Analysis.AnalyzerOptions { MaxDegreeOfParallelism = 1 }).Analyze(model, rules);
        var par = new Analysis.ModelAnalyzer(options: new Analysis.AnalyzerOptions { MaxDegreeOfParallelism = 8 }).Analyze(model, rules);
        var c = new Classification.DefectClassifier();
        Assert.Equal(c.Classify(seq.Findings, model, rules).Select(i => i.Id), c.Classify(par.Findings, model, rules).Select(i => i.Id));
        Assert.Equal(seq.Findings.Select(f => f.Fingerprint), par.Findings.Select(f => f.Fingerprint));
    }
}
