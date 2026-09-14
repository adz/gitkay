using Avalonia.Controls;
using Avalonia.Interactivity;

namespace GitKay.UI;

public partial class SettingsWindow : Window {
    public SettingsWindow() {
        InitializeComponent();
        Icon = AppIcon.Window;
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) {
        Close();
    }
}
