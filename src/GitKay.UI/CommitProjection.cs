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
    [ObservableProperty] private bool _hasMatchedFields;
    [ObservableProperty] private string _matchedFieldsLabel = "";
    [ObservableProperty] private bool _hasMatchedPaths;
    [ObservableProperty] private string _matchedPathsLabel = "";
    [ObservableProperty] private bool _hasMatchedRefs;
    [ObservableProperty] private string _matchedRefsLabel = "";
    [ObservableProperty] private IBrush _rowBackground = Brushes.Transparent;
    [ObservableProperty] private bool _hasRefBadges;
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
        UpdateSecondarySummary();

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
            UpdateMatchContext(result.MatchKinds, result.MatchedPaths, result.MatchedRefs);
        }
        else
        {
            ClearMatchContext();
        }

        RowBackground =
            result != null
                ? new SolidColorBrush(Color.FromArgb(28, 78, 201, 176))
                : Brushes.Transparent;
        UpdateSecondarySummary();
    }

    private void UpdateSecondarySummary()
    {
        var parts = new System.Collections.Generic.List<string>();

        if (HasMatchedFields)
        {
            parts.Add(MatchedFieldsLabel);
        }

        if (HasMatchedPaths)
        {
            parts.Add(MatchedPathsLabel);
        }

        if (HasMatchedRefs)
        {
            parts.Add(MatchedRefsLabel);
        }

        if (parts.Count == 0 && !string.IsNullOrWhiteSpace(SearchMatchSummary))
        {
            parts.Add(SearchMatchSummary);
        }

        SecondarySummary = string.Join(" · ", parts);
        HasSecondarySummary = !string.IsNullOrWhiteSpace(SecondarySummary);
    }

    private void UpdateMatchContext(
        System.Collections.Generic.IEnumerable<string> matchKinds,
        System.Collections.Generic.IEnumerable<string> matchedPaths,
        System.Collections.Generic.IEnumerable<string> matchedRefs)
    {
        var fieldLabels = matchKinds
            .Select(FormatMatchKind)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        HasMatchedFields = fieldLabels.Length > 0;
        MatchedFieldsLabel = HasMatchedFields ? FormatMatchLabel("Field", fieldLabels) : "";

        var pathLabels = matchedPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        HasMatchedPaths = pathLabels.Length > 0;
        MatchedPathsLabel = HasMatchedPaths ? FormatMatchLabel("File", pathLabels) : "";

        var refLabels = matchedRefs.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        HasMatchedRefs = refLabels.Length > 0;
        MatchedRefsLabel = HasMatchedRefs ? FormatMatchLabel("Ref", refLabels) : "";
    }

    private void ClearMatchContext()
    {
        HasMatchedFields = false;
        MatchedFieldsLabel = "";
        HasMatchedPaths = false;
        MatchedPathsLabel = "";
        HasMatchedRefs = false;
        MatchedRefsLabel = "";
        SecondarySummary = "";
        HasSecondarySummary = false;
    }

    private static string FormatMatchLabel(string singularLabel, IReadOnlyList<string> values)
    {
        var countLabel = values.Count == 1 ? singularLabel : singularLabel + "s";
        var visibleValues = values.Take(3).ToArray();
        var label = $"{countLabel} ({values.Count}): {string.Join(", ", visibleValues)}";

        if (values.Count > visibleValues.Length)
        {
            label += $" +{values.Count - visibleValues.Length} more";
        }

        return label;
    }

    private static string FormatMatchKind(string kind)
    {
        return kind.ToLowerInvariant() switch
        {
            "hash" => "Commit hash",
            "message" => "Message / subject",
            "author" => "Author",
            "path" => "File / path",
            "text" => "Diff text",
            "ref" => "Ref / tag / branch",
            _ => kind,
        };
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
