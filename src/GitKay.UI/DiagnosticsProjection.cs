using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using Avalonia.Collections;
using Axial;
using Axial.Elmish;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.FSharp.Core;

namespace GitKay.UI;

public sealed record RunningFiberRow(string Name, string Id, string Age, string Annotations, Avalonia.Thickness Indent, bool IsSlow);

public sealed record SettledFiberRow(string Time, string Name, string Duration, string Status, bool IsFailed, bool IsInterrupted, bool IsSlow, string Detail);

public sealed record FlowStatsRow(string Name, string Count, string Failed, string Interrupted, string Average, string Max, bool HasFailures);

public sealed record FailureRow(string Time, string Name, string Cause);

public sealed record MessageRow(string Time, string Name, string Duration, bool IsSlow);

/// <summary>Live snapshot of Axial fibers, Elmish messages, UI-thread health and the log for the diagnostics window.</summary>
public sealed partial class DiagnosticsProjection : ObservableObject {
    private const double SlowFlowMs = 500;
    private long _settledSignature = -1;
    private long _messagesSignature = -1;
    private long _logVersion = -1;
    private string _appliedLogFilter = "";
    private string _appliedFlowFilter = "";
    private int _failureCount = -1;

    [ObservableProperty] private bool _isPaused;
    public string PauseLabel => IsPaused ? "Resume" : "Pause";
    partial void OnIsPausedChanged(bool value) {
        OnPropertyChanged(nameof(PauseLabel));
        if (!value) Refresh(force: true);
    }
    [ObservableProperty] private string _uptime = "";
    [ObservableProperty] private string _memory = "";
    [ObservableProperty] private string _collections = "";
    [ObservableProperty] private string _threads = "";
    [ObservableProperty] private string _uiLag = "";
    [ObservableProperty] private bool _isUiLagging;
    [ObservableProperty] private string _fiberSummary = "";
    [ObservableProperty] private string _inFlightMessage = "";
    [ObservableProperty] private string _logText = "";
    [ObservableProperty] private string _logFilter = "";
    [ObservableProperty] private string _flowFilter = "";
    [ObservableProperty] private double[] _lagHistory = [];
    [ObservableProperty] private string _failuresHeader = "Failures";
    [ObservableProperty] private string _runningHeader = "Running";

    public AvaloniaList<RunningFiberRow> Running { get; } = new();
    public AvaloniaList<SettledFiberRow> Settled { get; } = new();
    public AvaloniaList<FlowStatsRow> Stats { get; } = new();
    public AvaloniaList<FailureRow> Failures { get; } = new();
    public AvaloniaList<MessageRow> Messages { get; } = new();

    public string LogDirectory => DiagnosticsLog.LogDirectory;

    partial void OnLogFilterChanged(string value) => Refresh(force: true);
    partial void OnFlowFilterChanged(string value) => Refresh(force: true);

    public void Refresh(bool force = false) {
        if (IsPaused && !force) return;
        var now = DateTimeOffset.UtcNow;

        RefreshProcess();
        RefreshRunning(now);
        RefreshSettled(force);
        RefreshFailures();
        RefreshMessages();
        RefreshLog(force);
    }

    private void RefreshProcess() {
        using var process = Process.GetCurrentProcess();
        var up = DateTimeOffset.Now - DiagnosticsLog.StartedAt;
        Uptime = up.TotalHours >= 1 ? $"{(int)up.TotalHours}h {up.Minutes}m" : $"{up.Minutes}m {up.Seconds}s";
        Memory = $"{process.WorkingSet64 / 1048576.0:F0} MB working set · {GC.GetTotalMemory(false) / 1048576.0:F0} MB managed";
        Collections = $"GC {GC.CollectionCount(0)} / {GC.CollectionCount(1)} / {GC.CollectionCount(2)}";
        Threads = $"{process.Threads.Count} threads · {ThreadPoolSummary()}";
        UiLag = $"UI lag {DiagnosticsLog.LastUiLagMs:F0} ms · worst {DiagnosticsLog.MaxUiLagMs:F0} ms · {DiagnosticsLog.HangCount} stall(s)";
        IsUiLagging = DiagnosticsLog.LastUiLagMs > 100;
        LagHistory = DiagnosticsLog.UiLagHistory();
    }

    private static string ThreadPoolSummary() {
        System.Threading.ThreadPool.GetAvailableThreads(out var available, out _);
        System.Threading.ThreadPool.GetMaxThreads(out var max, out _);
        return $"{max - available} pool busy · {System.Threading.ThreadPool.PendingWorkItemCount} queued";
    }

    private void RefreshRunning(DateTimeOffset now) {
        var snapshot = CmdDiagnostics.Registry.Snapshot().ToArray();
        var byId = snapshot.ToDictionary(dump => dump.Id.Value);
        int Depth(FiberDump dump) {
            var depth = 0;
            var parent = dump.ParentId;
            while (parent != null && byId.TryGetValue(parent.Value.Value, out var next) && depth < 32) {
                depth++;
                parent = next.ParentId;
            }
            return depth;
        }

        // Parents before children, in start order.
        var ordered = snapshot.OrderBy(dump => dump.StartedAt).ToList();
        var rows = new System.Collections.Generic.List<RunningFiberRow>();
        void Emit(FiberDump dump) {
            var age = now - dump.StartedAt;
            var annotations = string.Join("  ", dump.Annotations.Where(pair => pair.Key != "gitkay.cmd").Select(pair => $"{pair.Key}={pair.Value}"));
            rows.Add(new RunningFiberRow(
                OptionModule.DefaultValue($"fiber #{dump.Id.Value}", dump.Name),
                $"#{dump.Id.Value}",
                FormatDuration(age.TotalMilliseconds),
                annotations,
                new Avalonia.Thickness(Depth(dump) * 16, 0, 0, 0),
                age.TotalMilliseconds > SlowFlowMs));
            foreach (var child in ordered.Where(candidate => candidate.ParentId != null && candidate.ParentId.Value.Value == dump.Id.Value))
                Emit(child);
        }

        foreach (var root in ordered.Where(dump => dump.ParentId == null || !byId.ContainsKey(dump.ParentId.Value.Value)))
            Emit(root);

        Replace(Running, rows);
        RunningHeader = rows.Count == 0 ? "Running" : $"Running ({rows.Count})";
        FiberSummary = $"{rows.Count} running · {CmdDiagnostics.StartedCount} started since launch";
    }

