using System.Diagnostics;
using Collide.Findings;
using Collide.Model;
using Serilog;

namespace Collide.Monitoring;

public enum MonitorStatus
{
    Stopped,
    Initializing,
    Idle,
    Pending,
    Extracting,
    Analyzing,
    Paused,
    Error,
}

public sealed record MonitorOptions
{
    /// <summary>Quiet period after the last change before a batch is processed.</summary>
    public TimeSpan Debounce { get; init; } = TimeSpan.FromMilliseconds(400);

    /// <summary>Upper bound on how long continuous changes can postpone processing.</summary>
    public TimeSpan MaxLatency { get; init; } = TimeSpan.FromSeconds(3);

    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>After this many consecutive failures automatic retries stop until the next change or a manual full scan.</summary>
    public int MaxConsecutiveFailures { get; init; } = 3;
}

/// <summary>The monitor surface used by UI layers (implemented by <see cref="LiveClashMonitor"/>).</summary>
public interface ILiveMonitor
{
    event EventHandler<MonitorUpdate>? Updated;
    MonitorStatus Status { get; }
    MonitoringScope Scope { get; }
    DetectionUpdate? LastUpdate { get; }
    void Pause();
    void Resume();
    void RequestFullScan();
    void SetScope(MonitoringScope scope);
}

public sealed record MonitorUpdate
{
    public required MonitorStatus Status { get; init; }
    public required MonitoringScope Scope { get; init; }

    /// <summary>True when this update carries a freshly validated result (false for status-only notifications).</summary>
    public bool IsValidatedResult { get; init; }

    public long Revision { get; init; }
    public long? ValidatedRevision { get; init; }
    public DateTimeOffset? ValidatedAt { get; init; }
    public IReadOnlyList<Issue> Issues { get; init; } = [];
    public IReadOnlyList<string> NewIssueIds { get; init; } = [];
    public IReadOnlyList<string> ResolvedIssueIds { get; init; } = [];
    public IReadOnlyList<string> ChangedIssueIds { get; init; } = [];
    public IReadOnlyList<string> StaleIssueIds { get; init; } = [];
    public bool IncludesSelfChanges { get; init; }
    public bool FullScan { get; init; }
    public int PendingElementCount { get; init; }
    public string? Error { get; init; }
    public double AnalysisMilliseconds { get; init; }

    public int CriticalCount => Issues.Count(i => i.Severity == Severity.Critical && i.Status != IssueStatus.Indeterminate);
    public int HighCount => Issues.Count(i => i.Severity == Severity.High && i.Status != IssueStatus.Indeterminate);
    public int OtherWarningCount => Issues.Count - CriticalCount - HighCount;
}

