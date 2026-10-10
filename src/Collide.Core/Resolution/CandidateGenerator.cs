using System.Globalization;
using Collide.Analysis;
using Collide.Findings;
using Collide.Geometry;
using Collide.Model;

namespace Collide.Resolution;

/// <summary>
/// Generates candidate corrections for an issue. Only rigid translations within placement tolerances are proposed:
/// bars are never deleted, shortened, re-sized or re-shaped. Magnitudes are the smallest multiple of the displacement
/// increment that reaches the target separation for the pair (or the required cover), so candidates are minimal.
/// Whether a candidate is safe is decided afterwards by <see cref="ResolutionValidator"/>.
/// </summary>
internal sealed class CandidateGenerator
{
    private readonly PreparedModel _pm;
    private readonly double _inc;
    private readonly double _ct;

    public CandidateGenerator(PreparedModel pm)
    {
        _pm = pm;
        _inc = pm.Rules.Resolution.DisplacementIncrement.Millimetres;
        _ct = pm.Rules.Tolerances.ContactTolerance.Millimetres;
    }

    public IReadOnlyList<CandidateCorrection> Generate(Issue issue, Finding target)
    {
        var raw = new List<(ResolutionStrategy Strategy, string Description, List<(string BarId, Vec3 T)> Moves, Vec3 Dir)>();
        if (target.Contact is { } contact && _pm.Model.FindBar(contact.FirstBarId) is { } first && _pm.Model.FindBar(contact.SecondBarId) is { } second)
            GeneratePair(target, first, second, contact.SeparationDirection, raw);
        else if (target.FaceNormal is { } normal && target.BarIds.Count == 1 && _pm.Model.FindBar(target.BarIds[0]) is { } bar)
            GenerateCover(target, bar, normal, raw);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<CandidateCorrection>();
        foreach (var c in raw)
        {
            var key = string.Join(";", c.Moves.OrderBy(m => m.BarId, StringComparer.Ordinal)
                .Select(m => string.Create(CultureInfo.InvariantCulture, $"{m.BarId}:{m.T.X:0.######},{m.T.Y:0.######},{m.T.Z:0.######}")));
            if (!seen.Add(key)) continue;
            var mods = c.Moves.OrderBy(m => m.BarId, StringComparer.Ordinal).Select(m =>
            {
                var bar = _pm.Bar(m.BarId);
                return new BarModification(m.BarId, m.T, bar.GeometryHash, bar.Translate(m.T).GeometryHash);
            }).ToArray();
            result.Add(new CandidateCorrection
            {
                Id = $"{issue.Id}-C{result.Count + 1:D2}",
                IssueId = issue.Id,
                Strategy = c.Strategy,
                Description = c.Description,
                Modifications = mods,
                Direction = c.Dir,
            });
            if (result.Count >= _pm.Rules.Resolution.MaxCandidatesPerIssue) break;
        }

        return result;
    }

    // ------------------------------------------------------------------ pair issues (clash, spacing, lap offset)

