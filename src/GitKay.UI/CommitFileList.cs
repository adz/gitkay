using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Microsoft.FSharp.Core;

namespace GitKay.UI;

/// <summary>A folder in the commit window's file lists; it holds the changed files anywhere beneath it.</summary>
public sealed class CommitFolderRow(string name, string path, int depth, bool isExpanded, int count) {
    public string Name { get; } = name;
    public string Path { get; } = path;
    public Thickness Indent { get; } = new(depth * CommitFileList.IndentWidth, 0, 0, 0);
    public bool IsExpanded { get; } = isExpanded;
    public string CountText { get; } = count > 0 ? count.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
}

/// <summary>A file of the repository that this list has no change for; only the "All files" mode shows these.</summary>
public sealed class CommitRepoFileRow(string path, string name, int depth) {
    public string Path { get; } = path;
    public string Name { get; } = name;
    public Thickness Indent { get; } = new(depth * CommitFileList.IndentWidth, 0, 0, 0);
}

/// <summary>How the commit window lists files: as full paths, grouped by folder, or against every file in the repository.</summary>
public enum CommitFileListMode { Patch, Tree, All }

/// <summary>
/// Flattens a commit window list into rows, the same three ways the history window's file list offers: patch (full
/// paths), tree (folders), and all (every file of HEAD, with the changed ones in place).
/// </summary>
public static class CommitFileList {
    public const double IndentWidth = 14;

    public static List<object> Build(
        IReadOnlyList<CommitFileRow> files,
        CommitFileListMode mode,
        ISet<string> collapsedFolders,
        IReadOnlyList<string> allPaths) {
        if (mode == CommitFileListMode.Patch) {
            foreach (var file in files) {
                file.ListLabel = file.Label;
                file.ListIndent = default;
            }

            return files.Cast<object>().ToList();
        }

        var changedPaths = files.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
        var entries = files
            .Select(file => Tuple.Create(file.Path, FSharpOption<CommitFileRow>.Some(file)))
            .Concat(mode == CommitFileListMode.All
                ? allPaths.Where(path => !changedPaths.Contains(path)).Select(path => Tuple.Create(path, FSharpOption<CommitFileRow>.None))
                : []);

        // In the tree a folder is open unless it was collapsed; in the all-files tree only folders holding a change
        // start open, and collapsing or expanding one flips that.
        var expanded = FuncConvert.FromFunc<string, bool, bool>((path, hasChange) =>
            mode == CommitFileListMode.All
                ? hasChange != collapsedFolders.Contains(path)
                : !collapsedFolders.Contains(path));

        var rows = new List<object>();
        foreach (var row in GitKay.Kit.PathTree.rows(expanded, entries)) {
            switch (row) {
                case GitKay.Kit.PathTreeRow<CommitFileRow>.FolderRow folder:
                    rows.Add(new CommitFolderRow(folder.name, folder.path, folder.depth, folder.expanded, folder.items.Length));
                    break;
                case GitKay.Kit.PathTreeRow<CommitFileRow>.FileRow { item: null } unchanged:
                    rows.Add(new CommitRepoFileRow(unchanged.path, unchanged.name, unchanged.depth));
                    break;
                case GitKay.Kit.PathTreeRow<CommitFileRow>.FileRow file:
                    file.item.Value.ListLabel = file.name;
                    file.item.Value.ListIndent = new Thickness(file.depth * IndentWidth, 0, 0, 0);
                    rows.Add(file.item.Value);
                    break;
            }
        }

        return rows;
    }
}
