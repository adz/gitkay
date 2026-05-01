using System;
using System.IO;
using System.Text.Json;

namespace GitKay.UI;

public sealed class AppUiStateStore
{
    private static readonly JsonSerializerOptions JsonOptions =
        new()
        {
            WriteIndented = true,
        };

    private readonly string _statePath;

    public AppUiStateStore(string? statePath = null)
    {
        _statePath = statePath ?? GetDefaultStatePath();
    }

    public string StatePath => _statePath;

    public AppUiState Load()
    {
        try
        {
            if (!File.Exists(_statePath))
            {
                return AppUiState.Default;
            }

            var json = File.ReadAllText(_statePath);
            return JsonSerializer.Deserialize<AppUiState>(json, JsonOptions)?.Normalize() ?? AppUiState.Default;
        }
        catch
        {
            return AppUiState.Default;
        }
    }

    public void Save(AppUiState state)
    {
        try
        {
            var normalized = state.Normalize();
            var directory = Path.GetDirectoryName(_statePath);

            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(normalized, JsonOptions);
            File.WriteAllText(_statePath, json);
        }
        catch
        {
        }
    }

    public static string GetDefaultStatePath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        if (string.IsNullOrWhiteSpace(appData))
        {
            appData = Path.GetTempPath();
        }

        return Path.Combine(appData, "GitKay", "ui-state.json");
    }
}
