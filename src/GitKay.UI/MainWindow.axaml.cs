using System;
using System.ComponentModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

namespace GitKay.UI;

public partial class MainWindow : Window
{
    private MainProjection? _projection;
    private Control? _lastDiffPaneFocus;
    private Control? _activeHistoryColumnResizeHandle;
    private int _activeHistoryColumnResizeIndex = -1;
    private double _activeHistoryColumnResizeStartX;
    private double[]? _activeHistoryColumnResizeStartWidths;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        CommitListBox.AddHandler(InputElement.KeyDownEvent, OnMainListBoxKeyDown, RoutingStrategies.Tunnel);
        CommitScrollViewer.AddHandler(InputElement.PointerPressedEvent, OnCommitScrollPointerPressed, RoutingStrategies.Tunnel);
        DiffRowsListBox.AddHandler(InputElement.KeyDownEvent, OnMainListBoxKeyDown, RoutingStrategies.Tunnel);
        DiffFilesListBox.AddHandler(InputElement.KeyDownEvent, OnMainListBoxKeyDown, RoutingStrategies.Tunnel);
        HistoryHeaderGrid.LayoutUpdated += (_, _) => SyncCommitColumnWidths();
    }

    private async void OnSettingsMenuItemClick(object? sender, RoutedEventArgs e)
    {
        var settingsWindow = new SettingsWindow
        {
            DataContext = DataContext,
        };

        await settingsWindow.ShowDialog(this);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_projection != null)
        {
            _projection.PropertyChanged -= OnProjectionPropertyChanged;
        }

        _projection = DataContext as MainProjection;

        if (_projection != null)
        {
            _projection.PropertyChanged += OnProjectionPropertyChanged;
        }
    }

    private void OnProjectionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainProjection.IsSearchPanelExpanded))
        {
            var currentProjection = _projection;
            if (currentProjection == null || !currentProjection.IsSearchPanelExpanded)
            {
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (!ReferenceEquals(_projection, currentProjection) || !currentProjection.IsSearchPanelExpanded)
                {
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
            && e.PropertyName != nameof(MainProjection.IsSearchPanelExpanded))
        {
            return;
        }

        var projection = _projection;
        if (projection == null)
        {
            return;
        }

        var shouldScrollCommit = e.PropertyName is nameof(MainProjection.SelectedCommit) or nameof(MainProjection.Commits);
        var shouldScrollFile = e.PropertyName == nameof(MainProjection.SelectedDiffFile);
        var shouldScrollDiff = e.PropertyName is nameof(MainProjection.SelectedDiffFile) or nameof(MainProjection.SelectedDiffRow);

        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(_projection, projection))
            {
                return;
            }

            if (shouldScrollCommit && projection.SelectedCommit != null)
            {
                CommitListBox.ScrollIntoView(projection.SelectedCommit);
            }

            if (shouldScrollFile && projection.SelectedDiffFile != null)
            {
                DiffFilesListBox.ScrollIntoView(projection.SelectedDiffFile);
            }

            var target = projection.SelectedDiffRow ?? projection.SelectedDiffFile?.Header;
            if (shouldScrollDiff && target != null)
            {
                DiffRowsListBox.ScrollIntoView(target);
            }

            if (!CommitListBox.IsKeyboardFocusWithin
                && !DiffRowsListBox.IsKeyboardFocusWithin
                && !DiffFilesListBox.IsKeyboardFocusWithin)
            {
                DiffRowsListBox.Focus();
            }
        }, DispatcherPriority.Loaded);
    }

    private void OnCommitScrollPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        CommitListBox.Focus();
        CommitListBox.SelectAt(e.GetPosition(CommitListBox).Y);
    }

    private void OnDiffRowsListBoxGotFocus(object? sender, FocusChangedEventArgs e)
    {
        _lastDiffPaneFocus = DiffRowsListBox;
    }

    private void OnDiffFilesListBoxGotFocus(object? sender, FocusChangedEventArgs e)
    {
        _lastDiffPaneFocus = DiffFilesListBox;
    }

    private void OnMainListBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not ListBox && sender is not DiffSurfaceControl && sender is not CommitSurfaceControl)
            return;

        if (MainWindowNavigation.TryGetListNavigationDelta(e.Key, e.KeyModifiers, out var delta))
        {
            if (sender is ListBox listBox)
                MainWindowNavigation.TryMoveSelection(listBox, delta);
            else if (sender is DiffSurfaceControl diffSurface)
                diffSurface.MoveSelection(delta);
            else
                ((CommitSurfaceControl)sender).MoveSelection(delta);
            e.Handled = true;
            return;
        }

        if ((e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control) || (e.Key == Key.Oem2 && e.KeyModifiers == KeyModifiers.None))
        {
            if (ReferenceEquals(sender, CommitListBox))
            {
                if (_projection != null)
                    _projection.IsSearchPanelExpanded = true;
                SearchBox.Focus();
                SearchBox.SelectAll();
                e.Handled = true;
                return;
            }

            if (ReferenceEquals(sender, DiffRowsListBox) || ReferenceEquals(sender, DiffFilesListBox))
            {
                CommitFindBox.Focus();
                CommitFindBox.SelectAll();
                e.Handled = true;
                return;
            }
        }

        if (ReferenceEquals(sender, CommitListBox) && e.Key == Key.Right && e.KeyModifiers == KeyModifiers.None)
        {
            FocusDiffPane();
            e.Handled = true;
            return;
        }

        if ((ReferenceEquals(sender, DiffRowsListBox) || ReferenceEquals(sender, DiffFilesListBox))
            && e.Key == Key.Left && e.KeyModifiers == KeyModifiers.None)
        {
            CommitListBox.Focus();
            e.Handled = true;
        }
    }

    private void FocusDiffPane()
    {
        var target = _lastDiffPaneFocus;

        if (target == null || !target.IsVisible)
        {
            target = DiffRowsListBox;
        }

        target.Focus();
    }

    private void OnHistoryColumnResizePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control handle
            || handle.Tag is not string tag
            || !int.TryParse(tag, out var columnIndex)
            || columnIndex < 0
            || columnIndex >= HistoryHeaderGrid.ColumnDefinitions.Count)
        {
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

    private void OnHistoryColumnResizePointerMoved(object? sender, PointerEventArgs e)
    {
        if (_activeHistoryColumnResizeHandle == null
            || !ReferenceEquals(sender, _activeHistoryColumnResizeHandle)
            || _activeHistoryColumnResizeIndex < 0
            || _activeHistoryColumnResizeIndex >= HistoryHeaderGrid.ColumnDefinitions.Count - 1)
        {
            return;
        }

        var startWidths = _activeHistoryColumnResizeStartWidths;
        if (startWidths == null || startWidths.Length != HistoryHeaderGrid.ColumnDefinitions.Count)
            return;

        var boundary = _activeHistoryColumnResizeIndex;
        var requestedDelta = e.GetPosition(HistoryHeaderGrid).X - _activeHistoryColumnResizeStartX;
        var widths = (double[])startWidths.Clone();

        if (requestedDelta > 0)
        {
            var available = 0d;
            for (var index = boundary + 1; index < widths.Length; index++)
                available += Math.Max(0, widths[index] - HistoryHeaderGrid.ColumnDefinitions[index].MinWidth);
            var applied = Math.Min(requestedDelta, available);
            widths[boundary] += applied;
            ShrinkColumns(widths, boundary + 1, widths.Length, 1, applied);
        }
        else if (requestedDelta < 0)
        {
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

    private void OnHistoryColumnResizePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (ReferenceEquals(sender, _activeHistoryColumnResizeHandle))
        {
            e.Pointer.Capture(null);
            ClearHistoryColumnResize();
            e.Handled = true;
        }
    }

    private void OnHistoryColumnResizePointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (ReferenceEquals(sender, _activeHistoryColumnResizeHandle))
        {
            ClearHistoryColumnResize();
        }
    }

    private void ShrinkColumns(double[] widths, int start, int stop, int step, double amount)
    {
        for (var index = start; index != stop && amount > 0; index += step)
        {
            var minimum = Math.Max(0, HistoryHeaderGrid.ColumnDefinitions[index].MinWidth);
            var reduction = Math.Min(amount, Math.Max(0, widths[index] - minimum));
            widths[index] -= reduction;
            amount -= reduction;
        }
    }

    private void SyncCommitColumnWidths()
    {
        if (HistoryHeaderGrid.ColumnDefinitions.Count < 5)
            return;

        CommitListBox.GraphWidth = HistoryHeaderGrid.ColumnDefinitions[0].ActualWidth;
        CommitListBox.SubjectWidth = HistoryHeaderGrid.ColumnDefinitions[1].ActualWidth;
        CommitListBox.HashWidth = HistoryHeaderGrid.ColumnDefinitions[2].ActualWidth;
        CommitListBox.AuthorWidth = HistoryHeaderGrid.ColumnDefinitions[3].ActualWidth;
        CommitListBox.DateWidth = HistoryHeaderGrid.ColumnDefinitions[4].ActualWidth;
    }

    private double GetEffectiveColumnWidth(int columnIndex, ColumnDefinition column)
    {
        if (column.ActualWidth > 0d)
        {
            return column.ActualWidth;
        }

        return column.Width.IsAbsolute ? column.Width.Value : HistoryHeaderGrid.Bounds.Width / HistoryHeaderGrid.ColumnDefinitions.Count;
    }

    private void ClearHistoryColumnResize()
    {
        _activeHistoryColumnResizeHandle = null;
        _activeHistoryColumnResizeIndex = -1;
        _activeHistoryColumnResizeStartX = 0d;
        _activeHistoryColumnResizeStartWidths = null;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        if (DataContext is MainProjection projection)
        {
            projection.LogFirstPaint();
        }
    }
}
