using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using Avalonia.Threading;
using Axial.Elmish;

using Timing = GitKay.Core.Timing;

namespace GitKay.UI;

/// <summary>
/// Always-on diagnostics: a daily log file, crash and hang reports carrying the recent Elmish messages and a dump of
/// running Axial fibers, and a watchdog that notices when the UI thread stops responding.
/// </summary>
/// <remarks>
/// Logs live in %LOCALAPPDATA%\GitKay\logs on Windows, ~/Library/Logs/GitKay on macOS, and
/// $XDG_STATE_HOME/gitkay/logs (default ~/.local/state/gitkay/logs) elsewhere. Files older than two weeks are removed.
/// </remarks>
public static class DiagnosticsLog {
    private const int RetentionDays = 14;
    private static readonly TimeSpan HangThreshold = TimeSpan.FromSeconds(4);
    private static int _initialized;
    private static long _lastUiHeartbeat = Timing.mark();

    public static string LogDirectory { get; } = ResolveLogDirectory();
    public static DateTimeOffset StartedAt { get; } = Timing.now();

    // ----- Live metrics for the diagnostics window. -----

    private const int TailCapacity = 1000;
    private static readonly object TailGate = new();
    private static readonly System.Collections.Generic.Queue<string> Tail = new();
    private static readonly System.Collections.Generic.Queue<double> RecentLags = new();
    private static long _tailVersion;

    /// <summary>Milliseconds the most recent watchdog ping waited for the UI thread.</summary>
    public static double LastUiLagMs { get; private set; }
    /// <summary>Worst UI-thread wait since start.</summary>
    public static double MaxUiLagMs { get; private set; }
    /// <summary>Stalls longer than the hang threshold since start.</summary>
    public static int HangCount { get; private set; }
    /// <summary>Changes whenever a log line is added.</summary>
    public static long TailVersion => Interlocked.Read(ref _tailVersion);

    public static string[] LogTail() {
        lock (TailGate) return Tail.ToArray();
    }

    /// <summary>UI-thread waits for the last minute of pings, oldest first.</summary>
    public static double[] UiLagHistory() {
        lock (TailGate) return RecentLags.ToArray();
    }

    private static void AppendTail(string line) {
        lock (TailGate) {
            if (Tail.Count >= TailCapacity) Tail.Dequeue();
            Tail.Enqueue(line);
        }
        Interlocked.Increment(ref _tailVersion);
    }

    private static void RecordLag(double ms) {
        LastUiLagMs = ms;
        if (ms > MaxUiLagMs) MaxUiLagMs = ms;
        lock (TailGate) {
            if (RecentLags.Count >= 60) RecentLags.Dequeue();
            RecentLags.Enqueue(ms);
        }
    }

    // ----- What the UI thread is doing, for hang reports. -----
    // A hang report is written from the watchdog thread, and a managed stack of another thread is not available (least of
    // all under NativeAOT). Named scopes around the entry points that can run long stand in for it: whatever scope
    // was entered and never left is where the UI thread is stuck. Only the UI thread writes them.

    private const int BreadcrumbCapacity = 40;
    private static readonly string?[] BreadcrumbText = new string?[BreadcrumbCapacity];
    private static readonly long[] BreadcrumbTicks = new long[BreadcrumbCapacity];
    private static int _breadcrumbNext;
    private static string? _uiScope;
    private static long _uiScopeSince;

    /// <summary>Notes a UI event (a click, a menu opening) so a hang report can say what happened just before it.</summary>
    public static void Breadcrumb(string what) {
        var slot = (Interlocked.Increment(ref _breadcrumbNext) - 1) % BreadcrumbCapacity;
        BreadcrumbTicks[slot] = Stopwatch.GetTimestamp();
        BreadcrumbText[slot] = what;
    }

    private static readonly TimeSpan SlowScope = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Marks the UI thread as inside <paramref name="name"/> until the returned scope is disposed. A scope that took
    /// long enough for the user to notice is logged, so a slow pass shows up in the log before it becomes a hang.
    /// </summary>
    public static UiScope Enter(string name) {
        var scope = new UiScope(name, _uiScope, _uiScopeSince);
        _uiScope = name;
        _uiScopeSince = Stopwatch.GetTimestamp();
        return scope;
    }

