using Collide.Geometry;

namespace Collide.Spatial;

/// <summary>
/// Immutable bounding-volume hierarchy over axis-aligned boxes (median split on the longest centroid axis).
/// Deterministic for identical input order. Results are candidate pairs only; exact checks happen in the narrow phase.
/// </summary>
public sealed class BoundingVolumeHierarchy
{
    private readonly Aabb[] _boxes;
    private readonly int[] _items;
    private readonly List<Node> _nodes = [];
    private readonly int _leafSize;

    private struct Node
    {
        public Aabb Box;
        public int Left;
        public int Right;
        public int Start;
        public int Count;
        public readonly bool IsLeaf => Count > 0;
    }

    public BoundingVolumeHierarchy(IReadOnlyList<Aabb> boxes, int leafSize = 4)
    {
        if (leafSize < 1) throw new ArgumentOutOfRangeException(nameof(leafSize));
        _leafSize = leafSize;
        _boxes = boxes.ToArray();
        _items = Enumerable.Range(0, _boxes.Length).ToArray();
        if (_boxes.Length > 0) Build(0, _boxes.Length);
    }

    public int Count => _boxes.Length;
    public int NodeCount => _nodes.Count;
    public Aabb BoxOf(int item) => _boxes[item];

    private int Build(int start, int count)
    {
        var box = Aabb.Empty;
        var centroids = Aabb.Empty;
        for (var i = start; i < start + count; i++)
        {
            box = box.Union(_boxes[_items[i]]);
            centroids = centroids.Include(_boxes[_items[i]].Center);
        }

        var index = _nodes.Count;
        _nodes.Add(default);
        if (count <= _leafSize || centroids.Size.LengthSquared < 1e-18)
        {
            _nodes[index] = new Node { Box = box, Start = start, Count = count, Left = -1, Right = -1 };
            return index;
        }

        var axis = centroids.LongestAxis;
        Array.Sort(_items, start, count, Comparer<int>.Create((x, y) =>
        {
            var c = _boxes[x].Center[axis].CompareTo(_boxes[y].Center[axis]);
            return c != 0 ? c : x.CompareTo(y);
        }));
        var half = count / 2;
        var left = Build(start, half);
        var right = Build(start + half, count - half);
        _nodes[index] = new Node { Box = box, Left = left, Right = right, Start = 0, Count = 0 };
        return index;
    }

    /// <summary>Appends the indices of all boxes intersecting <paramref name="query"/>.</summary>
    public void Query(in Aabb query, List<int> results)
    {
        if (_nodes.Count == 0 || query.IsEmpty) return;
        var stack = new Stack<int>();
        stack.Push(0);
        while (stack.Count > 0)
        {
            var node = _nodes[stack.Pop()];
            if (!node.Box.Intersects(query)) continue;
            if (node.IsLeaf)
            {
                for (var i = node.Start; i < node.Start + node.Count; i++)
                {
                    var item = _items[i];
                    if (_boxes[item].Intersects(query)) results.Add(item);
                }
            }
            else
            {
                stack.Push(node.Right);
                stack.Push(node.Left);
            }
        }
    }

    /// <summary>Reports every unordered pair (i &lt; j) whose boxes intersect, exactly once.</summary>
    public void QueryAllPairs(Action<int, int> onPair)
    {
        if (_nodes.Count == 0) return;
        SelfPairs(0, onPair);
    }

    private void SelfPairs(int n, Action<int, int> onPair)
    {
        var node = _nodes[n];
        if (node.IsLeaf)
        {
            for (var i = node.Start; i < node.Start + node.Count; i++)
            for (var j = i + 1; j < node.Start + node.Count; j++)
                Emit(_items[i], _items[j], onPair);
            return;
        }

        SelfPairs(node.Left, onPair);
        SelfPairs(node.Right, onPair);
        CrossPairs(node.Left, node.Right, onPair);
    }

    private void CrossPairs(int a, int b, Action<int, int> onPair)
    {
        var na = _nodes[a];
        var nb = _nodes[b];
        if (!na.Box.Intersects(nb.Box)) return;
        if (na.IsLeaf && nb.IsLeaf)
        {
            for (var i = na.Start; i < na.Start + na.Count; i++)
            for (var j = nb.Start; j < nb.Start + nb.Count; j++)
                Emit(_items[i], _items[j], onPair);
            return;
        }

        if (nb.IsLeaf || (!na.IsLeaf && na.Box.SurfaceArea >= nb.Box.SurfaceArea))
        {
            CrossPairs(na.Left, b, onPair);
            CrossPairs(na.Right, b, onPair);
        }
        else
        {
            CrossPairs(a, nb.Left, onPair);
            CrossPairs(a, nb.Right, onPair);
        }
    }

    private void Emit(int i, int j, Action<int, int> onPair)
    {
        if (!_boxes[i].Intersects(_boxes[j])) return;
        if (i < j) onPair(i, j);
        else onPair(j, i);
    }
}