    private void GeneratePair(Finding target, RebarBar first, RebarBar second, Vec3 separation, List<(ResolutionStrategy, string, List<(string, Vec3)>, Vec3)> raw)
    {
        var goal = PairTarget(target, first, second);
        if (goal is null) return;
        var sa = _pm.Solids[first.Id];
        var sb = _pm.Solids[second.Id];

        foreach (var (mover, other, away) in new[] { (first, second, separation), (second, first, -separation) })
        {
            var rule = Placement.RuleFor(_pm, mover);
            if (rule is null) continue;
            var moverSolid = mover.Id == first.Id ? sa : sb;
            var otherSolid = mover.Id == first.Id ? sb : sa;
            foreach (var dir in Directions(mover, away))
            {
                var s = MinimalDisplacement(moverSolid, otherSolid, dir, rule.MaximumDisplacement.Millimetres, goal.Value);
                if (s is null) continue;
                raw.Add((ResolutionStrategy.TranslateSingle, Describe(mover, dir, s.Value), [(mover.Id, dir * s.Value)], dir));
                AddRespacing(mover, dir, s.Value, raw);
            }
        }

        // Split the separation between both bars when both may move along the same line.
        var ruleA = Placement.RuleFor(_pm, first);
        var ruleB = Placement.RuleFor(_pm, second);
        if (ruleA is null || ruleB is null) return;
        foreach (var dir in Directions(first, separation))
        {
            var dirB = Placement.Project(-dir, ruleB, _pm.Model.FindHost(second.HostId), second);
            if ((dirB + dir).Length > 1e-6) continue;
            var max = 2 * Math.Min(ruleA.MaximumDisplacement.Millimetres, ruleB.MaximumDisplacement.Millimetres);
            for (var s = 2 * _inc; s <= max + 1e-9; s += 2 * _inc)
            {
                if (PairDistance(sa, dir * (s / 2), sb, -dir * (s / 2)) < goal.Value - 1e-9) continue;
                raw.Add((ResolutionStrategy.TranslateBoth, $"Move {first.Id} and {second.Id} apart by {s / 2:0.#} mm each.", [(first.Id, dir * (s / 2)), (second.Id, -dir * (s / 2))], dir));
                break;
            }
        }
    }

    /// <summary>Surface separation the pair must reach.</summary>
    private double? PairTarget(Finding target, RebarBar a, RebarBar b)
    {
        switch (target.Subtype)
        {
            case IssueSubtype.InsufficientClearSpacing or IssueSubtype.InsufficientAggregatePassage:
                return target.Measurements.FirstOrDefault(m => m.Name == "requiredClearSpacing")?.Value;
            case IssueSubtype.LapGeometryConflict:
                return 0;
        }

        var parallel = target.Contact!.AngleDegrees <= _pm.Rules.Tolerances.ParallelAngleTolerance.Degrees;
        if (parallel)
        {
            var req = SpacingRequirement.Evaluate(_pm, a, b, _pm.Model.FindHost(a.HostId), _pm.Model.FindHost(b.HostId));
            return req.Rule is null ? _ct + _inc : req.Required;
        }

        // Crossing bars: touching is enough if an intentional-contact rule could accept it, otherwise clear the contact band.
        var mayTouch = _pm.Rules.IntentionalContacts.Any(r =>
            (r.RolesA is null || r.RolesA.Contains(a.Role) || r.RolesA.Contains(b.Role)) &&
            (!r.RequireDifferentLayers || (a.Layer is not null && b.Layer is not null && a.Layer != b.Layer)));
        return mayTouch ? 0 : _ct + _inc;
    }

    private void AddRespacing(RebarBar mover, Vec3 dir, double s, List<(ResolutionStrategy, string, List<(string, Vec3)>, Vec3)> raw)
    {
        if (!_pm.SetsByBar.TryGetValue(mover.Id, out var setIds)) return;
        foreach (var setId in setIds)
        {
            if (!_pm.Model.Sets.TryGetValue(setId, out var set) || set.BarIds.Count < 3) continue;
            var bars = set.BarIds.Select(_pm.Model.FindBar).Where(b => b is not null).Select(b => b!).ToArray();
            var centres = bars.ToDictionary(b => b.Id, b => b.Path.Bounds.Center, StringComparer.Ordinal);
            var dist = (set.DistributionDirection ?? (centres[bars[^1].Id] - centres[bars[0].Id])).Normalized();
            if (dist == Vec3.Zero || Math.Abs(Vec3.Dot(dist, dir)) < 0.999) continue;
            var ordered = bars.OrderBy(b => Vec3.Dot(centres[b.Id], dist)).ThenBy(b => b.Id, StringComparer.Ordinal).ToArray();
            var k = Array.FindIndex(ordered, b => b.Id == mover.Id);
            for (var w = 1; w <= 2; w++)
            {
                var moves = new List<(string, Vec3)>();
                var ok = true;
                for (var i = -w; i <= w && ok; i++)
                {
                    var idx = k + i;
                    if (idx < 0 || idx >= ordered.Length) continue;
                    var shift = s * (1 - Math.Abs(i) / (double)(w + 1));
                    var t = dir * shift;
                    if (Placement.Violation(_pm, ordered[idx], t) is not null) ok = false;
                    else moves.Add((ordered[idx].Id, t));
                }

                if (!ok || moves.Count < 2) continue;
                raw.Add((ResolutionStrategy.LocalRespacing,
                    $"Shift {mover.Id} by {s:0.#} mm along the distribution of set {set.Id}, tapering over {w} neighbour(s) each side (bar count preserved).", moves, dir));
            }
        }
    }

