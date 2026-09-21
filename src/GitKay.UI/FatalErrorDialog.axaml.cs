using System;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;

namespace GitKay.UI;

public partial class FatalErrorDialog : Window {
    public FatalErrorDialog() {
        InitializeComponent();
    }

    public FatalErrorDialog(string message, string details)
        : this() {
        MessageText.Text = message;
        DetailsText.Text = details;
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) {
        Close();
    }

    /// <summary>
    /// Puts the whole report on the clipboard. A crash is worth reporting, and retyping a stack trace out of a
    /// screenshot is not a reasonable thing to ask of anyone.
    /// </summary>
    private async void OnCopyDetailsClick(object? sender, RoutedEventArgs e) {
        if (Clipboard is not { } clipboard) return;
        try {
            await clipboard.SetTextAsync(DetailsText.Text ?? "");
            CopyDetailsButton.Content = "Copied";
        }
        catch (Exception) {
            CopyDetailsButton.Content = "Could not copy";
        }
    }
}
