using Collide.Geometry;
using Collide.Spatial;

namespace Collide.Core.Tests.Geometry;

public class SpatialIndexTests
{
    private static Aabb RandomBox(Random rng)
    {
        var c = KernelTests.RandomPoint(rng, 1000);
        var s = new Vec3(rng.NextDouble() * 80, rng.NextDouble() * 80, rng.NextDouble() * 80);
        return new Aabb(c - s, c + s);
    }

    [Fact]
    public void AllPairsMatchBruteForceExactlyOnce()
    {
        var rng = new Random(2024);
        var boxes = Enumerable.Range(0, 1500).Select(_ => RandomBox(rng)).ToArray();
        var bvh = new BoundingVolumeHierarchy(boxes);

        var found = new List<(int, int)>();
        bvh.QueryAllPairs((i, j) => found.Add((i, j)));

        var expected = new HashSet<(int, int)>();
        for (var i = 0; i < boxes.Length; i++)
        for (var j = i + 1; j < boxes.Length; j++)
            if (boxes[i].Intersects(boxes[j])) expected.Add((i, j));

        Assert.Equal(expected.Count, found.Count); // no duplicates
        Assert.True(expected.SetEquals(found));
    }

    [Fact]
    public void QueryMatchesBruteForce()
    {
        var rng = new Random(5);
        var boxes = Enumerable.Range(0, 800).Select(_ => RandomBox(rng)).ToArray();
        var bvh = new BoundingVolumeHierarchy(boxes, leafSize: 2);
        for (var q = 0; q < 50; q++)
        {
            var query = RandomBox(rng).Inflate(50);
            var hits = new List<int>();
            bvh.Query(query, hits);
            var expected = Enumerable.Range(0, boxes.Length).Where(i => boxes[i].Intersects(query)).ToHashSet();
            Assert.True(expected.SetEquals(hits));
        }
    }

    [Fact]
    public void SnapshotUpdatesAreEquivalentToRebuilding()
    {
        var rng = new Random(77);
        var current = new Dictionary<string, Aabb>();
        for (var i = 0; i < 400; i++) current[$"bar-{i:D4}"] = RandomBox(rng);
        var snapshot = SpatialIndexSnapshot.Build(current);

        for (var step = 0; step < 30; step++)
        {
            var upserts = new List<KeyValuePair<string, Aabb>>();
            var removals = new List<string>();
            var touched = new HashSet<string>();
            for (var k = 0; k < 10; k++)
            {
                var key = $"bar-{rng.Next(0, 480):D4}";
                if (!touched.Add(key)) continue;
                if (rng.NextDouble() < 0.3)
                {
                    removals.Add(key);
                    current.Remove(key);
                }
                else
                {
                    var box = RandomBox(rng);
                    upserts.Add(new KeyValuePair<string, Aabb>(key, box));
                    current[key] = box;
                }
            }

            snapshot = snapshot.Apply(upserts, removals);

            Assert.Equal(current.Count, snapshot.Count);
            var query = RandomBox(rng).Inflate(200);
            var expected = current.Where(kv => kv.Value.Intersects(query)).Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal);
            Assert.Equal(expected, snapshot.Query(query));
        }
    }

    [Fact]
    public void BuildIsDeterministic()
    {
        var rng = new Random(1);
        var boxes = Enumerable.Range(0, 300).Select(_ => RandomBox(rng)).ToArray();
        var run1 = new List<(int, int)>();
        var run2 = new List<(int, int)>();
        new BoundingVolumeHierarchy(boxes).QueryAllPairs((i, j) => run1.Add((i, j)));
        new BoundingVolumeHierarchy(boxes).QueryAllPairs((i, j) => run2.Add((i, j)));
        Assert.Equal(run1, run2);
    }
}