    private void RefreshSettled(bool force) {
        var settled = CmdDiagnostics.Settled();
        var signature = settled.Length == 0 ? 0 : settled[^1].Id * 1000 + settled.Length;
        if (!force && signature == _settledSignature && _appliedFlowFilter == FlowFilter) return;
        _settledSignature = signature;
        _appliedFlowFilter = FlowFilter;
        var filter = FlowFilter.Trim();

        var rows = settled.Reverse()
            .Where(fiber => filter.Length == 0 || fiber.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Select(fiber => new SettledFiberRow(
                fiber.SettledAt.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
                fiber.Name,
                FormatDuration(fiber.Duration.TotalMilliseconds),
                StatusName(fiber.Status),
                fiber.Status.IsFailed,
                fiber.Status.IsInterrupted,
                fiber.Duration.TotalMilliseconds > SlowFlowMs,
                fiber.Defect == null ? "" : OptionModule.DefaultValue("", fiber.Defect)))
            .ToList();
        Replace(Settled, rows);

        var stats = CmdDiagnostics.Stats()
            .Where(stat => filter.Length == 0 || stat.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(stat => stat.TotalMs)
            .Select(stat => new FlowStatsRow(
                stat.Name,
                stat.Count.ToString(CultureInfo.InvariantCulture),
                stat.Failed.ToString(CultureInfo.InvariantCulture),
                stat.Interrupted.ToString(CultureInfo.InvariantCulture),
                FormatDuration(stat.TotalMs / Math.Max(1, stat.Count)),
                FormatDuration(stat.MaxMs),
                stat.Failed > 0))
            .ToList();
        Replace(Stats, stats);
    }

    private void RefreshFailures() {
        var failures = CmdDiagnostics.Failures();
        if (failures.Length == _failureCount) return;
        _failureCount = failures.Length;
        Replace(Failures, failures.Reverse()
            .Select(failure => new FailureRow(failure.At.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture), failure.Name, failure.Cause))
            .ToList());
        FailuresHeader = failures.Length == 0 ? "Failures" : $"Failures ({failures.Length})";
    }

    private void RefreshMessages() {
        var messages = GitKay.Core.Diagnostics.recentMessages().ToArray();
        var signature = messages.Length == 0 ? 0 : messages[^1].At.UtcTicks + messages.Length;
        if (signature != _messagesSignature) {
            _messagesSignature = signature;
            Replace(Messages, messages.Reverse()
                .Select(crumb => new MessageRow(
                    crumb.At.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
                    crumb.Message,
                    $"{crumb.ElapsedMs:F1} ms",
                    crumb.ElapsedMs >= GitKay.Core.Diagnostics.slowUpdateThresholdMs))
                .ToList());
        }

        var running = GitKay.Core.Diagnostics.inFlightMessage();
        InFlightMessage = running == null ? "" : $"Handling {running.Value.Item1} for {running.Value.Item2:F0} ms";
    }

    private void RefreshLog(bool force) {
        var version = DiagnosticsLog.TailVersion;
        if (!force && version == _logVersion && _appliedLogFilter == LogFilter) return;
        _logVersion = version;
        _appliedLogFilter = LogFilter;
        var filter = LogFilter.Trim();
        var lines = DiagnosticsLog.LogTail();
        LogText = string.Join('\n', filter.Length == 0 ? lines : lines.Where(line => line.Contains(filter, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Everything on screen as text, for pasting into an issue.</summary>
    public string BuildSnapshot() {
        var builder = new StringBuilder();
        builder.AppendLine($"GitKay diagnostics snapshot {DateTimeOffset.Now:O}");
        builder.AppendLine($"Uptime {Uptime} · {Memory} · {Collections} · {Threads}");
        builder.AppendLine(UiLag);
        builder.AppendLine();
        builder.AppendLine(DiagnosticsLog.DescribeState());
        builder.AppendLine("Flow totals (name, count, failed, interrupted, avg, max):");
        foreach (var stat in Stats) builder.AppendLine($"  {stat.Name}  {stat.Count}  {stat.Failed}  {stat.Interrupted}  {stat.Average}  {stat.Max}");
        builder.AppendLine();
        builder.AppendLine("Recent failures:");
        foreach (var failure in Failures.Take(20)) builder.AppendLine($"  {failure.Time}  {failure.Name}: {failure.Cause}");
        return builder.ToString();
    }

    // F# unions' generated ToString needs reflection that NativeAOT removes; name the cases explicitly.
    private static string StatusName(FiberStatus status) =>
        status.IsSucceeded ? "Succeeded" : status.IsFailed ? "Failed" : status.IsInterrupted ? "Interrupted" : "Running";

    private static string FormatDuration(double ms) =>
        ms >= 60_000 ? $"{ms / 60_000:F1} min" : ms >= 1000 ? $"{ms / 1000:F2} s" : $"{ms:F1} ms";

    private static void Replace<T>(AvaloniaList<T> target, System.Collections.Generic.List<T> rows) {
        if (target.Count == rows.Count && target.SequenceEqual(rows)) return;
        target.Clear();
        target.AddRange(rows);
    }
}
