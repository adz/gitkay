using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Media;

namespace GitKay.UI;

public sealed class DiffFileProjection
{
    public DiffFileProjection(GitKay.Core.Models.BlamedFileDiff file)
    {
        OldPath = file.OldPath;
        NewPath = file.NewPath;
        DisplayPath = BuildDisplayPath(file.OldPath, file.NewPath);
        Hunks = new ObservableCollection<DiffHunkProjection>(file.Hunks.Select(h => new DiffHunkProjection(h)));
    }

    public string OldPath { get; }
    public string NewPath { get; }
    public string DisplayPath { get; }
    public ObservableCollection<DiffHunkProjection> Hunks { get; }

    private static string BuildDisplayPath(string oldPath, string newPath)
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

public sealed class DiffHunkProjection
{
    public DiffHunkProjection(GitKay.Core.Models.BlamedDiffHunk hunk)
    {
        Header = hunk.Header;
        Lines = new ObservableCollection<DiffLineProjection>(hunk.Lines.Select(line => new DiffLineProjection(line)));
    }

    public string Header { get; }
    public ObservableCollection<DiffLineProjection> Lines { get; }
}

public sealed class DiffLineProjection
{
    public DiffLineProjection(GitKay.Core.Models.BlamedDiffLine line)
    {
        var diffLine = line.Line;
        var lineType = diffLine.Type.ToString();
        OldLineNoText = diffLine.OldLineNo?.ToString() ?? "";
        NewLineNoText = diffLine.NewLineNo?.ToString() ?? "";
        Prefix = lineType switch
        {
            "Added" => "+",
            "Removed" => "-",
            _ => " "
        };
        Content = diffLine.Content;
        Foreground = lineType switch
        {
            "Added" => Brushes.LightGreen,
            "Removed" => Brushes.IndianRed,
            "Context" => Brushes.Gainsboro,
            _ => Brushes.LightSkyBlue
        };
        BlameText = line.Blame is { } blame
            ? $"{ShortHash(blame.Value.Hash)} {blame.Value.AuthorName}"
            : "";
    }

    public string OldLineNoText { get; }
    public string NewLineNoText { get; }
    public string Prefix { get; }
    public string Content { get; }
    public IBrush Foreground { get; }
    public string BlameText { get; }

    private static string ShortHash(string hash) => hash.Length > 8 ? hash.Substring(0, 8) : hash;
}
