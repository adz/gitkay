using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    [ObservableProperty] private bool _hasSecondarySummary;
    [ObservableProperty] private string _secondarySummary = "";
    [ObservableProperty] private int _lane = 0;

    public ObservableCollection<SegmentProjection> Segments { get; } = new();

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
        UpdateSecondarySummary();
    }

    private void UpdateSecondarySummary()
    {
        var parts = new System.Collections.Generic.List<string>();

        if (!string.IsNullOrWhiteSpace(SearchMatchSummary))
        {
            parts.Add(SearchMatchSummary);
        }

        if (!string.IsNullOrWhiteSpace(RefsSummary))
        {
            parts.Add(RefsSummary);
        }

        SecondarySummary = string.Join(" · ", parts);
        HasSecondarySummary = !string.IsNullOrWhiteSpace(SecondarySummary);
    }

    private static string FormatRefsSummary(System.Collections.Generic.IEnumerable<string> refs)
    {
        var visibleRefs = refs.Take(3).ToArray();
        var summary = string.Join(" · ", visibleRefs);

        var totalCount = refs.Count();

        if (totalCount > visibleRefs.Length)
        {
            var remainingCount = totalCount - visibleRefs.Length;
            summary = string.IsNullOrEmpty(summary) ? $"+{remainingCount}" : $"{summary} +{remainingCount}";
        }

        return summary;
    }
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
