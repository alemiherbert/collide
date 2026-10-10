using System.Text.Json;
using Collide.Serialization;

namespace Collide.Audit;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public static SystemClock Instance { get; } = new();
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>Deterministic clock for reproducible tests and reports: starts at a fixed instant and advances 1 ms per read.</summary>
public sealed class FixedClock(DateTimeOffset start) : IClock
{
    private long _ticks;

    public DateTimeOffset UtcNow => start.AddMilliseconds(Interlocked.Increment(ref _ticks) - 1);
}

public enum AuditAction
{
    RunStarted,
    IssueSkipped,
    CandidateGenerated,
    CandidateRejected,
    CandidateValid,
    DecisionMade,
    ApprovalRecorded,
    ExecutionStarted,
    StalePrecondition,
    ModificationApplied,
    HostResultMismatch,
    PostValidationPassed,
    PostValidationFailed,
    Committed,
    RolledBack,
    RollbackVerified,
    RollbackVerificationFailed,
    ExecutionFailed,
    AlreadyApplied,
    Reverted,
    RevertFailed,
    RunCompleted,
}

public sealed record AuditEntry(
    long Sequence,
    DateTimeOffset Timestamp,
    string RunId,
    AuditAction Action,
    string? IssueId,
    string? CandidateId,
    string? CorrectionId,
    IReadOnlyList<string> BarIds,
    string Message,
    IReadOnlyDictionary<string, string> Data);

public interface IAuditSink
{
    void Write(AuditEntry entry);
}

/// <summary>Writes one JSON object per line (append-only), suitable for long-term traceability.</summary>
public sealed class JsonLinesAuditSink(string path) : IAuditSink
{
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions Options = CollideJson.Create(indented: false);

    public void Write(AuditEntry entry)
    {
        var line = JsonSerializer.Serialize(entry, Options);
        lock (_gate) File.AppendAllText(path, line + Environment.NewLine);
    }
}

/// <summary>Thread-safe, append-only audit trail of every resolution step.</summary>
public sealed class AuditLog
{
    private readonly object _gate = new();
    private readonly List<AuditEntry> _entries = [];
    private readonly IClock _clock;
    private readonly IReadOnlyList<IAuditSink> _sinks;

    public AuditLog(IClock? clock = null, params IAuditSink[] sinks)
    {
        _clock = clock ?? SystemClock.Instance;
        _sinks = sinks;
    }

    public IReadOnlyList<AuditEntry> Entries
    {
        get
        {
            lock (_gate) return _entries.ToArray();
        }
    }

    public AuditEntry Record(string runId, AuditAction action, string message, string? issueId = null, string? candidateId = null,
        string? correctionId = null, IEnumerable<string>? barIds = null, IReadOnlyDictionary<string, string>? data = null)
    {
        AuditEntry entry;
        lock (_gate)
        {
            entry = new AuditEntry(_entries.Count + 1, _clock.UtcNow, runId, action, issueId, candidateId, correctionId,
                barIds?.ToArray() ?? [], message, data ?? new SortedDictionary<string, string>(StringComparer.Ordinal));
            _entries.Add(entry);
        }

        foreach (var sink in _sinks) sink.Write(entry);
        return entry;
    }
}
