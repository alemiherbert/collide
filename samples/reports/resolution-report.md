# Rebar clash and detailing report

Generated 2026-10-10 09:00:00 UTC by Collide rebar clash engine 1.0.0.0.

| Model | Revision | Content hash | Bars | Hosts | Rules | Standard | Rules hash |
|---|---|---|---|---|---|---|---|
| demo-building | 2 | `5D24B6906957B18F` | 18 | 4 | Example project rules A (example) | <governing design standard> <edition> | `5A6CD26C058DFB37` |

## Summary

- **14** issues: 7 confirmed defects, 7 suspected, 0 warnings, 0 indeterminate (missing design information).
- 2 intentional contacts or approved exceptions were recognised and not reported as defects.
- Resolution (applied, policy ApplyUnambiguous): 1 corrected automatically, 1 awaiting engineering approval, 13 unresolved after the run.

| Severity | Count |
|---|---|
| High | 12 |
| Medium | 2 |

| Category | Count |
|---|---|
| Constructability | 1 |
| Detailing | 5 |
| RebarToConcrete | 3 |
| RebarToRebar | 5 |

## Issues

| Id | Severity | Status | Type | Bars | Location | Key measurement | Rule |
|---|---|---|---|---|---|---|---|
| RC-CBC7F55C1E | High | ConfirmedDefect | Duplicate | S-DUP-A, S-DUP-B | Level 1 | maxCentrelineDeviation = 0 mm | — |
| RC-58D085252B | High | ConfirmedDefect | JointConflict | BEAM-T1, COL-V1 | Level 2; Level 1; Joint B1/C1 | penetrationDepth = 17.5 mm | — |
| RC-AE1CB5AA18 | High | ConfirmedDefect | SlabOpeningReinforcementConflict | S-MESH, S-TRIM | Level 1 | penetrationDepth = 4 mm | — |
| RC-011A879F6E | High | ConfirmedDefect | WallOpeningReinforcementConflict | W-TRIM, W-V-NEAR | Level 1 | penetrationDepth = 4 mm | — |
| RC-B8362B61BB | High | SuspectedDefect | InsufficientCover | S-LOW | Level 1 | providedCover = 14 mm | COV-SLAB |
| RC-8402F79EC3 | High | ConfirmedDefect | CrossesOpening | S-THROUGH | Level 1 | lengthInVoid = 600 mm | — |
| RC-D430D379C0 | High | ConfirmedDefect | CrossesOpening | W-V-THROUGH | Level 1 | lengthInVoid = 1000 mm | — |
| RC-AA741B5A2A | High | SuspectedDefect | LapLengthInsufficient | S-LAP-A, S-LAP-B | Level 1 | providedLapLength = 300 mm | LAP-GENERAL |
| RC-3F41375579 | High | SuspectedDefect | AnchorageInsufficient | BEAM-T1 | Level 2; Level 1 | providedEmbedment = 350 mm | DEV-STRAIGHT |
| RC-633F151B31 | High | SuspectedDefect | TransverseReinforcementMissing | 0 bar(s) | Level 1 |  | TRV-COLUMN-ENDS |
| RC-D1E0A60C59 | High | SuspectedDefect | TransverseReinforcementMissing | 0 bar(s) | Level 1 |  | TRV-COLUMN-ENDS |
| RC-F795714A76 | High | SuspectedDefect | TransverseReinforcementMissing | 0 bar(s) | Level 2 |  | TRV-BEAM |
| RC-DA43D850AC | Medium | ConfirmedDefect | ParallelOverlap | S-X1, S-X2 | Level 1 | penetrationDepth = 1 mm | — |
| RC-89288ED848 | Medium | SuspectedDefect | InsufficientAggregatePassage | S-GAP-A, S-GAP-B | Level 1 | clearSpacing = 23 mm | SPC-SAME-LAYER |

### Details (high/critical and indeterminate)

**RC-CBC7F55C1E — Duplicate** (High, ConfirmedDefect, confidence High, engineering review required)

- Bars s-dup-a and s-dup-b occupy the same position (max centreline deviation 0 mm ≤ duplicate tolerance 1 mm) with the same diameter and length.
- Measurements: maxCentrelineDeviation = 0 mm; barLength = 2800 mm
- Recommended action: Confirm which bar is intended and delete the duplicate after engineering review; check schedules for double counting.
- Automated resolution: not permitted — Removing or merging reinforcement changes quantities; requires engineering decision.

**RC-58D085252B — JointConflict** (High, ConfirmedDefect, confidence High, engineering review required)

