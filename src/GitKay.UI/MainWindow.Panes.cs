using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using GitKay.Core;

namespace GitKay.UI;

// ----- Ctrl+W pane commands: move between panes, maximise the focused one, restore the layout. -----
public partial class MainWindow {
    private Pane _currentPane = Pane.None;
    private Pane _previousPane = Pane.None;

    /// <summary>The layout before a pane was maximised; null when the normal layout is showing.</summary>
    private SavedPaneLayout? _savedPaneLayout;

    private sealed record SavedPaneLayout(GridLength Top, GridLength Bottom, GridLength Splitter, GridLength DiffColumn, double DiffMinWidth,
        GridLength FilesColumn, double FilesMinWidth, GridLength ColumnSplitter);

    /// <summary>Remembers the pane focused before the current one, for Ctrl+W p.</summary>
    private void TrackPaneFocus() {
        var pane = FocusedPane;
        if (pane == Pane.None || pane == _currentPane) return;
        _previousPane = _currentPane;
        _currentPane = pane;
    }

    void IVimCommands.PaneCommand(Vim.VimPaneCommand command) {
        var from = FocusedPane;
        switch (command) {
            case Vim.VimPaneCommand.Left or Vim.VimPaneCommand.Down or Vim.VimPaneCommand.Up or Vim.VimPaneCommand.Right: {
                var key = command switch {
                    Vim.VimPaneCommand.Left => Avalonia.Input.Key.H,
                    Vim.VimPaneCommand.Down => Avalonia.Input.Key.J,
                    Vim.VimPaneCommand.Up => Avalonia.Input.Key.K,
                    _ => Avalonia.Input.Key.L,
                };
                if (PaneInDirection(from, key) is { } target) FocusPaneShowing(target);
                break;
            }
            case Vim.VimPaneCommand.Next or Vim.VimPaneCommand.Previous: {
                var order = new[] { Pane.Commits, Pane.Diff, Pane.Files };
                var step = command == Vim.VimPaneCommand.Next ? 1 : -1;
                var index = Math.Max(0, Array.IndexOf(order, from));
                FocusPaneShowing(order[(index + step + order.Length) % order.Length]);
                break;
            }
            case Vim.VimPaneCommand.Last:
                if (_previousPane != Pane.None) FocusPaneShowing(_previousPane);
                break;
            case Vim.VimPaneCommand.Only:
                if (_savedPaneLayout != null) RestorePaneLayout();
                else MaximizePane(from, height: true, width: true);
                break;
            case Vim.VimPaneCommand.MaximizeHeight:
                MaximizePane(from, height: true, width: false);
                break;
            case Vim.VimPaneCommand.MaximizeWidth:
                MaximizePane(from, height: false, width: true);
                break;
            case Vim.VimPaneCommand.Equalize:
                RestorePaneLayout();
                break;
        }
    }

    /// <summary>Moving to a pane a maximised layout hides brings the normal layout back first.</summary>
    private void FocusPaneShowing(Pane pane) {
        if (_savedPaneLayout != null && !IsPaneShowing(pane)) RestorePaneLayout();
        FocusPaneFromKeyboard(pane);
    }

    private bool IsPaneShowing(Pane pane) => pane switch {
        Pane.Commits => MainSplitGrid.RowDefinitions[0].Height.Value > 0,
        Pane.Diff => MainSplitGrid.RowDefinitions[2].Height.Value > 0 && DiffSplitGrid.ColumnDefinitions[0].Width.Value > 0,
        Pane.Files => MainSplitGrid.RowDefinitions[2].Height.Value > 0 && DiffSplitGrid.ColumnDefinitions[2].Width.Value > 0,
        _ => true,
    };

    private void MaximizePane(Pane pane, bool height, bool width) {
        if (pane == Pane.None) return;
        var rows = MainSplitGrid.RowDefinitions;
        var columns = DiffSplitGrid.ColumnDefinitions;
        _savedPaneLayout ??= new SavedPaneLayout(rows[0].Height, rows[2].Height, rows[1].Height, columns[0].Width, columns[0].MinWidth,
            columns[2].Width, columns[2].MinWidth, columns[1].Width);

        var zero = new GridLength(0, GridUnitType.Pixel);
        var star = new GridLength(1, GridUnitType.Star);
        if (height) {
            var commits = pane == Pane.Commits;
            rows[0].Height = commits ? star : zero;
            rows[2].Height = commits ? zero : star;
            rows[1].Height = zero;
        }
        if (width && pane is Pane.Diff or Pane.Files) {
            var diff = pane == Pane.Diff;
            columns[0].MinWidth = diff ? _savedPaneLayout.DiffMinWidth : 0;
            columns[0].Width = diff ? star : zero;
            columns[2].MinWidth = diff ? 0 : _savedPaneLayout.FilesMinWidth;
            columns[2].Width = diff ? zero : star;
            columns[1].Width = zero;
        }
        SyncHiddenPaneContent();
        if (_projection != null) _projection.Status = "Pane maximised · Ctrl+W = restores";
    }

    private void RestorePaneLayout() {
        if (_savedPaneLayout is not { } saved) return;
        var rows = MainSplitGrid.RowDefinitions;
        var columns = DiffSplitGrid.ColumnDefinitions;
        rows[0].Height = saved.Top;
        rows[1].Height = saved.Splitter;
        rows[2].Height = saved.Bottom;
        columns[0].MinWidth = saved.DiffMinWidth;
        columns[0].Width = saved.DiffColumn;
        columns[1].Width = saved.ColumnSplitter;
        columns[2].MinWidth = saved.FilesMinWidth;
        columns[2].Width = saved.FilesColumn;
        _savedPaneLayout = null;
        SyncHiddenPaneContent();
    }

    /// <summary>Content in a zero-sized row or column is hidden, so it neither overflows nor takes focus or clicks.</summary>
    private void SyncHiddenPaneContent() {
        foreach (var child in MainSplitGrid.Children)
            child.IsVisible = MainSplitGrid.RowDefinitions[Grid.GetRow(child)].Height.Value > 0 || Grid.GetRowSpan(child) > 1;
        foreach (var child in DiffSplitGrid.Children)
            child.IsVisible = Grid.GetColumnSpan(child) > 1 || DiffSplitGrid.ColumnDefinitions[Grid.GetColumn(child)].Width.Value > 0;
    }

    /// <summary>The layout to persist: the normal one, even while a pane is maximised.</summary>
    private (double? HistoryPaneRatio, double? FileListWidth)? SavedNormalLayout {
        get {
            if (_savedPaneLayout is not { } saved) return null;
            var ratio = saved.Top.IsStar && saved.Bottom.IsStar && saved.Top.Value + saved.Bottom.Value > 0
                ? saved.Top.Value / (saved.Top.Value + saved.Bottom.Value)
                : (double?)null;
            return (ratio, saved.FilesColumn.IsAbsolute ? saved.FilesColumn.Value : null);
        }
    }
}
