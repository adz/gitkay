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

public partial class CommitProjection : ObservableObject, IProjection<Graph.CommitGraphInfo>, IDispatchTarget<GitKay.Core.App.Msg> {
    private System.Action<GitKay.Core.App.Msg>? _dispatch;

    public void SetDispatch(System.Action<GitKay.Core.App.Msg> dispatch) => _dispatch = dispatch;

    public void CreateTagNamed(string name) => _dispatch?.Invoke(GitKay.Core.App.Msg.NewCreateTag(FullHash, name));
    public void CreateBranchNamed(string name) => _dispatch?.Invoke(GitKay.Core.App.Msg.NewCreateBranch(FullHash, name));
    [RelayCommand] private void CherryPick() => _dispatch?.Invoke(GitKay.Core.App.Msg.NewCherryPick(FullHash));
    [RelayCommand] private void ResetSoft() => _dispatch?.Invoke(GitKay.Core.App.Msg.NewResetTo(FullHash, false));
    [RelayCommand] private void ResetHard() => _dispatch?.Invoke(GitKay.Core.App.Msg.NewResetTo(FullHash, true));
    [RelayCommand] private void Revert() => _dispatch?.Invoke(GitKay.Core.App.Msg.NewRevert(FullHash));

    [ObservableProperty] private string _fullHash = "";
    [ObservableProperty] private string _hash = "";
    [ObservableProperty] private string _subject = "";
    [ObservableProperty] private string _author = "";
    [ObservableProperty] private string _date = "";
    [ObservableProperty] private string _authorEmail = "";
    [ObservableProperty] private string _fullDate = "";
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private string _parents = "";
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
    /// <summary>The commit's branch line colour index, and whether a line from a child leads into it.</summary>
    [ObservableProperty] private int _graphColor;
    [ObservableProperty] private bool _hasIncoming;
    [ObservableProperty] private bool _isMerge;
    /// <summary>The commit HEAD points at, which is the one that can be amended.</summary>
    [ObservableProperty] private bool _isHead;
    [ObservableProperty] private bool _showBranchRefs = false;
    [ObservableProperty] private bool _showStashes = false;
    /// <summary>The "Uncommitted changes" row above the newest commit: no hash, author, date or commit actions.</summary>
    [ObservableProperty] private bool _isWorkingTree;

    public ObservableCollection<SegmentProjection> Segments { get; } = new();
    public ObservableCollection<CommitRefProjection> RefBadges { get; } = new();
    private GitKay.Core.Models.CommitRef[] _refs = Array.Empty<GitKay.Core.Models.CommitRef>();

    public void Update(Graph.CommitGraphInfo info) {
        var commit = info.Commit;

        if (FullHash == commit.Hash
            && Lane == info.Lane
            && GraphColor == info.Color
            && HasIncoming == info.HasIncoming
            && Segments.Count == info.Segments.Length
            && _refs.Length == commit.Refs.Length
            && _refs.SequenceEqual(commit.Refs)) {
            return;
        }

        FullHash = commit.Hash;
        Hash = GitKay.Core.CommitFormat.shortHash(commit.Hash);
        Subject = commit.Subject;
        Author = commit.AuthorName;
        var timestamp = System.DateTimeOffset.FromUnixTimeSeconds(commit.Timestamp).ToLocalTime();
        Date = timestamp.ToString("yyyy-MM-dd HH:mm");
        FullDate = timestamp.ToString("dddd, d MMMM yyyy HH:mm:ss zzz");
        AuthorEmail = commit.AuthorEmail;
        Message = string.IsNullOrWhiteSpace(commit.Message) ? commit.Subject : commit.Message.TrimEnd();
        Parents = commit.Parents.IsEmpty ? "(root commit)" : string.Join("  ", commit.Parents);
        _refs = commit.Refs.ToArray();
        HasRefs = _refs.Any();
        RefsSummary = GitKay.Core.CommitFormat.refsSummary(Microsoft.FSharp.Collections.ListModule.OfSeq(_refs.Select(reference => reference.Name)));
        UpdateRefBadges();
        Lane = info.Lane;
        GraphColor = info.Color;
        HasIncoming = info.HasIncoming;
        IsMerge = commit.Parents.Length > 1;
        IsHead = _refs.Any(reference => reference.IsCurrentHead);

        Segments.SyncWith(
            info.Segments,
            m => $"{m.Lane}-{m.TargetLane}-{m.IsCommit}-{m.Color}",
            vm => vm.Key,
            segment => {
                var projection = new SegmentProjection();
                projection.Update(segment);
                return projection;
            }
        );
    }

    /// <summary>Shows this projection as the uncommitted changes row, drawn in HEAD's lane.</summary>
    public void UpdateWorkingTree(Microsoft.FSharp.Collections.FSharpList<GitKay.Core.WorkingTree.Entry> entries, int lane) {
        IsWorkingTree = true;
        Subject = "Uncommitted changes";
        Message = GitKay.Core.WorkingTree.summary(entries);
        SecondarySummary = Message;
        HasSecondarySummary = true;
        Lane = lane;
    }

