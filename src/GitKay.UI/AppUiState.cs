using System;
using System.Collections.Generic;
using System.IO;

namespace GitKay.UI;

public sealed record RepoUiState
{
    public string? LastSelectedCommitHash { get; init; }

    public static RepoUiState Default { get; } = new();

    public RepoUiState Normalize()
    {
        return this with
        {
            LastSelectedCommitHash = NormalizeHash(LastSelectedCommitHash),
        };
    }

    internal static string? NormalizeHash(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}

/// <summary>Splitter positions and history column widths chosen by the user; null values use layout defaults.</summary>
public sealed record UiLayoutState
{
    /// <summary>Commit list height as a fraction of the commit list plus diff area.</summary>
    public double? HistoryPaneRatio { get; init; }
    public double? FileListWidth { get; init; }
    public double? GraphColumnWidth { get; init; }
    public double? HashColumnWidth { get; init; }
    public double? AuthorColumnWidth { get; init; }
    public double? DateColumnWidth { get; init; }

    public static UiLayoutState Default { get; } = new();

    public UiLayoutState Normalize() => new()
    {
        HistoryPaneRatio = HistoryPaneRatio is { } ratio && double.IsFinite(ratio) ? Math.Clamp(ratio, 0.1, 0.9) : null,
        FileListWidth = Width(FileListWidth),
        GraphColumnWidth = Width(GraphColumnWidth),
        HashColumnWidth = Width(HashColumnWidth),
        AuthorColumnWidth = Width(AuthorColumnWidth),
        DateColumnWidth = Width(DateColumnWidth),
    };

    private static double? Width(double? value) =>
        value is { } width && double.IsFinite(width) && width > 0 ? Math.Min(width, 10000) : null;
}

public sealed record AppUiState
{
    public UiLayoutState Layout { get; init; } = UiLayoutState.Default;

    public double? WindowWidth { get; init; }

    public double? WindowHeight { get; init; }

    public Dictionary<string, RepoUiState> RepoStates { get; init; } = new(StringComparer.Ordinal);

    public static AppUiState Default { get; } = new();

    public AppUiState Normalize()
    {
        var normalizedRepoStates = new Dictionary<string, RepoUiState>(StringComparer.Ordinal);

        foreach (var entry in RepoStates ?? new Dictionary<string, RepoUiState>())
        {
            var normalizedRepoKey = NormalizeRepoKey(entry.Key);
            if (normalizedRepoKey == null)
            {
                continue;
            }

            normalizedRepoStates[normalizedRepoKey] = (entry.Value ?? RepoUiState.Default).Normalize();
        }

        return this with
        {
            WindowWidth = NormalizeDimension(WindowWidth),
            WindowHeight = NormalizeDimension(WindowHeight),
            RepoStates = normalizedRepoStates,
            Layout = (Layout ?? UiLayoutState.Default).Normalize(),
        };
    }

    public AppUiState WithLayout(UiLayoutState layout) => this with { Layout = layout.Normalize() };

    public AppUiState WithWindowSize(double? width, double? height)
    {
        var normalizedWidth = NormalizeDimension(width);
        var normalizedHeight = NormalizeDimension(height);

        return this with
        {
            WindowWidth = normalizedWidth ?? WindowWidth,
            WindowHeight = normalizedHeight ?? WindowHeight,
        };
    }

    public AppUiState WithRepoSelection(string? repoKey, string? selectedCommitHash)
    {
        var normalizedRepoKey = NormalizeRepoKey(repoKey);
        var normalizedCommitHash = RepoUiState.NormalizeHash(selectedCommitHash);

        if (normalizedRepoKey == null || normalizedCommitHash == null)
        {
            return this;
        }

        var nextRepoStates = new Dictionary<string, RepoUiState>(RepoStates ?? new Dictionary<string, RepoUiState>(), StringComparer.Ordinal)
        {
            [normalizedRepoKey] = new RepoUiState
            {
                LastSelectedCommitHash = normalizedCommitHash,
            },
        };

        return this with
        {
            RepoStates = nextRepoStates,
        };
    }

    public string? GetLastSelectedCommitHash(string? repoKey)
    {
        var normalizedRepoKey = NormalizeRepoKey(repoKey);
        if (normalizedRepoKey == null)
        {
            return null;
        }

        if (RepoStates != null
            && RepoStates.TryGetValue(normalizedRepoKey, out var repoState)
            && repoState != null)
        {
            return RepoUiState.NormalizeHash(repoState.LastSelectedCommitHash);
        }

        return null;
    }

    private static double? NormalizeDimension(double? value)
    {
        return value.HasValue && double.IsFinite(value.Value) && value.Value > 0d ? value : null;
    }

    private static string? NormalizeRepoKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value.Trim()));
    }
}
