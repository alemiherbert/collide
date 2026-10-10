using System.Globalization;
using Collide.Findings;
using Collide.Geometry;
using Collide.Model;
using Collide.Rules;

namespace Collide.Analysis;

/// <summary>
/// Local congestion: steel volume / concrete volume in a regular grid of cells over each host (approximate method:
/// centreline length clipped to each cell × bar cross-section area; concrete fraction sampled at 27 points per cell).
/// Exceeding cells are merged into connected zones. A zone containing bars from both beam and column hosts is
/// reported as beam–column joint congestion.
/// </summary>
internal sealed class CongestionAnalyzer
{
    private const int MaxCells = 250_000;
    private readonly PreparedModel _pm;

    public CongestionAnalyzer(PreparedModel pm) => _pm = pm;

    public void Analyze(StructuralHost host, AnalysisSink sink)
    {
        var match = RuleEngine.Find(_pm.Rules.Congestion, RuleContext.ForHost(host));
        if (match is null) return;
        var rule = match.Rule;
        var cell = rule.CellSize.Millimetres;
        var bounds = host.Bounds;
        var size = bounds.Size;
        var nx = Math.Max(1, (int)Math.Ceiling(size.X / cell));
        var ny = Math.Max(1, (int)Math.Ceiling(size.Y / cell));
        var nz = Math.Max(1, (int)Math.Ceiling(size.Z / cell));
        if ((long)nx * ny * nz > MaxCells)
        {
            sink.Findings.Add(FindingFactory.Indeterminate(CheckKind.Congestion, FindingOwner.Host(host.Id), [], [host.Id],
                $"Congestion|{rule.Id}|grid-too-large",
                $"Congestion grid for host {host.Id} would need {(long)nx * ny * nz} cells of {rule.CellSize}; increase the cell size in rule {rule.Id}.",
                ["CongestionRule.CellSize"]) with { Rule = RuleReference.Of(rule) });
            return;
        }

        var steel = new double[nx, ny, nz];
        var barsInCell = new Dictionary<int, SortedSet<string>>();
        var barIds = _pm.BarIndex.Query(bounds);
        foreach (var barId in barIds)
        {
            var solid = _pm.Solids[barId];
            var area = Math.PI * solid.Radius * solid.Radius;
            foreach (var seg in solid.Segments)
            {
                var segBox = Aabb.FromPoints(seg.A, seg.B).Intersection(bounds);
                if (segBox.IsEmpty) continue;
                var (i0, j0, k0) = CellOf(bounds, segBox.Min, cell, nx, ny, nz);
                var (i1, j1, k1) = CellOf(bounds, segBox.Max, cell, nx, ny, nz);
                for (var i = i0; i <= i1; i++)
                for (var j = j0; j <= j1; j++)
                for (var k = k0; k <= k1; k++)
                {
                    var min = bounds.Min + new Vec3(i * cell, j * cell, k * cell);
                    var cellBox = OrientedBox.FromMinMax(min, min + new Vec3(cell, cell, cell));
                    if (!ConvexRegion.FromBox(cellBox).ClipSegment(seg.A, seg.B, out var t0, out var t1)) continue;
                    var length = (t1 - t0) * seg.Length;
                    if (length <= 1e-9) continue;
                    steel[i, j, k] += length * area;
                    var key = Index(i, j, k, ny, nz);
                    if (!barsInCell.TryGetValue(key, out var set)) barsInCell[key] = set = new SortedSet<string>(StringComparer.Ordinal);
                    set.Add(barId);
                }
            }
        }

        var exceeding = new Dictionary<int, double>();
        foreach (var key in barsInCell.Keys)
        {
            var (i, j, k) = Unindex(key, ny, nz);
            var min = bounds.Min + new Vec3(i * cell, j * cell, k * cell);
            var fraction = ConcreteFraction(host, min, cell);
            if (fraction < 0.5) continue;
            var ratio = steel[i, j, k] / (fraction * cell * cell * cell);
            if (ratio > rule.MaximumSteelRatio) exceeding[key] = ratio;
        }

        var visited = new HashSet<int>();
        foreach (var start in exceeding.Keys.OrderBy(k => k))
        {
            if (!visited.Add(start)) continue;
            var component = new List<int> { start };
            var queue = new Queue<int>([start]);
            while (queue.Count > 0)
            {
                var (i, j, k) = Unindex(queue.Dequeue(), ny, nz);
                foreach (var (di, dj, dk) in Neighbours)
                {
                    int a = i + di, b = j + dj, c = k + dk;
                    if (a < 0 || b < 0 || c < 0 || a >= nx || b >= ny || c >= nz) continue;
                    var n = Index(a, b, c, ny, nz);
                    if (exceeding.ContainsKey(n) && visited.Add(n))
                    {
                        component.Add(n);
                        queue.Enqueue(n);
                    }
                }
            }

            var worst = component.OrderByDescending(c => exceeding[c]).ThenBy(c => c).First();
            var (wi, wj, wk) = Unindex(worst, ny, nz);
            var centre = bounds.Min + new Vec3((wi + 0.5) * cell, (wj + 0.5) * cell, (wk + 0.5) * cell);
            var bars = component.SelectMany(c => barsInCell[c]).Distinct().OrderBy(b => b, StringComparer.Ordinal).ToArray();
            var hostCats = bars.Select(b => _pm.Model.FindHost(_pm.Bar(b).HostId)?.Category).Where(c => c is not null).Distinct().ToArray();
            var barHosts = bars.Select(b => _pm.Bar(b).HostId).Where(h => h is not null).Distinct().Count();
            var joint = barHosts >= 2 && hostCats.Contains(HostCategory.Column) && hostCats.Contains(HostCategory.Beam);
            sink.Findings.Add(new Finding
            {
                Check = CheckKind.Congestion,
                Subtype = joint ? IssueSubtype.JointCongestion : IssueSubtype.Congestion,
                Status = FindingStatus.Violation,
                Owner = FindingOwner.Host(host.Id),
                BarIds = bars,
                HostIds = FindingFactory.Sorted(bars.Select(b => _pm.Bar(b).HostId).Append(host.Id)),
                Location = centre,
                Measurements =
                [
                    Measurement.Ratio("maxSteelRatio", exceeding[worst]),
                    Measurement.Ratio("permittedSteelRatio", rule.MaximumSteelRatio),
                    Measurement.Count("congestedCells", component.Count),
                    Measurement.Mm("cellSize", cell),
                ],
                Exceedance = exceeding[worst] - rule.MaximumSteelRatio,
                DecisionMargin = exceeding[worst] - rule.MaximumSteelRatio,
                GeometryApproximate = true,
                Rule = RuleReference.Of(rule),
                Discriminator = worst.ToString(CultureInfo.InvariantCulture),
                Notes = ["Approximate method: clipped centreline length × bar area per cell; concrete fraction from 27-point sampling."],
                Explanation = string.Create(CultureInfo.InvariantCulture,
                    $"Local steel ratio reaches {exceeding[worst]:P1} in {component.Count} cell(s) of {cell:0} mm (limit {rule.MaximumSteelRatio:P1}, rule {rule.Id}: {rule.Source}). {(joint ? "Beam and column bars meet here (joint congestion); " : "")}Concrete placement and compaction may be impaired."),
            });
        }
    }

