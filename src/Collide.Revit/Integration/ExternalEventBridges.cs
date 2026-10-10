using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Collide.Integration;
using Collide.Model;
using Collide.Monitoring;
using Collide.Presentation;
using Collide.Resolution;
using Nice3point.Revit.Toolkit.External;
using Serilog;

namespace Collide.Revit.Integration;

/// <summary>
/// Extraction for the live monitor. Each request is queued as a Toolkit external event and executed by Revit when it is
/// idle (never during an active edit), so model reads always happen in a valid API context while analysis stays on the
/// monitor's background thread.
/// </summary>
internal sealed class RevitDeltaSource : IModelDeltaSource
{
    private readonly AsyncRequestExternalEvent<ChangeBatch, ModelDelta> _extract;
    private readonly AsyncRequestExternalEvent<MonitoringScope, ReinforcementModel> _full;

    public RevitDeltaSource(Document document, ILogger log)
    {
        _extract = new AsyncRequestExternalEvent<ChangeBatch, ModelDelta>((application, batch) =>
        {
            EnsureValid(document);
            return new RevitModelReader(document, log).ReadElements(batch, ActiveUiDocumentFor(application, document));
        });
        _full = new AsyncRequestExternalEvent<MonitoringScope, ReinforcementModel>((application, scope) =>
        {
            EnsureValid(document);
            return new RevitModelReader(document, log).ReadAll(scope, ActiveUiDocumentFor(application, document));
        });
    }

    public Task<ModelDelta> ExtractAsync(ChangeBatch batch, CancellationToken cancellationToken) =>
        _extract.RaiseAsync(batch).WaitAsync(cancellationToken);

    public Task<ReinforcementModel> ExtractFullAsync(MonitoringScope scope, CancellationToken cancellationToken) =>
        _full.RaiseAsync(scope).WaitAsync(cancellationToken);

    private static void EnsureValid(Document document)
    {
        if (!document.IsValidObject) throw new InvalidOperationException("The document was closed.");
    }

    internal static UIDocument? ActiveUiDocumentFor(UIApplication application, Document document) =>
        application.ActiveUIDocument is { } ui && ui.Document.Equals(document) ? ui : null;
}

/// <summary><see cref="IApiContext"/> backed by a Toolkit external event: the delegate runs on Revit's API thread.</summary>
internal sealed class ExternalEventApiContext : IApiContext
{
    private readonly AsyncRequestExternalEvent<Func<object?>, object?> _run = new((_, action) => action());

    public async Task<T> RunAsync<T>(Func<T> action, CancellationToken cancellationToken = default)
    {
        var result = await _run.RaiseAsync(() => action()).WaitAsync(cancellationToken).ConfigureAwait(false);
        return (T)result!;
    }
}

/// <summary>Pane actions for one document: selection and zoom through external events; corrections through <see cref="CorrectionService"/>.</summary>
internal sealed class RevitModelInteraction : IModelInteraction
{
    private readonly Document _document;
    private readonly CorrectionService _corrections;
    private readonly AsyncExternalEvent<ICollection<ElementId>> _select;
    private readonly AsyncExternalEvent<ICollection<ElementId>> _zoom;

    public RevitModelInteraction(Document document, CorrectionService corrections)
    {
        _document = document;
        _corrections = corrections;
        _select = new AsyncExternalEvent<ICollection<ElementId>>((application, ids) =>
        {
            if (RevitDeltaSource.ActiveUiDocumentFor(application, _document) is { } ui) ui.Selection.SetElementIds(ids);
        });
        _zoom = new AsyncExternalEvent<ICollection<ElementId>>((application, ids) =>
        {
            if (RevitDeltaSource.ActiveUiDocumentFor(application, _document) is { } ui) ui.ShowElements(ids);
        });
    }

    public Task SelectElementsAsync(IReadOnlyCollection<string> elementIds, CancellationToken cancellationToken = default) =>
        _select.RaiseAsync(ToIds(elementIds)).WaitAsync(cancellationToken);

    public Task ZoomToElementsAsync(IReadOnlyCollection<string> elementIds, CancellationToken cancellationToken = default) =>
        _zoom.RaiseAsync(ToIds(elementIds)).WaitAsync(cancellationToken);

    public Task<ResolutionPlan?> InspectCorrectionsAsync(string issueId, CancellationToken cancellationToken = default) =>
        _corrections.InspectAsync(issueId, cancellationToken);

    public Task<ExecutionResult> ApplyCorrectionAsync(string issueId, string candidateId, CancellationToken cancellationToken = default) =>
        _corrections.ApplyAsync(issueId, candidateId, cancellationToken);

    private ICollection<ElementId> ToIds(IEnumerable<string> ids) =>
        ids.Select(i => BarIds.TryParseElement(i, out var id) ? id : ElementId.InvalidElementId)
            .Where(id => id != ElementId.InvalidElementId && _document.GetElement(id) is not null).ToList();
}
