using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Media;
using DiffLineType = GitKay.Core.Models.LineType;

namespace GitKay.UI;

public interface IDiffRowProjection
{
}

public sealed class DiffFileProjection
{
    public DiffFileProjection(GitKay.Core.Models.FileDiff file)
    {
        OldPath = file.OldPath;
        NewPath = file.NewPath;
        DisplayPath = DiffFormatting.BuildDisplayPath(file.OldPath, file.NewPath);
        Hunks = new ObservableCollection<DiffHunkProjection>(file.Hunks.Select(h => new DiffHunkProjection(h)));
    }

    public string OldPath { get; }
    public string NewPath { get; }
    public string DisplayPath { get; }
    public ObservableCollection<DiffHunkProjection> Hunks { get; }
}

public sealed class DiffFileHeaderProjection : IDiffRowProjection
{
    public DiffFileHeaderProjection(DiffFileProjection file)
    {
        DisplayPath = file.DisplayPath;
    }

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
