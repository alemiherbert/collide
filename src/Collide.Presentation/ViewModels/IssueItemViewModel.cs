using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Collide.Findings;

namespace Collide.Presentation.ViewModels;

/// <summary>One row of the clash list.</summary>
public sealed partial class IssueItemViewModel : ObservableObject
{
    [ObservableProperty]
    private Issue _issue;

    [ObservableProperty]
    private bool _isNew;

    [ObservableProperty]
    private bool _isStale;

    public IssueItemViewModel(Issue issue) => _issue = issue;

    public string Id => Issue.Id;
    public Severity Severity => Issue.Severity;
    public string SeverityText => Issue.Severity.ToString();
    public string Category => Humanize(Issue.Category.ToString());
    public string Title => Humanize(Issue.Subtype.ToString());
    public string Status => Humanize(Issue.Status.ToString());
    public string Confidence => Issue.Confidence.ToString();
    public string Explanation => Issue.Explanation;
    public string RecommendedAction => Issue.RecommendedAction;
    public bool AutoResolutionPermitted => Issue.AutoResolutionPermitted;
    public bool RequiresReview => Issue.RequiresEngineeringReview;
    public string LevelOrZone => Issue.LevelOrZone ?? "";
    public IReadOnlyList<string> ElementIds => Issue.ElementIds.ToArray();
    public IReadOnlyList<string> BarElementIds => Issue.Bars.Select(b => b.ElementId).Distinct().ToArray();
    public string Hosts => string.Join(", ", Issue.Hosts.Select(h => h.Name ?? h.Id));
    public string HostKey => Issue.Hosts.Count == 0 ? "(no host)" : string.Join(" / ", Issue.Hosts.Select(h => h.Name ?? h.Id));

    public string Bars => Issue.Bars.Count == 0
        ? (Issue.AffectedBarCount > 0 ? $"{Issue.AffectedBarCount} bar(s)" : "")
        : string.Join(", ", Issue.Bars.Take(6).Select(b => $"{b.Mark ?? b.ElementId} Ø{b.NominalDiameterMm.ToString("0.#", CultureInfo.InvariantCulture)}"))
          + (Issue.AffectedBarCount > 6 ? $" +{Issue.AffectedBarCount - 6}" : "");

    public string Measurements => string.Join("; ", Issue.Measurements.Where(m => m.Unit != "tag" && m.Unit != "id").Select(m => m.ToString()));

    partial void OnIssueChanged(Issue value) => OnPropertyChanged(string.Empty);

    internal static string Humanize(string pascal)
    {
        var chars = new List<char>(pascal.Length + 8);
        for (var i = 0; i < pascal.Length; i++)
        {
            if (i > 0 && char.IsUpper(pascal[i]) && !char.IsUpper(pascal[i - 1])) chars.Add(' ');
            chars.Add(i == 0 ? pascal[i] : char.ToLowerInvariant(pascal[i]));
        }

        return new string(chars.ToArray());
    }
}

public sealed record IssueGroupViewModel(string Key, int Count, IReadOnlyList<IssueItemViewModel> Items);

/// <summary>A proposed correction shown for engineering approval.</summary>
public sealed record CorrectionItemViewModel(
    string IssueId,
    string CandidateId,
    string Description,
    string Strategy,
    double Score,
    double MaxDisplacementMm,
    bool RequiresApproval,
    string Notes,
    IReadOnlyList<string> Invariants,
    bool IsRecommended);
