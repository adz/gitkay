using System;
using System.IO;
using System.Threading;

namespace GitKay.UI;

/// <summary>
/// Watches the working tree and the git directory's index and HEAD, and reports a change once events have been quiet
/// for a moment, so a build or checkout touching many files refreshes once.
/// </summary>
public sealed class WorkingTreeWatcher : IDisposable {
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(300);
    private readonly string _gitDirectory;
    private readonly Action _changed;
    private readonly Timer _debounce;
    private readonly FileSystemWatcher[] _watchers;

    private WorkingTreeWatcher(string workingDirectory, string gitDirectory, Action changed) {
        _gitDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gitDirectory));
        _changed = changed;
        _debounce = new Timer(_ => _changed(), null, Timeout.Infinite, Timeout.Infinite);
        var tree = Watch(workingDirectory, recursive: true);
        // A linked worktree or separate git directory lives outside the working tree.
        _watchers = IsUnder(_gitDirectory, workingDirectory) ? [tree] : [tree, Watch(_gitDirectory, recursive: false)];
    }

    /// <summary>Starts watching; null when the platform can't watch this tree (for example, too many directories for inotify).</summary>
    public static WorkingTreeWatcher? TryStart(string? workingDirectory, string? gitDirectory, Action changed) {
        if (string.IsNullOrEmpty(workingDirectory) || string.IsNullOrEmpty(gitDirectory) || !Directory.Exists(workingDirectory)) return null;
        try {
            return new WorkingTreeWatcher(workingDirectory, gitDirectory, changed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException) {
            System.Diagnostics.Trace.WriteLine($"[watcher] not watching {workingDirectory}: {ex.Message}");
            return null;
        }
    }

    private FileSystemWatcher Watch(string directory, bool recursive) {
        var watcher = new FileSystemWatcher(directory) {
            IncludeSubdirectories = recursive,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        watcher.Changed += OnEvent;
        watcher.Created += OnEvent;
        watcher.Deleted += OnEvent;
        watcher.Renamed += OnEvent;
        // Overflowed buffers lose events: refresh rather than miss a change.
        watcher.Error += (_, _) => Schedule();
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    private void OnEvent(object sender, FileSystemEventArgs e) {
        if (Matters(e.FullPath)) Schedule();
    }

    /// <summary>Inside the git directory only the index and HEAD say the uncommitted changes moved.</summary>
    private bool Matters(string path) {
        if (!IsUnder(path, _gitDirectory)) return true;
        var relative = Path.GetRelativePath(_gitDirectory, path);
        return relative is "index" or "HEAD";
    }

    private static bool IsUnder(string path, string directory) {
        var full = Path.GetFullPath(path);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        return full.Equals(root, StringComparison.Ordinal)
               || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private void Schedule() => _debounce.Change(Quiet, Timeout.InfiniteTimeSpan);

    public void Dispose() {
        foreach (var watcher in _watchers) watcher.Dispose();
        _debounce.Dispose();
    }
}
