using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Elmish.Glue.Core;

namespace GitKay.UI;

public sealed class SearchScopeProjection
{
    public SearchScopeProjection(string key, string label, string placeholder = "")
    {
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

public partial class SearchResultProjection : ObservableObject, IProjection<GitKay.Core.GitSearch.Result>
{
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

    public void Update(GitKay.Core.GitSearch.Result result)
    {
        var shortHashLength = System.Math.Min(8, result.Commit.Hash.Length);
        FullHash = result.Commit.Hash;
        Hash = result.Commit.Hash.Substring(0, shortHashLength);
        Subject = result.Commit.Subject;
        Author = result.Commit.AuthorName;
        Date = System.DateTimeOffset.FromUnixTimeSeconds(result.Commit.Timestamp).LocalDateTime.ToString("yyyy-MM-dd HH:mm");
        MatchSummary = result.MatchSummary;
        UpdateMatchContext(result.MatchKinds, result.MatchedPaths, result.MatchedRefs);
    }

    private void UpdateMatchContext(IEnumerable<string> matchKinds, IEnumerable<string> matchedPaths, IEnumerable<string> matchedRefs)
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
}

/// <summary>A clickable reference to a related commit (parent or child).</summary>
public sealed class CommitLinkProjection
{
    public CommitLinkProjection(string fullHash, string subject)
    {
        FullHash = fullHash;
        ShortHash = fullHash.Length > 8 ? fullHash[..8] : fullHash;
        Subject = subject;
    }

    public string FullHash { get; }
    public string ShortHash { get; }
    public string Subject { get; }
}
