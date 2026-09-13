namespace GitKay.Core

open System
open System.Collections.Concurrent
open System.Text.RegularExpressions
open Axial
open Axial.Console
open Axial.FileSystem
open Axial.PlatformService
open Axial.Process
open Reified.Result
open Reified.ResultDSL
open GitKay.Core.Models
open GitKay.Core.GitStartup
open GitKay.Core.GitParsing
open LibGit2Sharp

module GitService =

    let private requireNotNull error value =
        value
        |> Result.failIf (box >> isNull)
        |> Result.orError error

    /// Repository discovery is a host-startup seam; failures are intentionally represented as null for the C# host.
    let tryDiscoverRepositoryPath () =
        match RepositoryDiscovery.discover () with
        | Ok repoPath -> repoPath
        | Error _ -> null

    type DiffFileKey =
        {
            OldPath: string
            NewPath: string
        }

    type internal DiffFileContentCacheKey =
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

    type GitCache() =
        // Bound once: a `member _.X = ConcurrentDictionary()` body is re-evaluated on every access, which
        // silently discarded every cached entry.
        let diff = ConcurrentDictionary<string, DiffCacheEntry>()
        let fileContent = ConcurrentDictionary<DiffFileContentCacheKey, FileDiff>()
        let diffGates = ConcurrentDictionary<string, obj>()
        member internal _.DiffGates = diffGates
        member internal _.Diff = diff
        member internal _.FileContent = fileContent

    type GitEnv =
        { RepoPath: string
          Runtime: BaseRuntime
          Processes: IProcess
          Cache: GitCache }
        interface IHasClock with member this.Clock = this.Runtime.Clock
        interface IHasProcess with member this.Process = this.Processes

    let environment repoPath =
        let runtime = BaseRuntime.liveValue
        { RepoPath = repoPath
          Runtime = runtime
          Processes = Process.live runtime.Clock FileSystem.live Console.live
          Cache = GitCache() }

    let executeGitCommand (arguments: string list) : Flow<GitEnv, GitError, string> =
        flow {
            let! repoPath = Flow.envWith _.RepoPath
            return!
                Process.commandArgs "git" arguments
                |> Process.workingDirectory repoPath
                |> Process.capture
                |> Flow.map _.StdOut
                |> Flow.mapError (fun error -> GitError.GitProcessFailed(arguments, error))
        }


    let private normalizeContextLines contextLines = max 0 contextLines

    let private buildCompareOptions contextLines =
        let options = CompareOptions()
        options.ContextLines <- normalizeContextLines contextLines
        options

    let private buildDisplayPath oldPath newPath =
        if oldPath = "/dev/null" then
            newPath + " (new file)"
        elif newPath = "/dev/null" then
            oldPath + " (deleted)"
        elif oldPath = newPath then
            newPath
        else
            $"{oldPath} -> {newPath}"

    let private toDiffFileSummary (file: FileDiff) =
        {
            OldPath = file.OldPath
            NewPath = file.NewPath
            DisplayPath = buildDisplayPath file.OldPath file.NewPath
        }

    let private toDiffFileSummaryFromChange (entry: TreeEntryChanges) =
        // Match the unified diff's file identity: added and deleted files name /dev/null on the missing side.
        let oldPath =
            if entry.Status = ChangeKind.Added || String.IsNullOrWhiteSpace entry.OldPath then
                "/dev/null"
            else
                entry.OldPath

        let newPath =
            if entry.Status = ChangeKind.Deleted || String.IsNullOrWhiteSpace entry.Path then
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

    // NativeAOT note: LibGit2Sharp's Compare<Patch> crashes in its native line callback under AOT.
    // TreeChanges, PatchStats and blob-to-blob ContentChanges are AOT-safe, so the read path uses only those.
    /// Added or deleted file count up to which similarity-based rename detection runs (git's default diff.renameLimit).
    [<Literal>]
    let fullRenameDetectionFileLimit = 1000

    let private loadDiffCacheEntry (repoPath: string) (hash: string) : Result<DiffCacheEntry, GitError> =
        result {
            use repo = new Repository(repoPath)
            let! (commit: LibGit2Sharp.Commit) =
                repo.Lookup<LibGit2Sharp.Commit>(hash) |> requireNotNull (GitError.CommitNotFound hash)

            let parentTree = commit.Parents |> Seq.tryHead |> Option.map _.Tree |> Option.toObj

            // Similarity-based rename detection reads every changed blob (~480ms for 686 files versus ~2ms
            // without). Like git's diff.renameLimit, use it up to the limit and pair only identical-content
            // renames beyond it, so huge commits stay responsive without splitting edited renames in normal ones.
            let renameCandidates =
                use plain = repo.Diff.Compare<TreeChanges>(parentTree, commit.Tree, CompareOptions(Similarity = SimilarityOptions.None))
                max (Seq.length plain.Added) (Seq.length plain.Deleted)

            let similarity =
                if renameCandidates <= fullRenameDetectionFileLimit then SimilarityOptions.Default else SimilarityOptions.Exact

            use changes = repo.Diff.Compare<TreeChanges>(parentTree, commit.Tree, CompareOptions(Similarity = similarity))
            let fileList = changes |> Seq.map toDiffFileSummaryFromChange |> Seq.toList

            return
                {
                    Summary = { Hash = hash; FileCount = fileList.Length; AddedLines = -1; RemovedLines = -1 }
                    FileList = fileList
                }
        }

    let private getDiffCacheEntry (hash: string) : Flow<GitEnv, GitError, DiffCacheEntry * bool> =
        flow {
            let! env = Flow.env
            match env.Cache.Diff.TryGetValue hash with
            | true, entry -> return (entry, true)
            | false, _ ->
                // Single-flight per commit: selection loads the file list and diff concurrently, and both
                // would otherwise run rename detection for the same commit at once.
                let gate = env.Cache.DiffGates.GetOrAdd(hash, fun _ -> obj ())
                let! entry, cached =
                    lock gate (fun () ->
                        match env.Cache.Diff.TryGetValue hash with
                        | true, entry -> Ok(entry, true)
                        | false, _ ->
                            loadDiffCacheEntry env.RepoPath hash
                            |> Result.map (fun entry ->
                                env.Cache.Diff.TryAdd(hash, entry) |> ignore
                                entry, false))
                return (entry, cached)
        }

    let private tryBlob (commit: LibGit2Sharp.Commit) (path: string) =
        if isNull commit || path = "/dev/null" then
            null
        else
            match commit.[path] with
            | null -> null
            | entry ->
                match entry.Target with
                | :? Blob as blob -> blob
                | _ -> null

    let private countLines (text: string) =
        let mutable newlines = 0
        for c in text do
            if c = '\n' then newlines <- newlines + 1
        if text.Length > 0 && not (text.EndsWith "\n") then newlines + 1 else newlines

    /// Diffs one file of an already-opened commit; callers share the repository handle across files.
    let private diffFileInCommit (repo: Repository) (compareOptions: CompareOptions) (commit: LibGit2Sharp.Commit) (parent: LibGit2Sharp.Commit) (oldPath: string) (newPath: string) : FileDiff =
        let oldBlob = tryBlob parent oldPath
        let newBlob = tryBlob commit newPath

        if isNull oldBlob && isNull newBlob then
            // Submodule links and other non-blob entries have no line content.
            { OldPath = oldPath; NewPath = newPath; Hunks = []; NewLineCount = None }
        else
            let changes = repo.Diff.Compare(oldBlob, newBlob, compareOptions)
            let hunks = if changes.IsBinaryComparison then [] else parseHunks changes.Patch
            let newLineCount =
                if isNull newBlob || newBlob.IsBinary then None else Some(countLines (newBlob.GetContentText()))
            { OldPath = oldPath; NewPath = newPath; Hunks = hunks; NewLineCount = newLineCount }

    /// Files per worker below which a commit's diffs load on a single thread.
    [<Literal>]
    let private parallelDiffMinimumFilesPerWorker = 24

    // Worker count only sizes CPU parallelism; it has no behavioural effect worth routing through 'env.
    // axial-allow-effect: environment
    let private diffWorkerCount = Math.Clamp(Environment.ProcessorCount / 2, 1, 4)

    /// Loads file diffs for one commit. Cold blob decompression and diffing dominate large commits, so files
    /// are split into contiguous chunks across workers, each with its own repository handle; order is preserved.
    let private loadDiffFileContents (contextLines: int) (hash: string) (files: (string * string) list) : Flow<GitEnv, GitError, FileDiff list> =
        flow {
            let! env = Flow.env
            let files = List.toArray files

            use repo = new Repository(env.RepoPath)
            let! (_: LibGit2Sharp.Commit) =
                repo.Lookup<LibGit2Sharp.Commit>(hash) |> requireNotNull (GitError.CommitNotFound hash)

            let workers = min diffWorkerCount (max 1 (files.Length / parallelDiffMinimumFilesPerWorker))
            let results = Array.zeroCreate<FileDiff> files.Length

            let loadRange (workerRepo: Repository) (start: int) (finish: int) =
                let commit = workerRepo.Lookup<LibGit2Sharp.Commit>(hash)
                let parent = commit.Parents |> Seq.tryHead |> Option.toObj
                let compareOptions = buildCompareOptions contextLines
                for index in start .. finish - 1 do
                    let oldPath, newPath = files.[index]
                    results.[index] <- diffFileInCommit workerRepo compareOptions commit parent oldPath newPath

            if workers <= 1 then
                loadRange repo 0 files.Length
            else
                let chunk = (files.Length + workers - 1) / workers
                System.Threading.Tasks.Parallel.For(0, workers, fun worker ->
                    let start = worker * chunk
                    let finish = min files.Length (start + chunk)
                    if start < finish then
                        if worker = 0 then
                            loadRange repo start finish
                        else
                            use workerRepo = new Repository(env.RepoPath)
                            loadRange workerRepo start finish)
                |> ignore

            return List.ofArray results
        }

    let private loadDiffFileContent (contextLines: int) (hash: string) (oldPath: string) (newPath: string) : Flow<GitEnv, GitError, FileDiff> =
        loadDiffFileContents contextLines hash [ oldPath, newPath ] |> Flow.map List.head

    let private loadCommit (repo: Repository) (hash: string) =
        repo.Lookup<LibGit2Sharp.Commit>(hash) |> requireNotNull (GitError.CommitNotFound hash)

    let private loadCommitterSignature (repo: Repository) : Flow<GitEnv, GitError, Signature> =
        flow {
            let! now = Clock.now
            return!
                repo.Config.BuildSignature(now)
                |> requireNotNull GitError.CommitterIdentityMissing
        }

    let private tryCommit (repo: Repository) (revision: string) =
        try
            match repo.Lookup(revision) with
            | :? LibGit2Sharp.Commit as commit -> Some commit
            | :? TagAnnotation as tag -> match tag.Target with :? LibGit2Sharp.Commit as commit -> Some commit | _ -> None
            | _ -> None
        with _ ->
            None

    /// Commits hidden by ^X, A..B and A...B.
    let private resolveExclusions (repo: Repository) (targets: StartupTarget list) =
        targets
        |> Result.traverse (fun target ->
            match target with
            | StartupTarget.Exclude revision ->
                match tryCommit repo revision with
                | Some commit -> Ok [ box commit ]
                | None -> Error(GitError.RevisionNotFound revision)
            | StartupTarget.ExcludeMergeBase(left, right) ->
                match tryCommit repo left, tryCommit repo right with
                | Some a, Some b -> Ok(match repo.ObjectDatabase.FindMergeBase(a, b) with null -> [] | mergeBase -> [ box mergeBase ])
                | None, _ -> Error(GitError.RevisionNotFound left)
                | _, None -> Error(GitError.RevisionNotFound right)
            | _ -> Ok [])
        |> Result.map List.concat

    let private resolveStartupTargets (includeStashes: bool) (repo: Repository) (targets: StartupTarget list) =
        let hasAll = targets |> List.exists ((=) StartupTarget.All)
        let tips = targets |> List.filter (function StartupTarget.Exclude _ | StartupTarget.ExcludeMergeBase _ | StartupTarget.Path _ -> false | _ -> true)

        if hasAll || List.isEmpty tips then
            Ok (box (History.defaultRoots includeStashes repo))
        else
            let resolveTarget target =
                match target with
                | StartupTarget.All
                | StartupTarget.Exclude _
                | StartupTarget.ExcludeMergeBase _
                | StartupTarget.Path _ ->
                    Ok []
                | StartupTarget.Branch name ->
                    repo.Branches.[name] |> requireNotNull (GitError.BranchNotFound name)
                    |> Result.map (fun branch -> [ box branch.Reference ])
                | StartupTarget.Sha hash ->
                    repo.Lookup<LibGit2Sharp.Commit>(hash) |> requireNotNull (GitError.CommitNotFound hash)
                    |> Result.map (fun (commit: LibGit2Sharp.Commit) -> [ box commit ])
                | StartupTarget.Tag name ->
                    repo.Tags.[name] |> requireNotNull (GitError.TagNotFound name)
                    |> Result.map (fun tag -> [ box tag ])
                | StartupTarget.Revision name ->
                    match repo.Branches.[name] |> Option.ofObj with
                    | Some branch -> Ok [ box branch.Reference ]
                    | None ->
                        // Lookup throws for too-short or ambiguous ids; tryCommit turns that into "not found".
                        match tryCommit repo name with
                        | Some commit -> Ok [ box commit ]
                        | None -> Error(GitError.RevisionNotFound name)

            tips
            |> Result.traverse resolveTarget
            |> Result.map (List.concat >> box)

    /// Resolves any revision git understands (full or short hash, branch, tag, HEAD~n) to a full commit hash.
    let resolveCommit (revision: string) : Flow<GitEnv, GitError, string> =
        flow {
            let! env = Flow.env
            use repo = new Repository(env.RepoPath)
            let resolved =
                try
                    match repo.Lookup(revision) with
                    | :? LibGit2Sharp.Commit as commit -> Some commit.Sha
                    | :? TagAnnotation as tag ->
                        match tag.Target with
                        | :? LibGit2Sharp.Commit as commit -> Some commit.Sha
                        | _ -> None
                    | _ -> None
                with _ ->
                    None

            match resolved with
            | Some hash -> return hash
            | None -> return! Error(GitError.RevisionNotFound revision)
        }

    let fetchHistory (limit: int option) (includeStashes: bool) (targets: StartupTarget list) : Flow<GitEnv, GitError, Models.Commit list> =
        flow {
            let! env = Flow.env
            use repo = new Repository(env.RepoPath)
            let! roots = resolveStartupTargets includeStashes repo targets
            let! exclusions = resolveExclusions repo targets
            let paths = targets |> List.choose (function StartupTarget.Path path -> Some path | _ -> None)
            
            let refsByCommit = History.commitRefs includeStashes repo
            let filter = CommitFilter()
            filter.IncludeReachableFrom <- roots
            if not exclusions.IsEmpty then filter.ExcludeReachableFrom <- box exclusions
            filter.SortBy <- CommitSortStrategies.Topological ||| CommitSortStrategies.Time

            // -- <paths>: keep commits whose change against their first parent touches a path.
            let touchesPaths (commit: LibGit2Sharp.Commit) =
                paths.IsEmpty
                || (let parentTree = commit.Parents |> Seq.tryHead |> Option.map _.Tree |> Option.toObj
                    use changes = repo.Diff.Compare<TreeChanges>(parentTree, commit.Tree, paths, ExplicitPathsOptions(ShouldFailOnUnmatchedPath = false), CompareOptions(Similarity = SimilarityOptions.None))
                    changes.Count > 0)

            let query = repo.Commits.QueryBy(filter)
            let distinctQuery = query |> Seq.distinctBy (fun commit -> commit.Sha) |> Seq.filter touchesPaths
            let limitedQuery =
                match limit with
                | Some n -> distinctQuery |> Seq.truncate n
                | None -> distinctQuery

            return
                limitedQuery
                |> Seq.map (fun commit ->
                    let refs =
                        match refsByCommit.TryFind commit.Sha with
                        | Some names -> names
                        | None -> []

                    toCommitModel commit refs)
                |> Seq.toList
        }

    let fetchDiff (contextLines: int) (hash: string) : Flow<GitEnv, GitError, FileDiff list> =
        flow {
            let! entry, _ = getDiffCacheEntry hash
            // One repository handle for every file; not cached, since search loads many commits' diffs.
            return! loadDiffFileContents contextLines hash (entry.FileList |> List.map (fun file -> file.OldPath, file.NewPath))
        }

    /// Line totals need a full patch pass, so they are computed on request rather than with the file list.
    let fetchDiffSummary (hash: string) : Flow<GitEnv, GitError, DiffSummary> =
        flow {
            let! entry, _ = getDiffCacheEntry hash
            let! env = Flow.env
            use repo = new Repository(env.RepoPath)
            let! (commit: LibGit2Sharp.Commit) =
                repo.Lookup<LibGit2Sharp.Commit>(hash) |> requireNotNull (GitError.CommitNotFound hash)
            let parentTree = commit.Parents |> Seq.tryHead |> Option.map _.Tree |> Option.toObj
            use stats = repo.Diff.Compare<PatchStats>(parentTree, commit.Tree, CompareOptions(Similarity = SimilarityOptions.None))
            return { entry.Summary with AddedLines = stats.TotalLinesAdded; RemovedLines = stats.TotalLinesDeleted }
        }

    let fetchDiffFileList (hash: string) : Flow<GitEnv, GitError, DiffFileSummary list> =
        flow {
            let! entry, _ = getDiffCacheEntry hash
            return entry.FileList
        }

    let fetchDiffFileContent (contextLines: int) (hash: string) (oldPath: string) (newPath: string) : Flow<GitEnv, GitError, FileDiff> =
        flow {
            let! env = Flow.env
            let key =
                {
                    Hash = hash
                    OldPath = oldPath
                    NewPath = newPath
                    ContextLines = normalizeContextLines contextLines
                }

            match env.Cache.FileContent.TryGetValue key with
            | true, file -> return file
            | false, _ ->
                let! file = loadDiffFileContent contextLines hash oldPath newPath
                env.Cache.FileContent.TryAdd(key, file) |> ignore
                return file
        }

    /// Context large enough to make a file diff include every unchanged line. LibGit2Sharp marshals
    /// context lines as a 16-bit value, so larger values wrap; gaps beyond this stay collapsed.
    let private fullContextLines = int UInt16.MaxValue

    /// Loads one file's diff with complete surrounding context, for file-scoped gap expansion.
    let fetchDiffFileFullContext (hash: string) (oldPath: string) (newPath: string) : Flow<GitEnv, GitError, FileDiff> =
        fetchDiffFileContent fullContextLines hash oldPath newPath

    let searchCommits (contextLines: int) (commits: Models.Commit list) (mode: GitSearch.Mode) (useRegex: bool) (query: string) : Flow<GitEnv, GitError, GitSearch.Result list> =
        flow {
            let! now = Clock.now
            return! GitSearch.searchCommitsWithDiffLoader now commits mode useRegex query (fun hash -> fetchDiff contextLines hash)
        }

    let fetchFileBlame (revision: string) (path: string) : Flow<GitEnv, GitError, Map<int, BlameInfo>> =
        flow {
            if path = "/dev/null" then
                return Map.empty
            else
                let! output =
                    executeGitCommand [ "blame"; "--line-porcelain"; revision; "--"; path ]
                return parseBlamePorcelain output
        }

    let createTag (hash: string) (name: string) : Flow<GitEnv, GitError, unit> =
        flow {
            let! env = Flow.env
            use repo = new Repository(env.RepoPath)
            let! commit = loadCommit repo hash
            repo.ApplyTag(name, commit.Sha) |> ignore
        }

    let createBranch (hash: string) (name: string) : Flow<GitEnv, GitError, unit> =
        flow {
            let! env = Flow.env
            use repo = new Repository(env.RepoPath)
            let! commit = loadCommit repo hash
            repo.CreateBranch(name, commit) |> ignore
        }

    let cherryPick (hash: string) : Flow<GitEnv, GitError, unit> =
        flow {
            let! env = Flow.env
            use repo = new Repository(env.RepoPath)
            let! commit = loadCommit repo hash
            let! committer = loadCommitterSignature repo
            let result = repo.CherryPick(commit, committer)

            match result.Status with
            | CherryPickStatus.CherryPicked -> return ()
            | status -> return! Error (GitError.OperationFailed("Cherry-pick", string status))
        }

    let resetTo (hash: string) (hard: bool) : Flow<GitEnv, GitError, unit> =
        flow {
            let! env = Flow.env
            use repo = new Repository(env.RepoPath)
            let! commit = loadCommit repo hash
            let mode = if hard then ResetMode.Hard else ResetMode.Soft
            repo.Reset(mode, commit)
        }

    let revert (hash: string) : Flow<GitEnv, GitError, unit> =
        flow {
            let! env = Flow.env
            use repo = new Repository(env.RepoPath)
            let! commit = loadCommit repo hash
            let! committer = loadCommitterSignature repo
            let result = repo.Revert(commit, committer)

            match result.Status with
            | RevertStatus.Reverted -> return ()
            | status -> return! Error (GitError.OperationFailed("Revert", string status))
        }
