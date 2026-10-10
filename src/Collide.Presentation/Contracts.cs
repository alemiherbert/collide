using Collide.Resolution;

namespace Collide.Presentation;

/// <summary>Marshals work to the UI thread (WPF: Dispatcher). Tests use <see cref="ImmediateDispatcher"/>.</summary>
public interface IUiDispatcher
{
    void Post(Action action);
}

public sealed class ImmediateDispatcher : IUiDispatcher
{
    public static ImmediateDispatcher Instance { get; } = new();
    public void Post(Action action) => action();
}

/// <summary>
/// Model actions requested from the pane. The Revit implementation raises external events so every Revit API call
/// happens in a valid API context; nothing here blocks the UI thread.
/// </summary>
public interface IModelInteraction
{
    Task SelectElementsAsync(IReadOnlyCollection<string> elementIds, CancellationToken cancellationToken = default);
    Task ZoomToElementsAsync(IReadOnlyCollection<string> elementIds, CancellationToken cancellationToken = default);
    Task<ResolutionPlan?> InspectCorrectionsAsync(string issueId, CancellationToken cancellationToken = default);
    Task<ExecutionResult> ApplyCorrectionAsync(string issueId, string candidateId, CancellationToken cancellationToken = default);
}
