using System.Collections.Concurrent;
using System.Globalization;
using Collide.Analysis;
using Collide.Audit;
using Collide.Findings;
using Collide.Geometry;
using Collide.Model;

namespace Collide.Resolution;

/// <summary>
/// Applies a validated candidate inside one host transaction: checks preconditions, applies the translations, reads the
/// bars back, verifies that the host produced the expected geometry and left neighbours untouched, re-validates every
/// invariant on the actual state, and only then commits. Any failure rolls back and the rollback is verified.
/// Re-applying the same correction is detected and skipped (idempotent).
/// </summary>
public sealed class ResolutionExecutor
{
    public const string TransactionPrefix = InMemoryModelStore.SelfTransactionPrefix;

    private readonly ResolutionValidator _validator;
    private readonly AuditLog _audit;
    private readonly IClock _clock;
    private readonly ConcurrentDictionary<string, CorrectionRecord> _ledger = new(StringComparer.Ordinal);
    private int _executing;

    public ResolutionExecutor(ResolutionValidator validator, AuditLog audit, IClock? clock = null)
    {
        _validator = validator;
        _audit = audit;
        _clock = clock ?? SystemClock.Instance;
    }

    /// <summary>Corrections committed by this executor, keyed by correction id.</summary>
    public IReadOnlyDictionary<string, CorrectionRecord> Ledger => _ledger;

    /// <summary>True while a correction is being applied (used to recognise the add-in's own model changes).</summary>
    public bool IsExecuting => Volatile.Read(ref _executing) > 0;

    public static string CorrectionIdFor(CandidateCorrection c) =>
        "COR-" + Finding.ShortHash(c.IssueId + "|" + string.Join(";", c.Modifications.Select(m =>
            string.Create(CultureInfo.InvariantCulture, $"{m.BarId}:{m.ExpectedBeforeHash}>{m.ExpectedAfterHash}"))));

    public ExecutionResult Execute(CandidateEvaluation evaluation, Finding target, PreparedModel current, IModelEditor editor, string runId,
        string? approvedBy = null, CancellationToken ct = default)
    {
        var candidate = evaluation.Candidate;
        var correctionId = CorrectionIdFor(candidate);
        var barIds = candidate.Modifications.Select(m => m.BarId).ToArray();
        if (_ledger.TryGetValue(correctionId, out var existing) && existing.Status == ExecutionStatus.Committed)
        {
            var now = editor.ReadBars(barIds);
            if (now.Zip(candidate.Modifications).All(p => p.First?.GeometryHash == p.Second.ExpectedAfterHash))
            {
                _audit.Record(runId, AuditAction.AlreadyApplied, "Correction already applied; nothing to do.", candidate.IssueId, candidate.Id, correctionId, barIds);
                return new ExecutionResult { Status = ExecutionStatus.AlreadyApplied, Reason = "Correction already applied.", Correction = existing };
            }
        }

        if (Interlocked.Increment(ref _executing) > 1)
        {
            Interlocked.Decrement(ref _executing);
            throw new InvalidOperationException("Re-entrant correction execution is not allowed.");
        }

        try
        {
            return ExecuteCore(evaluation, target, current, editor, runId, approvedBy, correctionId, barIds, ct);
        }
        finally
        {
            Interlocked.Decrement(ref _executing);
        }
    }

