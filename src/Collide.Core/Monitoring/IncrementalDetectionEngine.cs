using Collide.Analysis;
using Collide.Audit;
using Collide.Classification;
using Collide.Findings;
using Collide.Model;
using Collide.Rules;

namespace Collide.Monitoring;

/// <summary>A staged change: the model it produced and the accumulated scope still awaiting validation.</summary>
public sealed record StagedUpdate(long Revision, PreparedModel Model, AnalysisScope Scope, IReadOnlyList<string> StaleIssueIds, bool IncludesSelfChanges);

public sealed record ComputedUpdate(StagedUpdate Staged, AnalysisResult Result);

public sealed record DetectionUpdate
{
    public required long Revision { get; init; }
    public required long ModelRevision { get; init; }
    public required DateTimeOffset ValidatedAt { get; init; }
    public required IReadOnlyList<Issue> Issues { get; init; }
    public IReadOnlyList<string> NewIssueIds { get; init; } = [];
    public IReadOnlyList<string> ResolvedIssueIds { get; init; } = [];
    public IReadOnlyList<string> ChangedIssueIds { get; init; } = [];
    public bool IncludesSelfChanges { get; init; }
    public bool FullScan { get; init; }
    public int BarsAnalysed { get; init; }
    public double AnalysisMilliseconds { get; init; }
}

/// <summary>
/// Thread-safe incremental detection. Changes are <see cref="Stage"/>d (cheap, under lock), the affected scope is
/// <see cref="Compute"/>d on an immutable snapshot (any thread, cancellable), and <see cref="Commit"/> accepts the result
/// only if no newer change was staged meanwhile — obsolete or out-of-order results are discarded and their scope stays
/// pending, so nothing is lost. Committed results equal a full re-analysis of the latest model.
/// </summary>
public sealed class IncrementalDetectionEngine
{
    private readonly object _gate = new();
    private readonly ModelAnalyzer _analyzer;
    private readonly DefectClassifier _classifier = new();
    private readonly RuleSet _rules;
    private readonly IClock _clock;
    private DetectionState? _validated;
    private IReadOnlyList<Issue> _issues = [];
    private PreparedModel? _latest;
    private AnalysisScope _pending = new();
    private bool _pendingSelf;
    private long _revision;
    private long _committedRevision;

    public IncrementalDetectionEngine(ModelAnalyzer analyzer, RuleSet rules, IClock? clock = null)
    {
        _analyzer = analyzer;
        _rules = rules;
        _clock = clock ?? SystemClock.Instance;
    }

    public RuleSet Rules => _rules;

    public long Revision
    {
        get { lock (_gate) return _revision; }
    }

    public DetectionState? ValidatedState
    {
        get { lock (_gate) return _validated; }
    }

    public IReadOnlyList<Issue> Issues
    {
        get { lock (_gate) return _issues; }
    }

    public PreparedModel? LatestModel
    {
        get { lock (_gate) return _latest; }
    }

    /// <summary>Starts over from a complete snapshot (initial load, manual full scan or scope change).</summary>
    public StagedUpdate StageFull(ReinforcementModel model, CancellationToken ct = default)
    {
        var prepared = _analyzer.Prepare(model, _rules, ct);
        lock (_gate)
        {
            _latest = prepared;
            _pending = AnalysisScope.All;
            _pendingSelf = false;
            _revision++;
            return new StagedUpdate(_revision, prepared, AnalysisScope.All, _issues.Select(i => i.Id).ToArray(), false);
        }
    }

    /// <summary>Applies a delta to the latest model and widens the pending scope. Returns null if nothing is loaded yet.</summary>
    public StagedUpdate? Stage(ModelDelta delta, bool selfChange = false, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_latest is null) return null;
            var before = _latest;
            var after = before.Apply(delta, ct);

            var touchedElements = new HashSet<string>(delta.ReplacedElementIds.Concat(delta.RemovedElementIds), StringComparer.Ordinal);
            var changedBars = new HashSet<string>(delta.UpsertBars.Select(b => b.Id).Concat(delta.RemovedBarIds), StringComparer.Ordinal);
            foreach (var pm in new[] { before, after })
            {
                changedBars.UnionWith(pm.Model.Bars.Values.Where(b => touchedElements.Contains(b.ElementId)).Select(b => b.Id));
                // Bars whose accessories or zones changed must be re-checked as well.
                foreach (var acc in delta.UpsertAccessories.Concat(pm.Model.Accessories.Values.Where(a => touchedElements.Contains(a.ElementId))))
                    changedBars.UnionWith(pm.BarIndex.Query(Geometry.Aabb.FromPoints(acc.AxisStart, acc.AxisEnd).Inflate(acc.OuterDiameter.Millimetres)));
                foreach (var zone in delta.UpsertZones.Concat(pm.Model.Zones.Values.Where(z => touchedElements.Contains(z.Id))))
                    changedBars.UnionWith(pm.BarIndex.Query(zone.Box.Bounds));
            }

