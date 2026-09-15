using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Elmish.Glue.Core;

namespace GitKay.UI;

public sealed class SearchScopeProjection {
    public SearchScopeProjection(string key, string label, string placeholder = "") {
        Key = key;
        Label = label;
        Placeholder = placeholder;
    }

    public string Key { get; }
    public string Label { get; }
    /// <summary>Search box hint describing what this scope matches.</summary>
    public string Placeholder { get; }

    public override string ToString() => Label;
}

public partial class SearchResultProjection : ObservableObject, IProjection<GitKay.Core.GitSearch.Result> {
    [ObservableProperty] private string _fullHash = "";
    [ObservableProperty] private string _hash = "";
    [ObservableProperty] private string _subject = "";
    [ObservableProperty] private string _author = "";
    [ObservableProperty] private string _date = "";
    [ObservableProperty] private string _matchSummary = "";
    [ObservableProperty] private bool _hasMatchedFields;
    [ObservableProperty] private string _matchedFieldsLabel = "";
    [ObservableProperty] private bool _hasMatchedPaths;
    [ObservableProperty] private string _matchedPathsLabel = "";
    [ObservableProperty] private bool _hasMatchedRefs;
    [ObservableProperty] private string _matchedRefsLabel = "";

    public void Update(GitKay.Core.GitSearch.Result result) {
        FullHash = result.Commit.Hash;
        Hash = GitKay.Core.CommitFormat.shortHash(result.Commit.Hash);
        Subject = result.Commit.Subject;
        Author = result.Commit.AuthorName;
        Date = System.DateTimeOffset.FromUnixTimeSeconds(result.Commit.Timestamp).LocalDateTime.ToString("yyyy-MM-dd HH:mm");
        MatchSummary = result.MatchSummary;
        MatchedFieldsLabel = GitKay.Core.CommitFormat.countedList("Field", Microsoft.FSharp.Collections.ListModule.Map(
            Microsoft.FSharp.Core.FuncConvert.FromFunc<GitKay.Core.GitSearch.MatchKind, string>(GitKay.Core.GitSearch.matchKindLabel), result.MatchKinds));
        MatchedPathsLabel = GitKay.Core.CommitFormat.countedList("File", result.MatchedPaths);
        MatchedRefsLabel = GitKay.Core.CommitFormat.countedList("Ref", result.MatchedRefs);
        HasMatchedFields = MatchedFieldsLabel.Length > 0;
        HasMatchedPaths = MatchedPathsLabel.Length > 0;
        HasMatchedRefs = MatchedRefsLabel.Length > 0;
    }
}

/// <summary>A clickable reference to a related commit (parent or child).</summary>
public sealed class CommitLinkProjection {
    public CommitLinkProjection(string fullHash, string subject) {
        FullHash = fullHash;
        ShortHash = GitKay.Core.CommitFormat.shortHash(fullHash);
        Subject = subject;
    }

    public string FullHash { get; }
    public string ShortHash { get; }
    public string Subject { get; }
}
