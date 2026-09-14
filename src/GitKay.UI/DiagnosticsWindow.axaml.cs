using System;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace GitKay.UI;

/// <summary>A non-modal, live view of Axial fibers, Elmish messages, UI-thread health and the log.</summary>
public partial class DiagnosticsWindow : Window {
    private readonly DiagnosticsProjection _projection = new();
    private readonly DispatcherTimer _timer;

    public DiagnosticsWindow() {
        InitializeComponent();
        Icon = AppIcon.Window;
        DataContext = _projection;
        _projection.Refresh(force: true);
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => _projection.Refresh());
        _timer.Start();
        Closed += (_, _) => _timer.Stop();

        // Follow the log's end unless the reader has scrolled up.
        _projection.PropertyChanged += (_, e) => {
            if (e.PropertyName != nameof(DiagnosticsProjection.LogText)) return;
            var atEnd = LogScroll.Offset.Y >= LogScroll.Extent.Height - LogScroll.Viewport.Height - 24;
            if (atEnd) Dispatcher.UIThread.Post(() => LogScroll.ScrollToEnd(), DispatcherPriority.Background);
        };
    }

    private void OnTogglePause(object? sender, RoutedEventArgs e) => _projection.IsPaused = !_projection.IsPaused;

    private async void OnCopySnapshot(object? sender, RoutedEventArgs e) {
        if (Clipboard is { } clipboard) await clipboard.SetTextAsync(_projection.BuildSnapshot());
    }

    private void OnWriteReport(object? sender, RoutedEventArgs e) {
        var path = DiagnosticsLog.WriteReport("snapshot", _projection.BuildSnapshot());
        if (path != null) Trace.WriteLine($"[diagnostics] snapshot written to {path}");
    }

    private void OnOpenLogs(object? sender, RoutedEventArgs e) {
        try {
            var opener = OperatingSystem.IsWindows() ? "explorer.exe" : OperatingSystem.IsMacOS() ? "open" : "xdg-open";
            Process.Start(new ProcessStartInfo(opener) { ArgumentList = { DiagnosticsLog.LogDirectory }, UseShellExecute = false });
        }
        catch (Exception exception) {
            Trace.WriteLine($"[diagnostics] could not open {DiagnosticsLog.LogDirectory}: {exception.Message}");
        }
    }
}
