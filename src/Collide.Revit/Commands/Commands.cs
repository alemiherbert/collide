using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Collide.Analysis;
using Collide.Classification;
using Collide.Model;
using Collide.Reporting;
using Collide.Resolution;
using Collide.Revit.Application;
using Collide.Revit.Integration;
using Nice3point.Revit.Toolkit.External;

namespace Collide.Revit.Commands;

[Transaction(TransactionMode.Manual)]
public sealed class ShowClashPaneCommand : ExternalCommand
{
    public override void Execute() => Application.GetDockablePane(new DockablePaneId(CollideApplication.PaneGuid)).Show();
}

[Transaction(TransactionMode.ReadOnly)]
public sealed class FullScanCommand : ExternalCommand
{
    public override void Execute()
    {
        var session = CollideApplication.SessionFor(Application.ActiveUIDocument.Document);
        if (session is null)
        {
            TaskDialog.Show("Collide", "Live monitoring is not active for this document.");
            return;
        }

        session.Monitor.RequestFullScan();
    }
}

/// <summary>
/// Plans corrections for every eligible issue against a fresh snapshot and applies only what the loaded rules'
/// resolution policy explicitly permits (default ProposeOnly: nothing is applied). All applied corrections form one
/// undoable transaction group; if none commits, the group is rolled back.
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class ResolveClashesCommand : ExternalCommand
{
    public override void Execute()
    {
        var document = Application.ActiveUIDocument.Document;
        var session = CollideApplication.SessionFor(document);
        if (session is null)
        {
            TaskDialog.Show("Collide", "Live monitoring is not active for this document.");
            return;
        }

        var reader = new RevitModelReader(document, session.Log);
        var model = reader.ReadAll(Monitoring.MonitoringScope.EntireModel, Application.ActiveUIDocument);
        var state = DetectionState.Initialize(session.Analyzer.Prepare(model, session.Rules), session.Analyzer);

        ResolutionRunResult run;
        using (var group = new TransactionGroup(document, $"{ResolutionExecutor.TransactionPrefix} resolve clashes"))
        {
            group.Start();
            run = session.Resolution.Run(state, new RevitModelEditor(document, session.Log));
            if (run.Outcomes.Any(o => o.Execution?.Status == ExecutionStatus.Committed)) group.Assimilate();
            else group.RollBack();
        }

        var analysis = session.Analyzer.Analyze(run.FinalState.Prepared, AnalysisScope.All);
        var report = ReportBuilder.Build(run.FinalState, analysis, run.IssuesAfter, resolution: run, audit: session.Audit.Entries, ledger: session.Resolution.Executor.Ledger);
        var path = ReportFiles.Write(document, report, "resolution");
        var committed = run.Outcomes.Count(o => o.Execution?.Status == ExecutionStatus.Committed);
        var approval = run.Outcomes.Count(o => o.Plan.Decision == ResolutionDecision.RequiresApproval);
        var none = run.Outcomes.Count(o => o.Plan.Decision == ResolutionDecision.NoSafeResolution);
        TaskDialog.Show("Collide — resolution",
            $"Policy: {session.Rules.Resolution.Mode}\n{committed} correction(s) applied, {approval} awaiting approval, {none} without a safe automated resolution.\n" +
            $"{run.IssuesAfter.Count} issue(s) remain.\n\nReport: {path}");
    }
}

[Transaction(TransactionMode.ReadOnly)]
public sealed class ExportClashReportCommand : ExternalCommand
{
    public override void Execute()
    {
        var document = Application.ActiveUIDocument.Document;
        var session = CollideApplication.SessionFor(document);
        var state = session?.Engine.ValidatedState;
        if (session is null || state is null)
        {
            TaskDialog.Show("Collide", "No validated clash results yet. Wait for the initial scan or run Full Scan.");
            return;
        }

        var analysis = session.Analyzer.Analyze(state.Prepared, AnalysisScope.All);
        var issues = new DefectClassifier().Classify(analysis.Findings, state.Prepared.Model, state.Prepared.Rules);
        var report = ReportBuilder.Build(state, analysis, issues, audit: session.Audit.Entries, ledger: session.Resolution.Executor.Ledger);
        var path = ReportFiles.Write(document, report, "clashes");
        TaskDialog.Show("Collide — report", $"{issues.Count} issue(s) reported (rules: {session.Rules.Name}, {session.RulesSource}).\n\n{path}");
    }
}

internal static class ReportFiles
{
    public static string Write(Document document, EngineReport report, string kind)
    {
        var folder = !string.IsNullOrEmpty(document.PathName) && Path.GetDirectoryName(document.PathName) is { } dir
            ? dir
            : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var stem = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(document.Title)}.collide-{kind}-{DateTime.Now:yyyyMMdd-HHmmss}");
        File.WriteAllText(stem + ".json", ReportWriter.ToJson(report));
        File.WriteAllText(stem + ".md", ReportWriter.ToMarkdown(report));
        return stem + ".json";
    }
}
