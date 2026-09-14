using System;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Axial.Elmish;

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

    /// <summary>Raised with a command's name after the user cancels it here.</summary>
    public event Action<string>? CommandCancelled;

    private void ShowDetails(object? row) {
        var text = row switch {
            SettledFiberRow { Fiber: { } fiber } => DiagnosticsProjection.DescribeSettled(fiber),
            RunningFiberRow { Dump: { } dump } => DiagnosticsProjection.DescribeRunning(dump),
            _ => null,
        };
        if (text != null) new FiberDetailsWindow(text).Show(this);
    }

    private void OnInfoClick(object? sender, RoutedEventArgs e) {
        ShowDetails((sender as Control)?.DataContext);
        e.Handled = true;
    }

    private void OnRowDoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e) => ShowDetails((e.Source as Control)?.DataContext);

    private void OnRunningRowContextRequested(object? sender, ContextRequestedEventArgs e) {
        if (sender is not Control { DataContext: RunningFiberRow row } control) return;
        var menu = new ContextMenu();
        var running = CmdDiagnostics.RunningCommands();
        var cancel = new MenuItem { Header = $"Cancel “{row.Name}”", IsEnabled = Array.IndexOf(running, row.Name) >= 0 };
        if (!cancel.IsEnabled) ToolTip.SetTip(cancel, "Only whole commands can be cancelled; cancel its parent instead");
        cancel.Click += (_, _) => {
            var count = CmdDiagnostics.Cancel(row.Name);
            Trace.WriteLine($"[diagnostics] cancelled {count} command(s) named {row.Name}");
            CommandCancelled?.Invoke(row.Name);
            _projection.Refresh(force: true);
        };
        menu.Items.Add(cancel);
        var details = new MenuItem { Header = "Details…" };
        details.Click += (_, _) => ShowDetails(row);
        menu.Items.Add(details);
        menu.Open(control);
        e.Handled = true;
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
