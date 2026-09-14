using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Collections;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Elmish.Glue.Core;
using GitKay.Core;

namespace GitKay.UI;

internal readonly record struct DiffSearchStatusStyle(IBrush Foreground, IBrush Background);

internal static class DiffSearchStatusBrushes {
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

public sealed class DiffPresentationModeProjection {
    public DiffPresentationModeProjection(string key, string label) {
        Key = key;
        Label = label;
    }

    public string Key { get; }
    public string Label { get; }
}

public sealed class ThemeModeProjection {
    public ThemeModeProjection(string key, string label) {
        Key = key;
        Label = label;
    }

    public string Key { get; }
    public string Label { get; }
}

public sealed class DiffContextLineCountProjection {
    public DiffContextLineCountProjection(int count) {
        Count = count;
        Label = count == 1 ? "1 line" : $"{count} lines";
    }

    public int Count { get; }
    public string Label { get; }
}

public partial class MainProjection : ObservableObject, IProjection<GitKay.Core.App.Model, GitKay.Core.App.Msg> {
    private readonly long _createdAtTicks = Stopwatch.GetTimestamp();
    private bool _firstPaintLogged;
    private bool _suppressSelectionDispatch;
    private bool _suppressDiffSelectionSync;
    private bool _suppressSearchDispatch;
    private bool _suppressSearchSelectionDispatch;
    private bool _suppressShowBranchRefsDispatch;
    private bool _suppressShowStashesDispatch;
    private bool _suppressDiffContextDispatch;
    private bool _suppressDiffPresentationDispatch;
    private object? _commitsSource;
    private object? _commitSearchResultsSource;
    private string? _selectedDiffHash;
    private object? _selectedDiffFilesSource;
    private DiffFileKey? _selectedDiffFileKey;
    private object? _selectedDiffFileSource;
    private object? _diffExpansionsSource;
    private object? _searchResultsSource;
    private string? _selectedSearchResultHash;
    private string? _diffSearchQuery;
    private string? _diffSearchScopeKey;
    private string? _pendingDiffSearchQuery;
    private string? _pendingDiffSearchScopeKey;
    private long _pendingDiffSearchReadyAtTicks;
    private CancellationTokenSource? _searchDebounceCancellation;
    private CancellationTokenSource? _diffSearchDebounceCancellation;

    public ObservableCollection<DiffPresentationModeProjection> DiffPresentationModes { get; } = new()
    {
        new DiffPresentationModeProjection("diff", "Diff"),
        new DiffPresentationModeProjection("side-by-side", "Side-by-side"),
        new DiffPresentationModeProjection("new", "New"),
        new DiffPresentationModeProjection("old", "Old"),
    };

    public ObservableCollection<ThemeModeProjection> ThemeModes { get; } = new()
    {
        new ThemeModeProjection("system", "System"),
        new ThemeModeProjection("light", "Light"),
        new ThemeModeProjection("dark", "Dark"),
    };

    public ObservableCollection<DiffContextLineCountProjection> DiffContextLineCounts { get; } = new()
    {
        new DiffContextLineCountProjection(0),
        new DiffContextLineCountProjection(1),
        new DiffContextLineCountProjection(2),
        new DiffContextLineCountProjection(3),
        new DiffContextLineCountProjection(5),
        new DiffContextLineCountProjection(10),
        new DiffContextLineCountProjection(20),
    };

    public ObservableCollection<SearchScopeProjection> SearchScopes { get; } = new()
    {
        // Commit is instant; Path and Diff read each commit's diff.
        new SearchScopeProjection("commit", "Commit", "Headline, message, hash or branch — or author:, after:…"),
        new SearchScopeProjection("path", "Path", "File or folder, e.g. src/GitKay.UI"),
        new SearchScopeProjection("diff", "Diff", "Text added or removed by the commit"),
    };

    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isInitialLoading = true;
    [ObservableProperty] private string _commitRowFontFamily = AppSettings.DefaultCommitRowFontFamily;
    [ObservableProperty] private string _commitRowMonoFontFamily = AppSettings.DefaultCommitRowMonoFontFamily;
    [ObservableProperty] private double _commitRowTextFontSize = AppSettings.DefaultCommitRowTextFontSize;
    [ObservableProperty] private double _commitRowMetaFontSize = AppSettings.DefaultCommitRowMetaFontSize;
    [ObservableProperty] private double _commitRowBadgeFontSize = AppSettings.DefaultCommitRowBadgeFontSize;
    [ObservableProperty] private bool _showBranchRefs;
    [ObservableProperty] private bool _showStashes;
    [ObservableProperty] private int _diffContextLineCount = AppSettings.DefaultDiffContextLines;
    [ObservableProperty] private string _searchQuery = "";
    [ObservableProperty] private double _searchDebounceSeconds = AppSettings.DefaultSearchDebounceSeconds;
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
    [ObservableProperty] private DiffContextLineCountProjection? _selectedDiffContextLineCount;
    [ObservableProperty] private ThemeModeProjection? _selectedThemeMode;
    [ObservableProperty] private string _selectedDiffPresentationModeLabel = "Diff";
    [ObservableProperty] private bool _isCommitDetailsExpanded;
    [ObservableProperty] private bool _isDiffFileTreeMode;
    /// <summary>Commit search position, e.g. "3 of 27", "Searching…" or "No matches".</summary>
    [ObservableProperty] private string _commitSearchStatusText = "";
    [ObservableProperty] private string _searchPlaceholder = "Headline, message, hash or branch — or author:, after:…";
    [ObservableProperty] private bool _searchUseRegex;
    [ObservableProperty] private bool _commitFindUseRegex;
    [ObservableProperty] private bool _isAdvancedSearchExpanded;
    /// <summary>Hide non-matching commits instead of only showing matches in bold.</summary>
    [ObservableProperty] private bool _showOnlySearchMatches;
    /// <summary>A commit search has (or is producing) results; with show-only-matches, zero results means an empty list.</summary>
    [ObservableProperty] private bool _isCommitSearchActive;
    [ObservableProperty] private bool _isRecentSearchesOpen;
    [ObservableProperty] private bool _isShortcutHelpOpen;

    [RelayCommand]
    private void ToggleShortcutHelp() => IsShortcutHelpOpen = !IsShortcutHelpOpen;
    public ObservableCollection<string> RecentSearchMatches { get; } = new();
    private readonly List<string> _recentSearches = new();
    private const int MaxRecentSearches = 30;
    public bool IsCommitSearchMode => SelectedSearchScope?.Key is null or "commit";
    public bool IsPathSearchMode => SelectedSearchScope?.Key == "path";
    public bool IsDiffSearchMode => SelectedSearchScope?.Key == "diff";
    public bool HasSearchNavigation => !string.IsNullOrEmpty(CommitSearchStatusText);
    /// <summary>Find-in-diff position, e.g. "2 of 14" or "No matches".</summary>
    [ObservableProperty] private string _commitFindStatusText = "";
    /// <summary>The applied commit search's diff term, highlighted in the diff and borrowed by an empty find box.</summary>
    [ObservableProperty] private string _commitSearchDiffTerm = "";
    [ObservableProperty] private bool _commitSearchDiffTermUseRegex;
    [ObservableProperty] private string _commitSearchPathTerm = "";
    [ObservableProperty] private SearchHighlight? _commitSearchHighlight;
    /// <summary>Parent and child commits of the selection, for links and p / c navigation.</summary>
    public ObservableCollection<CommitLinkProjection> SelectedCommitParents { get; } = new();
    public ObservableCollection<CommitLinkProjection> SelectedCommitChildren { get; } = new();
    private Dictionary<string, List<string>> _childrenByParent = new(StringComparer.Ordinal);
    private object? _childrenSource;
    [ObservableProperty] private string _commitFindPlaceholder = "Find in diff  (Ctrl+F or /)";
    private bool _revealSearchMatchInDiff;
    private string? _commitSearchHighlightKey;
    private bool _startupFilterApplied;
    private string? _lastRunSearchQuery;

