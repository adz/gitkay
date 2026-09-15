using Microsoft.FSharp.Core;
using GitKay.Core;
using System;
using System.ComponentModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia;

namespace GitKay.UI;

public partial class MainWindow : Window, IVimCommands {
    private MainProjection? _projection;
    private readonly System.Collections.Generic.Dictionary<string, double> _diffScrollOffsets = new(StringComparer.Ordinal);
    private string? _diffScrollCommit;
    private Control? _lastDiffPaneFocus;
    private Control? _activeHistoryColumnResizeHandle;
    private int _activeHistoryColumnResizeIndex = -1;
    private double _activeHistoryColumnResizeStartX;
    private double[]? _activeHistoryColumnResizeStartWidths;

    public MainWindow() {
        InitializeComponent();
        Icon = AppIcon.Window;
        _filesVimHost = new ListBoxVimHost(DiffFilesListBox, this);
        InitializeSearchPrompt();
        DiffRowsListBox.SharedVim = _vim;
        DiffRowsListBox.VimCommands = this;
        CommitListBox.VimCommands = this;
        DataContextChanged += OnDataContextChanged;
        CommitListBox.AddHandler(InputElement.KeyDownEvent, OnMainListBoxKeyDown, RoutingStrategies.Tunnel);
        CommitScrollViewer.AddHandler(InputElement.PointerPressedEvent, OnCommitScrollPointerPressed, RoutingStrategies.Tunnel);
        DiffRowsListBox.AddHandler(InputElement.KeyDownEvent, OnMainListBoxKeyDown, RoutingStrategies.Tunnel);
        DiffFilesListBox.AddHandler(InputElement.KeyDownEvent, OnMainListBoxKeyDown, RoutingStrategies.Tunnel);
        HistoryHeaderGrid.LayoutUpdated += (_, _) => SyncCommitColumnWidths();
        AddHandler(InputElement.KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        AddHandler(InputElement.KeyUpEvent, OnWindowKeyUp, RoutingStrategies.Tunnel);
        CommitScrollViewer.PointerEntered += (_, _) => OnPaneHovered(Pane.Commits);
        DiffRowsScrollViewer.PointerEntered += (_, _) => OnPaneHovered(Pane.Diff);
        DiffFilesListBox.PointerEntered += (_, _) => OnPaneHovered(Pane.Files);
        Deactivated += (_, _) => HideCtrlHints();
        Loaded += (_, _) => CommitListBox.Focus();
        SearchBox.AddHandler(InputElement.KeyDownEvent, OnSearchBoxKeyDown, RoutingStrategies.Tunnel);
        PaletteBox.AddHandler(InputElement.KeyDownEvent, OnPaletteBoxKeyDown, RoutingStrategies.Tunnel);
        CommitFindBox.AddHandler(InputElement.KeyDownEvent, OnCommitFindBoxKeyDown, RoutingStrategies.Tunnel);
        CommitListBox.FilterRequested += OnCommitFilterRequested;
        CommitListBox.HistoryRequested += revision => _projection?.ShowHistoryOf(revision);
        CommitListBox.BranchOperationRequested += OnBranchOperationRequested;
        CommitListBox.CopyRequested += name => CopyToClipboard(name, "Copied");
        DiffRowsListBox.TextCopied += (_, lines) => { if (_projection != null) _projection.Status = lines switch { 0 => "Copied", 1 => "Copied 1 line", _ => $"Copied {lines} lines" }; };
        AddHandler(InputElement.GotFocusEvent, (_, _) => { UpdatePaneFocusIndicator(); TrackPaneFocus(); }, RoutingStrategies.Bubble);
        AddHandler(InputElement.LostFocusEvent, (_, _) => Dispatcher.UIThread.Post(UpdatePaneFocusIndicator), RoutingStrategies.Bubble);
    }

    // ----- Pane navigation: Ctrl+h/j/k/l or Ctrl+arrows move spatially; Tab cycles; 1/2/3 jump. -----
    // Layout: commits on top; diff (left) and changed files (right) below.

    internal enum Pane { None, Commits, Diff, Files }

    private Pane FocusedPane =>
        CommitListBox.IsKeyboardFocusWithin ? Pane.Commits
        : DiffRowsListBox.IsKeyboardFocusWithin ? Pane.Diff
        : DiffFilesListBox.IsKeyboardFocusWithin ? Pane.Files
        : Pane.None;

    /// <summary>Keyboard pane changes win over hover until the pointer next enters a pane.</summary>
    private void FocusPaneFromKeyboard(Pane pane) {
        _hoveredPane = Pane.None;
        FocusPane(pane);
    }

    private void FocusPane(Pane pane) {
        if (pane == Pane.Files) {
            // A ListBox doesn't take focus itself; its item containers do.
            var item = DiffFilesListBox.SelectedItem ?? DiffFilesListBox.Items.OfType<DiffFileProjection>().FirstOrDefault();
            if (item != null) DiffFilesListBox.ScrollIntoView(item);
            var container = item == null ? null : DiffFilesListBox.ContainerFromItem(item);
            if (container == null || !container.Focus()) DiffFilesListBox.Focus();
            UpdatePaneFocusIndicator();
            return;
        }

        Control target = pane == Pane.Commits ? CommitListBox : DiffRowsListBox;
        target.Focus();
        UpdatePaneFocusIndicator();
    }

    /// <summary>Unfocused panes fade very slightly, so the pane keys go to stands out without extra chrome.</summary>
    private void UpdatePaneFocusIndicator() {
        const double fade = 0.22;
        var pane = FocusedPane;
        CommitPaneFocus.Opacity = pane != Pane.None && pane != Pane.Commits ? fade : 0;
        DiffPaneFocus.Opacity = pane != Pane.None && pane != Pane.Diff ? fade : 0;
        FilesPaneFocus.Opacity = pane != Pane.None && pane != Pane.Files ? fade : 0;
    }

    // ----- Hover to focus: pointing at a pane makes it the target for keys, without leaving a text box. -----

    private Pane _hoveredPane = Pane.None;

    private void OnPaneHovered(Pane pane) {
        _hoveredPane = pane;
        if (_projection is not { HoverToFocus: true } projection || projection.IsPaletteOpen || projection.IsShortcutHelpOpen) return;
        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox) return;
        if (FocusedPane != pane) FocusPane(pane);
    }

