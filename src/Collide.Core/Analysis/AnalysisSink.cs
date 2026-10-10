using Collide.Findings;
using Collide.Model;
using Collide.Rules;

namespace Collide.Analysis;

/// <summary>Per-thread accumulator for analysis output; merged and sorted deterministically afterwards.</summary>
internal sealed class AnalysisSink
{
    public List<Finding> Findings { get; } = [];
    public List<SuppressedFinding> Suppressed { get; } = [];
    public List<LapPair> Laps { get; } = [];
    public long CandidatePairs;
    public long PrimitivePairs;

    public void Merge(AnalysisSink other)
    {
        Findings.AddRange(other.Findings);
        Suppressed.AddRange(other.Suppressed);
        Laps.AddRange(other.Laps);
        CandidatePairs += other.CandidatePairs;
        PrimitivePairs += other.PrimitivePairs;
    }
}

internal static class FindingFactory
{
    /// <summary>Indeterminate result for a required check whose rule or input is missing.</summary>
    public static Finding Indeterminate(
        CheckKind check,
        FindingOwner owner,
        IReadOnlyList<string> barIds,
        IReadOnlyList<string> hostIds,
        string consolidationKey,
        string explanation,
        IReadOnlyList<string> missingInputs,
        string discriminator = "") => new()
    {
        Check = check,
        Subtype = IssueSubtype.RequirementUndefined,
        Status = FindingStatus.Indeterminate,
        Owner = owner,
        BarIds = Sorted(barIds),
        HostIds = Sorted(hostIds),
        ConsolidationKey = consolidationKey,
        Explanation = explanation,
        MissingInputs = missingInputs,
        Discriminator = discriminator,
    };

    public static IReadOnlyList<string> Sorted(IEnumerable<string?> ids) =>
        ids.Where(i => i is not null).Select(i => i!).Distinct().OrderBy(i => i, StringComparer.Ordinal).ToArray();

    public static RuleContext Ctx(PreparedModel pm, RebarBar bar, StructuralHost? host = null, Geometry.FaceTag? face = null) =>
        RuleContext.ForBar(bar, host ?? pm.Model.FindHost(bar.HostId), face);
}
