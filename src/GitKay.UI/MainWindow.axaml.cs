using System;
using System.ComponentModel;
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
    private double _activeHistoryColumnResizeStartWidth;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        CommitListBox.AddHandler(InputElement.KeyDownEvent, OnMainListBoxKeyDown, RoutingStrategies.Tunnel);
        DiffRowsListBox.AddHandler(InputElement.KeyDownEvent, OnMainListBoxKeyDown, RoutingStrategies.Tunnel);
        DiffFilesListBox.AddHandler(InputElement.KeyDownEvent, OnMainListBoxKeyDown, RoutingStrategies.Tunnel);
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

        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(_projection, projection))
            {
                return;
            }

            if (projection.SelectedCommit != null)
            {
                CommitListBox.ScrollIntoView(projection.SelectedCommit);
            }

            if (projection.SelectedDiffFile != null)
            {
                DiffFilesListBox.ScrollIntoView(projection.SelectedDiffFile);
            }

            var target = (object?)projection.SelectedDiffRow ?? projection.SelectedDiffFile?.Header;
            if (target != null)
            {
                DiffRowsListBox.ScrollIntoView(target);
            }

            if (!DiffRowsListBox.IsKeyboardFocusWithin && !DiffFilesListBox.IsKeyboardFocusWithin)
            {
                DiffRowsListBox.Focus();
            }
        }, DispatcherPriority.Loaded);
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
        if (sender is not ListBox listBox)
        {
            return;
        }

        if (MainWindowNavigation.TryGetListNavigationDelta(e.Key, e.KeyModifiers, out var delta))
        {
            MainWindowNavigation.TryMoveSelection(listBox, delta);
            e.Handled = true;
            return;
        }

        if ((e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control) || (e.Key == Key.Oem2 && e.KeyModifiers == KeyModifiers.None))
        {
            if (ReferenceEquals(listBox, CommitListBox))
            {
                if (_projection != null)
                {
                    _projection.IsSearchPanelExpanded = true;
                }
                SearchBox.Focus();
                SearchBox.SelectAll();
                e.Handled = true;
                return;
            }

            if (ReferenceEquals(listBox, DiffRowsListBox) || ReferenceEquals(listBox, DiffFilesListBox))
            {
                CommitFindBox.Focus();
                CommitFindBox.SelectAll();
                e.Handled = true;
                return;
            }
        }

        if (ReferenceEquals(listBox, CommitListBox)
            && e.Key == Key.Right
            && e.KeyModifiers == KeyModifiers.None)
        {
            FocusDiffPane();
            e.Handled = true;
            return;
        }

        if ((ReferenceEquals(listBox, DiffRowsListBox) || ReferenceEquals(listBox, DiffFilesListBox))
            && e.Key == Key.Left
            && e.KeyModifiers == KeyModifiers.None)
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
        _activeHistoryColumnResizeStartX = e.GetPosition(HistoryHeaderGrid).X;
        _activeHistoryColumnResizeStartWidth = GetEffectiveColumnWidth(columnIndex, column);

        e.Pointer.Capture(handle);
        e.Handled = true;
    }

    private void OnHistoryColumnResizePointerMoved(object? sender, PointerEventArgs e)
    {
        if (_activeHistoryColumnResizeHandle == null
            || !ReferenceEquals(sender, _activeHistoryColumnResizeHandle)
            || _activeHistoryColumnResizeIndex < 0
            || _activeHistoryColumnResizeIndex >= HistoryHeaderGrid.ColumnDefinitions.Count)
        {
            return;
        }

        var column = HistoryHeaderGrid.ColumnDefinitions[_activeHistoryColumnResizeIndex];
        var delta = e.GetPosition(HistoryHeaderGrid).X - _activeHistoryColumnResizeStartX;
        var minWidth = Math.Max(0d, column.MinWidth);
        var nextWidth = Math.Max(minWidth, _activeHistoryColumnResizeStartWidth + delta);
        column.Width = new GridLength(nextWidth, GridUnitType.Pixel);
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
        _activeHistoryColumnResizeStartWidth = 0d;
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
