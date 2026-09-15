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

public sealed class DiffPresentationModeProjection(GitKay.Core.DiffLayout layout) {
    public GitKay.Core.DiffLayout Layout { get; } = layout;
    public string Key => GitKay.Core.DiffLayoutModule.key(Layout);
    public string Label => GitKay.Core.DiffLayoutModule.label(Layout);
}

public sealed class ThemeModeProjection(GitKay.Core.ThemeMode mode) {
    public GitKay.Core.ThemeMode Mode { get; } = mode;
    public string Key => GitKay.Core.ThemeModeModule.key(Mode);
    public string Label => GitKay.Core.ThemeModeModule.label(Mode);
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
    /// <summary>What the applied commit search marks in the diff, and the search it was built from.</summary>
    private GitKay.Core.GitSearch.DiffMark _diffMark = GitKay.Core.GitSearch.noDiffMark;
    private string? _diffMarkKey;
    private CancellationTokenSource? _searchDebounceCancellation;

    public ObservableCollection<DiffPresentationModeProjection> DiffPresentationModes { get; } =
        new(GitKay.Core.DiffLayoutModule.all.Select(layout => new DiffPresentationModeProjection(layout)));

    public ObservableCollection<ThemeModeProjection> ThemeModes { get; } =
        new(GitKay.Core.ThemeModeModule.all.Select(mode => new ThemeModeProjection(mode)));

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
    [ObservableProperty] private string _commitRowFontFamily = GitKay.Core.SettingsModule.defaults.CommitRowFontFamily;
    [ObservableProperty] private string _commitRowMonoFontFamily = GitKay.Core.SettingsModule.defaults.CommitRowMonoFontFamily;
    [ObservableProperty] private double _commitRowTextFontSize = GitKay.Core.SettingsModule.defaults.CommitRowTextFontSize;
    [ObservableProperty] private double _commitRowMetaFontSize = GitKay.Core.SettingsModule.defaults.CommitRowMetaFontSize;
    [ObservableProperty] private double _commitRowBadgeFontSize = GitKay.Core.SettingsModule.defaults.CommitRowBadgeFontSize;
    [ObservableProperty] private bool _showBranchRefs;
    [ObservableProperty] private bool _showStashes;
    [ObservableProperty] private int _diffContextLineCount = GitKay.Core.SettingsModule.defaults.DiffContextLines;
    [ObservableProperty] private string _searchQuery = "";
    [ObservableProperty] private double _searchDebounceSeconds = GitKay.Core.SettingsModule.defaults.SearchDebounceSeconds;
    [ObservableProperty] private string _commitFindQuery = "";
    [ObservableProperty] private SearchScopeProjection? _selectedSearchScope;
    [ObservableProperty] private bool _hasSearchResults;
    [ObservableProperty] private bool _isSearchPanelExpanded;
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
    private Microsoft.FSharp.Collections.FSharpList<string> _recentSearches = Microsoft.FSharp.Collections.FSharpList<string>.Empty;
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
    [ObservableProperty] private GitKay.Core.GitSearch.Highlight? _commitSearchHighlight;
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

