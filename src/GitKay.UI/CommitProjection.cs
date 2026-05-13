using System;
using System.Collections.Generic;
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
    [ObservableProperty] private bool _hasHashMatch;
    [ObservableProperty] private bool _hasSubjectMatch;
    [ObservableProperty] private bool _hasAuthorMatch;
    [ObservableProperty] private bool _hasRefMatch;
    [ObservableProperty] private int _refMatchCount;
    [ObservableProperty] private bool _hasDiffMatch;
    [ObservableProperty] private int _pathMatchCount;
    [ObservableProperty] private bool _hasSecondarySummary;
    [ObservableProperty] private string _secondarySummary = "";
    [ObservableProperty] private int _lane = 0;
    [ObservableProperty] private bool _showBranchRefs = false;
    [ObservableProperty] private bool _showStashes = false;

    public ObservableCollection<SegmentProjection> Segments { get; } = new();
    public ObservableCollection<CommitRefProjection> RefBadges { get; } = new();
    private GitKay.Core.Models.CommitRef[] _refs = Array.Empty<GitKay.Core.Models.CommitRef>();

    public void Update(Graph.CommitGraphInfo info)
    {
        var commit = info.Commit;
        var shortHashLength = System.Math.Min(8, commit.Hash.Length);
        FullHash = commit.Hash;
        Hash = commit.Hash.Substring(0, shortHashLength);
        Subject = commit.Subject;
        Author = commit.AuthorName;
        Date = System.DateTimeOffset.FromUnixTimeSeconds(commit.Timestamp).LocalDateTime.ToString("yyyy-MM-dd HH:mm");
        _refs = commit.Refs.ToArray();
        HasRefs = _refs.Any();
        RefsSummary = HasRefs ? FormatRefsSummary(_refs) : "";
        UpdateRefBadges();
        Lane = info.Lane;

        Segments.SyncWith(
            info.Segments,
            m => $"{m.Lane}-{m.TargetLane}-{m.IsCommit}",
            vm => vm.Key,
            segment =>
            {
                var projection = new SegmentProjection();
                projection.Update(segment);
                return projection;
            }
        );
    }

    public void ApplySearchMatch(GitKay.Core.GitService.SearchResult? result)
    {
        HasSearchMatch = result != null;
        SearchMatchSummary = result?.MatchSummary ?? "";

        if (result != null)
        {
            var kinds = new HashSet<string>(result.MatchKinds, StringComparer.OrdinalIgnoreCase);
            HasHashMatch = kinds.Contains("hash");
            HasSubjectMatch = kinds.Contains("message");
            HasAuthorMatch = kinds.Contains("author");
            HasRefMatch = kinds.Contains("ref");
            RefMatchCount = result.MatchedRefs.Length;
            HasDiffMatch = kinds.Contains("path") || kinds.Contains("text");
            PathMatchCount = result.MatchedPaths.Length;
        }
        else
        {
            HasHashMatch = false;
            HasSubjectMatch = false;
            HasAuthorMatch = false;
            HasRefMatch = false;
            RefMatchCount = 0;
            HasDiffMatch = false;
            PathMatchCount = 0;
        }

        RowBackground =
            result != null
                ? new SolidColorBrush(Color.FromArgb(28, 78, 201, 176))
                : Brushes.Transparent;
    }

    partial void OnShowBranchRefsChanged(bool value) => UpdateRefBadges();
    partial void OnShowStashesChanged(bool value) => UpdateRefBadges();

    private void UpdateRefBadges()
    {
        RefBadges.Clear();

        foreach (var badge in _refs
                     .Where(ShouldDisplayRef)
                     .OrderBy(refItem => refItem.Kind == GitKay.Core.Models.CommitRefKind.Tag ? 0 : 1)
                     .ThenBy(refItem => refItem.IsCurrentHead ? 0 : 1)
                     .ThenBy(refItem => (int)refItem.Kind)
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

    private bool ShouldDisplayRef(GitKay.Core.Models.CommitRef reference)
    {
        return reference.Kind switch
        {
            GitKay.Core.Models.CommitRefKind.Tag => true,
            GitKay.Core.Models.CommitRefKind.Stash => ShowStashes,
            GitKay.Core.Models.CommitRefKind.Branch => ShowBranchRefs || reference.IsCurrentHead,
            GitKay.Core.Models.CommitRefKind.Remote => true,
            _ => true
        };
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
    private static readonly IBrush BranchBackground = new SolidColorBrush(Color.FromRgb(0x00, 0xff, 0x00));
    private static readonly IBrush BranchBorder = Brushes.Black;
    private static readonly IBrush BranchForeground = Brushes.Black;
    private static readonly IBrush RemoteBackground = new SolidColorBrush(Color.FromRgb(0xff, 0xdd, 0xaa));
    private static readonly IBrush RemoteBorder = Brushes.Black;
    private static readonly IBrush RemoteForeground = Brushes.Black;
    private static readonly IBrush TagBackground = Brushes.Yellow;
    private static readonly IBrush TagBorder = Brushes.Black;
    private static readonly IBrush TagForeground = Brushes.Black;
    private static readonly IBrush StashBackground = new SolidColorBrush(Color.FromRgb(0xee, 0xee, 0xee));
    private static readonly IBrush StashBorder = Brushes.Black;
    private static readonly IBrush StashForeground = Brushes.Black;

    public CommitRefProjection(string text, CommitRefKind kind)
    {
        Text = text;
        Kind = kind;
        IsCurrentHead = false;

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
    public bool IsCurrentHead { get; }
    public bool IsTag => Kind == CommitRefKind.Tag;
    public bool IsNotTag => !IsTag;
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
