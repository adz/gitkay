using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using DiffLineType = GitKay.Core.Models.LineType;

namespace GitKay.UI;

public readonly record struct DiffFileKey(string OldPath, string NewPath);

public interface IDiffRowProjection {
}

internal static class DiffSearchPresentation {
    public static readonly IBrush MatchBackground = new SolidColorBrush(Color.FromArgb(34, 215, 186, 125));
    public static readonly IBrush MatchBorderBrush = new SolidColorBrush(Color.FromArgb(220, 215, 186, 125));
    public static readonly IBrush MatchForeground = new SolidColorBrush(Color.FromRgb(215, 186, 125));
    public static readonly FontWeight MatchFontWeight = FontWeight.SemiBold;

    public static (string Prefix, string Match, string Suffix, bool HasMatch) Split(string value, string query) {
        if (string.IsNullOrWhiteSpace(query)) {
            return (value, "", "", false);
        }

        var index = value.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (index < 0) {
            return (value, "", "", false);
        }

        var prefix = index > 0 ? value[..index] : "";
        var match = value.Substring(index, query.Length);
        var suffix = index + query.Length < value.Length ? value[(index + query.Length)..] : "";
        return (prefix, match, suffix, true);
    }
}

public partial class DiffFileProjection : ObservableObject {
    public DiffFileProjection(GitKay.Core.GitService.DiffFileSummary summary) {
        Key = new DiffFileKey(summary.OldPath, summary.NewPath);
        DisplayPath = summary.DisplayPath;
        ListLabel = summary.DisplayPath;
        Header = new DiffFileHeaderProjection(this);
    }

    public DiffFileKey Key { get; }
    [ObservableProperty] private string _displayPath = "";
    [ObservableProperty] private bool _isLoaded;
    /// <summary>Presentation-only: hides this file's diff rows beneath its header.</summary>
    [ObservableProperty] private bool _isCollapsed;
    /// <summary>Label and indent for the changed-files list: full path in patch mode, file name in tree mode.</summary>
    [ObservableProperty] private string _listLabel = "";
    /// <summary>The commit search's path term matches this file; shown as a dotted underline.</summary>
    [ObservableProperty] private bool _isPathSearchMatch;
    [ObservableProperty] private Avalonia.Thickness _listIndent;
    [ObservableProperty] private int _addedLines;
    [ObservableProperty] private int _removedLines;
    /// <summary>"added", "deleted", "renamed" or "modified", derived from the file identity.</summary>
    public string ChangeKind => GitKay.Core.FileChange.kindName(Change);
    public string ChangeGlyph => GitKay.Core.FileChange.glyph(Change);
    private GitKay.Core.FileChange.Kind Change => GitKay.Core.FileChange.kind(Key.OldPath, Key.NewPath);
    public string ChangeToolTip => IsLoaded
        ? $"{char.ToUpperInvariant(ChangeKind[0])}{ChangeKind[1..]} · +{AddedLines} −{RemovedLines}"
        : $"{char.ToUpperInvariant(ChangeKind[0])}{ChangeKind[1..]}";
    public bool IsAddedFile => Change.IsAdded;
    public bool IsDeletedFile => Change.IsDeleted;
    public bool IsModifiedFile => Change.IsModified || Change.IsRenamed;
    /// <summary>True when collapsed context remains that "expand all" can reveal.</summary>
    public bool HasHiddenContext => _blocks.Any(block => block is DiffGapProjection);
    /// <summary>True when context beyond the configured diff has been revealed and can be collapsed.</summary>
    public bool HasRevealedContext => _expansion != null && !_expansion.Revealed.IsEmpty;
    public bool IsContextLoading => _expansion?.PendingRequestId != null;
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
    /// <summary>Ordered hunks and collapsed gaps, after applying file-scoped expansion.</summary>
    public IReadOnlyList<object> Blocks => _blocks;
    public DiffFileHeaderProjection Header { get; }

    private readonly List<object> _blocks = new();
    private GitKay.Core.Models.FileDiff? _content;
    private GitKay.Core.App.FileExpansion? _expansion;

    public void UpdateSummary(GitKay.Core.GitService.DiffFileSummary summary) {
        DisplayPath = summary.DisplayPath;
        Header.UpdateDisplayPath(summary.DisplayPath);
    }

