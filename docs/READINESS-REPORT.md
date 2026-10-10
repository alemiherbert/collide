# Revit integration readiness report

Date: 2026-10-10 · Environment: Linux container, .NET SDK 10.0.112 (+ .NET 8 runtime), no Revit installation.

## Summary

| Component | Compiles | Tested here | Must be verified in Revit |
|---|---|---|---|
| Geometry kernel, spatial index, units | ✔ | ✔ 33 unit/property tests | — |
| Detection, validation, classification | ✔ | ✔ fixtures 1–13, 16, 17, 19 (+ variants) | real model data |
| Resolution (planner, validator, executor, rollback, audit) | ✔ | ✔ fixtures 1, 14, 15, 20 + 10 more | real transactions, `MoveBarInSet` results |
| Incremental detection and live monitor | ✔ | ✔ 11 simulated-event tests incl. property test | real `DocumentChanged` payloads and timing |
| Large models / performance | ✔ | ✔ fixture 18 (10,105 bars) | real model sizes and extraction cost |
| Reports and rule configuration | ✔ | ✔ 7 tests, example reports generated | — |
| Pane view model | ✔ | ✔ 6 tests against the real monitor | — |
| Revit adapter (reader, editor, tracker, external events) | ✔ R25 / R26 / R27 | ✘ (cannot run without Revit) | everything |
| Toolkit application, dockable pane (XAML), commands | ✔ R25 / R26 / R27 | ✘ | loading, layout, responsiveness |

All 105 automated tests pass (`dotnet test Collide.slnx -c Debug.R26`). The add-in builds for `Release.R25`,
`Release.R26` and `Release.R27` against `Nice3point.Revit.Api.*` reference assemblies with `Nice3point.Revit.Toolkit`
(`ExternalApplication`, `ExternalCommand`, `DockablePaneProvider`, `ExternalEvent<T>`, `AsyncExternalEvent<T>`,
`AsyncRequestExternalEvent<T, TResult>`). **Compilation proves only that the API members exist and the types match.**

## Fixture coverage (Part 7)

| # | Fixture | Tests |
|---|---|---|
| 1 | Parallel bars overlapping | `F01_ParallelBarsOverlappingPhysically`, `F01_ParallelOverlapResolvedBySymmetricSplit…` |
| 2 | Centrelines cross, solids do not | `F02_CentrelinesCrossInPlanButSolidsDoNotCollide`, `CrossingCentrelinesInPlan…` (kernel) |
| 3 | Intentional perpendicular contact | `F03_PerpendicularBarsIntentionallyTouching…`, `F03b_SameContactInTheSameLayerIsNotIntentional` |
| 4 | Insufficient clear spacing | `F04_…`, `F04b_AggregatePassageGoverns…`, `F04c_UnverifiedRuleYieldsSuspected…` |
| 5 | Cover violation | `F05_ConcreteCoverViolation` |
| 6 | Bar beyond host | `F06_BarExtendsBeyondItsHost` |
| 7 | Duplicate | `F07_DuplicateReinforcementInSamePosition`, `LargeModelTests` (25 injected duplicates) |
| 8 | Longitudinal vs stirrup | `F08_BeamLongitudinalBarCollidesWithStirrup`, `BeamBarMovedOutOfStirrupLeg…` (resolution) |
| 9 | Beam–column joint | `F09_BeamReinforcementConflictsWithColumnReinforcementAtJoint` |
| 10 | Slab vs opening reinforcement | `F10_SlabReinforcementConflictsWithOpeningReinforcement` |
| 11 | Wall around window | `F11_WallReinforcementConflictsAroundWindowOpening` |
| 12 | Starters vs footing | `F12_ColumnStarterBarsConflictWithFootingReinforcement` |
| 13 | Invalid lap/anchorage/hook | `F13_InvalidLapSpliceAndAnchorageGeometry`, `F13b_DeclaredAnchorageTooShort` |
| 14 | Fix creates another clash | `F14_CorrectionThatCreatesAnotherClashIsRejectedAndAlternativeChosen` |
| 15 | No safe resolution | `F15_NoSafeAutomatedResolutionLeavesModelUnchanged`, `F15b_ExceedingPlacementTolerance…` |
| 16 | Missing design information | `F16_MissingDesignInformationIsIndeterminateNotCompliant` |
| 17 | Tolerance boundaries | `F17_ContactToleranceBoundary`, `F17_ClearSpacingBoundary`, `F17_CoverBoundaryAfterRevitUnitConversion` |
| 18 | Large model | `LargeModelTests` (3 tests) |
| 19 | Intentional intersections | `F19_IntentionalIntersectionsAreNotFlagged` |
| 20 | Rollback after injected failure | `F20_InjectedFailureMidCorrectionRollsBackCompletely`, `HostThatAppliesDifferentGeometry…` |

