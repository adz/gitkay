using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Elmish.Glue.Core;
using GitKay.Core;

namespace GitKay.UI;

internal readonly record struct DiffSearchStatusStyle(IBrush Foreground, IBrush Background);

internal static class DiffSearchStatusBrushes
{
    public static readonly DiffSearchStatusStyle Pending = new(
        new SolidColorBrush(Color.FromRgb(242, 201, 125)),
        new SolidColorBrush(Color.FromArgb(36, 90, 66, 20)));

    public static readonly DiffSearchStatusStyle Applied = new(
        new SolidColorBrush(Color.FromRgb(78, 201, 176)),
        new SolidColorBrush(Color.FromArgb(30, 78, 201, 176)));

    public static readonly DiffSearchStatusStyle Empty = new(
        new SolidColorBrush(Color.FromRgb(215, 186, 125)),
        new SolidColorBrush(Color.FromArgb(28, 76, 58, 18)));
}

public sealed class DiffPresentationModeProjection
{
    public DiffPresentationModeProjection(string key, string label)
    {
        Key = key;
        Label = label;
    }

    public string Key { get; }
    public string Label { get; }
}

public partial class MainProjection : ObservableObject, IProjection<GitKay.Core.App.Model, GitKay.Core.App.Msg>
{
    private readonly long _createdAtTicks = Stopwatch.GetTimestamp();
    private bool _firstPaintLogged;
    private bool _suppressSelectionDispatch;
    private bool _suppressDiffSelectionSync;
    private bool _suppressSearchDispatch;
    private bool _suppressSearchSelectionDispatch;
    private bool _suppressShowBranchRefsDispatch;
    private bool _suppressShowStashesDispatch;
    private object? _commitsSource;
    private object? _commitSearchResultsSource;
    private object? _visibleCommitsSource;
    private string? _selectedDiffHash;
    private object? _selectedDiffFilesSource;
    private DiffFileKey? _selectedDiffFileKey;
    private object? _selectedDiffFileSource;
    private object? _searchResultsSource;
    private string? _selectedSearchResultHash;
    private string? _diffSearchQuery;
    private string? _diffSearchScopeKey;
    private string? _pendingDiffSearchQuery;
    private string? _pendingDiffSearchScopeKey;
    private long _pendingDiffSearchReadyAtTicks;
    private CancellationTokenSource? _searchDebounceCancellation;
    private CancellationTokenSource? _diffSearchDebounceCancellation;
    private bool _searchPanelAutoOpened;

    public ObservableCollection<DiffPresentationModeProjection> DiffPresentationModes { get; } = new()
    {
        new DiffPresentationModeProjection("diff", "Diff"),
        new DiffPresentationModeProjection("side-by-side", "Side-by-side"),
        new DiffPresentationModeProjection("new", "New"),
        new DiffPresentationModeProjection("old", "Old"),
    };

    public ObservableCollection<SearchScopeProjection> SearchScopes { get; } = new()
    {
        new SearchScopeProjection("all", "All"),
        new SearchScopeProjection("hash", "Commit hash"),
        new SearchScopeProjection("message", "Message / subject"),
        new SearchScopeProjection("author", "Author"),
        new SearchScopeProjection("path", "File / path"),
        new SearchScopeProjection("text", "Diff text"),
        new SearchScopeProjection("ref", "Ref / tag / branch"),
    };

    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _commitRowFontFamily = "Helvetica,Arial,Liberation Sans,Noto Sans,sans-serif";
    [ObservableProperty] private string _commitRowMonoFontFamily = "Courier,Courier New,Liberation Mono,Monospace";
    [ObservableProperty] private double _commitRowTextFontSize = 10;
    [ObservableProperty] private double _commitRowMetaFontSize = 10;
    [ObservableProperty] private double _commitRowBadgeFontSize = 10;
    [ObservableProperty] private bool _showBranchRefs;
    [ObservableProperty] private bool _showStashes;
    [ObservableProperty] private string _searchQuery = "";
    [ObservableProperty] private double _searchDebounceSeconds = 0.5;
    [ObservableProperty] private string _commitFindQuery = "";
    [ObservableProperty] private SearchScopeProjection? _selectedSearchScope;
    [ObservableProperty] private bool _hasSearchResults;
    [ObservableProperty] private bool _isSearchPanelExpanded;
    [ObservableProperty] private bool _hasDiffSearchStatus;
    [ObservableProperty] private bool _isDiffSearchPending;
    [ObservableProperty] private string _diffSearchStatusText = "";
    [ObservableProperty] private IBrush _diffSearchStatusForeground = Brushes.Transparent;
    [ObservableProperty] private IBrush _diffSearchStatusBackground = Brushes.Transparent;
    [ObservableProperty] private DiffPresentationModeProjection? _selectedDiffPresentationMode;
    [ObservableProperty] private string _selectedDiffPresentationModeLabel = "Diff";

