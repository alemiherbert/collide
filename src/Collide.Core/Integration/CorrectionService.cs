using Collide.Model;
using Collide.Monitoring;
using Collide.Resolution;

namespace Collide.Integration;

/// <summary>
/// Runs work inside the host application's valid API context. Revit: an external event (the modeless pane may not
/// call the API directly). Tests: <see cref="DirectApiContext"/>.
/// </summary>
public interface IApiContext
{
    Task<T> RunAsync<T>(Func<T> action, CancellationToken cancellationToken = default);
}

public sealed class DirectApiContext : IApiContext
{
    public static DirectApiContext Instance { get; } = new();

    public Task<T> RunAsync<T>(Func<T> action, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(action());
    }
}

/// <summary>
/// Approval-driven correction workflow for the pane: plan (off the API thread, on the validated snapshot) and apply an
/// engineer-chosen candidate (inside the API context, in one host transaction, re-validated against the current model).
/// Live detection never calls this on its own.
/// </summary>
public sealed class CorrectionService
{
    private readonly IncrementalDetectionEngine _detection;
    private readonly ResolutionEngine _resolution;
    private readonly Func<IModelEditor> _editorFactory;
    private readonly IApiContext _api;
    private readonly Func<string> _approver;

    public CorrectionService(IncrementalDetectionEngine detection, ResolutionEngine resolution, Func<IModelEditor> editorFactory, IApiContext api, Func<string> approver)
    {
        _detection = detection;
        _resolution = resolution;
        _editorFactory = editorFactory;
        _api = api;
        _approver = approver;
    }

    public Task<ResolutionPlan?> InspectAsync(string issueId, CancellationToken ct = default) => Task.Run(() =>
    {
        var state = _detection.ValidatedState;
        var issue = _detection.Issues.FirstOrDefault(i => i.Id == issueId);
        return state is null || issue is null ? null : _resolution.Plan(issue, state, ct);
    }, ct);

    public Task<ExecutionResult> ApplyAsync(string issueId, string candidateId, CancellationToken ct = default)
    {
        var state = _detection.ValidatedState ?? throw new InvalidOperationException("No validated detection state is available yet.");
        var approver = _approver();
        return _api.RunAsync(() => _resolution.ApplyApproved(state, issueId, candidateId, approver, _editorFactory(), ct: ct).Result, ct);
    }
}
