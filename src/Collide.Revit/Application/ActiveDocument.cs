using Collide.Monitoring;
using Collide.Presentation;
using Collide.Resolution;

namespace Collide.Revit.Application;

/// <summary>
/// The pane is application-wide; this forwards it to the monitor of the active document and re-publishes that
/// monitor's latest state when the user switches documents.
/// </summary>
internal sealed class ActiveDocumentMonitor : ILiveMonitor
{
    private readonly object _gate = new();
    private CollideSession? _session;

    public event EventHandler<MonitorUpdate>? Updated;

    public CollideSession? Session
    {
        get { lock (_gate) return _session; }
    }

    public MonitorStatus Status => Session?.Monitor.Status ?? MonitorStatus.Stopped;
    public MonitoringScope Scope => Session?.Monitor.Scope ?? MonitoringScope.EntireModel;
    public DetectionUpdate? LastUpdate => Session?.Monitor.LastUpdate;

    public void Switch(CollideSession? session)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_session, session)) return;
            if (_session is not null) _session.Monitor.Updated -= Forward;
            _session = session;
            if (_session is not null) _session.Monitor.Updated += Forward;
        }

        var last = session?.Monitor.LastUpdate;
        Updated?.Invoke(this, new MonitorUpdate
        {
            Status = session?.Monitor.Status ?? MonitorStatus.Stopped,
            Scope = session?.Monitor.Scope ?? MonitoringScope.EntireModel,
            IsValidatedResult = true,
            FullScan = true,
            Revision = last?.Revision ?? 0,
            ValidatedRevision = last?.Revision,
            ValidatedAt = last?.ValidatedAt,
            Issues = last?.Issues ?? [],
        });
    }

    private void Forward(object? sender, MonitorUpdate update) => Updated?.Invoke(this, update);

    public void Pause() => Session?.Monitor.Pause();
    public void Resume() => Session?.Monitor.Resume();
    public void RequestFullScan() => Session?.Monitor.RequestFullScan();
    public void SetScope(MonitoringScope scope) => Session?.Monitor.SetScope(scope);
}

internal sealed class ActiveDocumentInteraction(ActiveDocumentMonitor active) : IModelInteraction
{
    private IModelInteraction Current => active.Session?.Interaction ?? throw new InvalidOperationException("No project document is active.");

    public Task SelectElementsAsync(IReadOnlyCollection<string> elementIds, CancellationToken cancellationToken = default) => Current.SelectElementsAsync(elementIds, cancellationToken);
    public Task ZoomToElementsAsync(IReadOnlyCollection<string> elementIds, CancellationToken cancellationToken = default) => Current.ZoomToElementsAsync(elementIds, cancellationToken);
    public Task<ResolutionPlan?> InspectCorrectionsAsync(string issueId, CancellationToken cancellationToken = default) => Current.InspectCorrectionsAsync(issueId, cancellationToken);
    public Task<ExecutionResult> ApplyCorrectionAsync(string issueId, string candidateId, CancellationToken cancellationToken = default) => Current.ApplyCorrectionAsync(issueId, candidateId, cancellationToken);
}