    public ObservableCollection<CommitProjection> Commits { get; } = new();
    public ObservableCollection<CommitProjection> VisibleCommits { get; } = new();
    public ObservableCollection<SearchResultProjection> SearchResults { get; } = new();
    public ObservableCollection<DiffFileProjection> SelectedDiffFiles { get; } = new();
    public ObservableCollection<IDiffRowProjection> SelectedDiffRows { get; } = new();

    [ObservableProperty] private CommitProjection? _selectedCommit;
    [ObservableProperty] private SearchResultProjection? _selectedSearchResult;
    [ObservableProperty] private DiffFileProjection? _selectedDiffFile;
    [ObservableProperty] private IDiffRowProjection? _selectedDiffRow;

    public MainProjection()
    {
        SelectedDiffPresentationMode = DiffPresentationModes[0];
    }

    private static void LogTiming(string message)
    {
        var line = $"[timing] {message}";
        Trace.WriteLine(line);
        try
        {
            Console.Error.WriteLine(line);
        }
        catch
        {
        }
    }

    public void Update(GitKay.Core.App.Model model)
    {
        var startedAtTicks = Stopwatch.GetTimestamp();
        Status = model.Status;
        if (ShowBranchRefs != model.ShowBranchRefs)
        {
            _suppressShowBranchRefsDispatch = true;
            try
            {
                ShowBranchRefs = model.ShowBranchRefs;
            }
            finally
            {
                _suppressShowBranchRefsDispatch = false;
            }
        }

        if (ShowStashes != model.ShowStashes)
        {
            _suppressShowStashesDispatch = true;
            try
            {
                ShowStashes = model.ShowStashes;
            }
            finally
            {
                _suppressShowStashesDispatch = false;
            }
        }

        UpdateSearchState(model);
        UpdateDiffState(model);
        UpdateCommits(model);
        UpdateSelectedCommit(model);

        var elapsed = Stopwatch.GetElapsedTime(startedAtTicks);
        LogTiming($"ui projection elapsed={elapsed.TotalMilliseconds:F1}ms commits={model.Commits.Length} searchResults={SearchResults.Count} diffFiles={SelectedDiffFiles.Count} diffRows={SelectedDiffRows.Count}");
    }

    private void UpdateSearchState(GitKay.Core.App.Model model)
    {
        var searchResultsSource = model.SearchResults != null ? (object?)model.SearchResults.Value : null;

        if (!string.Equals(SearchQuery, model.SearchQuery, StringComparison.Ordinal))
        {
            _suppressSearchDispatch = true;
            try
            {
                SearchQuery = model.SearchQuery;
            }
            finally
            {
                _suppressSearchDispatch = false;
            }
        }

        var selectedScope = SearchScopes.FirstOrDefault(scope => scope.Key == model.SearchScopeKey) ?? SearchScopes.FirstOrDefault();
        if (!ReferenceEquals(SelectedSearchScope, selectedScope))
        {
            _suppressSearchDispatch = true;
            try
            {
                SelectedSearchScope = selectedScope;
            }
            finally
            {
                _suppressSearchDispatch = false;
            }
        }

        var shouldAutoOpenSearchPanel =
            !string.IsNullOrWhiteSpace(model.SearchQuery)
            || model.SearchResults != null;

        if (shouldAutoOpenSearchPanel)
        {
            IsSearchPanelExpanded = true;
            _searchPanelAutoOpened = true;
        }
        else if (_searchPanelAutoOpened)
        {
            IsSearchPanelExpanded = false;
            _searchPanelAutoOpened = false;
        }

        var searchResultsChanged = !ReferenceEquals(_searchResultsSource, searchResultsSource);

        if (model.SearchResults == null)
        {
            if (SearchResults.Count > 0 || _searchResultsSource != null)
            {
                SyncSelectedSearchResult(null);
                SearchResults.Clear();
            }

            _searchResultsSource = null;
            _selectedSearchResultHash = null;
            HasSearchResults = false;
            return;
        }

        if (searchResultsChanged)
        {
            var previousSelectedSearchHash = SelectedSearchResult?.FullHash ?? _selectedSearchResultHash;
            SyncSelectedSearchResult(null);
            SearchResults.Clear();

            foreach (var result in model.SearchResults.Value)
            {
                var projection = new SearchResultProjection();
                projection.Update(result);
                SearchResults.Add(projection);
            }

            _searchResultsSource = searchResultsSource;
            var selectedSearchResult = ResolveSelectedSearchResult(previousSelectedSearchHash);
            SyncSelectedSearchResult(selectedSearchResult);
            _selectedSearchResultHash = selectedSearchResult?.FullHash;
            HasSearchResults = SearchResults.Count > 0;
        }
    }