    public void ApplyContent(GitKay.Core.Models.FileDiff file, GitKay.Core.App.FileExpansion? expansion = null) {
        _content = file;
        var lines = file.Hunks.SelectMany(hunk => hunk.Lines).ToArray();
        AddedLines = lines.Count(line => line.Type.IsAdded);
        RemovedLines = lines.Count(line => line.Type.IsRemoved);
        _expansion = expansion;
        Project();
        IsLoaded = true;
        OnPropertyChanged(nameof(ChangeToolTip));
    }

    /// <summary>Re-projects this file for new expansion state. Returns false when nothing changed.</summary>
    public bool ApplyExpansion(GitKay.Core.App.FileExpansion? expansion) {
        if (ReferenceEquals(_expansion, expansion) || Equals(_expansion, expansion)) {
            return false;
        }

        _expansion = expansion;
        if (_content != null) {
            Project();
        }

        return true;
    }

    public void ClearContent() {
        _content = null;
        _expansion = null;
        AddedLines = 0;
        RemovedLines = 0;
        _blocks.Clear();
        Hunks.Clear();
        IsLoaded = false;
        ClearSearchState();
    }

    private void Project() {
        _blocks.Clear();
        Hunks.Clear();
        if (_content == null) {
            return;
        }

        var fullContext = _expansion?.FullContext;
        var revealed = _expansion?.Revealed ?? FSharpList<GitKay.Core.DiffExpansion.LineRange>.Empty;
        var isLoading = _expansion?.PendingRequestId != null;
        foreach (var block in GitKay.Core.DiffExpansion.project(_content, fullContext, revealed)) {
            switch (block) {
                case GitKay.Core.DiffExpansion.DiffBlock.HunkBlock hunkBlock:
                    var hunk = new DiffHunkProjection(hunkBlock.Item);
                    Hunks.Add(hunk);
                    _blocks.Add(hunk);
                    break;
                case GitKay.Core.DiffExpansion.DiffBlock.GapBlock gapBlock:
                    _blocks.Add(new DiffGapProjection(gapBlock.Item, isLoading));
                    break;
            }
        }
    }

    public void ApplySearchState(string query, string scopeKey) {
        var normalizedQuery = query.Trim();
        if (string.IsNullOrWhiteSpace(normalizedQuery)) {
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
        if (searchText && IsLoaded) {
            foreach (var hunk in Hunks) {
                foreach (var line in hunk.Lines) {
                    textMatch |= line.ApplySearchState(normalizedQuery, true);
                }
            }
        }
        else {
            foreach (var hunk in Hunks) {
                foreach (var line in hunk.Lines) {
                    line.ApplySearchState(normalizedQuery, false);
                }
            }
        }

        var hasSearchMatch = pathMatch || textMatch;
        HasSearchMatch = hasSearchMatch;
        IsPathSearchMatch = pathMatch;
        SearchMatchSummary = hasSearchMatch ? BuildSearchSummary(pathMatch, textMatch) : "";
        UpdateSearchHighlight(normalizedQuery, pathMatch, hasSearchMatch);
        Header.ApplySearchState(normalizedQuery, pathMatch, textMatch, SearchMatchSummary);
    }

    private void ClearSearchState(string scopeKey) {
        HasSearchMatch = false;
        IsPathSearchMatch = false;
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
        foreach (var hunk in Hunks) {
            foreach (var line in hunk.Lines) {
                line.ApplySearchState("", searchText);
            }
        }
    }

    private void ClearSearchState() {
        ClearSearchState("all");
    }

    private void UpdateSearchHighlight(string query, bool pathMatch, bool hasSearchMatch) {
        RowBackground = hasSearchMatch
            ? DiffSearchPresentation.MatchBackground
            : Brushes.Transparent;
        BorderBrush = hasSearchMatch
            ? DiffSearchPresentation.MatchBorderBrush
            : Brushes.Transparent;

        if (pathMatch) {
            var (prefix, match, suffix, _) = DiffSearchPresentation.Split(DisplayPath, query);
            MatchPrefix = prefix;
            MatchText = match;
            MatchSuffix = suffix;
            MatchForeground = DiffSearchPresentation.MatchForeground;
            MatchFontWeight = DiffSearchPresentation.MatchFontWeight;
        }
        else {
            MatchPrefix = DisplayPath;
            MatchText = "";
            MatchSuffix = "";
            MatchForeground = DiffSearchPresentation.MatchForeground;
            MatchFontWeight = FontWeight.Normal;
        }
    }

    private static bool ContainsIgnoreCase(string value, string query) =>
        value.Contains(query, StringComparison.OrdinalIgnoreCase);

    private static string BuildSearchSummary(bool pathMatch, bool textMatch) {
        var parts = new System.Collections.Generic.List<string>();

        if (pathMatch) {
            parts.Add("path");
        }

        if (textMatch) {
            parts.Add("text");
        }

        return string.Join(" · ", parts);
    }
}

/// <summary>A folder row in the changed-files tree; single-child folder chains are merged into one row.</summary>
public sealed partial class DiffFileFolderRow : ObservableObject {
    public DiffFileFolderRow(string name, string path, int depth, bool isExpanded) {
        Name = name;
        Path = path;
        Indent = new Avalonia.Thickness(depth * DiffFileTree.IndentWidth, 0, 0, 0);
        _isExpanded = isExpanded;
    }

