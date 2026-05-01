using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Media;
using Elmish.Glue.Core;
using GitKay.Core;

namespace GitKay.UI;

public partial class CommitProjection : ObservableObject, IProjection<Graph.CommitGraphInfo>, IDispatchTarget<GitKay.Core.App.Msg>
{
    private System.Action<GitKay.Core.App.Msg>? _dispatch;

    public void SetDispatch(System.Action<GitKay.Core.App.Msg> dispatch) => _dispatch = dispatch;

    [RelayCommand] private void CreateTag() => _dispatch?.Invoke(GitKay.Core.App.Msg.NewCreateTag(FullHash, "new-tag"));
    [RelayCommand] private void CreateBranch() => _dispatch?.Invoke(GitKay.Core.App.Msg.NewCreateBranch(FullHash, "new-branch"));
    [RelayCommand] private void CherryPick() => _dispatch?.Invoke(GitKay.Core.App.Msg.NewCherryPick(FullHash));
    [RelayCommand] private void ResetSoft() => _dispatch?.Invoke(GitKay.Core.App.Msg.NewResetTo(FullHash, false));
    [RelayCommand] private void ResetHard() => _dispatch?.Invoke(GitKay.Core.App.Msg.NewResetTo(FullHash, true));
    [RelayCommand] private void Revert() => _dispatch?.Invoke(GitKay.Core.App.Msg.NewRevert(FullHash));

    [ObservableProperty] private string _fullHash = "";
    [ObservableProperty] private string _hash = "";
    [ObservableProperty] private string _subject = "";
    [ObservableProperty] private string _author = "";
    [ObservableProperty] private string _date = "";
    [ObservableProperty] private string _refsSummary = "";
    [ObservableProperty] private bool _hasRefs;
    [ObservableProperty] private bool _hasSearchMatch;
    [ObservableProperty] private string _searchMatchSummary = "";
    [ObservableProperty] private IBrush _rowBackground = Brushes.Transparent;
    [ObservableProperty] private bool _hasRefBadges;
    [ObservableProperty] private bool _hasSecondarySummary;
    [ObservableProperty] private string _secondarySummary = "";
    [ObservableProperty] private int _lane = 0;

    public ObservableCollection<SegmentProjection> Segments { get; } = new();
    public ObservableCollection<CommitRefProjection> RefBadges { get; } = new();

    public void Update(Graph.CommitGraphInfo info)
    {
        var commit = info.Commit;
        var shortHashLength = System.Math.Min(8, commit.Hash.Length);
        FullHash = commit.Hash;
        Hash = commit.Hash.Substring(0, shortHashLength);
        Subject = commit.Subject;
        Author = commit.AuthorName;
        Date = System.DateTimeOffset.FromUnixTimeSeconds(commit.Timestamp).LocalDateTime.ToString("yyyy-MM-dd HH:mm");
        HasRefs = commit.Refs.Any();
        RefsSummary = HasRefs ? FormatRefsSummary(commit.Refs) : "";
        UpdateRefBadges(commit.Refs);
        Lane = info.Lane;
        UpdateSecondarySummary();

        Segments.SyncWith(
            info.Segments,
            m => $"{m.Lane}-{m.TargetLane}-{m.IsCommit}",
            vm => vm.Key,
            _ => new SegmentProjection()
        );
    }

    public void ApplySearchMatch(GitKay.Core.GitService.SearchResult? result)
    {
        HasSearchMatch = result != null;
        SearchMatchSummary = result?.MatchSummary ?? "";
        RowBackground =
            result != null
                ? new SolidColorBrush(Color.FromArgb(28, 78, 201, 176))
                : Brushes.Transparent;
        UpdateSecondarySummary();
    }

    private void UpdateSecondarySummary()
    {
        var parts = new System.Collections.Generic.List<string>();

        if (!string.IsNullOrWhiteSpace(SearchMatchSummary))
        {
            parts.Add(SearchMatchSummary);
        }

        SecondarySummary = string.Join(" · ", parts);
        HasSecondarySummary = !string.IsNullOrWhiteSpace(SecondarySummary);
    }

    private void UpdateRefBadges(System.Collections.Generic.IEnumerable<GitKay.Core.Models.CommitRef> refs)
    {
        RefBadges.Clear();

        foreach (var badge in refs
                     .OrderBy(refItem => (int)refItem.Kind)
                     .ThenBy(refItem => refItem.Name, StringComparer.OrdinalIgnoreCase)
                     .Select(CreateRefBadge))
        {
            RefBadges.Add(badge);
        }

        HasRefBadges = RefBadges.Count > 0;
    }

