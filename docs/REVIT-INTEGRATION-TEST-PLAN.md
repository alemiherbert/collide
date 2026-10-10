# Revit integration test plan

To be executed on the first machine with Revit 2025, 2026 or 2027. Nothing below has been executed yet.
Record for each step: Revit version and build, result, evidence (screenshot, log excerpt from
`%LOCALAPPDATA%\Collide\logs`, audit lines from `%LOCALAPPDATA%\Collide\audit`), and any deviation.

## 0. Preparation

* Build: `dotnet build src/Collide.Revit -c Debug.R26` (or `.R25` / `.R27`) on Windows; the Nice3point SDK deploys to
  `%APPDATA%\Autodesk\Revit\Addins\<year>\` (check `Collide.addin` and the `Collide.Revit` folder are present).
* Test model `collide-integration.rvt` (to be created) containing, with known coordinates:
  1. A beam framing into a column with one beam top bar deliberately passing 5 mm from a column bar.
  2. A 250 mm slab with a two-layer bottom mesh (layers set via the `Collide Layer` parameter), a shaft opening, a
     trimmer bar overlapping a mesh bar by 4 mm, and one bar crossing the opening.
  3. A wall with a window, a vertical bar through the window and a trimmer bar.
  4. A footing with column starter bars passing through the top mesh.
  5. A rebar set (≥ 5 bars, shape-driven, fixed number) with one bar 6 mm clear from a neighbouring bar.
  6. A beam with stirrups (closed, with 135° hooks) and corner bars tied into the stirrup corners.
  7. A coupler between two bars; an area-reinforcement system.
* Rule file `collide-integration.collide.json` next to the model (copy of the example with `resolution.mode`
  `ProposeOnly` for steps 1–9, `ApplyUnambiguous` for step 10).

## 1. Loading the add-in

* [ ] Revit starts without add-in load errors; the **Collide** ribbon panel shows four buttons.
* [ ] The **Rebar clashes** dockable pane is registered (View → User Interface lists it) and opens via **Clash Pane**.
* [ ] Revit 2025 only: no `FileLoadException` for Serilog / CommunityToolkit.Mvvm when other add-ins are loaded
      (shared load context). If one occurs, port Tekton's `SharedDependencyCheck`.

## 2. Opening a test model

* [ ] On open, the log shows "Collide monitoring started … rules … (path)" and the pane reaches "Monitoring — up to date".
* [ ] Initial scan time is logged; Revit remains responsive while the pane shows "Initial scan".
* [ ] Opening a second project: switching windows switches the pane's content.

## 3. Collecting actual reinforcement geometry

Use a diagnostic run of Export Report and compare with Revit:
* [ ] Bar count per rebar element = Revit `Quantity` (excluded positions skipped; `DoesBarExistAtPosition`).
* [ ] For 5 bars, centreline endpoints in the report match Revit's coordinates (Snoop/Lookup or dimensions) within
      0.01 mm after unit conversion; nominal and model diameters match the bar type.
* [ ] Bend arcs: centre/radius/sweep correct for a standard shape, including a reversed curve in the chain.
* [ ] **Hook tagging**: for a bar with start and end hooks, confirm the curve order assumed by `RevitGeometry.TagHooks`
      (`[extension, bend, …]` / `[…, bend, extension]`). Correct the tagging if Revit orders them differently.
* [ ] `MoveBarInSet`-moved bars are returned at their moved position by `GetTransformedCenterlineCurves`.
* [ ] Host faces: concealed faces (beam–column interface) report `GetCoverType(face) == null`; exposed faces return
      the cover type. Slab with shaft: host fidelity Exact (re-entrant faces explained by the shaft opening).
* [ ] `RebarInSystem` bars appear (design-locked); coupler appears as an accessory with its attached bars.

## 4. Verifying clash locations against the Revit model

* [ ] Each planted conflict (0.1–0.6) is reported with the expected subtype and measurement (e.g. joint conflict
      penetration ≈ expected value).
* [ ] **Zoom to** and **Select** in the pane select/zoom to the right elements (external events run).
* [ ] Mesh crossings and stirrup-corner contacts are *not* reported (intentional-contact rules).
* [ ] Compare against Revit's Interference Check for the same elements; explain every difference.

## 5. Live updates

* [ ] Drag a bar into a clash: the pane shows the new issue (NEW badge) without a manual scan, after the debounce.
* [ ] Move it back: the issue disappears and "resolved" increments.
* [ ] Delete an element involved in a clash: its issues disappear.
* [ ] Change a host (slab thickness, wall opening size): cover/opening issues update.
* [ ] Rapid edits (array of 50 bars, multi-drag): one or few batches in the log; no exceptions; Revit stays responsive.
* [ ] Undo and redo of the above: the pane follows the model state.
* [ ] Pause → edit → Resume: changes made while paused are processed on resume.
* [ ] Scopes: Active view / Current level / Selection restrict the monitored elements as expected.

## 6. Inspecting and applying an approved correction

* [ ] Select the 6 mm spacing issue → **Inspect corrections** lists ranked candidates with invariants.
* [ ] Approve one: a single transaction named `Collide: …` appears in the undo list; the bar moved by exactly the
      proposed vector (measure in Revit); the pane re-validates ("after correction") and the issue is resolved.
* [ ] The audit file contains `ApprovalRecorded … Committed` for the correction, with the Revit user name.

## 7. Undo of a correction

* [ ] Ctrl+Z restores the bar exactly; the pane shows the issue again.
* [ ] Re-applying the same candidate after undo works (precondition hashes match again).

## 8. Transaction rollback

* [ ] Force a failure: apply a correction to a bar of an area-reinforcement system (refused → rolled back), or lock
      the element / make it read-only in a workshared model. Expect `RolledBack` in the pane, no undo entry, model
      unchanged, `RollbackVerified` in the audit.
* [ ] Provoke a Revit warning during commit (e.g. move a bar out of its host via a permissive test rule): commit is
      rolled back by the failure preprocessor, the reason is reported.
* [ ] Edit a bar after **Inspect corrections** and then approve: expect `StalePrecondition`, nothing applied.

## 9. Pane responsiveness

* [ ] With a large model (≥ 10,000 bars), the pane stays responsive during the initial scan and during edits;
      measure time from edit to pane update (target: < 1 s for local edits after the debounce).
* [ ] No UI-thread exceptions (dispatcher) in the log; closing the pane during processing is safe.

## 10. Resolve command (policy ApplyUnambiguous)

* [ ] **Resolve** applies only unambiguous, verifiable corrections within auto limits as one undoable group
      (`Collide: resolve clashes`); everything else is reported as awaiting approval / no safe resolution.
* [ ] One Ctrl+Z undoes the whole run.

## 11. Persistence after saving and reopening

* [ ] Save, close and reopen the model: corrected bars stay at corrected positions; the initial scan after reopening
      reproduces the same issue ids for unchanged problems (deterministic ids).
* [ ] Workshared model: synchronise with central and reopen; repeat.

## 12. Version matrix

Repeat 1–6 and 8 on Revit 2025 (.NET 8, no add-in isolation), 2026 (.NET 8) and 2027 (.NET 10).
