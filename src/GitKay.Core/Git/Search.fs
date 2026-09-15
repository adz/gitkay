namespace GitKay.Core

open System
open System.Text
open System.Text.RegularExpressions
open Axial
open GitKay.Core.Models
open GitKay.Kit

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

    /// <summary>The query a term's text asks for: literal, or a regular expression when the search uses regex.</summary>
    let termQuery (useRegex: bool) (term: Term) = TextQuery.Create(useRegex, term.Text)

    /// <summary>
    /// An applied search split by what each part underlines: commit headline, hash, author and ref names, file paths and
    /// changed lines. Plain words of the Commit mode count for headline, hash and refs.
    /// </summary>
    type Highlight =
        { Subject: TextQuery list
          Hash: TextQuery list
          Ref: TextQuery list
          Author: TextQuery list
          Path: TextQuery list
          Line: TextQuery list }

        member this.IsEmpty =
            [ this.Subject; this.Hash; this.Ref; this.Author; this.Path; this.Line ] |> List.forall List.isEmpty

    let emptyHighlight = { Subject = []; Hash = []; Ref = []; Author = []; Path = []; Line = [] }

    let highlight (mode: Mode) (useRegex: bool) (query: string) : Highlight =
        let terms = parseQuery mode query
        let queriesFor (fields: Field list) =
            terms |> List.filter (fun term -> List.contains term.Field fields) |> List.map (termQuery useRegex)
        { Subject = queriesFor [ CommitInfo; Message ]
          Hash = queriesFor [ CommitInfo; Hash ]
          Ref = queriesFor [ CommitInfo; Ref ]
          Author = queriesFor [ Author ]
          Path = queriesFor [ ChangedPath ]
          Line = queriesFor [ ChangedLine ] }

    /// <summary>A highlight that underlines one query in headlines, hashes and authors, for a quick find over commits.</summary>
    let commitHighlight (query: TextQuery) =
        if query.IsEmpty then emptyHighlight
        else { emptyHighlight with Subject = [ query ]; Hash = [ query ]; Author = [ query ] }

    /// <summary>Every span of <paramref name="text"/> any of the queries matches, in query order.</summary>
    let spans (queries: TextQuery list) (text: string) : struct (int * int) list =
        queries |> List.collect (fun query -> query.Spans text)

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

    /// <summary>An <c>after:</c> or <c>before:</c> value that isn't a date, which a search ignores.</summary>
    type DateProblem = { Field: Field; Text: string }

    /// <summary>The date terms a search would ignore, so the UI can say so instead of silently widening the results.</summary>
    let invalidDates (now: DateTimeOffset) (mode: Mode) (query: string) : DateProblem list =
        parseQuery mode query
        |> List.choose (fun term ->
            match term.Field with
            | After | Before when (tryParseDate now term.Text).IsNone -> Some { Field = term.Field; Text = term.Text }
            | _ -> None)

    let describeDateProblem (problem: DateProblem) =
        let prefix = prefixOf problem.Field |> Option.defaultValue "date"
        $"“{problem.Text}” isn't a date, so {prefix}: is ignored — try 2024-05-01, “2 weeks ago” or yesterday"

    // ----- Editing one field of a query, for the advanced search inputs and column filters -----

    /// <summary>The text of every term of <paramref name="field"/>, space-separated.</summary>
    let fieldText (mode: Mode) (query: string) (field: Field) =
        parseQuery mode query |> List.filter (fun term -> term.Field = field) |> List.map _.Text |> String.concat " "

    /// <summary>
    /// The query with <paramref name="field"/>'s terms replaced by one term of <paramref name="value"/>, or removed when
    /// it's blank. A value with spaces stays one quoted term.
    /// </summary>
    let withFieldText (mode: Mode) (query: string) (field: Field) (value: string) =
        let others = parseQuery mode query |> List.filter (fun term -> term.Field <> field)
        let replaced =
            if String.IsNullOrWhiteSpace value then others
            else others @ [ { Field = field; Text = value.Trim() } ]
        formatQuery mode replaced

    /// <summary>A column filter's field by name ("author", "hash", ...); anything else filters on commit info.</summary>
    let fieldNamed (name: string) =
        let normalized = if isNull name then "" else name.Trim().ToLowerInvariant()
        prefixes |> List.tryFind (fun (prefix, _) -> prefix = normalized) |> Option.map snd |> Option.defaultValue CommitInfo

    /// <summary>The text of the first term of <paramref name="field"/>, if any.</summary>
    let firstTermText (mode: Mode) (query: string) (field: Field) =
        parseQuery mode query |> List.tryFind (fun term -> term.Field = field) |> Option.map _.Text

    // ----- Recent searches, suggested under the search box -----

    [<Literal>]
    let private MaxRecentSearches = 30

    /// <summary>Stored searches, trimmed and without blanks or duplicates, most recent first.</summary>
    let recentSearches (stored: string seq) =
        stored |> Seq.filter (String.IsNullOrWhiteSpace >> not) |> Seq.map _.Trim() |> Recent.ofSeq MaxRecentSearches

    /// <summary>The recent searches with <paramref name="query"/> run most recently; a blank query changes nothing.</summary>
    let rememberSearch (query: string) (recent: string list) =
        if String.IsNullOrWhiteSpace query then recent else Recent.add MaxRecentSearches (query.Trim()) recent

    /// <summary>Up to six earlier searches containing what's typed, not counting the text itself.</summary>
    let suggestSearches (typed: string) (recent: string list) =
        let typed = if isNull typed then "" else typed.Trim()
        recent
        |> Recent.matching 6 (fun search ->
            search <> typed && (typed.Length = 0 || search.Contains(typed, StringComparison.OrdinalIgnoreCase)))

    // ----- What an applied search marks in a commit's diff -----

    /// <summary>Which part of a changed file a search term is about.</summary>
    type DiffScope =
        | ChangedLines
        | ChangedPaths
        | AnyChange

    /// <summary>The term marked in the diff: the first changed-line term, else the first path term, else nothing.</summary>
    type DiffMark = { Query: TextQuery; Text: string; Scope: DiffScope }

    let noDiffMark = { Query = TextQuery.None; Text = ""; Scope = AnyChange }

    let diffMark (mode: Mode) (useRegex: bool) (query: string) =
        let terms = parseQuery mode query
        let mark scope (term: Term) = { Query = TextQuery.Create(useRegex, term.Text); Text = term.Text; Scope = scope }
        match terms |> List.tryFind (fun term -> term.Field = ChangedLine) with
        | Some term -> mark ChangedLines term
        | None ->
            match terms |> List.tryFind (fun term -> term.Field = ChangedPath) with
            | Some term -> mark ChangedPaths term
            | None -> noDiffMark

    /// <summary>Whether a changed file's old, new or display path carries the mark.</summary>
    let marksPath (mark: DiffMark) (oldPath: string) (newPath: string) (displayPath: string) =
        mark.Scope <> ChangedLines
        && (mark.Query.IsMatch oldPath || mark.Query.IsMatch newPath || mark.Query.IsMatch displayPath)

    /// <summary>Whether a diff line's text carries the mark.</summary>
    let marksLine (mark: DiffMark) (content: string) = mark.Scope <> ChangedPaths && mark.Query.IsMatch content

    let private needsDiff (term: Term) = term.Field = ChangedPath || term.Field = ChangedLine

    /// Reads commit contents for one search worker. A reader serves one worker at a time.
    type ContentReader<'env> =
        { /// Changed (old path, new path) pairs of a commit: no patch text.
          ChangedPaths: string -> Flow<'env, GitError, (string * string) list>
          /// For each predicate, whether any added or removed line of the commit satisfies it.
          MatchChangedLines: string -> (string -> bool) list -> Flow<'env, GitError, bool list>
          /// Releases the reader's resources (such as its repository handle).
          Release: unit -> unit }

    /// How a search reads commit contents in parallel, and where it reports progress.
    type Loaders<'env> =
        { /// Opens a reader for one worker.
          OpenReader: unit -> ContentReader<'env>
          /// Workers checking commit contents concurrently.
          Workers: int
          /// Called with (commits checked, total).
          Progress: int -> int -> unit
          /// Called for each match as soon as it is found (from worker threads, in no particular order).
          Found: Result -> unit }

    /// Searches commits: every term must match. Metadata terms filter all commits in one cheap pass; only the
    /// remaining candidates have their changed paths and then changed lines checked, split across parallel workers.
    /// Results keep history order.
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
                let compiled = terms |> List.map (fun term -> term, (termQuery useRegex term).IsMatch)
                let metadataTerms, diffTerms = compiled |> List.partition (fst >> needsDiff >> not)
                let pathTerms, lineTerms = diffTerms |> List.partition (fun (term, _) -> term.Field = ChangedPath)
                let total = commits.Length
                loaders.Progress 0 total
                do! Flow.Runtime.ensureNotCanceled (GitError.OperationCanceled "Search")

                // Phase 1: metadata, pure and fast.
                let candidates =
                    commits
                    |> List.choose (fun commit ->
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

                        if metadataMatches then Some(commit, kinds, matchedRefs) else None)
                    |> Array.ofList

                let toResult (commit: Models.Commit, kinds: Collections.Generic.List<string>, matchedRefs: Collections.Generic.List<string>, matchedPaths: Collections.Generic.List<string>) =
                    let details = ResizeArray<string>(kinds)
                    if matchedPaths.Count > 0 then details.Add("paths: " + String.Join(", ", matchedPaths))
                    if matchedRefs.Count > 0 then details.Add("refs: " + String.Join(", ", Seq.distinct matchedRefs))

                    { Commit = commit
                      MatchKinds = List.ofSeq kinds
                      MatchSummary = String.Join("; ", details)
                      MatchedPaths = List.ofSeq matchedPaths
                      MatchedRefs = matchedRefs |> Seq.distinct |> List.ofSeq }

                if diffTerms.IsEmpty then
                    loaders.Progress total total
                    return candidates |> Array.map (fun (commit, kinds, refs) -> toResult (commit, kinds, refs, Collections.Generic.List())) |> List.ofArray
                else
                    // Phase 2: contents of the candidates, in parallel.
                    let outcomes : Result option array = Array.zeroCreate candidates.Length
                    let mutable checkedCount = total - candidates.Length
                    loaders.Progress checkedCount total

                    let evaluate (reader: ContentReader<'env>) index =
                        flow {
                            let commit, kinds, matchedRefs = candidates.[index]
                            let addKind kind = if not (kinds.Contains kind) then kinds.Add kind
                            let matchedPaths = Collections.Generic.List<string>()

                            let! pathsMatch =
                                if pathTerms.IsEmpty then
                                    Flow.ok true
                                else
                                    flow {
                                        let! files = reader.ChangedPaths commit.Hash

                                        return
                                            pathTerms
                                            |> List.forall (fun (_, matches) ->
                                                let paths =
                                                    files
                                                    |> List.filter (fun (oldPath, newPath) -> matches oldPath || matches newPath)
                                                    |> List.map (fun (oldPath, newPath) -> FileChange.path oldPath newPath)
                                                if not paths.IsEmpty then
                                                    addKind "path"
                                                    for path in paths do
                                                        if not (matchedPaths.Contains path) then matchedPaths.Add path
                                                not paths.IsEmpty)
                                    }

                            let! linesMatch =
                                if not pathsMatch then
                                    Flow.ok false
                                elif lineTerms.IsEmpty then
                                    Flow.ok true
                                else
                                    flow {
                                        let! hits = reader.MatchChangedLines commit.Hash (lineTerms |> List.map snd)
                                        let all = List.forall id hits
                                        if all then addKind "text"
                                        return all
                                    }

                            if pathsMatch && linesMatch then
                                let result = toResult (commit, kinds, matchedRefs, matchedPaths)
                                outcomes.[index] <- Some result
                                loaders.Found result
                            let checkedNow = Threading.Interlocked.Increment(&checkedCount)
                            if checkedNow % 64 = 0 then loaders.Progress checkedNow total
                        }

                    let workers = max 1 (min loaders.Workers candidates.Length)
                    // Workers take contiguous chunks from a shared queue: neighbouring commits share blob versions,
                    // so keeping them on one worker lets its object cache hit, while the queue still balances load.
                    let chunkSize = 32
                    let chunks = Collections.Concurrent.ConcurrentQueue<int>(seq { 0 .. chunkSize .. candidates.Length - 1 })

                    let worker (_: int) =
                        flow {
                            let reader = loaders.OpenReader ()

                            try
                                let mutable next = 0
                                while chunks.TryDequeue(&next) do
                                    let last = min (next + chunkSize) candidates.Length - 1
                                    for index in next .. last do
                                        do! Flow.Runtime.ensureNotCanceled (GitError.OperationCanceled "Search")
                                        do! evaluate reader index
                            finally
                                reader.Release ()
                        }

                    if workers = 1 then
                        do! worker 0
                    else
                        let! fibers = List.init workers id |> Flow.traverse (fun slot -> Flow.forkNamed $"search worker {slot + 1}" (worker slot))
                        for fiber in fibers do
                            do! Flow.join fiber

                    loaders.Progress total total
                    return outcomes |> Array.choose id |> List.ofArray
        }

    /// Searches with one diff loader for both path and line terms (paths and lines are read from the loaded diffs).
    let searchCommitsWithDiffLoader
        (now: DateTimeOffset)
        (commits: Models.Commit list)
        (mode: Mode)
        (useRegex: bool)
        (query: string)
        (loadDiff: string -> Flow<'env, GitError, FileDiff list>) : Flow<'env, GitError, Result list> =
        searchCommitsWith now commits mode useRegex query
            { OpenReader =
                fun () ->
                    { ChangedPaths = fun hash -> loadDiff hash |> Flow.map (List.map (fun file -> file.OldPath, file.NewPath))
                      MatchChangedLines =
                        fun hash predicates ->
                            loadDiff hash
                            |> Flow.map (fun files ->
                                predicates
                                |> List.map (fun matches ->
                                    files
                                    |> List.exists (fun f ->
                                        f.Hunks
                                        |> List.exists (fun h ->
                                            h.Lines |> List.exists (fun l -> (l.Type = Added || l.Type = Removed) && matches l.Content)))))
                      Release = ignore }
              Workers = 1
              Progress = fun _ _ -> ()
              Found = ignore }