/// <summary>
/// Event-driven live clash monitoring. Change notifications only record element ids (cheap, safe inside Revit's
/// read-only DocumentChanged handler); after a debounce the batch is extracted through <see cref="IModelDeltaSource"/>
/// (the Revit implementation marshals onto the API thread via an external event), analysed incrementally on a
/// background thread, and published. Newer changes cancel obsolete computations; results for superseded revisions are
/// discarded. The monitor never modifies the model: corrections are a separate, approval-driven workflow.
/// </summary>
public sealed class LiveClashMonitor : ILiveMonitor, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly IncrementalDetectionEngine _engine;
    private readonly IModelDeltaSource _source;
    private readonly MonitorOptions _options;
    private readonly IDelayScheduler _scheduler;
    private readonly ILogger _log;
    private readonly SortedSet<string> _upserts = new(StringComparer.Ordinal);
    private readonly SortedSet<string> _deletes = new(StringComparer.Ordinal);
    private readonly Queue<long> _recentSequences = new();
    private readonly HashSet<long> _seenSequences = [];
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _computeCts;
    private TaskCompletionSource _idle = NewIdle(completed: false);
    private Task? _loop;
    private bool _pendingSelf;
    private bool _paused;
    private bool _fullScanRequested;
    private bool _haltedAfterFailures;
    private long _generation;
    private long _lastSequence;
    private int _failures;
    private MonitorStatus _status = MonitorStatus.Stopped;
    private MonitoringScope _scope = MonitoringScope.EntireModel;
    private DetectionUpdate? _last;
    private string? _lastError;

    public LiveClashMonitor(IncrementalDetectionEngine engine, IModelDeltaSource source, MonitorOptions? options = null,
        IDelayScheduler? scheduler = null, ILogger? logger = null)
    {
        _engine = engine;
        _source = source;
        _options = options ?? new MonitorOptions();
        _scheduler = scheduler ?? TaskDelayScheduler.Instance;
        _log = (logger ?? Serilog.Core.Logger.None).ForContext<LiveClashMonitor>();
    }

    /// <summary>Raised on a background thread; UI subscribers must marshal to their dispatcher.</summary>
    public event EventHandler<MonitorUpdate>? Updated;

    public MonitorStatus Status
    {
        get { lock (_gate) return _status; }
    }

    public MonitoringScope Scope
    {
        get { lock (_gate) return _scope; }
    }

    public DetectionUpdate? LastUpdate
    {
        get { lock (_gate) return _last; }
    }

    public string? LastError
    {
        get { lock (_gate) return _lastError; }
    }

    public Task StartAsync()
    {
        lock (_gate)
        {
            if (_loop is not null) return Task.CompletedTask;
            _fullScanRequested = true;
            _status = MonitorStatus.Initializing;
            ResetIdle();
            _loop = Task.Run(LoopAsync);
        }

        _signal.Release();
        return Task.CompletedTask;
    }

    /// <summary>Records a change notification. Thread-safe, non-blocking; never reads or modifies the model.</summary>
    public void NotifyChanged(ModelChange change)
    {
        IReadOnlyList<string> stale;
        lock (_gate)
        {
            if (change.Sequence > 0)
            {
                if (!_seenSequences.Add(change.Sequence)) return; // duplicate notification
                _recentSequences.Enqueue(change.Sequence);
                while (_recentSequences.Count > 4096) _seenSequences.Remove(_recentSequences.Dequeue());
                _lastSequence = Math.Max(_lastSequence, change.Sequence);
            }

            foreach (var id in change.AddedElementIds.Concat(change.ModifiedElementIds))
            {
                _deletes.Remove(id);
                _upserts.Add(id);
            }

            foreach (var id in change.DeletedElementIds)
            {
                _upserts.Remove(id);
                _deletes.Add(id);
            }

            _pendingSelf |= change.Origin == ChangeOrigin.Self;
            _generation++;
            _haltedAfterFailures = false;
            _computeCts?.Cancel(); // a newer change supersedes any computation in flight
            ResetIdle();
            if (_status is MonitorStatus.Idle or MonitorStatus.Error) _status = MonitorStatus.Pending;
            stale = StaleByElement(_upserts.Concat(_deletes));
        }

        Publish(MonitorStatusSnapshot(stale));
        _signal.Release();
    }

    public void Pause()
    {
        lock (_gate)
        {
            _paused = true;
            _status = MonitorStatus.Paused;
        }

        Publish(MonitorStatusSnapshot([]));
    }

    public void Resume()
    {
        lock (_gate)
        {
            _paused = false;
            _haltedAfterFailures = false;
            ResetIdle();
        }

        _signal.Release();
    }

    /// <summary>Requests a complete re-extraction and analysis (also clears an error halt).</summary>
    public void RequestFullScan()
    {
        lock (_gate)
        {
            _fullScanRequested = true;
            _haltedAfterFailures = false;
            _computeCts?.Cancel();
            ResetIdle();
        }

        _signal.Release();
    }

    public void SetScope(MonitoringScope scope)
    {
        lock (_gate) _scope = scope;
        RequestFullScan();
    }

    /// <summary>Completes when no work is pending (or the monitor is paused/halted). For tests and orderly shutdown.</summary>
    public Task WhenIdleAsync()
    {
        lock (_gate) return _idle.Task;
    }

    private async Task LoopAsync()
    {
        var ct = _lifetime.Token;
        while (!ct.IsCancellationRequested)
        {
            bool full, hasChanges, paused, halted;
            lock (_gate)
            {
                full = _fullScanRequested;
                hasChanges = _upserts.Count + _deletes.Count > 0;
                paused = _paused;
                halted = _haltedAfterFailures;
                if ((!full && !hasChanges) || (paused && !full) || halted)
                {
                    if (!paused) _status = halted ? MonitorStatus.Error : MonitorStatus.Idle;
                    _idle.TrySetResult();
                }
            }

            if ((!full && !hasChanges) || (paused && !full) || halted)
            {
                try
                {
                    await _signal.WaitAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                continue;
            }

            ChangeBatch? batch = null;
            try
            {
                if (full)
                {
                    await FullScanAsync(ct).ConfigureAwait(false);
                }
                else
                {
                    await DebounceAsync(ct).ConfigureAwait(false);
                    batch = TakeBatch();
                    if (batch is null) continue;
                    await ProcessBatchAsync(batch, ct).ConfigureAwait(false);
                }

                lock (_gate) _failures = 0;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Superseded by a newer change or a full-scan request: the engine keeps the pending scope.
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                await HandleFailureAsync(ex, batch, ct).ConfigureAwait(false);
            }
        }

        lock (_gate)
        {
            _status = MonitorStatus.Stopped;
            _idle.TrySetResult();
        }
    }

    private async Task FullScanAsync(CancellationToken ct)
    {
        MonitoringScope scope;
        lock (_gate)
        {
            _fullScanRequested = false;
            _status = MonitorStatus.Extracting;
            // A full scan supersedes element-level changes collected so far.
            _upserts.Clear();
            _deletes.Clear();
            _pendingSelf = false;
            scope = _scope;
        }

        var model = await _source.ExtractFullAsync(scope, ct).ConfigureAwait(false);
        var staged = _engine.StageFull(model, ct);
        await ComputeAndCommitAsync(staged, ct).ConfigureAwait(false);
    }

    private async Task ProcessBatchAsync(ChangeBatch batch, CancellationToken ct)
    {
        lock (_gate) _status = MonitorStatus.Extracting;
        var delta = await _source.ExtractAsync(batch, ct).ConfigureAwait(false);
        var staged = _engine.Stage(delta, batch.ContainsSelfChanges, ct);
        if (staged is null)
        {
            lock (_gate) _fullScanRequested = true;
            return;
        }

        Publish(MonitorStatusSnapshot(staged.StaleIssueIds, MonitorStatus.Analyzing));
        await ComputeAndCommitAsync(staged, ct).ConfigureAwait(false);
    }

    private async Task ComputeAndCommitAsync(StagedUpdate staged, CancellationToken ct)
    {
        CancellationTokenSource cts;
        lock (_gate)
        {
            _status = MonitorStatus.Analyzing;
            cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _computeCts = cts;
        }

        try
        {
            var sw = Stopwatch.StartNew();
            var computed = await Task.Run(() => _engine.Compute(staged, cts.Token), cts.Token).ConfigureAwait(false);
            var update = _engine.Commit(computed);
            if (update is null)
            {
                _log.Debug("Discarded obsolete result for revision {Revision}", staged.Revision);
                return;
            }

            MonitorUpdate published;
            lock (_gate)
            {
                _last = update;
                _lastError = null;
                published = new MonitorUpdate
                {
                    Status = _upserts.Count + _deletes.Count > 0 ? MonitorStatus.Pending : _paused ? MonitorStatus.Paused : MonitorStatus.Idle,
                    Scope = _scope,
                    IsValidatedResult = true,
                    Revision = update.Revision,
                    ValidatedRevision = update.Revision,
                    ValidatedAt = update.ValidatedAt,
                    Issues = update.Issues,
                    NewIssueIds = update.NewIssueIds,
                    ResolvedIssueIds = update.ResolvedIssueIds,
                    ChangedIssueIds = update.ChangedIssueIds,
                    StaleIssueIds = StaleByElement(_upserts.Concat(_deletes)),
                    IncludesSelfChanges = update.IncludesSelfChanges,
                    FullScan = update.FullScan,
                    PendingElementCount = _upserts.Count + _deletes.Count,
                    AnalysisMilliseconds = sw.Elapsed.TotalMilliseconds,
                };
            }

            _log.Debug("Validated revision {Revision}: {Issues} issues (+{New} / -{Resolved}) in {Ms:0.0} ms",
                update.Revision, update.Issues.Count, update.NewIssueIds.Count, update.ResolvedIssueIds.Count, published.AnalysisMilliseconds);
            Publish(published);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_computeCts, cts)) _computeCts = null;
            }

            cts.Dispose();
        }
    }

    private async Task DebounceAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            long generation;
            lock (_gate)
            {
                generation = _generation;
                _status = MonitorStatus.Pending;
            }

            await _scheduler.Delay(_options.Debounce, ct).ConfigureAwait(false);
            lock (_gate)
            {
                if (generation == _generation || sw.Elapsed >= _options.MaxLatency || _fullScanRequested) return;
            }
        }
    }

    private ChangeBatch? TakeBatch()
    {
        lock (_gate)
        {
            if (_upserts.Count + _deletes.Count == 0) return null;
            var batch = new ChangeBatch(_upserts.ToArray(), _deletes.ToArray(), _scope, _pendingSelf, _lastSequence);
            _upserts.Clear();
            _deletes.Clear();
            _pendingSelf = false;
            return batch;
        }
    }

    private async Task HandleFailureAsync(Exception ex, ChangeBatch? batch, CancellationToken ct)
    {
        bool halt;
        lock (_gate)
        {
            _failures++;
            _lastError = $"{ex.GetType().Name}: {ex.Message}";
            _status = MonitorStatus.Error;
            if (batch is not null)
            {
                // Re-queue the failed batch without overriding newer notifications for the same elements.
                foreach (var id in batch.UpsertedElementIds) if (!_deletes.Contains(id)) _upserts.Add(id);
                foreach (var id in batch.DeletedElementIds) if (!_upserts.Contains(id)) _deletes.Add(id);
                _pendingSelf |= batch.ContainsSelfChanges;
            }
            else if (_engine.ValidatedState is null)
            {
                _fullScanRequested = true;
            }

            halt = _failures >= _options.MaxConsecutiveFailures;
            _haltedAfterFailures = halt;
        }

        _log.Warning(ex, "Live clash detection failed ({Failures} consecutive)", _failures);
        Publish(MonitorStatusSnapshot([], MonitorStatus.Error));
        if (!halt)
        {
            try
            {
                await _scheduler.Delay(_options.RetryDelay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // shutting down
            }
        }
    }

    private IReadOnlyList<string> StaleByElement(IEnumerable<string> elementIds)
    {
        var ids = elementIds.ToHashSet(StringComparer.Ordinal);
        if (ids.Count == 0 || _last is null) return [];
        return _last.Issues.Where(i => i.ElementIds.Any(ids.Contains)).Select(i => i.Id).ToArray();
    }

    private MonitorUpdate MonitorStatusSnapshot(IReadOnlyList<string> stale, MonitorStatus? status = null)
    {
        lock (_gate)
        {
            return new MonitorUpdate
            {
                Status = status ?? _status,
                Scope = _scope,
                Revision = _engine.Revision,
                ValidatedRevision = _last?.Revision,
                ValidatedAt = _last?.ValidatedAt,
                Issues = _last?.Issues ?? [],
                StaleIssueIds = stale,
                PendingElementCount = _upserts.Count + _deletes.Count,
                Error = _status == MonitorStatus.Error || status == MonitorStatus.Error ? _lastError : null,
            };
        }
    }

    private void Publish(MonitorUpdate update)
    {
        try
        {
            Updated?.Invoke(this, update);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "A monitor subscriber threw");
        }
    }

    private void ResetIdle()
    {
        if (_idle.Task.IsCompleted) _idle = NewIdle(completed: false);
    }

    private static TaskCompletionSource NewIdle(bool completed)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (completed) tcs.SetResult();
        return tcs;
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        _signal.Release();
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // expected
            }
        }

        _lifetime.Dispose();
        _signal.Dispose();
    }
}
