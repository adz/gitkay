using Avalonia.Media;
using Avalonia.Controls;

namespace GitKay.UI;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
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
