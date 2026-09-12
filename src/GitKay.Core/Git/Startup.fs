namespace GitKay.Core

open System

/// Startup argument parsing and normalization. This module is pure: it performs no Git or host effects.
module GitStartup =
    type StartupTarget =
        | All
        | Branch of string
        | Sha of string
        | Tag of string
        | Revision of string

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
            SearchScopeKey = "all"
            SelectedCommitHash = None
            LogFile = None
            HelpRequested = false
            VersionRequested = false
        }

    let private tryParseSearchScopeKey (value: string) =
        match value.Trim().ToLowerInvariant() with
        | "hash" -> Some "hash"
        | "message" -> Some "message"
        | "author" -> Some "author"
        | "path" -> Some "path"
        | "text" -> Some "text"
        | "ref" -> Some "ref"
        | "all" -> Some "all"
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
                Error (sprintf "Missing %s after %s." valueLabel optionName)
        elif index + 1 < args.Length && not (args.[index + 1].StartsWith("--")) then
            Ok(args.[index + 1], index + 2)
        else
            Error (sprintf "Missing %s after %s." valueLabel optionName)

    let getHelpText () =
        "Usage: gitkay [options] [revision]\n\n" +
        "Options:\n" +
        "  --help, -h               Show this help message\n" +
        "  --version, -v            Show version information\n" +
        "  --all                    Show commits from all branches and tags\n" +
        "  --branch <name>          Show commits from the specified branch\n" +
        "  --sha <hash>             Show commits from the specified commit hash\n" +
        "  --tag <name>             Show commits from the specified tag\n" +
        "  --select <hash>          Select the specified commit on startup\n" +
        "  --search <query>         Filter commits by the specified search query\n" +
        "  --search-scope <scope>   Set search scope (all, hash, message, author, path, text, ref)\n" +
        "  --show-branch-refs       Show branch and tag markers in the history list\n" +
        "  --hide-branch-refs       Hide branch and tag markers in the history list\n" +
        "  --show-stashes           Show stashes in the history list\n" +
        "  --hide-stashes           Hide stashes in the history list\n" +
        "  --diff-context <n>       Number of context lines to show in diffs\n" +
        "  --diff-presentation <m>  Diff presentation mode (diff, side-by-side, new, old)\n" +
        "  --log <file>             Write logs to the specified file\n\n" +
        "Arguments:\n" +
        "  [revision]               Optional branch, tag, or commit hash to use as the history tip\n"

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
        let mutable positionalTargetSeen = false

        let rec loop () =
            if index >= args.Length then
                let startupTargets =
                    if hasAll then
                        [ StartupTarget.All ]
                    else
                        List.ofSeq targets

                Ok
                    {
                        StartupTargets = startupTargets
                        ShowBranchRefs = showBranchRefs
                        ShowStashes = showStashes
                        DiffContextLines = diffContextLines
                        DiffPresentationModeKey = diffPresentationModeKey
                        SearchQuery = searchQuery
                        SearchScopeKey = searchScopeKey
                        SelectedCommitHash = selectedCommitHash
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
                        match Int32.TryParse value with
                        | true, parsed ->
                            diffContextLines <- max 0 parsed
                            index <- nextIndex
                            loop ()
                        | false, _ ->
                            Error (sprintf "Invalid diff context line count: %s" value)
                | arg when arg.StartsWith("--diff-context=") ->
                    let value = arg.Substring("--diff-context=".Length)

                    match Int32.TryParse value with
                    | true, parsed ->
                        diffContextLines <- max 0 parsed
                        index <- index + 1
                        loop ()
                    | false, _ ->
                        Error (sprintf "Invalid diff context line count: %s" value)
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
                            Error (sprintf "Invalid diff presentation mode: %s" value)
                | arg when arg.StartsWith("--diff-presentation=") ->
                    let value = arg.Substring("--diff-presentation=".Length)

                    match tryParseDiffPresentationModeKey value with
                    | Some modeKey ->
                        diffPresentationModeKey <- modeKey
                        index <- index + 1
                        loop ()
                    | None ->
                        Error (sprintf "Invalid diff presentation mode: %s" value)
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
                            Error (sprintf "Invalid search scope: %s" value)
                | arg when arg.StartsWith("--search-scope=") ->
                    let value = arg.Substring("--search-scope=".Length)

                    match tryParseSearchScopeKey value with
                    | Some scopeKey ->
                        searchScopeKey <- scopeKey
                        index <- index + 1
                        loop ()
                    | None ->
                        Error (sprintf "Invalid search scope: %s" value)
                | "--select" ->
                    match tryConsumeValue args index "--select" "commit hash" false with
                    | Error err -> Error err
                    | Ok (value, nextIndex) ->
                        selectedCommitHash <- Some value
                        index <- nextIndex
                        loop ()
                | arg when arg.StartsWith("--select=") ->
                    let value = arg.Substring("--select=".Length)
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
                | arg ->
                    if not (arg.StartsWith("-")) && not positionalTargetSeen then
                        positionalTargetSeen <- true
                        if not hasAll then
                            targets.Add(StartupTarget.Revision arg)
                        index <- index + 1
                        loop ()
                    else
                        Error (sprintf "Unrecognized startup argument: %s" arg)

        loop ()

    let parseStartupTargets args =
        parseStartupOptions args |> Result.map _.StartupTargets

    let parseSearchScope value =
        match tryParseSearchScopeKey value with
        | Some "hash" -> GitSearch.Scope.Hash
        | Some "message" -> GitSearch.Scope.Message
        | Some "author" -> GitSearch.Scope.Author
        | Some "path" -> GitSearch.Scope.Path
        | Some "text" -> GitSearch.Scope.Text
        | Some "ref" -> GitSearch.Scope.Ref
        | _ -> GitSearch.Scope.All
