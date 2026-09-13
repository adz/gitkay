namespace GitKay.Core

open System
open Reified

/// Startup argument parsing and normalization. This module is pure: it performs no Git or host effects.
module GitStartup =
    type StartupTarget =
        | All
        | Branch of string
        | Sha of string
        | Tag of string
        | Revision of string
        /// ^X, or the left side of A..B: hide commits reachable from X.
        | Exclude of string
        /// A...B: hide commits reachable from the merge base of A and B.
        | ExcludeMergeBase of string * string
        /// -- <paths>: only commits touching these paths.
        | Path of string

    type StartupOptions =
        {
            StartupTargets: StartupTarget list
            ShowBranchRefs: bool
            ShowStashes: bool
            DiffContextLines: int
            DiffPresentationModeKey: string
            SearchQuery: string
            SearchScopeKey: string
            SelectedCommitHash: string option
            /// Filters from --author/--grep/-G etc. are regex when -G was given.
            SearchUseRegex: bool
            /// Filters given on the command line limit the list to matching commits, like git log.
            ShowOnlyMatches: bool
            LogFile: string option
            HelpRequested: bool
            VersionRequested: bool
        }

    let defaultStartupOptions =
        {
            StartupTargets = []
            ShowBranchRefs = false
            ShowStashes = false
            DiffContextLines = 3
            DiffPresentationModeKey = "diff"
            SearchQuery = ""
            SearchScopeKey = "commit"
            SelectedCommitHash = None
            SearchUseRegex = false
            ShowOnlyMatches = false
            LogFile = None
            HelpRequested = false
            VersionRequested = false
        }

    /// Search mode keys (commit, path, diff); older scope names are accepted and mapped to the nearest mode.
    let private tryParseSearchScopeKey (value: string) =
        match value.Trim().ToLowerInvariant() with
        | "commit" | "path" | "diff" | "fields" | "all" | "message" | "author" | "hash" | "ref" | "text" as key ->
            Some(GitSearch.modeKey (GitSearch.parseMode key))
        | _ -> None

    let private tryParseDiffPresentationModeKey (value: string) =
        match value.Trim().ToLowerInvariant() with
        | "diff" -> Some "diff"
        | "side-by-side" -> Some "side-by-side"
        | "new" -> Some "new"
        | "old" -> Some "old"
        | _ -> None

    let private tryConsumeValue (args: string array) index optionName valueLabel allowEmpty =
        let inlinePrefix = optionName + "="
        let arg = args.[index]

        if arg.StartsWith(inlinePrefix) then
            let value = arg.Substring(inlinePrefix.Length)

            if allowEmpty || not (String.IsNullOrWhiteSpace value) then
                Ok(value, index + 1)
            else
                Error $"Missing {valueLabel} after {optionName}."
        elif index + 1 < args.Length && not (args.[index + 1].StartsWith("--")) then
            Ok(args.[index + 1], index + 2)
        else
            Error $"Missing {valueLabel} after {optionName}."

    /// Expands one revision argument: ^X, A..B and A...B ranges (an empty side means HEAD), or a plain revision.
    let revisionTargets (arg: string) : StartupTarget list =
        let side (value: string) = if String.IsNullOrWhiteSpace value then "HEAD" else value
        if arg.StartsWith "^" && arg.Length > 1 then
            [ Exclude(arg.Substring 1) ]
        elif arg.Contains "..." then
            let index = arg.IndexOf "..."
            let left, right = side (arg.Substring(0, index)), side (arg.Substring(index + 3))
            [ Revision left; Revision right; ExcludeMergeBase(left, right) ]
        elif arg.Contains ".." then
            let index = arg.IndexOf ".."
            [ Revision(side (arg.Substring(index + 2))); Exclude(side (arg.Substring(0, index))) ]
        else
            [ Revision arg ]

    let private quoteTerm (value: string) =
        if value |> Seq.exists Char.IsWhiteSpace then "\"" + value.Replace("\"", "") + "\"" else value

    let getHelpText () =
        "Usage: gitkay [options] [<revision>...] [-- <path>...]\n\n" +
        "Options:\n" +
        "  --help, -h               Show this help message\n" +
        "  --version, -v            Show version information\n" +
        "  --all                    Show commits from all branches and tags\n" +
        "  --branch <name>          Show commits from the specified branch\n" +
        "  --sha <hash>             Show commits from the specified commit hash\n" +
        "  --tag <name>             Show commits from the specified tag\n" +
        "  --select <revision>      Select a commit on startup: hash, short hash, branch, tag or HEAD~n\n" +
        "                           (also --select-commit, as in gitk)\n" +
        "  --search <query>         Filter commits by the specified search query\n" +
        "  --search-scope <scope>   Set search mode (commit, path, diff)\n" +
        "  --show-branch-refs       Show branch and tag markers in the history list\n" +
        "  --hide-branch-refs       Hide branch and tag markers in the history list\n" +
        "  --show-stashes           Show stashes in the history list\n" +
        "  --hide-stashes           Hide stashes in the history list\n" +
        "  --diff-context <n>       Number of context lines to show in diffs\n" +
        "  --diff-presentation <m>  Diff presentation mode (diff, side-by-side, new, old)\n" +
        "  --log <file>             Write logs to the specified file\n\n" +
        "Filters (shown in the search bar, list limited to matches):\n" +
        "  --author <pattern>       author:<pattern>\n" +
        "  --grep <pattern>         message:<pattern>\n" +
        "  --since, --after <date>  after:<date>  (2024-05-01, \"2 weeks ago\", 3.days.ago)\n" +
        "  --until, --before <date> before:<date>\n" +
        "  -S <text>                diff:<text>   commits adding or removing the text\n" +
        "  -G <regex>               diff:<regex>  with regex search on\n\n" +
        "Arguments:\n" +
        "  <sha>                    Open with that commit selected (full or short hash)\n" +
        "  <revision>...            History tips: branches, tags, hashes; several allowed\n" +
        "  A..B  A...B  ^X          Ranges: in B not A; in either not both; exclude X\n" +
        "  -- <path>...             Only commits touching these paths\n"

    /// Maps a git log filter option onto the equivalent search prefix.
    let private filterTerm (option: string) (value: string) =
        let prefix =
            match option with
            | "--author" -> "author:"
            | "--grep" -> "message:"
            | "--since" | "--after" -> "after:"
            | _ -> "before:"
        prefix + quoteTerm value

    let parseStartupOptions (args: string array) =
        let mutable index = 0
        let mutable hasAll = false
        let mutable targets = ResizeArray<StartupTarget>()
        let mutable showBranchRefs = defaultStartupOptions.ShowBranchRefs
        let mutable showStashes = defaultStartupOptions.ShowStashes
        let mutable diffContextLines = defaultStartupOptions.DiffContextLines
        let mutable diffPresentationModeKey = defaultStartupOptions.DiffPresentationModeKey
        let mutable searchQuery = defaultStartupOptions.SearchQuery
        let mutable searchScopeKey = defaultStartupOptions.SearchScopeKey
        let mutable selectedCommitHash = defaultStartupOptions.SelectedCommitHash
        let mutable logFile = defaultStartupOptions.LogFile
        let mutable helpRequested = false
        let mutable versionRequested = false
        let positionals = ResizeArray<string>()
        let paths = ResizeArray<string>()
        let filterTerms = ResizeArray<string>()
        let mutable searchUseRegex = false

        let rec loop () =
            if index >= args.Length then
                // A single bare hash selects that commit in the normal history; anything else names history tips.
                let isHash (value: string) = value.Length >= 4 && value.Length <= 40 && value |> Seq.forall Uri.IsHexDigit
                match List.ofSeq positionals with
                | [ single ] when isHash single && targets.Count = 0 ->
                    if selectedCommitHash.IsNone then selectedCommitHash <- Some single
                | many ->
                    for positional in many do
                        targets.AddRange(revisionTargets positional)

                let exclusions =
                    targets |> Seq.filter (function Exclude _ | ExcludeMergeBase _ -> true | _ -> false) |> List.ofSeq
                let pathTargets = paths |> Seq.map Path |> List.ofSeq
                let startupTargets =
                    if hasAll then
                        StartupTarget.All :: exclusions @ pathTargets
                    else
                        List.ofSeq targets @ pathTargets

                let combinedQuery =
                    [ yield! (if String.IsNullOrWhiteSpace searchQuery then [] else [ searchQuery.Trim() ]); yield! filterTerms ]
                    |> String.concat " "

                Ok
                    {
                        StartupTargets = startupTargets
                        ShowBranchRefs = showBranchRefs
                        ShowStashes = showStashes
                        DiffContextLines = diffContextLines
                        DiffPresentationModeKey = diffPresentationModeKey
                        SearchQuery = combinedQuery
                        SearchScopeKey = searchScopeKey
                        SelectedCommitHash = selectedCommitHash
                        SearchUseRegex = searchUseRegex
                        ShowOnlyMatches = filterTerms.Count > 0
                        LogFile = logFile
                        HelpRequested = helpRequested
                        VersionRequested = versionRequested
                    }
            else
                match args.[index] with
                | "--help" | "-h" ->
                    helpRequested <- true
                    index <- index + 1
                    loop ()
                | "--version" | "-v" ->
                    versionRequested <- true
                    index <- index + 1
                    loop ()
                | "--all" ->
                    hasAll <- true
                    index <- index + 1
                    loop ()
                | "--show-branch-refs" ->
                    showBranchRefs <- true
                    index <- index + 1
                    loop ()
                | "--hide-branch-refs" ->
                    showBranchRefs <- false
                    index <- index + 1
                    loop ()
                | "--show-stashes" ->
                    showStashes <- true
                    index <- index + 1
                    loop ()
                | "--hide-stashes" ->
                    showStashes <- false
                    index <- index + 1
                    loop ()
                | "--diff-context" ->
                    match tryConsumeValue args index "--diff-context" "diff context line count" false with
                    | Error err -> Error err
                    | Ok (value, nextIndex) ->
                        match Parse.int value with
                        | Ok parsed ->
                            diffContextLines <- max 0 parsed
                            index <- nextIndex
                            loop ()
                        | Error _ ->
                            Error ("Invalid diff context line count: " + value)
                | arg when arg.StartsWith("--diff-context=") ->
                    let value = arg.Substring("--diff-context=".Length)

                    match Parse.int value with
                    | Ok parsed ->
                        diffContextLines <- max 0 parsed
                        index <- index + 1
                        loop ()
                    | Error _ ->
                        Error ("Invalid diff context line count: " + value)
                | "--diff-presentation" ->
                    match tryConsumeValue args index "--diff-presentation" "diff presentation mode" false with
                    | Error err -> Error err
                    | Ok (value, nextIndex) ->
                        match tryParseDiffPresentationModeKey value with
                        | Some modeKey ->
                            diffPresentationModeKey <- modeKey
                            index <- nextIndex
                            loop ()
                        | None ->
                            Error ("Invalid diff presentation mode: " + value)
                | arg when arg.StartsWith("--diff-presentation=") ->
                    let value = arg.Substring("--diff-presentation=".Length)

                    match tryParseDiffPresentationModeKey value with
                    | Some modeKey ->
                        diffPresentationModeKey <- modeKey
                        index <- index + 1
                        loop ()
                    | None ->
                        Error ("Invalid diff presentation mode: " + value)
                | "--search" ->
                    match tryConsumeValue args index "--search" "search query" true with
                    | Error err -> Error err
                    | Ok (value, nextIndex) ->
                        searchQuery <- value
                        index <- nextIndex
                        loop ()
                | arg when arg.StartsWith("--search=") ->
                    searchQuery <- arg.Substring("--search=".Length)
                    index <- index + 1
                    loop ()
                | "--search-scope" ->
                    match tryConsumeValue args index "--search-scope" "search scope" false with
                    | Error err -> Error err
                    | Ok (value, nextIndex) ->
                        match tryParseSearchScopeKey value with
                        | Some scopeKey ->
                            searchScopeKey <- scopeKey
                            index <- nextIndex
                            loop ()
                        | None ->
                            Error ("Invalid search scope: " + value)
                | arg when arg.StartsWith("--search-scope=") ->
                    let value = arg.Substring("--search-scope=".Length)

                    match tryParseSearchScopeKey value with
                    | Some scopeKey ->
                        searchScopeKey <- scopeKey
                        index <- index + 1
                        loop ()
                    | None ->
                        Error ("Invalid search scope: " + value)
                | "--select" | "--select-commit" as option ->
                    match tryConsumeValue args index option "revision" false with
                    | Error err -> Error err
                    | Ok (value, nextIndex) ->
                        selectedCommitHash <- Some value
                        index <- nextIndex
                        loop ()
                | arg when arg.StartsWith("--select=") || arg.StartsWith("--select-commit=") ->
                    let value = arg.Substring(arg.IndexOf('=') + 1)
                    selectedCommitHash <- Some value
                    index <- index + 1
                    loop ()
                | "--log" ->
                    match tryConsumeValue args index "--log" "log file" false with
                    | Error err -> Error err
                    | Ok (value, nextIndex) ->
                        logFile <- Some value
                        index <- nextIndex
                        loop ()
                | arg when arg.StartsWith("--log=") ->
                    logFile <- Some (arg.Substring("--log=".Length))
                    index <- index + 1
                    loop ()
                | "--branch" ->
                    if hasAll then
                        index <-
                            if index + 1 < args.Length && not (args.[index + 1].StartsWith("--")) then
                                index + 2
                            else
                                index + 1

                        loop ()
                    else
                        match tryConsumeValue args index "--branch" "branch name" false with
                        | Error err -> Error err
                        | Ok (value, nextIndex) ->
                            targets.Add(StartupTarget.Branch value)
                            index <- nextIndex
                            loop ()
                | arg when arg.StartsWith("--branch=") ->
                    let value = arg.Substring("--branch=".Length)

                    if not hasAll then
                        targets.Add(StartupTarget.Branch value)

                    index <- index + 1
                    loop ()
                | "--sha" ->
                    if hasAll then
                        index <-
                            if index + 1 < args.Length && not (args.[index + 1].StartsWith("--")) then
                                index + 2
                            else
                                index + 1

                        loop ()
                    else
                        match tryConsumeValue args index "--sha" "commit hash" false with
                        | Error err -> Error err
                        | Ok (value, nextIndex) ->
                            targets.Add(StartupTarget.Sha value)
                            index <- nextIndex
                            loop ()
                | arg when arg.StartsWith("--sha=") ->
                    let value = arg.Substring("--sha=".Length)

                    if not hasAll then
                        targets.Add(StartupTarget.Sha value)

                    index <- index + 1
                    loop ()
                | "--tag" ->
                    if hasAll then
                        index <-
                            if index + 1 < args.Length && not (args.[index + 1].StartsWith("--")) then
                                index + 2
                            else
                                index + 1

                        loop ()
                    else
                        match tryConsumeValue args index "--tag" "tag name" false with
                        | Error err -> Error err
                        | Ok (value, nextIndex) ->
                            targets.Add(StartupTarget.Tag value)
                            index <- nextIndex
                            loop ()
                | arg when arg.StartsWith("--tag=") ->
                    let value = arg.Substring("--tag=".Length)

                    if not hasAll then
                        targets.Add(StartupTarget.Tag value)

                    index <- index + 1
                    loop ()
                | "--" ->
                    paths.AddRange(args |> Seq.skip (index + 1))
                    index <- args.Length
                    loop ()
                | "--author" | "--grep" | "--since" | "--after" | "--until" | "--before" as option ->
                    match tryConsumeValue args index option "value" false with
                    | Error err -> Error err
                    | Ok(value, nextIndex) ->
                        filterTerms.Add(filterTerm option value)
                        index <- nextIndex
                        loop ()
                | arg when [ "--author="; "--grep="; "--since="; "--after="; "--until="; "--before=" ] |> List.exists arg.StartsWith ->
                    let option = arg.Substring(0, arg.IndexOf '=')
                    filterTerms.Add(filterTerm option (arg.Substring(arg.IndexOf '=' + 1)))
                    index <- index + 1
                    loop ()
                | "-S" | "-G" as option when index + 1 < args.Length ->
                    filterTerms.Add("diff:" + quoteTerm args.[index + 1])
                    if option = "-G" then searchUseRegex <- true
                    index <- index + 2
                    loop ()
                | arg when (arg.StartsWith "-S" || arg.StartsWith "-G") && arg.Length > 2 ->
                    filterTerms.Add("diff:" + quoteTerm (arg.Substring 2))
                    if arg.StartsWith "-G" then searchUseRegex <- true
                    index <- index + 1
                    loop ()
                | arg when not (arg.StartsWith "-") || (arg.StartsWith "^" && arg.Length > 1) ->
                    positionals.Add arg
                    index <- index + 1
                    loop ()
                | arg ->
                    Error ("Unrecognized startup argument: " + arg)

        loop ()

    let parseStartupTargets args =
        parseStartupOptions args |> Result.map _.StartupTargets

    let parseSearchScope value = GitSearch.parseMode value
