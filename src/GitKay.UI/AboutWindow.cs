using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace GitKay.UI;

/// <summary>The About dialog: version (from the release tag), runtime, platform and where logs live.</summary>
public sealed class AboutWindow : Window {
    /// <summary>The release version stamped from the tag (e.g. 0.2.1), with the commit it was built from.</summary>
    public static (string Version, string? Commit) VersionInfo {
        get {
            var assembly = typeof(AboutWindow).Assembly;
            var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                                ?? assembly.GetName().Version?.ToString() ?? "unknown";
            var plus = informational.IndexOf('+');
            if (plus < 0) return (informational, null);
            var commit = informational[(plus + 1)..];
            return (informational[..plus], commit.Length > 8 ? commit[..8] : commit);
        }
    }

    public AboutWindow() {
        Title = "About GitKay";
        Icon = AppIcon.Window;
        Width = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this[!BackgroundProperty] = this.GetResourceObservable("GitKaySurfaceBrush").ToBinding();

        TextBlock Text(string text, double size, string brush, FontWeight weight = FontWeight.Normal) {
            var block = new TextBlock { Text = text, FontSize = size, FontWeight = weight, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center };
            block[!TextBlock.ForegroundProperty] = this.GetResourceObservable(brush).ToBinding();
            return block;
        }

        var (version, commit) = VersionInfo;
        var details =
            $"{(RuntimeFeature.IsDynamicCodeSupported ? "JIT" : "NativeAOT")} · .NET {Environment.Version} · {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})";

        var panel = new StackPanel { Margin = new Thickness(24, 20), Spacing = 6 };
        using (var stream = typeof(AboutWindow).Assembly.GetManifestResourceStream("GitKay.UI.gitkay.png")) {
            if (stream != null)
                panel.Children.Add(new Image { Source = new Bitmap(stream), Width = 72, Height = 72, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 6) });
        }
        panel.Children.Add(Text("GitKay", 20, "GitKayTextBrush", FontWeight.SemiBold));
        panel.Children.Add(Text(commit == null ? $"Version {version}" : $"Version {version} ({commit})", 13, "GitKayTextBrush"));
        panel.Children.Add(Text("A fast, keyboard-friendly Git history browser in the spirit of gitk.", 12, "GitKaySecondaryTextBrush"));
        panel.Children.Add(Text(details, 11, "GitKayMutedTextBrush"));
        panel.Children.Add(Text($"Logs: {DiagnosticsLog.LogDirectory}", 11, "GitKayMutedTextBrush"));

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) };
        Button Action(string label, Action action) {
            var button = new Button { Content = label, Padding = new Thickness(12, 4) };
            button.Click += (_, _) => action();
            buttons.Children.Add(button);
            return button;
        }

        Action("Releases", () => Open("https://github.com/adz/gitkay/releases"));
        Action("Copy version", async () => {
            if (Clipboard is { } clipboard) await clipboard.SetTextAsync($"GitKay {version}{(commit == null ? "" : $" ({commit})")} · {details}");
        });
        var close = Action("Close", Close);
        close.IsDefault = true;
        close.IsCancel = true;
        panel.Children.Add(buttons);
        Content = panel;
    }

    private static void Open(string url) {
        try {
            var opener = OperatingSystem.IsWindows() ? "explorer.exe" : OperatingSystem.IsMacOS() ? "open" : "xdg-open";
            Process.Start(new ProcessStartInfo(opener) { ArgumentList = { url }, UseShellExecute = false });
        }
        catch (Exception exception) {
            Trace.WriteLine($"[about] could not open {url}: {exception.Message}");
        }
    }
}
