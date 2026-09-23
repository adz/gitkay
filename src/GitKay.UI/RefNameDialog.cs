using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace GitKay.UI;

public sealed class RefNameDialog : Window {
    private RefNameDialog(string title) {
        Title = title;
        Icon = AppIcon.Window;
        Width = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this[!BackgroundProperty] = this.GetResourceObservable("GitKaySurfaceBrush").ToBinding();

        var name = new TextBox { PlaceholderText = "Name" };
        var create = new Button { Content = title, IsDefault = true, IsEnabled = false, Padding = new Thickness(14, 4) };
        name.TextChanged += (_, _) => create.IsEnabled = !string.IsNullOrWhiteSpace(name.Text);
        create.Click += (_, _) => Close(name.Text?.Trim());
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(14, 4) };
        cancel.Click += (_, _) => Close(null);
        Content = new StackPanel {
            Margin = new Thickness(20, 16), Spacing = 12,
            Children = {
                new TextBlock { Text = title + " at the selected commit" },
                name,
                new StackPanel {
                    Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8,
                    Children = { cancel, create },
                },
            },
        };
        Opened += (_, _) => name.Focus();
    }

    public static Task<string?> ShowAsync(Window owner, string title) => new RefNameDialog(title).ShowDialog<string?>(owner);
}