    private static string FormatRefsSummary(System.Collections.Generic.IEnumerable<GitKay.Core.Models.CommitRef> refs)
    {
        var visibleRefs = refs.Select(reference => reference.Name).Take(3).ToArray();
        var summary = string.Join(" · ", visibleRefs);

        var totalCount = refs.Count();

        if (totalCount > visibleRefs.Length)
        {
            var remainingCount = totalCount - visibleRefs.Length;
            summary = string.IsNullOrEmpty(summary) ? $"+{remainingCount}" : $"{summary} +{remainingCount}";
        }

        return summary;
    }

    private static CommitRefProjection CreateRefBadge(GitKay.Core.Models.CommitRef reference)
    {
        return reference.Kind switch
        {
            GitKay.Core.Models.CommitRefKind.Branch => new CommitRefProjection(reference.Name, CommitRefKind.Branch),
            GitKay.Core.Models.CommitRefKind.Remote => new CommitRefProjection(reference.Name, CommitRefKind.Remote),
            GitKay.Core.Models.CommitRefKind.Tag => new CommitRefProjection(reference.Name, CommitRefKind.Tag),
            GitKay.Core.Models.CommitRefKind.Stash => new CommitRefProjection(reference.Name, CommitRefKind.Stash),
            _ => new CommitRefProjection(reference.Name, CommitRefKind.Branch)
        };
    }
}

public enum CommitRefKind
{
    Branch = 0,
    Remote = 1,
    Tag = 2,
    Stash = 3,
}

public sealed class CommitRefProjection
{
    private static readonly IBrush BranchBackground = new SolidColorBrush(Color.FromRgb(0x23, 0x4b, 0x2f));
    private static readonly IBrush BranchBorder = new SolidColorBrush(Color.FromRgb(0x5f, 0x9b, 0x6b));
    private static readonly IBrush BranchForeground = new SolidColorBrush(Color.FromRgb(0xe2, 0xf4, 0xe4));
    private static readonly IBrush RemoteBackground = new SolidColorBrush(Color.FromRgb(0x1c, 0x3a, 0x54));
    private static readonly IBrush RemoteBorder = new SolidColorBrush(Color.FromRgb(0x5a, 0x90, 0xc2));
    private static readonly IBrush RemoteForeground = new SolidColorBrush(Color.FromRgb(0xd9, 0xec, 0xff));
    private static readonly IBrush TagBackground = new SolidColorBrush(Color.FromRgb(0x4a, 0x3c, 0x14));
    private static readonly IBrush TagBorder = new SolidColorBrush(Color.FromRgb(0xc8, 0xa9, 0x5f));
    private static readonly IBrush TagForeground = new SolidColorBrush(Color.FromRgb(0xff, 0xf1, 0xc9));
    private static readonly IBrush StashBackground = new SolidColorBrush(Color.FromRgb(0x3d, 0x36, 0x50));
    private static readonly IBrush StashBorder = new SolidColorBrush(Color.FromRgb(0x8d, 0x7d, 0xb5));
    private static readonly IBrush StashForeground = new SolidColorBrush(Color.FromRgb(0xe9, 0xe4, 0xff));

    public CommitRefProjection(string text, CommitRefKind kind)
    {
        Text = text;
        Kind = kind;

        var styles = kind switch
        {
            CommitRefKind.Branch => (BranchBackground, BranchBorder, BranchForeground),
            CommitRefKind.Remote => (RemoteBackground, RemoteBorder, RemoteForeground),
            CommitRefKind.Tag => (TagBackground, TagBorder, TagForeground),
            CommitRefKind.Stash => (StashBackground, StashBorder, StashForeground),
            _ => (BranchBackground, BranchBorder, BranchForeground)
        };

        Background = styles.Item1;
        BorderBrush = styles.Item2;
        Foreground = styles.Item3;
    }

    public string Text { get; }
    public CommitRefKind Kind { get; }
    public IBrush Background { get; }
    public IBrush BorderBrush { get; }
    public IBrush Foreground { get; }
}

public partial class SegmentProjection : ObservableObject, IProjection<Graph.LaneSegment>
{
    [ObservableProperty] private int _lane;
    [ObservableProperty] private int _targetLane;
    [ObservableProperty] private bool _isCommit;
    [ObservableProperty] private int _color;
    [ObservableProperty] private string _key = "";

    public void Update(Graph.LaneSegment segment)
    {
        Lane = segment.Lane;
        TargetLane = segment.TargetLane;
        IsCommit = segment.IsCommit;
        Color = segment.Color;
        Key = $"{segment.Lane}-{segment.TargetLane}-{segment.IsCommit}";
    }
}
