using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    private string SearchHistoryPath => Path.Combine(Path.GetDirectoryName(_statePath) ?? "", "search-history.txt");

    /// <summary>Recent commit searches, newest first, one per line.</summary>
    public IReadOnlyList<string> LoadSearchHistory() {
        try {
            return File.Exists(SearchHistoryPath) ? File.ReadAllLines(SearchHistoryPath) : Array.Empty<string>();
        }
        catch {
            return Array.Empty<string>();
        }
    }

    public void SaveSearchHistory(IEnumerable<string> searches) {
        try {
            var directory = Path.GetDirectoryName(SearchHistoryPath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            File.WriteAllLines(SearchHistoryPath, searches);
        }
        catch {
        }
    }

    private string ViewPreferencesPath => Path.Combine(Path.GetDirectoryName(_statePath) ?? "", "view-preferences.txt");

    /// <summary>Small view toggles (file tree mode, details expanded, search mode...) as key=value lines.</summary>
    public IReadOnlyDictionary<string, string> LoadViewPreferences() {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        try {
            if (!File.Exists(ViewPreferencesPath)) return result;
            foreach (var line in File.ReadAllLines(ViewPreferencesPath)) {
                var separator = line.IndexOf('=');
                if (separator > 0) result[line[..separator]] = line[(separator + 1)..];
            }
        }
        catch {
        }

        return result;
    }

    public void SaveViewPreferences(IReadOnlyDictionary<string, string> preferences) {
        try {
            var directory = Path.GetDirectoryName(ViewPreferencesPath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            File.WriteAllLines(ViewPreferencesPath, preferences.Select(entry => $"{entry.Key}={entry.Value}"));
        }
        catch {
        }
    }

    public static string GetDefaultStatePath() {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        if (string.IsNullOrWhiteSpace(appData)) {
            appData = Path.GetTempPath();
        }

        return Path.Combine(appData, "GitKay", "ui-state.json");
    }
}
