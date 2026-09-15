using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Input;
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
        UnstagedTitle = $"Unstaged ({UnstagedFiles.Count})";
        StagedTitle = $"Staged ({StagedFiles.Count})";
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
        if (!_syncing) _dispatch?.Invoke(CoreWindow.Msg.NewSetAmend(value));
    }

    partial void OnSignOffChanged(bool value) {
        if (!_syncing) _dispatch?.Invoke(CoreWindow.Msg.NewSetSignOff(value));
    }

    private CommitFileRow? SelectedFile => SelectedStaged ?? SelectedUnstaged;

    [RelayCommand]
    private void Rescan() => _dispatch?.Invoke(CoreWindow.Msg.Rescan);

    [RelayCommand]
    private void StageAll() => _dispatch?.Invoke(CoreWindow.Msg.NewStagePaths(ListModule.OfSeq(UnstagedFiles.Select(row => row.Path))));

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

public partial class CommitWindow : Window {
    private IDisposable? _host;

    public CommitWindow() {
        InitializeComponent();
        Icon = AppIcon.Window;
    }

    public CommitWindow(string repositoryPath, string repositoryName) : this() {
        Surface.DiffLayout = GitKay.Core.DiffLayout.Unified;
        var projection = new CommitWindowProjection(repositoryName) { Surface = Surface };
        projection.ConfirmDiscard = ConfirmAsync;
        DataContext = projection;
        Projection = projection;
        Surface.PointerReleased += (_, _) => projection.RefreshSelectionLabels();
        Surface.KeyUp += (_, _) => projection.RefreshSelectionLabels();
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);

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

    private void OnFileDoubleTapped(object? sender, TappedEventArgs e) {
        if ((e.Source as Control)?.DataContext is CommitFileRow row) Projection?.ToggleFile(row);
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e) {
        if (Projection is not { } projection) return;
        var inMessage = MessageBox.IsFocused;
        var none = e.KeyModifiers == KeyModifiers.None;

        if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control)) {
            projection.Commit();
            e.Handled = true;
        }
        else if (e.Key == Key.F5) {
            projection.RescanCommand.Execute(null);
            e.Handled = true;
        }
        else if (inMessage) {
            if (e.Key == Key.Escape) { Surface.Focus(); e.Handled = true; }
        }
        else if (none && (e.Key is Key.S or Key.U) && Surface.IsKeyboardFocusWithin) {
            projection.ApplyToSelection();
            e.Handled = true;
        }
        else if (none && (e.Key is Key.Enter or Key.S or Key.U) && (UnstagedList.IsKeyboardFocusWithin || StagedList.IsKeyboardFocusWithin)) {
            projection.ToggleFile();
            e.Handled = true;
        }
        else if (none && e.Key == Key.Tab && (UnstagedList.IsKeyboardFocusWithin || StagedList.IsKeyboardFocusWithin)) {
            Surface.Focus();
            e.Handled = true;
        }
        else if (none && e.Key is Key.J or Key.K && Surface.IsKeyboardFocusWithin) {
            Surface.MoveSelection(e.Key == Key.J ? 1 : -1);
            e.Handled = true;
        }
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
