using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace Collide.Model;

/// <summary>
/// Immutable snapshot of reinforcement and hosts. Safe to share across threads; all updates produce a new snapshot
/// with structural sharing. Revit API objects never appear here: the adapter converts them on the API thread.
/// </summary>
public sealed class ReinforcementModel
{
    private ReinforcementModel(
        string modelId,
        long revision,
        ImmutableSortedDictionary<string, RebarBar> bars,
        ImmutableSortedDictionary<string, RebarSet> sets,
        ImmutableSortedDictionary<string, StructuralHost> hosts,
        ImmutableSortedDictionary<string, PlacementZone> zones,
        ImmutableSortedDictionary<string, Accessory> accessories,
        ImmutableSortedDictionary<string, SpliceDefinition> splices)
    {
        ModelId = modelId;
        Revision = revision;
        Bars = bars;
        Sets = sets;
        Hosts = hosts;
        Zones = zones;
        Accessories = accessories;
        Splices = splices;
    }

    public string ModelId { get; }

    /// <summary>Monotonic revision of this snapshot (incremented by every delta).</summary>
    public long Revision { get; }

    public ImmutableSortedDictionary<string, RebarBar> Bars { get; }
    public ImmutableSortedDictionary<string, RebarSet> Sets { get; }
    public ImmutableSortedDictionary<string, StructuralHost> Hosts { get; }
    public ImmutableSortedDictionary<string, PlacementZone> Zones { get; }
    public ImmutableSortedDictionary<string, Accessory> Accessories { get; }
    public ImmutableSortedDictionary<string, SpliceDefinition> Splices { get; }

    public static ReinforcementModel Create(
        string modelId,
        IEnumerable<RebarBar> bars,
        IEnumerable<StructuralHost> hosts,
        IEnumerable<RebarSet>? sets = null,
        IEnumerable<PlacementZone>? zones = null,
        IEnumerable<Accessory>? accessories = null,
        IEnumerable<SpliceDefinition>? splices = null,
        long revision = 1)
    {
        return new ReinforcementModel(
            modelId,
            revision,
            ToDictionary(bars, b => b.Id, "bar"),
            ToDictionary(sets ?? [], s => s.Id, "set"),
            ToDictionary(hosts, h => h.Id, "host"),
            ToDictionary(zones ?? [], z => z.Id, "zone"),
            ToDictionary(accessories ?? [], a => a.Id, "accessory"),
            ToDictionary(splices ?? [], s => s.Id, "splice"));
    }

    private static ImmutableSortedDictionary<string, T> ToDictionary<T>(IEnumerable<T> items, Func<T, string> key, string kind)
    {
        var builder = ImmutableSortedDictionary.CreateBuilder<string, T>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var k = key(item);
            if (builder.ContainsKey(k)) throw new ArgumentException($"Duplicate {kind} id '{k}'.");
            builder.Add(k, item);
        }

        return builder.ToImmutable();
    }

    public RebarBar? FindBar(string id) => Bars.TryGetValue(id, out var b) ? b : null;
    public StructuralHost? FindHost(string? id) => id is not null && Hosts.TryGetValue(id, out var h) ? h : null;

    public IEnumerable<RebarSet> SetsContaining(string barId) => Sets.Values.Where(s => s.BarIds.Contains(barId));

    /// <summary>Applies a delta and returns the next snapshot (revision + 1).</summary>
    public ReinforcementModel Apply(ModelDelta delta)
    {
        var bars = Bars.ToBuilder();
        var sets = Sets.ToBuilder();
        var hosts = Hosts.ToBuilder();
        var zones = Zones.ToBuilder();
        var accessories = Accessories.ToBuilder();

        // Element-level removal/replacement: a Revit element id maps to all bars/sets/accessories created from it.
        var replaced = new HashSet<string>(delta.RemovedElementIds.Concat(delta.ReplacedElementIds), StringComparer.Ordinal);
        if (replaced.Count > 0)
        {
            foreach (var b in Bars.Values.Where(b => replaced.Contains(b.ElementId))) bars.Remove(b.Id);
            foreach (var s in Sets.Values.Where(s => replaced.Contains(s.ElementId))) sets.Remove(s.Id);
            foreach (var a in Accessories.Values.Where(a => replaced.Contains(a.ElementId))) accessories.Remove(a.Id);
        }

        foreach (var id in delta.RemovedElementIds)
        {
            hosts.Remove(id);
            zones.Remove(id);
        }

        foreach (var id in delta.RemovedBarIds) bars.Remove(id);
        foreach (var b in delta.UpsertBars) bars[b.Id] = b;
        foreach (var s in delta.UpsertSets) sets[s.Id] = s;
        foreach (var h in delta.UpsertHosts) hosts[h.Id] = h;
        foreach (var z in delta.UpsertZones) zones[z.Id] = z;
        foreach (var a in delta.UpsertAccessories) accessories[a.Id] = a;

        return new ReinforcementModel(ModelId, Revision + 1, bars.ToImmutable(), sets.ToImmutable(), hosts.ToImmutable(),
            zones.ToImmutable(), accessories.ToImmutable(), Splices);
    }

    /// <summary>Replaces bars by id (used for candidate corrections).</summary>
    public ReinforcementModel WithBars(IEnumerable<RebarBar> replacements) => Apply(new ModelDelta { UpsertBars = replacements.ToArray() });

    /// <summary>Deterministic content hash over all bars (geometry + identity) and hosts.</summary>
    public string ContentHash()
    {
        var sb = new StringBuilder();
        foreach (var b in Bars.Values)
        {
            sb.Append(b.Id).Append('=').Append(b.GeometryHash).Append(';').Append(b.NominalDiameter.Millimetres.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
        }

        foreach (var h in Hosts.Values)
        {
            sb.Append(h.Id).Append(':').Append(h.Category).Append(':');
            foreach (var f in h.Region.Faces)
                sb.Append(Math.Round(f.Plane.Offset * 1e6).ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',');
            sb.Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16];
    }
}

/// <summary>
/// A change to apply to a snapshot. <see cref="ReplacedElementIds"/> removes every bar/set/accessory created from those
/// element ids before the upserts are added (a modified Revit Rebar element may change its number of bar positions).
/// </summary>
public sealed record ModelDelta
{
    public IReadOnlyList<RebarBar> UpsertBars { get; init; } = [];
    public IReadOnlyList<RebarSet> UpsertSets { get; init; } = [];
    public IReadOnlyList<StructuralHost> UpsertHosts { get; init; } = [];
    public IReadOnlyList<PlacementZone> UpsertZones { get; init; } = [];
    public IReadOnlyList<Accessory> UpsertAccessories { get; init; } = [];
    public IReadOnlyList<string> RemovedBarIds { get; init; } = [];
    public IReadOnlyList<string> RemovedElementIds { get; init; } = [];
    public IReadOnlyList<string> ReplacedElementIds { get; init; } = [];

    public bool IsEmpty =>
        UpsertBars.Count == 0 && UpsertSets.Count == 0 && UpsertHosts.Count == 0 && UpsertZones.Count == 0 &&
        UpsertAccessories.Count == 0 && RemovedBarIds.Count == 0 && RemovedElementIds.Count == 0 && ReplacedElementIds.Count == 0;
}