    // ------------------------------------------------------------------ cover issues

    private void GenerateCover(Finding target, RebarBar bar, Vec3 outward, List<(ResolutionStrategy, string, List<(string, Vec3)>, Vec3)> raw)
    {
        var provided = target.Measurements.FirstOrDefault(m => m.Name == "providedCover")?.Value;
        var required = target.Measurements.FirstOrDefault(m => m.Name == "requiredCover")?.Value;
        if (provided is null || required is null) return;
        var deficit = required.Value - provided.Value;
        var rule = Placement.RuleFor(_pm, bar);
        if (rule is null) return;
        foreach (var dir in Directions(bar, -outward))
        {
            var along = Vec3.Dot(dir, -outward);
            if (along < 0.2) continue;
            var s = Math.Ceiling((deficit / along - 1e-9) / _inc) * _inc;
            if (s > rule.MaximumDisplacement.Millimetres + 1e-9) continue;
            raw.Add((ResolutionStrategy.TranslateSingle, Describe(bar, dir, s) + " to restore cover.", [(bar.Id, dir * s)], dir));
        }
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Unit directions the bar may move in that separate it along <paramref name="away"/>.</summary>
    private IReadOnlyList<Vec3> Directions(RebarBar bar, Vec3 away)
    {
        var rule = Placement.RuleFor(_pm, bar);
        if (rule is null) return [];
        var host = _pm.Model.FindHost(bar.HostId);
        var candidates = new List<Vec3>();
        var projected = Placement.Project(away, rule, host, bar);
        if (projected.Length > 0.1) candidates.Add(projected.Normalized());
        foreach (var axis in Placement.AllowedAxes(rule, host))
        {
            foreach (var signed in new[] { axis, -axis })
            {
                var d = Placement.Project(signed, rule, host, bar);
                if (d.Length < 0.5) continue;
                d = d.Normalized();
                if (Vec3.Dot(d, away) > 0.2) candidates.Add(d);
            }
        }

        var unique = new List<Vec3>();
        foreach (var c in candidates)
        {
            if (unique.All(u => Vec3.Dot(u, c) < 0.9999)) unique.Add(c);
        }

        return unique;
    }

    private double? MinimalDisplacement(BarSolid mover, BarSolid other, Vec3 dir, double max, double goal)
    {
        var steps = (int)Math.Floor(max / _inc + 1e-9);
        for (var k = 1; k <= steps; k++)
        {
            var s = k * _inc;
            if (PairDistance(mover, dir * s, other, Vec3.Zero) >= goal - 1e-9) return s;
        }

        return null;
    }

    private double PairDistance(BarSolid a, Vec3 ta, BarSolid b, Vec3 tb)
    {
        var min = double.PositiveInfinity;
        foreach (var sa in a.Segments)
        {
            var ma = Move(sa, ta);
            foreach (var sb in b.Segments)
            {
                var d = _pm.Kernel.Measure(ma, Move(sb, tb)).SurfaceDistance;
                if (d < min) min = d;
            }
        }

        return min;
    }

    private static SolidSegment Move(in SolidSegment s, Vec3 t) =>
        t == Vec3.Zero ? s : new SolidSegment(s.A + t, s.B + t, s.Radius, s.ElementIndex, s.Kind, s.ChordError, s.FreeStart, s.FreeEnd);

    private static string Describe(RebarBar bar, Vec3 dir, double s) =>
        string.Create(CultureInfo.InvariantCulture, $"Move {bar.Id} by {s:0.#} mm along ({dir.X:0.###}, {dir.Y:0.###}, {dir.Z:0.###})");
}
