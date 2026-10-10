# Architecture

## Layers

```
            ┌──────────────────────────── Revit process ────────────────────────────┐
            │  Collide.Revit                                                         │
            │   Application/  CollideApplication (Toolkit ExternalApplication),      │
            │                 sessions per document, active-document forwarding      │
            │   UI/           ClashPaneView (WPF, DockablePaneProvider)              │
            │   Commands/     pane, full scan, resolve, export report                │
            │   Integration/  RevitModelReader  RevitModelEditor  RevitChangeTracker │
            │                 RevitDeltaSource / ExternalEventApiContext /           │
            │                 RevitModelInteraction   (Toolkit external events)      │
            └───────────────┬───────────────────────────────┬────────────────────────┘
                            │ immutable records only         │ view model binding
            ┌───────────────▼────────────────┐   ┌──────────▼─────────────────────────┐
            │ Collide.Core (net8.0)           │◄──│ Collide.Presentation (net8.0)      │
            │ geometry · rules · analysis ·   │   │ ClashPaneViewModel, IModelInteraction│
            │ classification · resolution ·   │   │ IUiDispatcher                       │
            │ monitoring · audit · reporting  │   └────────────────────────────────────┘
            └────────────────────────────────┘
```

Revit API types appear **only** in `Collide.Revit`. The engine and view models build and are tested on any OS.

## Data contracts

* `ReinforcementModel` — immutable snapshot (sorted immutable dictionaries) of `RebarBar`, `RebarSet`, `StructuralHost`,
  `PlacementZone`, `Accessory`, `SpliceDefinition`; updated only by `ModelDelta` (element-level replace/remove).
  A Revit `Rebar` with *n* included bar positions becomes *n* bars `"{elementId}#{position}"` plus one set.
* `IReinforcementModelSource`, `IModelEditor`, `IModelTransaction` — read and modify the host model; all-or-nothing
  transactions; disposing without commit rolls back. `BarModification` = rigid translation with expected before/after
  geometry hashes.
* `IModelDeltaSource` — extraction for live monitoring (Revit: external event). `IApiContext` — run work in the host's
  API context. `IModelInteraction` / `IUiDispatcher` — pane actions and UI-thread marshalling.
* `Finding` (raw, owned by bars / set / host / mark) → `Issue` (classified, immutable, deterministic id).

## Units

Engine coordinates are millimetres (`Vec3`), quantities are strongly typed (`Length`, `Angle`, `Volume`) at model,
rule and report boundaries. Configuration requires explicit units (`"40 mm"`, `"1.5 in"`; bare numbers are rejected).
Revit feet are converted in exactly one place (`RevitUnits`, 1 ft = 304.8 mm). Comparisons use a configurable
compliance tolerance (default 0.01 mm) so unit-conversion noise cannot flip a result.

## Geometry

* Bars are swept tubes along their actual centreline (`BarPath`: lines and arcs; bends and hooks tagged), using the
  *model* diameter for physical geometry and the *nominal* diameter for code rules.
* `CapsuleGeometryKernel` (behind `IGeometryKernel`): exact segment–segment distances; free bar ends are flat discs
  (refined with exact convex 1-D minimisation); arcs are chord-approximated with the error bound carried into every
  measurement; cover to whole arcs is computed exactly (closed-form maximum of `A cos φ + B sin φ`).
* Overlap volume: analytic for parallel cylinders (lens area × overlap) and for straight crossings
  (`V = ∫ w₁(t) w₂(t) / sin θ dt`, verified against the Steinmetz solid); voxel sampling otherwise.
* Hosts are convex regions (half-spaces) — exact clipping, containment and interior distances; openings are oriented
  boxes. Faces carry a tag (top/bottom/side/exterior/interior/end/opening) and an exposure (Revit cover settings).

## Detection pipeline

1. `PreparedModel` builds bar solids and a `SpatialIndexSnapshot` (BVH + persistent overlay). Boxes are inflated by
   half the *search margin* (largest gap any rule can care about), so the broad phase never misses a relevant pair.
