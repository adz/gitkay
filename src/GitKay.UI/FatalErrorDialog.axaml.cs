using Avalonia.Controls;
using Avalonia.Interactivity;

namespace GitKay.UI;

public partial class FatalErrorDialog : Window
{
    public FatalErrorDialog()
    {
        InitializeComponent();
    }

    public FatalErrorDialog(string message, string details)
        : this()
    {
        MessageText.Text = message;
        DetailsText.Text = details;
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
