using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Elmish.Avalonia.Glue;
using Microsoft.FSharp.Collections;
using CoreWindow = GitKay.Core.CommitWindow;

namespace GitKay.UI;

/// <summary>A file in one of the commit window's lists.</summary>
public sealed partial class CommitFileRow(CoreWindow.ListKind list, GitKay.Core.Models.FileDiff diff, bool untracked) : ObservableObject {
    public CoreWindow.ListKind List { get; } = list;
    public GitKay.Core.Models.FileDiff Diff { get; } = diff;
    public bool IsUntracked { get; } = untracked;
    public string Path { get; } = CoreWindow.pathOf(diff);
    public string Label { get; } = GitKay.Core.FileChange.displayPath(diff.OldPath, diff.NewPath);
    public string Marker => IsUntracked ? "+" : List.IsStagedList ? "●" : "○";
    public bool IsStaged => List.IsStagedList;
    public string ListName => IsUntracked ? "untracked" : List.IsStagedList ? "staged" : "unstaged";

    /// <summary>What the list shows for this file: its whole path in patch mode, its name alone in a tree.</summary>
    [ObservableProperty] private string _listLabel = "";
    [ObservableProperty] private Avalonia.Thickness _listIndent;
}

/// <summary>A key or key sequence and what it does, for the commit window's keys sheet.</summary>
public sealed record CommitKey(string Keys, string Description);
public sealed record CommitKeyGroup(string Title, IReadOnlyList<CommitKey> Keys);

/// <summary>Projects the commit window's Elmish model onto its lists, diff and message box.</summary>
public sealed partial class CommitWindowProjection : ObservableObject {
    private Action<CoreWindow.Msg>? _dispatch;
    private CoreWindow.Model? _model;
    private bool _syncing;
    private GitKay.Core.Models.FileDiff? _shownDiff;
    private DiffFileProjection? _shownFile;
    private GitKay.Core.App.FileExpansion? _expansion;
    private long _expansionRequest;
    private readonly Dictionary<DiffLineProjection, (int Hunk, int Line)> _linePositions = new(ReferenceEqualityComparer.Instance);
    /// <summary>Where each changed line sits in the file's own diff, in order, so staging still works once context is shown.</summary>
    private readonly List<(int Hunk, int Line)> _changePositions = new();

    /// <summary>The repository, for reading a file's full context.</summary>
    public string? RepositoryPath { get; set; }

    public CommitWindowProjection(string repositoryName) => WindowTitle = $"Commit — {repositoryName}";

    public string WindowTitle { get; }
    public AvaloniaList<CommitFileRow> UnstagedFiles { get; } = new();
    public AvaloniaList<CommitFileRow> StagedFiles { get; } = new();

    /// <summary>What the two lists actually show: the files, plus folder rows in tree and all-files modes.</summary>
    public AvaloniaList<object> UnstagedRows { get; } = new();
    public AvaloniaList<object> StagedRows { get; } = new();
    public AvaloniaList<IDiffRowProjection> Rows { get; } = new();

    [ObservableProperty] private CommitFileRow? _selectedUnstaged;
    [ObservableProperty] private CommitFileRow? _selectedStaged;

    /// <summary>What the list control has selected: a file, or a folder row the user is only pointing at.</summary>
    [ObservableProperty] private object? _selectedUnstagedRow;
    [ObservableProperty] private object? _selectedStagedRow;

    partial void OnSelectedUnstagedRowChanged(object? value) {
        if (value is CommitFileRow file) SelectedUnstaged = file;
    }

    partial void OnSelectedStagedRowChanged(object? value) {
        if (value is CommitFileRow file) SelectedStaged = file;
    }


    [ObservableProperty] private IDiffRowProjection? _selectedRow;
    [ObservableProperty] private string _unstagedTitle = "Unstaged";
    [ObservableProperty] private string _stagedTitle = "Staged";
    [ObservableProperty] private string _diffTitle = "";
    [ObservableProperty] private bool _hasSelectedFile;
    [ObservableProperty] private bool _isStagedFileSelected;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _amend;
    [ObservableProperty] private bool _signOff;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string? _failureOutput;
    [ObservableProperty] private bool _canCommit;
    [ObservableProperty] private string _commitTip = "";
    [ObservableProperty] private string _emptyDiffText = "Scanning…";
    /// <summary>A discard that can still be put back.</summary>
    [ObservableProperty] private bool _canUndoDiscard;
    [ObservableProperty] private string _branch = "";
    [ObservableProperty] private bool _isKeysOpen;
    /// <summary>Ctrl held for a moment: each pane shows its Ctrl keys.</summary>
    [ObservableProperty] private bool _isCtrlHintsVisible;
    [ObservableProperty] private bool _isSearchOpen;
    [ObservableProperty] private bool _isFilePaletteOpen;
    [ObservableProperty] private string _paletteQuery = "";
    [ObservableProperty] private int _paletteIndex;
    public AvaloniaList<CommitFileRow> PaletteFiles { get; } = new();

    /// <summary>Ctrl+P: every changed file from both lists, best fuzzy matches first; a file in both lists appears twice.</summary>
    public void OpenFilePalette() {
        PaletteQuery = "";
        FilterPalette();
        IsFilePaletteOpen = true;
    }

    partial void OnPaletteQueryChanged(string value) => FilterPalette();

    private void FilterPalette() {
        var query = PaletteQuery.Trim();
        var ranked = UnstagedFiles.Concat(StagedFiles)
            .Select((row, order) => (row, order, score: query.Length == 0 ? 0 : GitKay.Kit.Fuzzy.score(query, row.Path)))
            .Where(entry => entry.score >= 0)
            .OrderByDescending(entry => entry.score)
            .ThenBy(entry => entry.order)
            .Select(entry => entry.row)
            .ToList();
        PaletteFiles.Clear();
        PaletteFiles.AddRange(ranked);
        PaletteIndex = ranked.Count > 0 ? 0 : -1;
    }

    /// <summary>Shows the chosen palette file's diff.</summary>
    public void RunPalette() {
        if (PaletteIndex >= 0 && PaletteIndex < PaletteFiles.Count) {
            var row = PaletteFiles[PaletteIndex];
            _dispatch?.Invoke(CoreWindow.Msg.NewSelect(row.List, row.Path));
        }
        IsFilePaletteOpen = false;
    }

    // ----- Searching the diff: / ? n N * #, as in the main window. -----

    private GitKay.Kit.TextQuery? _find;
    private string _findText = "";
    private bool _findForward = true;

