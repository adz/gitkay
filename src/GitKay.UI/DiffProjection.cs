using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Media;

namespace GitKay.UI;

public sealed class DiffFileProjection
{
    public DiffFileProjection(GitKay.Core.Models.FileDiff file)
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
    public DiffHunkProjection(GitKay.Core.Models.DiffHunk hunk)
    {
        Header = hunk.Header;
        Lines = new ObservableCollection<DiffLineProjection>(hunk.Lines.Select(line => new DiffLineProjection(line)));
    }

    public string Header { get; }
    public ObservableCollection<DiffLineProjection> Lines { get; }
}

public sealed class DiffLineProjection
{
    public DiffLineProjection(GitKay.Core.Models.DiffLine line)
    {
        var lineType = line.Type.ToString();
        OldLineNoText = line.OldLineNo?.ToString() ?? "";
        NewLineNoText = line.NewLineNo?.ToString() ?? "";
        Prefix = lineType switch
        {
            "Added" => "+",
            "Removed" => "-",
            _ => " "
        };
        Content = line.Content;
        Foreground = lineType switch
        {
            "Added" => Brushes.LightGreen,
            "Removed" => Brushes.IndianRed,
            "Context" => Brushes.Gainsboro,
            _ => Brushes.LightSkyBlue
        };
    }

    public string OldLineNoText { get; }
    public string NewLineNoText { get; }
    public string Prefix { get; }
    public string Content { get; }
    public IBrush Foreground { get; }
}
