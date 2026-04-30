using System;
using System.ComponentModel;
using Avalonia.Media;
using Avalonia.Controls;
using Avalonia.Threading;

namespace GitKay.UI;

public partial class MainWindow : Window
{
    private MainProjection? _projection;

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
        if (e.PropertyName != nameof(MainProjection.SelectedDiffFile) && e.PropertyName != nameof(MainProjection.SelectedDiffRow))
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            DiffRowsListBox.Focus();
        });
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