    /// <summary>Selects the nearest row matching <paramref name="regex"/> from <paramref name="from"/>; returns the match summary.</summary>
    public string SearchDiff(string regex, bool forward, IDiffRowProjection? from, bool includeStart) {
        _find = GitKay.Kit.TextQuery.Create(true, regex);
        _findText = regex;
        _findForward = forward;
        return StepFind(from, forward ? GitKay.Kit.Direction.Forward : GitKay.Kit.Direction.Backward, includeStart);
    }

    /// <summary>n / N: the next match in the search's direction, or against it.</summary>
    public string? FindNext(bool sameDirection) {
        if (_find == null) return null;
        var direction = sameDirection == _findForward ? GitKay.Kit.Direction.Forward : GitKay.Kit.Direction.Backward;
        return StepFind(SelectedRow, direction, includeStart: false);
    }

    public void ClearFind() {
        _find = null;
        if (Surface != null) Surface.FindQuery = null;
    }

    private string StepFind(IDiffRowProjection? from, GitKay.Kit.Direction direction, bool includeStart) {
        if (_find is not { } query) return "";
        if (Surface != null) {
            Surface.FindUseRegex = true;
            Surface.FindQuery = _findText;
        }
        var matches = GitKay.Kit.Cycle.positionsWhere(Rows.Count, index => Rows[index] switch {
            DiffLineProjection line => query.IsMatch(line.Content),
            DiffHunkHeaderProjection header => query.IsMatch(header.Header),
            _ => false,
        });
        var focus = from == null ? -1 : Rows.IndexOf(from);
        var next = GitKay.Kit.Cycle.step(matches, focus, direction, includeStart);
        if (next >= 0) SelectedRow = Rows[matches[next]];
        return GitKay.Kit.Cycle.summary(next, matches.Length);
    }
    [ObservableProperty] private double _diffFontSize = DiffSurfaceControl.DefaultCodeFontSize;

    public string MessageTitle => Amend ? "Amended Commit Message:" : "Commit Message:";

    /// <summary>The keys that work here: the main window's movement and pane keys, and git gui's staging keys.</summary>
    public IReadOnlyList<CommitKeyGroup> KeyGroups { get; } = [
        new("Staging and committing", [
            new("Enter · double-click", "In a file list: move the file to the other list"),
            new("s / u · Ctrl+S / Ctrl+U", "Stage / unstage: the selected lines or the hunk at the cursor in the diff, the file in a list (Ctrl+U is half a page up in the diff)"),
            new("Ctrl+I", "Stage all unstaged and untracked files"),
            new("Delete", "Discard the selected lines, or the file's unstaged changes (asks first)"),
            new("Ctrl+Z", "Put back what the last discard threw away"),
            new("Ctrl+Enter", "Commit (or amend)"),
            new("Ctrl+S in the message · Ctrl+Shift+S", "Toggle sign off"),
            new("Ctrl+Shift+A", "Toggle amend"),
            new("F5", "Rescan the working tree and index"),
            new("Right-click", "File and line actions: stage, unstage, discard, copy path, open in VS Code"),
        ]),
        new("Finding", [
            new("Ctrl+P", "Go to a changed file, staged or unstaged; a file in both lists appears in both"),
            new("/ · ? · Ctrl+F", "Search the diff forwards · backwards (regex, smartcase; /i ignores case, \\V literal, Alt+R / Alt+C toggle, ↑↓ history); Enter keeps it, Esc goes back"),
            new("n / N · * / #", "Next / previous match · the word under the caret forwards / backwards"),
        ]),
        new("Panes", [
            new("Ctrl+1 / 2 / 3 / 4", "Unstaged files / staged files / diff / commit message"),
            new("Ctrl+h / j / k / l", "Pane left / down / up / right"),
            new("Ctrl+W  h j k l · w W · p", "Pane in a direction · next / previous pane · the pane before"),
            new("Tab / Shift+Tab", "Next / previous pane (in the message box, Esc first)"),
            new("Esc", "Close this sheet or a prompt · leave the message box for the diff"),
            new("F1", "Show or hide these keys"),
        ]),
        new("Moving", [
            new("j / k   ↓ / ↑", "Next / previous file or diff row"),
            new("gg / G   Home / End", "First / last"),
            new("{count}G", "Row or file N"),
            new("Ctrl+D / Ctrl+U · PageDown / PageUp", "Half page / page down and up in the diff"),
            new("]c / [c", "Next / previous hunk"),
            new("H / M / L · zz / zt / zb · Ctrl+E / Ctrl+Y", "Screen rows · scroll the cursor to centre / top / bottom · scroll a row"),
        ]),
        new("Diff text", [
            new("h / l · w / b / e · 0 / $ · f / t", "Move the caret, as in the main window's diff"),
            new("v / V then s, u, y or Delete", "Select characters or lines, then stage, unstage, copy or discard them"),
            new("yy / Y · y{motion} · yi / ya", "Copy the line, to a motion, or inside / around a text object"),
            new("Ctrl+= / Ctrl+- / Ctrl+0", "Zoom the diff text"),
        ]),
        new("In the main window only", [
            new("Ctrl+G · Ctrl+Shift+P · p / c · Alt+← / →", "Refs and commands palettes, and history navigation"),
        ]),
    ];

    /// <summary>The diff surface, for which lines are selected.</summary>
    public DiffSurfaceControl? Surface { get; set; }

    /// <summary>Raised after each successful commit; the flag says whether a push was asked for.</summary>
    public event Action<bool>? Committed;
    /// <summary>Asks the window to confirm a discard; returns whether to go ahead.</summary>
    public Func<string, Task<bool>>? ConfirmDiscard { get; set; }

    private bool _pushAfterCommit;
    private int _commitCount;

    public bool HasFailureOutput => !string.IsNullOrEmpty(FailureOutput);
    partial void OnFailureOutputChanged(string? value) => OnPropertyChanged(nameof(HasFailureOutput));

    // Ctrl keys aren't labelled on buttons: holding Ctrl shows them.
    public string CommitLabel => Amend ? "Amend" : "Commit";
    public string FileActionLabel => IsStagedFileSelected ? "Unstage file" : "Stage file";
    public string SelectionActionLabel => IsStagedFileSelected
        ? Surface?.HasTextSelection == true ? "Unstage lines (u)" : "Unstage hunk (u)"
        : Surface?.HasTextSelection == true ? "Stage lines (s)" : "Stage hunk (s)";
    public string SelectionActionTip => "s / u: stages or unstages the selected lines, or the hunk at the cursor";
    public bool CanDiscard => HasSelectedFile && !IsStagedFileSelected;
    public bool CanApplyToLines => HasSelectedFile;
    public string DiscardLabel => Surface?.HasTextSelection == true ? "Discard lines…" : "Discard file…";

