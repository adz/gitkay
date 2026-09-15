namespace GitKay.Core

open System
open System.Collections.Concurrent
open System.IO
open System.Collections.Generic
open System.Threading.Tasks
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
        // Single-flight in-flight diff loads, keyed by commit hash. The lock only guards the dictionary
        // decision (join the existing fiber or register a new one); the load itself runs as a forked
        // fiber, so a joiner waits without blocking a thread and can observe cancellation.
        let diffLoadsGate = obj ()
        let diffLoads = Dictionary<string, Fiber<GitError, DiffCacheEntry>>()
        member internal _.DiffLoadsGate = diffLoadsGate
        member internal _.DiffLoads = diffLoads
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

    let private toDiffFileSummary (file: FileDiff) =
        {
            OldPath = file.OldPath
            NewPath = file.NewPath
            DisplayPath = FileChange.displayPath file.OldPath file.NewPath
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
            DisplayPath = FileChange.displayPath oldPath newPath
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

    let private loadDiffCacheEntryFlow (repoPath: string) (hash: string) : Flow<GitEnv, GitError, DiffCacheEntry> =
        // LibGit2Sharp's Diff.Compare has no cancellation hook: an interrupted joiner stops waiting, but
        // the load itself still runs to completion in the background. axial-allow-discarded-cancellation
        Flow.fromTaskResult (fun _ -> Task.Run(fun () -> loadDiffCacheEntry repoPath hash))

    /// Registers `candidate` as the in-flight load for `hash`, or hands back a load already in flight.
    /// Atomic: exactly one caller's fiber is ever registered for a given hash at a time.
    let private claimDiffLoad (cache: GitCache) (hash: string) (candidate: Fiber<GitError, DiffCacheEntry>) =
        lock cache.DiffLoadsGate (fun () ->
            match cache.DiffLoads.TryGetValue hash with
            | true, existing -> existing, false
            | false, _ ->
                cache.DiffLoads.[hash] <- candidate
                candidate, true)

    let private releaseDiffLoad (cache: GitCache) (hash: string) (fiber: Fiber<GitError, DiffCacheEntry>) =
        lock cache.DiffLoadsGate (fun () ->
            match cache.DiffLoads.TryGetValue hash with
            | true, current when obj.ReferenceEquals(current, fiber) -> cache.DiffLoads.Remove hash |> ignore
            | _ -> ())

    let private getDiffCacheEntry (hash: string) : Flow<GitEnv, GitError, DiffCacheEntry * bool> =
        flow {
            let! env = Flow.env

            match env.Cache.Diff.TryGetValue hash |> Result.fromTry with
            | Ok entry -> return entry, true
            | Error () ->
                // Single-flight per commit: selection loads the file list and diff concurrently, and both
                // would otherwise run rename detection for the same commit at once. The load itself runs
                // as a forked fiber rather than under a lock, so a joiner waits without blocking a thread.
                let! candidate = Flow.forkNamed $"load changed files {hash}" (loadDiffCacheEntryFlow env.RepoPath hash)
                let fiber, started = claimDiffLoad env.Cache hash candidate

                if not started then
                    let! _ = Flow.interrupt candidate
                    ()

                // Observed manually (mirroring Flow.join) rather than joined, so a failure still runs the
                // cleanup below before the original outcome is re-raised - joining directly would
                // short-circuit past it and leave a permanently-failed fiber cached for this hash.
                fiber.Metadata.Observed <- true
                // Deliberate: a joiner's own cancellation must not interrupt a fiber other, still-waiting
                // joiners depend on. This only observes the shared fiber's own outcome. axial-allow-discarded-cancellation
                let! exit = Flow.fromTask (fun _ -> fiber.ExitTask)

                if started then
                    releaseDiffLoad env.Cache hash fiber

                match exit with
                | Exit.Success entry ->
                    env.Cache.Diff.TryAdd(hash, entry) |> ignore
                    return entry, not started
                | Exit.Failure cause -> return! Flow.ofExit (Exit.Failure cause)
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

        if hasAll then
            Ok (box (History.defaultRoots includeStashes repo))
        elif List.isEmpty tips then
            // Like gitk and git log: without revisions, history is what HEAD reaches. Stashes still show when asked for;
            // an unborn HEAD (empty repository) falls back to every ref.
            match repo.Head |> Option.ofObj |> Option.bind (fun head -> Option.ofObj head.Tip) with
            | Some tip ->
                let stashes =
                    if includeStashes then repo.Stashes |> Seq.choose (fun stash -> Option.ofObj stash.WorkTree |> Option.map box) |> List.ofSeq
                    else []
                Ok (box (box tip :: stashes))
            | None -> Ok (box (History.defaultRoots includeStashes repo))
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

    /// A path argument as git matches it: relative to the working tree, with forward slashes. Arguments are relative to
    /// the directory GitKay was launched from, as with gitk; paths outside the working tree are kept as given.
    let private toRepoPath (workingDirectory: string) (launchDirectory: string) (path: string) =
        let asGiven = path.Replace('\\', '/')
        try
            let full = Path.GetFullPath(path, launchDirectory)
            let relative = Path.GetRelativePath(workingDirectory, full)
            if relative = "." then ""
            elif relative.StartsWith ".." || Path.IsPathRooted relative then asGiven
            else relative.Replace('\\', '/')
        with _ ->
            asGiven

    /// Resolves command-line path arguments against the launch directory: a bare argument that is not a revision but
    /// names a file or folder becomes a path (as in `gitk file.cs`), and every path becomes repository-relative.
    let resolvePathArguments (repoPath: string) (launchDirectory: string) (targets: StartupTarget list) =
        if String.IsNullOrEmpty repoPath || targets.IsEmpty then
            targets
        else
            try
                use repo = new Repository(repoPath)
                match repo.Info.WorkingDirectory with
                | null -> targets
                | workingDirectory ->
                    let exists (name: string) =
                        try
                            let full = Path.GetFullPath(name, launchDirectory)
                            File.Exists full || Directory.Exists full // axial-allow-effect: filesystem
                        with _ ->
                            false

                    targets
                    |> List.map (fun target ->
                        match target with
                        | StartupTarget.Revision name when isNull repo.Branches.[name] && (tryCommit repo name).IsNone && exists name ->
                            StartupTarget.Path(toRepoPath workingDirectory launchDirectory name)
                        // A path that exists relative to the launch directory is rebased onto the working tree;
                        // anything else (a deleted file, a pattern) is taken as repository-relative, as before.
                        | StartupTarget.Path path when exists path -> StartupTarget.Path(toRepoPath workingDirectory launchDirectory path)
                        | StartupTarget.Path path -> StartupTarget.Path(path.Replace('\\', '/'))
                        | _ -> target)
            with _ ->
                targets

    /// The commits `git log` lists for these revisions and paths. git reads commit-graph changed-path Bloom filters,
    /// so a path-limited history takes milliseconds where diffing every commit in-process takes seconds. It applies
    /// history simplification, as gitk does. Returns None when git can't run, so callers fall back to libgit2.
    let private gitPathLimitedCommits (repo: Repository) (includeStashes: bool) (targets: StartupTarget list) (exclusions: obj list) (paths: string list) : Flow<GitEnv, GitError, Set<string> option> =
        flow {
            let workingDirectory = repo.Info.WorkingDirectory

            if isNull workingDirectory then
                return None
            else
                let tips =
                    targets
                    |> List.choose (function
                        | StartupTarget.Revision name | StartupTarget.Branch name | StartupTarget.Tag name | StartupTarget.Sha name -> Some name
                        | _ -> None)

                let hasAll = targets |> List.contains StartupTarget.All
                let revisionArguments =
                    [ if hasAll then
                          if not includeStashes then yield "--exclude=refs/stash"
                          yield "--all"
                      elif tips.IsEmpty then
                          yield "HEAD"
                      else
                          yield! tips
                      for exclusion in exclusions do
                          match exclusion with
                          | :? LibGit2Sharp.Commit as commit -> yield "^" + commit.Sha
                          | _ -> () ]

                let arguments = [ "--literal-pathspecs"; "log"; "--format=%H" ] @ revisionArguments @ [ "--" ] @ paths
                let! outcome =
                    Process.commandArgs "git" arguments
                    |> Process.workingDirectory workingDirectory
                    |> Process.capture
                    |> Flow.map (fun (result: ProcessResult) -> Some result.StdOut)
                    |> Flow.orElseWith (fun _ -> Flow.ok None)
                    |> Flow.mapError (fun (error: ProcessError) -> GitError.GitProcessFailed(arguments, error))

                return
                    outcome
                    |> Option.map (fun (output: string) ->
                        output.Split('\n', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries) |> Set.ofArray)
        }

    let fetchHistory (limit: int option) (includeStashes: bool) (targets: StartupTarget list) : Flow<GitEnv, GitError, Models.Commit list> =
        flow {
            let! env = Flow.env
            use repo = new Repository(env.RepoPath)
            let! roots = resolveStartupTargets includeStashes repo targets
            let! exclusions = resolveExclusions repo targets
            let paths = targets |> List.choose (function StartupTarget.Path path -> Some(path.Replace('\\', '/')) | _ -> None)
            let! gitMatches =
                if paths.IsEmpty then Flow.ok None
                else gitPathLimitedCommits repo includeStashes targets exclusions paths
            
            let refsByCommit = History.commitRefs includeStashes repo
            let filter = CommitFilter()
            filter.IncludeReachableFrom <- roots
            if not exclusions.IsEmpty then filter.ExcludeReachableFrom <- box exclusions
            filter.SortBy <- CommitSortStrategies.Topological ||| CommitSortStrategies.Time

            // -- <paths>: git's answer when it ran; otherwise commits whose change against their first parent touches a path.
            let touchesPaths (commit: LibGit2Sharp.Commit) =
                paths.IsEmpty
                || (match gitMatches with Some matches -> matches.Contains commit.Sha | None -> false)
                || (gitMatches.IsNone &&
                    let parentTree = commit.Parents |> Seq.tryHead |> Option.map _.Tree |> Option.toObj
                    use changes = repo.Diff.Compare<TreeChanges>(parentTree, commit.Tree, paths, ExplicitPathsOptions(ShouldFailOnUnmatchedPath = false), CompareOptions(Similarity = SimilarityOptions.None))
                    changes.Count > 0)

            let query = repo.Commits.QueryBy(filter)
            let distinctQuery = query |> Seq.distinctBy (fun commit -> commit.Sha) |> Seq.filter touchesPaths
            // A path-limited first page bounds the commits scanned, not the matches: a rarely touched path would
            // otherwise walk (and diff) the entire history before anything appears.
            let limitedQuery =
                match limit with
                | Some n when not paths.IsEmpty && gitMatches.IsNone ->
                    query |> Seq.distinctBy (fun commit -> commit.Sha) |> Seq.truncate n |> Seq.filter touchesPaths
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

    /// The working tree root of a repository, or null for a bare repository.
    let workingDirectory (repoPath: string) : string =
        try
            use repo = new Repository(repoPath)
            match repo.Info.WorkingDirectory with
            | null -> null
            | directory -> Path.TrimEndingDirectorySeparator directory
        with _ ->
            null

    /// Every file path in a commit's tree, for the "All files" list. Submodules and other non-blob entries are skipped.
    let listCommitFiles (repoPath: string) (hash: string) : Result<string list, GitError> =
        result {
            use repo = new Repository(repoPath)
            let! (commit: LibGit2Sharp.Commit) = loadCommit repo hash

            let rec walk (tree: Tree) =
                seq {
                    for entry in tree do
                        match entry.TargetType with
                        | TreeEntryTargetType.Tree -> yield! walk (entry.Target :?> Tree)
                        | TreeEntryTargetType.Blob -> yield entry.Path.Replace('\\', '/')
                        | _ -> ()
                }

            return walk commit.Tree |> Seq.toList
        }

    /// One file at a commit with its whole content: the change against the first parent with full context when
    /// the file changed, otherwise every line as unchanged context.
    let loadWholeFile (repoPath: string) (hash: string) (oldPath: string) (newPath: string) : Result<FileDiff, GitError> =
        result {
            use repo = new Repository(repoPath)
            let! (commit: LibGit2Sharp.Commit) = loadCommit repo hash
            let parent = commit.Parents |> Seq.tryHead |> Option.toObj
            let compareOptions = buildCompareOptions fullContextLines
            let diff = diffFileInCommit repo compareOptions commit parent oldPath newPath

            if not diff.Hunks.IsEmpty then
                return diff
            else
                let blob = tryBlob commit (if newPath = "/dev/null" then oldPath else newPath)

                if isNull blob || blob.IsBinary then
                    return diff
                else
                    let text = blob.GetContentText()
                    let lines = text.Split('\n')
                    let lines = if text.EndsWith "\n" then Array.take (lines.Length - 1) lines else lines

                    let contextLines =
                        lines
                        |> Array.mapi (fun index line ->
                            { Type = Context
                              Content = line.TrimEnd('\r')
                              OldLineNo = Some(index + 1)
                              NewLineNo = Some(index + 1) })
                        |> Array.toList

                    let hunks =
                        if contextLines.IsEmpty then []
                        else [ { Header = $"@@ -1,{lines.Length} +1,{lines.Length} @@"; Lines = contextLines } ]

                    return { diff with Hunks = hunks; NewLineCount = Some lines.Length }
        }

    /// Blobs above this size are skipped by diff text search: generated or vendored files cost seconds and rarely
    /// hold what a search is for.
    let private searchBlobSizeLimit = 2L * 1024L * 1024L

    let private searchWorkers = Math.Clamp(Environment.ProcessorCount - 1, 1, 8) // axial-allow-effect: environment

    /// A search content reader with its own repository handle (libgit2 handles are not shared across threads).
    let private openSearchReader (repoPath: string) : GitSearch.ContentReader<GitEnv> =
        let repo = new Repository(repoPath)
        let pathCompare = CompareOptions(Similarity = SimilarityOptions.None)
        // Exact rename detection is cheap (content hashes) and keeps a moved file from reading as all lines changed.
        let lineCompare = CompareOptions(Similarity = SimilarityOptions.Exact)
        let blobCompare = CompareOptions(ContextLines = 0, InterhunkLines = 0)

        let changes (hash: string) =
            match repo.Lookup<LibGit2Sharp.Commit>(hash) with
            | null -> Error(GitError.CommitNotFound hash)
            | commit ->
                let parentTree = commit.Parents |> Seq.tryHead |> Option.map _.Tree |> Option.toObj
                Ok(commit, parentTree)

        let blobAt (tree: Tree) (path: string) =
            if isNull tree || String.IsNullOrEmpty path then
                null
            else
                match tree.[path] with
                | null -> null
                | entry ->
                    match entry.Target with
                    | :? Blob as blob -> blob
                    | _ -> null

        { ChangedPaths =
            fun hash ->
                flow {
                    let! (commit: LibGit2Sharp.Commit), parentTree = changes hash
                    use treeChanges = repo.Diff.Compare<TreeChanges>(parentTree, commit.Tree, pathCompare)
                    return treeChanges |> Seq.map (fun change -> change.OldPath, change.Path) |> List.ofSeq
                }
          MatchChangedLines =
            fun hash predicates ->
                flow {
                    let! (commit: LibGit2Sharp.Commit), parentTree = changes hash
                    let predicates = Array.ofList predicates
                    let found = Array.zeroCreate predicates.Length
                    let mutable remaining = predicates.Length
                    use treeChanges = repo.Diff.Compare<TreeChanges>(parentTree, commit.Tree, lineCompare)
                    use enumerator = (treeChanges :> seq<TreeEntryChanges>).GetEnumerator()

                    while remaining > 0 && enumerator.MoveNext() do
                        let change = enumerator.Current

                        if change.Status <> ChangeKind.Renamed || change.OldOid <> change.Oid then
                            let oldBlob = if change.Status = ChangeKind.Added then null else blobAt parentTree change.OldPath
                            let newBlob = if change.Status = ChangeKind.Deleted then null else blobAt commit.Tree change.Path
                            let tooBig (blob: Blob) = not (isNull blob) && (blob.Size > searchBlobSizeLimit || blob.IsBinary)

                            let contentText (blob: Blob) = if isNull blob then "" else blob.GetContentText()
                            let oldText = lazy (contentText oldBlob)
                            let newText = lazy (contentText newBlob)
                            // A changed line matching a term appears in the old or new file, so a file whose contents
                            // match no outstanding term cannot hit: skip its diff, which is most of the cost.
                            let mayMatch () =
                                predicates
                                |> Array.exists (fun matches ->
                                    matches oldText.Value || matches newText.Value)

                            if not (isNull oldBlob && isNull newBlob) && not (tooBig oldBlob || tooBig newBlob) && mayMatch () then
                                let patch = repo.Diff.Compare(oldBlob, newBlob, blobCompare).Patch
                                use reader = new IO.StringReader(patch)
                                let mutable line = reader.ReadLine()

                                while remaining > 0 && not (isNull line) do
                                    if line.Length > 0 && (line.[0] = '+' || line.[0] = '-')
                                       && not (line.StartsWith "+++" || line.StartsWith "---") then
                                        let content = line.Substring 1
                                        for index in 0 .. predicates.Length - 1 do
                                            if not found.[index] && predicates.[index] content then
                                                found.[index] <- true
                                                remaining <- remaining - 1
                                    line <- reader.ReadLine()

                    return List.ofArray found
                }
          Release = fun () -> repo.Dispose() }

    /// Searches commits: metadata filters every commit first, then parallel workers check the candidates' changed
    /// paths and changed lines without building diff objects or caching them.
    let searchCommitsStreaming (contextLines: int) (commits: Models.Commit list) (mode: GitSearch.Mode) (useRegex: bool) (query: string) (progress: int -> int -> unit) (found: GitSearch.Result -> unit) : Flow<GitEnv, GitError, GitSearch.Result list> =
        ignore contextLines
        flow {
            let! now = Clock.now
            let! env = Flow.env

            return!
                GitSearch.searchCommitsWith now commits mode useRegex query
                    { OpenReader = fun () -> openSearchReader env.RepoPath
                      Workers = searchWorkers
                      Progress = progress
                      Found = found }
        }

    let searchCommitsWithProgress (contextLines: int) (commits: Models.Commit list) (mode: GitSearch.Mode) (useRegex: bool) (query: string) (progress: int -> int -> unit) : Flow<GitEnv, GitError, GitSearch.Result list> =
        searchCommitsStreaming contextLines commits mode useRegex query progress ignore

    let searchCommits (contextLines: int) (commits: Models.Commit list) (mode: GitSearch.Mode) (useRegex: bool) (query: string) : Flow<GitEnv, GitError, GitSearch.Result list> =
        searchCommitsWithProgress contextLines commits mode useRegex query (fun _ _ -> ())

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