    public readonly struct UiScope(string name, string? previous, long previousSince) : IDisposable {
        private readonly long _started = Stopwatch.GetTimestamp();

        public void Dispose() {
            _uiScope = previous;
            _uiScopeSince = previousSince;
            var elapsed = Stopwatch.GetElapsedTime(_started);
            if (elapsed > SlowScope) Trace.WriteLine($"[slow] {name} held the UI thread for {elapsed.TotalMilliseconds:F0}ms");
        }
    }

    /// <summary>The scope the UI thread is in, how long it has been there, and the last UI events, newest last.</summary>
    public static string DescribeUiActivity() {
        var now = Stopwatch.GetTimestamp();
        var builder = new StringBuilder();
        var scope = _uiScope;
        builder.AppendLine(scope == null
            ? "UI thread is not inside a tracked scope."
            : $"UI thread has been inside \"{scope}\" for {Stopwatch.GetElapsedTime(_uiScopeSince, now).TotalSeconds:F1}s.");
        builder.AppendLine("Recent UI events (oldest first):");
        var total = Volatile.Read(ref _breadcrumbNext);
        var first = Math.Max(0, total - BreadcrumbCapacity);
        for (var index = first; index < total; index++) {
            var slot = index % BreadcrumbCapacity;
            if (BreadcrumbText[slot] is { } text)
                builder.AppendLine($"  {Stopwatch.GetElapsedTime(BreadcrumbTicks[slot], now).TotalSeconds,6:F2}s ago  {text}");
        }
        return builder.ToString();
    }

    public static string? CurrentLogFile { get; private set; }

    public static void Initialize(string[] args) {
        if (Interlocked.Exchange(ref _initialized, 1) != 0) return;

        try {
            Directory.CreateDirectory(LogDirectory);
            DeleteOldFiles();
            CurrentLogFile = Path.Combine(LogDirectory, $"gitkay-{Timing.now().ToLocalTime():yyyyMMdd}.log");
            var writer = new StreamWriter(new FileStream(CurrentLogFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
            Trace.Listeners.Add(new TimestampedListener(writer));
            Trace.AutoFlush = true;
        }
        catch (Exception exception) {
            Console.Error.WriteLine($"GitKay could not open its log in {LogDirectory}: {exception.Message}");
        }

        Trace.WriteLine("==================================================================");
        Trace.WriteLine($"GitKay {typeof(DiagnosticsLog).Assembly.GetName().Version} starting, pid {Environment.ProcessId}");
        Trace.WriteLine($"OS {System.Runtime.InteropServices.RuntimeInformation.OSDescription} ({System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}), "
                        + $".NET {Environment.Version}, {(RuntimeFeature.IsDynamicCodeSupported ? "JIT" : "NativeAOT")}");
        Trace.WriteLine($"cwd {Environment.CurrentDirectory}");
        Trace.WriteLine($"args [{string.Join(", ", args.Select(arg => $"\"{arg}\""))}]");