    public string SubjectLengthText => $"{GitKay.Core.CommitWindow.subject(Message).Length}/72";
    public bool IsSubjectLong => GitKay.Core.CommitWindow.subject(Message).Length > 72;

    public void SetDispatch(Action<CoreWindow.Msg> dispatch) => _dispatch = dispatch;

    /// <summary>Refreshes the labels that depend on the diff surface's selection.</summary>
    public void RefreshSelectionLabels() {
        OnPropertyChanged(nameof(SelectionActionLabel));
        OnPropertyChanged(nameof(DiscardLabel));
    }

    public void Update(CoreWindow.Model model) {
        _syncing = true;
        try {
            var previous = _model;
            _model = model;

            if (previous == null || !ReferenceEquals(previous.Changes, model.Changes)) SyncLists(model);
            SyncSelection(model);

            if (Message != model.Message) Message = model.Message;
            if (Amend != model.Amend) Amend = model.Amend;
            if (SignOff != model.SignOff) SignOff = model.SignOff;
            Status = model.Status;
            CanUndoDiscard = model.LastDiscard != null;
            Branch = string.IsNullOrEmpty(model.Branch) ? "…" : model.Branch;
            FailureOutput = model.FailureOutput?.Value;
            var blocker = CoreWindow.commitBlocker(model);
            CanCommit = blocker == null;
            CommitTip = blocker?.Value ?? (model.Amend ? "Replace the last commit with the staged changes and this message" : "Commit the staged changes");
            EmptyDiffText = model.Changes == null ? "Scanning…" : "No changes";
            OnPropertyChanged(nameof(CommitLabel));

            if (model.Commits > _commitCount) {
                _commitCount = model.Commits;
                var push = _pushAfterCommit;
                _pushAfterCommit = false;
                Committed?.Invoke(push);
            }
        }
        finally {
            _syncing = false;
        }
    }

    // ----- File list: patch, tree, or every file in the repository, as in the history window. -----

    private CommitFileListMode _fileListMode = CommitFileListMode.Patch;
    private readonly HashSet<string> _collapsedUnstaged = new(StringComparer.Ordinal);
    private readonly HashSet<string> _collapsedStaged = new(StringComparer.Ordinal);
    private IReadOnlyList<string> _allPaths = [];
    private bool _allPathsLoading;

    public bool IsPatchFileListMode => _fileListMode == CommitFileListMode.Patch;
    public bool IsTreeFileListMode => _fileListMode == CommitFileListMode.Tree;
    public bool IsAllFilesMode => _fileListMode == CommitFileListMode.All;

    [RelayCommand]
    private void SetFileListMode(string mode) {
        _fileListMode = mode switch {
            "tree" => CommitFileListMode.Tree,
            "all" => CommitFileListMode.All,
            _ => CommitFileListMode.Patch,
        };
        OnPropertyChanged(nameof(IsPatchFileListMode));
        OnPropertyChanged(nameof(IsTreeFileListMode));
        OnPropertyChanged(nameof(IsAllFilesMode));
        if (IsAllFilesMode) LoadAllPaths();
        RebuildRows();
    }

    /// <summary>Clicking a folder opens or closes it, in whichever list it belongs to.</summary>
    [RelayCommand]
    private void ToggleFolder(CommitFolderRow folder) {
        var collapsed = UnstagedRows.Contains(folder) ? _collapsedUnstaged : _collapsedStaged;
        if (!collapsed.Remove(folder.Path)) collapsed.Add(folder.Path);
        RebuildRows();
    }

    /// <summary>Every file of HEAD, read once, for the all-files tree.</summary>
    private void LoadAllPaths() {
        if (RepositoryPath is not { } repo || _allPathsLoading || _allPaths.Count > 0) return;
        _allPathsLoading = true;
        _ = Task.Run(() => GitKay.Core.GitService.listCommitFiles(repo, "HEAD")).ContinueWith(task => Avalonia.Threading.Dispatcher.UIThread.Post(() => {
            _allPathsLoading = false;
            if (task.IsFaulted || task.Result.IsError) return;
            _allPaths = task.Result.ResultValue.ToArray();
            if (IsAllFilesMode) RebuildRows();
        }));
    }

    private void RebuildRows() {
        UnstagedRows.Clear();
        UnstagedRows.AddRange(CommitFileList.Build([.. UnstagedFiles], _fileListMode, _collapsedUnstaged, _allPaths));
        StagedRows.Clear();
        StagedRows.AddRange(CommitFileList.Build([.. StagedFiles], _fileListMode, _collapsedStaged, _allPaths));
    }

    private void SyncLists(CoreWindow.Model model) {
        UnstagedFiles.Clear();
        StagedFiles.Clear();
        if (model.Changes?.Value is not { } changes) return;
        foreach (var diff in CoreWindow.filesIn(CoreWindow.ListKind.UnstagedList, changes))
            UnstagedFiles.Add(new CommitFileRow(CoreWindow.ListKind.UnstagedList, diff, CoreWindow.isUntracked(changes, CoreWindow.pathOf(diff))));
        foreach (var diff in CoreWindow.filesIn(CoreWindow.ListKind.StagedList, changes))
            StagedFiles.Add(new CommitFileRow(CoreWindow.ListKind.StagedList, diff, false));
        UnstagedTitle = $"Unstaged Changes · {UnstagedFiles.Count}";
        StagedTitle = $"Staged Changes (Will Commit) · {StagedFiles.Count}";
        if (IsAllFilesMode) LoadAllPaths();
        RebuildRows();
    }

    private void SyncSelection(CoreWindow.Model model) {
        CommitFileRow? selected = null;
        if (model.Selected?.Value is var (list, path)) {
            var rows = list.IsStagedList ? StagedFiles : UnstagedFiles;
            selected = rows.FirstOrDefault(row => row.Path == path);
        }

        SelectedUnstaged = selected is { List.IsUnstagedList: true } ? selected : null;
        SelectedStaged = selected is { List.IsStagedList: true } ? selected : null;
        HasSelectedFile = selected != null;
        IsStagedFileSelected = selected?.List.IsStagedList == true;
        OnPropertyChanged(nameof(FileActionLabel));
        OnPropertyChanged(nameof(CanDiscard));
        OnPropertyChanged(nameof(CanApplyToLines));
        RefreshSelectionLabels();

        if (!ReferenceEquals(_shownDiff, selected?.Diff)) ShowDiff(selected);
    }