    public ObservableCollection<CommitProjection> Commits { get; } = new();
    public ObservableCollection<SearchResultProjection> SearchResults { get; } = new();
    public ObservableCollection<DiffFileProjection> SelectedDiffFiles { get; } = new();
    public AvaloniaList<IDiffRowProjection> SelectedDiffRows { get; } = new();
    /// <summary>Changed-files list rows: files in patch mode; folders and files in tree mode.</summary>
    public AvaloniaList<object> DiffFileListRows { get; } = new();
    private readonly HashSet<string> _collapsedDiffFolders = new(StringComparer.Ordinal);

    /// <summary>
    /// List selection follows the selected file only. Folder rows are never selected — clicking one toggles it
    /// (<see cref="ToggleDiffFolderCommand"/>) — so a collapsed folder can always be clicked open again.
    /// </summary>
    public object? SelectedDiffFileListRow {
        get => (object?)_selectedRepoFileRow ?? SelectedDiffFile;
        set {
            // Unchanged files have no diff to jump to; the list selects them and Enter or a double-click opens them.
            _selectedRepoFileRow = value as RepoFileRow;
            if (value is DiffFileProjection file) {
                SelectedDiffFile = file;
                FileJumpRequested?.Invoke(file);
            }

            OnPropertyChanged();
        }
    }

    [ObservableProperty] private CommitProjection? _selectedCommit;
    [ObservableProperty] private SearchResultProjection? _selectedSearchResult;
    [ObservableProperty] private DiffFileProjection? _selectedDiffFile;
    [ObservableProperty] private IDiffRowProjection? _selectedDiffRow;

    public FontFamily CommitRowFont => FontStacks.Resolve(CommitRowFontFamily);
    public FontFamily CommitRowMonoFont => FontStacks.Resolve(CommitRowMonoFontFamily);

    public MainProjection() {
        SelectedDiffPresentationMode = DiffPresentationModes[0];
        SelectedDiffContextLineCount = DiffContextLineCounts.First(option => option.Count == DiffContextLineCount);
        SelectedThemeMode = ThemeModes[0];
    }

    public void ApplySettings(AppSettings settings) {
        var normalized = settings.Normalize();

        _suppressShowBranchRefsDispatch = true;
        _suppressShowStashesDispatch = true;
        _suppressDiffContextDispatch = true;
        _suppressDiffPresentationDispatch = true;

        try {
            ShowBranchRefs = normalized.ShowBranchRefs;
            ShowStashes = normalized.ShowStashes;
            CommitRowFontFamily = normalized.CommitRowFontFamily;
            CommitRowMonoFontFamily = normalized.CommitRowMonoFontFamily;
            CommitRowTextFontSize = normalized.CommitRowTextFontSize;
            CommitRowMetaFontSize = normalized.CommitRowMetaFontSize;
            CommitRowBadgeFontSize = normalized.CommitRowBadgeFontSize;
            SearchDebounceSeconds = normalized.SearchDebounceSeconds;
            DiffContextLineCount = normalized.DiffContextLines;
            SelectedDiffContextLineCount =
                DiffContextLineCounts.FirstOrDefault(option => option.Count == normalized.DiffContextLines)
                ?? DiffContextLineCounts.First();
            SelectedDiffPresentationMode =
                DiffPresentationModes.FirstOrDefault(mode => mode.Key == normalized.DiffPresentationModeKey)
                ?? DiffPresentationModes.First();
            SelectedThemeMode =
                ThemeModes.FirstOrDefault(mode => mode.Key == normalized.ThemeMode)
                ?? ThemeModes.First();
        }
        finally {
            _suppressDiffPresentationDispatch = false;
            _suppressDiffContextDispatch = false;
            _suppressShowStashesDispatch = false;
            _suppressShowBranchRefsDispatch = false;
        }
    }

    public AppSettings CaptureSettings() {
        return new AppSettings {
            ShowBranchRefs = ShowBranchRefs,
            ShowStashes = ShowStashes,
            DiffContextLines = DiffContextLineCount,
            DiffPresentationModeKey = SelectedDiffPresentationMode?.Key ?? AppSettings.DefaultDiffPresentationModeKey,
            CommitRowFontFamily = CommitRowFontFamily,
            CommitRowMonoFontFamily = CommitRowMonoFontFamily,
            CommitRowTextFontSize = CommitRowTextFontSize,
            CommitRowMetaFontSize = CommitRowMetaFontSize,
            CommitRowBadgeFontSize = CommitRowBadgeFontSize,
            SearchDebounceSeconds = SearchDebounceSeconds,
            ThemeMode = SelectedThemeMode?.Key ?? AppSettings.DefaultThemeMode,
        };
    }

    private static void LogTiming(string message) {
        var line = $"[timing] {message}";
        Trace.WriteLine(line);
    }

    partial void OnCommitRowFontFamilyChanged(string value) {
        OnPropertyChanged(nameof(CommitRowFont));
    }

    partial void OnCommitRowMonoFontFamilyChanged(string value) {
        OnPropertyChanged(nameof(CommitRowMonoFont));
    }

    public void Update(GitKay.Core.App.Model model) {
        var startedAtTicks = Stopwatch.GetTimestamp();
        Status = model.Status;
        IsInitialLoading = model.Commits.IsEmpty && !model.Status.StartsWith("Error", StringComparison.OrdinalIgnoreCase);
        UpdateHistoryTargets(model.StartupTargets);
        if (ShowBranchRefs != model.ShowBranchRefs) {
            _suppressShowBranchRefsDispatch = true;
            try {
                ShowBranchRefs = model.ShowBranchRefs;
            }
            finally {
                _suppressShowBranchRefsDispatch = false;
            }
        }

        if (ShowStashes != model.ShowStashes) {
            _suppressShowStashesDispatch = true;
            try {
                ShowStashes = model.ShowStashes;
            }
            finally {
                _suppressShowStashesDispatch = false;
            }
        }

        if (model.StartupShowOnlyMatches && !_startupFilterApplied) {
            _startupFilterApplied = true;
            ShowOnlySearchMatches = true;
        }

        if (SearchUseRegex != model.SearchUseRegex) {
            _suppressSearchDispatch = true;
            try { SearchUseRegex = model.SearchUseRegex; } finally { _suppressSearchDispatch = false; }
        }

        UpdateDiffContextState(model);
        UpdateDiffPresentationState(model);
        UpdateSearchState(model);
        UpdateDiffState(model);
        UpdateCommits(model);
        UpdateSelectedCommit(model);
        UpdateCommitRelations(model);
        UpdateCommitSearchStatus(model);

        var elapsed = Stopwatch.GetElapsedTime(startedAtTicks);
        LogTiming($"ui projection elapsed={elapsed.TotalMilliseconds:F1}ms commits={model.Commits.Length} searchResults={SearchResults.Count} diffFiles={SelectedDiffFiles.Count} diffRows={SelectedDiffRows.Count}");
    }

