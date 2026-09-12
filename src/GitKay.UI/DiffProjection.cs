using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Microsoft.FSharp.Core;
using DiffLineType = GitKay.Core.Models.LineType;

namespace GitKay.UI;

public readonly record struct DiffFileKey(string OldPath, string NewPath);

public interface IDiffRowProjection
{
}

internal static class DiffSearchPresentation
{
    public static readonly IBrush MatchBackground = new SolidColorBrush(Color.FromArgb(34, 215, 186, 125));
    public static readonly IBrush MatchBorderBrush = new SolidColorBrush(Color.FromArgb(220, 215, 186, 125));
    public static readonly IBrush MatchForeground = new SolidColorBrush(Color.FromRgb(215, 186, 125));
    public static readonly FontWeight MatchFontWeight = FontWeight.SemiBold;

    public static (string Prefix, string Match, string Suffix, bool HasMatch) Split(string value, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return (value, "", "", false);
        }

        var index = value.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return (value, "", "", false);
        }

        var prefix = index > 0 ? value[..index] : "";
        var match = value.Substring(index, query.Length);
        var suffix = index + query.Length < value.Length ? value[(index + query.Length)..] : "";
        return (prefix, match, suffix, true);
    }
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
    [ObservableProperty] private IBrush _rowBackground = Brushes.Transparent;
    [ObservableProperty] private IBrush _borderBrush = Brushes.Transparent;
    [ObservableProperty] private string _matchPrefix = "";
    [ObservableProperty] private string _matchText = "";
    [ObservableProperty] private string _matchSuffix = "";
    [ObservableProperty] private IBrush _matchForeground = DiffSearchPresentation.MatchForeground;
    [ObservableProperty] private FontWeight _matchFontWeight = FontWeight.Normal;
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
        ClearSearchState();
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
        UpdateSearchHighlight(normalizedQuery, pathMatch, hasSearchMatch);
        Header.ApplySearchState(normalizedQuery, pathMatch, textMatch, SearchMatchSummary);
    }

    private void ClearSearchState(string scopeKey)
    {
        HasSearchMatch = false;
        SearchMatchSummary = "";
        RowBackground = Brushes.Transparent;
        BorderBrush = Brushes.Transparent;
        MatchPrefix = DisplayPath;
        MatchText = "";
        MatchSuffix = "";
        MatchForeground = DiffSearchPresentation.MatchForeground;
        MatchFontWeight = FontWeight.Normal;
        Header.ClearSearchState();

        var searchText = scopeKey is "all" or "text";
        foreach (var hunk in Hunks)
        {
            foreach (var line in hunk.Lines)
            {
                line.ApplySearchState("", searchText);
            }
        }
    }

    private void ClearSearchState()
    {
        ClearSearchState("all");
    }

    private void UpdateSearchHighlight(string query, bool pathMatch, bool hasSearchMatch)
    {
        RowBackground = hasSearchMatch
            ? DiffSearchPresentation.MatchBackground
            : Brushes.Transparent;
        BorderBrush = hasSearchMatch
            ? DiffSearchPresentation.MatchBorderBrush
            : Brushes.Transparent;

        if (pathMatch)
        {
            var (prefix, match, suffix, _) = DiffSearchPresentation.Split(DisplayPath, query);
            MatchPrefix = prefix;
            MatchText = match;
            MatchSuffix = suffix;
            MatchForeground = DiffSearchPresentation.MatchForeground;
            MatchFontWeight = DiffSearchPresentation.MatchFontWeight;
        }
        else
        {
            MatchPrefix = DisplayPath;
            MatchText = "";
            MatchSuffix = "";
            MatchForeground = DiffSearchPresentation.MatchForeground;
            MatchFontWeight = FontWeight.Normal;
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
    [ObservableProperty] private bool _hasSearchMatch;
    [ObservableProperty] private string _searchMatchSummary = "";
    [ObservableProperty] private IBrush _rowBackground = Brushes.Transparent;
    [ObservableProperty] private IBrush _borderBrush = Brushes.Transparent;
    [ObservableProperty] private string _matchPrefix = "";
    [ObservableProperty] private string _matchText = "";
    [ObservableProperty] private string _matchSuffix = "";
    [ObservableProperty] private IBrush _matchForeground = DiffSearchPresentation.MatchForeground;
    [ObservableProperty] private FontWeight _matchFontWeight = FontWeight.Normal;

    public void UpdateDisplayPath(string displayPath) => DisplayPath = displayPath;

    public void ApplySearchState(string query, bool pathMatch, bool textMatch, string searchMatchSummary)
    {
        HasSearchMatch = pathMatch || textMatch;
        SearchMatchSummary = HasSearchMatch ? searchMatchSummary : "";
        RowBackground = HasSearchMatch
            ? DiffSearchPresentation.MatchBackground
            : Brushes.Transparent;
        BorderBrush = HasSearchMatch
            ? DiffSearchPresentation.MatchBorderBrush
            : Brushes.Transparent;

        if (pathMatch)
        {
            var (prefix, match, suffix, _) = DiffSearchPresentation.Split(DisplayPath, query);
            MatchPrefix = prefix;
            MatchText = match;
            MatchSuffix = suffix;
            MatchForeground = DiffSearchPresentation.MatchForeground;
            MatchFontWeight = DiffSearchPresentation.MatchFontWeight;
        }
        else
        {
            MatchPrefix = DisplayPath;
            MatchText = "";
            MatchSuffix = "";
            MatchForeground = DiffSearchPresentation.MatchForeground;
            MatchFontWeight = FontWeight.Normal;
        }
    }

    public void ClearSearchState()
    {
        HasSearchMatch = false;
        SearchMatchSummary = "";
        RowBackground = Brushes.Transparent;
        BorderBrush = Brushes.Transparent;
        MatchPrefix = DisplayPath;
        MatchText = "";
        MatchSuffix = "";
        MatchForeground = DiffSearchPresentation.MatchForeground;
        MatchFontWeight = FontWeight.Normal;
    }
}

public sealed class DiffHunkHeaderProjection : IDiffRowProjection
{
    private InlineCollection? _headerInlines;

    public DiffHunkHeaderProjection(DiffHunkProjection hunk)
    {
        Header = hunk.Header;
    }

    public string Header { get; }
    public InlineCollection HeaderInlines => _headerInlines ??= SyntaxHighlighting.BuildHunkHeaderInlines(Header);
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
    private static readonly IBrush AddedBackground = new SolidColorBrush(Color.FromArgb(72, 31, 108, 56));
    private static readonly IBrush RemovedBackground = new SolidColorBrush(Color.FromArgb(78, 128, 46, 46));
    private static readonly IBrush ContextBackground = Brushes.Transparent;
    private static readonly IBrush HunkBackground = new SolidColorBrush(Color.FromArgb(34, 86, 156, 214));
    private static readonly IBrush AddedAccent = new SolidColorBrush(Color.FromRgb(87, 206, 117));
    private static readonly IBrush RemovedAccent = new SolidColorBrush(Color.FromRgb(230, 96, 96));
    private static readonly IBrush ContextAccent = new SolidColorBrush(Color.FromRgb(132, 132, 132));
    private static readonly IBrush HunkAccent = new SolidColorBrush(Color.FromRgb(86, 156, 214));
    private static readonly IBrush ContentForegroundBrush = new SolidColorBrush(Color.FromRgb(220, 220, 220));
    private static readonly IBrush LineNumberForegroundBrush = new SolidColorBrush(Color.FromRgb(150, 150, 150));
    private static readonly IBrush EmptySideBackground = Brushes.Transparent;

    public DiffLineProjection(GitKay.Core.Models.DiffLine line)
    {
        var isAdded = line.Type.Equals(DiffLineType.Added);
        var isRemoved = line.Type.Equals(DiffLineType.Removed);
        var isContext = line.Type.Equals(DiffLineType.Context);

        OldLineNoText = FormatLineNumber(line.OldLineNo);
        NewLineNoText = FormatLineNumber(line.NewLineNo);
        OldContent = isAdded ? "" : line.Content;
        NewContent = isRemoved ? "" : line.Content;
        Prefix = isAdded
            ? "+"
            : isRemoved
                ? "-"
                : " ";
        Content = line.Content;
        RowBackground = isAdded
            ? AddedBackground
            : isRemoved
                ? RemovedBackground
                : isContext
                    ? ContextBackground
                    : HunkBackground;
        OldCellBackground = isAdded ? EmptySideBackground : RowBackground;
        NewCellBackground = isRemoved ? EmptySideBackground : RowBackground;
        PrefixForeground = isAdded
            ? AddedAccent
            : isRemoved
                ? RemovedAccent
                : isContext
                    ? ContextAccent
                    : HunkAccent;
        Foreground = ContentForegroundBrush;
        LineNumberForeground = LineNumberForegroundBrush;
        MatchForeground = DiffSearchPresentation.MatchForeground;
    }

    private DiffLineProjection(DiffLineProjection removedLine, DiffLineProjection addedLine)
    {
        OldLineNoText = removedLine.OldLineNoText;
        NewLineNoText = addedLine.NewLineNoText;
        OldContent = removedLine.Content;
        NewContent = addedLine.Content;
        Prefix = " ";
        Content = $"{removedLine.Content}\n{addedLine.Content}";
        RowBackground = ContextBackground;
        OldCellBackground = RemovedBackground;
        NewCellBackground = AddedBackground;
        PrefixForeground = ContextAccent;
        Foreground = ContentForegroundBrush;
        LineNumberForeground = LineNumberForegroundBrush;
        MatchForeground = DiffSearchPresentation.MatchForeground;
    }

    public static DiffLineProjection CreateSideBySidePair(DiffLineProjection removedLine, DiffLineProjection addedLine) =>
        new(removedLine, addedLine);

    public string OldLineNoText { get; }
    public string NewLineNoText { get; }
    public string OldContent { get; }
    public string NewContent { get; }
    public string Prefix { get; }
    public string Content { get; }
    public IBrush Foreground { get; }
    public IBrush PrefixForeground { get; }
    public IBrush LineNumberForeground { get; }
    public IBrush OldCellBackground { get; }
    public IBrush NewCellBackground { get; }
    public bool IsAdded => Prefix == "+";
    public bool IsRemoved => Prefix == "-";
    [ObservableProperty] private bool _isSearchMatch;
    [ObservableProperty] private IBrush _rowBackground = Brushes.Transparent;
    [ObservableProperty] private IBrush _borderBrush = Brushes.Transparent;
    [ObservableProperty] private string _matchPrefix = "";
    [ObservableProperty] private string _matchText = "";
    [ObservableProperty] private string _matchSuffix = "";
    [ObservableProperty] private IBrush _matchForeground = DiffSearchPresentation.MatchForeground;
    [ObservableProperty] private FontWeight _matchFontWeight = FontWeight.Normal;

    private InlineCollection? _oldContentInlines;
    private InlineCollection? _newContentInlines;
    private InlineCollection? _contentInlines;

    public InlineCollection OldContentInlines => _oldContentInlines ??= SyntaxHighlighting.BuildCodeInlines(OldContent, Foreground, _searchTextEnabled ? _searchQuery : null);
    public InlineCollection NewContentInlines => _newContentInlines ??= SyntaxHighlighting.BuildCodeInlines(NewContent, Foreground, _searchTextEnabled ? _searchQuery : null);
    public InlineCollection ContentInlines => _contentInlines ??= SyntaxHighlighting.BuildCodeInlines(Content, Foreground, _searchTextEnabled ? _searchQuery : null);

    private string _searchQuery = "";
    private bool _searchTextEnabled;

    private static string FormatLineNumber(FSharpOption<int> lineNumber) =>
        lineNumber is null ? "" : lineNumber.Value.ToString();

    public bool ApplySearchState(string query, bool searchTextEnabled)
    {
        var normalizedQuery = query.Trim();
        if (_searchQuery == normalizedQuery && _searchTextEnabled == searchTextEnabled)
        {
            return IsSearchMatch;
        }

        _searchQuery = normalizedQuery;
        _searchTextEnabled = searchTextEnabled;

        var isSearchMatch =
            searchTextEnabled
            && !string.IsNullOrWhiteSpace(_searchQuery)
            && Content.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase);

        IsSearchMatch = isSearchMatch;
        BorderBrush = isSearchMatch
            ? DiffSearchPresentation.MatchBorderBrush
            : Brushes.Transparent;

        if (isSearchMatch)
        {
            var (prefix, match, suffix, _) = DiffSearchPresentation.Split(Content, _searchQuery);
            MatchPrefix = prefix;
            MatchText = match;
            MatchSuffix = suffix;
            MatchForeground = DiffSearchPresentation.MatchForeground;
            MatchFontWeight = DiffSearchPresentation.MatchFontWeight;
        }
        else
        {
            MatchPrefix = Content;
            MatchText = "";
            MatchSuffix = "";
            MatchForeground = DiffSearchPresentation.MatchForeground;
            MatchFontWeight = FontWeight.Normal;
        }

        _oldContentInlines = null;
        _newContentInlines = null;
        _contentInlines = null;
        OnPropertyChanged(nameof(OldContentInlines));
        OnPropertyChanged(nameof(NewContentInlines));
        OnPropertyChanged(nameof(ContentInlines));

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
