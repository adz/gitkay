using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace GitKay.UI;

/// <summary>A file the user can act on from a context menu: changed in the commit, or unchanged in its tree.</summary>
public sealed record FileTarget(string OldPath, string NewPath, string DisplayPath, DiffFileProjection? Changed) {
    public string Path => NewPath == "/dev/null" ? OldPath : NewPath;

    public static FileTarget From(DiffFileProjection file) => new(file.Key.OldPath, file.Key.NewPath, file.DisplayPath, file);
    public static FileTarget From(RepoFileRow row) => new(row.Path, row.Path, row.Path, null);
}

public partial class MainProjection {
    public const double MinDiffFontSize = 7;
    public const double MaxDiffFontSize = 32;

    /// <summary>The repository's git directory, as discovered at startup.</summary>
    public string? RepositoryPath { get; set; }

    /// <summary>The repository's working tree root; null for a bare repository.</summary>
    public string? WorkingDirectory => _workingDirectory ??= RepositoryPath == null ? null : GitKay.Core.GitService.workingDirectory(RepositoryPath);
    private string? _workingDirectory;

    public string? SelectedCommitHash => _selectedDiffHash;

    /// <summary>Font size of diff code only; zoomed with Ctrl+= / Ctrl+- / Ctrl+0 or Ctrl+wheel.</summary>
    [ObservableProperty] private double _diffFontSize = DiffSurfaceControl.DefaultCodeFontSize;

    partial void OnDiffFontSizeChanged(double value) {
        var clamped = Math.Clamp(Math.Round(value), MinDiffFontSize, MaxDiffFontSize);
        if (clamped != value) DiffFontSize = clamped;
    }

    /// <summary>+1 zooms in, -1 out, 0 resets.</summary>
    public void ZoomDiff(int direction) {
        DiffFontSize = direction == 0 ? DiffSurfaceControl.DefaultCodeFontSize : DiffFontSize + direction;
        Status = $"Diff text {DiffFontSize:0}px";
    }

    // ----- File list: patch, tree, or every file in the commit. -----

    [ObservableProperty] private bool _isAllFilesMode;
    public bool IsPatchFileListMode => !IsDiffFileTreeMode && !IsAllFilesMode;
    public bool IsTreeFileListMode => IsDiffFileTreeMode && !IsAllFilesMode;

    private readonly HashSet<string> _toggledAllFilesFolders = new(StringComparer.Ordinal);
    private RepoFileRow? _selectedRepoFileRow;
    private string? _allFilesHash;
    private IReadOnlyList<string>? _allFiles;
    private string? _allFilesLoadingHash;

    partial void OnIsDiffFileTreeModeChanged(bool value) => OnFileListModeChanged();
    partial void OnIsAllFilesModeChanged(bool value) => OnFileListModeChanged();

    private void OnFileListModeChanged() {
        OnPropertyChanged(nameof(IsPatchFileListMode));
        OnPropertyChanged(nameof(IsTreeFileListMode));
        RebuildDiffFileListRows();
    }

    [RelayCommand]
    private void SetDiffFileListMode(string mode) {
        IsAllFilesMode = mode == "all";
        if (mode != "all") IsDiffFileTreeMode = mode == "tree";
    }

    [RelayCommand]
    private void ToggleDiffFolder(DiffFileFolderRow folder) {
        var folders = IsAllFilesMode ? _toggledAllFilesFolders : _collapsedDiffFolders;
        if (!folders.Remove(folder.Path)) folders.Add(folder.Path);
        RebuildDiffFileListRows();
    }

    private void RebuildDiffFileListRows() {
        List<object> rows;
        if (IsAllFilesMode) {
            var hash = _selectedDiffHash;
            if (hash != null && _allFilesHash != hash) LoadAllFiles(hash);
            rows = DiffFileTree.BuildAllFilesRows(SelectedDiffFiles, _allFilesHash == hash ? _allFiles ?? [] : [], _toggledAllFilesFolders);
        }
        else {
            rows = DiffFileTree.BuildRows(SelectedDiffFiles, IsDiffFileTreeMode, _collapsedDiffFolders);
        }

        if (_selectedRepoFileRow != null)
            _selectedRepoFileRow = rows.OfType<RepoFileRow>().FirstOrDefault(row => row.Path == _selectedRepoFileRow.Path);
        DiffFileListRows.Clear();
        DiffFileListRows.AddRange(rows);
        OnPropertyChanged(nameof(SelectedDiffFileListRow));
        RefreshDiffTotals();
    }

