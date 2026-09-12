using System;
using System.IO;
using GitKay.Serialization;

namespace GitKay.UI;

public sealed class AppSettingsStore
{
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
            var document = GitKayJson.DeserializeSettings(json);

            return AppSettings.Create(
                showBranchRefs: document.ShowBranchRefs,
                showStashes: document.ShowStashes,
                diffContextLines: document.DiffContextLines,
                diffPresentationModeKey: document.DiffPresentationModeKey,
                commitRowFontFamily: document.CommitRowFontFamily,
                commitRowMonoFontFamily: document.CommitRowMonoFontFamily,
                commitRowTextFontSize: document.CommitRowTextFontSize,
                commitRowMetaFontSize: document.CommitRowMetaFontSize,
                commitRowBadgeFontSize: document.CommitRowBadgeFontSize,
                searchDebounceSeconds: document.SearchDebounceSeconds).Normalize();
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

            var document = new AppSettingsDocument(
                normalized.ShowBranchRefs,
                normalized.ShowStashes,
                normalized.DiffContextLines,
                normalized.DiffPresentationModeKey,
                normalized.CommitRowFontFamily,
                normalized.CommitRowMonoFontFamily,
                normalized.CommitRowTextFontSize,
                normalized.CommitRowMetaFontSize,
                normalized.CommitRowBadgeFontSize,
                normalized.SearchDebounceSeconds);

            var json = GitKayJson.SerializeSettings(document);
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
