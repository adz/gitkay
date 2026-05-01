namespace GitKay.Core

open System
open System.Diagnostics
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

    let parseStartupTargets (args: string array) =
        let rec loop index accumulated =
            if index >= args.Length then
                Ok (List.rev accumulated)
            else
                match args.[index] with
                | "--all" ->
                    Ok [ StartupTarget.All ]
                | "--branch" ->
                    if index + 1 >= args.Length then
                        Error "Missing branch name after --branch."
                    elif args.[index + 1].StartsWith("--") then
                        Error "Missing branch name after --branch."
                    else
                        loop (index + 2) (StartupTarget.Branch args.[index + 1] :: accumulated)
                | arg when arg.StartsWith("--branch=") ->
                    loop (index + 1) (StartupTarget.Branch (arg.Substring("--branch=".Length)) :: accumulated)
                | "--sha" ->
                    if index + 1 >= args.Length then
                        Error "Missing commit hash after --sha."
                    elif args.[index + 1].StartsWith("--") then
                        Error "Missing commit hash after --sha."
                    else
                        loop (index + 2) (StartupTarget.Sha args.[index + 1] :: accumulated)
                | arg when arg.StartsWith("--sha=") ->
                    loop (index + 1) (StartupTarget.Sha (arg.Substring("--sha=".Length)) :: accumulated)
                | "--tag" ->
                    if index + 1 >= args.Length then
                        Error "Missing tag name after --tag."
                    elif args.[index + 1].StartsWith("--") then
                        Error "Missing tag name after --tag."
                    else
                        loop (index + 2) (StartupTarget.Tag args.[index + 1] :: accumulated)
                | arg when arg.StartsWith("--tag=") ->
                    loop (index + 1) (StartupTarget.Tag (arg.Substring("--tag=".Length)) :: accumulated)
                | arg ->
                    Error (sprintf "Unrecognized startup argument: %s" arg)

        loop 0 []

    let parseSearchScope (value: string) =
        match value.Trim().ToLowerInvariant() with
        | "hash" -> SearchScope.Hash
        | "message" -> SearchScope.Message
        | "author" -> SearchScope.Author
        | "path" -> SearchScope.Path
        | "text" -> SearchScope.Text
        | "ref" -> SearchScope.Ref
        | _ -> SearchScope.All

    let private logTiming (message: string) =
        let line = sprintf "[timing] %s" message
        Trace.WriteLine line
        try
            Console.Error.WriteLine line
        with _ ->
            ()

    let private discoverRepositoryPath () =
        Repository.Discover(Environment.CurrentDirectory)

    let private withRepository (action: Repository -> Result<'T, string>) =
        try
            let repoPath = discoverRepositoryPath ()

            if String.IsNullOrWhiteSpace repoPath then
                Error "Could not locate a Git repository."
            else
                use repo = new Repository(repoPath)
                action repo
        with ex ->
            Error ex.Message

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

    let private buildDiffCacheEntryFromPatch (hash: string) (patch: Patch) =
        let fileList =
            patch
            |> Seq.cast<PatchEntryChanges>
            |> Seq.map toDiffFileSummaryFromPatchEntry
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

    let private buildCommitRefs (repo: Repository) =
        let refsByCommit = System.Collections.Generic.Dictionary<string, System.Collections.Generic.HashSet<Models.CommitRef>>()

        let addRef (hash: string) (kind: Models.CommitRefKind) (name: string) =
            if not (String.IsNullOrWhiteSpace hash) && not (String.IsNullOrWhiteSpace name) then
                let bucket =
                    match refsByCommit.TryGetValue hash with
                    | true, existing -> existing
                    | false, _ ->
                        let created = System.Collections.Generic.HashSet<Models.CommitRef>()
                        refsByCommit.[hash] <- created
                        created

                bucket.Add { Name = name; Kind = kind } |> ignore

        for branch in repo.Branches do
            if not (isNull branch.Tip) then
                let kind =
                    if branch.IsRemote then
                        Models.CommitRefKind.Remote
                    else
                        Models.CommitRefKind.Branch

                addRef branch.Tip.Sha kind branch.FriendlyName

        for tag in repo.Tags do
            match tag.PeeledTarget with
            | :? Commit as commit -> addRef commit.Sha Models.CommitRefKind.Tag tag.FriendlyName
            | _ -> ()

        repo.Stashes
        |> Seq.mapi (fun index stash -> index, stash)
        |> Seq.iter (fun (index, stash) ->
            if not (isNull stash.WorkTree) then
                addRef stash.WorkTree.Sha Models.CommitRefKind.Stash (sprintf "stash@{%d}" index))

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

    let private buildDefaultHistoryRoots (repo: Repository) =
        seq {
            yield! repo.Refs |> Seq.map box
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
                use patch =
                    match commit.Parents |> Seq.tryHead with
                    | Some parent -> repo.Diff.Compare<Patch>(parent.Tree, commit.Tree)
                    | None -> repo.Diff.Compare<Patch>(null, commit.Tree)

                Ok (buildDiffCacheEntryFromPatch hash patch)
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

    let private loadDiffFileContent (hash: string) (oldPath: string) (newPath: string) =
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
                use patch =
                    match commit.Parents |> Seq.tryHead with
                    | Some parent -> repo.Diff.Compare<Patch>(parent.Tree, commit.Tree, candidatePaths, ExplicitPathsOptions())
                    | None -> repo.Diff.Compare<Patch>(null, commit.Tree, candidatePaths, ExplicitPathsOptions())

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

    let private resolveStartupTargets (repo: Repository) (targets: StartupTarget list) =
        let hasAll = targets |> List.exists ((=) StartupTarget.All)

        if hasAll || List.isEmpty targets then
            Ok (box (buildDefaultHistoryRoots repo))
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

    let fetchHistory (targets: StartupTarget list) =
        withRepository (fun repo ->
            match resolveStartupTargets repo targets with
            | Error err -> Error err
            | Ok roots ->
                let refsByCommit = buildCommitRefs repo
                let filter = CommitFilter()
                filter.IncludeReachableFrom <- roots
                filter.SortBy <- CommitSortStrategies.Topological ||| CommitSortStrategies.Time

                repo.Commits.QueryBy(filter)
                |> Seq.map (fun commit ->
                    let refs =
                        match refsByCommit.TryFind commit.Sha with
                        | Some names -> names
                        | None -> []

                    toCommitModel commit refs)
                |> Seq.toList
                |> Ok)

    let fetchDiff (hash: string) =
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
                    match loadDiffFileContent hash file.OldPath file.NewPath with
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

    let fetchDiffFileContent (hash: string) (oldPath: string) (newPath: string) =
        let startedAtTicks = Stopwatch.GetTimestamp()
        let key =
            {
                Hash = hash
                OldPath = oldPath
                NewPath = newPath
            }

        match diffFileContentCache.TryGetValue key with
        | true, file ->
            let elapsed = Stopwatch.GetElapsedTime(startedAtTicks)
            logTiming (sprintf "file diff cache hit hash=%s path=%s -> %s elapsed=%.1fms" hash oldPath newPath elapsed.TotalMilliseconds)
            Ok file
        | false, _ ->
            let result = loadDiffFileContent hash oldPath newPath

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

    let searchCommits (commits: Models.Commit list) (query: string) (scope: SearchScope) =
        searchCommitsWithDiffLoader commits query scope fetchDiff

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
