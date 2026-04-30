using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace GitKay.UI;

public partial class MainWindow : Window
{
    private MainProjection? _projection;
    private Control? _lastDiffPaneFocus;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
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
        if (e.PropertyName != nameof(MainProjection.SelectedCommit)
            && e.PropertyName != nameof(MainProjection.Commits)
            && e.PropertyName != nameof(MainProjection.SelectedSearchResult)
            && e.PropertyName != nameof(MainProjection.SearchResults)
            && e.PropertyName != nameof(MainProjection.SelectedDiffFile)
            && e.PropertyName != nameof(MainProjection.SelectedDiffRow)
            && e.PropertyName != nameof(MainProjection.SelectedDiffFiles))
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

            if (projection.SelectedSearchResult != null)
            {
                SearchResultsListBox.ScrollIntoView(projection.SelectedSearchResult);
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
        });
    }

    private void OnDiffRowsListBoxGotFocus(object? sender, FocusChangedEventArgs e)
    {
        _lastDiffPaneFocus = DiffRowsListBox;
    }

    private void OnDiffFilesListBoxGotFocus(object? sender, FocusChangedEventArgs e)
    {
        _lastDiffPaneFocus = DiffFilesListBox;
    }

    private void OnCommitListBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Right && e.KeyModifiers == KeyModifiers.None)
        {
            FocusDiffPane();
            e.Handled = true;
        }
    }

    private void OnDiffListBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Left && e.KeyModifiers == KeyModifiers.None)
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

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        if (DataContext is MainProjection projection)
        {
            projection.LogFirstPaint();
        }
    }
}
