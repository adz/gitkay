namespace GitKay.Core

open System
open System.Diagnostics
open System.IO
open System.Collections.Concurrent
open System.Text.RegularExpressions
open GitKay.Core.Models
open LibGit2Sharp

module GitService =

    type StartupTarget =
        | All
        | Branch of string
        | Sha of string
        | Tag of string

    type SearchScope =
        | All
        | Hash
        | Message
        | Author
        | Path
        | Text
        | Ref

    type SearchResult =
        {
            Commit: Models.Commit
            MatchKinds: string list
            MatchSummary: string
            MatchedPaths: string list
            MatchedRefs: string list
        }

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
        "Usage: gitkay [options]\n\n" +
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
        "  --diff-presentation <m>  Diff presentation mode (diff, side-by-side, new, old)\n"

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
        let mutable helpRequested = false
        let mutable versionRequested = false

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
                    Error (sprintf "Unrecognized startup argument: %s" arg)

        loop ()

    let parseStartupTargets (args: string array) =
        parseStartupOptions args |> Result.map (fun options -> options.StartupTargets)

    let parseSearchScope (value: string) =
        match tryParseSearchScopeKey value with
        | Some "hash" -> SearchScope.Hash
        | Some "message" -> SearchScope.Message
        | Some "author" -> SearchScope.Author
        | Some "path" -> SearchScope.Path
        | Some "text" -> SearchScope.Text
        | Some "ref" -> SearchScope.Ref
        | _ -> SearchScope.All

    let private logTiming (message: string) =
        let line = sprintf "[timing] %s" message
        Trace.WriteLine line
        try
            Console.Error.WriteLine line
        with _ ->
            ()

    let private discoverRepositoryPath () =
        let candidateRoots =
            seq {
                yield Environment.CurrentDirectory
                yield AppContext.BaseDirectory

                match Environment.ProcessPath with
                | null -> ()
                | processPath ->
                    yield Path.GetDirectoryName(processPath)

                    try
                        let processFile = FileInfo(processPath)
                        match processFile.ResolveLinkTarget(true) with
                        | null -> ()
                        | resolved -> yield Path.GetDirectoryName(resolved.FullName)
                    with _ ->
                        ()
            }
            |> Seq.choose (fun value ->
                if String.IsNullOrWhiteSpace value then None else Some value)
            |> Seq.distinct
            |> Seq.toList

        let rec tryDiscover roots errors =
            match roots with
            | [] -> Error (sprintf "Could not locate a Git repository. Tried: %s" (String.Join(", ", List.rev errors)))
            | root :: rest ->
                try
                    let repoPath = Repository.Discover(root)

                    if String.IsNullOrWhiteSpace repoPath then
                        tryDiscover rest (root :: errors)
                    else
                        Ok repoPath
                with _ ->
                    tryDiscover rest (root :: errors)

        tryDiscover candidateRoots []

    let private withRepository (action: Repository -> Result<'T, string>) =
        try
            match discoverRepositoryPath () with
            | Error err -> Error err
            | Ok repoPath ->
                use repo = new Repository(repoPath)
                action repo
        with ex ->
            Error ex.Message

    let tryDiscoverRepositoryPath () =
        match discoverRepositoryPath () with
        | Ok repoPath -> Path.TrimEndingDirectorySeparator(Path.GetFullPath(repoPath))
        | Error _ -> null

    let private quoteArg (value: string) =
        "\"" + value.Replace("\"", "\\\"") + "\""

    let executeGitCommand (args: string) =
        let startInfo = ProcessStartInfo("git", args)
        startInfo.RedirectStandardOutput <- true
        startInfo.RedirectStandardError <- true
        startInfo.UseShellExecute <- false
        startInfo.CreateNoWindow <- true
        
        use process' = new Process()
        process'.StartInfo <- startInfo
        process'.Start() |> ignore
        
        let output = process'.StandardOutput.ReadToEnd()
        let error = process'.StandardError.ReadToEnd()
        process'.WaitForExit()
        
        if process'.ExitCode <> 0 then
            Error error
        else
            Ok output

    let private getFirstParent (hash: string) =
        match executeGitCommand (sprintf "rev-list --parents -n 1 %s" hash) with
        | Ok output ->
            output.Trim().Split(' ', System.StringSplitOptions.RemoveEmptyEntries)
            |> Array.tryItem 1
        | Error _ ->
            None

    let parseCommitLine (line: string) : Models.Commit option =
        let parts = line.TrimEnd('\r').Split('|')
        if parts.Length >= 6 then
            Some {
                Hash = parts.[0]
                Timestamp = int64 parts.[1]
                AuthorName = parts.[2]
                AuthorEmail = parts.[3]
                Parents = parts.[4].Split(' ', System.StringSplitOptions.RemoveEmptyEntries) |> Array.toList
                Subject = parts.[5]
                Message = parts.[5]
                Refs = []
            }
        else
            None

    let private parseDiffHeader (line: string) =
        if line.StartsWith("diff --git ") then
            let parts = line.Split(' ', System.StringSplitOptions.RemoveEmptyEntries)
            if parts.Length >= 4 then
                Some(parts.[2], parts.[3])
            else
                None
        else
            None

    let private parseHunkHeader (line: string) =
        let matchResult = Regex.Match(line, "^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@")
        if matchResult.Success then
            let oldStart = int matchResult.Groups.[1].Value
            let newStart = int matchResult.Groups.[3].Value
            Some(oldStart, newStart)
        else
            None

    let parseBlamePorcelain (output: string) : Map<int, BlameInfo> =
        let lines = output.Replace("\r", "").Split('\n', System.StringSplitOptions.RemoveEmptyEntries)
        let mutable index = 0
        let mutable results = []

        while index < lines.Length do
            let headerParts = lines.[index].Split(' ', System.StringSplitOptions.RemoveEmptyEntries)
            if headerParts.Length >= 3 then
                let commitHash = headerParts.[0]
                let finalLineNumber = int headerParts.[2]
                let mutable authorName = ""
                let mutable authorEmail = ""
                let mutable authorTimestamp = 0L

                index <- index + 1

                while index < lines.Length && not (Regex.IsMatch(lines.[index], "^[0-9a-fA-F]{7,40}\\s")) do
                    let metadata = lines.[index]
                    if metadata.StartsWith("author ") then
                        authorName <- metadata.Substring(7)
                    elif metadata.StartsWith("author-mail ") then
                        authorEmail <- metadata.Substring(12).Trim().Trim('<', '>')
                    elif metadata.StartsWith("author-time ") then
                        authorTimestamp <- int64 (metadata.Substring(12).Trim())
                    index <- index + 1

                results <- (finalLineNumber, {
                    Hash = commitHash
                    AuthorName = authorName
                    AuthorEmail = authorEmail
                    Timestamp = authorTimestamp
                }) :: results
            else
                index <- index + 1

        results |> List.rev |> Map.ofList

    let private toCommitModel (commit: LibGit2Sharp.Commit) (refs: Models.CommitRef list) : Models.Commit =
        {
            Hash = commit.Sha
            Timestamp = commit.Author.When.ToUnixTimeSeconds()
            AuthorName = commit.Author.Name
            AuthorEmail = commit.Author.Email
            Parents = commit.Parents |> Seq.map (fun parent -> parent.Sha) |> Seq.toList
            Subject = commit.MessageShort
            Message = commit.Message
            Refs = refs
        }

    let parseDiff (output: string) : FileDiff list =
        let lines = output.Replace("\r", "").Split('\n')
        let mutable files = []
        let mutable currentFile : FileDiff option = None
        let mutable currentHunk : DiffHunk option = None
        let mutable oldLineNumber = 0
        let mutable newLineNumber = 0

        let flushHunk () =
            currentHunk
            |> Option.iter (fun h ->
                currentFile <- currentFile |> Option.map (fun f -> { f with Hunks = h :: f.Hunks }))
            currentHunk <- None

        let flushFile () =
            flushHunk ()
            currentFile |> Option.iter (fun f -> files <- f :: files)
            currentFile <- None

        for line in lines do
            if line.StartsWith("diff --git") then
                flushFile ()
                currentFile <-
                    match parseDiffHeader line with
                    | Some(oldPath, newPath) -> Some { OldPath = oldPath; NewPath = newPath; Hunks = [] }
                    | None -> Some { OldPath = ""; NewPath = ""; Hunks = [] }
                oldLineNumber <- 0
                newLineNumber <- 0
            elif line.StartsWith("--- ") then
                currentFile <-
                    currentFile
                    |> Option.map (fun f ->
                        let oldPath =
                            if line = "--- /dev/null" then "/dev/null"
                            elif line.StartsWith("--- a/") then line.Substring(6)
                            else line.Substring(4)

                        { f with OldPath = oldPath })
            elif line.StartsWith("+++ ") then
                currentFile <-
                    currentFile
                    |> Option.map (fun f ->
                        let newPath =
                            if line = "+++ /dev/null" then "/dev/null"
                            elif line.StartsWith("+++ b/") then line.Substring(6)
                            else line.Substring(4)

                        { f with NewPath = newPath })
            elif line.StartsWith("@@") then
                flushHunk ()
                match parseHunkHeader line with
                | Some(oldStart, newStart) ->
                    oldLineNumber <- oldStart
                    newLineNumber <- newStart
                    currentHunk <- Some { Header = line; Lines = [] }
                | None ->
                    currentHunk <- Some { Header = line; Lines = [] }
            elif line.StartsWith("+") && not (line.StartsWith("+++")) then
                currentHunk <-
                    currentHunk
                    |> Option.map (fun h ->
                        let diffLine : DiffLine =
                            {
                                Type = Added
                                Content = line.Substring(1)
                                OldLineNo = None
                                NewLineNo = Some newLineNumber
                            }

                        { h with Lines = diffLine :: h.Lines })
                newLineNumber <- newLineNumber + 1
            elif line.StartsWith("-") && not (line.StartsWith("---")) then
                currentHunk <-
                    currentHunk
                    |> Option.map (fun h ->
                        let diffLine : DiffLine =
                            {
                                Type = Removed
                                Content = line.Substring(1)
                                OldLineNo = Some oldLineNumber
                                NewLineNo = None
                            }

                        { h with Lines = diffLine :: h.Lines })
                oldLineNumber <- oldLineNumber + 1
            elif line.StartsWith(" ") then
                currentHunk <-
                    currentHunk
                    |> Option.map (fun h ->
                        let diffLine : DiffLine =
                            {
                                Type = Context
                                Content = line.Substring(1)
                                OldLineNo = Some oldLineNumber
                                NewLineNo = Some newLineNumber
                            }

                        {
                            h with
                                Lines = diffLine :: h.Lines
                        })
                oldLineNumber <- oldLineNumber + 1
                newLineNumber <- newLineNumber + 1

        flushFile ()

        files
        |> List.rev
        |> List.map (fun f -> { f with Hunks = f.Hunks |> List.rev |> List.map (fun h -> { h with Lines = h.Lines |> List.rev }) })

    type DiffFileKey =
        {
            OldPath: string
            NewPath: string
        }

    type private DiffFileContentCacheKey =
        {
            Hash: string
            OldPath: string
            NewPath: string
            ContextLines: int
        }

    type DiffFileSummary =
        {
            OldPath: string
            NewPath: string
            DisplayPath: string
        }

    type DiffSummary =
        {
            Hash: string
            FileCount: int
            AddedLines: int
            RemovedLines: int
        }

    type DiffCacheEntry =
        {
            Summary: DiffSummary
            FileList: DiffFileSummary list
        }

    let private diffCache = ConcurrentDictionary<string, DiffCacheEntry>()
    let private diffFileContentCache = ConcurrentDictionary<DiffFileContentCacheKey, FileDiff>()

    let private normalizeContextLines contextLines = max 0 contextLines

    let private buildCompareOptions contextLines =
        let options = CompareOptions()
        options.ContextLines <- normalizeContextLines contextLines
        options

    let private buildDisplayPath oldPath newPath =
        if oldPath = "/dev/null" then
            sprintf "%s (new file)" newPath
        elif newPath = "/dev/null" then
            sprintf "%s (deleted)" oldPath
        elif oldPath = newPath then
            newPath
        else
            sprintf "%s -> %s" oldPath newPath

    let private toDiffFileSummary (file: FileDiff) =
        {
            OldPath = file.OldPath
            NewPath = file.NewPath
            DisplayPath = buildDisplayPath file.OldPath file.NewPath
        }

    let private toDiffFileKey (file: FileDiff) =
        {
            OldPath = file.OldPath
            NewPath = file.NewPath
        }

    let private toDiffFileSummaryFromPatchEntry (entry: PatchEntryChanges) =
        let oldPath =
            if String.IsNullOrWhiteSpace entry.OldPath then
                "/dev/null"
            else
                entry.OldPath

        let newPath =
            if String.IsNullOrWhiteSpace entry.Path then
                "/dev/null"
            else
                entry.Path

        {
            OldPath = oldPath
            NewPath = newPath
            DisplayPath = buildDisplayPath oldPath newPath
        }

    let buildDiffCacheEntry (hash: string) (files: FileDiff list) =
        let addedLines =
            files
            |> List.sumBy (fun file ->
                file.Hunks
                |> List.sumBy (fun hunk ->
                    hunk.Lines |> List.sumBy (fun line -> if line.Type = Added then 1 else 0)))

        let removedLines =
            files
            |> List.sumBy (fun file ->
                file.Hunks
                |> List.sumBy (fun hunk ->
                    hunk.Lines |> List.sumBy (fun line -> if line.Type = Removed then 1 else 0)))

        {
            Summary =
                {
                    Hash = hash
                    FileCount = files.Length
                    AddedLines = addedLines
                    RemovedLines = removedLines
                }
            FileList = files |> List.map toDiffFileSummary
        }

    let private buildDiffCacheEntryFromPatch (hash: string) (isRootCommit: bool) (patch: Patch) =
        let normalizeEntry (entry: PatchEntryChanges) =
            if isRootCommit && String.Equals(entry.OldPath, entry.Path, StringComparison.Ordinal) then
                let newPath = if String.IsNullOrWhiteSpace entry.Path then "/dev/null" else entry.Path
                {
                    OldPath = "/dev/null"
                    NewPath = newPath
                    DisplayPath = buildDisplayPath "/dev/null" newPath
                }
            else
                toDiffFileSummaryFromPatchEntry entry

        let fileList =
            patch
            |> Seq.cast<PatchEntryChanges>
            |> Seq.map normalizeEntry
            |> Seq.toList

        {
            Summary =
                {
                    Hash = hash
                    FileCount = fileList.Length
                    AddedLines = patch.LinesAdded
                    RemovedLines = patch.LinesDeleted
            }
            FileList = fileList
        }

    let private buildCommitRefs (includeStashes: bool) (repo: Repository) =
        let refsByCommit = System.Collections.Generic.Dictionary<string, System.Collections.Generic.HashSet<Models.CommitRef>>()

        let addRef (hash: string) (kind: Models.CommitRefKind) (name: string) (isCurrentHead: bool) =
            if not (String.IsNullOrWhiteSpace hash) && not (String.IsNullOrWhiteSpace name) then
                let bucket =
                    match refsByCommit.TryGetValue hash with
                    | true, existing -> existing
                    | false, _ ->
                        let created = System.Collections.Generic.HashSet<Models.CommitRef>()
                        refsByCommit.[hash] <- created
                        created
                bucket.Add { Name = name; Kind = kind; IsCurrentHead = isCurrentHead } |> ignore

        for branch in repo.Branches do
            if not (isNull branch.Tip)
               && not (
                   branch.IsRemote
                   && branch.FriendlyName.EndsWith("/HEAD", StringComparison.OrdinalIgnoreCase)
               ) then
                let kind =
                    if branch.IsRemote then
                        Models.CommitRefKind.Remote
                    else
                        Models.CommitRefKind.Branch

                addRef branch.Tip.Sha kind branch.FriendlyName branch.IsCurrentRepositoryHead

        for tag in repo.Tags do
            match tag.PeeledTarget with
            | :? Commit as commit -> addRef commit.Sha Models.CommitRefKind.Tag tag.FriendlyName false
            | _ -> ()

        if includeStashes then
            repo.Stashes
            |> Seq.mapi (fun index stash -> index, stash)
            |> Seq.iter (fun (index, stash) ->
                if not (isNull stash.WorkTree) then
                    addRef stash.WorkTree.Sha Models.CommitRefKind.Stash (sprintf "stash@{%d}" index) false)

        refsByCommit
        |> Seq.map (fun kvp ->
            kvp.Key,
            kvp.Value
            |> Seq.sortWith (fun left right ->
                match compare (int left.Kind) (int right.Kind) with
                | 0 -> StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name)
                | comparison -> comparison)
            |> Seq.toList)
        |> Map.ofSeq

    let private buildDefaultHistoryRoots (includeStashes: bool) (repo: Repository) =
        seq {
            yield!
                repo.Refs
                |> Seq.filter (fun reference ->
                    not (String.Equals(reference.CanonicalName, "refs/stash", StringComparison.Ordinal)))
                |> Seq.map box
            if includeStashes then
                yield!
                    repo.Stashes
                    |> Seq.choose (fun stash ->
                        if isNull stash.WorkTree then
                            None
                        else
                            Some (box stash.WorkTree))
        }

    let private containsIgnoreCase (haystack: string) (needle: string) =
        haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0

    let private loadDiffCacheEntry (hash: string) =
        withRepository (fun repo ->
            let commit = repo.Lookup<LibGit2Sharp.Commit>(hash)

            if isNull commit then
                Error (sprintf "Commit not found: %s" hash)
            else
                let isRootCommit = commit.Parents |> Seq.isEmpty
                use patch =
                    match commit.Parents |> Seq.tryHead with
                    | Some parent -> repo.Diff.Compare<Patch>(parent.Tree, commit.Tree)
                    | None -> repo.Diff.Compare<Patch>(null, commit.Tree)

                Ok (buildDiffCacheEntryFromPatch hash isRootCommit patch)
        )

    let private getDiffCacheEntry (hash: string) =
        match diffCache.TryGetValue hash with
        | true, entry ->
            Ok (entry, true)
        | false, _ ->
            match loadDiffCacheEntry hash with
            | Error err -> Error err
            | Ok entry ->
                diffCache.TryAdd(hash, entry) |> ignore
                Ok (entry, false)

    let private loadDiffFileContent (contextLines: int) (hash: string) (oldPath: string) (newPath: string) =
        let candidatePaths =
            if oldPath = "/dev/null" then
                [ newPath ]
            elif newPath = "/dev/null" then
                [ oldPath ]
            elif oldPath = newPath then
                [ oldPath ]
            else
                [ oldPath; newPath ]

        withRepository (fun repo ->
            let commit = repo.Lookup<LibGit2Sharp.Commit>(hash)

            if isNull commit then
                Error (sprintf "Commit not found: %s" hash)
            else
                let compareOptions = buildCompareOptions contextLines
                use patch =
                    match commit.Parents |> Seq.tryHead with
                    | Some parent -> repo.Diff.Compare<Patch>(parent.Tree, commit.Tree, candidatePaths, ExplicitPathsOptions(), compareOptions)
                    | None -> repo.Diff.Compare<Patch>(null, commit.Tree, candidatePaths, ExplicitPathsOptions(), compareOptions)

                match parseDiff patch.Content with
                | [ file ] -> Ok file
                | [] -> Error (sprintf "File not found in commit %s: %s -> %s" hash oldPath newPath)
                | _ -> Error (sprintf "Multiple files matched in commit %s: %s -> %s" hash oldPath newPath)
        )

    let private loadCommit (repo: Repository) (hash: string) =
        match repo.Lookup<LibGit2Sharp.Commit>(hash) with
        | null -> Error (sprintf "Commit not found: %s" hash)
        | commit -> Ok commit

    let private buildCommitterSignature (repo: Repository) =
        match repo.Config.BuildSignature(DateTimeOffset.UtcNow) with
        | null -> Error "Could not determine the git user identity. Configure user.name and user.email."
        | signature -> Ok signature

    let private resolveStartupTargets (includeStashes: bool) (repo: Repository) (targets: StartupTarget list) =
        let hasAll = targets |> List.exists ((=) StartupTarget.All)

        if hasAll || List.isEmpty targets then
            Ok (box (buildDefaultHistoryRoots includeStashes repo))
        else
            let resolveTarget target =
                match target with
                | StartupTarget.All ->
                    Ok []
                | StartupTarget.Branch name ->
                    match repo.Branches.[name] with
                    | null -> Error (sprintf "Branch not found: %s" name)
                    | branch -> Ok [ box branch ]
                | StartupTarget.Sha hash ->
                    match repo.Lookup<LibGit2Sharp.Commit>(hash) with
                    | null -> Error (sprintf "Commit not found: %s" hash)
                    | commit -> Ok [ box commit ]
                | StartupTarget.Tag name ->
                    match repo.Tags.[name] with
                    | null -> Error (sprintf "Tag not found: %s" name)
                    | tag -> Ok [ box tag ]

            targets
            |> List.fold
                (fun state target ->
                    match state with
                    | Error _ as err -> err
                    | Ok resolved ->
                        match resolveTarget target with
                        | Ok additions -> Ok (resolved @ additions)
                        | Error err -> Error err)
                (Ok [])
            |> Result.map box

    let fetchHistory (includeStashes: bool) (targets: StartupTarget list) =
        withRepository (fun repo ->
            match resolveStartupTargets includeStashes repo targets with
            | Error err -> Error err
            | Ok roots ->
                let refsByCommit = buildCommitRefs includeStashes repo
                let filter = CommitFilter()
                filter.IncludeReachableFrom <- roots
                filter.SortBy <- CommitSortStrategies.Topological ||| CommitSortStrategies.Time

                repo.Commits.QueryBy(filter)
                |> Seq.distinctBy (fun commit -> commit.Sha)
                |> Seq.map (fun commit ->
                    let refs =
                        match refsByCommit.TryFind commit.Sha with
                        | Some names -> names
                        | None -> []

                    toCommitModel commit refs)
                |> Seq.toList
                |> Ok)

    let fetchDiff (contextLines: int) (hash: string) =
        let startedAtTicks = Stopwatch.GetTimestamp()

        match getDiffCacheEntry hash with
        | Error err ->
            let elapsed = Stopwatch.GetElapsedTime(startedAtTicks)
            logTiming (sprintf "diff load hash=%s elapsed=%.1fms error=%s" hash elapsed.TotalMilliseconds err)
            Error err
        | Ok (entry, wasCached) ->
            let rec loadFiles remainingFiles accumulated =
                match remainingFiles with
                | [] -> Ok (List.rev accumulated)
                | file :: rest ->
                    match loadDiffFileContent contextLines hash file.OldPath file.NewPath with
                    | Ok content -> loadFiles rest (content :: accumulated)
                    | Error err -> Error err

            let files = loadFiles entry.FileList []
            match files with
            | Error err ->
                let elapsed = Stopwatch.GetElapsedTime(startedAtTicks)
                logTiming (sprintf "diff load hash=%s elapsed=%.1fms error=%s" hash elapsed.TotalMilliseconds err)
                Error err
            | Ok files ->
                let elapsed = Stopwatch.GetElapsedTime(startedAtTicks)

                if wasCached then
                    logTiming (sprintf "diff cache hit hash=%s elapsed=%.1fms files=%d" hash elapsed.TotalMilliseconds files.Length)
                else
                    logTiming (sprintf "diff load hash=%s elapsed=%.1fms files=%d" hash elapsed.TotalMilliseconds files.Length)

                Ok files

    let fetchDiffSummary (hash: string) =
        match getDiffCacheEntry hash with
        | Error err -> Error err
        | Ok (entry, _) -> Ok entry.Summary

    let fetchDiffFileList (hash: string) =
        match getDiffCacheEntry hash with
        | Error err -> Error err
        | Ok (entry, _) -> Ok entry.FileList

    let fetchDiffFileContent (contextLines: int) (hash: string) (oldPath: string) (newPath: string) =
        let startedAtTicks = Stopwatch.GetTimestamp()
        let key =
            {
                Hash = hash
                OldPath = oldPath
                NewPath = newPath
                ContextLines = normalizeContextLines contextLines
            }

        match diffFileContentCache.TryGetValue key with
        | true, file ->
            let elapsed = Stopwatch.GetElapsedTime(startedAtTicks)
            logTiming (sprintf "file diff cache hit hash=%s path=%s -> %s elapsed=%.1fms" hash oldPath newPath elapsed.TotalMilliseconds)
            Ok file
        | false, _ ->
            let result = loadDiffFileContent contextLines hash oldPath newPath

            match result with
            | Error err ->
                let elapsed = Stopwatch.GetElapsedTime(startedAtTicks)
                logTiming (sprintf "file diff load hash=%s path=%s -> %s elapsed=%.1fms error=%s" hash oldPath newPath elapsed.TotalMilliseconds err)
                Error err
            | Ok file ->
                diffFileContentCache.TryAdd(key, file) |> ignore
                let elapsed = Stopwatch.GetElapsedTime(startedAtTicks)
                logTiming (sprintf "file diff load hash=%s path=%s -> %s elapsed=%.1fms" hash oldPath newPath elapsed.TotalMilliseconds)
                Ok file

    let private buildSearchSummary (matchKinds: string list) (paths: string list) (refs: string list) =
        let details = System.Collections.Generic.List<string>()

        if matchKinds |> List.contains "hash" then details.Add "hash"
        if matchKinds |> List.contains "message" then details.Add "message"
        if matchKinds |> List.contains "author" then details.Add "author"
        if matchKinds |> List.contains "path" then details.Add "path"
        if matchKinds |> List.contains "text" then details.Add "text"
        if matchKinds |> List.contains "ref" then details.Add "ref"

        if paths.Length > 0 then
            details.Add (sprintf "paths: %s" (String.Join(", ", paths)))

        if refs.Length > 0 then
            details.Add (sprintf "refs: %s" (String.Join(", ", refs)))

        String.Join("; ", details)

    let private searchCommitDiff
        (query: string)
        (loadDiff: string -> Result<FileDiff list, string>)
        (searchPaths: bool)
        (searchText: bool)
        (commit: Models.Commit) =
        match loadDiff commit.Hash with
        | Error _ -> None
        | Ok files ->
            let matchedPaths = System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
            let mutable textMatched = false

            for file in files do
                if searchPaths && (containsIgnoreCase file.OldPath query || containsIgnoreCase file.NewPath query || containsIgnoreCase (buildDisplayPath file.OldPath file.NewPath) query) then
                    matchedPaths.Add (buildDisplayPath file.OldPath file.NewPath) |> ignore

                if searchText && not textMatched then
                    let fileTextMatched =
                        file.Hunks
                        |> List.exists (fun hunk ->
                            hunk.Lines
                            |> List.exists (fun line -> containsIgnoreCase line.Content query))

                    if fileTextMatched then
                        textMatched <- true

            let pathMatches = matchedPaths |> Seq.toList

            if (searchPaths && pathMatches.Length > 0) || (searchText && textMatched) then
                Some(pathMatches, textMatched)
            else
                None

    let searchCommitsWithDiffLoader
        (commits: Models.Commit list)
        (query: string)
        (scope: SearchScope)
        (loadDiff: string -> Result<FileDiff list, string>)
        =
        let normalizedQuery = query.Trim()

        if String.IsNullOrWhiteSpace normalizedQuery then
            Error "Search query must not be empty."
        else
            let results =
                commits
                |> List.choose (fun commit ->
                    let matchKinds = System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    let matchedPaths = System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    let matchedRefs = System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)

                    let addMetadataMatches () =
                        match scope with
                        | SearchScope.Hash ->
                            if containsIgnoreCase commit.Hash normalizedQuery then
                                matchKinds.Add "hash" |> ignore
                        | SearchScope.Message ->
                            if containsIgnoreCase commit.Subject normalizedQuery || containsIgnoreCase commit.Message normalizedQuery then
                                matchKinds.Add "message" |> ignore
                        | SearchScope.Author ->
                            if containsIgnoreCase commit.AuthorName normalizedQuery || containsIgnoreCase commit.AuthorEmail normalizedQuery then
                                matchKinds.Add "author" |> ignore
                        | SearchScope.Ref ->
                            let refMatches =
                                commit.Refs
                                |> List.filter (fun reference -> containsIgnoreCase reference.Name normalizedQuery)

                            if refMatches.Length > 0 then
                                matchKinds.Add "ref" |> ignore
                                refMatches |> List.iter (fun reference -> matchedRefs.Add reference.Name |> ignore)
                        | SearchScope.Path
                        | SearchScope.Text ->
                            ()
                        | SearchScope.All ->
                            if containsIgnoreCase commit.Hash normalizedQuery then
                                matchKinds.Add "hash" |> ignore

                            if containsIgnoreCase commit.Subject normalizedQuery || containsIgnoreCase commit.Message normalizedQuery then
                                matchKinds.Add "message" |> ignore

                            if containsIgnoreCase commit.AuthorName normalizedQuery || containsIgnoreCase commit.AuthorEmail normalizedQuery then
                                matchKinds.Add "author" |> ignore

                            let refMatches =
                                commit.Refs
                                |> List.filter (fun reference -> containsIgnoreCase reference.Name normalizedQuery)

                            if refMatches.Length > 0 then
                                matchKinds.Add "ref" |> ignore
                                refMatches |> List.iter (fun reference -> matchedRefs.Add reference.Name |> ignore)

                    addMetadataMatches ()

                    match scope with
                    | SearchScope.Path
                    | SearchScope.Text
                    | SearchScope.All ->
                        let searchPaths, searchText =
                            match scope with
                            | SearchScope.Path -> true, false
                            | SearchScope.Text -> false, true
                            | SearchScope.All -> true, true
                            | _ -> false, false

                        match searchCommitDiff normalizedQuery loadDiff searchPaths searchText commit with
                        | Some(pathMatches, textMatch) ->
                            if pathMatches.Length > 0 then
                                matchKinds.Add "path" |> ignore
                                pathMatches |> List.iter (fun path -> matchedPaths.Add path |> ignore)

                            if textMatch then
                                matchKinds.Add "text" |> ignore
                        | None ->
                            ()
                    | _ ->
                        ()

                    if matchKinds.Count > 0 then
                        let kinds = matchKinds |> Seq.toList
                        let paths = matchedPaths |> Seq.toList
                        let refs = matchedRefs |> Seq.toList

                        Some
                            {
                                Commit = commit
                                MatchKinds = kinds
                                MatchSummary = buildSearchSummary kinds paths refs
                                MatchedPaths = paths
                                MatchedRefs = refs
                            }
                    else
                        None)

            Ok results

    let searchCommits (contextLines: int) (commits: Models.Commit list) (query: string) (scope: SearchScope) =
        searchCommitsWithDiffLoader commits query scope (fun hash -> fetchDiff contextLines hash)

    let fetchFileBlame (revision: string) (path: string) =
        if path = "/dev/null" then
            Ok Map.empty
        else
            let args =
                sprintf "blame --line-porcelain %s -- %s" revision (quoteArg path)

            match executeGitCommand args with
            | Ok output -> Ok (parseBlamePorcelain output)
            | Error err -> Error err

    let createTag (hash: string) (name: string) =
        withRepository (fun repo ->
            match loadCommit repo hash with
            | Error err -> Error err
            | Ok commit ->
                try
                    repo.ApplyTag(name, commit.Sha) |> ignore
                    Ok ""
                with ex ->
                    Error ex.Message)

    let createBranch (hash: string) (name: string) =
        withRepository (fun repo ->
            match loadCommit repo hash with
            | Error err -> Error err
            | Ok commit ->
                try
                    repo.CreateBranch(name, commit) |> ignore
                    Ok ""
                with ex ->
                    Error ex.Message)

    let cherryPick (hash: string) =
        withRepository (fun repo ->
            match loadCommit repo hash, buildCommitterSignature repo with
            | Error err, _ -> Error err
            | _, Error err -> Error err
            | Ok commit, Ok committer ->
                try
                    let result = repo.CherryPick(commit, committer)

                    match result.Status with
                    | CherryPickStatus.CherryPicked -> Ok ""
                    | status -> Error (sprintf "Cherry-pick failed: %A" status)
                with ex ->
                    Error ex.Message)

    let resetTo (hash: string) (hard: bool) =
        withRepository (fun repo ->
            match loadCommit repo hash with
            | Error err -> Error err
            | Ok commit ->
                try
                    let mode = if hard then ResetMode.Hard else ResetMode.Soft
                    repo.Reset(mode, commit)
                    Ok ""
                with ex ->
                    Error ex.Message)

    let revert (hash: string) =
        withRepository (fun repo ->
            match loadCommit repo hash, buildCommitterSignature repo with
            | Error err, _ -> Error err
            | _, Error err -> Error err
            | Ok commit, Ok committer ->
                try
                    let result = repo.Revert(commit, committer)

                    match result.Status with
                    | RevertStatus.Reverted -> Ok ""
                    | status -> Error (sprintf "Revert failed: %A" status)
                with ex ->
                    Error ex.Message)
