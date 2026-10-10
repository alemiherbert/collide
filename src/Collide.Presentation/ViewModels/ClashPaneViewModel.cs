using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Collide.Findings;
using Collide.Monitoring;
using Collide.Resolution;

namespace Collide.Presentation.ViewModels;

public enum IssueGrouping
{
    None,
    Severity,
    Category,
    Host,
    Type,
}

/// <summary>
/// View model of the dockable clash pane. Updates arrive from the live monitor on a background thread and are marshalled
/// to the UI through <see cref="IUiDispatcher"/>; the pane refreshes automatically without a manual scan.
/// </summary>
public sealed partial class ClashPaneViewModel : ObservableObject, IDisposable
{
    private readonly ILiveMonitor _monitor;
    private readonly IModelInteraction _interaction;
    private readonly IUiDispatcher _dispatcher;
    private readonly Dictionary<string, IssueItemViewModel> _items = new(StringComparer.Ordinal);
    private MonitorUpdate? _last;

    public ClashPaneViewModel(ILiveMonitor monitor, IModelInteraction interaction, IUiDispatcher dispatcher)
    {
        _monitor = monitor;
        _interaction = interaction;
        _dispatcher = dispatcher;
        _monitor.Updated += OnMonitorUpdated;
    }

    public ObservableCollection<IssueItemViewModel> Issues { get; } = [];
    public ObservableCollection<IssueGroupViewModel> Groups { get; } = [];
    public ObservableCollection<CorrectionItemViewModel> Corrections { get; } = [];
    public ObservableCollection<string> RecentlyResolved { get; } = [];

    public IReadOnlyList<Severity> SeverityOptions { get; } = Enum.GetValues<Severity>().OrderByDescending(s => s).ToArray();
    public IReadOnlyList<IssueGrouping> GroupingOptions { get; } = Enum.GetValues<IssueGrouping>();
    public IReadOnlyList<MonitoringScopeKind> ScopeOptions { get; } = Enum.GetValues<MonitoringScopeKind>();

