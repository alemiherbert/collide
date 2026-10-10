using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Collide.Model;
using Serilog;
using RevitElementId = Autodesk.Revit.DB.ElementId;

namespace Collide.Revit.Integration;

/// <summary>
/// <see cref="IModelEditor"/> over a Revit document. MUST be used inside a valid Revit API context.
/// Single bars are moved with <c>ElementTransformUtils.MoveElement</c>; individual bars of a set with
/// <c>Rebar.MoveBarInSet</c> (which concatenates with any existing move, per the Revit API documentation).
/// Bars of area/path reinforcement are refused. Commit rolls back on any Revit failure (errors and, conservatively, warnings).
/// </summary>
internal sealed class RevitModelEditor(Document document, ILogger log) : IModelEditor
{
    private readonly RevitModelReader _reader = new(document, log);

    public IModelTransaction BeginTransaction(string name) => new RevitModelTransaction(document, name, _reader, log);

    public IReadOnlyList<RebarBar?> ReadBars(IReadOnlyList<string> barIds) => _reader.ReadBars(barIds);

    private sealed class RevitModelTransaction : IModelTransaction
    {
        private readonly Document _doc;
        private readonly RevitModelReader _reader;
        private readonly ILogger _log;
        private readonly Transaction _tx;

        public RevitModelTransaction(Document doc, string name, RevitModelReader reader, ILogger log)
        {
            _doc = doc;
            _reader = reader;
            _log = log;
            Name = name;
            _tx = new Transaction(doc, name);
            if (_tx.Start() != TransactionStatus.Started) throw new InvalidOperationException($"Revit refused to start transaction '{name}'.");
        }

        public string Name { get; }

        public TransactionState State => _tx.GetStatus() switch
        {
            TransactionStatus.Started => TransactionState.Active,
            TransactionStatus.Committed => TransactionState.Committed,
            _ => TransactionState.RolledBack,
        };

        public void Apply(BarModification modification)
        {
            if (!BarIds.TryParse(modification.BarId, out var elementId, out var position))
                throw new ArgumentException($"Invalid bar id '{modification.BarId}'.");
            var element = _doc.GetElement(new RevitElementId(elementId));
            var vector = RevitUnits.ToFeet(modification.Translation);
            switch (element)
            {
                case Rebar rebar when rebar.NumberOfBarPositions == 1:
                    ElementTransformUtils.MoveElement(_doc, rebar.Id, vector);
                    break;
                case Rebar rebar:
                    rebar.MoveBarInSet(position, Transform.CreateTranslation(vector));
                    break;
                case RebarInSystem:
                    throw new NotSupportedException($"Bar {modification.BarId} belongs to area/path reinforcement and cannot be moved individually.");
                default:
                    throw new InvalidOperationException($"Element {elementId} is not a rebar element.");
            }

            _doc.Regenerate();
        }

        public IReadOnlyList<RebarBar?> ReadBars(IReadOnlyList<string> barIds) => _reader.ReadBars(barIds);

        public void Commit()
        {
            var preprocessor = new RejectFailures();
            var options = _tx.GetFailureHandlingOptions();
            options.SetFailuresPreprocessor(preprocessor);
            options.SetClearAfterRollback(true);
            var status = _tx.Commit(options);
            if (status != TransactionStatus.Committed)
                throw new InvalidOperationException($"Revit did not commit '{Name}' ({status}): {string.Join("; ", preprocessor.Messages)}");
        }

        public void RollBack()
        {
            if (_tx.GetStatus() == TransactionStatus.Started) _tx.RollBack();
        }

        public void Dispose()
        {
            if (_tx.GetStatus() == TransactionStatus.Started)
            {
                _log.Warning("Transaction {Name} disposed without commit; rolling back", Name);
                _tx.RollBack();
            }

            _tx.Dispose();
        }
    }

    /// <summary>Any warning or error raised while committing a correction aborts it (the engine already validated the change).</summary>
    private sealed class RejectFailures : IFailuresPreprocessor
    {
        public List<string> Messages { get; } = [];

        public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
        {
            var failures = failuresAccessor.GetFailureMessages();
            if (failures.Count == 0) return FailureProcessingResult.Continue;
            Messages.AddRange(failures.Select(f => $"{f.GetSeverity()}: {f.GetDescriptionText()}"));
            return FailureProcessingResult.ProceedWithRollBack;
        }
    }
}
