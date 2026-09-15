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
        IsPathSearchMatch = false;
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

    /// <summary>Marks the file when an applied commit search's path term matches it.</summary>
    public void ApplyDiffMark(GitKay.Core.GitSearch.DiffMark mark) =>
        IsPathSearchMatch = GitKay.Core.GitSearch.marksPath(mark, Key.OldPath, Key.NewPath, DisplayPath);
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

    public static string PathOf(DiffFileProjection file) => GitKay.Core.FileChange.currentPath(file.Key.OldPath, file.Key.NewPath);

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
        var changed = files.ToList();
        var changedPaths = changed.Select(PathOf).ToHashSet(StringComparer.Ordinal);
        var entries = changed.Select(file => Tuple.Create(PathOf(file), Microsoft.FSharp.Core.FSharpOption<DiffFileProjection>.Some(file)))
            .Concat((allPaths ?? []).Where(path => !changedPaths.Contains(path)).Select(path => Tuple.Create(path, Microsoft.FSharp.Core.FSharpOption<DiffFileProjection>.None)));
        // Folders holding changes start expanded in the all-files tree; toggling flips that. The changes-only tree follows the collapsed set.
        var expanded = Microsoft.FSharp.Core.FuncConvert.FromFunc<string, bool, bool>((path, hasChange) =>
            isExpanded?.Invoke(path) ?? hasChange != (toggledFolders?.Contains(path) ?? false));

        var rows = new List<object>();
        foreach (var row in GitKay.Kit.PathTree.rows(expanded, entries)) {
            switch (row) {
                case GitKay.Kit.PathTreeRow<DiffFileProjection>.FolderRow folder:
                    var folderRow = new DiffFileFolderRow(folder.name, folder.path, folder.depth, folder.expanded);
                    folderRow.Files.AddRange(folder.items);
                    folderRow.RefreshTotals();
                    rows.Add(folderRow);
                    break;
                case GitKay.Kit.PathTreeRow<DiffFileProjection>.FileRow { item: null } unchanged:
                    rows.Add(new RepoFileRow(unchanged.path, unchanged.name, unchanged.depth));
                    break;
                case GitKay.Kit.PathTreeRow<DiffFileProjection>.FileRow file:
                    file.item.Value.ListLabel = file.name;
                    file.item.Value.ListIndent = new Avalonia.Thickness(file.depth * IndentWidth, 0, 0, 0);
                    rows.Add(file.item.Value);
                    break;
            }
        }
        return rows;
    }
}

public sealed partial class DiffFileHeaderProjection : ObservableObject, IDiffRowProjection {
    public DiffFileHeaderProjection(DiffFileProjection file) {
        File = file;
        DisplayPath = file.DisplayPath;
    }

    public DiffFileProjection File { get; }
    [ObservableProperty] private string _displayPath = "";

    public void UpdateDisplayPath(string displayPath) => DisplayPath = displayPath;
}

/// <summary>Flattens a file's header, hunks and gaps into diff surface rows for a presentation mode.</summary>
public static class DiffRowBuilder {
    public static void AppendFile(List<IDiffRowProjection> rows, DiffFileProjection file, GitKay.Core.DiffLayout layout) {
        rows.Add(file.Header);
        if (!file.IsLoaded || file.IsCollapsed) return;

        foreach (var block in file.Blocks) {
            if (block is DiffGapProjection gap) {
                gap.HeaderText = null;
                rows.Add(gap);
                continue;
            }

            if (block is not DiffHunkProjection hunk) continue;

            if (layout.IsSideBySide) {
                AddHunkHeader(rows, hunk);
                AddSideBySideLines(rows, hunk.Lines);
                continue;
            }

            var lines = layout.IsNewFile ? hunk.Lines.Where(line => !line.IsRemoved).ToArray()
                : layout.IsOldFile ? hunk.Lines.Where(line => !line.IsAdded).ToArray()
                : hunk.Lines.ToArray();
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

    public DiffHunkHeaderProjection(DiffHunkProjection hunk) {
        Header = hunk.Header;
    }

    public string Header { get; }
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
    [ObservableProperty] private IBrush _rowBackground = Brushes.Transparent;

    private static string FormatLineNumber(FSharpOption<int>? lineNumber) =>
        lineNumber is null ? "" : lineNumber.Value.ToString();
}