    private ExecutionResult ExecuteCore(CandidateEvaluation evaluation, Finding target, PreparedModel current, IModelEditor editor, string runId,
        string? approvedBy, string correctionId, string[] barIds, CancellationToken ct)
    {
        var candidate = evaluation.Candidate;
        var issueId = candidate.IssueId;
        if (approvedBy is not null)
            _audit.Record(runId, AuditAction.ApprovalRecorded, $"Approved by {approvedBy}.", issueId, candidate.Id, correctionId, barIds);
        _audit.Record(runId, AuditAction.ExecutionStarted, candidate.Description, issueId, candidate.Id, correctionId, barIds,
            Data(("strategy", candidate.Strategy.ToString()), ("modifications", candidate.Modifications.Count.ToString(CultureInfo.InvariantCulture))));

        var neighbours = evaluation.CheckedBarIds.Where(id => !barIds.Contains(id) && current.Model.Bars.ContainsKey(id)).ToArray();
        using var tx = editor.BeginTransaction($"{TransactionPrefix} {issueId} {candidate.Strategy}");
        string? failure = null;
        IReadOnlyList<InvariantResult> post = [];
        IReadOnlyList<RebarBar> actual = [];
        try
        {
            var pre = tx.ReadBars(barIds);
            for (var i = 0; i < barIds.Length; i++)
            {
                if (pre[i]?.GeometryHash == candidate.Modifications[i].ExpectedBeforeHash) continue;
                tx.RollBack();
                _audit.Record(runId, AuditAction.StalePrecondition, $"Bar {barIds[i]} no longer matches the validated geometry; nothing applied.", issueId, candidate.Id, correctionId, barIds);
                return new ExecutionResult { Status = ExecutionStatus.StalePrecondition, Reason = $"Bar {barIds[i]} changed since validation." };
            }

            foreach (var mod in candidate.Modifications)
            {
                ct.ThrowIfCancellationRequested();
                tx.Apply(mod);
                _audit.Record(runId, AuditAction.ModificationApplied, Fmt($"Translated {mod.BarId} by {mod.Translation} ({mod.Translation.Length:0.###} mm)."),
                    issueId, candidate.Id, correctionId, [mod.BarId], Data(("before", mod.ExpectedBeforeHash), ("expectedAfter", mod.ExpectedAfterHash)));
            }

            actual = tx.ReadBars(barIds).Select((b, i) => b ?? throw new InvalidOperationException($"Bar {barIds[i]} disappeared during the transaction.")).ToArray();
            for (var i = 0; i < actual.Count; i++)
            {
                var expected = current.Bar(barIds[i]).Translate(candidate.Modifications[i].Translation);
                var deviation = MaxDeviation(actual[i].Path, expected.Path);
                if (actual[i].GeometryHash == candidate.Modifications[i].ExpectedAfterHash || deviation <= 1e-3) continue;
                failure = Fmt($"Host produced different geometry for {barIds[i]} than validated (deviation {deviation:0.###} mm).");
                _audit.Record(runId, AuditAction.HostResultMismatch, failure, issueId, candidate.Id, correctionId, [barIds[i]]);
                break;
            }

            if (failure is null && neighbours.Length > 0)
            {
                var after = tx.ReadBars(neighbours);
                var changed = neighbours.Where((id, i) => after[i]?.GeometryHash != current.Bar(id).GeometryHash).ToArray();
                if (changed.Length > 0) failure = "Unaffected bars changed during the transaction: " + string.Join(", ", changed);
            }

            if (failure is null)
            {
                var check = _validator.EvaluateState(candidate, target, current, actual, [], ct);
                post = check.Invariants;
                if (!check.Valid) failure = "Post-application validation failed: " + string.Join(" | ", check.RejectionReasons);
            }

            if (failure is not null)
            {
                _audit.Record(runId, AuditAction.PostValidationFailed, failure, issueId, candidate.Id, correctionId, barIds);
                tx.RollBack();
            }
            else
            {
                _audit.Record(runId, AuditAction.PostValidationPassed, $"All {post.Count} invariants hold on the actual post-state.", issueId, candidate.Id, correctionId, barIds);
                tx.Commit();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failure = $"{ex.GetType().Name}: {ex.Message}";
            _audit.Record(runId, AuditAction.ExecutionFailed, failure, issueId, candidate.Id, correctionId, barIds);
            if (tx.State == TransactionState.Active) tx.RollBack();
        }
        catch (OperationCanceledException)
        {
            if (tx.State == TransactionState.Active) tx.RollBack();
            VerifyRollback(editor, candidate, runId, correctionId);
            throw;
        }

        if (failure is not null)
        {
            _audit.Record(runId, AuditAction.RolledBack, "Transaction rolled back: " + failure, issueId, candidate.Id, correctionId, barIds);
            VerifyRollback(editor, candidate, runId, correctionId);
            return new ExecutionResult { Status = ExecutionStatus.RolledBack, Reason = failure, PostValidation = post };
        }

        var record = new CorrectionRecord
        {
            CorrectionId = correctionId,
            IssueId = issueId,
            CandidateId = candidate.Id,
            RunId = runId,
            Strategy = candidate.Strategy,
            Modifications = candidate.Modifications,
            Inverse = candidate.Modifications.Select(m => m.Inverse()).ToArray(),
            AppliedAt = _clock.UtcNow,
            ApprovedBy = approvedBy,
            PostValidation = post,
            Before = evaluation.Before,
            After = evaluation.After,
        };
        _ledger[correctionId] = record;
        _audit.Record(runId, AuditAction.Committed, $"Correction committed; reversible via {record.Inverse.Count} inverse translation(s).", issueId, candidate.Id, correctionId, barIds,
            Data(candidate.Modifications.Select(m => ($"after:{m.BarId}", m.ExpectedAfterHash)).ToArray()));
        return new ExecutionResult { Status = ExecutionStatus.Committed, Reason = "Committed.", Correction = record, ActualBars = actual, PostValidation = post };
    }

    /// <summary>Reverts a committed correction (precondition: bars still in the corrected state).</summary>
    public ExecutionResult Revert(CorrectionRecord record, IModelEditor editor, string runId)
    {
        var barIds = record.Modifications.Select(m => m.BarId).ToArray();
        using var tx = editor.BeginTransaction($"{TransactionPrefix} revert {record.CorrectionId}");
        try
        {
            var now = tx.ReadBars(barIds);
            for (var i = 0; i < barIds.Length; i++)
            {
                if (now[i]?.GeometryHash == record.Modifications[i].ExpectedAfterHash) continue;
                tx.RollBack();
                _audit.Record(runId, AuditAction.RevertFailed, $"Bar {barIds[i]} was modified after the correction; revert refused.", record.IssueId, record.CandidateId, record.CorrectionId, barIds);
                return new ExecutionResult { Status = ExecutionStatus.StalePrecondition, Reason = $"Bar {barIds[i]} changed after the correction." };
            }

            foreach (var inv in record.Inverse) tx.Apply(inv);
            var restored = tx.ReadBars(barIds);
            for (var i = 0; i < barIds.Length; i++)
            {
                if (restored[i]?.GeometryHash == record.Modifications[i].ExpectedBeforeHash) continue;
                tx.RollBack();
                _audit.Record(runId, AuditAction.RevertFailed, $"Revert did not restore {barIds[i]}; rolled back.", record.IssueId, record.CandidateId, record.CorrectionId, barIds);
                return new ExecutionResult { Status = ExecutionStatus.RolledBack, Reason = $"Revert did not restore {barIds[i]}." };
            }

            tx.Commit();
        }
        catch (Exception ex)
        {
            if (tx.State == TransactionState.Active) tx.RollBack();
            _audit.Record(runId, AuditAction.RevertFailed, ex.Message, record.IssueId, record.CandidateId, record.CorrectionId, barIds);
            return new ExecutionResult { Status = ExecutionStatus.RolledBack, Reason = ex.Message };
        }

        var reverted = record with { Status = ExecutionStatus.Reverted };
        _ledger[record.CorrectionId] = reverted;
        _audit.Record(runId, AuditAction.Reverted, "Correction reverted; original geometry restored.", record.IssueId, record.CandidateId, record.CorrectionId, barIds);
        return new ExecutionResult { Status = ExecutionStatus.Reverted, Reason = "Reverted.", Correction = reverted };
    }

    private void VerifyRollback(IModelEditor editor, CandidateCorrection candidate, string runId, string correctionId)
    {
        var barIds = candidate.Modifications.Select(m => m.BarId).ToArray();
        var now = editor.ReadBars(barIds);
        var intact = now.Zip(candidate.Modifications).All(p => p.First?.GeometryHash == p.Second.ExpectedBeforeHash);
        _audit.Record(runId, intact ? AuditAction.RollbackVerified : AuditAction.RollbackVerificationFailed,
            intact ? "Model state after rollback matches the original geometry." : "Model state after rollback does NOT match the original geometry: manual inspection required.",
            candidate.IssueId, candidate.Id, correctionId, barIds);
    }

    private static double MaxDeviation(BarPath a, BarPath b)
    {
        var ta = a.Tessellate(0.1);
        var tb = b.Tessellate(0.1);
        if (ta.Count != tb.Count) return double.PositiveInfinity;
        return ta.Zip(tb).Max(p => Math.Max(Vec3.Distance(p.First.A, p.Second.A), Vec3.Distance(p.First.B, p.Second.B)));
    }

    private static string Fmt(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    private static IReadOnlyDictionary<string, string> Data(params (string Key, string Value)[] items)
    {
        var d = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in items) d[k] = v;
        return d;
    }
}