    public string Name { get; }
    public string Path { get; }
    public Avalonia.Thickness Indent { get; }
    [ObservableProperty] private bool _isExpanded;

    /// <summary>Changed files anywhere beneath this folder.</summary>
    public List<DiffFileProjection> Files { get; } = new();
    [ObservableProperty] private string _addedText = "";
    [ObservableProperty] private string _removedText = "";
    [ObservableProperty] private bool _hasChanges;

    /// <summary>Sums the loaded files' added and removed lines.</summary>
    public void RefreshTotals() {
        var loaded = Files.Where(file => file.IsLoaded).ToList();
        var added = loaded.Sum(file => file.AddedLines);
        var removed = loaded.Sum(file => file.RemovedLines);
        HasChanges = loaded.Count > 0 && added + removed > 0;
        AddedText = added > 0 ? $"+{added}" : "";
        RemovedText = removed > 0 ? $"−{removed}" : "";
    }
}

/// <summary>An unchanged file in the "All files" tree; it has no diff, so it opens as a whole file.</summary>
public sealed class RepoFileRow {
    public RepoFileRow(string path, string name, int depth) {
        Path = path;
        Name = name;
        Indent = new Avalonia.Thickness(depth * DiffFileTree.IndentWidth, 0, 0, 0);
    }

    public string Path { get; }
    public string Name { get; }
    public Avalonia.Thickness Indent { get; }
}

/// <summary>Builds the flattened file-list rows for patch (flat), tree (folder) and all-files modes.</summary>
public static class DiffFileTree {
    public const double IndentWidth = 14;

    private sealed class Node {
        public readonly SortedDictionary<string, Node> Folders = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<(string Name, string Path, DiffFileProjection? File)> Files = new();
        public bool HasChange;
    }

    public static string PathOf(DiffFileProjection file) => file.Key.NewPath == "/dev/null" ? file.Key.OldPath : file.Key.NewPath;

    public static List<object> BuildRows(IEnumerable<DiffFileProjection> files, bool treeMode, ISet<string> collapsedFolders) {
        var rows = new List<object>();
        if (!treeMode) {
            foreach (var file in files) {
                file.ListLabel = file.DisplayPath;
                file.ListIndent = default;
                rows.Add(file);
            }
            return rows;
        }

        return BuildTree(files, null, path => !collapsedFolders.Contains(path));
    }

    /// <summary>
    /// Every file in the commit's tree, with changed files in place. Folders holding changes start expanded and
    /// the rest collapsed; <paramref name="toggledFolders"/> flips that default.
    /// </summary>
    public static List<object> BuildAllFilesRows(IEnumerable<DiffFileProjection> files, IEnumerable<string> allPaths, ISet<string> toggledFolders) =>
        BuildTree(files, allPaths, null, toggledFolders);

    private static List<object> BuildTree(IEnumerable<DiffFileProjection> files, IEnumerable<string>? allPaths, Func<string, bool>? isExpanded, ISet<string>? toggledFolders = null) {
        var rows = new List<object>();
        var root = new Node();

        void Add(string path, DiffFileProjection? file) {
            var parts = path.Split('/');
            var node = root;
            if (file != null) node.HasChange = true;
            for (var i = 0; i < parts.Length - 1; i++) {
                if (!node.Folders.TryGetValue(parts[i], out var child))
                    node.Folders[parts[i]] = child = new Node();
                node = child;
                if (file != null) node.HasChange = true;
            }
            node.Files.Add((parts[^1], path, file));
        }

        var changedPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files) {
            var path = PathOf(file);
            changedPaths.Add(path);
            Add(path, file);
        }

