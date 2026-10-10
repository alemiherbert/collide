using System.Collections.Immutable;
using Collide.Findings;

namespace Collide.Analysis;

/// <summary>
/// Which part of the model an analysis recomputes. A finding is recomputed (and may be replaced) exactly when its
/// owner is inside the scope: pair/bar findings by any owning bar, set findings by set, host-level findings by host,
/// mark findings by mark.
/// </summary>
public sealed record AnalysisScope
{
    public static AnalysisScope All { get; } = new() { IsAll = true };

    public bool IsAll { get; init; }
    public ImmutableSortedSet<string> Bars { get; init; } = ImmutableSortedSet.Create<string>(StringComparer.Ordinal);
    public ImmutableSortedSet<string> Sets { get; init; } = ImmutableSortedSet.Create<string>(StringComparer.Ordinal);
    public ImmutableSortedSet<string> Hosts { get; init; } = ImmutableSortedSet.Create<string>(StringComparer.Ordinal);
    public ImmutableSortedSet<string> Marks { get; init; } = ImmutableSortedSet.Create<string>(StringComparer.Ordinal);

    public bool IsEmpty => !IsAll && Bars.IsEmpty && Sets.IsEmpty && Hosts.IsEmpty && Marks.IsEmpty;

    public bool Owns(Finding finding)
    {
        if (IsAll) return true;
        return finding.Owner.Kind switch
        {
            OwnerKind.Bars => finding.Owner.Keys.Any(Bars.Contains),
            OwnerKind.Set => finding.Owner.Keys.Any(Sets.Contains),
            OwnerKind.Host => finding.Owner.Keys.Any(Hosts.Contains),
            OwnerKind.Mark => finding.Owner.Keys.Any(Marks.Contains),
            _ => false,
        };
    }

    public AnalysisScope Union(AnalysisScope other)
    {
        if (IsAll || other.IsAll) return All;
        return new AnalysisScope
        {
            Bars = Bars.Union(other.Bars),
            Sets = Sets.Union(other.Sets),
            Hosts = Hosts.Union(other.Hosts),
            Marks = Marks.Union(other.Marks),
        };
    }

    /// <summary>
    /// Expands a set of changed bars/hosts into a complete re-analysis scope on <paramref name="before"/> and
    /// <paramref name="after"/>: changed bars, bars hosted by or overlapping changed hosts, every set/mark containing a
    /// changed bar, and every host whose host-level checks (congestion, transverse spacing) can see a changed bar.
    /// </summary>
    public static AnalysisScope ForChanges(PreparedModel? before, PreparedModel after, IEnumerable<string> changedBarIds, IEnumerable<string> changedHostIds)
    {
        var bars = ImmutableSortedSet.CreateBuilder<string>(StringComparer.Ordinal);
        var hosts = ImmutableSortedSet.CreateBuilder<string>(StringComparer.Ordinal);
        var sets = ImmutableSortedSet.CreateBuilder<string>(StringComparer.Ordinal);
        var marks = ImmutableSortedSet.CreateBuilder<string>(StringComparer.Ordinal);
        foreach (var id in changedBarIds) bars.Add(id);

        foreach (var hostId in changedHostIds)
        {
            hosts.Add(hostId);
            foreach (var pm in new[] { before, after })
            {
                if (pm is null) continue;
                if (pm.BarsByHost.TryGetValue(hostId, out var hosted)) bars.UnionWith(hosted);
                if (pm.HostIndex.TryGetBox(hostId, out var box)) bars.UnionWith(pm.BarIndex.Query(box));
            }
        }

        foreach (var barId in bars.ToArray())
        {
            foreach (var pm in new[] { before, after })
            {
                if (pm is null) continue;
                if (pm.Model.Bars.TryGetValue(barId, out var bar))
                {
                    if (bar.Mark is not null) marks.Add(bar.Mark);
                    if (bar.HostId is not null) hosts.Add(bar.HostId);
                }

                if (pm.SetsByBar.TryGetValue(barId, out var s)) sets.UnionWith(s);
                if (pm.Solids.TryGetValue(barId, out var solid)) hosts.UnionWith(pm.HostIndex.Query(solid.Bounds));
            }
        }

        return new AnalysisScope { Bars = bars.ToImmutable(), Hosts = hosts.ToImmutable(), Sets = sets.ToImmutable(), Marks = marks.ToImmutable() };
    }
}
