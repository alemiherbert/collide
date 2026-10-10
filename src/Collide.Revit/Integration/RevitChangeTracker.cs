using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Collide.Model;
using Collide.Resolution;
using ModelChange = Collide.Model.ModelChange;

namespace Collide.Revit.Integration;

/// <summary>
/// Turns <c>DocumentChanged</c> notifications into id-only <see cref="ModelChange"/>s. The handler runs inside Revit's
/// read-only event: it only reads element categories (cheap, permitted) and never modifies the document or extracts
/// geometry; extraction happens later through an external event after the monitor's debounce.
/// </summary>
internal sealed class RevitChangeTracker
{
    private static readonly HashSet<BuiltInCategory> Relevant =
    [
        BuiltInCategory.OST_Rebar, BuiltInCategory.OST_AreaRein, BuiltInCategory.OST_PathRein, BuiltInCategory.OST_Coupler,
        BuiltInCategory.OST_StructuralFraming, BuiltInCategory.OST_StructuralColumns, BuiltInCategory.OST_Floors,
        BuiltInCategory.OST_Walls, BuiltInCategory.OST_StructuralFoundation,
        BuiltInCategory.OST_FloorOpening, BuiltInCategory.OST_ShaftOpening, BuiltInCategory.OST_SWallRectOpening,
        BuiltInCategory.OST_StructuralFramingOpening, BuiltInCategory.OST_ColumnOpening, BuiltInCategory.OST_Doors, BuiltInCategory.OST_Windows,
    ];

    private readonly Func<Document, Action<ModelChange>?> _sinkFor;
    private long _sequence;

    public RevitChangeTracker(Func<Document, Action<ModelChange>?> sinkFor) => _sinkFor = sinkFor;

    public void OnDocumentChanged(object? sender, DocumentChangedEventArgs e)
    {
        var doc = e.GetDocument();
        var sink = _sinkFor(doc);
        if (sink is null) return;

        var added = RelevantIds(doc, e.GetAddedElementIds());
        var modified = RelevantIds(doc, e.GetModifiedElementIds());
        var deleted = e.GetDeletedElementIds().Select(BarIds.Element).ToArray();
        if (added.Count == 0 && modified.Count == 0 && deleted.Length == 0) return;

        var names = e.GetTransactionNames();
        var origin = names.Any(n => n.StartsWith(ResolutionExecutor.TransactionPrefix, StringComparison.Ordinal)) ? ChangeOrigin.Self : ChangeOrigin.User;
        var kind = e.Operation switch
        {
            UndoOperation.TransactionUndone => ChangeKind.Undo,
            UndoOperation.TransactionRedone => ChangeKind.Redo,
            _ => ChangeKind.Commit,
        };
        sink(new ModelChange(Interlocked.Increment(ref _sequence), added, modified, deleted, names.ToArray(), origin, kind));
    }

    private static IReadOnlyList<string> RelevantIds(Document doc, ICollection<ElementId> ids)
    {
        var result = new List<string>();
        foreach (var id in ids)
        {
            var element = doc.GetElement(id);
            if (element?.Category?.BuiltInCategory is not { } category || !Relevant.Contains(category)) continue;
            result.Add(BarIds.Element(id));
            // An opening or insert changes its host's concrete: report the host too.
            if (element is FamilyInstance { Host: { } host } && category is BuiltInCategory.OST_Doors or BuiltInCategory.OST_Windows)
                result.Add(BarIds.Element(host.Id));
            if (element is Opening { Host: { } openingHost }) result.Add(BarIds.Element(openingHost.Id));
        }

        return result;
    }
}