- Physical bar solids intersect: penetration 17.5 mm exceeds the contact tolerance 0.5 mm (Ø20 vs Ø25, contact angle 90°). No intentional-contact rule or approved exception covers this pair.
- Measurements: penetrationDepth = 17.5 mm; surfaceDistance = -17.5 mm; centrelineDistance = 5 mm; contactAngle = 90 deg; overlapVolume = 5601.2 mm3; clashingPrimitivePairs = 1 count
- Recommended action: Coordinate beam and column bar positions in the joint (layer offsets, cranking, or alternate bar positions) with the engineer.
- Automated resolution: not permitted — No involved bar is covered by a placement-tolerance rule (or all are design-locked).
- Limitation: Overlap volume estimated by voxel sampling (4096 samples).

**RC-AE1CB5AA18 — SlabOpeningReinforcementConflict** (High, ConfirmedDefect, confidence High)

- Physical bar solids intersect: penetration 4 mm exceeds the contact tolerance 0.5 mm (Ø12 vs Ø16, contact angle 0°). No intentional-contact rule or approved exception covers this pair.
- Measurements: penetrationDepth = 4 mm; surfaceDistance = -4 mm; centrelineDistance = 10 mm; contactAngle = 0 deg; overlapVolume = 31880.04 mm3; clashingPrimitivePairs = 1 count
- Recommended action: Shift the slab bar or the trimmer bar so they no longer occupy the same layer position; keep trimmer anchorage.
- Automated resolution: permitted — Candidate relocation of s-mesh within placement tolerances.

**RC-011A879F6E — WallOpeningReinforcementConflict** (High, ConfirmedDefect, confidence High)

- Physical bar solids intersect: penetration 4 mm exceeds the contact tolerance 0.5 mm (Ø16 vs Ø12, contact angle 0°). No intentional-contact rule or approved exception covers this pair.
- Measurements: penetrationDepth = 4 mm; surfaceDistance = -4 mm; centrelineDistance = 10 mm; contactAngle = 0 deg; overlapVolume = 42506.72 mm3; clashingPrimitivePairs = 1 count
- Recommended action: Re-space wall bars around the opening or offset the trimmer bars into the correct layer.
- Automated resolution: permitted — Candidate relocation of w-v-near within placement tolerances.

**RC-B8362B61BB — InsufficientCover** (High, SuspectedDefect, confidence High, engineering review required)

- Cover from bar s-low to the Bottom face of S1 is 14 mm; rule COV-SLAB requires 25 mm (Example project specification §4.1).
- Measurements: providedCover = 14 mm; requiredCover = 25 mm
- Rule: COV-SLAB (Example project specification §4.1), not engineer-verified
- Recommended action: Move the bar inward to provide the required cover, or adjust bar dimensions.
- Automated resolution: permitted — Candidate relocation of s-low within placement tolerances.
- Limitation: Rule COV-SLAB is not marked as engineer-verified; reported as suspected, not confirmed.

**RC-8402F79EC3 — CrossesOpening** (High, ConfirmedDefect, confidence High, engineering review required)

- Bar s-through passes 600 mm through opening O1 of host S1 (Riser).
- Measurements: lengthInVoid = 600 mm
- Recommended action: Stop the bar at the opening with required cover and provide trimmer bars as designed.
- Automated resolution: not permitted — No automated strategy for this issue type.

**RC-D430D379C0 — CrossesOpening** (High, ConfirmedDefect, confidence High, engineering review required)

- Bar w-v-through passes 1000 mm through opening WIN1 of host W1 (Window).
- Measurements: lengthInVoid = 1000 mm
- Recommended action: Stop the bar at the opening with required cover and provide trimmer bars as designed.
- Automated resolution: not permitted — No automated strategy for this issue type.

**RC-AA741B5A2A — LapLengthInsufficient** (High, SuspectedDefect, confidence Medium, engineering review required)

- Lap length 300 mm is less than the required 600 mm (table entry for Ø12; rule LAP-GENERAL: Example project specification §6.1). Lap inferred from geometry.
- Measurements: providedLapLength = 300 mm; requiredLapLength = 600 mm
- Rule: LAP-GENERAL (Example project specification §6.1), not engineer-verified
- Recommended action: Increase the lap length to the required value (design change: engineering approval required).
- Automated resolution: not permitted — Lengthening laps or anchorage changes the design; never adjusted automatically.
- Limitation: Based on an inferred relationship (not declared in the model).
- Limitation: Rule LAP-GENERAL is not marked as engineer-verified; reported as suspected, not confirmed.

**RC-3F41375579 — AnchorageInsufficient** (High, SuspectedDefect, confidence Medium, engineering review required)