2. For each focus bar (parallel, thread-local sinks): neighbours from the index → `PairAnalyzer` (duplicates, laps,
   clashes vs intentional contacts, unclassified contacts, clear spacing), accessories, `HostAnalyzer` (containment,
   openings, zones, cover with interface probing), `DetailingValidator` (bends, hooks, anchorage, restrictions).
3. Set checks (max spacing, quantity, diameter consistency), mark consistency, host checks (congestion, transverse).
4. Approved exceptions suppress matching findings; suppressed contacts are kept for traceability.
5. Output is sorted by fingerprint: identical inputs give identical findings, ids and order regardless of threads.

`AnalysisScope` defines exactly which owners a run recomputes; `DetectionState.Merge` replaces exactly the findings
owned by that scope. A randomized property test checks that incremental results equal a full re-analysis.

## Classification

`DefectClassifier` applies policy: default severities (overridable per subtype), minor-penetration downgrade,
exposed-bar escalation; status (geometric facts are *confirmed* when the measurement margin exceeds the error bound and
nothing is inferred; compliance findings are *confirmed* only with an engineer-verified rule and complete inputs);
confidence; review flag; eligibility; recommended action. Indeterminate findings with the same missing rule/input are
consolidated into one issue listing all affected bars.

## Resolution pipeline

```
Issue ─► CandidateGenerator ─► ResolutionValidator ─► ResolutionPlanner ─► (approval) ─► ResolutionExecutor
          minimal translations   hard constraints        rank, ambiguity,     explicit      transaction, read-back,
          (single/split/         neighbourhood before/   policy & auto        or policy     neighbour check, post-
          local respacing)       after, invariants        limits                             validation, commit or
                                                                                             rollback (verified)
```

Safety invariants (Part 8), evaluated with evidence for every candidate and again on the actual post-state:

| Id | Invariant |
|---|---|
| INV-01 | Target issue resolved |
| INV-02 | No newly introduced prohibited clashes or containment problems |
| INV-03 | Reinforcement quantities, bar diameters, lengths and identity data preserved |
| INV-04 | Concrete cover remains compliant (no new/worsened findings) |
| INV-05 | Minimum clear spacing remains compliant |
| INV-06 | Anchorage and laps: no new/worsened findings, laps preserved and not shortened, embedment not reduced |
| INV-07 | Reinforcement remains within permitted placement regions |
| INV-08 | Modified bars retain valid geometry |
| INV-09 | Unaffected reinforcement unchanged |
| INV-10 | Every modification can be reverted exactly |
| INV-11 | No other new or worsened findings |

Candidates with indeterminate invariant checks are *unverifiable* and never auto-applied.

## Live monitoring and Revit threading

```
Revit main thread                          background                         Revit main thread (idle)
DocumentChanged ─► RevitChangeTracker ─►  LiveClashMonitor                ─►  external event: RevitModelReader
 (read-only event:  ids + category only)    merge/de-dup, debounce,             .ReadElements(batch) → ModelDelta
                                            cancel superseded compute    ◄──
                                            IncrementalDetectionEngine
                                              Stage (lock) → Compute (Task.Run, cancellable) → Commit (revision check)
                                            Updated event ─► IUiDispatcher ─► ClashPaneViewModel ─► WPF pane
```

* The `DocumentChanged` handler never modifies the document and does no geometry work.
* Extraction runs through Toolkit `AsyncRequestExternalEvent`s, queued until Revit is idle (not during edit modes).
* Analysis runs on immutable snapshots off the API thread; obsolete results are discarded by revision.
* Changes from the add-in's own transactions (prefix `Collide:`) are flagged as self-changes and re-validated; the
  monitor has no reference to the resolution engine, so no recursive correction loop is possible.
* Approved corrections from the pane run through `CorrectionService` → `IApiContext` (external event) → one Revit
  transaction; the resulting `DocumentChanged` re-validates the region automatically.

## Determinism and reproducibility

Ordinal-sorted collections, sorted outputs, fingerprint-based ids, quantised geometry hashes, model/rule content
hashes in every report, run ids derived from content, and an injectable clock (`FixedClock`) for reproducible audit
and reports. Performance figures are the only intentionally non-deterministic report fields.