    public void ApplySettings(GitKay.Core.Settings settings) {
        var normalized = GitKay.Core.SettingsModule.normalize(settings);

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
            SelectedDiffPresentationMode = PresentationModeFor(normalized.DiffLayout);
            SelectedThemeMode = ThemeModes.FirstOrDefault(mode => mode.Mode.Equals(normalized.Theme)) ?? ThemeModes.First();
        }
        finally {
            _suppressDiffPresentationDispatch = false;
            _suppressDiffContextDispatch = false;
            _suppressShowStashesDispatch = false;
            _suppressShowBranchRefsDispatch = false;
        }
    }

    public GitKay.Core.Settings CaptureSettings() => GitKay.Core.SettingsModule.normalize(new GitKay.Core.Settings(
        ShowBranchRefs,
        ShowStashes,
        DiffContextLineCount,
        DiffLayout,
        CommitRowFontFamily,
        CommitRowMonoFontFamily,
        CommitRowTextFontSize,
        CommitRowMetaFontSize,
        CommitRowBadgeFontSize,
        SearchDebounceSeconds,
        SelectedThemeMode?.Mode ?? GitKay.Core.SettingsModule.defaults.Theme));

    /// <summary>The diff layout chosen in the view menu.</summary>
    public GitKay.Core.DiffLayout DiffLayout => SelectedDiffPresentationMode?.Layout ?? GitKay.Core.SettingsModule.defaults.DiffLayout;

    private DiffPresentationModeProjection PresentationModeFor(GitKay.Core.DiffLayout layout) =>
        DiffPresentationModes.FirstOrDefault(mode => mode.Layout.Equals(layout)) ?? DiffPresentationModes.First();

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

    /// <summary>Stands in for a commit hash while the uncommitted changes are shown; never passed to git.</summary>
    private const string WorkingTreeDiffId = "\u0001working-tree";
    private readonly HashSet<string> _collapsedDiffSections = new(StringComparer.Ordinal);

    public bool IsWorkingTreeDiffShown => _selectedDiffHash == WorkingTreeDiffId;

    /// <summary>Shows the uncommitted changes as Staged, Unstaged and Untracked files, keeping collapsed files and the selected file across refreshes.</summary>
    private void UpdateWorkingTreeDiffState(GitKay.Core.App.Model model) {
        var changes = model.WorkingTreeChanges?.Value;
        var wasShown = IsWorkingTreeDiffShown;
        if (wasShown && ReferenceEquals(_selectedDiffFilesSource, changes)) return;

        if (!wasShown) {
            SaveCommitViewState();
            _collapsedDiffSections.Clear();
        }
        var previousKey = wasShown ? SelectedDiffFile?.Key : null;
        var collapsed = wasShown ? SelectedDiffFiles.Where(file => file.IsCollapsed).Select(file => file.Key).ToHashSet() : [];
        var existing = SelectedDiffFiles.ToDictionary(file => file.Key);

        SyncSelectedDiffSelection(null);
        SelectedDiffRows.Clear();
        SelectedDiffFiles.Clear();
        _selectedDiffHash = WorkingTreeDiffId;
        _selectedDiffFilesSource = changes;
        _selectedDiffFileSource = changes;
        _diffExpansionsSource = model.DiffExpansions;

        if (changes != null) {
            var markers = changes.Entries.ToDictionary(entry => entry.Path, GitKay.Core.WorkingTree.marker, StringComparer.Ordinal);
            foreach (var (section, files) in GitKay.Core.GitService.workingTreeSections(changes)) {
                var sectionName = GitKay.Core.WorkingTree.sectionName(section);
                foreach (var diff in files) {
                    var key = new DiffFileKey(diff.OldPath, diff.NewPath, sectionName);
                    var summary = new GitKay.Core.GitService.DiffFileSummary(diff.OldPath, diff.NewPath, GitKay.Core.FileChange.displayPath(diff.OldPath, diff.NewPath));
                    if (!existing.TryGetValue(key, out var file)) file = new DiffFileProjection(summary, sectionName);
                    else file.UpdateSummary(summary);
                    file.ApplyContent(diff);
                    file.IsCollapsed = collapsed.Contains(key);
                    file.Marker = markers.GetValueOrDefault(DiffFileTree.PathOf(file), "");
                    SelectedDiffFiles.Add(file);
                }
            }
        }

        RebuildDiffFileListRows();
        RenderSelectedDiffRows();
        var selected = SelectedDiffFiles.FirstOrDefault(file => file.Key == previousKey) ?? SelectedDiffFiles.FirstOrDefault();
        SyncSelectedDiffSelection(selected);
        _selectedDiffFileKey = selected?.Key;
        ApplyDiffMark();
        OnPropertyChanged(nameof(IsWorkingTreeDiffShown));
    }

    private void UpdateDiffState(GitKay.Core.App.Model model) {
        if (model.IsWorkingTreeSelected) {
            UpdateWorkingTreeDiffState(model);
            return;
        }
        var leavingWorkingTree = IsWorkingTreeDiffShown;
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

        // Mark in the diff only what the commit search looked for in diffs (its diff: or path: terms).
        var diffMarkKey = $"{model.SearchScopeKey}\u0001{model.SearchUseRegex}\u0001{model.SearchQuery}";
        var searchStateChanged = !string.Equals(_diffMarkKey, diffMarkKey, StringComparison.Ordinal);
        if (searchStateChanged) {
            _diffMark = GitKay.Core.GitSearch.diffMark(GitKay.Core.GitSearch.parseMode(model.SearchScopeKey), model.SearchUseRegex, model.SearchQuery);
            _diffMarkKey = diffMarkKey;
        }

        var diffCollectionChanged =
            !string.Equals(_selectedDiffHash, selectedDiffHash, StringComparison.Ordinal)
            || !ReferenceEquals(_selectedDiffFilesSource, selectedDiffFilesSource);

        if (leavingWorkingTree) OnPropertyChanged(nameof(IsWorkingTreeDiffShown));
        if (selectedDiffHash == null) {
            if (!leavingWorkingTree) SaveCommitViewState();
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
            return;
        }

        if (diffCollectionChanged) {
            if (!leavingWorkingTree) SaveCommitViewState();
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

            if (searchStateChanged || diffContentChanged || diffCollectionChanged) ApplyDiffMark();
        }

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
        var selectedPresentationMode = PresentationModeFor(model.DiffLayout);

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

    private void ApplyDiffMark() {
        foreach (var file in SelectedDiffFiles) file.ApplyDiffMark(_diffMark);
    }

    private void UpdateCommits(GitKay.Core.App.Model model) {
        var searchResultsSource = model.SearchResults != null ? (object?)model.SearchResults.Value : null;
        var commitsChanged = !ReferenceEquals(_commitsSource, model.Commits);
        var searchResultsChanged = !ReferenceEquals(_commitSearchResultsSource, searchResultsSource);

        var workingTreeChanged = !ReferenceEquals(_workingTreeSource, model.WorkingTree);
        if (commitsChanged && Commits.Count > 0 && Commits[0].IsWorkingTree) Commits.RemoveAt(0);

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

        if (commitsChanged || workingTreeChanged) SyncWorkingTreeRow(model);

        // While a new search runs, keep the previous matches instead of flashing every commit back.
        var searchPending = model.SearchStartedAtTicks != null && model.SearchResults == null;
        if ((commitsChanged || searchResultsChanged) && !searchPending) {
            ApplyCommitSearchMatches(model.SearchResults?.Value);
            _commitSearchResultsSource = searchResultsSource;
        }

        IsCommitSearchActive = !string.IsNullOrWhiteSpace(model.SearchQuery) && (model.SearchResults != null || searchPending);
    }

    private object? _workingTreeSource;
    private readonly CommitProjection _workingTreeRow = new() { IsWorkingTree = true };

    /// <summary>Keeps the uncommitted changes row first in the list while the working tree differs from HEAD.</summary>
    private void SyncWorkingTreeRow(GitKay.Core.App.Model model) {
        _workingTreeSource = model.WorkingTree;
        var shown = Commits.Count > 0 && Commits[0].IsWorkingTree;
        // Only shown above history that starts at HEAD: a filtered or other-branch history has nowhere to attach it.
        var head = Commits.Skip(shown ? 1 : 0).FirstOrDefault();
        var wanted = !model.WorkingTree.IsEmpty && (head == null || head.LocalBranches.Any(branch => branch.IsCurrentHead) || model.IsWorkingTreeSelected);
        if (wanted) {
            _workingTreeRow.UpdateWorkingTree(model.WorkingTree, head?.Lane ?? 0);
            if (!shown) Commits.Insert(0, _workingTreeRow);
        }
        else if (shown) {
            Commits.RemoveAt(0);
        }
    }

    public CommitProjection WorkingTreeRow => _workingTreeRow;

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

        var selected = SelectedCommit is { IsWorkingTree: true } ? null : SelectedCommit;
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

        if (model.IsWorkingTreeSelected && Commits.Count > 0 && Commits[0].IsWorkingTree) {
            selectedCommit = Commits[0];
        }
        else if (model.SelectedCommitHash != null) {
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

    /// <summary>Rereads git status: the working tree changed on disk, or the window regained focus.</summary>
    public void RefreshWorkingTree() => _dispatch?.Invoke(GitKay.Core.App.Msg.RefreshWorkingTree);

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

        if (value is { IsWorkingTree: true }) {
            _dispatch?.Invoke(GitKay.Core.App.Msg.NewSelectWorkingTree(Stopwatch.GetTimestamp()));
        }
        else if (value != null) {
            var startedAtTicks = Stopwatch.GetTimestamp();
            LogTiming($"commit click hash={value.FullHash}");
            _dispatch?.Invoke(GitKay.Core.App.Msg.NewSelectCommit(value.FullHash, startedAtTicks));
        }
    }

    /// <summary>Why part of the search is being ignored (an unparseable date), or empty.</summary>
    [ObservableProperty] private string _searchValidationMessage = "";
    [ObservableProperty] private bool _isAfterDateInvalid;
    [ObservableProperty] private bool _isBeforeDateInvalid;
    public bool HasSearchValidationMessage => SearchValidationMessage.Length > 0;
    partial void OnSearchValidationMessageChanged(string value) => OnPropertyChanged(nameof(HasSearchValidationMessage));

    private void ValidateSearchQuery(string query) {
        var problems = GitKay.Core.GitSearch.invalidDates(DateTimeOffset.Now, CurrentSearchMode, query).ToList();
        IsAfterDateInvalid = problems.Any(problem => problem.Field.IsAfter);
        IsBeforeDateInvalid = problems.Any(problem => problem.Field.IsBefore);
        SearchValidationMessage = string.Join("  ·  ", problems.Select(GitKay.Core.GitSearch.describeDateProblem));
    }

    partial void OnSearchQueryChanged(string value) {
        ValidateSearchQuery(value);
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
            application.RequestedThemeVariant =
                value?.Mode is { IsLightTheme: true } ? Avalonia.Styling.ThemeVariant.Light
                : value?.Mode is { IsDarkTheme: true } ? Avalonia.Styling.ThemeVariant.Dark
                : Avalonia.Styling.ThemeVariant.Default;
        }
    }

    partial void OnSelectedDiffPresentationModeChanged(DiffPresentationModeProjection? value) {
        SelectedDiffPresentationModeLabel = value?.Label ?? "Diff";
        OnPropertyChanged(nameof(IsUnifiedDiffMode));
        OnPropertyChanged(nameof(IsSideBySideDiffMode));
        OnPropertyChanged(nameof(IsNewDiffMode));
        OnPropertyChanged(nameof(IsOldDiffMode));
        OnPropertyChanged(nameof(DiffLayout));
        RenderSelectedDiffRows();

        if (_suppressDiffPresentationDispatch || value == null) {
            return;
        }

        _dispatch?.Invoke(GitKay.Core.App.Msg.NewSetDiffLayout(value.Layout));
    }

    partial void OnSelectedDiffFileChanged(DiffFileProjection? value) {
        OnPropertyChanged(nameof(SelectedDiffFileListRow));
        if (_suppressDiffSelectionSync) {
            return;
        }

        SyncSelectedDiffSelection(value);

        if (value == null || SelectedCommit is null or { IsWorkingTree: true }) {
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

    /// <summary>Set by ? and #: Enter and n step backwards through diff matches, N forwards, as in vim.</summary>
    public bool DiffFindBackward { get; set; }

    [RelayCommand]
    private void FindInCommit() => StepDiffFind(DiffFindBackward ? -1 : +1);

    [RelayCommand]
    private void FindPreviousInCommit() => StepDiffFind(DiffFindBackward ? +1 : -1);

    private bool _steppingFromSelection;

    /// <summary>vim's * and #: finds the whole word in the diff, starting from the focused row rather than the top.</summary>
    public void FindWordInDiff(string word, bool forward) {
        static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';
        var pattern = System.Text.RegularExpressions.Regex.Escape(word);
        if (IsWord(word[0])) pattern = @"\b" + pattern;
        if (IsWord(word[^1])) pattern += @"\b";
        _steppingFromSelection = true;
        try {
            CommitFindUseRegex = true;
            CommitFindQuery = pattern;
        }
        finally {
            _steppingFromSelection = false;
        }
        DiffFindBackward = !forward;
        StepDiffFind(forward ? +1 : -1);
    }

    [RelayCommand]
    private void ClearCommitFind() => CommitFindQuery = "";

    partial void OnCommitSearchStatusTextChanged(string value) => OnPropertyChanged(nameof(HasSearchNavigation));

    partial void OnCommitFindUseRegexChanged(bool value) {
        if (_steppingFromSelection) return;
        SelectedDiffRow = null;
        StepDiffFind(+1);
    }

    partial void OnCommitFindQueryChanged(string value) {
        UpdateCommitFindPlaceholder();
        if (_steppingFromSelection) return;
        // Incremental, like a browser: typing jumps to the first match in the selected commit's diff.
        SelectedDiffRow = null;
        StepDiffFind(+1);
    }

    /// <summary>
    /// The / and ? prompt: sets the find text to a regex without resetting the selection, then moves from
    /// <paramref name="origin"/> to the nearest matching row in the search direction, including the origin itself.
    /// </summary>
    public void SearchDiffFrom(string regex, bool forward, IDiffRowProjection? origin) {
        _steppingFromSelection = true;
        try {
            CommitFindUseRegex = true;
            CommitFindQuery = regex;
        }
        finally {
            _steppingFromSelection = false;
        }
        DiffFindBackward = !forward;
        SelectedDiffRow = origin;
        StepDiffFind(forward ? +1 : -1, includeCurrent: true);
    }

    /// <summary>Puts back the find text and regex setting a cancelled / search replaced, without moving the selection.</summary>
    public void RestoreDiffFind(string query, bool useRegex, bool backward) {
        _steppingFromSelection = true;
        try {
            CommitFindUseRegex = useRegex;
            CommitFindQuery = query;
        }
        finally {
            _steppingFromSelection = false;
        }
        DiffFindBackward = backward;
    }

    /// <summary>Moves the diff selection to the next or previous row containing the find text, wrapping.</summary>
    private void StepDiffFind(int direction, bool includeCurrent = false) {
        if (GitKay.Core.DiffFind.choose(CommitFindQuery, CommitFindUseRegex, CommitSearchDiffTerm, CommitSearchDiffTermUseRegex) is not { } found) {
            CommitFindStatusText = "";
            return;
        }

        var find = found.Value;
        var rows = SelectedDiffRows;
        var includesHeaders = GitKay.Core.DiffFind.includesHeaders(find);
        var matches = GitKay.Kit.Cycle.positionsWhere(rows.Count, index =>
            rows[index] is DiffLineProjection || includesHeaders ? RowMatchesFindQuery(rows[index], find.Query.IsMatch) : false);
        var focus = SelectedDiffRow == null ? -1 : rows.IndexOf(SelectedDiffRow);
        var next = GitKay.Kit.Cycle.step(matches, focus, direction > 0 ? GitKay.Kit.Direction.Forward : GitKay.Kit.Direction.Backward, includeCurrent);
        if (next >= 0) SelectedDiffRow = rows[matches[next]];
        CommitFindStatusText = GitKay.Core.DiffFind.status(find, next, matches.Length);
    }

    partial void OnCommitSearchDiffTermChanged(string value) => UpdateCommitFindPlaceholder();

    private void UpdateCommitFindPlaceholder() {
        var term = CommitSearchDiffTerm.Trim();
        CommitFindPlaceholder = term.Length > 0
            ? $"Enter steps through “{term}”"
            : "Find in diff  (Ctrl+F)";
        if (string.IsNullOrWhiteSpace(CommitFindQuery) && term.Length == 0) CommitFindStatusText = "";
    }

    private static string FirstTermOf(GitKay.Core.App.Model model, GitKay.Core.GitSearch.Field field) =>
        GitKay.Core.GitSearch.firstTermText(GitKay.Core.GitSearch.parseMode(model.SearchScopeKey), model.SearchQuery, field) is { } text ? text.Value : "";

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
        GitKay.Core.GitSearch.fieldText(CurrentSearchMode, SearchQuery, field);

    private void SetFieldText(GitKay.Core.GitSearch.Field field, string? value) =>
        SearchQuery = GitKay.Core.GitSearch.withFieldText(CurrentSearchMode, SearchQuery, field, value ?? "");

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
        SetFieldText(GitKay.Core.GitSearch.fieldNamed(field), value);
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

    public void LoadRecentSearches(IEnumerable<string> searches) => _recentSearches = GitKay.Core.GitSearch.recentSearches(searches);

    public IReadOnlyList<string> RecentSearches => _recentSearches.ToList();

    private void RememberSearch(string query) {
        _recentSearches = GitKay.Core.GitSearch.rememberSearch(query, _recentSearches);
        OnPropertyChanged(nameof(RecentSearches));
    }

    /// <summary>Refreshes the recent-search suggestions for the current text; opens them when any match.</summary>
    public void UpdateRecentSearchMatches(bool open) {
        RecentSearchMatches.Clear();
        foreach (var recent in GitKay.Core.GitSearch.suggestSearches(SearchQuery, _recentSearches)) RecentSearchMatches.Add(recent);
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
    private int[] CommitMatchPositions() => GitKay.Kit.Cycle.positionsWhere(Commits.Count, index => Commits[index].HasSearchMatch);

    private void StepCommitMatch(int direction) {
        var positions = CommitMatchPositions();
        var focus = SelectedCommit == null ? -1 : Commits.IndexOf(SelectedCommit);
        var next = GitKay.Kit.Cycle.step(positions, focus, direction > 0 ? GitKay.Kit.Direction.Forward : GitKay.Kit.Direction.Backward, false);
        if (next < 0) return;
        // Land on the change: once this commit's diff loads, select its first match.
        _revealSearchMatchInDiff = !string.IsNullOrWhiteSpace(CommitFindQuery) || !string.IsNullOrWhiteSpace(CommitSearchDiffTerm);
        SelectedCommit = Commits[positions[next]];
    }

    private void UpdateCommitSearchStatus(GitKay.Core.App.Model model) {
        // Only an applied search (with results) highlights the diff, so half-typed text doesn't flicker there.
        if (model.SearchResults != null || string.IsNullOrWhiteSpace(model.SearchQuery)) {
            var term = FirstTermOf(model, GitKay.Core.GitSearch.Field.ChangedLine);
            CommitSearchDiffTerm = term;
            CommitSearchPathTerm = FirstTermOf(model, GitKay.Core.GitSearch.Field.ChangedPath);
            var highlightKey = $"{model.SearchScopeKey}\u0001{model.SearchUseRegex}\u0001{model.SearchQuery}";
            if (!string.Equals(_commitSearchHighlightKey, highlightKey, StringComparison.Ordinal)) {
                _commitSearchHighlightKey = highlightKey;
                CommitSearchHighlight = string.IsNullOrWhiteSpace(model.SearchQuery)
                    ? null
                    : GitKay.Core.GitSearch.highlight(GitKay.Core.GitSearch.parseMode(model.SearchScopeKey), model.SearchUseRegex, model.SearchQuery);
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
            IsSearchRunning = false;
            CommitSearchStatusText = "";
            return;
        }

        var wasSearchRunning = IsSearchRunning;
        IsSearchRunning = model.SearchStartedAtTicks != null;
        // A finished search leaves libgit2's freed allocations resident; hand them back to the OS.
        if (wasSearchRunning && !IsSearchRunning) System.Threading.Tasks.Task.Run(NativeMemory.TrimNow);
        GitKay.Core.GitSearch.Progress progress;
        if (model.SearchStartedAtTicks != null) {
            progress = GitKay.Core.GitSearch.Progress.NewSearching(model.SearchResults?.Value.Length ?? 0, model.SearchProgress);
        }
        else if (model.SearchResults == null) {
            progress = GitKay.Core.GitSearch.Progress.NotSearching;
        }
        else {
            var positions = CommitMatchPositions();
            var selected = SelectedCommit is { HasSearchMatch: true } ? Array.BinarySearch(positions, Commits.IndexOf(SelectedCommit)) : -1;
            progress = GitKay.Core.GitSearch.Progress.NewSearched(model.SearchResults.Value.Length, Math.Max(-1, selected));
        }

        CommitSearchStatusText = GitKay.Core.GitSearch.progressText(progress);
    }

    public void RereadRefs() => _dispatch?.Invoke(GitKay.Core.App.Msg.RereadRefs);

    [RelayCommand]
    private void SetDiffPresentationMode(string key) {
        if (GitKay.Core.DiffLayoutModule.tryParse(key) is { } layout) SelectedDiffPresentationMode = PresentationModeFor(layout.Value);
    }

    [RelayCommand]
    private void ToggleCommitDetails() => IsCommitDetailsExpanded = !IsCommitDetailsExpanded;

    public bool IsUnifiedDiffMode => DiffLayout.IsUnified;
    public bool IsSideBySideDiffMode => DiffLayout.IsSideBySide;
    public bool IsNewDiffMode => DiffLayout.IsNewFile;
    public bool IsOldDiffMode => DiffLayout.IsOldFile;

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

    /// <summary>A commit search is running; Esc cancels it.</summary>
    [ObservableProperty] private bool _isSearchRunning;

    /// <summary>Stops the running search and keeps its query.</summary>
    public void CancelSearch() {
        CancelSearchDebounce();
        _dispatch?.Invoke(GitKay.Core.App.Msg.CancelSearch);
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
        var mode = DiffLayout;
        foreach (var section in SelectedDiffFiles.GroupBy(file => file.Key.Section)) {
            var sectionCollapsed = false;
            if (section.Key.Length > 0) {
                var name = section.Key;
                sectionCollapsed = _collapsedDiffSections.Contains(name);
                rows.Add(new DiffSectionHeaderProjection(name, section.Count(), sectionCollapsed, () => ToggleDiffSection(name)));
            }
            if (sectionCollapsed) continue;
            foreach (var file in section)
                DiffRowBuilder.AppendFile(rows, file, mode);
        }

        SelectedDiffRows.Clear();
        SelectedDiffRows.AddRange(rows);
    }

    /// <summary>Collapses or expands an uncommitted changes section in the diff pane.</summary>
    public void ToggleDiffSection(string section) {
        if (!_collapsedDiffSections.Remove(section)) _collapsedDiffSections.Add(section);
        RenderSelectedDiffRows();
    }

    [RelayCommand]
    private void ToggleDiffFileCollapsed(DiffFileProjection file) {
        file.IsCollapsed = !file.IsCollapsed;
        RenderSelectedDiffRows();
    }

    [RelayCommand]
    private void ToggleDiffFileContext(DiffFileProjection file) {
        if (_selectedDiffHash is null or WorkingTreeDiffId) {
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
        if (_selectedDiffHash is null or WorkingTreeDiffId) {
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