    private void ShowDiff(CommitFileRow? row) {
        _shownDiff = row?.Diff;
        _linePositions.Clear();
        Rows.Clear();
        if (row == null) {
            DiffTitle = "";
            return;
        }

        DiffTitle = $"{row.Label} · {(row.IsUntracked ? "untracked" : row.List.IsStagedList ? "staged" : "unstaged")}";
        _expansion = null;
        _changePositions.Clear();
        // Patches are built against the file's own diff, so remember where its changed lines are, in order.
        for (var hunk = 0; hunk < row.Diff.Hunks.Length; hunk++) {
            var lines = row.Diff.Hunks[hunk].Lines;
            for (var line = 0; line < lines.Length; line++)
                if (!lines[line].Type.IsContext) _changePositions.Add((hunk, line));
        }

        _shownFile = new DiffFileProjection(new GitKay.Core.GitService.DiffFileSummary(row.Diff.OldPath, row.Diff.NewPath, row.Label));
        _shownFile.ApplyContent(row.Diff);
        RenderRows();
    }

    /// <summary>Rebuilds the diff rows and re-maps changed lines onto the file's own diff.</summary>
    private void RenderRows() {
        Rows.Clear();
        _linePositions.Clear();
        if (_shownFile is not { } file) return;

        var rows = new List<IDiffRowProjection>();
        DiffRowBuilder.AppendFile(rows, file, GitKay.Core.DiffLayout.Unified);
        // The title bar already names the file.
        rows = rows.Where(item => item is not DiffFileHeaderProjection).ToList();

        var changed = rows.OfType<DiffLineProjection>().Where(line => line.IsAdded || line.IsRemoved).ToList();
        // Expanding context adds context lines only, so the changed lines stay in the same order as the diff's.
        for (var i = 0; i < changed.Count && i < _changePositions.Count; i++) _linePositions[changed[i]] = _changePositions[i];
        Rows.AddRange(rows);
    }

    /// <summary>Shows more of the file around a gap, or all of it.</summary>
    public void RevealContext(GitKay.Core.DiffExpansion.LineRange range) {
        if (_shownFile is not { } file || SelectedFile is not { } row || RepositoryPath is not { } repo) return;
        if (GitKay.Core.WorkingTree.tryParseSection(row.List.IsStagedList ? "Staged" : row.IsUntracked ? "Untracked" : "Unstaged") is not { } section) return;

        var current = _expansion ?? new GitKay.Core.App.FileExpansion(null, Microsoft.FSharp.Collections.FSharpList<GitKay.Core.DiffExpansion.LineRange>.Empty, null);
        var revealed = GitKay.Core.DiffExpansion.addRange(range, current.Revealed);
        if (current.FullContext != null) {
            Apply(new GitKay.Core.App.FileExpansion(current.FullContext, revealed, null));
            return;
        }

        var request = ++_expansionRequest;
        Apply(new GitKay.Core.App.FileExpansion(null, revealed, Microsoft.FSharp.Core.FSharpOption<long>.Some(request)));
        _ = LoadContextAsync(repo, section.Value, row, request);

        void Apply(GitKay.Core.App.FileExpansion expansion) {
            _expansion = expansion;
            file.ApplyExpansion(expansion);
            RenderRows();
        }
    }

    private async Task LoadContextAsync(string repo, GitKay.Core.WorkingTree.Section section, CommitFileRow row, long request) {
        GitKay.Core.Models.FileDiff? loaded = null;
        string? error = null;
        try {
            var result = await Task.Run(() => GitKay.Core.GitService.loadWorkingTreeFile(repo, section, row.Diff.OldPath, row.Diff.NewPath));
            if (result.IsOk) loaded = result.ResultValue;
            else error = GitKay.Core.GitErrorModule.describe(result.ErrorValue);
        }
        catch (Exception exception) {
            error = exception.Message;
        }

        if (request != _expansionRequest || _shownFile is not { } file || _expansion is not { } current) return;
        if (loaded == null) {
            Status = $"Could not read more of {row.Label}: {error}";
            _expansion = null;
            file.ApplyExpansion(null);
        }
        else {
            _expansion = new GitKay.Core.App.FileExpansion(loaded, current.Revealed, null);
            file.ApplyExpansion(_expansion);
        }
        RenderRows();
    }

    partial void OnSelectedUnstagedChanged(CommitFileRow? value) {
        if (value != null && !ReferenceEquals(SelectedUnstagedRow, value)) SelectedUnstagedRow = value;
        if (!_syncing && value != null) _dispatch?.Invoke(CoreWindow.Msg.NewSelect(value.List, value.Path));
    }

    partial void OnSelectedStagedChanged(CommitFileRow? value) {
        if (value != null && !ReferenceEquals(SelectedStagedRow, value)) SelectedStagedRow = value;
        if (!_syncing && value != null) _dispatch?.Invoke(CoreWindow.Msg.NewSelect(value.List, value.Path));
    }

    partial void OnMessageChanged(string value) {
        OnPropertyChanged(nameof(SubjectLengthText));
        OnPropertyChanged(nameof(IsSubjectLong));
        if (!_syncing) _dispatch?.Invoke(CoreWindow.Msg.NewSetMessage(value));
    }

    partial void OnAmendChanged(bool value) {
        OnPropertyChanged(nameof(CommitLabel));
        OnPropertyChanged(nameof(MessageTitle));
        if (!_syncing) _dispatch?.Invoke(CoreWindow.Msg.NewSetAmend(value));
    }

    partial void OnSignOffChanged(bool value) {
        if (!_syncing) _dispatch?.Invoke(CoreWindow.Msg.NewSetSignOff(value));
    }

    public CommitFileRow? SelectedFile => SelectedStaged ?? SelectedUnstaged;

    public void SetAmendFromKeyboard() => Amend = !Amend;
    public void SelectRow(IDiffRowProjection? row) => SelectedRow = row;
    public void ToggleSignOff() => SignOff = !SignOff;

    /// <summary>Stages (or unstages) whatever the key means where the focus is: lines or a hunk in the diff, the file in a list.</summary>
    public void Stage(bool inDiff, bool stage) {
        if (SelectedFile is not { } file || file.List.IsStagedList == stage) {
            Status = stage ? "Select an unstaged file to stage" : "Select a staged file to unstage";
            return;
        }
        if (inDiff) ApplyToSelection();
        else ToggleFile(file);
    }

    public void DiscardFromKeyboard() => DiscardCommand.Execute(null);

    [RelayCommand]
    private void Rescan() => _dispatch?.Invoke(CoreWindow.Msg.Rescan);

