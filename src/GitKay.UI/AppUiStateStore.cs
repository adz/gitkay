using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GitKay.Serialization;

namespace GitKay.UI;

public sealed class AppUiStateStore
{
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
            var document = GitKayJson.DeserializeUiState(json);
            var state = AppUiState.Default.WithWindowSize(document.WindowWidth, document.WindowHeight);
            if (document.Layout != null)
            {
                state = state.WithLayout(new UiLayoutState
                {
                    HistoryPaneRatio = document.Layout.HistoryPaneRatio,
                    FileListWidth = document.Layout.FileListWidth,
                    GraphColumnWidth = document.Layout.GraphColumnWidth,
                    HashColumnWidth = document.Layout.HashColumnWidth,
                    AuthorColumnWidth = document.Layout.AuthorColumnWidth,
                    DateColumnWidth = document.Layout.DateColumnWidth,
                });
            }

            foreach (var repoSelection in document.RepoSelections)
            {
                state = state.WithRepoSelection(repoSelection.Key, repoSelection.Value);
            }

            return state.Normalize();
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

            var repoSelections = new List<KeyValuePair<string, string>>();

            foreach (var repoState in normalized.RepoStates)
            {
                var selectedCommitHash = RepoUiState.NormalizeHash(repoState.Value.LastSelectedCommitHash);

                if (selectedCommitHash != null)
                {
                    repoSelections.Add(new KeyValuePair<string, string>(repoState.Key, selectedCommitHash));
                }
            }

            var layout = normalized.Layout;
            var json = GitKayJson.SerializeUiState(
                normalized.WindowWidth,
                normalized.WindowHeight,
                repoSelections,
                new UiLayoutDocument(
                    layout.HistoryPaneRatio,
                    layout.FileListWidth,
                    layout.GraphColumnWidth,
                    layout.HashColumnWidth,
                    layout.AuthorColumnWidth,
                    layout.DateColumnWidth));

            File.WriteAllText(_statePath, json);
        }
        catch
        {
        }
    }

    private string SearchHistoryPath => Path.Combine(Path.GetDirectoryName(_statePath) ?? "", "search-history.txt");

    /// <summary>Recent commit searches, newest first, one per line.</summary>
    public IReadOnlyList<string> LoadSearchHistory()
    {
        try
        {
            return File.Exists(SearchHistoryPath) ? File.ReadAllLines(SearchHistoryPath) : Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public void SaveSearchHistory(IEnumerable<string> searches)
    {
        try
        {
            var directory = Path.GetDirectoryName(SearchHistoryPath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            File.WriteAllLines(SearchHistoryPath, searches);
        }
        catch
        {
        }
    }

    private string ViewPreferencesPath => Path.Combine(Path.GetDirectoryName(_statePath) ?? "", "view-preferences.txt");

    /// <summary>Small view toggles (file tree mode, details expanded, search mode...) as key=value lines.</summary>
    public IReadOnlyDictionary<string, string> LoadViewPreferences()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(ViewPreferencesPath)) return result;
            foreach (var line in File.ReadAllLines(ViewPreferencesPath))
            {
                var separator = line.IndexOf('=');
                if (separator > 0) result[line[..separator]] = line[(separator + 1)..];
            }
        }
        catch
        {
        }

        return result;
    }

    public void SaveViewPreferences(IReadOnlyDictionary<string, string> preferences)
    {
        try
        {
            var directory = Path.GetDirectoryName(ViewPreferencesPath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            File.WriteAllLines(ViewPreferencesPath, preferences.Select(entry => $"{entry.Key}={entry.Value}"));
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