Live detection (simulated events): adding a conflicting bar, moving into/out of a clash, deleting an element, host
geometry change, rapid changes with debounce and duplicate notifications, out-of-order/obsolete results, cancellation,
error recovery, pause/resume, recursion prevention after the add-in's own correction, and a randomized
incremental-equals-full property test. These use `InMemoryModelStore`/`InMemoryDeltaSource`; they are **not**
evidence that Revit events behave as assumed.

## Performance (this container, Release, .NET 8)

| Scenario | Result |
|---|---|
| 10,105 bars (two-layer meshes + 800 columns) full analysis | ≈ 1.0 s; 40,030 candidate pairs of ~51 M (99.92 % pruned); 30,381 intentional contacts recognised; 25/25 duplicates found |
| Single-bar edit in the same model (incremental) | ≈ 50 ms; 1 focus bar re-analysed against its neighbours |
| Full test suite | ≈ 6 s |

Optimisation done: overlap volume for straight crossings moved from voxel sampling to an exact 1-D integral
(property test suite 58 s → 3 s). Revit extraction cost is unknown until measured in Revit.

## Revit API usage to confirm (from the test plan)

1. `Rebar.GetTransformedCenterlineCurves` curve order and orientation, including hook curves (hook tagging assumption).
2. `Rebar.MoveBarInSet` (documented to concatenate with existing moves) yields the exact expected geometry; the
   executor verifies read-back to 0.001 mm and rolls back otherwise.
3. `ElementTransformUtils.MoveElement` for single-bar rebar keeps constraints/host as expected.
4. `RebarHostData.GetCoverType(face.Reference)` returns null exactly for concealed faces with `ComputeReferences = true`.
5. `DocumentChangedEventArgs` id sets for rebar edits, set re-layouts, host edits, inserts, undo/redo; transaction
   names of the add-in's own transactions inside the `TransactionGroup` of the Resolve command.
6. Toolkit external events: queued execution while Revit is in edit modes; `RaiseAsync` from the monitor's
   background thread; behaviour when a document closes with requests pending.
7. Failure preprocessor behaviour on commit (warnings and errors roll back the correction).
8. Revit 2025 shared load context with other add-ins (Serilog, CommunityToolkit.Mvvm versions).

## Dependencies

All restored from nuget.org without Revit: `Nice3point.Revit.Sdk 6.2.3`, `Nice3point.Revit.Toolkit` and
`Nice3point.Revit.Api.RevitAPI/RevitAPIUI` (`$(RevitVersion).*`; reference assemblies only, never copied to output),
`Serilog 4.4.0`, `Serilog.Sinks.File 6.0.0`, `CommunityToolkit.Mvvm 8.4.2`, xunit 2.9.3. No third-party geometry library.

## Open decisions

* Merge target layout inside Tekton (separate `Tekton.Clash` project vs folder in `Tekton.Core`).
* Whether warnings during a correction's commit should always roll back (current, conservative) or be filtered.
* Where to persist the audit/ledger long-term (file per document now; Extensible Storage possible).
* Real project rule values and which rules an engineer marks `verified`.
