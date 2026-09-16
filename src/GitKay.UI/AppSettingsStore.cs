using System;
using System.IO;
using GitKay.Core;
using GitKay.Serialization;

namespace GitKay.UI;

public sealed class AppSettingsStore {
    private readonly string _settingsPath;

    public AppSettingsStore(string? settingsPath = null) {
        _settingsPath = settingsPath ?? GetDefaultSettingsPath();
    }

    public string SettingsPath => _settingsPath;

    /// <summary>The saved settings, or the defaults when there are none or the file can't be read.</summary>
    public Settings Load() {
        try {
            if (!File.Exists(_settingsPath)) return SettingsModule.defaults;
            var decoded = SettingsJson.decode(File.ReadAllText(_settingsPath));
            if (decoded.IsOk) return decoded.ResultValue;
            System.Diagnostics.Trace.WriteLine($"[settings] {_settingsPath} ignored: {decoded.ErrorValue}");
        }
        catch (Exception exception) {
            System.Diagnostics.Trace.WriteLine($"[settings] {_settingsPath} unreadable: {exception.Message}");
        }
        return SettingsModule.defaults;
    }

    public void Save(Settings settings) {
        try {
            var directory = Path.GetDirectoryName(_settingsPath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(_settingsPath, SettingsJson.encode(settings));
        }
        catch (Exception exception) {
            System.Diagnostics.Trace.WriteLine($"[settings] {_settingsPath} not saved: {exception.Message}");
        }
    }

    public static string GetDefaultSettingsPath() {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        if (string.IsNullOrWhiteSpace(appData)) {
            appData = Path.GetTempPath();
        }

        return Path.Combine(appData, "gitkay", "settings.json");
    }
}
