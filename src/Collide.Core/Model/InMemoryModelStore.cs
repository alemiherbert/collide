using Collide.Geometry;

namespace Collide.Model;

/// <summary>
/// Transactional in-memory model used for tests, dry runs and examples. Transactions work on a private snapshot that
/// replaces the committed model only on <see cref="IModelTransaction.Commit"/>; a rolled-back or disposed transaction
/// leaves no trace. Supports fault injection and host-behaviour distortion to exercise rollback paths.
/// It is a test double: it does not demonstrate how Revit itself behaves.
/// </summary>
public sealed class InMemoryModelStore : IModelEditor, IReinforcementModelSource
{
    public const string SelfTransactionPrefix = "Collide:";

    private readonly object _gate = new();
    private ReinforcementModel _current;
    private long _changeSequence;

    public InMemoryModelStore(ReinforcementModel model) => _current = model;

    public ReinforcementModel Current
    {
        get
        {
            lock (_gate) return _current;
        }
    }

    /// <summary>Raised after a commit, like Revit's DocumentChanged (ids only).</summary>
    public event Action<ModelChange>? Changed;

    /// <summary>Return true to make the n-th <see cref="IModelTransaction.Apply"/> call (1-based, per transaction) throw.</summary>
    public Func<BarModification, int, bool>? FailOn { get; set; }

    /// <summary>Simulates a host that applies a different translation than requested (e.g. snapping).</summary>
    public Func<BarModification, Vec3>? ActualTranslation { get; set; }

    public int CommitCount { get; private set; }

    public ReinforcementModel ReadSnapshot(CancellationToken cancellationToken = default) => Current;

    public IReadOnlyList<RebarBar?> ReadBars(IReadOnlyList<string> barIds)
    {
        var model = Current;
        return barIds.Select(model.FindBar).ToArray();
    }

    public IModelTransaction BeginTransaction(string name) => new Transaction(this, name);

    /// <summary>Simulates a user edit (origin User), raising <see cref="Changed"/>.</summary>
    public void ApplyUserDelta(ModelDelta delta, IReadOnlyList<string>? added = null, IReadOnlyList<string>? modified = null, IReadOnlyList<string>? deleted = null)
    {
        lock (_gate) _current = _current.Apply(delta);
        Raise(added ?? [], modified ?? [], deleted ?? [], ["User edit"], ChangeOrigin.User);
    }

    private void Raise(IReadOnlyList<string> added, IReadOnlyList<string> modified, IReadOnlyList<string> deleted, IReadOnlyList<string> names, ChangeOrigin origin)
    {
        var seq = Interlocked.Increment(ref _changeSequence);
        Changed?.Invoke(new ModelChange(seq, added, modified, deleted, names, origin));
    }

    private sealed class Transaction : IModelTransaction
    {
        private readonly InMemoryModelStore _store;
        private readonly HashSet<string> _touchedElements = new(StringComparer.Ordinal);
        private ReinforcementModel _working;
        private int _applyCount;

        public Transaction(InMemoryModelStore store, string name)
        {
            _store = store;
            Name = name;
            _working = store.Current;
        }

        public string Name { get; }
        public TransactionState State { get; private set; } = TransactionState.Active;

        public void Apply(BarModification modification)
        {
            EnsureActive();
            _applyCount++;
            if (_store.FailOn?.Invoke(modification, _applyCount) == true)
                throw new InvalidOperationException($"Injected failure while applying modification {_applyCount} to bar {modification.BarId}.");
            var bar = _working.FindBar(modification.BarId) ?? throw new InvalidOperationException($"Bar {modification.BarId} does not exist.");
            var translation = _store.ActualTranslation?.Invoke(modification) ?? modification.Translation;
            _working = _working.WithBars([bar.Translate(translation)]);
            _touchedElements.Add(bar.ElementId);
        }

        public IReadOnlyList<RebarBar?> ReadBars(IReadOnlyList<string> barIds)
        {
            EnsureActive();
            return barIds.Select(_working.FindBar).ToArray();
        }

        public void Commit()
        {
            EnsureActive();
            lock (_store._gate) _store._current = _working;
            State = TransactionState.Committed;
            _store.CommitCount++;
            var origin = Name.StartsWith(SelfTransactionPrefix, StringComparison.Ordinal) ? ChangeOrigin.Self : ChangeOrigin.User;
            _store.Raise([], _touchedElements.OrderBy(e => e, StringComparer.Ordinal).ToArray(), [], [Name], origin);
        }

        public void RollBack()
        {
            if (State != TransactionState.Active) return;
            _working = _store.Current;
            State = TransactionState.RolledBack;
        }

        public void Dispose()
        {
            if (State == TransactionState.Active) RollBack();
        }

        private void EnsureActive()
        {
            if (State != TransactionState.Active) throw new InvalidOperationException($"Transaction '{Name}' is {State}.");
        }
    }
}
