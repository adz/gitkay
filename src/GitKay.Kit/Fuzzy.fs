namespace GitKay.Kit

open System

/// <summary>
/// Fuzzy matching for pickers: the query's characters must appear in order, ignoring case and spaces; word starts and
/// runs of consecutive characters score higher, and shorter candidates win ties.
/// </summary>
module Fuzzy =

    /// <summary>The score of <paramref name="candidate"/> for <paramref name="query"/>, or -1 when it doesn't match. A blank query scores 0.</summary>
    let score (query: string) (candidate: string) : int =
        let query = if isNull query then "" else String(query.ToCharArray() |> Array.filter (Char.IsWhiteSpace >> not))
        let candidate = if isNull candidate then "" else candidate
        if query.Length = 0 then 0
        else
            let mutable total = 0
            let mutable next = 0
            let mutable previous = -2
            let mutable i = 0
            while i < candidate.Length && next < query.Length do
                if Char.ToLowerInvariant candidate[i] = Char.ToLowerInvariant query[next] then
                    let wordStart =
                        i = 0
                        || not (Char.IsLetterOrDigit candidate[i - 1])
                        || (Char.IsUpper candidate[i] && Char.IsLower candidate[i - 1])
                    total <- total + 1 + (if wordStart then 8 else 0) + (if previous = i - 1 then 5 else 0)
                    previous <- i
                    next <- next + 1
                i <- i + 1
            if next < query.Length then -1 else total * 100 - candidate.Length

    /// <summary>
    /// The positions of the candidates matching <paramref name="query"/>, best first, at most <paramref name="limit"/>.
    /// Each candidate is a title and a detail; a match only in the detail ranks below every title match. A blank query
    /// keeps the original order.
    /// </summary>
    let rank (limit: int) (query: string) (candidates: struct (string * string) list) : int list =
        let blank = String.IsNullOrWhiteSpace query
        candidates
        |> List.mapi (fun index (struct (title, detail)) ->
            match score query title with
            | -1 -> (match score query detail with -1 -> None | detailScore -> Some(index, detailScore - 5000))
            | titleScore -> Some(index, titleScore))
        |> List.choose id
        |> fun scored -> if blank then scored else List.sortByDescending snd scored
        |> List.truncate (max 0 limit)
        |> List.map fst
