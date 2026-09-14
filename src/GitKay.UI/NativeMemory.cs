using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace GitKay.UI;

/// <summary>
/// Returns freed native memory to the OS on Linux. libgit2 allocates and frees heavily while walking trees and diffs;
/// glibc keeps that freed memory in its arenas, so a full-history search left ~1 GB of anonymous memory resident
/// although live native use was ~190 MB. <c>malloc_trim</c> releases it without slowing later work.
/// </summary>
public static partial class NativeMemory {
    private const long GrowthThresholdBytes = 100L * 1024 * 1024;
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(10);
    private static long _lastTrimTicks;
    // The lowest resident size seen after a trim: a trim during heavy work would otherwise set a high baseline and
    // suppress the trim that matters once the work finishes.
    private static long _baseline = long.MaxValue;
    private static bool _unsupported = !OperatingSystem.IsLinux();

    public static long TotalReleasedBytes { get; private set; }
    public static int TrimCount { get; private set; }

    [DllImport("libc", EntryPoint = "malloc_trim")]
    private static extern int MallocTrim(nuint pad);

    /// <summary>Trims when anonymous resident memory has grown past the threshold since the last trim.</summary>
    public static void TrimIfGrown() {
        if (_unsupported) return;
        if (Stopwatch.GetElapsedTime(_lastTrimTicks) < MinimumInterval && _lastTrimTicks != 0) return;
        var before = AnonymousResidentBytes();
        if (before < 0 || (_baseline != long.MaxValue && before - _baseline < GrowthThresholdBytes)) return;
        Trim(before);
    }

    /// <summary>Trims now, for example after a large search completes.</summary>
    public static void TrimNow() {
        if (_unsupported) return;
        Trim(AnonymousResidentBytes());
    }

    private static void Trim(long before) {
        try {
            MallocTrim(0);
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException) {
            // Not glibc (e.g. musl): nothing to trim.
            _unsupported = true;
            return;
        }

        _lastTrimTicks = Stopwatch.GetTimestamp();
        var after = AnonymousResidentBytes();
        if (after > 0) _baseline = Math.Min(_baseline, after);
        if (before > 0 && after > 0 && before > after) {
            TotalReleasedBytes += before - after;
            TrimCount++;
            Trace.WriteLine($"[memory] malloc_trim released {(before - after) / 1048576} MB (anonymous resident {before / 1048576} -> {after / 1048576} MB)");
        }
    }

    /// <summary>RssAnon from /proc/self/status, in bytes; -1 when unavailable.</summary>
    public static long AnonymousResidentBytes() {
        try {
            foreach (var line in File.ReadLines("/proc/self/status")) {
                if (!line.StartsWith("RssAnon:", StringComparison.Ordinal)) continue;
                var digits = line.AsSpan("RssAnon:".Length).Trim();
                var end = digits.IndexOf(' ');
                return long.Parse(end < 0 ? digits : digits[..end]) * 1024;
            }
        }
        catch (Exception) {
        }
        return -1;
    }
}
