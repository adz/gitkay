using System;
using System.IO;
using GitKay.Core;
using GitKay.Serialization;

namespace GitKay.UI;

public sealed class AppUiStateStore {
    private readonly string _statePath;

    public AppUiStateStore(string? statePath = null) {
        _statePath = statePath ?? GetDefaultStatePath();
    }

    public string StatePath => _statePath;

    /// <summary>The saved UI state, or an empty one when there is none or the file can't be read.</summary>
    public UiState Load() {
        try {
            if (!File.Exists(_statePath)) return UiStateModule.empty;
            var decoded = UiStateJson.decode(File.ReadAllText(_statePath));
            if (decoded.IsOk) return decoded.ResultValue;
            System.Diagnostics.Trace.WriteLine($"[ui-state] {_statePath} ignored: {decoded.ErrorValue}");
        }
        catch (Exception exception) {
            System.Diagnostics.Trace.WriteLine($"[ui-state] {_statePath} unreadable: {exception.Message}");
        }
        return UiStateModule.empty;
    }

    private readonly object _writeGate = new();

    public void Save(UiState state) {
        try {
            var directory = Path.GetDirectoryName(_statePath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            var json = UiStateJson.encode(state);
            // One writer at a time: the exit save and a background save must not interleave on the same file.
            lock (_writeGate) File.WriteAllText(_statePath, json);
        }
        catch (Exception exception) {
            System.Diagnostics.Trace.WriteLine($"[ui-state] {_statePath} not saved: {exception.Message}");
        }
    }

    private readonly object _pendingGate = new();
    private UiState? _pending;
    private bool _writerRunning;

    /// <summary>
    /// Saves without making the caller wait for the disk. Selecting a commit saves the selection, and a click must not
    /// stall on a slow or contended file (a synced AppData folder, a virus scan, a second GitKay writing the same file).
    /// Saves made while one is being written collapse into the newest; the returned task completes once the file holds it.
    /// </summary>
    public System.Threading.Tasks.Task SaveInBackground(UiState state) {
        var completion = new System.Threading.Tasks.TaskCompletionSource();
        lock (_pendingGate) {
            _pending = state;
            _waiting.Add(completion);
            if (_writerRunning) return completion.Task;
            _writerRunning = true;
        }
        System.Threading.Tasks.Task.Run(WriteWhilePending);
        return completion.Task;
    }

    private readonly System.Collections.Generic.List<System.Threading.Tasks.TaskCompletionSource> _waiting = new();

    private void WriteWhilePending() {
        while (true) {
            UiState next;
            System.Threading.Tasks.TaskCompletionSource[] waiting;
            lock (_pendingGate) {
                if (_pending is not { } pending) {
                    _writerRunning = false;
                    return;
                }
                next = pending;
                _pending = null;
                waiting = _waiting.ToArray();
                _waiting.Clear();
            }
            Save(next);
            foreach (var completion in waiting) completion.TrySetResult();
        }
    }

    public static string GetDefaultStatePath() {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        if (string.IsNullOrWhiteSpace(appData)) {
            appData = Path.GetTempPath();
        }

        return Path.Combine(appData, "gitkay", "ui-state.json");
    }
}
