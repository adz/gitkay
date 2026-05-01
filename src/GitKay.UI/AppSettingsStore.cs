using System;
using System.IO;
using System.Text.Json;

namespace GitKay.UI;

public sealed class AppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions =
        new()
        {
            WriteIndented = true,
        };

    private readonly string _settingsPath;

    public AppSettingsStore(string? settingsPath = null)
    {
        _settingsPath = settingsPath ?? GetDefaultSettingsPath();
    }

    public string SettingsPath => _settingsPath;

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return AppSettings.Default;
            }

            var json = File.ReadAllText(_settingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions)?.Normalize() ?? AppSettings.Default;
        }
        catch
        {
            return AppSettings.Default;
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            var normalized = settings.Normalize();
            var directory = Path.GetDirectoryName(_settingsPath);

            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(normalized, JsonOptions);
            File.WriteAllText(_settingsPath, json);
        }
        catch
        {
        }
    }

    public static string GetDefaultSettingsPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        if (string.IsNullOrWhiteSpace(appData))
        {
            appData = Path.GetTempPath();
        }

        return Path.Combine(appData, "GitKay", "settings.json");
    }
}