    /// <summary>A gap's up, down or show-all control.</summary>
    [RelayCommand]
    private void ExpandDiffGap(DiffGapExpansionRequest request) {
        if (GitKay.Core.DiffExpansion.revealRange(request.Direction, request.Gap) is { } range) RevealContext(range.Value);
    }

    [RelayCommand]
    public void StageAll() => _dispatch?.Invoke(CoreWindow.Msg.NewStagePaths(ListModule.OfSeq(UnstagedFiles.Select(row => row.Path))));

    [RelayCommand]
    private void UnstageAll() => _dispatch?.Invoke(CoreWindow.Msg.NewUnstagePaths(ListModule.OfSeq(StagedFiles.Select(row => row.Path))));

    /// <summary>Moves a file to the other list.</summary>
    [RelayCommand]
    public void ToggleFile() => ToggleFile(SelectedFile);

    public void ToggleFile(CommitFileRow? row) {
        if (row == null) return;
        var paths = ListModule.OfSeq(Paths(row));
        _dispatch?.Invoke(row.List.IsStagedList ? CoreWindow.Msg.NewUnstagePaths(paths) : CoreWindow.Msg.NewStagePaths(paths));
    }

    // A rename's old path moves with it, so the index doesn't keep half of it.
    private static IEnumerable<string> Paths(CommitFileRow row) =>
        GitKay.Core.FileChange.isRenamed(row.Diff.OldPath, row.Diff.NewPath)
            ? [row.Diff.OldPath, row.Diff.NewPath]
            : [row.Path];

    /// <summary>The changed lines the selection covers; with no selection, every change in the hunk at the cursor.</summary>
    private List<GitKay.Core.PatchBuilder.SelectedLine> ChosenLines() {
        var lines = new List<GitKay.Core.PatchBuilder.SelectedLine>();
        if (Surface == null) return lines;
        var rows = Surface.SelectedRows;
        IEnumerable<DiffLineProjection> chosen;
        if (Surface.HasTextSelection) {
            chosen = rows.OfType<DiffLineProjection>();
        }
        else {
            var focus = rows.OfType<DiffLineProjection>().FirstOrDefault()
                        ?? NextLineAfter(rows.FirstOrDefault());
            if (focus == null || !_linePositions.TryGetValue(focus, out var at)) return lines;
            chosen = _linePositions.Where(entry => entry.Value.Hunk == at.Hunk).Select(entry => entry.Key);
        }

        foreach (var line in chosen) {
            if (!(line.IsAdded || line.IsRemoved) || !_linePositions.TryGetValue(line, out var position)) continue;
            lines.Add(new GitKay.Core.PatchBuilder.SelectedLine(position.Hunk, position.Line,
                line.IsAdded ? GitKay.Core.Models.LineType.Added : GitKay.Core.Models.LineType.Removed, line.Content));
        }
        return lines.OrderBy(line => line.Hunk).ThenBy(line => line.Line).ToList();
    }

    // A hunk header or file header at the cursor stands for the hunk below it.
    private DiffLineProjection? NextLineAfter(IDiffRowProjection? row) {
        if (row == null) return null;
        var index = Rows.IndexOf(row);
        return index < 0 ? null : Rows.Skip(index + 1).OfType<DiffLineProjection>().FirstOrDefault();
    }

    /// <summary>s / u: stages or unstages the selected lines, or the hunk at the cursor.</summary>
    [RelayCommand]
    public void ApplyToSelection() {
        if (SelectedFile is not { } file) return;
        var lines = ChosenLines();
        if (lines.Count == 0) {
            Status = "Put the cursor in a hunk or select changed lines";
            return;
        }
        var target = file.List.IsStagedList ? GitKay.Core.GitService.PatchTarget.UnstageFromIndex : GitKay.Core.GitService.PatchTarget.StageInIndex;
        _dispatch?.Invoke(CoreWindow.Msg.NewApplyLines(target, file.Path, ListModule.OfSeq(lines)));
    }

    [RelayCommand]
    private async Task Discard() {
        if (SelectedFile is not { List.IsUnstagedList: true } file) return;
        var selectedLines = Surface?.HasTextSelection == true ? ChosenLines() : [];
        var what = selectedLines.Count > 0
            ? $"{selectedLines.Count} changed line{(selectedLines.Count == 1 ? "" : "s")} in {file.Label}"
            : file.IsUntracked ? $"the untracked file {file.Label} (it will be deleted)" : $"all unstaged changes to {file.Label}";
        if (ConfirmDiscard != null && !await ConfirmDiscard($"Discard {what}? This can't be undone.")) return;

        if (selectedLines.Count > 0 && !file.IsUntracked)
            _dispatch?.Invoke(CoreWindow.Msg.NewApplyLines(GitKay.Core.GitService.PatchTarget.DiscardFromWorkingTree, file.Path, ListModule.OfSeq(selectedLines)));
        else if (file.IsUntracked)
            _dispatch?.Invoke(CoreWindow.Msg.NewDiscardPaths(FSharpList<string>.Empty, ListModule.OfSeq([file.Path])));
        else
            _dispatch?.Invoke(CoreWindow.Msg.NewDiscardPaths(ListModule.OfSeq([file.Path]), FSharpList<string>.Empty));
    }

    /// <summary>Puts back what the last discard threw away.</summary>
    [RelayCommand]
    public void UndoDiscard() => _dispatch?.Invoke(CoreWindow.Msg.UndoDiscard);

    [RelayCommand]
    public void Commit() {
        _pushAfterCommit = false;
        _dispatch?.Invoke(CoreWindow.Msg.Commit);
    }

    [RelayCommand]
    private void CommitAndPush() {
        _dispatch?.Invoke(CoreWindow.Msg.Commit);
        _pushAfterCommit = true;
    }
}

public partial class CommitWindow : Window, IVimCommands {
    private enum Pane { None, Unstaged, Staged, Diff, Message }

    private readonly IDisposable? _host;
    private readonly GitKay.Core.Vim.VimSession _vim = new();
    private readonly PaneChrome _paneChrome = new();
    private readonly ListBoxVimHost? _unstagedVim;
    private readonly ListBoxVimHost? _stagedVim;
    private Pane _lastPane = Pane.None;
    private Pane _currentPane = Pane.None;

    public CommitWindow() {
        InitializeComponent();
        Icon = AppIcon.Window;
    }

    private readonly AppUiStateStore _uiState = new();
    private string _repositoryPath = "";

