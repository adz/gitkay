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

    public void Save(UiState state) {
        try {
            var directory = Path.GetDirectoryName(_statePath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(_statePath, UiStateJson.encode(state));
        }
        catch (Exception exception) {
            System.Diagnostics.Trace.WriteLine($"[ui-state] {_statePath} not saved: {exception.Message}");
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
