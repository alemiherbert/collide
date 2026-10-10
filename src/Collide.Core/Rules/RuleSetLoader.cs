using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Collide.Serialization;
using Collide.Units;

namespace Collide.Rules;

public sealed class RuleConfigurationException(IReadOnlyList<string> errors)
    : Exception("Invalid rule configuration:\n- " + string.Join("\n- ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>Loads, validates and fingerprints rule configurations.</summary>
public static class RuleSetLoader
{
    public static RuleSet Load(string json)
    {
        RuleSet? rules;
        try
        {
            rules = JsonSerializer.Deserialize<RuleSet>(json, CollideJson.Options);
        }
        catch (JsonException ex)
        {
            throw new RuleConfigurationException([ex.Message]);
        }

        if (rules is null) throw new RuleConfigurationException(["Configuration is empty."]);
        var errors = Validate(rules);
        if (errors.Count > 0) throw new RuleConfigurationException(errors);
        return rules;
    }

    public static RuleSet LoadFile(string path) => Load(File.ReadAllText(path));

    public static string ToJson(RuleSet rules) => JsonSerializer.Serialize(rules, CollideJson.Options);

    /// <summary>Stable fingerprint of a configuration, recorded in every report for reproducibility.</summary>
    public static string Hash(RuleSet rules) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(rules, CollideJson.Create(false)))))[..16];

    public static IReadOnlyList<string> Validate(RuleSet rules)
    {
        var errors = new List<string>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in rules.AllRules())
        {
            if (string.IsNullOrWhiteSpace(rule.Id)) errors.Add("A rule has an empty id.");
            else if (!ids.Add(rule.Id)) errors.Add($"Duplicate rule id '{rule.Id}'.");
            if (string.IsNullOrWhiteSpace(rule.Source?.Document)) errors.Add($"Rule '{rule.Id}' has no source document.");
        }

        foreach (var ex in rules.Exceptions)
        {
            if (!ids.Add(ex.Id)) errors.Add($"Duplicate id '{ex.Id}' (exception).");
            if (ex.BarIds.Count == 0) errors.Add($"Exception '{ex.Id}' must name at least one bar.");
            if (string.IsNullOrWhiteSpace(ex.ApprovalReference)) errors.Add($"Exception '{ex.Id}' has no approval reference.");
        }

        void Positive(Length value, string what)
        {
            if (!(value.Millimetres > 0) || !value.IsFinite) errors.Add($"{what} must be a positive length (got {value}).");
        }

        void NonNegative(Length value, string what)
        {
            if (value.Millimetres < 0 || !value.IsFinite) errors.Add($"{what} must not be negative (got {value}).");
        }

        foreach (var r in rules.Cover) Positive(r.MinimumCover, $"Cover rule '{r.Id}' minimumCover");
        foreach (var r in rules.MaxSpacing) Positive(r.MaximumCentreSpacing, $"Max spacing rule '{r.Id}'");
        foreach (var r in rules.TransverseSpacing)
        {
            Positive(r.MaximumSpacing, $"Transverse rule '{r.Id}' maximumSpacing");
            if (r.Zone == TransverseZone.EndZones && r.EndZoneLength is null) errors.Add($"Transverse rule '{r.Id}' uses EndZones but has no endZoneLength.");
        }

        foreach (var r in rules.ClearSpacing)
        {
            if (r.BarDiameterFactor is null && r.AggregateAllowance is null && r.AggregateFactor is null && r.AbsoluteMinimum is null)
                errors.Add($"Clear spacing rule '{r.Id}' defines no term.");
        }

        foreach (var r in rules.LapLengths.Cast<LengthRequirementRule>().Concat(rules.DevelopmentLengths))
        {
            if (r.DiameterFactor is null && r.Minimum is null && (r.Table is null || r.Table.Count == 0))
                errors.Add($"Length rule '{r.Id}' defines no table, factor or minimum.");
        }

        foreach (var r in rules.Bends)
        {
            if (!(r.MinimumInnerDiameterFactor > 0)) errors.Add($"Bend rule '{r.Id}' needs a positive minimumInnerDiameterFactor.");
        }

        foreach (var r in rules.Congestion)
        {
            Positive(r.CellSize, $"Congestion rule '{r.Id}' cellSize");
            if (!(r.MaximumSteelRatio > 0 && r.MaximumSteelRatio < 1)) errors.Add($"Congestion rule '{r.Id}' maximumSteelRatio must be in (0, 1).");
        }

        foreach (var r in rules.Placement)
        {
            NonNegative(r.MaximumDisplacement, $"Placement rule '{r.Id}' maximumDisplacement");
            if (r.AutoApplyMaximumDisplacement is { } auto && auto > r.MaximumDisplacement)
                errors.Add($"Placement rule '{r.Id}': autoApplyMaximumDisplacement exceeds maximumDisplacement.");
            if (r.AllowedAxes.Count == 0) errors.Add($"Placement rule '{r.Id}' allows no axes.");
        }

        foreach (var r in rules.IntentionalContacts) NonNegative(r.MaximumPenetration, $"Intentional contact rule '{r.Id}' maximumPenetration");

        var t = rules.Tolerances;
        NonNegative(t.ContactTolerance, "tolerances.contactTolerance");
        NonNegative(t.ComplianceTolerance, "tolerances.complianceTolerance");
        Positive(t.ArcChordTolerance, "tolerances.arcChordTolerance");
        Positive(t.DuplicateTolerance, "tolerances.duplicateTolerance");
        Positive(rules.Resolution.DisplacementIncrement, "resolution.displacementIncrement");
        if (rules.Resolution.AmbiguityMargin < 0) errors.Add("resolution.ambiguityMargin must not be negative.");
        return errors;
    }
}
