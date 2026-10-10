using Collide.Findings;
using Collide.Geometry;

namespace Collide.Analysis;

/// <summary>A contact or clash that matched an intentional-contact rule or an approved exception, kept for traceability.</summary>
public sealed record SuppressedFinding(
    string Fingerprint,
    IssueSubtype Subtype,
    IReadOnlyList<string> BarIds,
    string Reason,
    string RuleOrExceptionId,
    double PenetrationMm,
    Vec3? Location);

/// <summary>A lap splice detected between two bars (declared in the model or inferred from geometry).</summary>
public sealed record LapPair(
    string BarA,
    string BarB,
    double OverlapLengthMm,
    double CentreOffsetMm,
    double ClearOffsetMm,
    bool Declared,
    Vec3 Location);

public sealed record AnalysisStatistics
{
    public int BarsInModel { get; init; }
    public int BarsAnalysed { get; init; }
    public int SolidPrimitives { get; init; }
    public long CandidateBarPairs { get; init; }
    public long PrimitivePairsMeasured { get; init; }
    public int FindingCount { get; init; }
    public double ElapsedMilliseconds { get; init; }
    public IReadOnlyDictionary<string, double> StageMilliseconds { get; init; } = new Dictionary<string, double>();

    /// <summary>Fraction of all bar pairs eliminated by the broad phase (1 − candidates / (n·(n−1)/2)).</summary>
    public double BroadPhasePruning =>
        BarsInModel < 2 ? 0 : 1 - CandidateBarPairs / (BarsInModel * (BarsInModel - 1) / 2.0);
}

public sealed record AnalysisResult
{
    public required long ModelRevision { get; init; }
    public required AnalysisScope Scope { get; init; }
    public required IReadOnlyList<Finding> Findings { get; init; }
    public IReadOnlyList<SuppressedFinding> Suppressed { get; init; } = [];
    public IReadOnlyList<LapPair> Laps { get; init; } = [];
    public IReadOnlyList<string> ChecksNotPerformed { get; init; } = [];
    public required AnalysisStatistics Statistics { get; init; }
}