    private void UpdateDiffState(GitKay.Core.App.Model model)
    {
        var selectedDiffHash =
            model.SelectedCommitHash != null
            && model.SelectedDiffHash != null
            && model.SelectedCommitHash.Value == model.SelectedDiffHash.Value
            && model.SelectedDiffFiles != null
                ? model.SelectedDiffHash.Value
                : null;

        var selectedDiffFilesSource =
            selectedDiffHash != null && model.SelectedDiffFiles != null
                ? (object?)model.SelectedDiffFiles.Value
                : null;

        var selectedDiffContentSource =
            selectedDiffHash != null && model.SelectedDiff != null
                ? (object?)model.SelectedDiff.Value
                : null;

        var searchQuery = model.SearchQuery.Trim();
        var searchStateChanged =
            !string.Equals(_diffSearchQuery, searchQuery, StringComparison.Ordinal)
            || !string.Equals(_diffSearchScopeKey, model.SearchScopeKey, StringComparison.Ordinal);

        var diffCollectionChanged =
            !string.Equals(_selectedDiffHash, selectedDiffHash, StringComparison.Ordinal)
            || !ReferenceEquals(_selectedDiffFilesSource, selectedDiffFilesSource)
            || !ReferenceEquals(_selectedDiffFileSource, selectedDiffContentSource);

        if (selectedDiffHash == null)
        {
            if (SelectedDiffFiles.Count > 0 || SelectedDiffRows.Count > 0 || _selectedDiffHash != null)
            {
                SyncSelectedDiffSelection(null);
                SelectedDiffFiles.Clear();
                SelectedDiffRows.Clear();
            }

            _selectedDiffHash = null;
            _selectedDiffFilesSource = null;
            _selectedDiffFileKey = null;
            _selectedDiffFileSource = null;
            RefreshDiffSearchState(model, searchQuery, searchStateChanged, false, false);
            _diffSearchQuery = searchQuery;
            _diffSearchScopeKey = model.SearchScopeKey;
            return;
        }

        if (diffCollectionChanged)
        {
            var previousSelectedDiffFileKey = SelectedDiffFile?.Key;

            SyncSelectedDiffSelection(null);
            SelectedDiffRows.Clear();

            if (model.SelectedDiffFiles != null)
            {
                SyncSelectedDiffFiles(model.SelectedDiffFiles.Value, true);
            }

            _selectedDiffHash = selectedDiffHash;
            _selectedDiffFilesSource = selectedDiffFilesSource;

            if (model.SelectedDiff != null)
            {
                SyncSelectedDiffFileContents(model.SelectedDiff.Value);
            }

            _selectedDiffFileSource = selectedDiffContentSource;

            var selectedDiffFile = ResolveSelectedDiffFile(model, previousSelectedDiffFileKey);

            SyncSelectedDiffSelection(selectedDiffFile);
            RenderSelectedDiffRows();
            _selectedDiffFileKey = selectedDiffFile?.Key;
        }

        if (model.SelectedDiffFiles != null)
        {
            var selectedDiffFile = ResolveSelectedDiffFile(model, _selectedDiffFileKey);
            var selectedDiffFileKey = selectedDiffFile?.Key;
            var diffContentChanged = !ReferenceEquals(_selectedDiffFileSource, selectedDiffContentSource);
            var selectionChanged = !Nullable.Equals(_selectedDiffFileKey, selectedDiffFileKey);

            if (diffContentChanged)
            {
                if (model.SelectedDiff != null)
                {
                    SyncSelectedDiffFileContents(model.SelectedDiff.Value);
                }
                else
                {
                    foreach (var file in SelectedDiffFiles)
                    {
                        file.ClearContent();
                    }
                }

                RenderSelectedDiffRows();
                _selectedDiffFileSource = selectedDiffContentSource;
            }

            if (selectionChanged || diffContentChanged || searchStateChanged)
            {
                SyncSelectedDiffSelection(selectedDiffFile);
                _selectedDiffFileKey = selectedDiffFileKey;
            }

            RefreshDiffSearchState(model, searchQuery, searchStateChanged, diffContentChanged, true);
        }
        else
        {
            RefreshDiffSearchState(model, searchQuery, searchStateChanged, diffCollectionChanged, false);
        }

        _diffSearchQuery = searchQuery;
        _diffSearchScopeKey = model.SearchScopeKey;
    }

