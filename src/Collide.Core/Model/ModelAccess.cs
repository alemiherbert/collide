using Collide.Geometry;

namespace Collide.Model;

/// <summary>
/// Reads an immutable snapshot of the host model. Revit implementations must be called in a valid Revit API
/// context (command, external event); the returned snapshot can then be processed on any thread.
/// </summary>
public interface IReinforcementModelSource
{
    ReinforcementModel ReadSnapshot(CancellationToken cancellationToken = default);
}

/// <summary>A single permitted modification: rigid translation of one bar.</summary>
/// <param name="BarId">Bar to move.</param>
/// <param name="Translation">Translation in millimetres (world frame).</param>
/// <param name="ExpectedBeforeHash">Geometry hash the bar must have before applying (stale-state guard).</param>
/// <param name="ExpectedAfterHash">Geometry hash the bar should have after applying (verifies host behaviour).</param>
public sealed record BarModification(string BarId, Vec3 Translation, string ExpectedBeforeHash, string ExpectedAfterHash)
{
    public BarModification Inverse() => new(BarId, -Translation, ExpectedAfterHash, ExpectedBeforeHash);
}

public enum TransactionState
{
    Active,
    Committed,
    RolledBack,
}

/// <summary>
/// Opens all-or-nothing modification scopes on the host model. In Revit this wraps <c>Transaction</c>; it must be used
/// from a valid API context (an external event when initiated from the modeless pane).
/// </summary>
public interface IModelEditor
{
    IModelTransaction BeginTransaction(string name);

    /// <summary>Reads the current committed state of the given bars (null entries for bars that do not exist).</summary>
    IReadOnlyList<RebarBar?> ReadBars(IReadOnlyList<string> barIds);
}

/// <summary>
/// An open modification scope. Disposing without <see cref="Commit"/> rolls back. After a failure nothing that was
/// applied inside the scope may remain in the model.
/// </summary>
public interface IModelTransaction : IDisposable
{
    string Name { get; }
    TransactionState State { get; }

    /// <summary>Applies a modification. Throws on failure; the caller must then roll back.</summary>
    void Apply(BarModification modification);

    /// <summary>Reads bars as they currently are inside this transaction (reflecting applied modifications).</summary>
    IReadOnlyList<RebarBar?> ReadBars(IReadOnlyList<string> barIds);

    void Commit();
    void RollBack();
}