    private void UpdateSearchState(GitKay.Core.App.Model model) {
        var searchResultsSource = model.SearchResults != null ? (object?)model.SearchResults.Value : null;

        if (!string.Equals(SearchQuery, model.SearchQuery, StringComparison.Ordinal)) {
            _suppressSearchDispatch = true;
            try {
                SearchQuery = model.SearchQuery;
            }
            finally {
                _suppressSearchDispatch = false;
            }
        }

        var selectedScope = SearchScopes.FirstOrDefault(scope => scope.Key == model.SearchScopeKey) ?? SearchScopes.FirstOrDefault();
        if (!ReferenceEquals(SelectedSearchScope, selectedScope)) {
            _suppressSearchDispatch = true;
            try {
                SelectedSearchScope = selectedScope;
            }
            finally {
                _suppressSearchDispatch = false;
            }
        }

        var shouldAutoOpenSearchPanel =
            !string.IsNullOrWhiteSpace(model.SearchQuery)
            || model.SearchResults != null;

        if (shouldAutoOpenSearchPanel) {
            IsSearchPanelExpanded = true;
        }

        var searchResultsChanged = !ReferenceEquals(_searchResultsSource, searchResultsSource);

        if (model.SearchResults == null) {
            if (SearchResults.Count > 0 || _searchResultsSource != null) {
                SyncSelectedSearchResult(null);
                SearchResults.Clear();
            }

            _searchResultsSource = null;
            _selectedSearchResultHash = null;
            HasSearchResults = false;
            return;
        }

        if (searchResultsChanged) {
            var previousSelectedSearchHash = SelectedSearchResult?.FullHash ?? _selectedSearchResultHash;
            SyncSelectedSearchResult(null);
            SearchResults.Clear();

            foreach (var result in model.SearchResults.Value) {
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

    private void UpdateDiffState(GitKay.Core.App.Model model) {
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

        // Highlight in the diff only what the commit search looked for in diffs (its diff: or path: terms).
        var (searchQuery, diffSearchScopeKey) = DiffHighlightFor(model);
        var searchStateChanged =
            !string.Equals(_diffSearchQuery, searchQuery, StringComparison.Ordinal)
            || !string.Equals(_diffSearchScopeKey, diffSearchScopeKey, StringComparison.Ordinal);

        var diffCollectionChanged =
            !string.Equals(_selectedDiffHash, selectedDiffHash, StringComparison.Ordinal)
            || !ReferenceEquals(_selectedDiffFilesSource, selectedDiffFilesSource);

        if (selectedDiffHash == null) {
            SaveCommitViewState();
            if (SelectedDiffFiles.Count > 0 || SelectedDiffRows.Count > 0 || _selectedDiffHash != null) {
                SyncSelectedDiffSelection(null);
                SelectedDiffFiles.Clear();
                SelectedDiffRows.Clear();
                DiffFileListRows.Clear();
            }

            _selectedDiffHash = null;
            _selectedDiffFilesSource = null;
            _selectedDiffFileKey = null;
            _selectedDiffFileSource = null;
            RefreshDiffSearchState(model, searchQuery, searchStateChanged, false, false);
            _diffSearchQuery = searchQuery;
            _diffSearchScopeKey = diffSearchScopeKey;
            return;
        }

        if (diffCollectionChanged) {
            SaveCommitViewState();
            var previousSelectedDiffFileKey = SelectedDiffFile?.Key;

            SyncSelectedDiffSelection(null);
            SelectedDiffRows.Clear();

            if (model.SelectedDiffFiles != null) {
                SyncSelectedDiffFiles(model.SelectedDiffFiles.Value, true);
            }

            var restored = RestoreCommitViewState(selectedDiffHash);
            if (restored?.SelectedFile is { } restoredFile && SelectedDiffFiles.Any(file => file.Key == restoredFile)) {
                previousSelectedDiffFileKey = restoredFile;
                if (model.SelectedDiffFileKey == null || !MatchesKey(restoredFile, model.SelectedDiffFileKey.Value))
                    _dispatch?.Invoke(GitKay.Core.App.Msg.NewSelectDiffFile(selectedDiffHash, restoredFile.OldPath, restoredFile.NewPath));
            }

            _selectedDiffHash = selectedDiffHash;
            _selectedDiffFilesSource = selectedDiffFilesSource;

            if (model.SelectedDiff != null) {
                SyncSelectedDiffFileContents(model.SelectedDiff.Value, model.DiffExpansions);
            }

            _selectedDiffFileSource = selectedDiffContentSource;
            _diffExpansionsSource = model.DiffExpansions;

            var selectedDiffFile = ResolveSelectedDiffFile(model, previousSelectedDiffFileKey);

            SyncSelectedDiffSelection(selectedDiffFile);
            RenderSelectedDiffRows();
            _selectedDiffFileKey = selectedDiffFile?.Key;
        }

        if (model.SelectedDiffFiles != null) {
            var selectedDiffFile = ResolveSelectedDiffFile(model, _selectedDiffFileKey);
            var selectedDiffFileKey = selectedDiffFile?.Key;
            // A context expansion temporarily publishes no content while its replacement
            // loads. Keep the current rows visible until the new diff can replace them.
            var diffContentChanged = selectedDiffContentSource != null
                                     && !ReferenceEquals(_selectedDiffFileSource, selectedDiffContentSource);
            var selectionChanged = !Nullable.Equals(_selectedDiffFileKey, selectedDiffFileKey);

            if (diffContentChanged) {
                SyncSelectedDiffFileContents(model.SelectedDiff!.Value, model.DiffExpansions);
                RenderSelectedDiffRows();
                _selectedDiffFileSource = selectedDiffContentSource;
                _diffExpansionsSource = model.DiffExpansions;
            }
            else if (!ReferenceEquals(_diffExpansionsSource, model.DiffExpansions)) {
                // Expansion is file-scoped: only files whose expansion state changed re-project.
                _diffExpansionsSource = model.DiffExpansions;
                if (SyncSelectedDiffExpansions(model.DiffExpansions)) {
                    RenderSelectedDiffRows();
                    diffContentChanged = true;
                }
            }

            if (selectionChanged || diffContentChanged || searchStateChanged) {
                SyncSelectedDiffSelection(selectedDiffFile);
                _selectedDiffFileKey = selectedDiffFileKey;
            }

            RefreshDiffSearchState(model, searchQuery, searchStateChanged, diffContentChanged, true);
        }
        else {
            RefreshDiffSearchState(model, searchQuery, searchStateChanged, diffCollectionChanged, false);
        }

        _diffSearchQuery = searchQuery;
        _diffSearchScopeKey = diffSearchScopeKey;

        if (_revealSearchMatchInDiff && model.SelectedDiff != null && SelectedDiffRows.Any(row => row is DiffLineProjection)) {
            _revealSearchMatchInDiff = false;
            SelectedDiffRow = null;
            StepDiffFind(+1);
        }
    }

    private void UpdateDiffContextState(GitKay.Core.App.Model model) {
        if (DiffContextLineCount != model.DiffContextLines) {
            DiffContextLineCount = model.DiffContextLines;
        }

        var selectedContextLineCount =
            DiffContextLineCounts.FirstOrDefault(option => option.Count == model.DiffContextLines)
            ?? DiffContextLineCounts.FirstOrDefault();

        if (!ReferenceEquals(SelectedDiffContextLineCount, selectedContextLineCount)) {
            _suppressDiffContextDispatch = true;
            try {
                SelectedDiffContextLineCount = selectedContextLineCount;
            }
            finally {
                _suppressDiffContextDispatch = false;
            }
        }
    }

    private void UpdateDiffPresentationState(GitKay.Core.App.Model model) {
        var selectedPresentationMode =
            DiffPresentationModes.FirstOrDefault(mode => mode.Key == model.DiffPresentationModeKey)
            ?? DiffPresentationModes.First();

        if (!ReferenceEquals(SelectedDiffPresentationMode, selectedPresentationMode)) {
            _suppressDiffPresentationDispatch = true;
            try {
                SelectedDiffPresentationMode = selectedPresentationMode;
            }
            finally {
                _suppressDiffPresentationDispatch = false;
            }
        }
    }

    private void RefreshDiffSearchState(
        GitKay.Core.App.Model model,
        string searchQuery,
        bool searchStateChanged,
        bool diffContentChanged,
        bool hasSelectedDiff) {
        if (string.IsNullOrWhiteSpace(searchQuery)) {
            CancelDiffSearchDebounce();
            _pendingDiffSearchQuery = null;
            _pendingDiffSearchScopeKey = null;
            _pendingDiffSearchReadyAtTicks = 0;
            IsDiffSearchPending = false;
            SetDiffSearchStatus("", Brushes.Transparent, Brushes.Transparent);

            if (hasSelectedDiff) {
                ClearSearchStateToSelectedDiff(DiffHighlightFor(model).ScopeKey);
            }

            return;
        }

        if (searchStateChanged) {
            StartDiffSearchDebounce(model, searchQuery);
            return;
        }

        if (TryApplyPendingDiffSearch(model, searchQuery, hasSelectedDiff)) {
            return;
        }

        if (hasSelectedDiff && diffContentChanged && !IsDiffSearchPending) {
            ApplySearchStateToSelectedDiff(searchQuery, DiffHighlightFor(model).ScopeKey);
            SetDiffSearchAppliedStatus(searchQuery);
        }
        else if (IsDiffSearchPending) {
            SetDiffSearchPendingStatus(searchQuery);
        }
        else if (SelectedDiffFiles.Count > 0) {
            SetDiffSearchAppliedStatus(searchQuery);
        }
    }

    private void StartDiffSearchDebounce(GitKay.Core.App.Model model, string searchQuery) {
        CancelDiffSearchDebounce();

        _pendingDiffSearchQuery = searchQuery;
        _pendingDiffSearchScopeKey = DiffHighlightFor(model).ScopeKey;
        _pendingDiffSearchReadyAtTicks = Stopwatch.GetTimestamp() + SecondsToStopwatchTicks(SearchDebounceSeconds);
        IsDiffSearchPending = true;

        if (SelectedDiffFiles.Count > 0) {
            ClearSearchStateToSelectedDiff(DiffHighlightFor(model).ScopeKey);
        }

        SetDiffSearchPendingStatus(searchQuery);

        if (SearchDebounceSeconds > 0d) {
            ScheduleDiffSearchDebounce();
        }

        _ = TryApplyPendingDiffSearch(model, searchQuery, true);
    }

    private bool TryApplyPendingDiffSearch(GitKay.Core.App.Model model, string searchQuery, bool hasSelectedDiff) {
        if (!IsDiffSearchPending || _pendingDiffSearchQuery == null || _pendingDiffSearchScopeKey == null) {
            return false;
        }

        if (!string.Equals(_pendingDiffSearchQuery, searchQuery, StringComparison.Ordinal)
            || !string.Equals(_pendingDiffSearchScopeKey, DiffHighlightFor(model).ScopeKey, StringComparison.Ordinal)) {
            return false;
        }

        if (Stopwatch.GetTimestamp() < _pendingDiffSearchReadyAtTicks) {
            return false;
        }

        if (!hasSelectedDiff || SelectedDiffFiles.Count == 0) {
            SetDiffSearchPendingStatus(searchQuery);
            return false;
        }

        CancelDiffSearchDebounce();
        _pendingDiffSearchQuery = null;
        _pendingDiffSearchScopeKey = null;
        _pendingDiffSearchReadyAtTicks = 0;
        IsDiffSearchPending = false;

        ApplySearchStateToSelectedDiff(searchQuery, DiffHighlightFor(model).ScopeKey);
        SetDiffSearchAppliedStatus(searchQuery);
        return true;
    }

    private void SetDiffSearchPendingStatus(string searchQuery) {
        SetDiffSearchStatus(
            $"Searching diff for \"{searchQuery}\"...",
            DiffSearchStatusBrushes.Pending.Foreground,
            DiffSearchStatusBrushes.Pending.Background);
    }

    private void SetDiffSearchAppliedStatus(string searchQuery) {
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

    private void SetDiffSearchStatus(string text, IBrush foreground, IBrush background) {
        DiffSearchStatusText = text;
        DiffSearchStatusForeground = foreground;
        DiffSearchStatusBackground = background;
        HasDiffSearchStatus = !string.IsNullOrWhiteSpace(text);
    }

    private void ClearSearchStateToSelectedDiff(string scopeKey) {
        foreach (var file in SelectedDiffFiles) {
            file.ApplySearchState("", scopeKey);
        }
    }

    private void ApplySearchStateToSelectedDiff(string query, string scopeKey) {
        foreach (var file in SelectedDiffFiles) {
            file.ApplySearchState(query, scopeKey);
        }
    }

    private void ScheduleDiffSearchDebounce() {
        if (string.IsNullOrWhiteSpace(_pendingDiffSearchQuery)) {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _diffSearchDebounceCancellation = cancellation;

        _ = DebounceDiffSearchAsync(cancellation, TimeSpan.FromSeconds(Math.Max(0d, SearchDebounceSeconds)));
    }

    private async Task DebounceDiffSearchAsync(CancellationTokenSource cancellation, TimeSpan delay) {
        try {
            await Task.Delay(delay, cancellation.Token);

            if (cancellation.IsCancellationRequested || !ReferenceEquals(_diffSearchDebounceCancellation, cancellation)) {
                return;
            }

            _dispatch?.Invoke(GitKay.Core.App.Msg.NoOp);
        }
        catch (OperationCanceledException) {
        }
        finally {
            if (ReferenceEquals(_diffSearchDebounceCancellation, cancellation)) {
                _diffSearchDebounceCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private void CancelDiffSearchDebounce() {
        if (_diffSearchDebounceCancellation == null) {
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

    private void UpdateCommits(GitKay.Core.App.Model model) {
        var searchResultsSource = model.SearchResults != null ? (object?)model.SearchResults.Value : null;
        var commitsChanged = !ReferenceEquals(_commitsSource, model.Commits);
        var searchResultsChanged = !ReferenceEquals(_commitSearchResultsSource, searchResultsSource);

        if (commitsChanged) {
            Commits.SyncWith(
                model.Commits,
                m => m.Commit.Hash,
                vm => vm.FullHash,
                _ => new CommitProjection(),
                _dispatch!);

            _commitsSource = model.Commits;
            ApplyRefVisibility();
        }

        // While a new search runs, keep the previous matches instead of flashing every commit back.
        var searchPending = model.SearchStartedAtTicks != null && model.SearchResults == null;
        if ((commitsChanged || searchResultsChanged) && !searchPending) {
            ApplyCommitSearchMatches(model.SearchResults?.Value);
            _commitSearchResultsSource = searchResultsSource;
        }

        IsCommitSearchActive = !string.IsNullOrWhiteSpace(model.SearchQuery) && (model.SearchResults != null || searchPending);
    }

    private void ApplyRefVisibility() {
        foreach (var commit in Commits) {
            commit.ShowBranchRefs = ShowBranchRefs;
            commit.ShowStashes = ShowStashes;
        }
    }

    // ----- Per-commit view state: returning to a commit restores its selected file and collapsed files/folders. -----

    private sealed record CommitViewState(DiffFileKey? SelectedFile, HashSet<DiffFileKey> CollapsedFiles, HashSet<string> CollapsedFolders);
    private readonly Dictionary<string, CommitViewState> _commitViewStates = new(StringComparer.Ordinal);

    private void SaveCommitViewState() {
        if (_selectedDiffHash == null || SelectedDiffFiles.Count == 0) return;
        _commitViewStates[_selectedDiffHash] = new CommitViewState(
            SelectedDiffFile?.Key,
            SelectedDiffFiles.Where(file => file.IsCollapsed).Select(file => file.Key).ToHashSet(),
            new HashSet<string>(_collapsedDiffFolders, StringComparer.Ordinal));
        if (_commitViewStates.Count > 500) _commitViewStates.Remove(_commitViewStates.Keys.First());
    }

    private CommitViewState? RestoreCommitViewState(string hash) {
        _commitViewStates.TryGetValue(hash, out var state);
        foreach (var file in SelectedDiffFiles) {
            // File projections are reused across commits by path, so collapse state is always reset here.
            file.IsCollapsed = state?.CollapsedFiles.Contains(file.Key) == true;
        }

        _collapsedDiffFolders.Clear();
        if (state != null) _collapsedDiffFolders.UnionWith(state.CollapsedFolders);
        RebuildDiffFileListRows();
        return state;
    }

    private void UpdateCommitRelations(GitKay.Core.App.Model model) {
        if (!ReferenceEquals(_childrenSource, model.Commits)) {
            _childrenSource = model.Commits;
            _childrenByParent = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var info in model.Commits) {
                foreach (var parent in info.Commit.Parents) {
                    if (!_childrenByParent.TryGetValue(parent, out var childHashes))
                        _childrenByParent[parent] = childHashes = new List<string>();
                    childHashes.Add(info.Commit.Hash);
                }
            }
        }

        var selected = SelectedCommit;
        RecordVisitedCommit(selected?.FullHash);
        var parents = selected == null
            ? new List<string>()
            : model.Commits.FirstOrDefault(info => info.Commit.Hash == selected.FullHash)?.Commit.Parents.ToList() ?? new List<string>();
        var children = selected != null && _childrenByParent.TryGetValue(selected.FullHash, out var found) ? found : new List<string>();
        SyncLinks(SelectedCommitParents, parents);
        SyncLinks(SelectedCommitChildren, children);
    }

    private void SyncLinks(ObservableCollection<CommitLinkProjection> target, IReadOnlyList<string> hashes) {
        if (target.Select(link => link.FullHash).SequenceEqual(hashes, StringComparer.Ordinal)) return;
        target.Clear();
        foreach (var hash in hashes) {
            var commit = Commits.FirstOrDefault(candidate => candidate.FullHash == hash);
            target.Add(new CommitLinkProjection(hash, commit?.Subject ?? "(not in loaded history)"));
        }
    }

    [RelayCommand]
    private void GoToCommit(string? hash) {
        var commit = hash == null ? null : Commits.FirstOrDefault(candidate => candidate.FullHash == hash);
        if (commit != null) SelectedCommit = commit;
    }

    /// <summary>p: first parent (Shift+P: second parent of a merge); c: first child.</summary>
    public void GoToParent(int index) {
        if (index < SelectedCommitParents.Count) GoToCommit(SelectedCommitParents[index].FullHash);
    }

    public void GoToChild() {
        if (SelectedCommitChildren.Count > 0) GoToCommit(SelectedCommitChildren[0].FullHash);
    }

    private void UpdateSelectedCommit(GitKay.Core.App.Model model) {
        CommitProjection? selectedCommit = null;

        if (model.SelectedCommitHash != null) {
            var hash = model.SelectedCommitHash.Value;
            foreach (var commit in Commits) {
                if (commit.FullHash == hash) {
                    selectedCommit = commit;
                    break;
                }
            }
        }

        if (selectedCommit != null) {
            _suppressSelectionDispatch = true;
            try {
                SelectedCommit = selectedCommit;
            }
            finally {
                _suppressSelectionDispatch = false;
            }
        }
        else if (SelectedCommit != null) {
            _suppressSelectionDispatch = true;
            try {
                SelectedCommit = null;
            }
            finally {
                _suppressSelectionDispatch = false;
            }
        }
    }

    private SearchResultProjection? ResolveSelectedSearchResult(string? previousSelectedSearchHash) {
        if (previousSelectedSearchHash != null) {
            var selectedSearchResult = SearchResults.FirstOrDefault(result => result.FullHash == previousSelectedSearchHash);
            if (selectedSearchResult != null) {
                return selectedSearchResult;
            }
        }

        return null;
    }

    private Action<GitKay.Core.App.Msg>? _dispatch;

    public void SetDispatch(Action<GitKay.Core.App.Msg> dispatch) {
        _dispatch = dispatch;
    }

    public void LogFirstPaint() {
        if (_firstPaintLogged) {
            return;
        }

        _firstPaintLogged = true;
        var firstPaintElapsed = Stopwatch.GetElapsedTime(_createdAtTicks);
        LogTiming($"first paint elapsed={firstPaintElapsed.TotalMilliseconds:F1}ms");
    }

    partial void OnSelectedCommitChanged(CommitProjection? value) {
        if (_suppressSelectionDispatch) {
            return;
        }

        if (value != null) {
            var startedAtTicks = Stopwatch.GetTimestamp();
            LogTiming($"commit click hash={value.FullHash}");
            _dispatch?.Invoke(GitKay.Core.App.Msg.NewSelectCommit(value.FullHash, startedAtTicks));
        }
    }

    partial void OnSearchQueryChanged(string value) {
        RaiseAdvancedFieldsChanged();
        if (_suppressSearchDispatch) {
            return;
        }

        _dispatch?.Invoke(GitKay.Core.App.Msg.NewSetSearchQuery(value));
        ScheduleSearchDebounce();
    }

    partial void OnShowStashesChanged(bool value) {
        ApplyRefVisibility();
        if (_suppressShowStashesDispatch) {
            return;
        }

        _dispatch?.Invoke(GitKay.Core.App.Msg.NewSetShowStashes(value));
    }

    partial void OnShowBranchRefsChanged(bool value) {
        ApplyRefVisibility();
        if (_suppressShowBranchRefsDispatch) {
            return;
        }

        _dispatch?.Invoke(GitKay.Core.App.Msg.NewSetShowBranchRefs(value));
    }

    partial void OnSelectedDiffContextLineCountChanged(DiffContextLineCountProjection? value) {
        if (_suppressDiffContextDispatch || value == null) {
            return;
        }

        DiffContextLineCount = value.Count;
        _dispatch?.Invoke(GitKay.Core.App.Msg.NewSetDiffContextLines(value.Count));
    }

    partial void OnSelectedSearchScopeChanged(SearchScopeProjection? value) {
        SearchPlaceholder = value?.Placeholder ?? SearchPlaceholder;
        OnPropertyChanged(nameof(IsCommitSearchMode));
        OnPropertyChanged(nameof(IsPathSearchMode));
        OnPropertyChanged(nameof(IsDiffSearchMode));
        if (_suppressSearchDispatch || value == null) {
            return;
        }

        _dispatch?.Invoke(GitKay.Core.App.Msg.NewSetSearchScope(value.Key));
        ScheduleSearchDebounce();
    }

    partial void OnSelectedSearchResultChanged(SearchResultProjection? value) {
        if (_suppressSearchSelectionDispatch) {
            return;
        }

        if (value == null) {
            return;
        }

        _selectedSearchResultHash = value.FullHash;
        var startedAtTicks = Stopwatch.GetTimestamp();
        LogTiming($"search result click hash={value.FullHash} summary={value.MatchSummary}");
        _dispatch?.Invoke(GitKay.Core.App.Msg.NewSelectCommit(value.FullHash, startedAtTicks));
    }

    partial void OnSelectedThemeModeChanged(ThemeModeProjection? value) {
        if (Avalonia.Application.Current is { } application) {
            application.RequestedThemeVariant = value?.Key switch {
                "light" => Avalonia.Styling.ThemeVariant.Light,
                "dark" => Avalonia.Styling.ThemeVariant.Dark,
                _ => Avalonia.Styling.ThemeVariant.Default,
            };
        }
    }

    partial void OnSelectedDiffPresentationModeChanged(DiffPresentationModeProjection? value) {
        SelectedDiffPresentationModeLabel = value?.Label ?? "Diff";
        OnPropertyChanged(nameof(IsUnifiedDiffMode));
        OnPropertyChanged(nameof(IsSideBySideDiffMode));
        OnPropertyChanged(nameof(IsNewDiffMode));
        OnPropertyChanged(nameof(IsOldDiffMode));
        RenderSelectedDiffRows();

        if (_suppressDiffPresentationDispatch || value == null) {
            return;
        }

        _dispatch?.Invoke(GitKay.Core.App.Msg.NewSetDiffPresentationMode(value.Key));
    }

    partial void OnSelectedDiffFileChanged(DiffFileProjection? value) {
        OnPropertyChanged(nameof(SelectedDiffFileListRow));
        if (_suppressDiffSelectionSync) {
            return;
        }

        SyncSelectedDiffSelection(value);

        if (value == null || SelectedCommit == null) {
            return;
        }

        LogTiming($"file click hash={SelectedCommit.FullHash} path={value.DisplayPath}");
        _dispatch?.Invoke(GitKay.Core.App.Msg.NewSelectDiffFile(SelectedCommit.FullHash, value.Key.OldPath, value.Key.NewPath));
    }

    partial void OnSelectedDiffRowChanged(IDiffRowProjection? value) {
        if (_suppressDiffSelectionSync) {
            return;
        }

        if (value is DiffFileHeaderProjection fileHeader) {
            SyncSelectedDiffSelection(fileHeader.File);
        }
    }

    [RelayCommand]
    private void FindInCommit() => StepDiffFind(+1);

    [RelayCommand]
    private void FindPreviousInCommit() => StepDiffFind(-1);

    [RelayCommand]
    private void ClearCommitFind() => CommitFindQuery = "";

    partial void OnCommitSearchStatusTextChanged(string value) => OnPropertyChanged(nameof(HasSearchNavigation));

    partial void OnCommitFindUseRegexChanged(bool value) {
        SelectedDiffRow = null;
        StepDiffFind(+1);
    }

    partial void OnCommitFindQueryChanged(string value) {
        UpdateCommitFindPlaceholder();
        // Incremental, like a browser: typing jumps to the first match in the selected commit's diff.
        SelectedDiffRow = null;
        StepDiffFind(+1);
    }

    /// <summary>Moves the diff selection to the next or previous row containing the find text, wrapping.</summary>
    private void StepDiffFind(int direction) {
        // Your own find text wins; an empty box borrows the commit search's diff term without writing to the box.
        var ownQuery = CommitFindQuery.Trim();
        var borrowed = string.IsNullOrWhiteSpace(ownQuery);
        var query = borrowed ? CommitSearchDiffTerm.Trim() : ownQuery;
        if (string.IsNullOrWhiteSpace(query)) {
            CommitFindStatusText = "";
            return;
        }

        var useRegex = borrowed ? CommitSearchDiffTermUseRegex : CommitFindUseRegex;
        var matcher = GitKay.Core.GitSearch.matcher(useRegex, query);
        var matches = SelectedDiffRows.Where(row => row is DiffLineProjection && RowMatchesFindQuery(row, text => matcher.Invoke(text))
                                                    || !borrowed && RowMatchesFindQuery(row, text => matcher.Invoke(text))).ToList();
        if (matches.Count == 0) {
            CommitFindStatusText = "No matches";
            return;
        }

        var currentIndex = SelectedDiffRow != null ? matches.FindIndex(row => ReferenceEquals(row, SelectedDiffRow)) : -1;
        var nextIndex = currentIndex < 0
            ? (direction > 0 ? 0 : matches.Count - 1)
            : (currentIndex + direction + matches.Count) % matches.Count;
        SelectedDiffRow = matches[nextIndex];
        CommitFindStatusText = borrowed ? $"{nextIndex + 1} of {matches.Count} · search" : $"{nextIndex + 1} of {matches.Count}";
    }

    partial void OnCommitSearchDiffTermChanged(string value) => UpdateCommitFindPlaceholder();

    private void UpdateCommitFindPlaceholder() {
        var term = CommitSearchDiffTerm.Trim();
        CommitFindPlaceholder = term.Length > 0
            ? $"Enter steps through “{term}”"
            : "Find in diff  (Ctrl+F or /)";
        if (string.IsNullOrWhiteSpace(CommitFindQuery) && term.Length == 0) CommitFindStatusText = "";
    }

    private static string PathTermFor(GitKay.Core.App.Model model) {
        foreach (var term in GitKay.Core.GitSearch.parseQuery(GitKay.Core.GitSearch.parseMode(model.SearchScopeKey), model.SearchQuery)) {
            if (term.Field.IsChangedPath) return term.Text;
        }

        return "";
    }

    private static string DiffTermFor(GitKay.Core.App.Model model) {
        foreach (var term in GitKay.Core.GitSearch.parseQuery(GitKay.Core.GitSearch.parseMode(model.SearchScopeKey), model.SearchQuery)) {
            if (term.Field.IsChangedLine) return term.Text;
        }

        return "";
    }

    private static (string Query, string ScopeKey) DiffHighlightFor(GitKay.Core.App.Model model) {
        var terms = GitKay.Core.GitSearch.parseQuery(GitKay.Core.GitSearch.parseMode(model.SearchScopeKey), model.SearchQuery);
        foreach (var term in terms) {
            if (term.Field.IsChangedLine) return (term.Text, "text");
        }

        foreach (var term in terms) {
            if (term.Field.IsChangedPath) return (term.Text, "path");
        }

        return ("", "all");
    }

    private GitKay.Core.GitSearch.Mode CurrentSearchMode => GitKay.Core.GitSearch.parseMode(SelectedSearchScope?.Key ?? "commit");

    [RelayCommand]
    private void SetSearchMode(string key) {
        var scope = SearchScopes.FirstOrDefault(candidate => candidate.Key == key);
        if (scope != null) SelectedSearchScope = scope;
    }

    partial void OnSearchUseRegexChanged(bool value) {
        if (_suppressSearchDispatch) return;
        _dispatch?.Invoke(GitKay.Core.App.Msg.NewSetSearchRegex(value));
        ScheduleSearchDebounce();
    }

    // ----- Advanced search: one input per field, kept in sync with prefixed terms in the query text. -----

    private string GetFieldText(GitKay.Core.GitSearch.Field field) =>
        string.Join(" ", GitKay.Core.GitSearch.parseQuery(CurrentSearchMode, SearchQuery).Where(term => term.Field.Equals(field)).Select(term => term.Text));

    private void SetFieldText(GitKay.Core.GitSearch.Field field, string? value) {
        var mode = CurrentSearchMode;
        var terms = GitKay.Core.GitSearch.parseQuery(mode, SearchQuery).Where(term => !term.Field.Equals(field)).ToList();
        if (!string.IsNullOrWhiteSpace(value)) {
            // Advanced inputs are always explicit fields, so a value with spaces stays one quoted term.
            terms.Add(new GitKay.Core.GitSearch.Term(field, value.Trim()));
        }

        SearchQuery = GitKay.Core.GitSearch.formatQuery(mode, Microsoft.FSharp.Collections.ListModule.OfSeq(terms));
    }

    public string AdvancedMessage { get => GetFieldText(GitKay.Core.GitSearch.Field.Message); set => SetFieldText(GitKay.Core.GitSearch.Field.Message, value); }
    public string AdvancedAuthor { get => GetFieldText(GitKay.Core.GitSearch.Field.Author); set => SetFieldText(GitKay.Core.GitSearch.Field.Author, value); }
    public string AdvancedHash { get => GetFieldText(GitKay.Core.GitSearch.Field.Hash); set => SetFieldText(GitKay.Core.GitSearch.Field.Hash, value); }
    public string AdvancedRef { get => GetFieldText(GitKay.Core.GitSearch.Field.Ref); set => SetFieldText(GitKay.Core.GitSearch.Field.Ref, value); }
    public string AdvancedPath { get => GetFieldText(GitKay.Core.GitSearch.Field.ChangedPath); set => SetFieldText(GitKay.Core.GitSearch.Field.ChangedPath, value); }
    public string AdvancedDiff { get => GetFieldText(GitKay.Core.GitSearch.Field.ChangedLine); set => SetFieldText(GitKay.Core.GitSearch.Field.ChangedLine, value); }
    public string AdvancedAfter { get => GetFieldText(GitKay.Core.GitSearch.Field.After); set => SetFieldText(GitKay.Core.GitSearch.Field.After, value); }
    public string AdvancedBefore { get => GetFieldText(GitKay.Core.GitSearch.Field.Before); set => SetFieldText(GitKay.Core.GitSearch.Field.Before, value); }

    public bool HasAuthorFilter => !string.IsNullOrEmpty(AdvancedAuthor);
    // Funnels mark explicit field filters only; a plain search isn't a column filter.
    public bool HasCommitFilter => !string.IsNullOrEmpty(AdvancedMessage) || !string.IsNullOrEmpty(AdvancedRef);
    public bool HasHashFilter => !string.IsNullOrEmpty(AdvancedHash);
    public bool HasDateFilter => !string.IsNullOrEmpty(AdvancedAfter) || !string.IsNullOrEmpty(AdvancedBefore);

    private void RaiseAdvancedFieldsChanged() {
        foreach (var name in new[] { nameof(AdvancedMessage), nameof(AdvancedAuthor), nameof(AdvancedHash), nameof(AdvancedRef), nameof(AdvancedPath),
                     nameof(AdvancedDiff), nameof(AdvancedAfter), nameof(AdvancedBefore), nameof(HasAuthorFilter), nameof(HasCommitFilter), nameof(HasHashFilter), nameof(HasDateFilter) }) {
            OnPropertyChanged(name);
        }
    }

    [RelayCommand]
    private void ClearAdvancedSearch() => SearchQuery = "";

    // ----- Column filters: right-click actions add a field term and switch to showing only matches. -----

    /// <summary>Adds or replaces a field filter (e.g. "author", "Jane") and runs the search showing only matches.</summary>
    public void ApplyColumnFilter(string field, string value) {
        var parsed = field switch {
            "author" => GitKay.Core.GitSearch.Field.Author,
            "message" => GitKay.Core.GitSearch.Field.Message,
            "hash" => GitKay.Core.GitSearch.Field.Hash,
            "ref" => GitKay.Core.GitSearch.Field.Ref,
            "after" => GitKay.Core.GitSearch.Field.After,
            "before" => GitKay.Core.GitSearch.Field.Before,
            _ => GitKay.Core.GitSearch.Field.CommitInfo,
        };
        SetFieldText(parsed, value);
        ShowOnlySearchMatches = true;
        Search();
    }

    [RelayCommand]
    private void ClearColumnFilter(string field) {
        switch (field) {
            case "author": AdvancedAuthor = ""; break;
            case "hash": AdvancedHash = ""; break;
            case "date": AdvancedAfter = ""; AdvancedBefore = ""; break;
            default:
                AdvancedMessage = "";
                AdvancedRef = "";
                SetFieldText(GitKay.Core.GitSearch.Field.CommitInfo, null);
                break;
        }

        if (string.IsNullOrWhiteSpace(SearchQuery)) ClearSearch(); else Search();
    }

    // ----- Recent searches, shown under the search box while typing (like JetBrains). -----

    public void LoadRecentSearches(IEnumerable<string> searches) {
        _recentSearches.Clear();
        _recentSearches.AddRange(searches.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.Ordinal).Take(MaxRecentSearches));
    }

    public IReadOnlyList<string> RecentSearches => _recentSearches;

    private void RememberSearch(string query) {
        var trimmed = query.Trim();
        if (string.IsNullOrWhiteSpace(trimmed)) return;
        _recentSearches.RemoveAll(existing => string.Equals(existing, trimmed, StringComparison.Ordinal));
        _recentSearches.Insert(0, trimmed);
        if (_recentSearches.Count > MaxRecentSearches) _recentSearches.RemoveRange(MaxRecentSearches, _recentSearches.Count - MaxRecentSearches);
        OnPropertyChanged(nameof(RecentSearches));
    }

    /// <summary>Refreshes the recent-search suggestions for the current text; opens them when any match.</summary>
    public void UpdateRecentSearchMatches(bool open) {
        var text = SearchQuery.Trim();
        RecentSearchMatches.Clear();
        foreach (var recent in _recentSearches.Where(recent =>
                     !string.Equals(recent, text, StringComparison.Ordinal)
                     && (text.Length == 0 || recent.Contains(text, StringComparison.OrdinalIgnoreCase))).Take(8)) {
            RecentSearchMatches.Add(recent);
        }

        IsRecentSearchesOpen = open && RecentSearchMatches.Count > 0;
    }

    public void ApplyRecentSearch(string query) {
        IsRecentSearchesOpen = false;
        SearchQuery = query;
        Search();
    }

    private bool _jumpToMatchWhenResultsArrive;
    private bool _modelSearchPending;
    private string _modelSearchQuery = "";

    /// <summary>
    /// Enter in the commit search box always means "find next". When results for this text aren't in yet
    /// (still searching, or the text just changed), the first match is selected as soon as they arrive.
    /// </summary>
    [RelayCommand]
    private void SearchOrNextCommit() {
        var query = SearchQuery.Trim();
        if (string.IsNullOrWhiteSpace(query)) {
            return;
        }

        var resultsAreCurrent = string.Equals(_lastRunSearchQuery, query, StringComparison.Ordinal);
        if (resultsAreCurrent && !_modelSearchPending) {
            StepCommitMatch(+1);
            return;
        }

        _jumpToMatchWhenResultsArrive = true;
        if (!(resultsAreCurrent && _modelSearchPending && string.Equals(_modelSearchQuery.Trim(), query, StringComparison.Ordinal))) {
            Search();
        }
    }

    [RelayCommand]
    private void FindNextCommit() => StepCommitMatch(+1);

    [RelayCommand]
    private void FindPreviousCommit() => StepCommitMatch(-1);

    /// <summary>Selects the next or previous matching commit in history order, wrapping.</summary>
    private void StepCommitMatch(int direction) {
        if (Commits.Count == 0) {
            return;
        }

        var count = Commits.Count;
        var current = SelectedCommit == null ? -1 : Commits.IndexOf(SelectedCommit);
        var start = current >= 0 ? current : direction > 0 ? -1 : count;
        for (var step = 1; step <= count; step++) {
            var index = ((start + direction * step) % count + count) % count;
            if (Commits[index].HasSearchMatch) {
                // Land on the change: once this commit's diff loads, select its first match.
                _revealSearchMatchInDiff = !string.IsNullOrWhiteSpace(CommitFindQuery) || !string.IsNullOrWhiteSpace(CommitSearchDiffTerm);
                SelectedCommit = Commits[index];
                return;
            }
        }
    }

    private void UpdateCommitSearchStatus(GitKay.Core.App.Model model) {
        // Only an applied search (with results) highlights the diff, so half-typed text doesn't flicker there.
        if (model.SearchResults != null || string.IsNullOrWhiteSpace(model.SearchQuery)) {
            var term = string.IsNullOrWhiteSpace(model.SearchQuery) ? "" : DiffTermFor(model);
            CommitSearchDiffTerm = term;
            CommitSearchPathTerm = string.IsNullOrWhiteSpace(model.SearchQuery) ? "" : PathTermFor(model);
            var highlightKey = $"{model.SearchScopeKey}\u0001{model.SearchUseRegex}\u0001{model.SearchQuery}";
            if (!string.Equals(_commitSearchHighlightKey, highlightKey, StringComparison.Ordinal)) {
                _commitSearchHighlightKey = highlightKey;
                CommitSearchHighlight = string.IsNullOrWhiteSpace(model.SearchQuery)
                    ? null
                    : new SearchHighlight(model.SearchQuery, model.SearchScopeKey, model.SearchUseRegex);
            }
            CommitSearchDiffTermUseRegex = model.SearchUseRegex;
        }

        _modelSearchPending = model.SearchStartedAtTicks != null;
        _modelSearchQuery = model.SearchQuery;
        if (_jumpToMatchWhenResultsArrive && !_modelSearchPending && model.SearchResults != null) {
            _jumpToMatchWhenResultsArrive = false;
            // Selecting dispatches SelectCommit; the status position updates on the next model update.
            StepCommitMatch(+1);
        }

        if (string.IsNullOrWhiteSpace(model.SearchQuery)) {
            CommitSearchStatusText = "";
            return;
        }

        if (model.SearchStartedAtTicks != null) {
            CommitSearchStatusText = "Searching…";
            return;
        }

        if (model.SearchResults == null) {
            CommitSearchStatusText = "";
            return;
        }

        var matches = model.SearchResults.Value.Length;
        if (matches == 0) {
            CommitSearchStatusText = "No matches";
            return;
        }

        var position = 0;
        if (SelectedCommit is { HasSearchMatch: true }) {
            foreach (var commit in Commits) {
                if (commit.HasSearchMatch) position++;
                if (ReferenceEquals(commit, SelectedCommit)) break;
            }
        }

        CommitSearchStatusText = position > 0 ? $"{position} of {matches}" : matches == 1 ? "1 match" : $"{matches} matches";
    }

    public void RereadRefs() => _dispatch?.Invoke(GitKay.Core.App.Msg.RereadRefs);

    [RelayCommand]
    private void SetDiffPresentationMode(string key) {
        var mode = DiffPresentationModes.FirstOrDefault(candidate => candidate.Key == key);
        if (mode != null) SelectedDiffPresentationMode = mode;
    }

    [RelayCommand]
    private void ToggleCommitDetails() => IsCommitDetailsExpanded = !IsCommitDetailsExpanded;

    public bool IsUnifiedDiffMode => SelectedDiffPresentationMode?.Key == "diff";
    public bool IsSideBySideDiffMode => SelectedDiffPresentationMode?.Key == "side-by-side";
    public bool IsNewDiffMode => SelectedDiffPresentationMode?.Key == "new";
    public bool IsOldDiffMode => SelectedDiffPresentationMode?.Key == "old";

    [RelayCommand]
    private void Search() {
        CancelSearchDebounce();
        var query = SearchQuery;
        var scopeKey = SelectedSearchScope?.Key ?? "commit";
        _lastRunSearchQuery = query.Trim();
        RememberSearch(query);
        var startedAtTicks = Stopwatch.GetTimestamp();
        LogTiming($"search click query={query} scope={scopeKey}");
        _dispatch?.Invoke(GitKay.Core.App.Msg.NewRunSearch(query, scopeKey, startedAtTicks));
    }

    [RelayCommand]
    private void ClearSearch() {
        CancelSearchDebounce();
        _dispatch?.Invoke(GitKay.Core.App.Msg.NewSetSearchQuery(""));
        _lastRunSearchQuery = null;
        _dispatch?.Invoke(GitKay.Core.App.Msg.NewRunSearch("", SelectedSearchScope?.Key ?? "commit", Stopwatch.GetTimestamp()));
    }

    partial void OnIsSearchPanelExpandedChanged(bool value) {
        // No-op for now
    }

    private void ScheduleSearchDebounce() {
        if (string.IsNullOrWhiteSpace(SearchQuery)) {
            CancelSearchDebounce();
            return;
        }

        CancelSearchDebounce();

        var cancellation = new CancellationTokenSource();
        _searchDebounceCancellation = cancellation;

        _ = DebounceSearchAsync(cancellation, TimeSpan.FromSeconds(Math.Max(0d, SearchDebounceSeconds)));
    }

    private async Task DebounceSearchAsync(CancellationTokenSource cancellation, TimeSpan delay) {
        try {
            await Task.Delay(delay, cancellation.Token);

            if (cancellation.IsCancellationRequested || !ReferenceEquals(_searchDebounceCancellation, cancellation)) {
                return;
            }

            var query = SearchQuery;
            if (string.IsNullOrWhiteSpace(query)) {
                return;
            }

            var scopeKey = SelectedSearchScope?.Key ?? "commit";
            _lastRunSearchQuery = query.Trim();
            var startedAtTicks = Stopwatch.GetTimestamp();
            _dispatch?.Invoke(GitKay.Core.App.Msg.NewRunSearch(query, scopeKey, startedAtTicks));
        }
        catch (OperationCanceledException) {
        }
        finally {
            if (ReferenceEquals(_searchDebounceCancellation, cancellation)) {
                _searchDebounceCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private void CancelSearchDebounce() {
        if (_searchDebounceCancellation == null) {
            return;
        }

        _searchDebounceCancellation.Cancel();
        _searchDebounceCancellation = null;
    }

    private void SyncSelectedDiffFiles(IReadOnlyList<GitKay.Core.GitService.DiffFileSummary> summaries, bool clearContent) {
        var existingByKey = SelectedDiffFiles.ToDictionary(file => file.Key);

        SelectedDiffFiles.Clear();
        foreach (var summary in summaries) {
            var key = new DiffFileKey(summary.OldPath, summary.NewPath);

            if (!existingByKey.TryGetValue(key, out var fileProjection)) {
                fileProjection = new DiffFileProjection(summary);
            }
            else {
                fileProjection.UpdateSummary(summary);
                if (clearContent) {
                    fileProjection.ClearContent();
                }
            }

            SelectedDiffFiles.Add(fileProjection);
        }

        _collapsedDiffFolders.Clear();
        _toggledAllFilesFolders.Clear();
        RebuildDiffFileListRows();
    }

    private static bool MatchesKey(DiffFileKey uiKey, GitKay.Core.GitService.DiffFileKey coreKey) =>
        uiKey.OldPath == coreKey.OldPath && uiKey.NewPath == coreKey.NewPath;

    private DiffFileProjection? ResolveSelectedDiffFile(GitKay.Core.App.Model model, DiffFileKey? previousSelectedDiffFileKey) {
        if (model.SelectedDiffFileKey != null) {
            var key = model.SelectedDiffFileKey.Value;
            var selectedDiffFile = SelectedDiffFiles.FirstOrDefault(file => MatchesKey(file.Key, key));
            if (selectedDiffFile != null) {
                return selectedDiffFile;
            }
        }

        if (previousSelectedDiffFileKey != null) {
            var key = previousSelectedDiffFileKey.Value;
            var selectedDiffFile = SelectedDiffFiles.FirstOrDefault(file => file.Key == key);
            if (selectedDiffFile != null) {
                return selectedDiffFile;
            }
        }

        return SelectedDiffFiles.FirstOrDefault();
    }

    private static GitKay.Core.App.FileExpansion? FindExpansion(
        Microsoft.FSharp.Collections.FSharpMap<GitKay.Core.GitService.DiffFileKey, GitKay.Core.App.FileExpansion>? expansions,
        DiffFileKey key) {
        if (expansions == null) return null;
        var found = expansions.TryFind(new GitKay.Core.GitService.DiffFileKey(key.OldPath, key.NewPath));
        return found == null ? null : found.Value;
    }

    private bool SyncSelectedDiffExpansions(
        Microsoft.FSharp.Collections.FSharpMap<GitKay.Core.GitService.DiffFileKey, GitKay.Core.App.FileExpansion> expansions) {
        var changed = false;
        foreach (var fileProjection in SelectedDiffFiles) {
            if (fileProjection.IsLoaded) {
                changed |= fileProjection.ApplyExpansion(FindExpansion(expansions, fileProjection.Key));
            }
        }

        return changed;
    }

    private void SyncSelectedDiffFileContents(
        IReadOnlyList<GitKay.Core.Models.FileDiff> files,
        Microsoft.FSharp.Collections.FSharpMap<GitKay.Core.GitService.DiffFileKey, GitKay.Core.App.FileExpansion>? expansions = null) {
        var filesByKey = files.ToDictionary(
            file => new DiffFileKey(file.OldPath, file.NewPath),
            file => file);

        foreach (var fileProjection in SelectedDiffFiles) {
            if (filesByKey.TryGetValue(fileProjection.Key, out var loadedFile)) {
                fileProjection.ApplyContent(loadedFile, FindExpansion(expansions, fileProjection.Key));
            }
            else {
                fileProjection.ClearContent();
            }
        }
    }

    private void ApplyCommitSearchMatches(IEnumerable<GitKay.Core.GitSearch.Result>? results) {
        var resultsByHash = results?.ToDictionary(result => result.Commit.Hash);

        foreach (var commit in Commits) {
            if (resultsByHash != null && resultsByHash.TryGetValue(commit.FullHash, out var result)) {
                commit.ApplySearchMatch(result);
            }
            else {
                commit.ApplySearchMatch(null);
            }
        }
    }

    private void SyncSelectedSearchResult(SearchResultProjection? selectedSearchResult) {
        _suppressSearchSelectionDispatch = true;
        try {
            SelectedSearchResult = selectedSearchResult;
        }
        finally {
            _suppressSearchSelectionDispatch = false;
        }
    }

    private void RenderSelectedDiffRows() {
        var rows = new List<IDiffRowProjection>();
        var mode = SelectedDiffPresentationMode?.Key ?? "diff";
        foreach (var file in SelectedDiffFiles)
            DiffRowBuilder.AppendFile(rows, file, mode);

        SelectedDiffRows.Clear();
        SelectedDiffRows.AddRange(rows);
    }

    [RelayCommand]
    private void ToggleDiffFileCollapsed(DiffFileProjection file) {
        file.IsCollapsed = !file.IsCollapsed;
        RenderSelectedDiffRows();
    }

    [RelayCommand]
    private void ToggleDiffFileContext(DiffFileProjection file) {
        if (_selectedDiffHash == null) {
            return;
        }

        var key = new GitKay.Core.GitService.DiffFileKey(file.Key.OldPath, file.Key.NewPath);
        if (file.HasHiddenContext) {
            _dispatch?.Invoke(GitKay.Core.App.Msg.NewExpandDiffFile(_selectedDiffHash, key, Stopwatch.GetTimestamp()));
        }
        else if (file.HasRevealedContext) {
            _dispatch?.Invoke(GitKay.Core.App.Msg.NewCollapseDiffFileContext(_selectedDiffHash, key));
        }
    }

    [RelayCommand]
    private void ExpandDiffGap(DiffGapExpansionRequest request) {
        if (_selectedDiffHash == null) {
            return;
        }

        _dispatch?.Invoke(GitKay.Core.App.Msg.NewExpandDiffGap(
            _selectedDiffHash,
            request.Gap,
            request.Direction,
            Stopwatch.GetTimestamp()));
    }

    private void SyncSelectedDiffSelection(DiffFileProjection? selectedDiffFile) {
        _suppressDiffSelectionSync = true;
        try {
            SelectedDiffFile = selectedDiffFile;
            SelectedDiffRow = selectedDiffFile?.Header;
        }
        finally {
            _suppressDiffSelectionSync = false;
        }
    }

    private static bool RowMatchesFindQuery(IDiffRowProjection row, Func<string, bool> matches) =>
        row switch {
            DiffFileHeaderProjection fileHeader => matches(fileHeader.DisplayPath),
            DiffHunkHeaderProjection hunkHeader => matches(hunkHeader.Header),
            DiffLineProjection line => matches(line.Content),
            _ => false,
        };
}