    private void RefreshDiffSearchState(
        GitKay.Core.App.Model model,
        string searchQuery,
        bool searchStateChanged,
        bool diffContentChanged,
        bool hasSelectedDiff)
    {
        if (string.IsNullOrWhiteSpace(searchQuery))
        {
            CancelDiffSearchDebounce();
            _pendingDiffSearchQuery = null;
            _pendingDiffSearchScopeKey = null;
            _pendingDiffSearchReadyAtTicks = 0;
            IsDiffSearchPending = false;
            SetDiffSearchStatus("", Brushes.Transparent, Brushes.Transparent);

            if (hasSelectedDiff)
            {
                ClearSearchStateToSelectedDiff(model.SearchScopeKey);
            }

            return;
        }

        if (searchStateChanged)
        {
            StartDiffSearchDebounce(model, searchQuery);
            return;
        }

        if (TryApplyPendingDiffSearch(model, searchQuery, hasSelectedDiff))
        {
            return;
        }

        if (hasSelectedDiff && diffContentChanged && !IsDiffSearchPending)
        {
            ApplySearchStateToSelectedDiff(searchQuery, model.SearchScopeKey);
            SetDiffSearchAppliedStatus(searchQuery);
        }
        else if (IsDiffSearchPending)
        {
            SetDiffSearchPendingStatus(searchQuery);
        }
        else if (SelectedDiffFiles.Count > 0)
        {
            SetDiffSearchAppliedStatus(searchQuery);
        }
    }

    private void StartDiffSearchDebounce(GitKay.Core.App.Model model, string searchQuery)
    {
        CancelDiffSearchDebounce();

        _pendingDiffSearchQuery = searchQuery;
        _pendingDiffSearchScopeKey = model.SearchScopeKey;
        _pendingDiffSearchReadyAtTicks = Stopwatch.GetTimestamp() + SecondsToStopwatchTicks(SearchDebounceSeconds);
        IsDiffSearchPending = true;

        if (SelectedDiffFiles.Count > 0)
        {
            ClearSearchStateToSelectedDiff(model.SearchScopeKey);
        }

        SetDiffSearchPendingStatus(searchQuery);

        if (SearchDebounceSeconds > 0d)
        {
            ScheduleDiffSearchDebounce();
        }

        _ = TryApplyPendingDiffSearch(model, searchQuery, true);
    }

    private bool TryApplyPendingDiffSearch(GitKay.Core.App.Model model, string searchQuery, bool hasSelectedDiff)
    {
        if (!IsDiffSearchPending || _pendingDiffSearchQuery == null || _pendingDiffSearchScopeKey == null)
        {
            return false;
        }

        if (!string.Equals(_pendingDiffSearchQuery, searchQuery, StringComparison.Ordinal)
            || !string.Equals(_pendingDiffSearchScopeKey, model.SearchScopeKey, StringComparison.Ordinal))
        {
            return false;
        }

        if (Stopwatch.GetTimestamp() < _pendingDiffSearchReadyAtTicks)
        {
            return false;
        }

        if (!hasSelectedDiff || SelectedDiffFiles.Count == 0)
        {
            SetDiffSearchPendingStatus(searchQuery);
            return false;
        }

        CancelDiffSearchDebounce();
        _pendingDiffSearchQuery = null;
        _pendingDiffSearchScopeKey = null;
        _pendingDiffSearchReadyAtTicks = 0;
        IsDiffSearchPending = false;

        ApplySearchStateToSelectedDiff(searchQuery, model.SearchScopeKey);
        SetDiffSearchAppliedStatus(searchQuery);
        return true;
    }

    private void SetDiffSearchPendingStatus(string searchQuery)
    {
        SetDiffSearchStatus(
            $"Searching diff for \"{searchQuery}\"...",
            DiffSearchStatusBrushes.Pending.Foreground,
            DiffSearchStatusBrushes.Pending.Background);
    }