    [ObservableProperty] private MonitorStatus _status = MonitorStatus.Stopped;
    [ObservableProperty] private string _statusText = "Not started";
    [ObservableProperty] private string _validatedText = "No validated results yet";
    [ObservableProperty] private int _criticalCount;
    [ObservableProperty] private int _highCount;
    [ObservableProperty] private int _otherCount;
    [ObservableProperty] private int _newCount;
    [ObservableProperty] private int _resolvedCount;
    [ObservableProperty] private int _staleCount;
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private string? _lastError;
    [ObservableProperty] private string? _actionMessage;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private Severity _minimumSeverity = Severity.Informational;
    [ObservableProperty] private IssueGrouping _grouping = IssueGrouping.Severity;
    [ObservableProperty] private MonitoringScopeKind _scopeKind = MonitoringScopeKind.EntireModel;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SelectElementsCommand), nameof(ZoomToElementsCommand), nameof(InspectCorrectionsCommand))]
    private IssueItemViewModel? _selectedIssue;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCorrectionCommand))]
    private CorrectionItemViewModel? _selectedCorrection;

    partial void OnMinimumSeverityChanged(Severity value) => RebuildList();
    partial void OnGroupingChanged(IssueGrouping value) => RebuildList();

    partial void OnScopeKindChanged(MonitoringScopeKind value)
    {
        if (_monitor.Scope.Kind != value) _monitor.SetScope(new MonitoringScope(value));
    }

    partial void OnSelectedIssueChanged(IssueItemViewModel? value)
    {
        Corrections.Clear();
        SelectedCorrection = null;
    }

    private void OnMonitorUpdated(object? sender, MonitorUpdate update) => _dispatcher.Post(() => Apply(update));

    /// <summary>Applies a monitor update to the pane state (UI thread).</summary>
    internal void Apply(MonitorUpdate update)
    {
        _last = update;
        Status = update.Status;
        IsPaused = update.Status == MonitorStatus.Paused;
        LastError = update.Error;
        StatusText = update.Status switch
        {
            MonitorStatus.Idle => "Monitoring — up to date",
            MonitorStatus.Pending => $"Changes detected ({update.PendingElementCount} element(s)) — waiting to recalculate",
            MonitorStatus.Extracting => "Reading changed elements",
            MonitorStatus.Analyzing => "Recalculating affected region",
            MonitorStatus.Paused => "Paused — changes are recorded and processed on resume",
            MonitorStatus.Error => $"Error — {update.Error}",
            MonitorStatus.Initializing => "Initial scan",
            _ => update.Status.ToString(),
        };
        if (update.ValidatedAt is { } at)
            ValidatedText = string.Create(CultureInfo.InvariantCulture, $"Validated revision {update.ValidatedRevision} at {at.ToLocalTime():HH:mm:ss}{(update.IncludesSelfChanges ? " (after correction)" : "")}");

        if (update.IsValidatedResult)
        {
            var present = update.Issues.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var id in _items.Keys.Where(k => !present.Contains(k)).ToArray())
            {
                RecentlyResolved.Insert(0, $"{_items[id].Title} — {_items[id].Bars}");
                _items.Remove(id);
            }

            while (RecentlyResolved.Count > 20) RecentlyResolved.RemoveAt(RecentlyResolved.Count - 1);
            var newIds = update.NewIssueIds.ToHashSet(StringComparer.Ordinal);
            foreach (var issue in update.Issues)
            {
                if (_items.TryGetValue(issue.Id, out var item)) item.Issue = issue;
                else _items[issue.Id] = item = new IssueItemViewModel(issue);
                item.IsNew = newIds.Contains(issue.Id) && !update.FullScan;
            }

            NewCount = update.FullScan ? 0 : update.NewIssueIds.Count;
            ResolvedCount = update.ResolvedIssueIds.Count;
        }

        var stale = update.StaleIssueIds.ToHashSet(StringComparer.Ordinal);
        foreach (var item in _items.Values) item.IsStale = stale.Contains(item.Id);
        StaleCount = stale.Count;
        CriticalCount = update.CriticalCount;
        HighCount = update.HighCount;
        OtherCount = update.OtherWarningCount;
        RebuildList();
    }

    private void RebuildList()
    {
        var selectedId = SelectedIssue?.Id;
        var visible = _items.Values
            .Where(i => i.Severity >= MinimumSeverity)
            .OrderByDescending(i => i.Severity).ThenByDescending(i => i.IsNew).ThenBy(i => i.Title, StringComparer.Ordinal).ThenBy(i => i.Id, StringComparer.Ordinal)
            .ToArray();
        Issues.Clear();
        foreach (var item in visible) Issues.Add(item);

        Groups.Clear();
        if (Grouping != IssueGrouping.None)
        {
            Func<IssueItemViewModel, string> key = Grouping switch
            {
                IssueGrouping.Severity => i => i.SeverityText,
                IssueGrouping.Category => i => i.Category,
                IssueGrouping.Host => i => i.HostKey,
                _ => i => i.Title,
            };
            var groups = visible.GroupBy(key);
            groups = Grouping == IssueGrouping.Severity
                ? groups.OrderByDescending(g => g.First().Severity)
                : groups.OrderBy(g => g.Key, StringComparer.Ordinal);
            foreach (var g in groups) Groups.Add(new IssueGroupViewModel(g.Key, g.Count(), g.ToArray()));
        }

        SelectedIssue = selectedId is not null && _items.TryGetValue(selectedId, out var keep) && visible.Contains(keep) ? keep : null;
    }

    [RelayCommand]
    private void PauseResume()
    {
        var pause = !IsPaused;
        if (pause) _monitor.Pause();
        else _monitor.Resume();
        IsPaused = pause;
    }

    [RelayCommand]
    private void FullScan() => _monitor.RequestFullScan();

    private bool HasSelection() => SelectedIssue is not null;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task SelectElementsAsync() => _interaction.SelectElementsAsync(SelectedIssue!.ElementIds);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task ZoomToElementsAsync() => _interaction.ZoomToElementsAsync(SelectedIssue!.ElementIds);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task InspectCorrectionsAsync()
    {
        var issue = SelectedIssue!;
        IsBusy = true;
        try
        {
            var plan = await _interaction.InspectCorrectionsAsync(issue.Id);
            Corrections.Clear();
            if (plan is null)
            {
                ActionMessage = "The issue is no longer current; wait for the next refresh.";
                return;
            }

            ActionMessage = $"{IssueItemViewModel.Humanize(plan.Decision.ToString())}: {plan.Reason}";
            foreach (var c in plan.Ranked)
            {
                Corrections.Add(new CorrectionItemViewModel(
                    plan.IssueId,
                    c.Candidate.Id,
                    c.Candidate.Description,
                    IssueItemViewModel.Humanize(c.Candidate.Strategy.ToString()),
                    c.Score,
                    c.Candidate.MaxDisplacementMm,
                    c.RequiresApproval,
                    string.Join(" ", c.ApprovalReasons),
                    c.Invariants.Select(i => $"{(i.Passed ? "✓" : "✗")} {i.Id} {i.Name}").ToArray(),
                    ReferenceEquals(c, plan.Selected)));
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanApply() => SelectedCorrection is not null && !IsBusy;

    /// <summary>Applies the engineer-selected correction (explicit approval). The live monitor then re-validates the region.</summary>
    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyCorrectionAsync()
    {
        var c = SelectedCorrection!;
        IsBusy = true;
        try
        {
            var result = await _interaction.ApplyCorrectionAsync(c.IssueId, c.CandidateId);
            ActionMessage = result.Status switch
            {
                ExecutionStatus.Committed => $"Applied {c.CandidateId}; re-validating the affected region.",
                ExecutionStatus.RolledBack => $"Rolled back: {result.Reason}",
                _ => $"{result.Status}: {result.Reason}",
            };
            Corrections.Clear();
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void Dispose() => _monitor.Updated -= OnMonitorUpdated;
}
