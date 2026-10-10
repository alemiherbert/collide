using System.Globalization;
using System.Text;
using System.Text.Json;
using Collide.Findings;
using Collide.Serialization;

namespace Collide.Reporting;

/// <summary>Serialises reports as JSON (machine-readable) and Markdown (human-readable summary).</summary>
public static class ReportWriter
{
    public static string ToJson(EngineReport report) => JsonSerializer.Serialize(report, CollideJson.Options);

    public static string ToMarkdown(EngineReport report, int maxIssues = 200)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        void L(string line = "") => sb.AppendLine(line);
        string Fmt(FormattableString f) => f.ToString(inv);

        L("# Rebar clash and detailing report");
        L();
        L(Fmt($"Generated {report.GeneratedAt:yyyy-MM-dd HH:mm:ss} UTC by {report.Tool}."));
        L();
        L("| Model | Revision | Content hash | Bars | Hosts | Rules | Standard | Rules hash |");
        L("|---|---|---|---|---|---|---|---|");
        L(Fmt($"| {report.Model.ModelId} | {report.Model.Revision} | `{report.Model.ContentHash}` | {report.Model.Bars} | {report.Model.Hosts} | {report.Rules.Name} {report.Rules.Revision} | {report.Rules.DesignStandard} {report.Rules.StandardVersion} | `{report.Rules.Hash}` |"));
        L();

        var s = report.Summary;
        L("## Summary");
        L();
        L(Fmt($"- **{s.TotalIssues}** issues: {s.ConfirmedDefects} confirmed defects, {s.SuspectedDefects} suspected, {s.Warnings} warnings, {s.Indeterminate} indeterminate (missing design information)."));
        L(Fmt($"- {s.SuppressedContacts} intentional contacts or approved exceptions were recognised and not reported as defects."));
        if (report.Resolution is { } r)
            L(Fmt($"- Resolution ({(r.DryRun ? "dry run" : "applied")}, policy {r.Mode}): {s.AutoResolved} corrected automatically, {s.RequiringApproval} awaiting engineering approval, {s.Unresolved} unresolved after the run."));
        L();
        L("| Severity | Count |");
        L("|---|---|");
        foreach (var sev in Enum.GetValues<Severity>().OrderByDescending(v => v))
            if (s.BySeverity.TryGetValue(sev.ToString(), out var n)) L(Fmt($"| {sev} | {n} |"));
        L();
        L("| Category | Count |");
        L("|---|---|");
        foreach (var (cat, n) in s.ByCategory) L(Fmt($"| {cat} | {n} |"));
        L();

        L("## Issues");
        L();
        L("| Id | Severity | Status | Type | Bars | Location | Key measurement | Rule |");
        L("|---|---|---|---|---|---|---|---|");
        foreach (var i in report.Issues.Take(maxIssues))
        {
            var bars = i.Bars.Count > 0 ? string.Join(", ", i.Bars.Take(3).Select(b => b.Mark ?? b.Id)) + (i.AffectedBarCount > 3 ? $" +{i.AffectedBarCount - 3}" : "") : $"{i.AffectedBarCount} bar(s)";
            var m = i.Measurements.FirstOrDefault(x => x.Unit is "mm" or "ratio");
            L(Fmt($"| {i.Id} | {i.Severity} | {i.Status} | {i.Subtype} | {bars} | {i.LevelOrZone ?? ""} | {m?.ToString() ?? ""} | {i.Rule?.RuleId ?? "—"} |"));
        }

        if (report.Issues.Count > maxIssues) L(Fmt($"\n_{report.Issues.Count - maxIssues} more issue(s) in the JSON report._"));
        L();

