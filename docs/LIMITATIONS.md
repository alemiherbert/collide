# Assumptions and limitations

## Geometry

* Bars are swept circular tubes at the **model** diameter; ribs are not modelled. Free ends are flat discs. Near a free
  end, separations are exact lower bounds (conservative), and penetration depths there are approximate.
* Arcs are chord-approximated for bar-to-bar distances (default chord error ≤ 0.1 mm, carried as an error bound;
  findings whose margin is within the bound are *low confidence* / *suspected*). Cover to whole arcs is exact; cover
  for arcs clipped by a host boundary subtracts the chord error (conservative).
* Spline or other non-line/arc centrelines from Revit are tessellated by Revit and treated as polylines.
* Overlap volume is analytic for parallel straight bars and straight crossings away from bar ends, and voxel-sampled
  (16³) otherwise; it is reported for information, not used for decisions.
* Hosts are convex regions. Non-convex hosts are represented by the convex outline of their supporting planar faces
  and flagged **approximate** unless the re-entrant faces are explained by known openings. Openings, doors, windows and
  shafts are oriented bounding boxes. Curved hosts are approximate.
* Face exposure: from Revit's cover settings where available (`GetCoverType(face) == null` → concealed); otherwise a
  face is treated as concealed only where another host is found 1 mm beyond it at the measurement point.
* Couplers/accessories are approximated by a cylinder from their bounding box.

## Detection and validation

* Laps are inferred for end-to-end parallel bars within 4·d centre offset (configurable) unless declared; anchorage
  requirements are inferred for free ends inside another host. Inferred findings are *suspected*.
* Hook detection assumes Revit's centreline chain order `[extension, bend, …]` / `[…, bend, extension]` when the bar
  has a hook type at that end — to be verified in Revit.
* Bar roles are inferred from rebar style and host category unless `Collide Role` is set; layers come only from the
  `Collide Layer` parameter. Layer-based intentional-contact rules do not apply when layers are unknown.
* Transverse spacing uses the member axis from the location curve, else the longest bounding-box axis (flagged).
* Congestion is an approximate steel-volume ratio on a regular grid; it is a constructability indicator only.
* "Incompatible layouts that cannot be physically assembled" is covered only to the extent that it appears as physical
  clashes; sequencing/assembly simulation is not implemented.

## Resolution

* Only rigid translations are generated. Re-bending, rerouting, lap relocation, bundling and alternative arrangements
  are recommended as engineering actions, not generated as candidates.
* Bars of area/path reinforcement (`RebarInSystem`) are treated as design-locked (they cannot be moved individually).
* In Revit, single-bar rebar is moved with `ElementTransformUtils.MoveElement`, bars within a set with
  `MoveBarInSet`; the executor verifies the actual geometry read back and rolls back on any mismatch or Revit failure
  (warnings included — conservative).
* The detection-time "engineering review" flag is provisional; a resolution run finalises it from the plan outcome.

## Live monitoring

* Only element ids and categories are read inside `DocumentChanged`; deleted ids are passed through unfiltered.
* Monitoring scopes (selection, view, level) are resolved by the Revit reader at extraction time; elements leaving the
  scope are removed from the monitored model.
* Revit cannot be modelled in tests: event timing, external-event queuing, undo/redo payloads and transaction
  behaviour are assumptions until validated with the integration test plan.

## Not yet done

* Per-version NuGet lock files (Tekton convention) for the new projects.
* Persisting the audit log/ledger inside the model (Extensible Storage); the audit is a JSON-lines file per document.
* Visual indicators in the 3D view (e.g. temporary graphics) — the pane selects/zooms to elements instead.