        CmdDiagnostics.SetFailureHandler((name, cause) =>
            // Latest-wins flows are cancelled routinely when a newer request supersedes them.
            Trace.WriteLine(cause.Contains("OperationCanceled", StringComparison.Ordinal)
                ? $"[flow] {name} cancelled"
                : $"[flow] {name} failed:{Environment.NewLine}{cause}"));
    }

    /// <summary>A report of what the app was doing: recent messages, the message still being handled, and live fibers.</summary>
    public static string DescribeState() {
        var builder = new StringBuilder();
        builder.AppendLine("Recent messages (oldest first):");
        builder.AppendLine(GitKay.Core.Diagnostics.describeRecent(40));
        builder.AppendLine();
        builder.AppendLine(DescribeUiActivity());
        builder.AppendLine("Running flows (Axial fibers):");
        try {
            var dump = CmdDiagnostics.Registry.DumpAt(Axial.PlatformService.Clock.live);
            builder.AppendLine(string.IsNullOrWhiteSpace(dump) ? "  (none)" : dump);
        }
        catch (Exception exception) {
            builder.AppendLine($"  (fiber dump failed: {exception.Message})");
        }

        return builder.ToString();
    }

    /// <summary>Writes a crash report next to the logs and returns its path.</summary>
    public static string? WriteReport(string kind, string details) {
        var report = new StringBuilder()
            .AppendLine($"GitKay {kind} report, {Timing.now():O}")
            .AppendLine($"Version {typeof(DiagnosticsLog).Assembly.GetName().Version}, {System.Runtime.InteropServices.RuntimeInformation.OSDescription}, "
                        + $"{(RuntimeFeature.IsDynamicCodeSupported ? "JIT" : "NativeAOT")}")
            .AppendLine($"Log: {CurrentLogFile}")
            .AppendLine()
            .AppendLine(details)
            .AppendLine()
            .AppendLine(DescribeState())
            .ToString();

        Trace.WriteLine($"[{kind}] {details}");
        Trace.WriteLine(DescribeState());
        try {
            Directory.CreateDirectory(LogDirectory);
            var path = Path.Combine(LogDirectory, $"{kind}-{Timing.now().ToLocalTime():yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(path, report);
            return path;
        }
        catch {
            return null;
        }
    }

    /// <summary>
    /// Pings the UI thread every second. When it stays silent past the threshold, writes a hang report once per stall
    /// (recent messages and running flows show what it was stuck on) and logs how long the stall lasted.
    /// </summary>
    public static void StartHangWatchdog() {
        var thread = new Thread(() => {
            var reported = false;
            while (true) {
                Thread.Sleep(1000);
                var postedAt = Timing.mark();
                Dispatcher.UIThread.Post(() => {
                    Interlocked.Exchange(ref _lastUiHeartbeat, Timing.mark());
                    RecordLag(Timing.elapsedFrom(postedAt).TotalMilliseconds);
                }, DispatcherPriority.Send);
                NativeMemory.TrimIfGrown();
                var silence = Timing.elapsedFrom(Interlocked.Read(ref _lastUiHeartbeat));
                if (silence > HangThreshold && !reported) {
                    reported = true;
                    HangCount++;
                    var path = WriteReport("hang", $"The UI thread has not responded for {silence.TotalSeconds:F1}s.");
                    Trace.WriteLine($"[hang] report written to {path}");
                    if (path != null && HangDump.TryWrite(Path.ChangeExtension(path, ".dmp")) is { } dump)
                        Trace.WriteLine($"[hang] thread stacks written to {dump}");
                }
                else if (silence < TimeSpan.FromSeconds(1.5) && reported) {
                    reported = false;
                    Trace.WriteLine("[hang] UI thread responsive again");
                }
            }
        }) { IsBackground = true, Name = "GitKay hang watchdog" };
        thread.Start();
    }

    private static string ResolveLogDirectory() {
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GitKay", "logs");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS())
            return Path.Combine(home, "Library", "Logs", "GitKay");
        var state = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        return Path.Combine(string.IsNullOrWhiteSpace(state) ? Path.Combine(home, ".local", "state") : state, "gitkay", "logs");
    }

    private static void DeleteOldFiles() {
        foreach (var file in Directory.EnumerateFiles(LogDirectory)) {
            try {
                if (File.GetLastWriteTime(file) < Timing.now().ToLocalTime().AddDays(-RetentionDays)) File.Delete(file);
            }
            catch {
            }
        }
    }

    private sealed class TimestampedListener(TextWriter writer) : TextWriterTraceListener(writer) {
        private bool _lineStart = true;

        // Some platform messages arrive padded with NUL characters; keep the log readable as text.
        private static string? Clean(string? message) => message?.Replace("\0", "");

        private readonly StringBuilder _pending = new();

        public override void Write(string? message) {
            message = Clean(message);
            lock (_pending) {
                if (_lineStart) {
                    var prefix = Prefix();
                    base.Write(prefix);
                    _pending.Append(prefix);
                }
                base.Write(message);
                _pending.Append(message);
                _lineStart = false;
            }
        }

        public override void WriteLine(string? message) {
            message = Clean(message);
            lock (_pending) {
                if (_lineStart) {
                    var prefix = Prefix();
                    base.Write(prefix);
                    _pending.Append(prefix);
                }
                base.WriteLine(message);
                _pending.Append(message);
                foreach (var line in _pending.ToString().Split('\n')) AppendTail(line.TrimEnd('\r'));
                _pending.Clear();
                _lineStart = true;
            }
        }

        private static string Prefix() => $"{Timing.now().ToLocalTime():HH:mm:ss.fff} [{Environment.CurrentManagedThreadId,3}] ";
    }
}
