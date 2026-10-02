using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace GitKay.UI;

/// <summary>
/// Tells the user a persisted file (settings.json or ui-state.json) could not be read and is being left untouched,
/// and offers to open it so they can see what is wrong. Not fatal: the app proceeds with the defaults.
/// </summary>
public sealed class SettingsLoadFailureDialog : Window {
    public sealed record Failure(string Label, string Path, string Reason);

    public SettingsLoadFailureDialog(IReadOnlyList<Failure> failures) {
        Title = "Settings not loaded";
        Icon = AppIcon.Window;
        Width = 600;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this[!BackgroundProperty] = this.GetResourceObservable("GitKaySurfaceBrush").ToBinding();

        TextBlock Text(string text, double size, string brush, FontWeight weight = FontWeight.Normal) {
            var block = new TextBlock { Text = text, FontSize = size, FontWeight = weight, TextWrapping = TextWrapping.Wrap };
            block[!TextBlock.ForegroundProperty] = this.GetResourceObservable(brush).ToBinding();
            return block;
        }

        var panel = new StackPanel { Margin = new Thickness(24, 20), Spacing = 10 };

        panel.Children.Add(Text(
            failures.Count == 1
                ? $"GitKay couldn't read your {failures[0].Label} file, so it's using the defaults. The file was left as it is."
                : "GitKay couldn't read some of your saved files, so it's using the defaults. The files were left as they are.",
            13, "GitKayTextBrush"));

        foreach (var failure in failures) {
            var linkText = new TextBlock {
                Text = failure.Path,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                TextDecorations = TextDecorations.Underline,
            };
            linkText[!TextBlock.ForegroundProperty] = this.GetResourceObservable("GitKayAccentBrush").ToBinding();

            var link = new Button {
                Content = linkText,
                HorizontalAlignment = HorizontalAlignment.Left,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            link.Click += (_, _) => OpenPath(failure.Path);

            var row = new StackPanel { Spacing = 2 };
            row.Children.Add(Text($"{failure.Label} — {failure.Reason}", 11, "GitKayMutedTextBrush"));
            row.Children.Add(link);
            panel.Children.Add(row);
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };

        var showFolder = new Button { Content = "Show in folder" };
        showFolder.Click += (_, _) => {
            var directory = Path.GetDirectoryName(failures[0].Path);
            if (!string.IsNullOrEmpty(directory)) OpenPath(directory);
        };
        buttons.Children.Add(showFolder);

        var close = new Button { Content = "Use defaults", IsDefault = true, IsCancel = true };
        close.Click += (_, _) => Close();
        buttons.Children.Add(close);

        panel.Children.Add(buttons);
        Content = panel;
    }

    private static void OpenPath(string path) {
        try {
            ExternalTools.OpenInDefaultApp(path);
        }
        catch (Exception exception) {
            System.Diagnostics.Trace.WriteLine($"[settings] could not open {path}: {exception.Message}");
        }
    }

    /// <summary>Shows the dialog for these failures and completes when it is closed.</summary>
    public static Task ShowAsync(Window owner, IReadOnlyList<Failure> failures) =>
        new SettingsLoadFailureDialog(failures).ShowDialog(owner);
}