    private static readonly (int, int, int)[] Neighbours = [(1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1)];

    private static int Index(int i, int j, int k, int ny, int nz) => (i * ny + j) * nz + k;

    private static (int, int, int) Unindex(int key, int ny, int nz) => (key / (ny * nz), key / nz % ny, key % nz);

    private static (int, int, int) CellOf(Aabb bounds, Vec3 p, double cell, int nx, int ny, int nz) => (
        Math.Clamp((int)Math.Floor((p.X - bounds.Min.X) / cell), 0, nx - 1),
        Math.Clamp((int)Math.Floor((p.Y - bounds.Min.Y) / cell), 0, ny - 1),
        Math.Clamp((int)Math.Floor((p.Z - bounds.Min.Z) / cell), 0, nz - 1));

    private static double ConcreteFraction(StructuralHost host, Vec3 min, double cell)
    {
        var inside = 0;
        for (var a = 0; a < 3; a++)
        for (var b = 0; b < 3; b++)
        for (var c = 0; c < 3; c++)
        {
            var p = min + new Vec3((a + 0.5) * cell / 3, (b + 0.5) * cell / 3, (c + 0.5) * cell / 3);
            if (host.Region.Contains(p) && !host.Voids.Any(v => v.Box.Contains(p))) inside++;
        }

        return inside / 27.0;
    }
}
