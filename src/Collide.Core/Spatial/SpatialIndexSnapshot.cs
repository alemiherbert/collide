using System.Collections.Immutable;
using Collide.Geometry;

namespace Collide.Spatial;

/// <summary>
/// Persistent (immutable) keyed spatial index: a static BVH plus a small overlay of inserted/updated keys and a set
/// of removed keys. Updates return a new snapshot and share the BVH, so incremental detection and candidate
/// validation never rebuild the whole tree; the BVH is rebuilt once the overlay grows past a threshold.
/// </summary>
public sealed class SpatialIndexSnapshot
{
    private readonly BoundingVolumeHierarchy _bvh;
    private readonly string[] _baseKeys;
    private readonly ImmutableDictionary<string, Aabb> _baseBoxes;
    private readonly ImmutableSortedDictionary<string, Aabb> _overlay;
    private readonly ImmutableHashSet<string> _removed;

    private SpatialIndexSnapshot(
        BoundingVolumeHierarchy bvh,
        string[] baseKeys,
        ImmutableDictionary<string, Aabb> baseBoxes,
        ImmutableSortedDictionary<string, Aabb> overlay,
        ImmutableHashSet<string> removed)
    {
        _bvh = bvh;
        _baseKeys = baseKeys;
        _baseBoxes = baseBoxes;
        _overlay = overlay;
        _removed = removed;
    }

    public static SpatialIndexSnapshot Build(IEnumerable<KeyValuePair<string, Aabb>> entries)
    {
        var ordered = entries.OrderBy(e => e.Key, StringComparer.Ordinal).ToArray();
        var keys = ordered.Select(e => e.Key).ToArray();
        var bvh = new BoundingVolumeHierarchy(ordered.Select(e => e.Value).ToArray());
        return new SpatialIndexSnapshot(
            bvh,
            keys,
            ordered.ToImmutableDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal),
            ImmutableSortedDictionary.Create<string, Aabb>(StringComparer.Ordinal),
            ImmutableHashSet.Create<string>(StringComparer.Ordinal));
    }

    // Every upsert of a base key also marks it removed from the base, so overlay and removals never double count.
    public int Count => _baseKeys.Length - _removed.Count + _overlay.Count;
    public int OverlayCount => _overlay.Count;

    public bool TryGetBox(string key, out Aabb box)
    {
        if (_overlay.TryGetValue(key, out box)) return true;
        if (!_removed.Contains(key) && _baseBoxes.TryGetValue(key, out box)) return true;
        box = default;
        return false;
    }

    public SpatialIndexSnapshot Apply(IEnumerable<KeyValuePair<string, Aabb>> upserts, IEnumerable<string> removals, int rebuildThreshold = -1)
    {
        var overlay = _overlay.ToBuilder();
        var removed = _removed.ToBuilder();
        foreach (var key in removals)
        {
            overlay.Remove(key);
            if (_baseBoxes.ContainsKey(key)) removed.Add(key);
        }

        foreach (var (key, box) in upserts)
        {
            overlay[key] = box;
            if (_baseBoxes.ContainsKey(key)) removed.Add(key);
        }

        var next = new SpatialIndexSnapshot(_bvh, _baseKeys, _baseBoxes, overlay.ToImmutable(), removed.ToImmutable());
        var threshold = rebuildThreshold >= 0 ? rebuildThreshold : Math.Max(64, _baseKeys.Length / 50);
        return next._overlay.Count + next._removed.Count > threshold ? Build(next.Entries()) : next;
    }

    public IEnumerable<KeyValuePair<string, Aabb>> Entries()
    {
        foreach (var key in _baseKeys)
        {
            if (!_removed.Contains(key)) yield return new KeyValuePair<string, Aabb>(key, _baseBoxes[key]);
        }

        foreach (var kv in _overlay) yield return kv;
    }

    /// <summary>Keys whose boxes intersect <paramref name="query"/>, in ordinal order (deterministic).</summary>
    public List<string> Query(in Aabb query)
    {
        var hits = new List<int>();
        _bvh.Query(query, hits);
        var result = new List<string>(hits.Count + 4);
        foreach (var i in hits)
        {
            var key = _baseKeys[i];
            if (!_removed.Contains(key)) result.Add(key);
        }

        foreach (var (key, box) in _overlay)
        {
            if (box.Intersects(query)) result.Add(key);
        }

        result.Sort(StringComparer.Ordinal);
        return result;
    }
}