    internal static Pane? PaneInDirection(Pane from, Key key) => (from, key) switch {
        (Pane.Commits, Key.J or Key.Down) => Pane.Diff,
        (Pane.Diff or Pane.Files, Key.K or Key.Up) => Pane.Commits,
        (Pane.Diff, Key.L or Key.Right) => Pane.Files,
        (Pane.Files, Key.H or Key.Left) => Pane.Diff,
        (Pane.None, _) => Pane.Commits,
        _ => null,
    };

    private bool TryHandlePaneNavigation(KeyEventArgs e) {
        var from = FocusedPane;

        if (e.KeyModifiers == KeyModifiers.Control && e.Key is Key.H or Key.J or Key.K or Key.L or Key.Left or Key.Right or Key.Up or Key.Down) {
            if (PaneInDirection(from, e.Key) is { } target) FocusPaneFromKeyboard(target);
            return true;
        }

        if (e.Key == Key.Tab && e.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift && from != Pane.None) {
            var order = new[] { Pane.Commits, Pane.Diff, Pane.Files };
            var index = Array.IndexOf(order, from);
            var step = e.KeyModifiers == KeyModifiers.Shift ? -1 : 1;
            FocusPaneFromKeyboard(order[(index + step + order.Length) % order.Length]);
            return true;
        }

        if (e.KeyModifiers == KeyModifiers.Control && e.Key is Key.D1 or Key.D2 or Key.D3 or Key.NumPad1 or Key.NumPad2 or Key.NumPad3) {
            FocusPaneFromKeyboard(e.Key switch { Key.D1 or Key.NumPad1 => Pane.Commits, Key.D2 or Key.NumPad2 => Pane.Diff, _ => Pane.Files });
            return true;
        }

        if (e.KeyModifiers == KeyModifiers.None && e.Key == Key.Enter && from == Pane.Files && DiffFilesListBox.SelectedItem is RepoFileRow unchanged) {
            _projection?.ShowWholeFile(FileTarget.From(unchanged));
            return true;
        }

        if (e.KeyModifiers == KeyModifiers.None && e.Key == Key.Enter && from is Pane.Commits or Pane.Files) {
            FocusPaneFromKeyboard(Pane.Diff);
            return true;
        }

        // Esc clears a diff text selection first; the next Esc goes back to commits.
        if (e.KeyModifiers == KeyModifiers.None && e.Key == Key.Escape && from is Pane.Diff or Pane.Files && !DiffRowsListBox.HasTextSelection) {
            FocusPaneFromKeyboard(Pane.Commits);
            return true;
        }

        return false;
    }

    // ----- Search entry: Ctrl+F or / focuses the diff search from the diff pane, the commit search elsewhere. -----

    private bool IsDiffPaneFocused =>
        _projection is { HoverToFocus: true } && _hoveredPane != Pane.None
            ? _hoveredPane is Pane.Diff or Pane.Files
            : DiffRowsListBox.IsKeyboardFocusWithin || DiffFilesListBox.IsKeyboardFocusWithin || CommitFindBox.IsKeyboardFocusWithin;

    private void OnShortcutHelpBackdropPressed(object? sender, PointerPressedEventArgs e) {
        if (_projection != null) _projection.IsShortcutHelpOpen = false;
        e.Handled = true;
    }

    // ----- Ctrl hints: holding Ctrl briefly shows badges for Ctrl shortcuts; a quick chord never flashes them. -----

    private int _ctrlHintGeneration;
    private bool _ctrlHintPending;

    private void HideCtrlHints() {
        _ctrlHintPending = false;
        _ctrlHintGeneration++;
        if (_projection != null) _projection.IsCtrlHintsVisible = false;
    }

    private async void ShowCtrlHintsAfterHold() {
        var generation = ++_ctrlHintGeneration;
        _ctrlHintPending = true;
        await System.Threading.Tasks.Task.Delay(400);
        // Any other key or releasing Ctrl bumps the generation and cancels this.
        if (generation != _ctrlHintGeneration || _projection == null) return;
        _ctrlHintPending = false;
        _projection.IsCtrlHintsVisible = true;
    }

    private int _ctrlReleaseGeneration;

    private async void OnWindowKeyUp(object? sender, KeyEventArgs e) {
        if (e.Key is not (Key.LeftCtrl or Key.RightCtrl)) return;
        // X11 auto-repeat can deliver a held key as release+press pairs; only a release that isn't
        // immediately followed by another Ctrl press counts.
        var release = ++_ctrlReleaseGeneration;
        await System.Threading.Tasks.Task.Delay(60);
        if (release == _ctrlReleaseGeneration) HideCtrlHints();
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e) {
        // After a count, y, f / t or a prefix, the next key belongs to vim, not to a window shortcut.
        if (_vim.IsAwaitingKey && !(TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox)) return;
        if (e.Key is Key.LeftCtrl or Key.RightCtrl) {
            _ctrlReleaseGeneration++;
            // Held modifiers auto-repeat on some systems: only the first press starts the wait.
            if (_projection is { IsCtrlHintsVisible: false } && !_ctrlHintPending) ShowCtrlHintsAfterHold();
            return;
        }

        HideCtrlHints();

        var typingInTextBox = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox;

        if (_projection?.IsPaletteOpen == true) return;

        // Pane jumps, palette and history work everywhere, including from the search boxes.
        if (e.KeyModifiers == KeyModifiers.Control && e.Key is Key.D1 or Key.D2 or Key.D3 or Key.NumPad1 or Key.NumPad2 or Key.NumPad3) {
            FocusPaneFromKeyboard(e.Key is Key.D1 or Key.NumPad1 ? Pane.Commits : e.Key is Key.D2 or Key.NumPad2 ? Pane.Diff : Pane.Files);
            e.Handled = true;
            return;
        }
        var ctrlShift = KeyModifiers.Control | KeyModifiers.Shift;
        if (e.Key == Key.P && e.KeyModifiers == ctrlShift) { OpenPalette(PaletteMode.Commands); e.Handled = true; return; }
        if (e.Key == Key.P && e.KeyModifiers == KeyModifiers.Control) { OpenPalette(PaletteMode.Files); e.Handled = true; return; }
        if (e.Key == Key.D && e.KeyModifiers == ctrlShift) { ShowDiagnostics(); e.Handled = true; return; }
        if (e.Key == Key.G && e.KeyModifiers == KeyModifiers.Control) { OpenPalette(PaletteMode.Refs); e.Handled = true; return; }
        if (DiffZoomDirection(e) is { } zoom) { _projection?.ZoomDiff(zoom); e.Handled = true; return; }
        if (e.Key is Key.Left or Key.Right && e.KeyModifiers == KeyModifiers.Alt) {
            (e.Key == Key.Left ? _projection?.GoBackCommand : _projection?.GoForwardCommand)?.Execute(null);
            e.Handled = true;
            return;
        }

        if (!typingInTextBox) {
            if (e.Key == Key.OemSemicolon && e.KeyModifiers == KeyModifiers.Shift) { OpenPalette(PaletteMode.Commands); e.Handled = true; return; }
            if (e.Key is Key.O or Key.I && e.KeyModifiers == KeyModifiers.Control) {
                (e.Key == Key.O ? _projection?.GoBackCommand : _projection?.GoForwardCommand)?.Execute(null);
                e.Handled = true;
                return;
            }
        }

        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None && _projection is { IsSearchRunning: true } running && !typingInTextBox) {
            running.CancelSearch();
            e.Handled = true;
            return;
        }

