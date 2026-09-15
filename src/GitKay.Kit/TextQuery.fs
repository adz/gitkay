namespace GitKay.Kit

open System
open System.Collections.Generic
open System.Text.RegularExpressions

/// <summary>
/// Text to look for: nothing, a literal (matched ignoring case), or a regular expression (ignoring case unless it
/// says otherwise with an inline <c>(?-i)</c>).
/// </summary>
/// <remarks>
/// Building a query is total. An expression that doesn't compile becomes a literal of the same text, so a half-typed
/// pattern still finds something, and searching, highlighting and find-in-diff always agree about what matches.
/// Matching is total too: an expression that runs past its time limit matches nothing rather than throwing.
/// </remarks>
[<Sealed>]
type TextQuery private (literal: string, regex: Regex) =
    static let options = RegexOptions.IgnoreCase ||| RegexOptions.CultureInvariant
    static let timeLimit = TimeSpan.FromMilliseconds 100.0
    static let none = TextQuery(null, null)

    /// <summary>A query that matches nothing.</summary>
    static member None = none

    /// <summary>A literal, or a regular expression when <paramref name="useRegex"/> is set; blank text matches nothing.</summary>
    static member Create(useRegex: bool, text: string) =
        if String.IsNullOrWhiteSpace text then none
        elif not useRegex then TextQuery(text, null)
        else
            try TextQuery(null, Regex(text, options, timeLimit))
            with :? ArgumentException -> TextQuery(text, null)

    member _.IsEmpty = isNull literal && isNull regex

    /// <summary>Whether <paramref name="text"/> contains a match.</summary>
    member _.IsMatch(text: string) =
        if String.IsNullOrEmpty text then false
        elif not (isNull literal) then text.Contains(literal, StringComparison.OrdinalIgnoreCase)
        elif isNull regex then false
        else
            try regex.IsMatch text
            with :? RegexMatchTimeoutException -> false

    /// <summary>Every non-empty match as (start, length), in order.</summary>
    member _.Spans(text: string) : struct (int * int) list =
        if String.IsNullOrEmpty text then []
        elif not (isNull literal) then
            let spans = ResizeArray()
            let mutable index = text.IndexOf(literal, StringComparison.OrdinalIgnoreCase)
            while index >= 0 do
                spans.Add(struct (index, literal.Length))
                index <- text.IndexOf(literal, index + literal.Length, StringComparison.OrdinalIgnoreCase)
            List.ofSeq spans
        elif isNull regex then []
        else
            try
                [ for m in regex.Matches text do
                      if m.Length > 0 then struct (m.Index, m.Length) ]
            with :? RegexMatchTimeoutException -> []

    /// <summary>The start of the first match, or -1.</summary>
    member this.FirstIndex(text: string) =
        match this.Spans text with
        | struct (start, _) :: _ -> start
        | [] -> -1

/// <summary>
/// Remembers recently built queries, for views that rebuild the same query on every render.
/// </summary>
[<Sealed>]
type TextQueryCache(capacity: int) =
    let queries = Dictionary<struct (bool * string), TextQuery>()

    new() = TextQueryCache 16

    member _.Get(useRegex: bool, text: string) =
        let key = struct (useRegex, (if isNull text then "" else text))
        match queries.TryGetValue key with
        | true, query -> query
        | _ ->
            if queries.Count >= capacity then queries.Clear()
            let query = TextQuery.Create(useRegex, text)
            queries[key] <- query
            query
