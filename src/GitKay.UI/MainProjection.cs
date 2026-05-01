using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Elmish.Glue.Core;
using GitKay.Core;

namespace GitKay.UI;

public partial class MainProjection : ObservableObject, IProjection<GitKay.Core.App.Model, GitKay.Core.App.Msg>
{
    private readonly long _createdAtTicks = Stopwatch.GetTimestamp();
    private bool _firstPaintLogged;
    private bool _suppressSelectionDispatch;
    private bool _suppressDiffSelectionSync;
    private bool _suppressSearchDispatch;
    private bool _suppressSearchSelectionDispatch;
    private object? _commitsSource;
    private string? _selectedDiffHash;
    private object? _selectedDiffFilesSource;
    private DiffFileKey? _selectedDiffFileKey;
    private object? _selectedDiffFileSource;
    private object? _searchResultsSource;
    private string? _selectedSearchResultHash;

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
    [ObservableProperty] private string _searchQuery = "";
    [ObservableProperty] private SearchScopeProjection? _selectedSearchScope;
    [ObservableProperty] private bool _hasSearchResults;
    [ObservableProperty] private bool _isSearchPanelExpanded;

    public ObservableCollection<CommitProjection> Commits { get; } = new();
    public ObservableCollection<SearchResultProjection> SearchResults { get; } = new();
    public ObservableCollection<DiffFileProjection> SelectedDiffFiles { get; } = new();
    public ObservableCollection<IDiffRowProjection> SelectedDiffRows { get; } = new();

    [ObservableProperty] private CommitProjection? _selectedCommit;
    [ObservableProperty] private SearchResultProjection? _selectedSearchResult;
    [ObservableProperty] private DiffFileProjection? _selectedDiffFile;
    [ObservableProperty] private IDiffRowProjection? _selectedDiffRow;

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

        IsSearchPanelExpanded = !string.IsNullOrWhiteSpace(model.SearchQuery) || model.SearchResults != null;

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
            return;
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

            if (selectionChanged || diffContentChanged)
            {
                SyncSelectedDiffSelection(selectedDiffFile);
                _selectedDiffFileKey = selectedDiffFileKey;
            }
        }
    }

    private void UpdateCommits(GitKay.Core.App.Model model)
    {
        if (!ReferenceEquals(_commitsSource, model.Commits))
        {
            Commits.SyncWith(
                model.Commits,
                m => m.Commit.Hash,
                vm => vm.FullHash,
                _ => new CommitProjection(),
                _dispatch!);

            _commitsSource = model.Commits;
        }
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
    }

    partial void OnSelectedSearchScopeChanged(SearchScopeProjection? value)
    {
        if (_suppressSearchDispatch || value == null)
        {
            return;
        }

        _dispatch?.Invoke(GitKay.Core.App.Msg.NewSetSearchScope(value.Key));
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

    public void RereadRefs() => _dispatch?.Invoke(GitKay.Core.App.Msg.RereadRefs);

    [RelayCommand]
    private void Search()
    {
        var query = SearchQuery;
        var scopeKey = SelectedSearchScope?.Key ?? "all";
        var startedAtTicks = Stopwatch.GetTimestamp();
        LogTiming($"search click query={query} scope={scopeKey}");
        _dispatch?.Invoke(GitKay.Core.App.Msg.NewRunSearch(query, scopeKey, startedAtTicks));
    }

    [RelayCommand]
    private void ClearSearch()
    {
        _dispatch?.Invoke(GitKay.Core.App.Msg.NewSetSearchQuery(""));
        _dispatch?.Invoke(GitKay.Core.App.Msg.NewRunSearch("", SelectedSearchScope?.Key ?? "all", Stopwatch.GetTimestamp()));
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
}
