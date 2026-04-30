using CommunityToolkit.Mvvm.ComponentModel;
using Elmish.Glue.Core;

namespace GitKay.UI;

public sealed class SearchScopeProjection
{
    public SearchScopeProjection(string key, string label)
    {
        Key = key;
        Label = label;
    }

    public string Key { get; }
    public string Label { get; }

    public override string ToString() => Label;
}

public partial class SearchResultProjection : ObservableObject, IProjection<GitKay.Core.GitService.SearchResult>
{
    [ObservableProperty] private string _fullHash = "";
    [ObservableProperty] private string _hash = "";
    [ObservableProperty] private string _subject = "";
    [ObservableProperty] private string _author = "";
    [ObservableProperty] private string _date = "";
    [ObservableProperty] private string _matchSummary = "";

    public void Update(GitKay.Core.GitService.SearchResult result)
    {
        var shortHashLength = System.Math.Min(8, result.Commit.Hash.Length);
        FullHash = result.Commit.Hash;
        Hash = result.Commit.Hash.Substring(0, shortHashLength);
        Subject = result.Commit.Subject;
        Author = result.Commit.AuthorName;
        Date = System.DateTimeOffset.FromUnixTimeSeconds(result.Commit.Timestamp).LocalDateTime.ToString("yyyy-MM-dd HH:mm");
        MatchSummary = result.MatchSummary;
    }
}