    private void SetDiffSearchAppliedStatus(string searchQuery)
    {
        var fileMatchCount = SelectedDiffFiles.Count(file => file.HasSearchMatch);
        var lineMatchCount = SelectedDiffFiles.Sum(
            file => file.Hunks.Sum(hunk => hunk.Lines.Count(line => line.IsSearchMatch)));

        var statusText =
            fileMatchCount == 0 && lineMatchCount == 0
                ? $"Diff search: no matches for \"{searchQuery}\""
                : $"Diff search: {FormatSearchCount(fileMatchCount, "file")} and {FormatSearchCount(lineMatchCount, "line")} matched";

        var statusBrushes =
            fileMatchCount == 0 && lineMatchCount == 0
                ? DiffSearchStatusBrushes.Empty
                : DiffSearchStatusBrushes.Applied;

        SetDiffSearchStatus(statusText, statusBrushes.Foreground, statusBrushes.Background);
    }

    private void SetDiffSearchStatus(string text, IBrush foreground, IBrush background)
    {
        DiffSearchStatusText = text;
        DiffSearchStatusForeground = foreground;
        DiffSearchStatusBackground = background;
        HasDiffSearchStatus = !string.IsNullOrWhiteSpace(text);
    }

    private void ClearSearchStateToSelectedDiff(string scopeKey)
    {
        foreach (var file in SelectedDiffFiles)
        {
            file.ApplySearchState("", scopeKey);
        }
    }

    private void ApplySearchStateToSelectedDiff(string query, string scopeKey)
    {
        foreach (var file in SelectedDiffFiles)
        {
            file.ApplySearchState(query, scopeKey);
        }
    }

    private void ScheduleDiffSearchDebounce()
    {
        if (string.IsNullOrWhiteSpace(_pendingDiffSearchQuery))
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _diffSearchDebounceCancellation = cancellation;

        _ = DebounceDiffSearchAsync(cancellation, TimeSpan.FromSeconds(Math.Max(0d, SearchDebounceSeconds)));
    }

    private async Task DebounceDiffSearchAsync(CancellationTokenSource cancellation, TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay, cancellation.Token);

            if (cancellation.IsCancellationRequested || !ReferenceEquals(_diffSearchDebounceCancellation, cancellation))
            {
                return;
            }

