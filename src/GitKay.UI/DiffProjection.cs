using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Media;
using DiffLineType = GitKay.Core.Models.LineType;

namespace GitKay.UI;

public readonly record struct DiffFileKey(string OldPath, string NewPath);

public interface IDiffRowProjection
{
}

public sealed class DiffFileProjection
{
    public DiffFileProjection(GitKay.Core.GitService.DiffFileSummary summary)
    {
        Key = new DiffFileKey(summary.OldPath, summary.NewPath);
        DisplayPath = summary.DisplayPath;
        Header = new DiffFileHeaderProjection(this);
    }

    public DiffFileKey Key { get; }
    public string DisplayPath { get; private set; }
    public bool IsLoaded { get; private set; }
    public ObservableCollection<DiffHunkProjection> Hunks { get; } = new();
    public DiffFileHeaderProjection Header { get; }

    public void UpdateSummary(GitKay.Core.GitService.DiffFileSummary summary)
    {
        DisplayPath = summary.DisplayPath;
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
}

public sealed class DiffFileHeaderProjection : IDiffRowProjection
{
    public DiffFileHeaderProjection(DiffFileProjection file)
    {
        File = file;
        DisplayPath = file.DisplayPath;
    }

    public DiffFileProjection File { get; }
    public string DisplayPath { get; }
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

public sealed class DiffLineProjection : IDiffRowProjection
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
