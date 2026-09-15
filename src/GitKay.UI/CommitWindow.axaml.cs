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
public sealed class CommitFileRow(CoreWindow.ListKind list, GitKay.Core.Models.FileDiff diff, bool untracked) {
    public CoreWindow.ListKind List { get; } = list;
    public GitKay.Core.Models.FileDiff Diff { get; } = diff;
    public bool IsUntracked { get; } = untracked;
    public string Path { get; } = CoreWindow.pathOf(diff);
    public string Label { get; } = GitKay.Core.FileChange.displayPath(diff.OldPath, diff.NewPath);
    public string Marker => IsUntracked ? "+" : List.IsStagedList ? "●" : "○";
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
    private readonly Dictionary<DiffLineProjection, (int Hunk, int Line)> _linePositions = new(ReferenceEqualityComparer.Instance);

    public CommitWindowProjection(string repositoryName) => WindowTitle = $"Commit — {repositoryName}";

    public string WindowTitle { get; }
    public AvaloniaList<CommitFileRow> UnstagedFiles { get; } = new();
    public AvaloniaList<CommitFileRow> StagedFiles { get; } = new();
    public AvaloniaList<IDiffRowProjection> Rows { get; } = new();

    [ObservableProperty] private CommitFileRow? _selectedUnstaged;
    [ObservableProperty] private CommitFileRow? _selectedStaged;
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
    [ObservableProperty] private string _branch = "";
    [ObservableProperty] private bool _isKeysOpen;
    [ObservableProperty] private double _diffFontSize = DiffSurfaceControl.DefaultCodeFontSize;

    public string MessageTitle => Amend ? "Amended Commit Message:" : "Commit Message:";

