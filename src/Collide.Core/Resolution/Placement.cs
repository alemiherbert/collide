using Collide.Analysis;
using Collide.Geometry;
using Collide.Model;
using Collide.Rules;

namespace Collide.Resolution;

/// <summary>Evaluates placement-tolerance rules: which bars may move, along which axes and how far.</summary>
internal static class Placement
{
    public static PlacementToleranceRule? RuleFor(PreparedModel pm, RebarBar bar) =>
        bar.IsDesignLocked ? null : RuleEngine.Find(pm.Rules.Placement, RuleContext.ForBar(bar, pm.Model.FindHost(bar.HostId)))?.Rule;

    public static IReadOnlyList<Vec3> AllowedAxes(PlacementToleranceRule rule, StructuralHost? host)
    {
        IReadOnlyList<Vec3> frame = rule.Frame == AxisFrame.Host && host?.LocalAxes is { Count: 3 } local
            ? local
            : [Vec3.UnitX, Vec3.UnitY, Vec3.UnitZ];
        return rule.AllowedAxes.Select(a => frame[(int)a].Normalized()).ToArray();
    }

    /// <summary>Projects a direction onto the subspace the rule allows for this bar.</summary>
    public static Vec3 Project(Vec3 d, PlacementToleranceRule rule, StructuralHost? host, RebarBar bar)
    {
        var p = Vec3.Zero;
        foreach (var axis in AllowedAxes(rule, host)) p += axis * Vec3.Dot(d, axis);
        if (!rule.AllowAlongBarAxis && bar.Path.IsStraight())
        {
            var axis = bar.Path.PrimaryDirection;
            p -= axis * Vec3.Dot(p, axis);
        }

        return p;
    }

    /// <summary>Null when the translation is permitted, otherwise the reason it is not.</summary>
    public static string? Violation(PreparedModel pm, RebarBar bar, Vec3 translation)
    {
        if (bar.IsDesignLocked) return $"Bar {bar.Id} is design-locked.";
        var rule = RuleFor(pm, bar);
        if (rule is null) return $"No placement-tolerance rule permits moving bar {bar.Id}.";
        var host = pm.Model.FindHost(bar.HostId);
        var length = translation.Length;
        if (length > rule.MaximumDisplacement.Millimetres + 1e-6)
            return $"Move of {length:0.###} mm exceeds the {rule.MaximumDisplacement} placement tolerance of rule {rule.Id} for bar {bar.Id}.";
        var residual = (translation - Project(translation, rule, host, bar)).Length;
        if (residual > 1e-6)
            return $"Move of bar {bar.Id} has a {residual:0.###} mm component along a direction rule {rule.Id} does not allow (allowed axes {string.Join("/", rule.AllowedAxes)}{(rule.AllowAlongBarAxis ? "" : ", not along the bar axis")}).";
        if (rule.PermittedZoneIds is { Count: > 0 } zoneIds)
        {
            var moved = bar.Path.Translate(translation);
            var points = moved.Tessellate(pm.Rules.Tolerances.ArcChordTolerance.Millimetres).SelectMany(s => new[] { s.A, s.B }).ToArray();
            var inZone = zoneIds.Select(pm.Model.Zones.GetValueOrDefault).Where(z => z is not null)
                .Any(z => points.All(p => z!.Box.Contains(p, 1e-6)));
            if (!inZone) return $"Moved bar {bar.Id} would leave its permitted adjustment zone(s) {string.Join(", ", zoneIds)}.";
        }

        return null;
    }

    public static bool RequiresApproval(PreparedModel pm, RebarBar bar, Vec3 translation, out string? reason)
    {
        reason = null;
        var rule = RuleFor(pm, bar);
        if (rule?.AutoApplyMaximumDisplacement is { } auto && translation.Length > auto.Millimetres + 1e-6)
        {
            reason = $"Move of {translation.Length:0.#} mm for bar {bar.Id} exceeds the auto-apply limit {auto} of rule {rule.Id}.";
            return true;
        }

        return false;
    }
}
