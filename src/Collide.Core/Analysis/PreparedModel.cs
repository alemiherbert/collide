using System.Collections.Immutable;
using Collide.Geometry;
using Collide.Model;
using Collide.Rules;
using Collide.Spatial;

namespace Collide.Analysis;

/// <summary>
/// A model snapshot with its physical solids and spatial indexes. Immutable; <see cref="Apply"/> derives a new
/// prepared model that reuses every unchanged solid and shares the index trees (used by live monitoring and by
/// candidate validation, which must not rebuild the whole model per candidate).
/// </summary>
public sealed class PreparedModel
{
    private PreparedModel(
        ReinforcementModel model,
        RuleSet rules,
        IGeometryKernel kernel,
        ImmutableDictionary<string, BarSolid> solids,
        ImmutableDictionary<string, BarSolid> accessorySolids,
        SpatialIndexSnapshot barIndex,
        SpatialIndexSnapshot hostIndex,
        double searchMargin)
    {
        Model = model;
        Rules = rules;
        Kernel = kernel;
        Solids = solids;
        AccessorySolids = accessorySolids;
        BarIndex = barIndex;
        HostIndex = hostIndex;
        SearchMargin = searchMargin;
        SetsByBar = BuildSetsByBar(model);
        BarsByHost = model.Bars.Values.Where(b => b.HostId is not null)
            .GroupBy(b => b.HostId!, StringComparer.Ordinal)
            .ToImmutableDictionary(g => g.Key, g => g.Select(b => b.Id).ToImmutableSortedSet(StringComparer.Ordinal), StringComparer.Ordinal);
    }

    public ReinforcementModel Model { get; }
    public RuleSet Rules { get; }
    public IGeometryKernel Kernel { get; }
    public ImmutableDictionary<string, BarSolid> Solids { get; }
    public ImmutableDictionary<string, BarSolid> AccessorySolids { get; }

    /// <summary>Bar solids' bounds inflated by half the search margin (so overlap ⇔ surfaces within the margin).</summary>
    public SpatialIndexSnapshot BarIndex { get; }

    public SpatialIndexSnapshot HostIndex { get; }

    /// <summary>Largest surface gap at which any configured check can matter (clear spacing, laps, contact).</summary>
    public double SearchMargin { get; }

    public ImmutableDictionary<string, ImmutableArray<string>> SetsByBar { get; }
    public ImmutableDictionary<string, ImmutableSortedSet<string>> BarsByHost { get; }

    public static PreparedModel Create(ReinforcementModel model, RuleSet rules, IGeometryKernel kernel, CancellationToken ct = default)
    {
        var chord = rules.Tolerances.ArcChordTolerance.Millimetres;
        var solids = ImmutableDictionary.CreateBuilder<string, BarSolid>(StringComparer.Ordinal);
        foreach (var bar in model.Bars.Values)
        {
            ct.ThrowIfCancellationRequested();
            solids[bar.Id] = kernel.CreateSolid(bar.Id, bar.Path, bar.PhysicalDiameter.Millimetres, chord);
        }

        var margin = ComputeSearchMargin(model, rules);
        var barIndex = SpatialIndexSnapshot.Build(solids.Select(kv => KeyValuePair.Create(kv.Key, kv.Value.Bounds.Inflate(margin / 2))));
        var hostIndex = SpatialIndexSnapshot.Build(model.Hosts.Values.Select(h => KeyValuePair.Create(h.Id, h.Bounds)));
        return new PreparedModel(model, rules, kernel, solids.ToImmutable(), BuildAccessorySolids(model), barIndex, hostIndex, margin);
    }

    /// <summary>Applies a delta, reusing solids of bars whose record is unchanged (reference equality).</summary>
    public PreparedModel Apply(ModelDelta delta, CancellationToken ct = default) => WithModel(Model.Apply(delta), ct);