    /// <summary>The keys that work here: the main window's movement and pane keys, and git gui's staging keys.</summary>
    public IReadOnlyList<CommitKeyGroup> KeyGroups { get; } = [
        new("Staging and committing", [
            new("Enter · double-click", "In a file list: move the file to the other list"),
            new("s / u · Ctrl+T / Ctrl+U", "Stage / unstage: the selected lines or the hunk at the cursor in the diff, the file in a list"),
            new("Ctrl+I", "Stage all unstaged and untracked files"),
            new("Delete", "Discard the selected lines, or the file's unstaged changes (asks first)"),
            new("Ctrl+Enter", "Commit (or amend)"),
            new("Ctrl+Shift+A · Ctrl+S", "Toggle amend · toggle sign off"),
            new("F5", "Rescan the working tree and index"),
            new("Right-click", "File and line actions: stage, unstage, discard, copy path, open in VS Code"),
        ]),
        new("Panes", [
            new("Ctrl+1 / 2 / 3 / 4", "Unstaged files / staged files / diff / commit message"),
            new("Ctrl+h / j / k / l", "Pane left / down / up / right"),
            new("Ctrl+W  h j k l · w W · p", "Pane in a direction · next / previous pane · the pane before"),
            new("Tab / Shift+Tab", "Next / previous pane (in the message box, Esc first)"),
            new("Esc", "Close this sheet · leave the message box for the diff"),
        ]),
        new("Moving", [
            new("j / k   ↓ / ↑", "Next / previous file or diff row"),
            new("gg / G   Home / End", "First / last"),
            new("{count}G", "Row or file N"),
            new("Ctrl+D / Ctrl+U · PageDown / PageUp", "Half page / page down and up in the diff (Ctrl+U unstages in a file list)"),
            new("]c / [c", "Next / previous hunk"),
            new("H / M / L · zz / zt / zb · Ctrl+E / Ctrl+Y", "Screen rows · scroll the cursor to centre / top / bottom · scroll a row"),
        ]),
        new("Diff text", [
            new("h / l · w / b / e · 0 / $ · f / t", "Move the caret, as in the main window's diff"),
            new("v / V then s, u, y or Delete", "Select characters or lines, then stage, unstage, copy or discard them"),
            new("yy / Y · y{motion} · yi / ya", "Copy the line, to a motion, or inside / around a text object"),
            new("Ctrl+= / Ctrl+- / Ctrl+0", "Zoom the diff text"),
        ]),
        new("Not here", [
            new("/ ? n N * # · Ctrl+P · Ctrl+G · p / c", "Search, palettes and history navigation stay in the main window; ? opens this sheet"),
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

    public string CommitLabel => Amend ? "Amend  Ctrl+Enter" : "Commit  Ctrl+Enter";
    public string FileActionLabel => IsStagedFileSelected ? "Unstage file" : "Stage file";
    public string SelectionActionLabel => IsStagedFileSelected
        ? Surface?.HasTextSelection == true ? "Unstage lines" : "Unstage hunk"
        : Surface?.HasTextSelection == true ? "Stage lines" : "Stage hunk";
    public string SelectionActionTip => "s / u: stages or unstages the selected lines, or the hunk at the cursor";
    public bool CanDiscard => HasSelectedFile && !IsStagedFileSelected;
    /// <summary>Untracked files have no hunks in the index to patch; they stage whole.</summary>
    public bool CanApplyToLines => SelectedFile is { IsUntracked: false };
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
        var file = new DiffFileProjection(new GitKay.Core.GitService.DiffFileSummary(row.Diff.OldPath, row.Diff.NewPath, row.Label));
        file.ApplyContent(row.Diff);
        var rows = new List<IDiffRowProjection>();
        DiffRowBuilder.AppendFile(rows, file, GitKay.Core.DiffLayout.Unified);
        // The title bar names the file, and hidden context can't be revealed here: gaps show as plain hunk headers.
        rows = rows
            .Where(item => item is not DiffFileHeaderProjection)
            .Select(item => item is DiffGapProjection gap ? gap.HeaderText is { } header ? new DiffHunkHeaderProjection(header) : null : item)
            .OfType<IDiffRowProjection>()
            .ToList();
        for (var hunk = 0; hunk < file.Hunks.Count; hunk++)
            for (var line = 0; line < file.Hunks[hunk].Lines.Count; line++)
                _linePositions[file.Hunks[hunk].Lines[line]] = (hunk, line);
        Rows.AddRange(rows);
    }

    partial void OnSelectedUnstagedChanged(CommitFileRow? value) {
        if (!_syncing && value != null) _dispatch?.Invoke(CoreWindow.Msg.NewSelect(value.List, value.Path));
    }

    partial void OnSelectedStagedChanged(CommitFileRow? value) {
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
        row.Diff.OldPath != row.Diff.NewPath && row.Diff.OldPath != "/dev/null" && row.Diff.NewPath != "/dev/null"
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
        if (file.IsUntracked) {
            // An untracked file has nothing in the index to patch yet: it stages whole.
            ToggleFile(file);
            return;
        }
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
    private readonly ListBoxVimHost? _unstagedVim;
    private readonly ListBoxVimHost? _stagedVim;
    private Pane _lastPane = Pane.None;
    private Pane _currentPane = Pane.None;

    public CommitWindow() {
        InitializeComponent();
        Icon = AppIcon.Window;
    }

    public CommitWindow(string repositoryPath, string repositoryName) : this() {
        Surface.DiffLayout = GitKay.Core.DiffLayout.Unified;
        Surface.SharedVim = _vim;
        Surface.VimCommands = this;
        _unstagedVim = new ListBoxVimHost(UnstagedList, this);
        _stagedVim = new ListBoxVimHost(StagedList, this);

        var projection = new CommitWindowProjection(repositoryName) { Surface = Surface };
        projection.ConfirmDiscard = ConfirmAsync;
        DataContext = projection;
        Projection = projection;
        Surface.PointerReleased += (_, _) => projection.RefreshSelectionLabels();
        Surface.KeyUp += (_, _) => projection.RefreshSelectionLabels();
        Surface.LineMenuOpening += AddLineMenuItems;
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        AddHandler(GotFocusEvent, (_, _) => TrackPane(), RoutingStrategies.Bubble);

        var env = GitKay.Core.GitService.environment(repositoryPath);
        _host = ElmishHost.startAndBind(
            GitKay.Core.CommitWindow.program(env, ""),
            model => projection.Update(model),
            dispatch => projection.SetDispatch(dispatch));
        Activated += (_, _) => projection.RescanCommand.Execute(null);
        Closed += (_, _) => _host?.Dispose();
        Opened += (_, _) => MessageBox.Focus();
    }

    public CommitWindowProjection? Projection { get; }

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
    void IVimCommands.FindWord(string word, bool forward) => NotHere("Search");
    void IVimCommands.FindNext(GitKay.Core.Vim.VimPane pane, bool forward) => NotHere("Search");
    void IVimCommands.GoToParent(int index) { }
    void IVimCommands.GoToChild() { }
    void IVimCommands.OpenSearch(GitKay.Core.Vim.VimPane pane, bool forward) => NotHere("Search");

    private void NotHere(string what) {
        if (Projection != null) Projection.Status = $"{what} is in the main window";
    }

    // ----- Keys -----

    private void OnWindowKeyDown(object? sender, KeyEventArgs e) {
        if (Projection is not { } projection) return;
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
            if (e.Key is Key.Escape or Key.F1 || (e.KeySymbol == "?" && !inMessage)) projection.IsKeysOpen = false;
            Handled();
            return;
        }

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
            case Key.S when ctrl:
                projection.ToggleSignOff();
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
            case Key.U when ctrl && pane is Pane.Unstaged or Pane.Staged:
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
        if (e.KeySymbol == "?") {
            projection.IsKeysOpen = true;
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
            Add(file.IsUntracked ? "Stage file" : $"Stage {lines}", "S", () => projection.Stage(inDiff: true, stage: true));
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
