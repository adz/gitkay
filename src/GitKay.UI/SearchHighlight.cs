using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace GitKay.UI;

/// <summary>
/// The applied commit search, split by what each part should underline: commit headline, hash, author and ref
/// names, file paths and changed lines. One source for the dotted underline used everywhere.
/// </summary>
public sealed class SearchHighlight
{
    private readonly Dictionary<string, Regex?> _regexes = new(StringComparer.Ordinal);

    public SearchHighlight(string query, string modeKey, bool useRegex)
    {
        UseRegex = useRegex;
        var terms = GitKay.Core.GitSearch.parseQuery(GitKay.Core.GitSearch.parseMode(modeKey), query);
        foreach (var term in terms)
        {
            var field = term.Field;
            if (field.IsCommitInfo || field.IsMessage) Subject.Add(term.Text);
            if (field.IsCommitInfo || field.IsHash) Hash.Add(term.Text);
            if (field.IsCommitInfo || field.IsRef) Ref.Add(term.Text);
            if (field.IsAuthor) Author.Add(term.Text);
            if (field.IsChangedPath) Path.Add(term.Text);
            if (field.IsChangedLine) Line.Add(term.Text);
        }
    }

    public bool UseRegex { get; }
    public List<string> Subject { get; } = new();
    public List<string> Hash { get; } = new();
    public List<string> Ref { get; } = new();
    public List<string> Author { get; } = new();
    public List<string> Path { get; } = new();
    public List<string> Line { get; } = new();

    public bool IsEmpty => Subject.Count + Hash.Count + Ref.Count + Author.Count + Path.Count + Line.Count == 0;

    /// <summary>Every (start, length) span of <paramref name="text"/> matched by any of <paramref name="terms"/>.</summary>
    public IEnumerable<(int Start, int Length)> Matches(IReadOnlyList<string> terms, string? text)
    {
        if (string.IsNullOrEmpty(text) || terms.Count == 0) yield break;
        foreach (var term in terms)
        {
            if (string.IsNullOrEmpty(term)) continue;
            if (UseRegex)
            {
                if (Compile(term) is not { } regex) continue;
                foreach (Match match in regex.Matches(text))
                    if (match.Length > 0) yield return (match.Index, match.Length);
            }
            else
            {
                for (var index = text.IndexOf(term, StringComparison.OrdinalIgnoreCase); index >= 0;
                     index = text.IndexOf(term, index + term.Length, StringComparison.OrdinalIgnoreCase))
                    yield return (index, term.Length);
            }
        }
    }

    private Regex? Compile(string pattern)
    {
        if (_regexes.TryGetValue(pattern, out var cached)) return cached;
        Regex? regex;
        try
        {
            regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
        }
        catch (ArgumentException)
        {
            regex = null;
        }

        return _regexes[pattern] = regex;
    }
}
