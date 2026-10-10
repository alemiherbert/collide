using Collide.Model;

namespace Collide.Monitoring;

public enum MonitoringScopeKind
{
    EntireModel,
    ActiveSelection,
    ActiveView,
    CurrentLevel,
}

/// <summary>Which part of the model live detection covers. The host adapter resolves membership (view visibility, selection, level).</summary>
public sealed record MonitoringScope(MonitoringScopeKind Kind, string? Level = null)
{
    public static MonitoringScope EntireModel { get; } = new(MonitoringScopeKind.EntireModel);
}

/// <summary>Element ids changed since the last processed batch (merged and de-duplicated).</summary>
public sealed record ChangeBatch(
    IReadOnlyCollection<string> UpsertedElementIds,
    IReadOnlyCollection<string> DeletedElementIds,
    MonitoringScope Scope,
    bool ContainsSelfChanges,
    long LastSequence);

/// <summary>
/// Reads changed elements from the host model. In Revit this marshals onto the API thread through an external event,
/// converts elements to immutable core records and returns; it must never run Revit API calls on the caller's thread.
/// </summary>
public interface IModelDeltaSource
{
    Task<ModelDelta> ExtractAsync(ChangeBatch batch, CancellationToken cancellationToken);

    Task<ReinforcementModel> ExtractFullAsync(MonitoringScope scope, CancellationToken cancellationToken);
}

public interface IDelayScheduler
{
    Task Delay(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class TaskDelayScheduler : IDelayScheduler
{
    public static TaskDelayScheduler Instance { get; } = new();
    public Task Delay(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
}

/// <summary>
/// Delta source over an <see cref="InMemoryModelStore"/> (tests, samples). Maps element ids to bars/sets/accessories
/// (by ElementId) and hosts/zones (by Id). Not a Revit implementation.
/// </summary>
public sealed class InMemoryDeltaSource(InMemoryModelStore store) : IModelDeltaSource
{
    private int _calls;

    public int ExtractCalls => _calls;
    public List<ChangeBatch> Batches { get; } = [];

    /// <summary>Throw on the next extraction (error-recovery tests).</summary>
    public Exception? FailNext { get; set; }

    /// <summary>Artificial extraction latency.</summary>
    public TimeSpan Latency { get; set; }

    public async Task<ModelDelta> ExtractAsync(ChangeBatch batch, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        lock (Batches) Batches.Add(batch);
        if (Latency > TimeSpan.Zero) await Task.Delay(Latency, cancellationToken).ConfigureAwait(false);
        if (FailNext is { } ex)
        {
            FailNext = null;
            throw ex;
        }

        var model = store.Current;
        var ids = batch.UpsertedElementIds.ToHashSet(StringComparer.Ordinal);
        return new ModelDelta
        {
            ReplacedElementIds = ids.Concat(batch.DeletedElementIds).ToArray(),
            RemovedElementIds = batch.DeletedElementIds.ToArray(),
            UpsertBars = model.Bars.Values.Where(b => ids.Contains(b.ElementId)).ToArray(),
            UpsertSets = model.Sets.Values.Where(s => ids.Contains(s.ElementId)).ToArray(),
            UpsertAccessories = model.Accessories.Values.Where(a => ids.Contains(a.ElementId)).ToArray(),
            UpsertHosts = model.Hosts.Values.Where(h => ids.Contains(h.Id)).ToArray(),
            UpsertZones = model.Zones.Values.Where(z => ids.Contains(z.Id)).ToArray(),
        };
    }

    public Task<ReinforcementModel> ExtractFullAsync(MonitoringScope scope, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        if (FailNext is { } ex)
        {
            FailNext = null;
            throw ex;
        }

        return Task.FromResult(store.Current);
    }
}
