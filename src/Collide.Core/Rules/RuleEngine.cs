namespace Collide.Rules;

/// <summary>Result of selecting the applicable rule for one or more contexts.</summary>
public sealed record RuleMatch<T>(T Rule, int Candidates) where T : RuleBase;

/// <summary>
/// Deterministic rule selection: among rules whose scope matches every supplied context, the most specific wins;
/// ties are broken by declaration order. Returns null when no rule applies (callers then report indeterminate results
/// for required checks rather than assuming compliance).
/// </summary>
public static class RuleEngine
{
    public static RuleMatch<T>? Find<T>(IReadOnlyList<T> rules, IReadOnlyList<RuleContext> contexts, Func<T, bool>? extraFilter = null)
        where T : RuleBase
    {
        T? best = null;
        var bestSpecificity = int.MinValue;
        var count = 0;
        foreach (var rule in rules)
        {
            if (extraFilter is not null && !extraFilter(rule)) continue;
            if (!contexts.All(rule.Scope.Matches)) continue;
            count++;
            var s = rule.Scope.Specificity;
            if (s > bestSpecificity)
            {
                best = rule;
                bestSpecificity = s;
            }
        }

        return best is null ? null : new RuleMatch<T>(best, count);
    }

    public static RuleMatch<T>? Find<T>(IReadOnlyList<T> rules, RuleContext context, Func<T, bool>? extraFilter = null) where T : RuleBase =>
        Find(rules, [context], extraFilter);
}