- Straight anchorage of bar beam-t1 (Start end) into C1 provides 350 mm; rule DEV-STRAIGHT requires 800 mm (40·d; Example project specification §6.3).
- Measurements: providedEmbedment = 350 mm; requiredDevelopmentLength = 800 mm
- Rule: DEV-STRAIGHT (Example project specification §6.3), not engineer-verified
- Recommended action: Extend the bar or provide a hook/head to achieve the required anchorage (engineering approval required).
- Automated resolution: not permitted — Lengthening laps or anchorage changes the design; never adjusted automatically.
- Limitation: Anchorage requirement inferred: the bar end lies inside a host other than its own.
- Limitation: Based on an inferred relationship (not declared in the model).
- Limitation: Rule DEV-STRAIGHT is not marked as engineer-verified; reported as suspected, not confirmed.

**RC-633F151B31 — TransverseReinforcementMissing** (High, SuspectedDefect, confidence Medium, engineering review required)

- No stirrups or ties found in the start zone of C1; rule TRV-COLUMN-ENDS requires transverse reinforcement at ≤ 100 mm (Example project specification §8.2).
- Rule: TRV-COLUMN-ENDS (Example project specification §8.2), not engineer-verified
- Recommended action: Provide transverse reinforcement in the zone as required.
- Automated resolution: not permitted — No automated strategy for this issue type.
- Limitation: Rule TRV-COLUMN-ENDS is not marked as engineer-verified; reported as suspected, not confirmed.

**RC-D1E0A60C59 — TransverseReinforcementMissing** (High, SuspectedDefect, confidence Medium, engineering review required)

- No stirrups or ties found in the end zone of C1; rule TRV-COLUMN-ENDS requires transverse reinforcement at ≤ 100 mm (Example project specification §8.2).
- Rule: TRV-COLUMN-ENDS (Example project specification §8.2), not engineer-verified
- Recommended action: Provide transverse reinforcement in the zone as required.
- Automated resolution: not permitted — No automated strategy for this issue type.
- Limitation: Rule TRV-COLUMN-ENDS is not marked as engineer-verified; reported as suspected, not confirmed.

**RC-F795714A76 — TransverseReinforcementMissing** (High, SuspectedDefect, confidence Medium, engineering review required)

- No stirrups or ties found in the member of B1; rule TRV-BEAM requires transverse reinforcement at ≤ 300 mm (Example project specification §8.1).
- Rule: TRV-BEAM (Example project specification §8.1), not engineer-verified
- Recommended action: Provide transverse reinforcement in the zone as required.
- Automated resolution: not permitted — No automated strategy for this issue type.
- Limitation: Rule TRV-BEAM is not marked as engineer-verified; reported as suspected, not confirmed.

## Resolution

| Issue | Type | Decision | Selected correction | Result |
|---|---|---|---|---|
| RC-58D085252B | JointConflict | NotEligible | — | not applied |
| RC-AE1CB5AA18 | SlabOpeningReinforcementConflict | NoSafeResolution | — | not applied |
| RC-011A879F6E | WallOpeningReinforcementConflict | NoSafeResolution | — | not applied |
| RC-B8362B61BB | InsufficientCover | NoSafeResolution | — | not applied |
| RC-DA43D850AC | ParallelOverlap | RequiresApproval | Move s-x1 and s-x2 apart by 13 mm each. | not applied |
| RC-89288ED848 | InsufficientAggregatePassage | AutoApply | Move s-gap-b by 2 mm along (0, 1, 0) | Committed |

**RC-58D085252B** — NotEligible: No involved bar is covered by a placement-tolerance rule (or all are design-locked).


**RC-AE1CB5AA18** — NoSafeResolution: No permitted modification can reach the required separation/cover within the placement tolerances of the involved bars. Model left unchanged; engineering review required.


**RC-011A879F6E** — NoSafeResolution: No permitted modification can reach the required separation/cover within the placement tolerances of the involved bars. Model left unchanged; engineering review required.


**RC-B8362B61BB** — NoSafeResolution: No permitted modification can reach the required separation/cover within the placement tolerances of the involved bars. Model left unchanged; engineering review required.


**RC-DA43D850AC** — RequiresApproval: Move of 13 mm for bar s-x1 exceeds the auto-apply limit 10 mm of rule PLC-SLAB-MESH. Move of 13 mm for bar s-x2 exceeds the auto-apply limit 10 mm of rule PLC-SLAB-MESH.

