using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Avalonia.Media;
using DiffLineType = GitKay.Core.Models.LineType;

namespace GitKay.UI;

public readonly record struct DiffFileKey(string OldPath, string NewPath);

public interface IDiffRowProjection
{
}

public partial class DiffFileProjection : ObservableObject
{
    public DiffFileProjection(GitKay.Core.GitService.DiffFileSummary summary)
    {
        Key = new DiffFileKey(summary.OldPath, summary.NewPath);
        DisplayPath = summary.DisplayPath;
        Header = new DiffFileHeaderProjection(this);
    }

    public DiffFileKey Key { get; }
    [ObservableProperty] private string _displayPath = "";
    [ObservableProperty] private bool _isLoaded;
    [ObservableProperty] private bool _hasSearchMatch;
    [ObservableProperty] private string _searchMatchSummary = "";
    public ObservableCollection<DiffHunkProjection> Hunks { get; } = new();
    public DiffFileHeaderProjection Header { get; }

    public void UpdateSummary(GitKay.Core.GitService.DiffFileSummary summary)
    {
        DisplayPath = summary.DisplayPath;
        Header.UpdateDisplayPath(summary.DisplayPath);
    }

    public void ApplyContent(GitKay.Core.Models.FileDiff file)
    {
        Hunks.Clear();
        foreach (var hunk in file.Hunks.Select(h => new DiffHunkProjection(h)))
        {
            Hunks.Add(hunk);
        }

        IsLoaded = true;
    }

    public void ClearContent()
    {
        Hunks.Clear();
        IsLoaded = false;
    }

    public void ApplySearchState(string query, string scopeKey)
    {
        var normalizedQuery = query.Trim();
        if (string.IsNullOrWhiteSpace(normalizedQuery))
        {
            ClearSearchState(scopeKey);
            return;
        }

        var searchPaths = scopeKey is "all" or "path";
        var searchText = scopeKey is "all" or "text";

        var pathMatch =
            searchPaths
            && (ContainsIgnoreCase(Key.OldPath, normalizedQuery)
                || ContainsIgnoreCase(Key.NewPath, normalizedQuery)
                || ContainsIgnoreCase(DisplayPath, normalizedQuery));

        var textMatch = false;
        if (searchText && IsLoaded)
        {
            foreach (var hunk in Hunks)
            {
                foreach (var line in hunk.Lines)
                {
                    textMatch |= line.ApplySearchState(normalizedQuery, true);
                }
            }
        }
        else
        {
            foreach (var hunk in Hunks)
            {
                foreach (var line in hunk.Lines)
                {
                    line.ApplySearchState(normalizedQuery, false);
                }
            }
        }

        var hasSearchMatch = pathMatch || textMatch;
        HasSearchMatch = hasSearchMatch;
        SearchMatchSummary = hasSearchMatch ? BuildSearchSummary(pathMatch, textMatch) : "";
    }

    private void ClearSearchState(string scopeKey)
    {
        HasSearchMatch = false;
        SearchMatchSummary = "";

        var searchText = scopeKey is "all" or "text";
        foreach (var hunk in Hunks)
        {
            foreach (var line in hunk.Lines)
            {
                line.ApplySearchState("", searchText);
            }
        }
    }

    private static bool ContainsIgnoreCase(string value, string query) =>
        value.Contains(query, StringComparison.OrdinalIgnoreCase);

    private static string BuildSearchSummary(bool pathMatch, bool textMatch)
    {
        var parts = new System.Collections.Generic.List<string>();

        if (pathMatch)
        {
            parts.Add("path");
        }

        if (textMatch)
        {
            parts.Add("text");
        }

        return string.Join(" · ", parts);
    }
}

public sealed partial class DiffFileHeaderProjection : ObservableObject, IDiffRowProjection
{
    public DiffFileHeaderProjection(DiffFileProjection file)
    {
        File = file;
        DisplayPath = file.DisplayPath;
    }

    public DiffFileProjection File { get; }
    [ObservableProperty] private string _displayPath = "";

    public void UpdateDisplayPath(string displayPath) => DisplayPath = displayPath;
}

public sealed class DiffHunkHeaderProjection : IDiffRowProjection
{
    public DiffHunkHeaderProjection(DiffHunkProjection hunk)
    {
        Header = hunk.Header;
    }

    public string Header { get; }
}

public sealed class DiffHunkProjection
{
    public DiffHunkProjection(GitKay.Core.Models.DiffHunk hunk)
    {
        Header = hunk.Header;
        Lines = new ObservableCollection<DiffLineProjection>(hunk.Lines.Select(line => new DiffLineProjection(line)));
    }

    public string Header { get; }
    public ObservableCollection<DiffLineProjection> Lines { get; }
}

public partial class DiffLineProjection : ObservableObject, IDiffRowProjection
{
    public DiffLineProjection(GitKay.Core.Models.DiffLine line)
    {
        OldLineNoText = line.OldLineNo?.ToString() ?? "";
        NewLineNoText = line.NewLineNo?.ToString() ?? "";
        Prefix = line.Type.Equals(DiffLineType.Added)
            ? "+"
            : line.Type.Equals(DiffLineType.Removed)
                ? "-"
                : " ";
        Content = line.Content;
        Foreground = line.Type.Equals(DiffLineType.Added)
            ? Brushes.LightGreen
            : line.Type.Equals(DiffLineType.Removed)
                ? Brushes.IndianRed
                : line.Type.Equals(DiffLineType.Context)
                    ? Brushes.Gainsboro
                    : Brushes.LightSkyBlue;
    }

    public string OldLineNoText { get; }
    public string NewLineNoText { get; }
    public string Prefix { get; }
    public string Content { get; }
    public IBrush Foreground { get; }
    [ObservableProperty] private bool _isSearchMatch;
    [ObservableProperty] private IBrush _rowBackground = Brushes.Transparent;

    public bool ApplySearchState(string query, bool searchTextEnabled)
    {
        var isSearchMatch =
            searchTextEnabled
            && !string.IsNullOrWhiteSpace(query)
            && Content.Contains(query, StringComparison.OrdinalIgnoreCase);

        IsSearchMatch = isSearchMatch;
        RowBackground = isSearchMatch
            ? new SolidColorBrush(Color.FromArgb(48, 78, 201, 176))
            : Brushes.Transparent;

        return isSearchMatch;
    }
}

internal static class DiffFormatting
{
    public static string BuildDisplayPath(string oldPath, string newPath)
    {
        if (oldPath == "/dev/null")
        {
            return $"{newPath} (new file)";
        }

        if (newPath == "/dev/null")
        {
            return $"{oldPath} (deleted)";
        }

        if (oldPath == newPath)
        {
            return newPath;
        }

        return $"{oldPath} -> {newPath}";
    }
}
