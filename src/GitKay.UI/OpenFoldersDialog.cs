using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace GitKay.UI;

/// <summary>
/// Asks for the two folders to compare in one dialog, rather than two pickers in a row with no way back from the
/// first. Either path can be typed or browsed for, and browsing for the second starts where the first one is, since
/// folders being compared almost always sit beside each other.
/// </summary>
public sealed class OpenFoldersDialog : Window {
    private readonly TextBox _left;
    private readonly TextBox _right;
    private readonly TextBlock _problem;
    private (string Left, string Right)? _chosen;

    private OpenFoldersDialog(string? startingLeft) {
        Title = "Compare two folders";
        Icon = AppIcon.Window;
        Width = 620;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this[!BackgroundProperty] = this.GetResourceObservable("GitKaySurfaceBrush").ToBinding();

        TextBlock Label(string text, double size, string brush, FontWeight weight = FontWeight.Normal) {
            var block = new TextBlock { Text = text, FontSize = size, FontWeight = weight, TextWrapping = TextWrapping.Wrap };
            block[!TextBlock.ForegroundProperty] = this.GetResourceObservable(brush).ToBinding();
            return block;
        }

        _left = new TextBox { Watermark = "The folder on the left", Text = startingLeft ?? "" };
        _right = new TextBox { Watermark = "The folder on the right" };
        _problem = new TextBlock { FontSize = 11, IsVisible = false, TextWrapping = TextWrapping.Wrap };
        _problem[!TextBlock.ForegroundProperty] = this.GetResourceObservable("GitKayRemovedAccentBrush").ToBinding();

        var panel = new StackPanel { Margin = new Thickness(20, 16), Spacing = 10 };
        panel.Children.Add(Label("Compare two folders", 15, "GitKayTextBrush", FontWeight.SemiBold));
        panel.Children.Add(Label("No repository is involved; the files are compared as they are on disk.", 12, "GitKaySecondaryTextBrush"));
        panel.Children.Add(Row(Label("Left", 12, "GitKayMutedTextBrush"), _left, "left"));
        panel.Children.Add(Row(Label("Right", 12, "GitKayMutedTextBrush"), _right, "right"));
        panel.Children.Add(_problem);

        var buttons = new StackPanel {
            Orientation = Orientation.Horizontal, Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0),
        };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(14, 4), IsCancel = true };
        cancel.Click += (_, _) => Close();
        var compare = new Button { Content = "Compare", Padding = new Thickness(14, 4), IsDefault = true };
        compare.Click += (_, _) => Accept();
        buttons.Children.Add(cancel);
        buttons.Children.Add(compare);
        panel.Children.Add(buttons);
        Content = panel;
    }

    private Control Row(Control label, TextBox box, string which) {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("48,*,Auto"), ColumnSpacing = 8 };
        label.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(label, 0);
        Grid.SetColumn(box, 1);
        var browse = new Button { Content = "Browse…", Padding = new Thickness(12, 4) };
        Grid.SetColumn(browse, 2);
        browse.Click += async (_, _) => await BrowseInto(box, which);
        grid.Children.Add(label);
        grid.Children.Add(box);
        grid.Children.Add(browse);
        return grid;
    }

    /// <summary>Browsing starts beside whichever folder is already chosen, so the second pick is one click away.</summary>
    private async Task BrowseInto(TextBox box, string which) {
        var suggested = FirstExisting(box.Text, _left.Text, _right.Text);
        var options = new FolderPickerOpenOptions { Title = $"Compare: the folder on the {which}", AllowMultiple = false };
        if (suggested != null) {
            try { options.SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(new Uri(suggested)); }
            catch (Exception) { /* A folder that cannot be resolved just means the picker opens where it likes. */ }
        }

        var folders = await StorageProvider.OpenFolderPickerAsync(options);
        if (folders.Count > 0 && StorageProviderExtensions.TryGetLocalPath(folders[0]) is { } path) box.Text = path;
    }

    /// <summary>The first of these that is a folder, or its parent when it is a folder's sibling.</summary>
    private static string? FirstExisting(params string?[] candidates) {
        foreach (var candidate in candidates) {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            if (Directory.Exists(candidate)) return candidate;
            var parent = Path.GetDirectoryName(candidate);
            if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent)) return parent;
        }
        return null;
    }

    private void Accept() {
        var left = (_left.Text ?? "").Trim();
        var right = (_right.Text ?? "").Trim();
        if (Problem(left, right) is { } problem) {
            _problem.Text = problem;
            _problem.IsVisible = true;
            return;
        }

        _chosen = (Path.GetFullPath(left), Path.GetFullPath(right));
        Close();
    }

    /// <summary>What is wrong with this pair, or null when there is nothing to say.</summary>
    internal static string? Problem(string left, string right) {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return "Choose both folders.";
        if (!Directory.Exists(left)) return $"Not a folder: {left}";
        if (!Directory.Exists(right)) return $"Not a folder: {right}";
        if (string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.Ordinal))
            return "These are the same folder; nothing would differ.";
        return null;
    }

    /// <summary>The two folders to compare, or null when the dialog was cancelled.</summary>
    public static async Task<(string Left, string Right)?> ShowAsync(Window owner, string? startingLeft) {
        var dialog = new OpenFoldersDialog(startingLeft);
        await dialog.ShowDialog(owner);
        return dialog._chosen;
    }
}
