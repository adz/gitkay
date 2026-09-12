using System;
using System.Collections.Generic;
using System.IO;
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

            var json = GitKayJson.SerializeUiState(
                normalized.WindowWidth,
                normalized.WindowHeight,
                repoSelections);

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
