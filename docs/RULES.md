# Rule configuration

Rules live in a JSON file (comments and trailing commas allowed). See
[`samples/rules/example-project-rules.json`](../samples/rules/example-project-rules.json) — its values are
**placeholders**, not design-code provisions.

## Principles

* **No hard-coded code provisions.** Every requirement is an explicit rule with an `id`, a `source`
  (`document`, `clause`, `edition`, `note`), an applicability `scope` and explicit units.
* **Indeterminate, not compliant.** Checks listed in `requiredChecks` (default Cover, ClearSpacing, Lap, Anchorage,
  Bend, Hook) report *indeterminate* when no rule applies or an input (e.g. `project.maximumAggregateSize`) is missing.
* **Verified vs suspected.** Set `"verified": true` only after an engineer has checked a rule against the governing
  standard; violations of unverified rules are *suspected*.
* **Most specific rule wins**: specificity = host ids / bar marks (16) > face tags, host categories, roles, layers (4)
  > diameter range (2) > tags (1); ties go to the first declared rule.

## Scope

```json
"scope": {
  "hostCategories": ["Beam", "Column", "Slab", "Wall", "Footing", "PileCap", "Pile", "BoundaryElement", "CouplingBeam"],
  "hostIds": ["123456"], "barRoles": ["Longitudinal", "Stirrup", "Tie", "Crosstie", "Distribution", "Bent", "Starter",
  "Dowel", "OpeningTrimmer", "Additional", "Pile"], "barMarks": ["12"], "layers": ["B1"],
  "faceTags": ["Top", "Bottom", "Side", "End", "Exterior", "Interior", "Opening"],
  "minimumDiameter": "16 mm", "maximumDiameter": "32 mm", "tags": ["approved-detail"]
}
```

Pair rules (clear spacing, laps, intentional contacts) must match **both** bars.

## Rule types

| Section | Fields | Meaning |
|---|---|---|
| `cover` | `minimumCover` | Clear cover from bar surface to an exposed face of the scoped face tag. `Opening` faces are cover to voids. |
| `clearSpacing` | `barDiameterFactor`, `aggregateAllowance`, `aggregateFactor`, `absoluteMinimum`, `relation` (`Any`/`SameLayer`/`BetweenLayers`) | Required clear spacing between parallel bars = max of the terms. If an aggregate term governs, the issue is *aggregate passage*. |
| `maxSpacing` | `maximumCentreSpacing` | Maximum centre spacing within a rebar set. |
| `lapLengths` | `table` [{`diameter`, `length`}], `diameterFactor`, `minimum`, `measurementBasis` | Required lap; table entry first, else factor·d with a floor. |
| `nonContactLaps` | `maximumClearOffset`, `maximumOffsetToLapLengthRatio` | Limits for non-contact laps. |
| `developmentLengths` | as laps + `anchorageType` (`Straight`/`Hooked`/`Headed`) | Required embedment into the anchoring host. |
| `bends` | `minimumInnerDiameterFactor` | Inside bend diameter ≥ factor·d. |
| `hooks` | `hookAngle`, `angleTolerance`, `extensionDiameterFactor`, `minimumExtension` | Permitted hooks and tail lengths. |
| `transverseSpacing` | `maximumSpacing`, `zone` (`Entire`/`EndZones`), `endZoneLength`, `requirePresence` | Stirrup/tie spacing and confinement zones along the member axis. |
| `restrictions` | `allowedDiameters`, `allowedShapeCodes` | Permitted diameters/shapes. |
| `congestion` | `maximumSteelRatio`, `cellSize` | Local steel-volume ratio limit (approximate method). |
| `placement` | `maximumDisplacement`, `autoApplyMaximumDisplacement`, `frame` (`Global`/`Host`), `allowedAxes`, `allowAlongBarAxis`, `permittedZoneIds` | What automated corrections may do. **Without a matching rule a bar is never moved.** |
| `intentionalContacts` | `rolesA`, `rolesB`, `requireDifferentLayers`, `minimumAngle`, `maximumAngle`, `maximumPenetration` | Contacts that are not defects (mesh layers, bars tied to links). |
| `exceptions` | `id`, `approvalReference`, `approvedBy`, `date`, `barIds`, `subtypes`, `note` | Approved details that suppress specific findings; recorded in reports. |
| `severityOverrides` | `subtype`, `severity`, `reason` | Project severity policy. |
| `resolution` | `mode` (`DetectOnly`/`ProposeOnly`/`ApplyUnambiguous`), `ambiguityMargin`, `displacementIncrement`, `movePreference`, `maxCandidatesPerIssue`, `autoApplySubtypes` | Resolution policy. Default `ProposeOnly`. |

## Tolerances (`tolerances`)

`contactTolerance` (surfaces within ± are in contact; default 0.5 mm), `complianceTolerance` (0.01 mm),
`duplicateTolerance` (1 mm), `parallelAngleTolerance` (2°), `arcChordTolerance` (0.1 mm), `minimumParallelOverlap`
(50 mm), `lapInferenceMaxOffsetFactor` (4·d), `minorPenetration` (2 mm), `interfaceProbeOffset` (1 mm),
`continuityTolerance` (0.1 mm), `minimumReportableLength` (1 mm), `inferAnchorage`, `inferLaps`.
These are numerical/modelling tolerances, not code values.

## Validation

The loader rejects: missing ids or sources, duplicate ids, bare numbers or unknown units, non-positive lengths,
rules with no terms, auto-apply limits above the maximum displacement, exceptions without bars or approval reference.
All errors are reported together.