        if (!typingInTextBox && !(_projection?.IsShortcutHelpOpen ?? false) && TryHandlePaneNavigation(e)) {
            e.Handled = true;
            return;
        }

        // F1 toggles the shortcut sheet; so does ? outside the panes, where vim's ? search doesn't apply. Esc closes it.
        if (_projection is { } help) {
            var questionMark = e.Key == Key.Oem2 && e.KeyModifiers == KeyModifiers.Shift && !typingInTextBox && (FocusedPane == Pane.None || help.IsShortcutHelpOpen);
            if (e.Key == Key.F1 || questionMark) {
                help.IsShortcutHelpOpen = !help.IsShortcutHelpOpen;
                e.Handled = true;
                return;
            }

            if (help.IsShortcutHelpOpen && e.Key == Key.Escape) {
                help.IsShortcutHelpOpen = false;
                e.Handled = true;
                return;
            }
        }

        // In a pane, / and ? open vim's search prompt (Vim.step); elsewhere / focuses the search box like Ctrl+F.
        var ctrlF = e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control;
        var slash = e.Key == Key.Oem2 && e.KeyModifiers == KeyModifiers.None && !typingInTextBox && FocusedPane == Pane.None;
        if (!ctrlF && !slash) return;

        var target = IsDiffPaneFocused ? CommitFindBox : SearchBox;
        if (IsDiffPaneFocused && _projection != null) _projection.DiffFindBackward = false;
        target.Focus();
        target.SelectAll();
        e.Handled = true;
    }

    // Recent searches stay out of the way of live results: they open on an empty box, or on ↓ while typing.
    private void OnSearchBoxGotFocus(object? sender, FocusChangedEventArgs e) =>
        _projection?.UpdateRecentSearchMatches(string.IsNullOrWhiteSpace(SearchBox.Text));

    private void OnSearchBoxLostFocus(object? sender, RoutedEventArgs e) {
        // Delay so a click on a recent search lands before the popup closes.
        DispatcherTimer.RunOnce(() => {
            if (_projection != null && !SearchBox.IsKeyboardFocusWithin) _projection.IsRecentSearchesOpen = false;
        }, TimeSpan.FromMilliseconds(180));
    }

    private void OnSearchBoxTextChanged(object? sender, TextChangedEventArgs e) {
        if (SearchBox.IsKeyboardFocusWithin) _projection?.UpdateRecentSearchMatches(string.IsNullOrWhiteSpace(SearchBox.Text));
    }

    private void OnRecentSearchPointerReleased(object? sender, PointerReleasedEventArgs e) {
        if (RecentSearchesList.SelectedItem is string recent) {
            _projection?.ApplyRecentSearch(recent);
            SearchBox.Focus();
            SearchBox.CaretIndex = SearchBox.Text?.Length ?? 0;
        }
    }

    private void OnSearchBoxKeyDown(object? sender, KeyEventArgs e) {
        if (_projection is not { } projection) return;
        var popupOpen = projection.IsRecentSearchesOpen && projection.RecentSearchMatches.Count > 0;

        switch (e.Key) {
            case Key.Down when !popupOpen && e.KeyModifiers == KeyModifiers.None:
                projection.UpdateRecentSearchMatches(true);
                e.Handled = projection.IsRecentSearchesOpen;
                break;
            case Key.Down or Key.Up when popupOpen:
                var count = projection.RecentSearchMatches.Count;
                var index = RecentSearchesList.SelectedIndex;
                RecentSearchesList.SelectedIndex = e.Key == Key.Down
                    ? Math.Min(count - 1, index + 1)
                    : Math.Max(-1, index - 1);
                e.Handled = true;
                break;
            case Key.Enter when popupOpen && RecentSearchesList.SelectedItem is string recent:
                projection.ApplyRecentSearch(recent);
                SearchBox.CaretIndex = SearchBox.Text?.Length ?? 0;
                e.Handled = true;
                break;
            case Key.Enter:
                projection.IsRecentSearchesOpen = false;
                if (e.KeyModifiers == KeyModifiers.Shift) projection.FindPreviousCommitCommand.Execute(null);
                else projection.SearchOrNextCommitCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Escape when popupOpen:
                projection.IsRecentSearchesOpen = false;
                e.Handled = true;
                break;
            // A running search: the first Esc stops it and keeps the query; the next clears it.
            case Key.Escape when projection.IsSearchRunning:
                projection.CancelSearch();
                e.Handled = true;
                break;
            case Key.Escape:
                projection.ClearSearchCommand.Execute(null);
                projection.SearchQuery = "";
                CommitListBox.Focus();
                e.Handled = true;
                break;
        }
    }

    private void OnCommitFindBoxKeyDown(object? sender, KeyEventArgs e) {
        if (e.Key != Key.Escape || _projection is not { } projection) return;
        projection.CommitFindQuery = "";
        FocusDiffPane();
        e.Handled = true;
    }

    // ----- Column filters -----

    private void OnCommitFilterRequested(string field, string? value) {
        if (_projection is not { } projection) return;
        if (value != null) {
            projection.ApplyColumnFilter(field, value);
            return;
        }

        // Ask for a value: add the prefix to the search box and put the caret after it.
        var text = projection.SearchQuery.TrimEnd();
        projection.SearchQuery = (text.Length == 0 ? "" : text + " ") + field + ":";
        projection.ShowOnlySearchMatches = true;
        SearchBox.Focus();
        SearchBox.CaretIndex = projection.SearchQuery.Length;
    }

    /// <summary>Ctrl with =/+ zooms in, - zooms out, 0 resets; applies to diff text only.</summary>
    internal static int? DiffZoomDirection(KeyEventArgs e) {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Alt)) return null;
        return e.Key switch {
            Key.OemPlus or Key.Add => 1,
            Key.OemMinus or Key.Subtract => -1,
            Key.D0 or Key.NumPad0 when !e.KeyModifiers.HasFlag(KeyModifiers.Shift) => 0,
            _ => null,
        };
    }

    private async void OnAboutMenuItemClick(object? sender, RoutedEventArgs e) => await new AboutWindow().ShowDialog(this);

    // ----- File filter chip -----

    private void ShowFileFilterMenu(Control anchor) {
        if (_projection is not { HasHistoryPathFilter: true } projection) return;
        var path = projection.HistoryPathFilter;
        var target = new FileTarget(path, path, path, null);
        var menu = new ContextMenu();
        void Add(string header, Action action) {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        menu.Items.Add(new MenuItem { Header = path, IsEnabled = false });
        menu.Items.Add(new Separator());
        Add("Clear file filter", projection.ClearHistoryPathFilter);
        Add("Copy relative path", () => CopyToClipboard(path, "Copied relative path"));
        Add("Copy full path", () => CopyToClipboard(projection.FullPath(target), "Copied full path"));
        Add("Show whole file", () => projection.ShowWholeFile(target));
        Add("Open in VS Code", () => projection.OpenInVsCode(target));
        menu.Open(anchor);
    }

    private void ShowBranchFilterMenu(Control anchor) {
        if (_projection is not { HasHistoryTipFilter: true } projection) return;
        var name = projection.HistoryTipFilter;
        var menu = new ContextMenu();
        void Add(string header, Action action) {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        menu.Items.Add(new MenuItem { Header = $"Commits on {name}", IsEnabled = false });
        menu.Items.Add(new Separator());
        Add("Show the full history", projection.ClearHistoryTipFilter);
        Add("Show all branches", () => projection.IsAllBranches = true);
        Add("Copy name", () => CopyToClipboard(name, "Copied"));
        menu.Open(anchor);
    }

    private void OnBranchFilterChipContextRequested(object? sender, ContextRequestedEventArgs e) {
        ShowBranchFilterMenu(BranchFilterChip);
        e.Handled = true;
    }

    private void OnBranchFilterChipPointerReleased(object? sender, PointerReleasedEventArgs e) {
        if (e.InitialPressMouseButton == MouseButton.Left && e.Source is not Button && (e.Source as Visual)?.FindAncestorOfType<Button>() == null)
            ShowBranchFilterMenu(BranchFilterChip);
    }

    private void OnFileFilterChipContextRequested(object? sender, ContextRequestedEventArgs e) {
        ShowFileFilterMenu(FileFilterChip);
        e.Handled = true;
    }

    private void OnFileFilterChipPointerReleased(object? sender, PointerReleasedEventArgs e) {
        if (e.InitialPressMouseButton == MouseButton.Left && e.Source is not Button && (e.Source as Visual)?.FindAncestorOfType<Button>() == null)
            ShowFileFilterMenu(FileFilterChip);
    }

    // ----- File actions: context menus on the file list and diff file headers, whole-file popup, VS Code. -----

    private void AddFileMenuItems(ContextMenu menu, FileTarget target, int? line) {
        if (_projection is not { } projection) return;
        void Add(string header, Action action, string? gesture = null) {
            var item = new MenuItem { Header = header };
            if (gesture != null) item.InputGesture = KeyGesture.Parse(gesture);
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        Add("Copy full path", () => CopyToClipboard(projection.FullPath(target), "Copied full path"));
        Add("Copy relative path", () => CopyToClipboard(target.Path, "Copied relative path"));
        menu.Items.Add(new Separator());
        Add("Show whole file", () => projection.ShowWholeFile(target));
        Add("Filter history to this file", () => projection.FilterHistoryToFile(target));
        if (projection.HasHistoryPathFilter) Add($"Clear file filter ({projection.HistoryPathFilter})", projection.ClearHistoryPathFilter);
        Add(line is { } number ? $"Open in VS Code at line {number}" : "Open in VS Code", () => projection.OpenInVsCode(target, line));
    }

    private async void CopyToClipboard(string text, string status) {
        if (Clipboard is not { } clipboard) return;
        await clipboard.SetTextAsync(text);
        if (_projection != null) _projection.Status = $"{status}: {text}";
    }

    private void OnDiffFilesContextRequested(object? sender, ContextRequestedEventArgs e) {
        var row = (e.Source as Control)?.DataContext;
        FileTarget? target = row switch {
            DiffFileProjection file => FileTarget.From(file),
            RepoFileRow unchanged => FileTarget.From(unchanged),
            _ => null,
        };
        if (target == null) return;
        var menu = new ContextMenu();
        AddFileMenuItems(menu, target, null);
        menu.Open(e.Source as Control ?? DiffFilesListBox);
        e.Handled = true;
    }

    private void OnDiffFilesDoubleTapped(object? sender, TappedEventArgs e) {
        if ((e.Source as Control)?.DataContext is RepoFileRow unchanged) _projection?.ShowWholeFile(FileTarget.From(unchanged));
        else if ((e.Source as Control)?.DataContext is DiffFileProjection file) _projection?.ShowWholeFile(FileTarget.From(file));
    }

    private void OnDiffFileHeaderContextRequested(object? sender, DiffFileMenuEventArgs e) =>
        AddFileMenuItems(e.Menu, FileTarget.From(e.File), e.LineNumber);

    private void OpenWholeFile(FileTarget target) {
        if (_projection is not { RepositoryPath: { } repo, SelectedCommit: { IsWorkingTree: false } commit } projection) return;
        var window = new WholeFileWindow(projection, repo, commit.FullHash, commit.Hash, target);
        window.Show(this);
    }

    // ----- Repositories and remotes -----

    private void OnOpenRepositoryMenuItemClick(object? sender, RoutedEventArgs e) => OnWindowCommandRequested("open-repository");

    private DiagnosticsWindow? _diagnosticsWindow;

    private void OnDiagnosticsMenuItemClick(object? sender, RoutedEventArgs e) => ShowDiagnostics();

    /// <summary>One diagnostics window, left open alongside the main window; asking again brings it forward.</summary>
    private void ShowDiagnostics() {
        if (_diagnosticsWindow is { } open) {
            open.Activate();
            return;
        }

        _diagnosticsWindow = new DiagnosticsWindow();
        _diagnosticsWindow.CommandCancelled += name => {
            // An interrupted command dispatches nothing; settle the matching app state so it doesn't stay "running".
            if (name.StartsWith("search", StringComparison.Ordinal)) _projection?.CancelSearch();
            else if (_projection != null) _projection.Status = $"Cancelled {name}";
        };
        _diagnosticsWindow.Closed += (_, _) => _diagnosticsWindow = null;
        _diagnosticsWindow.Show();
    }

    private void OnFetchAllMenuItemClick(object? sender, RoutedEventArgs e) => OnWindowCommandRequested("fetch-all");

    private async System.Threading.Tasks.Task OpenRepositoryAsync() {
        var folders = await StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions {
            Title = "Open Git repository",
            AllowMultiple = false,
        });
        if (folders.Count == 0 || Avalonia.Platform.Storage.StorageProviderExtensions.TryGetLocalPath(folders[0]) is not { } path || _projection is not { } projection) return;
        if (!IsInsideRepository(path)) {
            projection.Status = $"Not a Git repository: {path}";
            return;
        }

        try {
            ExternalTools.StartGitKay(path);
            projection.Status = $"Opened {path} in a new window";
        }
        catch (Exception exception) {
            projection.Status = $"Could not open {path}: {exception.Message}";
        }
    }

    private static bool IsInsideRepository(string path) {
        for (var directory = new System.IO.DirectoryInfo(path); directory != null; directory = directory.Parent)
            if (LibGit2Sharp.Repository.IsValid(directory.FullName)) return true;
        return false;
    }

    private async void OnBranchOperationRequested(string operation, BranchTarget branch) {
        if (operation != "delete") {
            RunGitOperation(GitOperations.ForBranch(operation, branch));
            return;
        }

        if (_projection is not { WorkingDirectory: { } directory }) return;
        var choice = await DeleteBranchDialog.ShowAsync(this, directory, branch);
        if (choice != null) RunGitOperation(GitOperations.DeleteBranch(branch, force: choice == DeleteBranchDialog.Choice.ForceDelete));
    }

    private void RunGitOperation(GitOperation operation) {
        if (_projection is not { WorkingDirectory: { } directory } projection) return;
        var window = new GitOperationWindow(operation, directory, succeeded => {
            if (succeeded) projection.RereadRefs();
        });
        window.Show(this);
    }

    /// <summary>Restarts the footer's pulse animation for each new error.</summary>
    private void PulseStatusBar() => Dispatcher.UIThread.Post(() => {
        StatusBar.Classes.Remove("pulse");
        Dispatcher.UIThread.Post(() => StatusBar.Classes.Add("pulse"), DispatcherPriority.Background);
    });

    private void OnDiffFolderPointerPressed(object? sender, PointerPressedEventArgs e) {
        if (sender is Control { DataContext: DiffFileFolderRow folder } && _projection != null) {
            _projection.ToggleDiffFolderCommand.Execute(folder);
            e.Handled = true;
        }
    }

    private void OnHistoryHeaderContextRequested(object? sender, ContextRequestedEventArgs e) {
        if (sender is not Control { Tag: string column } header || _projection is not { } projection) return;
        var menu = new ContextMenu();
        void Add(string title, Action action) {
            var item = new MenuItem { Header = title };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        switch (column) {
            case "message": Add("Filter by message…", () => OnCommitFilterRequested("message", null)); Add("Filter by branch / tag…", () => OnCommitFilterRequested("ref", null)); break;
            case "hash": Add("Filter by hash…", () => OnCommitFilterRequested("hash", null)); break;
            case "author": Add("Filter by author…", () => OnCommitFilterRequested("author", null)); break;
            case "date": Add("Commits after…", () => OnCommitFilterRequested("after", null)); Add("Commits before…", () => OnCommitFilterRequested("before", null)); break;
        }

        menu.Items.Add(new Separator());
        Add("Clear this filter", () => projection.ClearColumnFilterCommand.Execute(column));
        Add(projection.ShowOnlySearchMatches ? "Show all commits" : "Show only matching commits", () => projection.ShowOnlySearchMatches = !projection.ShowOnlySearchMatches);
        menu.Open(header);
        e.Handled = true;
    }

    private async void OnSettingsMenuItemClick(object? sender, RoutedEventArgs e) {
        var settingsWindow = new SettingsWindow {
            DataContext = DataContext,
        };

        await settingsWindow.ShowDialog(this);
    }

    private void OnDataContextChanged(object? sender, EventArgs e) {
        if (_projection != null) {
            _projection.PropertyChanged -= OnProjectionPropertyChanged;
        }

        _projection = DataContext as MainProjection;

        if (_projection != null) {
            _projection.PropertyChanged += OnProjectionPropertyChanged;
            _projection.WindowCommandRequested += OnWindowCommandRequested;
            _projection.WholeFileRequested += OpenWholeFile;
            _projection.ErrorStatusRaised += PulseStatusBar;
            _projection.FileJumpRequested += file =>
                Dispatcher.UIThread.Post(() => DiffRowsListBox.ScrollToTop(file.Header), DispatcherPriority.Background);
        }
    }

    // ----- Palette -----

    private void OpenPalette(PaletteMode mode) {
        if (_projection == null) return;
        _projection.OpenPalette(mode);
        Dispatcher.UIThread.Post(() => { PaletteBox.Focus(); PaletteBox.CaretIndex = PaletteBox.Text?.Length ?? 0; }, DispatcherPriority.Loaded);
    }

    private void ClosePalette() {
        _projection?.ClosePalette();
        FocusPane(Pane.Commits);
    }

    private void OnPaletteBackdropPressed(object? sender, PointerPressedEventArgs e) {
        ClosePalette();
        e.Handled = true;
    }

    private void OnPalettePanelPressed(object? sender, PointerPressedEventArgs e) => e.Handled = true;

    private void OnPaletteListPointerReleased(object? sender, PointerReleasedEventArgs e) {
        if (PaletteList.SelectedItem is PaletteItem item) RunPalette(item);
    }

    private void RunPalette(PaletteItem? item = null) {
        if (_projection == null) return;
        var mode = _projection.PaletteMode;
        _projection.RunPaletteItem(item);
        // Jumping to a file or commit moves focus to where the result is; commands that open another palette keep it.
        if (!_projection.IsPaletteOpen)
            FocusPane(mode == PaletteMode.Files ? Pane.Diff : Pane.Commits);
        else
            Dispatcher.UIThread.Post(() => PaletteBox.Focus(), DispatcherPriority.Loaded);
    }

    private void OnPaletteBoxKeyDown(object? sender, KeyEventArgs e) {
        if (_projection is not { IsPaletteOpen: true } projection) return;
        switch (e.Key) {
            case Key.Down: projection.MovePaletteSelection(1); break;
            case Key.Up: projection.MovePaletteSelection(-1); break;
            case Key.PageDown: projection.MovePaletteSelection(10); break;
            case Key.PageUp: projection.MovePaletteSelection(-10); break;
            case Key.N when e.KeyModifiers == KeyModifiers.Control: projection.MovePaletteSelection(1); break;
            case Key.P when e.KeyModifiers == KeyModifiers.Control: projection.MovePaletteSelection(-1); break;
            case Key.Enter: RunPalette(); break;
            case Key.Escape: ClosePalette(); break;
            default: return;
        }

        if (PaletteList.SelectedItem is { } selected) PaletteList.ScrollIntoView(selected);
        e.Handled = true;
    }

    private async void OnWindowCommandRequested(string command) {
        switch (command) {
            case "settings":
                OnSettingsMenuItemClick(this, new RoutedEventArgs());
                break;
            case "focus-search":
                SearchBox.Focus();
                SearchBox.SelectAll();
                break;
            case "focus-find":
                CommitFindBox.Focus();
                CommitFindBox.SelectAll();
                break;
            case "open-repository":
                await OpenRepositoryAsync();
                break;
            case "diagnostics":
                ShowDiagnostics();
                break;
            case "fetch-all":
                RunGitOperation(GitOperations.FetchAll());
                break;
            case "copy-hash" or "copy-subject" when _projection?.SelectedCommit is { IsWorkingTree: false } commit && Clipboard is { } clipboard:
                await clipboard.SetTextAsync(command == "copy-hash" ? commit.FullHash : commit.Subject);
                _projection.Status = command == "copy-hash" ? $"Copied {commit.Hash}" : "Copied subject";
                break;
        }
    }

    private void OnProjectionPropertyChanged(object? sender, PropertyChangedEventArgs e) {
        // Applying a commit search replaces a / search's underline and its n / N.
        if (e.PropertyName == nameof(MainProjection.CommitSearchHighlight)) ClearCommitQuickFind();
        if (e.PropertyName == nameof(MainProjection.SelectedCommit) && _projection != null) {
            // Remember where the diff was scrolled for the commit we're leaving; restore it when returning.
            if (_diffScrollCommit != null) _diffScrollOffsets[_diffScrollCommit] = DiffRowsListBox.CurrentScrollOffset;
            _diffScrollCommit = _projection.SelectedCommit?.FullHash;
            if (_diffScrollCommit != null && _diffScrollOffsets.TryGetValue(_diffScrollCommit, out var offset) && offset > 0)
                DiffRowsListBox.RestoreScrollOffsetWhenReady(offset);
        }

        if (e.PropertyName == nameof(MainProjection.IsSearchPanelExpanded)) {
            var currentProjection = _projection;
            if (currentProjection == null || !currentProjection.IsSearchPanelExpanded) {
                return;
            }

            Dispatcher.UIThread.Post(() => {
                if (!ReferenceEquals(_projection, currentProjection) || !currentProjection.IsSearchPanelExpanded) {
                    return;
                }

                SearchBox.Focus();
                SearchBox.SelectAll();
            });

            return;
        }

        if (e.PropertyName != nameof(MainProjection.SelectedCommit)
            && e.PropertyName != nameof(MainProjection.Commits)
            && e.PropertyName != nameof(MainProjection.SelectedDiffFile)
            && e.PropertyName != nameof(MainProjection.SelectedDiffRow)
            && e.PropertyName != nameof(MainProjection.SelectedDiffFiles)
            && e.PropertyName != nameof(MainProjection.IsSearchPanelExpanded)) {
            return;
        }

        var projection = _projection;
        if (projection == null) {
            return;
        }

        var shouldScrollCommit = e.PropertyName is nameof(MainProjection.SelectedCommit) or nameof(MainProjection.Commits);
        var shouldScrollFile = e.PropertyName == nameof(MainProjection.SelectedDiffFile);
        var shouldScrollDiff = e.PropertyName is nameof(MainProjection.SelectedDiffFile) or nameof(MainProjection.SelectedDiffRow);

        Dispatcher.UIThread.Post(() => {
            if (!ReferenceEquals(_projection, projection)) {
                return;
            }

            if (shouldScrollCommit && projection.SelectedCommit != null) {
                CommitListBox.ScrollIntoView(projection.SelectedCommit);
            }

            if (shouldScrollFile && projection.SelectedDiffFile != null) {
                DiffFilesListBox.ScrollIntoView(projection.SelectedDiffFile);
            }

            var target = projection.SelectedDiffRow ?? projection.SelectedDiffFile?.Header;
            if (shouldScrollDiff && target != null) {
                // Keyboard movement only brings rows into view; explicit file jumps are handled by FileJumpRequested.
                DiffRowsListBox.ScrollIntoView(target);
            }

            // Never pull focus out of a text box: Enter in a search box selects commits and must keep working.
            var typing = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox;
            if (!typing
                && !CommitListBox.IsKeyboardFocusWithin
                && !DiffRowsListBox.IsKeyboardFocusWithin
                && !DiffFilesListBox.IsKeyboardFocusWithin) {
                DiffRowsListBox.Focus();
            }
        }, DispatcherPriority.Loaded);
    }

    private void OnCommitScrollPointerPressed(object? sender, PointerPressedEventArgs e) {
        CommitListBox.Focus();
        CommitListBox.SelectAt(e.GetPosition(CommitListBox).Y);
    }

    private void OnDiffModeButtonContextRequested(object? sender, ContextRequestedEventArgs e) {
        if (_projection is not { } projection) return;
        var menu = new ContextMenu();
        var context = new MenuItem { Header = "Context lines" };
        foreach (var count in projection.DiffContextLineCounts) {
            var item = new MenuItem {
                Header = count.Label,
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = ReferenceEquals(count, projection.SelectedDiffContextLineCount),
            };
            item.Click += (_, _) => projection.SelectedDiffContextLineCount = count;
            context.Items.Add(item);
        }
        menu.Items.Add(context);
        menu.Open(DiffModeButton);
        e.Handled = true;
    }

    private void OnDiffRowsListBoxGotFocus(object? sender, FocusChangedEventArgs e) {
        _lastDiffPaneFocus = DiffRowsListBox;
    }

    private void OnDiffFilesListBoxGotFocus(object? sender, FocusChangedEventArgs e) {
        _lastDiffPaneFocus = DiffFilesListBox;
    }

    // ----- vim keys: every pane shares one GitKay.Core.Vim session, so counts and pending keys behave the same. -----

    private readonly Vim.VimSession _vim = new();
    private readonly ListBoxVimHost _filesVimHost;

    private void OnMainListBoxKeyDown(object? sender, KeyEventArgs e) {
        Vim.IVimHost? host = sender switch {
            DiffSurfaceControl diff => diff,
            CommitSurfaceControl commits => commits,
            ListBox list when ReferenceEquals(list, DiffFilesListBox) => _filesVimHost,
            _ => null,
        };
        if (host == null) return;

        if (_vim.Handle(host, VimKeys.From(e))) {
            e.Handled = true;
            return;
        }

        if (ReferenceEquals(sender, CommitListBox) && e.Key == Key.Right && e.KeyModifiers == KeyModifiers.None) {
            FocusDiffPane();
            e.Handled = true;
            return;
        }

        if ((ReferenceEquals(sender, DiffRowsListBox) || ReferenceEquals(sender, DiffFilesListBox))
            && e.Key == Key.Left && e.KeyModifiers == KeyModifiers.None) {
            CommitListBox.Focus();
            e.Handled = true;
        }
    }

    void IVimCommands.CopyCommitReference(bool subject) => OnWindowCommandRequested(subject ? "copy-subject" : "copy-hash");

    void IVimCommands.FindWord(string word, bool forward) => _projection?.FindWordInDiff(word, forward);

    void IVimCommands.FindNext(Vim.VimPane pane, bool forward) {
        if (_projection is not { } projection) return;
        if (pane == Vim.VimPane.Commits && TryStepCommitQuickFind(forward)) return;
        if (pane == Vim.VimPane.Commits)
            (forward ? projection.FindNextCommitCommand : projection.FindPreviousCommitCommand).Execute(null);
        else
            (forward ? projection.FindInCommitCommand : projection.FindPreviousInCommitCommand).Execute(null);
    }

    void IVimCommands.GoToParent(int index) => _projection?.GoToParent(index);

    void IVimCommands.GoToChild() => _projection?.GoToChild();

    private void FocusDiffPane() {
        var target = _lastDiffPaneFocus;

        if (target == null || !target.IsVisible) {
            target = DiffRowsListBox;
        }

        target.Focus();
    }

    private void OnHistoryColumnResizePointerPressed(object? sender, PointerPressedEventArgs e) {
        if (sender is not Control handle
            || handle.Tag is not string tag
            || !int.TryParse(tag, out var columnIndex)
            || columnIndex < 0
            || columnIndex >= HistoryHeaderGrid.ColumnDefinitions.Count) {
            return;
        }

        var column = HistoryHeaderGrid.ColumnDefinitions[columnIndex];
        _activeHistoryColumnResizeHandle = handle;
        _activeHistoryColumnResizeIndex = columnIndex;
        if (columnIndex + 1 >= HistoryHeaderGrid.ColumnDefinitions.Count)
            return;

        _activeHistoryColumnResizeStartX = e.GetPosition(HistoryHeaderGrid).X;
        _activeHistoryColumnResizeStartWidths = HistoryHeaderGrid.ColumnDefinitions
            .Select((definition, index) => GetEffectiveColumnWidth(index, definition))
            .ToArray();

        e.Pointer.Capture(handle);
        e.Handled = true;
    }

    private void OnHistoryColumnResizePointerMoved(object? sender, PointerEventArgs e) {
        if (_activeHistoryColumnResizeHandle == null
            || !ReferenceEquals(sender, _activeHistoryColumnResizeHandle)
            || _activeHistoryColumnResizeIndex < 0
            || _activeHistoryColumnResizeIndex >= HistoryHeaderGrid.ColumnDefinitions.Count - 1) {
            return;
        }

        var startWidths = _activeHistoryColumnResizeStartWidths;
        if (startWidths == null || startWidths.Length != HistoryHeaderGrid.ColumnDefinitions.Count)
            return;

        var boundary = _activeHistoryColumnResizeIndex;
        var requestedDelta = e.GetPosition(HistoryHeaderGrid).X - _activeHistoryColumnResizeStartX;
        var widths = (double[])startWidths.Clone();

        if (requestedDelta > 0) {
            var available = 0d;
            for (var index = boundary + 1; index < widths.Length; index++)
                available += Math.Max(0, widths[index] - HistoryHeaderGrid.ColumnDefinitions[index].MinWidth);
            var applied = Math.Min(requestedDelta, available);
            widths[boundary] += applied;
            ShrinkColumns(widths, boundary + 1, widths.Length, 1, applied);
        }
        else if (requestedDelta < 0) {
            var requested = -requestedDelta;
            var available = 0d;
            for (var index = boundary; index >= 0; index--)
                available += Math.Max(0, widths[index] - HistoryHeaderGrid.ColumnDefinitions[index].MinWidth);
            var applied = Math.Min(requested, available);
            widths[boundary + 1] += applied;
            ShrinkColumns(widths, boundary, -1, -1, applied);
        }

        for (var index = 0; index < widths.Length; index++)
            HistoryHeaderGrid.ColumnDefinitions[index].Width = new GridLength(widths[index], GridUnitType.Pixel);
        e.Handled = true;
    }

    private void OnHistoryColumnResizePointerReleased(object? sender, PointerReleasedEventArgs e) {
        if (ReferenceEquals(sender, _activeHistoryColumnResizeHandle)) {
            e.Pointer.Capture(null);
            ClearHistoryColumnResize();
            e.Handled = true;
        }
    }

    private void OnHistoryColumnResizePointerCaptureLost(object? sender, PointerCaptureLostEventArgs e) {
        if (ReferenceEquals(sender, _activeHistoryColumnResizeHandle)) {
            ClearHistoryColumnResize();
        }
    }

    private void ShrinkColumns(double[] widths, int start, int stop, int step, double amount) {
        for (var index = start; index != stop && amount > 0; index += step) {
            var minimum = Math.Max(0, HistoryHeaderGrid.ColumnDefinitions[index].MinWidth);
            var reduction = Math.Min(amount, Math.Max(0, widths[index] - minimum));
            widths[index] -= reduction;
            amount -= reduction;
        }
    }

    private void SyncCommitColumnWidths() {
        if (HistoryHeaderGrid.ColumnDefinitions.Count < 5)
            return;

        CommitListBox.GraphWidth = HistoryHeaderGrid.ColumnDefinitions[0].ActualWidth;
        CommitListBox.SubjectWidth = HistoryHeaderGrid.ColumnDefinitions[1].ActualWidth;
        CommitListBox.HashWidth = HistoryHeaderGrid.ColumnDefinitions[2].ActualWidth;
        CommitListBox.AuthorWidth = HistoryHeaderGrid.ColumnDefinitions[3].ActualWidth;
        CommitListBox.DateWidth = HistoryHeaderGrid.ColumnDefinitions[4].ActualWidth;
    }

    private double GetEffectiveColumnWidth(int columnIndex, ColumnDefinition column) {
        if (column.ActualWidth > 0d) {
            return column.ActualWidth;
        }

        return column.Width.IsAbsolute ? column.Width.Value : HistoryHeaderGrid.Bounds.Width / HistoryHeaderGrid.ColumnDefinitions.Count;
    }

    private void ClearHistoryColumnResize() {
        _activeHistoryColumnResizeHandle = null;
        _activeHistoryColumnResizeIndex = -1;
        _activeHistoryColumnResizeStartX = 0d;
        _activeHistoryColumnResizeStartWidths = null;
    }

    /// <summary>Captures the current splitter positions and history column widths for persistence.</summary>
    public UiLayout CaptureLayout() {
        var top = MainSplitGrid.RowDefinitions[0].ActualHeight;
        var bottom = MainSplitGrid.RowDefinitions[2].ActualHeight;
        var columns = HistoryHeaderGrid.ColumnDefinitions;
        static FSharpOption<double>? Option(double? value) => value is { } some ? FSharpOption<double>.Some(some) : null;
        static FSharpOption<double>? Positive(double value) => Option(value > 0 ? value : null);
        // While a pane is maximised, keep the normal layout rather than the maximised one.
        var normal = SavedNormalLayout;
        return new UiLayout(
            normal is { } saved ? Option(saved.HistoryPaneRatio) : Option(top + bottom > 0 ? top / (top + bottom) : null),
            normal is { } savedWidth ? Option(savedWidth.FileListWidth) : Positive(DiffSplitGrid.ColumnDefinitions[2].ActualWidth),
            Positive(columns[0].ActualWidth),
            Positive(columns[2].ActualWidth),
            Positive(columns[3].ActualWidth),
            Positive(columns[4].ActualWidth));
    }

    /// <summary>Restores persisted layout; the subject column stays proportional so it absorbs window resizes.</summary>
    public void ApplyLayout(UiLayout layout) {
        layout = UiLayoutModule.normalize(layout);
        if (layout.HistoryPaneRatio is { } ratio) {
            MainSplitGrid.RowDefinitions[0].Height = new GridLength(ratio.Value, GridUnitType.Star);
            MainSplitGrid.RowDefinitions[2].Height = new GridLength(1 - ratio.Value, GridUnitType.Star);
        }

        if (layout.FileListWidth is { } fileListWidth)
            DiffSplitGrid.ColumnDefinitions[2].Width = new GridLength(Math.Max(DiffSplitGrid.ColumnDefinitions[2].MinWidth, fileListWidth.Value), GridUnitType.Pixel);

        var columns = HistoryHeaderGrid.ColumnDefinitions;
        void Restore(int index, FSharpOption<double>? width) {
            if (width is { } value)
                columns[index].Width = new GridLength(Math.Max(columns[index].MinWidth, value.Value), GridUnitType.Pixel);
        }

        Restore(0, layout.GraphColumnWidth);
        Restore(2, layout.HashColumnWidth);
        // The author column now shows the email too; a width at or below the old 110px default takes the new default.
        Restore(3, layout.AuthorColumnWidth is { Value: <= 110 } ? null : layout.AuthorColumnWidth);
        Restore(4, layout.DateColumnWidth);
        columns[1].Width = new GridLength(1, GridUnitType.Star);
    }

    public override void Render(DrawingContext context) {
        base.Render(context);

        if (DataContext is MainProjection projection) {
            projection.LogFirstPaint();
        }
    }
}
