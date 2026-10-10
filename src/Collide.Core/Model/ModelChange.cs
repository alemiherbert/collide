namespace Collide.Model;

public enum ChangeOrigin
{
    User,

    /// <summary>A change made by this add-in's own transaction (e.g. an approved correction).</summary>
    Self,
    Unknown,
}

public enum ChangeKind
{
    Commit,
    Undo,
    Redo,
}

/// <summary>
/// Notification that host-application elements changed (Revit: <c>DocumentChanged</c>). Carries element ids only:
/// the notification is never used to read or modify the model directly.
/// </summary>
public sealed record ModelChange(
    long Sequence,
    IReadOnlyList<string> AddedElementIds,
    IReadOnlyList<string> ModifiedElementIds,
    IReadOnlyList<string> DeletedElementIds,
    IReadOnlyList<string> TransactionNames,
    ChangeOrigin Origin,
    ChangeKind Kind = ChangeKind.Commit);