        if (allPaths != null)
            foreach (var path in allPaths)
                if (!changedPaths.Contains(path)) Add(path, null);

        void Emit(Node node, string prefix, int depth) {
            foreach (var (folderName, folder) in node.Folders) {
                // Merge chains of folders that contain only one folder, like GitHub ("dev-docs/releases").
                var name = folderName;
                var current = folder;
                while (current.Files.Count == 0 && current.Folders.Count == 1) {
                    var only = current.Folders.First();
                    name = $"{name}/{only.Key}";
                    current = only.Value;
                }

                var path = prefix.Length == 0 ? name : $"{prefix}/{name}";
                var expanded = isExpanded?.Invoke(path) ?? current.HasChange != (toggledFolders?.Contains(path) ?? false);
                var folderRow = new DiffFileFolderRow(name, path, depth, expanded);
                folderRow.Files.AddRange(ChangedFilesUnder(current));
                folderRow.RefreshTotals();
                rows.Add(folderRow);
                if (expanded) Emit(current, path, depth + 1);
            }

            foreach (var (name, path, file) in node.Files.OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)) {
                if (file == null) {
                    rows.Add(new RepoFileRow(path, name, depth));
                    continue;
                }

                file.ListLabel = name;
                file.ListIndent = new Avalonia.Thickness(depth * IndentWidth, 0, 0, 0);
                rows.Add(file);
            }
        }

        Emit(root, "", 0);
        return rows;
    }

    private static IEnumerable<DiffFileProjection> ChangedFilesUnder(Node node) {
        foreach (var (_, _, file) in node.Files)
            if (file != null) yield return file;
        foreach (var child in node.Folders.Values)
            foreach (var file in ChangedFilesUnder(child))
                yield return file;
    }
}

