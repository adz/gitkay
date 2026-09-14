namespace GitKay.Core

open System
open System.Text
open System.Text.RegularExpressions
open Axial
open GitKay.Core.Models

/// Commit search: a query of prefixed terms (author:, path:, diff:, after: ...) combined with AND, where
/// plain words search the field chosen by the Commit | Path | Diff mode.
module GitSearch =

    /// What plain (unprefixed) words search.
    type Mode =
        /// Headline, message, hash and branch/tag names.
        | Commit
        /// Changed file paths.
        | Path
        /// Lines the commit added or removed.
        | Diff

    type Field =
        /// Headline, message, hash and ref names.
        | CommitInfo
        | Message
        | Author
        | Hash
        | Ref
        | ChangedPath
        | ChangedLine
        | After
        | Before

    type Term = { Field: Field; Text: string }

    type Result =
        { Commit: Models.Commit
          MatchKinds: string list
          MatchSummary: string
          MatchedPaths: string list
          MatchedRefs: string list }

    let modeKey mode =
        match mode with
        | Commit -> "commit"
        | Path -> "path"
        | Diff -> "diff"

    /// Parses a mode key; legacy scope keys map to the closest mode.
    let parseMode (key: string) =
        match (if isNull key then "" else key.Trim().ToLowerInvariant()) with
        | "path" -> Path
        | "diff" | "text" -> Diff
        | _ -> Commit

    let private prefixes =
        [ "message", Message
          "author", Author
          "hash", Hash
          "ref", Ref
          "path", ChangedPath
          "diff", ChangedLine
          "after", After
          "before", Before ]

    let prefixOf field =
        prefixes |> List.tryFind (fun (_, f) -> f = field) |> Option.map fst

    let private defaultField mode =
        match mode with
        | Commit -> CommitInfo
        | Path -> ChangedPath
        | Diff -> ChangedLine

    /// Splits on whitespace, keeping "quoted phrases" (and prefix:"quoted values") together.
    let private tokenize (text: string) =
        let tokens = ResizeArray<string>()
        let current = StringBuilder()
        let mutable quoted = false

        for c in text do
            if c = '"' then
                quoted <- not quoted
            elif Char.IsWhiteSpace c && not quoted then
                if current.Length > 0 then
                    tokens.Add(current.ToString())
                    current.Clear() |> ignore
            else
                current.Append c |> ignore

        if current.Length > 0 then
            tokens.Add(current.ToString())

        List.ofSeq tokens

    /// Parses query text into terms. Unknown prefixes are treated as plain words of the mode's field.
    let parseQuery (mode: Mode) (text: string) : Term list =
        if String.IsNullOrWhiteSpace text then
            []
        else
            tokenize text
            |> List.choose (fun token ->
                let colon = token.IndexOf ':'
                let prefixed =
                    if colon > 0 then
                        let name = token.Substring(0, colon).ToLowerInvariant()
                        prefixes |> List.tryFind (fun (prefix, _) -> prefix = name) |> Option.map (fun (_, field) -> field, token.Substring(colon + 1))
                    else
                        None

                match prefixed with
                | Some(_, value) when String.IsNullOrWhiteSpace value -> None
                | Some(field, value) -> Some { Field = field; Text = value }
                | None -> Some { Field = defaultField mode; Text = token })

    /// Formats terms back to query text; plain terms of the mode's field stay unprefixed.
    let formatQuery (mode: Mode) (terms: Term list) =
        let quote (value: string) = if value |> Seq.exists Char.IsWhiteSpace then "\"" + value + "\"" else value

        terms
        |> List.map (fun term ->
            match prefixOf term.Field with
            | Some prefix when term.Field <> defaultField mode -> prefix + ":" + quote term.Text
            | _ -> quote term.Text)
        |> String.concat " "

    let private containsIgnoreCase (haystack: string) (needle: string) =
        not (isNull haystack) && haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0

    /// A case-insensitive text matcher, or a regular expression when useRegex is set. An invalid expression falls
    /// back to literal matching so a half-typed pattern still behaves sensibly.
    let matcher (useRegex: bool) (text: string) : string -> bool =
        if useRegex then
            try
                let regex = Regex(text, RegexOptions.IgnoreCase ||| RegexOptions.CultureInvariant)
                fun value -> not (isNull value) && regex.IsMatch value
            with :? ArgumentException ->
                fun value -> containsIgnoreCase value text
        else
            fun value -> containsIgnoreCase value text

    /// Absolute dates, or git-style relative ones: "2 weeks ago", "3.days.ago", "yesterday".
    let tryParseDate (now: DateTimeOffset) (text: string) =
        let normalized = text.Trim().ToLowerInvariant().Replace('.', ' ').Replace('_', ' ')
        let relative = Regex.Match(normalized, @"^(\d+)\s*(second|minute|hour|day|week|month|year)s?(\s+ago)?$")
        if normalized = "yesterday" then
            Some(now.AddDays(-1.0).ToUnixTimeSeconds())
        elif relative.Success then
            let amount = float (int relative.Groups.[1].Value)
            let date =
                match relative.Groups.[2].Value with
                | "second" -> now.AddSeconds(-amount)
                | "minute" -> now.AddMinutes(-amount)
                | "hour" -> now.AddHours(-amount)
                | "day" -> now.AddDays(-amount)
                | "week" -> now.AddDays(-7.0 * amount)
                | "month" -> now.AddMonths(-(int amount))
                | _ -> now.AddYears(-(int amount))
            Some(date.ToUnixTimeSeconds())
        else
            match DateTimeOffset.TryParse(text, Globalization.CultureInfo.InvariantCulture, Globalization.DateTimeStyles.AssumeLocal) with
            | true, date -> Some(date.ToUnixTimeSeconds())
            | _ -> None

    /// `after:`/`before:` values that aren't dates, as (field prefix, text). A search ignores these terms, so the UI
    /// reports them instead of silently widening the results.
    let invalidDateTerms (now: DateTimeOffset) (mode: Mode) (query: string) : (string * string) list =
        parseQuery mode query
        |> List.choose (fun term ->
            match term.Field with
            | After | Before when (tryParseDate now term.Text).IsNone ->
                Some((if term.Field = After then "after" else "before"), term.Text)
            | _ -> None)

    let private buildDisplayPath oldPath newPath =
        if oldPath = newPath then newPath
        elif oldPath = "/dev/null" then newPath
        elif newPath = "/dev/null" then oldPath
        else $"{oldPath} -> {newPath}"

    let private needsDiff (term: Term) = term.Field = ChangedPath || term.Field = ChangedLine

    /// How a search reads commit contents, and where it reports progress.
    type Loaders<'env> =
        { /// Changed (old path, new path) pairs of a commit: cheap, no patch text.
          LoadPaths: string -> Flow<'env, GitError, (string * string) list>
          /// A commit's full diff, for added/removed line terms.
          LoadDiff: string -> Flow<'env, GitError, FileDiff list>
          /// Called with (commits checked, total).
          Progress: int -> int -> unit }

    /// Searches commits: every term must match. Each commit is checked cheapest-first: metadata, then its changed
    /// paths (a file list, no patch text), then its diff lines, and stops at the first term that fails. Progress is
    /// reported as (commits checked, total) at most every 256 commits.
    let searchCommitsWith
        (now: DateTimeOffset)
        (commits: Models.Commit list)
        (mode: Mode)
        (useRegex: bool)
        (query: string)
        (loaders: Loaders<'env>) : Flow<'env, GitError, Result list> =
        flow {
            let terms = parseQuery mode query

            if terms.IsEmpty then
                return! Error GitError.EmptySearchQuery
            else
                let compiled = terms |> List.map (fun term -> term, matcher useRegex term.Text)
                let metadataTerms, diffTerms = compiled |> List.partition (fst >> needsDiff >> not)
                let pathTerms, lineTerms = diffTerms |> List.partition (fun (term, _) -> term.Field = ChangedPath)
                let results = ResizeArray<Result>()
                let total = commits.Length
                let mutable checkedCount = 0
                loaders.Progress 0 total

                for commit in commits do
                    do! Flow.Runtime.ensureNotCanceled (GitError.OperationCanceled "Search")
                    checkedCount <- checkedCount + 1
                    if checkedCount % 256 = 0 then loaders.Progress checkedCount total

                    let kinds = Collections.Generic.List<string>()
                    let matchedRefs = Collections.Generic.List<string>()
                    let addKind kind = if not (kinds.Contains kind) then kinds.Add kind

                    let metadataMatches =
                        metadataTerms
                        |> List.forall (fun (term, matches) ->
                            let refMatches () = commit.Refs |> List.filter (fun r -> matches r.Name) |> List.map _.Name
                            match term.Field with
                            | CommitInfo ->
                                let refs = refMatches ()
                                let message = matches commit.Subject || matches commit.Message
                                let hash = matches commit.Hash
                                if message then addKind "message"
                                if hash then addKind "hash"
                                if not refs.IsEmpty then addKind "ref"; matchedRefs.AddRange refs
                                message || hash || not refs.IsEmpty
                            | Message ->
                                let hit = matches commit.Subject || matches commit.Message
                                if hit then addKind "message"
                                hit
                            | Author ->
                                let hit = matches commit.AuthorName || matches commit.AuthorEmail
                                if hit then addKind "author"
                                hit
                            | Hash ->
                                let hit = matches commit.Hash
                                if hit then addKind "hash"
                                hit
                            | Ref ->
                                let refs = refMatches ()
                                if not refs.IsEmpty then addKind "ref"; matchedRefs.AddRange refs
                                not refs.IsEmpty
                            | After -> tryParseDate now term.Text |> Option.forall (fun date -> commit.Timestamp >= date)
                            | Before -> tryParseDate now term.Text |> Option.forall (fun date -> commit.Timestamp < date)
                            | ChangedPath
                            | ChangedLine -> true)

                    let matchedPaths = Collections.Generic.List<string>()

                    let! pathMatches =
                        if not metadataMatches then
                            Flow.ok false
                        elif pathTerms.IsEmpty then
                            Flow.ok true
                        else
                            flow {
                                let! files = loaders.LoadPaths commit.Hash

                                return
                                    pathTerms
                                    |> List.forall (fun (_, matches) ->
                                        let paths =
                                            files
                                            |> List.filter (fun (oldPath, newPath) -> matches oldPath || matches newPath)
                                            |> List.map (fun (oldPath, newPath) -> buildDisplayPath oldPath newPath)
                                        if not paths.IsEmpty then
                                            addKind "path"
                                            for path in paths do
                                                if not (matchedPaths.Contains path) then matchedPaths.Add path
                                        not paths.IsEmpty)
                            }

                    let! diffMatches =
                        if not pathMatches then
                            Flow.ok false
                        elif lineTerms.IsEmpty then
                            Flow.ok true
                        else
                            flow {
                                let! files = loaders.LoadDiff commit.Hash

                                return
                                    lineTerms
                                    |> List.forall (fun (_, matches) ->
                                        let hit =
                                            files
                                            |> List.exists (fun f ->
                                                f.Hunks
                                                |> List.exists (fun h ->
                                                    h.Lines |> List.exists (fun l -> (l.Type = Added || l.Type = Removed) && matches l.Content)))
                                        if hit then addKind "text"
                                        hit)
                            }

                    if diffMatches then
                        let details = ResizeArray<string>(kinds)
                        if matchedPaths.Count > 0 then details.Add("paths: " + String.Join(", ", matchedPaths))
                        if matchedRefs.Count > 0 then details.Add("refs: " + String.Join(", ", Seq.distinct matchedRefs))

                        results.Add
                            { Commit = commit
                              MatchKinds = List.ofSeq kinds
                              MatchSummary = String.Join("; ", details)
                              MatchedPaths = List.ofSeq matchedPaths
                              MatchedRefs = matchedRefs |> Seq.distinct |> List.ofSeq }

                loaders.Progress total total
                return List.ofSeq results
        }

    /// Searches with one diff loader for both path and line terms (paths are read from the loaded diffs).
    let searchCommitsWithDiffLoader
        (now: DateTimeOffset)
        (commits: Models.Commit list)
        (mode: Mode)
        (useRegex: bool)
        (query: string)
        (loadDiff: string -> Flow<'env, GitError, FileDiff list>) : Flow<'env, GitError, Result list> =
        searchCommitsWith now commits mode useRegex query
            { LoadPaths = fun hash -> loadDiff hash |> Flow.map (List.map (fun file -> file.OldPath, file.NewPath))
              LoadDiff = loadDiff
              Progress = fun _ _ -> () }
