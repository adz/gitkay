using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;

namespace GitKay.UI;

/// <summary>Selectable, copyable details of one fiber from the diagnostics window.</summary>
public sealed class FiberDetailsWindow : Window {
    public FiberDetailsWindow(string text) {
        Title = "Flow details";
        Icon = AppIcon.Window;
        Width = 640;
        Height = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this[!BackgroundProperty] = this.GetResourceObservable("GitKaySurfaceBrush").ToBinding();

        var body = new SelectableTextBlock { Text = text, FontFamily = FontStacks.Mono, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12) };
        body[!TextBlock.ForegroundProperty] = this.GetResourceObservable("GitKayTextBrush").ToBinding();
        var copy = new Button { Content = "Copy", Padding = new Thickness(14, 4) };
        copy.Click += async (_, _) => { if (Clipboard is { } clipboard) await clipboard.SetTextAsync(text); };
        var close = new Button { Content = "Close", Padding = new Thickness(14, 4), IsCancel = true, IsDefault = true };
        close.Click += (_, _) => Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12, 0, 12, 12) };
        buttons.Children.Add(copy);
        buttons.Children.Add(close);

        var layout = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        layout.Children.Add(buttons);
        layout.Children.Add(new ScrollViewer { Content = body });
        Content = layout;
    }
}