    public void ApplySearchMatch(GitKay.Core.GitSearch.Result? result) {
        HasSearchMatch = result != null;
        SearchMatchSummary = result?.MatchSummary ?? "";

        if (result != null) {
            var kinds = result.MatchKinds;
            HasHashMatch = kinds.Contains(GitKay.Core.GitSearch.MatchKind.HashMatch);
            HasSubjectMatch = kinds.Contains(GitKay.Core.GitSearch.MatchKind.MessageMatch);
            HasAuthorMatch = kinds.Contains(GitKay.Core.GitSearch.MatchKind.AuthorMatch);
            HasRefMatch = kinds.Contains(GitKay.Core.GitSearch.MatchKind.RefMatch);
            RefMatchCount = result.MatchedRefs.Length;
            HasDiffMatch = kinds.Contains(GitKay.Core.GitSearch.MatchKind.PathMatch) || kinds.Contains(GitKay.Core.GitSearch.MatchKind.TextMatch);
            PathMatchCount = result.MatchedPaths.Length;
        }
        else {
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

    /// <summary>Every branch, remote branch and tag on this commit (not stashes), for copying names.</summary>
    public IEnumerable<(string Name, string Kind)> RefNames =>
        _refs.Where(reference => reference.Kind != GitKay.Core.Models.CommitRefKind.Stash)
            .Select(reference => (reference.Name, reference.Kind switch {
                GitKay.Core.Models.CommitRefKind.Tag => "tag",
                GitKay.Core.Models.CommitRefKind.Remote => "remote branch",
                _ => "branch",
            }));

    /// <summary>Local branches pointing at this commit, whether or not their badges are shown.</summary>
    public IEnumerable<BranchTarget> LocalBranches =>
        _refs.Where(reference => reference.Kind == GitKay.Core.Models.CommitRefKind.Branch)
            .Select(reference => new BranchTarget(reference.Name, reference.IsCurrentHead));

    partial void OnShowBranchRefsChanged(bool value) => UpdateRefBadges();
    partial void OnShowStashesChanged(bool value) => UpdateRefBadges();

    private void UpdateRefBadges() {
        var visibleRefs = GitKay.Core.CommitFormat.shownRefs(ShowBranchRefs, ShowStashes, Microsoft.FSharp.Collections.ListModule.OfSeq(_refs)).ToArray();

        if (RefBadges.Count == visibleRefs.Length) {
            var isSame = true;
            for (int i = 0; i < visibleRefs.Length; i++) {
                if (RefBadges[i].Text != visibleRefs[i].Name || (int)RefBadges[i].Kind != (int)visibleRefs[i].Kind) {
                    isSame = false;
                    break;
                }
            }

            if (isSame) {
                return;
            }
        }

        RefBadges.Clear();

        foreach (var badge in visibleRefs.Select(CreateRefBadge)) {
            RefBadges.Add(badge);
        }

        HasRefBadges = RefBadges.Count > 0;
    }

    private static CommitRefProjection CreateRefBadge(GitKay.Core.Models.CommitRef reference) {
        return reference.Kind switch {
            GitKay.Core.Models.CommitRefKind.Branch => new CommitRefProjection(reference.Name, CommitRefKind.Branch),
            GitKay.Core.Models.CommitRefKind.Remote => new CommitRefProjection(reference.Name, CommitRefKind.Remote),
            GitKay.Core.Models.CommitRefKind.Tag => new CommitRefProjection(reference.Name, CommitRefKind.Tag),
            GitKay.Core.Models.CommitRefKind.Stash => new CommitRefProjection(reference.Name, CommitRefKind.Stash),
            _ => new CommitRefProjection(reference.Name, CommitRefKind.Branch)
        };
    }
}

public sealed record BranchTarget(string Name, bool IsCurrentHead);

public enum CommitRefKind {
    Branch = 0,
    Remote = 1,
    Tag = 2,
    Stash = 3,
}

public sealed class CommitRefProjection {
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

    public CommitRefProjection(string text, CommitRefKind kind) {
        Text = text;
        Kind = kind;
        IsCurrentHead = false;

        var styles = kind switch {
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
    public bool IsRemote => Kind == CommitRefKind.Remote;
    public bool IsStash => Kind == CommitRefKind.Stash;
    public bool IsNotTag => !IsTag;
    public IBrush Background { get; }
    public IBrush BorderBrush { get; }
    public IBrush Foreground { get; }
}

public partial class SegmentProjection : ObservableObject, IProjection<Graph.LaneSegment> {
    [ObservableProperty] private int _lane;
    [ObservableProperty] private int _targetLane;
    [ObservableProperty] private bool _isCommit;
    [ObservableProperty] private int _color;
    [ObservableProperty] private string _key = "";

    public void Update(Graph.LaneSegment segment) {
        Lane = segment.Lane;
        TargetLane = segment.TargetLane;
        IsCommit = segment.IsCommit;
        Color = segment.Color;
        Key = $"{segment.Lane}-{segment.TargetLane}-{segment.IsCommit}-{segment.Color}";
    }
}