    public CommitWindow(string repositoryPath, string repositoryName) : this() {
        _repositoryPath = repositoryPath;
        // The diff here follows the same settings as the history window's: layout, context and the pane chrome.
        var appSettings = GitKay.Core.SettingsModule.normalize(new AppSettingsStore().Load());
        Surface.DiffLayout = appSettings.DiffLayout;
        Surface.SharedVim = _vim;
        Surface.VimCommands = this;
        _unstagedVim = new ListBoxVimHost(UnstagedList, this);
        _stagedVim = new ListBoxVimHost(StagedList, this);

        var projection = new CommitWindowProjection(repositoryName) { Surface = Surface, RepositoryPath = repositoryPath };
        projection.ConfirmDiscard = ConfirmAsync;
        DataContext = projection;
        Projection = projection;
        Surface.PointerReleased += (_, _) => projection.RefreshSelectionLabels();
        Surface.KeyUp += (_, _) => projection.RefreshSelectionLabels();
        Surface.LineMenuOpening += AddLineMenuItems;
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnWindowKeyUp, RoutingStrategies.Tunnel);
        Deactivated += (_, _) => HideCtrlHints();
        InitializeSearch();
        _paneChrome.Add(nameof(Pane.Unstaged), UnstagedPaneEffect, UnstagedTitleBar, UnstagedList);
        _paneChrome.Add(nameof(Pane.Staged), StagedPaneEffect, StagedTitleBar, StagedList);
        _paneChrome.Add(nameof(Pane.Diff), DiffPaneEffect, DiffPanePart);
        _paneChrome.Add(nameof(Pane.Message), MessagePaneEffect, FailureOutputPart, MessagePanePart);
        _paneChrome.AddSeparator(ListsSplitterLine);
        _paneChrome.AddSeparator(DiffSplitterLine);
        ApplyPaneSettings();
        AddHandler(GotFocusEvent, (_, _) => TrackPane(), RoutingStrategies.Bubble);

