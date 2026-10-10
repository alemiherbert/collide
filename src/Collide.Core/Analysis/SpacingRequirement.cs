using Collide.Model;
using Collide.Rules;

namespace Collide.Analysis;

/// <summary>Required clear spacing between two parallel bars (shared by detection and resolution so they agree exactly).</summary>
internal readonly record struct SpacingRequirement(ClearSpacingRule? Rule, double? Required, string GoverningTerm, bool AggregateGoverns)
{
    public static SpacingRequirement Evaluate(PreparedModel pm, RebarBar a, RebarBar b, StructuralHost? hostA, StructuralHost? hostB)
    {
        var sameLayer = a.Layer is null || b.Layer is null || a.Layer == b.Layer;
        var differentLayers = a.Layer is not null && b.Layer is not null && a.Layer != b.Layer;
        var match = RuleEngine.Find(pm.Rules.ClearSpacing, [FindingFactory.Ctx(pm, a, hostA), FindingFactory.Ctx(pm, b, hostB)], r =>
            r.Relation == SpacingRelation.Any || (r.Relation == SpacingRelation.SameLayer && sameLayer) || (r.Relation == SpacingRelation.BetweenLayers && differentLayers));
        if (match is null) return new SpacingRequirement(null, null, "", false);

        var rule = match.Rule;
        var d = Math.Max(a.NominalDiameter.Millimetres, b.NominalDiameter.Millimetres);
        var aggregate = hostA?.MaximumAggregateSize ?? pm.Rules.Project.MaximumAggregateSize;
        var terms = new List<(string Name, double Value, bool Aggregate)>();
        if (rule.BarDiameterFactor is { } k) terms.Add(($"{k:0.##}·d", k * d, false));
        if (rule.AggregateAllowance is not null || rule.AggregateFactor is not null)
        {
            if (aggregate is null) return new SpacingRequirement(rule, null, "aggregate size missing", true);
            if (rule.AggregateAllowance is { } allowance) terms.Add(("aggregate + allowance", aggregate.Value.Millimetres + allowance.Millimetres, true));
            if (rule.AggregateFactor is { } f) terms.Add(($"{f:0.##}·aggregate", f * aggregate.Value.Millimetres, true));
        }

        if (rule.AbsoluteMinimum is { } abs) terms.Add(("absolute minimum", abs.Millimetres, false));
        // On a tie the non-aggregate term governs (the aggregate term is reported only when it alone controls).
        var gov = terms.OrderByDescending(t => t.Value).ThenBy(t => t.Aggregate).First();
        return new SpacingRequirement(rule, gov.Value, gov.Name, gov.Aggregate);
    }
}