    private void LoadAllFiles(string hash) {
        if (RepositoryPath is not { } repo || _allFilesLoadingHash == hash) return;
        _allFilesLoadingHash = hash;
        _ = Task.Run(() => GitKay.Core.GitService.listCommitFiles(repo, hash)).ContinueWith(task => Dispatcher.UIThread.Post(() => {
            if (_allFilesLoadingHash == hash) _allFilesLoadingHash = null;
            if (task.IsFaulted) {
                Status = $"Could not list files: {task.Exception?.GetBaseException().Message}";
                return;
            }

            var result = task.Result;
            if (result.IsError) {
                Status = GitKay.Core.GitErrorModule.describe(result.ErrorValue);
                return;
            }

            _allFilesHash = hash;
            _allFiles = result.ResultValue.ToArray();
            if (IsAllFilesMode && _selectedDiffHash == hash) RebuildDiffFileListRows();
        }));
    }

    /// <summary>Every file of the selected commit when "All files" has loaded it; otherwise only changed files.</summary>
    public IEnumerable<string> UnchangedFilePaths() {
        if (_allFilesHash != _selectedDiffHash || _allFiles == null) return [];
        var changed = SelectedDiffFiles.Select(DiffFileTree.PathOf).ToHashSet(StringComparer.Ordinal);
        return _allFiles.Where(path => !changed.Contains(path));
    }

    // ----- Change totals: the files title bar and each folder row show +added −removed. -----

    private readonly HashSet<DiffFileProjection> _totalsSubscriptions = new(ReferenceEqualityComparer.Instance);
    [ObservableProperty] private string _diffTotalAddedText = "";
    [ObservableProperty] private string _diffTotalRemovedText = "";
    [ObservableProperty] private bool _hasDiffTotals;

    private void RefreshDiffTotals() {
        foreach (var file in SelectedDiffFiles)
            if (_totalsSubscriptions.Add(file))
                file.PropertyChanged += (_, e) => {
                    if (e.PropertyName is nameof(DiffFileProjection.IsLoaded) or nameof(DiffFileProjection.AddedLines) or nameof(DiffFileProjection.RemovedLines))
                        Dispatcher.UIThread.Post(RefreshDiffTotalsNow, DispatcherPriority.Background);
                };
        RefreshDiffTotalsNow();
    }

    private void RefreshDiffTotalsNow() {
        var loaded = SelectedDiffFiles.Where(file => file.IsLoaded).ToList();
        var added = loaded.Sum(file => file.AddedLines);
        var removed = loaded.Sum(file => file.RemovedLines);
        HasDiffTotals = loaded.Count > 0;
        DiffTotalAddedText = $"+{added}";
        DiffTotalRemovedText = $"−{removed}";
        foreach (var folder in DiffFileListRows.OfType<DiffFileFolderRow>()) folder.RefreshTotals();
    }

    // ----- History scope: all branches or HEAD, and a file filter. -----

    private Microsoft.FSharp.Collections.FSharpList<GitKay.Core.GitStartup.StartupTarget> _historyTargets =
        Microsoft.FSharp.Collections.FSharpList<GitKay.Core.GitStartup.StartupTarget>.Empty;
    private bool _suppressAllBranchesDispatch;

    /// <summary>History from every branch, tag and remote (like <c>--all</c>) rather than what HEAD reaches.</summary>
    [ObservableProperty] private bool _isAllBranches;
    /// <summary>The paths history is limited to, or empty.</summary>
    [ObservableProperty] private string _historyPathFilter = "";
    public bool HasHistoryPathFilter => HistoryPathFilter.Length > 0;
    public string HistoryPathFilterTip => $"History limited to commits touching {HistoryPathFilter}\nClick or right-click for actions · ✕ clears the filter";
    /// <summary>The branches, tags or revisions history is limited to, or empty for HEAD / all branches.</summary>
    [ObservableProperty] private string _historyTipFilter = "";
    public bool HasHistoryTipFilter => HistoryTipFilter.Length > 0;
    public string HistoryTipFilterTip => $"History limited to commits reachable from {HistoryTipFilter}\nClick or right-click for actions · ✕ shows the full history";