        // The message being written survives closing the window, until it is committed.
        var draft = GitKay.Core.UiStateModule.commitDraft(repositoryPath, _uiState.Load());
        var env = GitKay.Core.GitService.environment(repositoryPath);
        _host = ElmishHost.startAndBind(
            GitKay.Core.CommitWindow.program(env, draft == null ? "" : draft.Value, appSettings.DiffContextLines),
            model => projection.Update(model),
            dispatch => projection.SetDispatch(dispatch));
        // Rescan when the user comes back from elsewhere; not on every activation, which focus changes can repeat.
        var wasAway = false;
        Deactivated += (_, _) => wasAway = true;
        Activated += (_, _) => {
            if (!wasAway) return;
            wasAway = false;
            projection.RescanCommand.Execute(null);
            ApplyPaneSettings();
        };
        Closed += (_, _) => {
            SaveDraft(projection.Message);
            _host?.Dispose();
        };
        projection.Committed += _ => SaveDraft("");
        // Take keyboard focus as soon as the window is up, so its keys go here and not to the window behind.
        Opened += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => {
            Activate();
            MessageBox.Focus();
        }, Avalonia.Threading.DispatcherPriority.Input);
    }

    public CommitWindowProjection? Projection { get; }

    internal void SetPaneChrome(PaneChrome.Settings settings) => _paneChrome.Update(settings);

    /// <summary>Panes and the diff follow the same settings as the main window's; re-read when the window is activated.</summary>
    private void ApplyPaneSettings() {
        var settings = GitKay.Core.SettingsModule.normalize(new AppSettingsStore().Load());
        Surface.DiffLayout = settings.DiffLayout;
        _paneChrome.Update(new PaneChrome.Settings(settings.PaneGap, settings.PaneDimUnfocused, settings.PaneFocusHighlight,
            settings.PaneFocusEffect, settings.PaneEffectColor, settings.PaneEffectIntensity, settings.PaneBorder,
            settings.PaneBorderStyle, settings.PaneBorderColor, settings.PaneBorderThickness, settings.SplitterLinesHidden));
        TrackPane();
    }

    internal TextBox MessageBoxForTests => MessageBox;

    /// <summary>Turns on amend, so the window opens showing the last commit's files and message.</summary>
    public void StartAmending() {
        if (Projection is { Amend: false } projection) projection.Amend = true;
    }

    private void SaveDraft(string draft) {
        if (string.IsNullOrEmpty(_repositoryPath)) return;
        try {
            _uiState.Save(GitKay.Core.UiStateModule.withCommitDraft(_repositoryPath, draft, _uiState.Load()));
        }
        catch (Exception exception) {
            System.Diagnostics.Trace.WriteLine($"[commit-window] draft not saved: {exception.Message}");
        }
    }

    public void FocusMessage() => Avalonia.Threading.Dispatcher.UIThread.Post(() => MessageBox.Focus(), Avalonia.Threading.DispatcherPriority.Input);

    /// <summary>Opens a file in VS Code at a line; set by the main window.</summary>
    public Action<string, int?>? OpenInVsCode { get; set; }

    // ----- Panes: Ctrl+1..4, Ctrl+h/j/k/l, Ctrl+W, Tab. -----

    private Pane FocusedPane =>
        UnstagedList.IsKeyboardFocusWithin ? Pane.Unstaged
        : StagedList.IsKeyboardFocusWithin ? Pane.Staged
        : Surface.IsKeyboardFocusWithin ? Pane.Diff
        : MessageBox.IsKeyboardFocusWithin ? Pane.Message
        : Pane.None;

    private void TrackPane() {
        var pane = FocusedPane;
        _paneChrome.SetFocused(pane == Pane.None ? null : pane.ToString());
        if (pane == Pane.None || pane == _currentPane) return;
        _lastPane = _currentPane;
        _currentPane = pane;
    }

    private void FocusPane(Pane pane) {
        switch (pane) {
            case Pane.Unstaged: FocusList(UnstagedList); break;
            case Pane.Staged: FocusList(StagedList); break;
            case Pane.Diff: Surface.Focus(); break;
            case Pane.Message: MessageBox.Focus(); break;
        }
    }

    // A list takes focus on its selected file (or its first), so j / k continue from there.
    private static void FocusList(ListBox list) {
        if (list.SelectedIndex < 0 && list.ItemCount > 0) list.SelectedIndex = 0;
        if (list.SelectedIndex >= 0 && list.ContainerFromIndex(list.SelectedIndex) is { } container) container.Focus();
        else list.Focus();
    }

    private static Pane InDirection(Pane from, GitKay.Core.Vim.VimPaneCommand direction) => (from, direction) switch {
        (Pane.Unstaged, GitKay.Core.Vim.VimPaneCommand.Down) => Pane.Staged,
        (Pane.Unstaged or Pane.Staged, GitKay.Core.Vim.VimPaneCommand.Right) => Pane.Diff,
        (Pane.Staged, GitKay.Core.Vim.VimPaneCommand.Up) => Pane.Unstaged,
        (Pane.Diff, GitKay.Core.Vim.VimPaneCommand.Left) => Pane.Unstaged,
        (Pane.Diff, GitKay.Core.Vim.VimPaneCommand.Down) => Pane.Message,
        (Pane.Message, GitKay.Core.Vim.VimPaneCommand.Up) => Pane.Diff,
        (Pane.Message, GitKay.Core.Vim.VimPaneCommand.Left) => Pane.Staged,
        _ => Pane.None,
    };

    private static readonly Pane[] PaneOrder = [Pane.Unstaged, Pane.Staged, Pane.Diff, Pane.Message];

    private void CyclePane(int step) {
        var index = Array.IndexOf(PaneOrder, FocusedPane);
        FocusPane(PaneOrder[((index < 0 ? 0 : index + step) + PaneOrder.Length) % PaneOrder.Length]);
    }

    void IVimCommands.PaneCommand(GitKay.Core.Vim.VimPaneCommand command) {
        switch (command) {
            case GitKay.Core.Vim.VimPaneCommand.Next: CyclePane(1); break;
            case GitKay.Core.Vim.VimPaneCommand.Previous: CyclePane(-1); break;
            case GitKay.Core.Vim.VimPaneCommand.Last: if (_lastPane != Pane.None) FocusPane(_lastPane); break;
            default:
                if (InDirection(FocusedPane, command) is var target and not Pane.None) FocusPane(target);
                break;
        }
    }

    // The main window's commit-level keys have nothing to act on here.
    void IVimCommands.CopyCommitReference(bool subject) { }
    void IVimCommands.GoToParent(int index) { }
    void IVimCommands.GoToChild() { }

    // ----- Keys -----

    // ----- Hold Ctrl for hints, as in the main window. -----

    private int _ctrlHintGeneration;
    private int _ctrlReleaseGeneration;
    private bool _ctrlHintPending;

    private void HideCtrlHints() {
        _ctrlHintPending = false;
        _ctrlHintGeneration++;
        if (Projection != null) Projection.IsCtrlHintsVisible = false;
    }

    private async void ShowCtrlHintsAfterHold() {
        var generation = ++_ctrlHintGeneration;
        _ctrlHintPending = true;
        await Task.Delay(400);
        // Any other key or releasing Ctrl bumps the generation and cancels this.
        if (generation != _ctrlHintGeneration || Projection == null) return;
        _ctrlHintPending = false;
        Projection.IsCtrlHintsVisible = true;
    }

    private async void OnWindowKeyUp(object? sender, KeyEventArgs e) {
        if (e.Key is not (Key.LeftCtrl or Key.RightCtrl)) return;
        // X11 auto-repeat can deliver a held key as release+press pairs; only a release not followed by a press counts.
        var release = ++_ctrlReleaseGeneration;
        await Task.Delay(60);
        if (release == _ctrlReleaseGeneration) HideCtrlHints();
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e) {
        if (Projection is not { } projection) return;
        if (e.Key is Key.LeftCtrl or Key.RightCtrl) {
            _ctrlReleaseGeneration++;
            if (!projection.IsCtrlHintsVisible && !_ctrlHintPending) ShowCtrlHintsAfterHold();
            return;
        }
        HideCtrlHints();
        var pane = FocusedPane;
        var inMessage = pane == Pane.Message;
        var none = e.KeyModifiers == KeyModifiers.None;
        var ctrl = e.KeyModifiers == KeyModifiers.Control;
        var ctrlShift = e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift);

        bool Handled() {
            e.Handled = true;
            return true;
        }

        // The keys sheet swallows keys until it closes.
        if (projection.IsKeysOpen) {
            if (e.Key is Key.Escape or Key.F1) projection.IsKeysOpen = false;
            Handled();
            return;
        }
        if (projection.IsFilePaletteOpen) {
            OnPaletteKey(e);
            return;
        }
        // The search prompt handles its own keys.
        if (projection.IsSearchOpen) return;

        // A pending count, operator or prefix owns the next key.
        if (_vim.IsAwaitingKey && !inMessage && HostFor(pane) is { } pendingHost) {
            if (_vim.Handle(pendingHost, VimKeys.From(e))) Handled();
            return;
        }

        switch (e.Key) {
            case Key.F1:
                projection.IsKeysOpen = true;
                Handled();
                return;
            case Key.F5:
                projection.RescanCommand.Execute(null);
                Handled();
                return;
            case Key.Z when ctrl && !inMessage:
                projection.UndoDiscard();
                Handled();
                return;
            case Key.Enter when ctrl:
                projection.Commit();
                Handled();
                return;
            case Key.D1 or Key.D2 or Key.D3 or Key.D4 or Key.NumPad1 or Key.NumPad2 or Key.NumPad3 or Key.NumPad4 when ctrl:
                FocusPane(e.Key switch {
                    Key.D1 or Key.NumPad1 => Pane.Unstaged,
                    Key.D2 or Key.NumPad2 => Pane.Staged,
                    Key.D3 or Key.NumPad3 => Pane.Diff,
                    _ => Pane.Message,
                });
                Handled();
                return;
            case Key.H or Key.J or Key.K or Key.L when ctrl:
                ((IVimCommands)this).PaneCommand(e.Key switch {
                    Key.H => GitKay.Core.Vim.VimPaneCommand.Left,
                    Key.J => GitKay.Core.Vim.VimPaneCommand.Down,
                    Key.K => GitKay.Core.Vim.VimPaneCommand.Up,
                    _ => GitKay.Core.Vim.VimPaneCommand.Right,
                });
                Handled();
                return;
            case Key.A when ctrlShift:
                projection.SetAmendFromKeyboard();
                Handled();
                return;
            // In the message box Ctrl+S signs off, as in git gui; elsewhere it stages, like s.
            case Key.S when ctrlShift || (ctrl && inMessage):
                projection.ToggleSignOff();
                Handled();
                return;
            case Key.P when ctrl:
                projection.OpenFilePalette();
                Avalonia.Threading.Dispatcher.UIThread.Post(() => PaletteBox.Focus(), Avalonia.Threading.DispatcherPriority.Input);
                Handled();
                return;
            case Key.F when ctrl:
                ((IVimCommands)this).OpenSearch(GitKay.Core.Vim.VimPane.Diff, true);
                Handled();
                return;
            case Key.S when ctrl:
                projection.Stage(pane == Pane.Diff, stage: true);
                Handled();
                return;
            case Key.I when ctrl && !inMessage:
                projection.StageAll();
                Handled();
                return;
            case Key.T when ctrl && !inMessage:
                projection.Stage(pane == Pane.Diff, stage: true);
                Handled();
                return;
            case Key.U when ctrl && pane is Pane.Unstaged or Pane.Staged or Pane.Message:
                projection.Stage(false, stage: false);
                Handled();
                return;
        }

        if (MainWindow.DiffZoomDirection(e) is { } zoom) {
            projection.DiffFontSize = zoom == 0 ? DiffSurfaceControl.DefaultCodeFontSize : Math.Clamp(projection.DiffFontSize + zoom, MainProjection.MinDiffFontSize, MainProjection.MaxDiffFontSize);
            Handled();
            return;
        }

        if (inMessage) {
            if (e.Key == Key.Escape) {
                Surface.Focus();
                Handled();
            }
            return;
        }

        if (e.Key == Key.Tab && e.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift) {
            CyclePane(e.KeyModifiers == KeyModifiers.Shift ? -1 : 1);
            Handled();
            return;
        }

        if (none && pane is Pane.Unstaged or Pane.Staged or Pane.Diff) {
            switch (e.Key) {
                case Key.S or Key.U:
                    projection.Stage(pane == Pane.Diff, stage: e.Key == Key.S);
                    Handled();
                    return;
                case Key.Enter when pane is Pane.Unstaged or Pane.Staged:
                    projection.ToggleFile();
                    Handled();
                    return;
                case Key.Delete:
                    projection.DiscardFromKeyboard();
                    Handled();
                    return;
            }
        }

        if (HostFor(pane) is { } host && _vim.Handle(host, VimKeys.From(e))) Handled();
    }

    private GitKay.Core.Vim.IVimHost? HostFor(Pane pane) => pane switch {
        Pane.Unstaged => _unstagedVim,
        Pane.Staged => _stagedVim,
        Pane.Diff => Surface,
        _ => null,
    };

    private void OnKeysBackdropPressed(object? sender, PointerPressedEventArgs e) {
        if (Projection != null) Projection.IsKeysOpen = false;
    }

    // ----- Mouse -----

    /// <summary>A click anywhere on a folder row opens or closes it.</summary>
    private void OnFolderPointerPressed(object? sender, PointerPressedEventArgs e) {
        if ((sender as Control)?.DataContext is not CommitFolderRow folder || Projection is not { } projection) return;
        projection.ToggleFolderCommand.Execute(folder);
        e.Handled = true;
    }

    private void OnFileDoubleTapped(object? sender, TappedEventArgs e) {
        if ((e.Source as Control)?.DataContext is CommitFileRow row) Projection?.ToggleFile(row);
    }

    private void OnFileContextRequested(object? sender, ContextRequestedEventArgs e) {
        if (Projection is not { } projection || (e.Source as Control)?.DataContext is not CommitFileRow row) return;
        // The menu acts on the clicked file.
        if (row.List.IsStagedList) projection.SelectedStaged = row;
        else projection.SelectedUnstaged = row;

        var menu = new ContextMenu();
        void Add(string header, string? gesture, Action action, bool enabled = true) {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            if (gesture != null) item.InputGesture = KeyGesture.Parse(gesture);
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        if (row.List.IsUnstagedList) {
            Add("Stage file", "Ctrl+T", () => projection.ToggleFile(row));
            Add(row.IsUntracked ? "Delete untracked file…" : "Discard changes…", "Delete", projection.DiscardFromKeyboard);
            menu.Items.Add(new Separator());
            Add("Stage all", "Ctrl+I", projection.StageAll, projection.UnstagedFiles.Count > 0);
        }
        else {
            Add("Unstage file", "Ctrl+U", () => projection.ToggleFile(row));
            menu.Items.Add(new Separator());
            Add("Unstage all", null, () => projection.UnstageAllCommand.Execute(null), projection.StagedFiles.Count > 0);
        }
        menu.Items.Add(new Separator());
        Add("Copy relative path", null, () => Copy(row.Path));
        if (OpenInVsCode != null) Add("Open in VS Code", null, () => OpenInVsCode(row.Path, null));
        menu.Open(e.Source as Control ?? this);
        e.Handled = true;
    }

    private void AddLineMenuItems(ContextMenu menu) {
        if (Projection is not { SelectedFile: { } file } projection) return;
        var lines = Surface.HasTextSelection ? "lines" : "hunk";
        void Add(string header, string gesture, Action action) {
            var item = new MenuItem { Header = header, InputGesture = KeyGesture.Parse(gesture) };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        if (file.List.IsUnstagedList) {
            Add($"Stage {lines}", "S", () => projection.Stage(inDiff: true, stage: true));
            Add(file.IsUntracked ? "Delete untracked file…" : Surface.HasTextSelection ? "Discard lines…" : "Discard file…", "Delete", projection.DiscardFromKeyboard);
        }
        else {
            Add($"Unstage {lines}", "U", () => projection.Stage(inDiff: true, stage: false));
        }
        if (OpenInVsCode != null && Surface.SelectedItem is DiffLineProjection line) {
            var item = new MenuItem { Header = "Open in VS Code at this line" };
            item.Click += (_, _) => OpenInVsCode(file.Path, line.NewLineNo ?? line.OldLineNo);
            menu.Items.Add(item);
        }
    }

    private async void Copy(string text) {
        if (Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
        if (Projection != null) Projection.Status = $"Copied {text}";
    }

    private async Task<bool> ConfirmAsync(string question) {
        var confirmed = false;
        var dialog = new Window {
            Title = "Discard changes?",
            Icon = AppIcon.Window,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        dialog[!BackgroundProperty] = dialog.GetResourceObservable("GitKaySurfaceBrush").ToBinding();
        var text = new TextBlock { Text = question, TextWrapping = TextWrapping.Wrap, FontSize = 13 };
        text[!TextBlock.ForegroundProperty] = dialog.GetResourceObservable("GitKayTextBrush").ToBinding();
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(14, 4), IsCancel = true, IsDefault = true };
        cancel.Click += (_, _) => dialog.Close();
        var discard = new Button { Content = "Discard", Padding = new Thickness(14, 4) };
        discard[!ForegroundProperty] = dialog.GetResourceObservable("GitKayRemovedAccentBrush").ToBinding();
        discard.Click += (_, _) => { confirmed = true; dialog.Close(); };
        dialog.Content = new StackPanel {
            Margin = new Thickness(20, 16),
            Spacing = 14,
            Children = {
                text,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, discard } },
            },
        };
        await dialog.ShowDialog(this);
        return confirmed;
    }
}
