using System.Linq;
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace GitKay.UI;

public static class FatalErrorPresenter {
    private static IClassicDesktopStyleApplicationLifetime? _desktopLifetime;
    private static int _handlersAttached;
    private static int _fatalDialogShown;

    public static void Initialize(IClassicDesktopStyleApplicationLifetime desktopLifetime) {
        _desktopLifetime = desktopLifetime;

        if (Interlocked.Exchange(ref _handlersAttached, 1) != 0) {
            return;
        }

        Dispatcher.UIThread.UnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    public static void ShowStartupFailure(IClassicDesktopStyleApplicationLifetime desktopLifetime, Exception exception) {
        _desktopLifetime = desktopLifetime;

        if (Interlocked.Exchange(ref _fatalDialogShown, 1) != 0) {
            return;
        }

        var dialog = new FatalErrorDialog(
            "GitKay could not start",
            BuildDetails("GitKay failed while starting.", exception));

        Console.Error.WriteLine(BuildDetails("GitKay failed while starting.", exception));
        PersistCrashDetails("GitKay failed while starting.", exception);
        dialog.Closed += (_, _) => desktopLifetime.Shutdown(1);
        desktopLifetime.MainWindow = dialog;
        // Startup runs after the lifetime has started, when assigning MainWindow no longer shows it.
        dialog.Show();
    }

    public static string BuildDetails(string heading, Exception exception) {
        var builder = new StringBuilder();
        builder.AppendLine(heading);
        builder.AppendLine();
        builder.AppendLine(exception.GetType().FullName);
        builder.AppendLine(exception.Message);
        builder.AppendLine();
        builder.AppendLine(exception.ToString());
        builder.AppendLine();
        builder.AppendLine($"Logs and crash reports: {DiagnosticsLog.LogDirectory}");
        return builder.ToString();
    }

    private static void OnDispatcherUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e) {
        e.Handled = true;
        ReportFatalError("GitKay hit an unrecoverable UI error.", e.Exception);
    }

    private static void OnAppDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e) {
        var exception = e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString() ?? "Unknown fatal error");
        ReportFatalError("GitKay hit an unrecoverable background error.", exception);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e) {
        e.SetObserved();
        if (IsIgnorablePlatformError(e.Exception)) {
            // Avalonia's Linux integration fires D-Bus calls (accessibility, input method, portals) without
            // awaiting them; on sessions where a service isn't running they fail harmlessly.
            System.Diagnostics.Trace.WriteLine($"[platform] ignored unobserved D-Bus error: {e.Exception.GetBaseException().Message}");
            return;
        }

        ReportFatalError("GitKay hit an unrecoverable async error.", e.Exception);
    }

    /// <summary>True when every inner exception is a D-Bus protocol error from the desktop platform layer.</summary>
    internal static bool IsIgnorablePlatformError(AggregateException exception) {
        var inner = exception.Flatten().InnerExceptions;
        return inner.Count > 0
            && inner.All(error => error.GetType().FullName?.StartsWith("Tmds.DBus", StringComparison.Ordinal) == true);
    }

    private static void ReportFatalError(string heading, Exception exception) {
        PersistCrashDetails(heading, exception);

        if (Interlocked.Exchange(ref _fatalDialogShown, 1) != 0) {
            return;
        }

        var desktopLifetime = _desktopLifetime;
        if (desktopLifetime == null) {
            WriteFallbackError(heading, exception);
            return;
        }

        var details = BuildDetails(heading, exception);

        try {
            Dispatcher.UIThread.Post(() => _ = ShowFatalDialogAsync(desktopLifetime, heading, details));
        }
        catch {
            WriteFallbackError(heading, exception);
        }
    }

    private static async Task ShowFatalDialogAsync(IClassicDesktopStyleApplicationLifetime desktopLifetime, string heading, string details) {
        try {
            var dialog = new FatalErrorDialog(heading, details);
            dialog.Closed += (_, _) => desktopLifetime.Shutdown(1);

            var owner = desktopLifetime.MainWindow;
            if (owner != null) {
                await dialog.ShowDialog(owner);
            }
            else {
                desktopLifetime.MainWindow = dialog;
                dialog.Show();
            }
        }
        catch (Exception ex) {
            WriteFallbackError("GitKay could not display the fatal error dialog.", ex);
        }
    }

    private static void PersistCrashDetails(string heading, Exception exception) {
        var path = DiagnosticsLog.WriteReport("crash", BuildDetails(heading, exception));
        if (path != null) Trace.WriteLine($"[crash] report written to {path}");
    }

    private static void WriteFallbackError(string heading, Exception exception) {
        try {
            Console.Error.WriteLine(BuildDetails(heading, exception));
        }
        catch {
        }
    }
}