    partial void OnHistoryTipFilterChanged(string value) {
        OnPropertyChanged(nameof(HasHistoryTipFilter));
        OnPropertyChanged(nameof(HistoryTipFilterTip));
    }

    /// <summary>Shows the commits a branch, tag or commit reaches, from its head back, keeping any file filter.</summary>
    public void ShowHistoryOf(string revision) {
        SetHistoryTargets(GitKay.Core.GitStartup.historyOf(revision, _historyTargets).ToList());
        Status = $"History of {revision}";
    }

    [RelayCommand]
    public void ClearHistoryTipFilter() => SetHistoryTargets(GitKay.Core.GitStartup.withoutTips(_historyTargets).ToList());

    partial void OnHistoryPathFilterChanged(string value) {
        OnPropertyChanged(nameof(HasHistoryPathFilter));
        OnPropertyChanged(nameof(HistoryPathFilterTip));
    }

    private void UpdateHistoryTargets(Microsoft.FSharp.Collections.FSharpList<GitKay.Core.GitStartup.StartupTarget> targets) {
        if (ReferenceEquals(_historyTargets, targets)) return;
        _historyTargets = targets;
        _suppressAllBranchesDispatch = true;
        try { IsAllBranches = targets.Any(target => target.IsAll); }
        finally { _suppressAllBranchesDispatch = false; }
        HistoryPathFilter = string.Join(", ", targets.OfType<GitKay.Core.GitStartup.StartupTarget.Path>().Select(path => path.Item));
        HistoryTipFilter = string.Join(", ", GitKay.Core.GitStartup.tipNames(targets));
    }

    partial void OnIsAllBranchesChanged(bool value) {
        if (_suppressAllBranchesDispatch) return;
        // All branches replaces a branch or tag scope; either way a file filter stays.
        var targets = GitKay.Core.GitStartup.withoutTips(_historyTargets).Where(target => !target.IsAll).ToList();
        if (value) targets.Insert(0, GitKay.Core.GitStartup.StartupTarget.All);
        SetHistoryTargets(targets);
    }

    /// <summary>Limits history to commits touching one file, keeping the branch scope.</summary>
    public void FilterHistoryToFile(FileTarget target) {
        var targets = _historyTargets.Where(existing => !existing.IsPath).ToList();
        targets.Add(GitKay.Core.GitStartup.StartupTarget.NewPath(target.Path));
        SetHistoryTargets(targets);
        Status = $"History limited to {target.Path}";
    }

    [RelayCommand]
    public void ClearHistoryPathFilter() => SetHistoryTargets(_historyTargets.Where(existing => !existing.IsPath).ToList());

    private void SetHistoryTargets(List<GitKay.Core.GitStartup.StartupTarget> targets) =>
        _dispatch?.Invoke(GitKay.Core.App.Msg.NewSetHistoryTargets(Microsoft.FSharp.Collections.ListModule.OfSeq(targets)));

    // ----- File actions: copy paths, whole file, VS Code. -----

    public event Action<FileTarget>? WholeFileRequested;

    public string FullPath(FileTarget target) =>
        System.IO.Path.GetFullPath(System.IO.Path.Combine(WorkingDirectory ?? "", target.Path.Replace('/', System.IO.Path.DirectorySeparatorChar)));

    public void ShowWholeFile(FileTarget target) {
        if (_selectedDiffHash == null || RepositoryPath == null) return;
        WholeFileRequested?.Invoke(target);
    }

    /// <summary>
    /// Opens the working-tree file in VS Code rooted at the repository. Passing the folder together with the file makes
    /// VS Code reuse a window that already has the folder open, or open one rooted there.
    /// </summary>
    public void OpenInVsCode(FileTarget? target, int? line = null) {
        if (WorkingDirectory is not { } root) {
            Status = "No working tree to open in VS Code";
            return;
        }

        var arguments = new List<string> { root };
        if (target != null) {
            var full = FullPath(target);
            if (!File.Exists(full)) Status = $"{target.Path} is not in the working tree; opening the repository";
            else {
                arguments.Add("--goto");
                arguments.Add(line is { } number ? $"{full}:{number}" : full);
            }
        }

        try {
            ExternalTools.StartVsCode(arguments, root);
        }
        catch (Exception exception) {
            Status = $"Could not start VS Code (is 'code' on PATH?): {exception.Message}";
        }
    }
}