public sealed partial class DiffFileHeaderProjection : ObservableObject, IDiffRowProjection {
    public DiffFileHeaderProjection(DiffFileProjection file) {
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

    public void ApplySearchState(string query, bool pathMatch, bool textMatch, string searchMatchSummary) {
        HasSearchMatch = pathMatch || textMatch;
        SearchMatchSummary = HasSearchMatch ? searchMatchSummary : "";
        RowBackground = HasSearchMatch
            ? DiffSearchPresentation.MatchBackground
            : Brushes.Transparent;
        BorderBrush = HasSearchMatch
            ? DiffSearchPresentation.MatchBorderBrush
            : Brushes.Transparent;

        if (pathMatch) {
            var (prefix, match, suffix, _) = DiffSearchPresentation.Split(DisplayPath, query);
            MatchPrefix = prefix;
            MatchText = match;
            MatchSuffix = suffix;
            MatchForeground = DiffSearchPresentation.MatchForeground;
            MatchFontWeight = DiffSearchPresentation.MatchFontWeight;
        }
        else {
            MatchPrefix = DisplayPath;
            MatchText = "";
            MatchSuffix = "";
            MatchForeground = DiffSearchPresentation.MatchForeground;
            MatchFontWeight = FontWeight.Normal;
        }
    }

    public void ClearSearchState() {
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

/// <summary>Flattens a file's header, hunks and gaps into diff surface rows for a presentation mode.</summary>
public static class DiffRowBuilder {
    public static void AppendFile(List<IDiffRowProjection> rows, DiffFileProjection file, string mode) {
        rows.Add(file.Header);
        if (!file.IsLoaded || file.IsCollapsed) return;

        foreach (var block in file.Blocks) {
            if (block is DiffGapProjection gap) {
                gap.HeaderText = null;
                rows.Add(gap);
                continue;
            }

            if (block is not DiffHunkProjection hunk) continue;

            if (mode == "side-by-side") {
                AddHunkHeader(rows, hunk);
                AddSideBySideLines(rows, hunk.Lines);
                continue;
            }

            var lines = mode switch {
                "new" => hunk.Lines.Where(line => !line.IsRemoved).ToArray(),
                "old" => hunk.Lines.Where(line => !line.IsAdded).ToArray(),
                _ => hunk.Lines.ToArray(),
            };
            if (lines.Length == 0) continue;

            AddHunkHeader(rows, hunk);
            rows.AddRange(lines);
        }
    }

    private static void AddHunkHeader(List<IDiffRowProjection> rows, DiffHunkProjection hunk) {
        if (rows.Count > 0 && rows[^1] is DiffGapProjection gap)
            gap.HeaderText = hunk.Header;
        else
            rows.Add(new DiffHunkHeaderProjection(hunk));
    }

    private static void AddSideBySideLines(List<IDiffRowProjection> target, IList<DiffLineProjection> lines) {
        var index = 0;
        while (index < lines.Count) {
            var line = lines[index];
            if (line.IsRemoved && index + 1 < lines.Count && lines[index + 1].IsAdded) {
                target.Add(DiffLineProjection.CreateSideBySidePair(line, lines[index + 1]));
                index += 2;
                continue;
            }

            target.Add(line);
            index++;
        }
    }
}

public readonly record struct DiffGapExpansionRequest(
    GitKay.Core.DiffExpansion.DiffGap Gap,
    GitKay.Core.DiffExpansion.ExpandDirection Direction);

public sealed class DiffGapProjection : IDiffRowProjection {
    public DiffGapProjection(GitKay.Core.DiffExpansion.DiffGap gap, bool isLoading = false) {
        Gap = gap;
        IsLoading = isLoading;
        Directions = GitKay.Core.DiffExpansion.availableDirections(gap).ToArray();
    }

    public GitKay.Core.DiffExpansion.DiffGap Gap { get; }
    public bool IsLoading { get; }
    /// <summary>Header of the hunk that follows this gap; the gap row stands in for that header row.</summary>
    public string? HeaderText { get; set; }
    public int? HiddenLineCount => Gap.HiddenCount is null ? null : Gap.HiddenCount.Value;
    public IReadOnlyList<GitKay.Core.DiffExpansion.ExpandDirection> Directions { get; }

    public string Label => HiddenLineCount is { } count ? $"⋯  {count} hidden lines" : "⋯  more lines";

    public string ActionLabel(GitKay.Core.DiffExpansion.ExpandDirection direction) {
        if (direction.IsDown) return $"↓  Show {GitKay.Core.DiffExpansion.StepLines} lines";
        if (direction.IsUp) return $"↑  Show {GitKay.Core.DiffExpansion.StepLines} lines";
        return HiddenLineCount is { } count ? $"↕  Show all {count}" : "↕  Show all";
    }
}

public sealed class DiffHunkHeaderProjection : IDiffRowProjection {
    private InlineCollection? _headerInlines;

    public DiffHunkHeaderProjection(DiffHunkProjection hunk) {
        Header = hunk.Header;
    }

    public string Header { get; }
    public InlineCollection HeaderInlines => _headerInlines ??= SyntaxHighlighting.BuildHunkHeaderInlines(Header);
}

public sealed class DiffHunkProjection {
    public DiffHunkProjection(GitKay.Core.Models.DiffHunk hunk) {
        Header = hunk.Header;
        Lines = new ObservableCollection<DiffLineProjection>(hunk.Lines.Select(line => new DiffLineProjection(line)));
    }

    public string Header { get; }
    public ObservableCollection<DiffLineProjection> Lines { get; }
}

public partial class DiffLineProjection : ObservableObject, IDiffRowProjection {
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

    public DiffLineProjection(GitKay.Core.Models.DiffLine line) {
        var isAdded = line.Type.Equals(DiffLineType.Added);
        var isRemoved = line.Type.Equals(DiffLineType.Removed);
        var isContext = line.Type.Equals(DiffLineType.Context);

        OldLineNo = line.OldLineNo is null ? null : line.OldLineNo.Value;
        NewLineNo = line.NewLineNo is null ? null : line.NewLineNo.Value;
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

    private DiffLineProjection(DiffLineProjection removedLine, DiffLineProjection addedLine) {
        OldLineNo = removedLine.OldLineNo;
        NewLineNo = addedLine.NewLineNo;
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

    public int? OldLineNo { get; }
    public int? NewLineNo { get; }
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

    private static string FormatLineNumber(FSharpOption<int>? lineNumber) =>
        lineNumber is null ? "" : lineNumber.Value.ToString();

    public bool ApplySearchState(string query, bool searchTextEnabled) {
        var normalizedQuery = query.Trim();
        if (_searchQuery == normalizedQuery && _searchTextEnabled == searchTextEnabled) {
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

        if (isSearchMatch) {
            var (prefix, match, suffix, _) = DiffSearchPresentation.Split(Content, _searchQuery);
            MatchPrefix = prefix;
            MatchText = match;
            MatchSuffix = suffix;
            MatchForeground = DiffSearchPresentation.MatchForeground;
            MatchFontWeight = DiffSearchPresentation.MatchFontWeight;
        }
        else {
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