- Before: penetrationDepth = 1 mm; surfaceDistance = -1 mm; centrelineDistance = 15 mm
- After: surfaceDistance = 25 mm
- Invariants: INV-01 pass, INV-02 pass, INV-03 pass, INV-04 pass, INV-05 pass, INV-06 pass, INV-07 pass, INV-08 pass, INV-09 pass, INV-10 pass, INV-11 pass

**RC-89288ED848** — AutoApply: Move s-gap-b by 2 mm along (0, 1, 0) Valid, verifiable and unambiguous (1 valid, 0 rejected).

- Before: clearSpacing = 23 mm; requiredClearSpacing = 25 mm; parallelRunLength = 2800 mm
- After: surfaceDistance = 25 mm
- Invariants: INV-01 pass, INV-02 pass, INV-03 pass, INV-04 pass, INV-05 pass, INV-06 pass, INV-07 pass, INV-08 pass, INV-09 pass, INV-10 pass, INV-11 pass

### Correction and rollback history

| Correction | Issue | Strategy | Status | Applied | Approved by |
|---|---|---|---|---|---|
| COR-BC48E7878D | RC-89288ED848 | TranslateSingle | Committed | 2026-10-10 09:00:00 | automatic |

### Audit trail

1. `RunStarted`   — Resolution run over 14 issue(s); mode ApplyUnambiguous.
2. `DecisionMade` RC-58D085252B  — NotEligible: No involved bar is covered by a placement-tolerance rule (or all are design-locked).
3. `DecisionMade` RC-AE1CB5AA18  — NoSafeResolution: No permitted modification can reach the required separation/cover within the placement tolerances of the involved bars. Model left unchanged; engineering review required.
4. `DecisionMade` RC-011A879F6E  — NoSafeResolution: No permitted modification can reach the required separation/cover within the placement tolerances of the involved bars. Model left unchanged; engineering review required.
5. `DecisionMade` RC-B8362B61BB  — NoSafeResolution: No permitted modification can reach the required separation/cover within the placement tolerances of the involved bars. Model left unchanged; engineering review required.
6. `CandidateValid` RC-DA43D850AC RC-DA43D850AC-C01 — Move s-x1 and s-x2 apart by 13 mm each. (score 0.816)
7. `DecisionMade` RC-DA43D850AC RC-DA43D850AC-C01 — RequiresApproval: Move of 13 mm for bar s-x1 exceeds the auto-apply limit 10 mm of rule PLC-SLAB-MESH. Move of 13 mm for bar s-x2 exceeds the auto-apply limit 10 mm of rule PLC-SLAB-MESH.
8. `CandidateValid` RC-89288ED848 RC-89288ED848-C01 — Move s-gap-b by 2 mm along (0, 1, 0) (score 0.106)
9. `DecisionMade` RC-89288ED848 RC-89288ED848-C01 — AutoApply: Move s-gap-b by 2 mm along (0, 1, 0) Valid, verifiable and unambiguous (1 valid, 0 rejected).
10. `ExecutionStarted` RC-89288ED848 RC-89288ED848-C01 — Move s-gap-b by 2 mm along (0, 1, 0)
11. `ModificationApplied` RC-89288ED848 RC-89288ED848-C01 — Translated s-gap-b by (0, 2, 0) (2 mm).
12. `PostValidationPassed` RC-89288ED848 RC-89288ED848-C01 — All 11 invariants hold on the actual post-state.
13. `Committed` RC-89288ED848 RC-89288ED848-C01 — Correction committed; reversible via 1 inverse translation(s).
14. `RunCompleted`   — 1 correction(s) committed; 13 issue(s) remain.

## Performance

- 18 of 18 bars analysed (18 solid primitives) in 7.8 ms.
- Broad phase kept 9 candidate bar pairs (94.118 % of all pairs pruned); 9 primitive pairs measured exactly.
- Resolution: 109.3 ms.

## Limitations

- Bars are swept tubes along chord-approximated centrelines (arc chord error reported per measurement). Free bar ends are flat discs; near-end separations are exact lower bounds (max of two one-sided exact cylinder distances). Rib/deformation geometry is represented only through the model diameter. Overlap volume is analytic for parallel straight cylinders and for straight cylinders crossing away from their ends, voxel-sampled otherwise.
- Host concrete is represented by convex solids; non-convex hosts supplied as bounding boxes are flagged as approximate and their findings are low confidence.
- Faces with unknown exposure are treated as concealed only where another host is found 1 mm beyond the face at the measurement point.
- Laps and anchorage targets may be inferred from geometry; such findings are reported as suspected, not confirmed.
- Automated corrections are limited to rigid translations within placement tolerances; rerouting, re-bending and lap relocation are proposed only as engineering actions.