        var details = report.Issues.Where(i => i.Severity >= Severity.High || i.Status == IssueStatus.Indeterminate).Take(40).ToArray();
        if (details.Length > 0)
        {
            L("### Details (high/critical and indeterminate)");
            L();
            foreach (var i in details)
            {
                L(Fmt($"**{i.Id} — {i.Subtype}** ({i.Severity}, {i.Status}, confidence {i.Confidence}{(i.RequiresEngineeringReview ? ", engineering review required" : "")})"));
                L();
                L($"- {i.Explanation}");
                if (i.Measurements.Count > 0) L($"- Measurements: {string.Join("; ", i.Measurements.Where(x => x.Unit != "tag").Select(x => x.ToString()))}");
                if (i.Rule is { } rule) L($"- Rule: {rule.RuleId} ({rule.Source}){(rule.Verified ? "" : ", not engineer-verified")}");
                if (i.MissingInputs.Count > 0) L($"- Missing inputs: {string.Join(", ", i.MissingInputs)}");
                L($"- Recommended action: {i.RecommendedAction}");
                L($"- Automated resolution: {(i.AutoResolutionPermitted ? "permitted" : "not permitted")} — {i.AutoResolutionReason}");
                foreach (var lim in i.Limitations) L($"- Limitation: {lim}");
                L();
            }
        }

        if (report.Resolution is { } res)
        {
            L("## Resolution");
            L();
            L("| Issue | Type | Decision | Selected correction | Result |");
            L("|---|---|---|---|---|");
            foreach (var o in res.Outcomes)
                L(Fmt($"| {o.IssueId} | {o.Subtype} | {o.Decision} | {o.Selected?.Description ?? "—"} | {o.ExecutionStatus ?? (res.DryRun ? "dry run" : "not applied")} |"));
            L();
            foreach (var o in res.Outcomes)
            {
                L(Fmt($"**{o.IssueId}** — {o.Decision}: {o.Reason}"));
                L();
                if (o.Selected is { } sel)
                {
                    L($"- Before: {string.Join("; ", sel.Before.Where(x => x.Unit == "mm").Select(x => x.ToString()))}");
                    L($"- After: {string.Join("; ", sel.After.Select(x => x.ToString()))}");
                    L($"- Invariants: {string.Join(", ", sel.Invariants.Select(x => $"{x.Id} {(x.Passed ? "pass" : "FAIL")}"))}");
                }

                foreach (var alt in o.Alternatives.Take(5)) L(Fmt($"- Alternative {alt.CandidateId} (score {alt.Score:0.####}): {alt.Description}"));
                foreach (var rej in o.Rejected.Take(5)) L($"- Rejected {rej.CandidateId}: {string.Join(" | ", rej.Reasons)}");
                L();
            }

            if (res.Corrections.Count > 0)
            {
                L("### Correction and rollback history");
                L();
                L("| Correction | Issue | Strategy | Status | Applied | Approved by |");
                L("|---|---|---|---|---|---|");
                foreach (var c in res.Corrections)
                    L(Fmt($"| {c.CorrectionId} | {c.IssueId} | {c.Strategy} | {c.Status} | {c.AppliedAt:yyyy-MM-dd HH:mm:ss} | {c.ApprovedBy ?? "automatic"} |"));
                L();
            }
        }

        if (report.Audit.Count > 0)
        {
            L("### Audit trail");
            L();
            foreach (var e in report.Audit.Take(200)) L(Fmt($"{e.Sequence}. `{e.Action}` {e.IssueId} {e.CandidateId} — {e.Message}"));
            L();
        }

        if (report.ChecksNotPerformed.Count > 0)
        {
            L("## Checks not performed");
            L();
            foreach (var c in report.ChecksNotPerformed) L($"- {c}");
            L();
        }

        L("## Performance");
        L();
        var p = report.Performance;
        L(Fmt($"- {p.BarsAnalysed} of {p.BarsInModel} bars analysed ({p.SolidPrimitives} solid primitives) in {p.AnalysisMilliseconds:0.#} ms."));
        L(Fmt($"- Broad phase kept {p.CandidateBarPairs} candidate bar pairs ({p.BroadPhasePruning:P3} of all pairs pruned); {p.PrimitivePairsMeasured} primitive pairs measured exactly."));
        if (p.ResolutionMilliseconds is { } rm) L(Fmt($"- Resolution: {rm:0.#} ms."));
        L();
        L("## Limitations");
        L();
        foreach (var lim in report.Limitations) L($"- {lim}");
        return sb.ToString();
    }
}