            var changedHosts = delta.UpsertHosts.Select(h => h.Id)
                .Concat(delta.RemovedElementIds.Where(before.Model.Hosts.ContainsKey)).Distinct(StringComparer.Ordinal).ToArray();
            var scope = AnalysisScope.ForChanges(before, after, changedBars, changedHosts);
            _pending = _pending.Union(scope);
            _pendingSelf |= selfChange;
            _latest = after;
            _revision++;
            var stale = _validated is null ? [] : StaleIssues(_pending);
            return new StagedUpdate(_revision, after, _pending, stale, _pendingSelf);
        }
    }

    /// <summary>Pure computation on the staged snapshot. Safe on any thread; honours cancellation.</summary>
    public ComputedUpdate Compute(StagedUpdate staged, CancellationToken ct = default) =>
        new(staged, _analyzer.Analyze(staged.Model, staged.Scope, ct));

    /// <summary>Accepts a computed result if it is still current; otherwise returns null (obsolete).</summary>
    public DetectionUpdate? Commit(ComputedUpdate computed)
    {
        lock (_gate)
        {
            if (computed.Staged.Revision != _revision || computed.Staged.Revision <= _committedRevision) return null;
            var full = computed.Staged.Scope.IsAll;
            if (!full && _validated is null) return null;
            var state = full
                ? DetectionState.FromFullResult(computed.Staged.Model, computed.Result)
                : _validated!.Merge(computed.Staged.Model, computed.Staged.Scope, computed.Result);
            var issues = _classifier.Classify(state.Findings, state.Prepared.Model, _rules);
            var before = _issues.ToDictionary(i => i.Id, StringComparer.Ordinal);
            var after = issues.ToDictionary(i => i.Id, StringComparer.Ordinal);
            var update = new DetectionUpdate
            {
                Revision = _revision,
                ModelRevision = state.Prepared.Model.Revision,
                ValidatedAt = _clock.UtcNow,
                Issues = issues,
                NewIssueIds = after.Keys.Where(k => !before.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal).ToArray(),
                ResolvedIssueIds = before.Keys.Where(k => !after.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal).ToArray(),
                ChangedIssueIds = after.Keys.Where(k => before.TryGetValue(k, out var old) && !SameMeasurements(old, after[k])).OrderBy(k => k, StringComparer.Ordinal).ToArray(),
                IncludesSelfChanges = computed.Staged.IncludesSelfChanges,
                FullScan = full,
                BarsAnalysed = computed.Result.Statistics.BarsAnalysed,
                AnalysisMilliseconds = computed.Result.Statistics.ElapsedMilliseconds,
            };
            _validated = state;
            _issues = issues;
            _pending = new AnalysisScope();
            _pendingSelf = false;
            _committedRevision = computed.Staged.Revision;
            return update;
        }
    }

    /// <summary>Convenience: stage, compute and commit synchronously.</summary>
    public DetectionUpdate? ApplyNow(ModelDelta delta, bool selfChange = false, CancellationToken ct = default)
    {
        var staged = Stage(delta, selfChange, ct);
        return staged is null ? null : Commit(Compute(staged, ct));
    }

    public DetectionUpdate? InitializeNow(ReinforcementModel model, CancellationToken ct = default) => Commit(Compute(StageFull(model, ct), ct));

    private IReadOnlyList<string> StaleIssues(AnalysisScope scope)
    {
        var staleFingerprints = _validated!.Findings.Where(scope.Owns).Select(f => f.Fingerprint).ToHashSet(StringComparer.Ordinal);
        return _issues.Where(i => i.FindingFingerprints.Any(staleFingerprints.Contains)).Select(i => i.Id).ToArray();
    }

    private static bool SameMeasurements(Issue a, Issue b) =>
        a.Measurements.Count == b.Measurements.Count && a.Measurements.Zip(b.Measurements).All(p => p.First == p.Second) && a.Severity == b.Severity;
}