    public PreparedModel WithModel(ReinforcementModel next, CancellationToken ct = default)
    {
        var chord = Rules.Tolerances.ArcChordTolerance.Millimetres;
        var margin = ComputeSearchMargin(next, Rules);
        if (Math.Abs(margin - SearchMargin) > 1e-9) return Create(next, Rules, Kernel, ct);

        var solids = Solids.ToBuilder();
        var upserts = new List<KeyValuePair<string, Aabb>>();
        var removals = new List<string>();
        foreach (var id in Model.Bars.Keys)
        {
            if (!next.Bars.ContainsKey(id))
            {
                solids.Remove(id);
                removals.Add(id);
            }
        }

        foreach (var bar in next.Bars.Values)
        {
            ct.ThrowIfCancellationRequested();
            if (Model.Bars.TryGetValue(bar.Id, out var old) && ReferenceEquals(old, bar)) continue;
            var solid = Kernel.CreateSolid(bar.Id, bar.Path, bar.PhysicalDiameter.Millimetres, chord);
            solids[bar.Id] = solid;
            upserts.Add(KeyValuePair.Create(bar.Id, solid.Bounds.Inflate(margin / 2)));
        }

        var hostUpserts = new List<KeyValuePair<string, Aabb>>();
        var hostRemovals = Model.Hosts.Keys.Where(k => !next.Hosts.ContainsKey(k)).ToList();
        foreach (var host in next.Hosts.Values)
        {
            if (Model.Hosts.TryGetValue(host.Id, out var old) && ReferenceEquals(old, host)) continue;
            hostUpserts.Add(KeyValuePair.Create(host.Id, host.Bounds));
        }

        var accessories = ReferenceEquals(next.Accessories, Model.Accessories) ? AccessorySolids : BuildAccessorySolids(next);
        return new PreparedModel(next, Rules, Kernel, solids.ToImmutable(), accessories,
            BarIndex.Apply(upserts, removals), HostIndex.Apply(hostUpserts, hostRemovals, rebuildThreshold: 0), margin);
    }

    public RebarBar Bar(string id) => Model.Bars[id];

    public IReadOnlyList<StructuralHost> HostsNear(Aabb box) =>
        HostIndex.Query(box).Select(id => Model.Hosts[id]).ToArray();

    public IEnumerable<StructuralHost> HostsContaining(Vec3 p, double tolerance) =>
        HostIndex.Query(new Aabb(p, p).Inflate(Math.Abs(tolerance) + 1e-9)).Select(id => Model.Hosts[id]).Where(h => h.Region.Contains(p, tolerance));

    private static ImmutableDictionary<string, BarSolid> BuildAccessorySolids(ReinforcementModel model) =>
        model.Accessories.Values.ToImmutableDictionary(
            a => a.Id,
            a => BarSolid.Cylinder(a.Id, a.AxisStart, a.AxisEnd, a.OuterDiameter.Millimetres / 2),
            StringComparer.Ordinal);

    private static ImmutableDictionary<string, ImmutableArray<string>> BuildSetsByBar(ReinforcementModel model)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var set in model.Sets.Values)
        {
            foreach (var barId in set.BarIds)
            {
                if (!map.TryGetValue(barId, out var list)) map[barId] = list = [];
                list.Add(set.Id);
            }
        }

        foreach (var bar in model.Bars.Values)
        {
            if (bar.SetId is null) continue;
            if (!map.TryGetValue(bar.Id, out var list)) map[bar.Id] = list = [];
            if (!list.Contains(bar.SetId)) list.Add(bar.SetId);
        }

        return map.ToImmutableDictionary(kv => kv.Key, kv => kv.Value.OrderBy(s => s, StringComparer.Ordinal).ToImmutableArray(), StringComparer.Ordinal);
    }

    /// <summary>
    /// Largest surface gap any check needs to see: max possible required clear spacing, lap-inference offset and
    /// contact tolerance, evaluated for the largest bar diameter in the model.
    /// </summary>
    internal static double ComputeSearchMargin(ReinforcementModel model, RuleSet rules)
    {
        var maxD = model.Bars.Count == 0 ? 0 : model.Bars.Values.Max(b => b.NominalDiameter.Millimetres);
        var maxAgg = new[] { rules.Project.MaximumAggregateSize?.Millimetres ?? 0 }
            .Concat(model.Hosts.Values.Select(h => h.MaximumAggregateSize?.Millimetres ?? 0)).Max();
        var margin = rules.Tolerances.ContactTolerance.Millimetres;
        foreach (var r in rules.ClearSpacing)
        {
            if (r.BarDiameterFactor is { } k) margin = Math.Max(margin, k * maxD);
            if (r.AggregateAllowance is { } allowance) margin = Math.Max(margin, maxAgg + allowance.Millimetres);
            if (r.AggregateFactor is { } f) margin = Math.Max(margin, f * maxAgg);
            if (r.AbsoluteMinimum is { } min) margin = Math.Max(margin, min.Millimetres);
        }

        if (rules.Tolerances.InferLaps) margin = Math.Max(margin, rules.Tolerances.LapInferenceMaxOffsetFactor * maxD);
        // Round up so tiny model changes do not force index rebuilds.
        return Math.Ceiling(margin + 1.0);
    }
}