            _dispatch?.Invoke(GitKay.Core.App.Msg.NoOp);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_diffSearchDebounceCancellation, cancellation))
            {
                _diffSearchDebounceCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private void CancelDiffSearchDebounce()
    {
        if (_diffSearchDebounceCancellation == null)
        {
            return;
        }

        _diffSearchDebounceCancellation.Cancel();
        _diffSearchDebounceCancellation = null;
    }

    private static long SecondsToStopwatchTicks(double seconds) =>
        seconds <= 0d
            ? 0L
            : (long)(seconds * Stopwatch.Frequency);

    private static string FormatSearchCount(int count, string singularLabel) =>
        count == 1 ? $"1 {singularLabel}" : $"{count} {singularLabel}s";

    private void UpdateCommits(GitKay.Core.App.Model model)
    {
        var searchResultsSource = model.SearchResults != null ? (object?)model.SearchResults.Value : null;
        var commitsChanged = !ReferenceEquals(_commitsSource, model.Commits);
        var searchResultsChanged = !ReferenceEquals(_commitSearchResultsSource, searchResultsSource);

        if (commitsChanged)
        {
            Commits.SyncWith(
                model.Commits,
                m => m.Commit.Hash,
                vm => vm.FullHash,
                _ => new CommitProjection(),
                _dispatch!);

            _commitsSource = model.Commits;
        }

        ApplyRefVisibility();

        if (commitsChanged || searchResultsChanged)
        {
            ApplyCommitSearchMatches(model.SearchResults?.Value);
            UpdateVisibleCommits(model, commitsChanged, searchResultsChanged);
            _commitSearchResultsSource = searchResultsSource;
        }
    }

    private void ApplyRefVisibility()
    {
        foreach (var commit in Commits)
        {
            commit.ShowBranchRefs = ShowBranchRefs;
            commit.ShowStashes = ShowStashes;
        }
    }

    private void UpdateVisibleCommits(GitKay.Core.App.Model model, bool commitsChanged, bool searchResultsChanged)
    {
        var visibleCommitsSource = model.Commits;

        if (!commitsChanged && ReferenceEquals(_visibleCommitsSource, visibleCommitsSource))
        {
            return;
        }

        VisibleCommits.Clear();

        foreach (var commit in Commits)
        {
            VisibleCommits.Add(commit);
        }

        _visibleCommitsSource = visibleCommitsSource;
    }

    private void UpdateSelectedCommit(GitKay.Core.App.Model model)
    {
        CommitProjection? selectedCommit = null;

        if (model.SelectedCommitHash != null)
        {
            var hash = model.SelectedCommitHash.Value;
            foreach (var commit in Commits)
            {
                if (commit.FullHash == hash)
                {
                    selectedCommit = commit;
                    break;
                }
            }
        }

        if (selectedCommit != null)
        {
            _suppressSelectionDispatch = true;
            try
            {
                SelectedCommit = selectedCommit;
            }
            finally
            {
                _suppressSelectionDispatch = false;
            }
        }
        else if (SelectedCommit != null)
        {
            _suppressSelectionDispatch = true;
            try
            {
                SelectedCommit = null;
            }
            finally
            {
                _suppressSelectionDispatch = false;
            }
        }
    }

    private SearchResultProjection? ResolveSelectedSearchResult(string? previousSelectedSearchHash)
    {
        if (previousSelectedSearchHash != null)
        {
            var selectedSearchResult = SearchResults.FirstOrDefault(result => result.FullHash == previousSelectedSearchHash);
            if (selectedSearchResult != null)
            {
                return selectedSearchResult;
            }
        }

        return null;
    }

    private Action<GitKay.Core.App.Msg>? _dispatch;

    public void SetDispatch(Action<GitKay.Core.App.Msg> dispatch)
    {
        _dispatch = dispatch;
    }

    public void LogFirstPaint()
    {
        if (_firstPaintLogged)
        {
            return;
        }

        _firstPaintLogged = true;
        var firstPaintElapsed = Stopwatch.GetElapsedTime(_createdAtTicks);
        LogTiming($"first paint elapsed={firstPaintElapsed.TotalMilliseconds:F1}ms");
    }

    partial void OnSelectedCommitChanged(CommitProjection? value)
    {
        if (_suppressSelectionDispatch)
        {
            return;
        }

        if (value != null)
        {
            var startedAtTicks = Stopwatch.GetTimestamp();
            LogTiming($"commit click hash={value.FullHash}");
            _dispatch?.Invoke(GitKay.Core.App.Msg.NewSelectCommit(value.FullHash, startedAtTicks));
        }
    }

    partial void OnSearchQueryChanged(string value)
    {
        if (_suppressSearchDispatch)
        {
            return;
        }

        _dispatch?.Invoke(GitKay.Core.App.Msg.NewSetSearchQuery(value));
        ScheduleSearchDebounce();
    }

    partial void OnShowStashesChanged(bool value)
    {
        if (_suppressShowStashesDispatch)
        {
            return;
        }

        _dispatch?.Invoke(GitKay.Core.App.Msg.NewSetShowStashes(value));
    }

    partial void OnShowBranchRefsChanged(bool value)
    {
        if (_suppressShowBranchRefsDispatch)
        {
            return;
        }

        _dispatch?.Invoke(GitKay.Core.App.Msg.NewSetShowBranchRefs(value));
    }

    partial void OnSelectedSearchScopeChanged(SearchScopeProjection? value)
    {
        if (_suppressSearchDispatch || value == null)
        {
            return;
        }

        _dispatch?.Invoke(GitKay.Core.App.Msg.NewSetSearchScope(value.Key));
        ScheduleSearchDebounce();
    }

    partial void OnSelectedSearchResultChanged(SearchResultProjection? value)
    {
        if (_suppressSearchSelectionDispatch)
        {
            return;
        }

        if (value == null)
        {
            return;
        }

        _selectedSearchResultHash = value.FullHash;
        var startedAtTicks = Stopwatch.GetTimestamp();
        LogTiming($"search result click hash={value.FullHash} summary={value.MatchSummary}");
        _dispatch?.Invoke(GitKay.Core.App.Msg.NewSelectCommit(value.FullHash, startedAtTicks));
    }

    partial void OnSelectedDiffPresentationModeChanged(DiffPresentationModeProjection? value)
    {
        SelectedDiffPresentationModeLabel = value?.Label ?? "Diff";
        OnPropertyChanged(nameof(IsUnifiedDiffMode));
        OnPropertyChanged(nameof(IsSideBySideDiffMode));
        OnPropertyChanged(nameof(IsNewDiffMode));
        OnPropertyChanged(nameof(IsOldDiffMode));
    }

    partial void OnSelectedDiffFileChanged(DiffFileProjection? value)
    {
        if (_suppressDiffSelectionSync)
        {
            return;
        }

        SyncSelectedDiffSelection(value);

        if (value == null || SelectedCommit == null)
        {
            return;
        }

        LogTiming($"file click hash={SelectedCommit.FullHash} path={value.DisplayPath}");
        _dispatch?.Invoke(GitKay.Core.App.Msg.NewSelectDiffFile(SelectedCommit.FullHash, value.Key.OldPath, value.Key.NewPath));
    }

    partial void OnSelectedDiffRowChanged(IDiffRowProjection? value)
    {
        if (_suppressDiffSelectionSync)
        {
            return;
        }

        if (value is DiffFileHeaderProjection fileHeader)
        {
            SyncSelectedDiffSelection(fileHeader.File);
        }
    }

    [RelayCommand]
    private void FindInCommit()
    {
        var query = CommitFindQuery.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            return;
        }

        var matches = SelectedDiffRows.Where(row => RowMatchesFindQuery(row, query)).ToList();
        if (matches.Count == 0)
        {
            return;
        }

        var currentIndex = SelectedDiffRow != null ? matches.FindIndex(row => ReferenceEquals(row, SelectedDiffRow)) : -1;
        var nextIndex = currentIndex >= 0 ? (currentIndex + 1) % matches.Count : 0;
        var match = matches[nextIndex];

        if (!ReferenceEquals(SelectedDiffRow, match))
        {
            SelectedDiffRow = match;
        }
    }

    public void RereadRefs() => _dispatch?.Invoke(GitKay.Core.App.Msg.RereadRefs);

    public bool IsUnifiedDiffMode => SelectedDiffPresentationMode?.Key == "diff";
    public bool IsSideBySideDiffMode => SelectedDiffPresentationMode?.Key == "side-by-side";
    public bool IsNewDiffMode => SelectedDiffPresentationMode?.Key == "new";
    public bool IsOldDiffMode => SelectedDiffPresentationMode?.Key == "old";

    [RelayCommand]
    private void ToggleSearchPanel()
    {
        if (IsSearchPanelExpanded)
        {
            IsSearchPanelExpanded = false;
            _searchPanelAutoOpened = false;
            return;
        }

        IsSearchPanelExpanded = true;
    }

    [RelayCommand]
    private void Search()
    {
        CancelSearchDebounce();
        var query = SearchQuery;
        var scopeKey = SelectedSearchScope?.Key ?? "all";
        var startedAtTicks = Stopwatch.GetTimestamp();
        LogTiming($"search click query={query} scope={scopeKey}");
        _dispatch?.Invoke(GitKay.Core.App.Msg.NewRunSearch(query, scopeKey, startedAtTicks));
    }

    [RelayCommand]
    private void ClearSearch()
    {
        CancelSearchDebounce();
        _dispatch?.Invoke(GitKay.Core.App.Msg.NewSetSearchQuery(""));
        _dispatch?.Invoke(GitKay.Core.App.Msg.NewRunSearch("", SelectedSearchScope?.Key ?? "all", Stopwatch.GetTimestamp()));
    }

    public string SearchPanelActionText => IsSearchPanelExpanded ? "Hide search" : "Search";

    partial void OnIsSearchPanelExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(SearchPanelActionText));
    }

    private void ScheduleSearchDebounce()
    {
        if (string.IsNullOrWhiteSpace(SearchQuery))
        {
            CancelSearchDebounce();
            return;
        }

        CancelSearchDebounce();

        var cancellation = new CancellationTokenSource();
        _searchDebounceCancellation = cancellation;

        _ = DebounceSearchAsync(cancellation, TimeSpan.FromSeconds(Math.Max(0d, SearchDebounceSeconds)));
    }

    private async Task DebounceSearchAsync(CancellationTokenSource cancellation, TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay, cancellation.Token);

            if (cancellation.IsCancellationRequested || !ReferenceEquals(_searchDebounceCancellation, cancellation))
            {
                return;
            }

            var query = SearchQuery;
            if (string.IsNullOrWhiteSpace(query))
            {
                return;
            }

            var scopeKey = SelectedSearchScope?.Key ?? "all";
            var startedAtTicks = Stopwatch.GetTimestamp();
            _dispatch?.Invoke(GitKay.Core.App.Msg.NewRunSearch(query, scopeKey, startedAtTicks));
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_searchDebounceCancellation, cancellation))
            {
                _searchDebounceCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private void CancelSearchDebounce()
    {
        if (_searchDebounceCancellation == null)
        {
            return;
        }

        _searchDebounceCancellation.Cancel();
        _searchDebounceCancellation = null;
    }

    private void SyncSelectedDiffFiles(IReadOnlyList<GitKay.Core.GitService.DiffFileSummary> summaries, bool clearContent)
    {
        var existingByKey = SelectedDiffFiles.ToDictionary(file => file.Key);

        SelectedDiffFiles.Clear();
        foreach (var summary in summaries)
        {
            var key = new DiffFileKey(summary.OldPath, summary.NewPath);

            if (!existingByKey.TryGetValue(key, out var fileProjection))
            {
                fileProjection = new DiffFileProjection(summary);
            }
            else
            {
                fileProjection.UpdateSummary(summary);
                if (clearContent)
                {
                    fileProjection.ClearContent();
                }
            }

            SelectedDiffFiles.Add(fileProjection);
        }
    }

    private static bool MatchesKey(DiffFileKey uiKey, GitKay.Core.GitService.DiffFileKey coreKey) =>
        uiKey.OldPath == coreKey.OldPath && uiKey.NewPath == coreKey.NewPath;

    private DiffFileProjection? ResolveSelectedDiffFile(GitKay.Core.App.Model model, DiffFileKey? previousSelectedDiffFileKey)
    {
        if (model.SelectedDiffFileKey != null)
        {
            var key = model.SelectedDiffFileKey.Value;
            var selectedDiffFile = SelectedDiffFiles.FirstOrDefault(file => MatchesKey(file.Key, key));
            if (selectedDiffFile != null)
            {
                return selectedDiffFile;
            }
        }

        if (previousSelectedDiffFileKey != null)
        {
            var key = previousSelectedDiffFileKey.Value;
            var selectedDiffFile = SelectedDiffFiles.FirstOrDefault(file => file.Key == key);
            if (selectedDiffFile != null)
            {
                return selectedDiffFile;
            }
        }

        return SelectedDiffFiles.FirstOrDefault();
    }

    private void SyncSelectedDiffFileContents(IReadOnlyList<GitKay.Core.Models.FileDiff> files)
    {
        var filesByKey = files.ToDictionary(
            file => new DiffFileKey(file.OldPath, file.NewPath),
            file => file);

        foreach (var fileProjection in SelectedDiffFiles)
        {
            if (filesByKey.TryGetValue(fileProjection.Key, out var loadedFile))
            {
                fileProjection.ApplyContent(loadedFile);
            }
            else
            {
                fileProjection.ClearContent();
            }
        }
    }

    private void ApplyCommitSearchMatches(IEnumerable<GitKay.Core.GitService.SearchResult>? results)
    {
        var resultsByHash = results?.ToDictionary(result => result.Commit.Hash);

        foreach (var commit in Commits)
        {
            if (resultsByHash != null && resultsByHash.TryGetValue(commit.FullHash, out var result))
            {
                commit.ApplySearchMatch(result);
            }
            else
            {
                commit.ApplySearchMatch(null);
            }
        }
    }

    private void SyncSelectedSearchResult(SearchResultProjection? selectedSearchResult)
    {
        _suppressSearchSelectionDispatch = true;
        try
        {
            SelectedSearchResult = selectedSearchResult;
        }
        finally
        {
            _suppressSearchSelectionDispatch = false;
        }
    }

    private void RenderSelectedDiffRows()
    {
        SelectedDiffRows.Clear();

        foreach (var file in SelectedDiffFiles)
        {
            SelectedDiffRows.Add(file.Header);

            if (!file.IsLoaded)
            {
                continue;
            }

            foreach (var hunk in file.Hunks)
            {
                SelectedDiffRows.Add(new DiffHunkHeaderProjection(hunk));

                foreach (var line in hunk.Lines)
                {
                    SelectedDiffRows.Add(line);
                }
            }
        }
    }

    private void SyncSelectedDiffSelection(DiffFileProjection? selectedDiffFile)
    {
        _suppressDiffSelectionSync = true;
        try
        {
            SelectedDiffFile = selectedDiffFile;
            SelectedDiffRow = selectedDiffFile?.Header;
        }
        finally
        {
            _suppressDiffSelectionSync = false;
        }
    }

    private static bool RowMatchesFindQuery(IDiffRowProjection row, string query) =>
        row switch
        {
            DiffFileHeaderProjection fileHeader => fileHeader.DisplayPath.Contains(query, StringComparison.OrdinalIgnoreCase),
            DiffHunkHeaderProjection hunkHeader => hunkHeader.Header.Contains(query, StringComparison.OrdinalIgnoreCase),
            DiffLineProjection line => line.Content.Contains(query, StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
}
