namespace GitKay.Core

open System
open System.Collections.Concurrent
open System.IO
open System.Collections.Generic
open System.Threading.Tasks
open System.Text.RegularExpressions
open System.Net.Http
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

    type WholeFilePayload =
        { File: FileDiff
          Rendered: RenderedMarkdownContent option
          ImageBytes: byte array option }

    type DiffSummary =
        {
            Hash: string
            FileCount: int
            AddedLines: int
            RemovedLines: int
        }

    /// A comparison between arbitrary revisions. BaseHash is their merge base, so branch comparisons
    /// contain only changes introduced on the target side (git's three-dot semantics).
    type RevisionComparison =
        { BaseRevision: string
          TargetRevision: string
          BaseHash: string
          TargetHash: string }

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

    /// Repository comparison base used on first open: main, then master, including their origin counterparts.
    let defaultComparisonBase (repoPath: string) =
        try
            use repo = new Repository(repoPath)
            [ "main"; "master"; "origin/main"; "origin/master" ]
            |> List.tryFind (fun name -> not (isNull repo.Branches.[name]))
            |> Option.toObj
        with _ -> null

    /// Local and remote branch names for the comparison picker, independent of which refs the history currently shows.
    let comparisonRevisions (repoPath: string) =
        try
            use repo = new Repository(repoPath)
            repo.Branches
            |> Seq.map (fun branch -> branch.FriendlyName)
            |> Seq.filter (fun name -> not (name.EndsWith("/HEAD", StringComparison.Ordinal)))
            |> Seq.distinct
            |> Seq.sort
            |> Seq.toArray
        with _ -> Array.empty

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
                FileChange.missing
            else
                entry.OldPath

        let newPath =
            if entry.Status = ChangeKind.Deleted || String.IsNullOrWhiteSpace entry.Path then
                FileChange.missing
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
        if isNull commit || FileChange.isMissing path then
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

    // ----- Uncommitted changes, read through the git command line (see dev-docs/uncommitted-changes-plan.md) -----

    /// <summary>Uncommitted changes: each section's file diffs.</summary>
    type WorkingTreeChanges =
        { Entries: WorkingTree.Entry list
          Staged: FileDiff list
          Unstaged: FileDiff list
          Untracked: FileDiff list }

    /// <summary>The sections with changes, in display order, each with its file diffs.</summary>
    let workingTreeSections (changes: WorkingTreeChanges) : (WorkingTree.Section * FileDiff list) list =
        [ WorkingTree.Staged, changes.Staged; WorkingTree.Unstaged, changes.Unstaged; WorkingTree.Untracked, changes.Untracked ]
        |> List.filter (fun (_, files) -> not files.IsEmpty)

    // Paths are printed as-is (not octal-escaped) and diffs ignore external diff drivers and colour configuration.
    // --no-optional-locks: status mustn't rewrite the index, or the working tree watcher would see its own refresh.
    // Runs in the working tree: status and diff fail inside the git directory, which is what RepoPath names.
    let private workTreeGit (input: string option) (arguments: string list) =
        flow {
            let! repoPath = Flow.envWith _.RepoPath
            let directory = match workingDirectory repoPath with null -> repoPath | directory -> directory
            let arguments = [ "--no-optional-locks"; "-c"; "core.quotePath=false"; "-c"; "color.ui=false"; "-c"; "diff.noprefix=false"; "-c"; "diff.mnemonicPrefix=false" ] @ arguments
            let spec = Process.commandArgs "git" arguments |> Process.workingDirectory directory
            let spec = match input with Some text -> spec |> Process.stdin (DSL.Input.text text) | None -> spec
            return!
                spec
                |> Process.capture
                |> Flow.map _.StdOut
                |> Flow.mapError (fun error -> GitError.GitProcessFailed(arguments, error))
        }

    let private plainGit (arguments: string list) = workTreeGit None arguments

    let private firstOutputLine (text: string) =
        text.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries) |> Array.tryHead

    /// Resolves two arbitrary revisions and their merge base. This is the shared primitive for branch and commit comparisons.
    let resolveRevisionComparison (baseRevision: string) (targetRevision: string) : Flow<GitEnv, GitError, RevisionComparison> =
        flow {
            let! baseHashText = plainGit [ "rev-parse"; "--verify"; baseRevision + "^{commit}" ]
            let! targetHashText = plainGit [ "rev-parse"; "--verify"; targetRevision + "^{commit}" ]
            let! mergeBaseText = plainGit [ "merge-base"; baseRevision; targetRevision ]
            match firstOutputLine baseHashText, firstOutputLine targetHashText, firstOutputLine mergeBaseText with
            | Some _, Some targetHash, Some baseHash ->
                return { BaseRevision = baseRevision; TargetRevision = targetRevision; BaseHash = baseHash; TargetHash = targetHash }
            | _ -> return! Flow.fail (GitError.CommitNotFound $"{baseRevision}...{targetRevision}")
        }

    /// Names suitable for a comparison picker. HEAD aliases are omitted and local names win over remote duplicates.
    let fetchComparisonRevisions : Flow<GitEnv, GitError, string list> =
        plainGit [ "for-each-ref"; "--format=%(refname:short)"; "refs/heads"; "refs/remotes" ]
        |> Flow.map (fun text ->
            text.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
            |> Seq.filter (fun name -> not (name.EndsWith("/HEAD", StringComparison.Ordinal)))
            |> Seq.distinct
            |> Seq.sort
            |> Seq.toList)

    /// Loads a three-dot comparison between arbitrary commits or branches.
    let fetchRevisionDiff (ignoreWhitespace: bool) (contextLines: int) (comparison: RevisionComparison) : Flow<GitEnv, GitError, FileDiff list> =
        let whitespace = if ignoreWhitespace then [ "-w" ] else []
        plainGit ([ "diff"; "--no-ext-diff"; "--find-renames" ] @ whitespace @ [ $"-U{normalizeContextLines contextLines}"; comparison.BaseHash; comparison.TargetHash ])
        |> Flow.map WorkingTree.parsePatch

    /// Complete context for one file in an arbitrary revision comparison.
    let fetchRevisionDiffFileFullContext (comparison: RevisionComparison) (oldPath: string) (newPath: string) : Flow<GitEnv, GitError, FileDiff> =
        let paths = FileChange.paths oldPath newPath
        plainGit ([ "diff"; "--no-ext-diff"; $"-U{int UInt16.MaxValue}"; comparison.BaseHash; comparison.TargetHash; "--" ] @ paths)
        |> Flow.map WorkingTree.parsePatch
        |> Flow.map (fun files -> files |> List.tryHead |> Option.defaultValue { OldPath = oldPath; NewPath = newPath; Hunks = []; NewLineCount = None })

    /// <summary>
    /// The commit's diff with whitespace-only changes left out, which libgit2 can't do: git itself produces the patch
    /// and the same parser the working tree uses reads it back.
    /// </summary>
    let fetchDiffIgnoringWhitespace (contextLines: int) (hash: string) : Flow<GitEnv, GitError, FileDiff list> =
        plainGit [ "show"; "-w"; "--no-ext-diff"; "--find-renames"; "--first-parent"; "--format="; $"-U{normalizeContextLines contextLines}"; hash ]
        |> Flow.map WorkingTree.parsePatch

    /// <summary>The commit's diff, with or without the whitespace-only changes.</summary>
    let fetchDiffWith (ignoreWhitespace: bool) (contextLines: int) (hash: string) : Flow<GitEnv, GitError, FileDiff list> =
        if ignoreWhitespace then fetchDiffIgnoringWhitespace contextLines hash else fetchDiff contextLines hash

    /// <summary>What <c>git status</c> reports for the working tree and index.</summary>
    let fetchWorkingTreeStatus : Flow<GitEnv, GitError, WorkingTree.Entry list> =
        plainGit [ "status"; "--porcelain=v2"; "-z"; "--untracked-files=all" ] |> Flow.map WorkingTree.parseStatus

    let private untrackedSizeLimit = 2L * 1024L * 1024L

    let private readUntracked (repoPath: string) (path: string) : FileDiff =
        let full = IO.Path.Combine((match workingDirectory repoPath with null -> repoPath | directory -> directory), path)
        try
            let info = IO.FileInfo full
            if not info.Exists || info.Length > untrackedSizeLimit then
                { OldPath = FileChange.missing; NewPath = path; Hunks = []; NewLineCount = None }
            else
                WorkingTree.untrackedDiff path (IO.File.ReadAllText full) // axial-allow-effect: filesystem
        with :? IO.IOException | :? UnauthorizedAccessException ->
            { OldPath = FileChange.missing; NewPath = path; Hunks = []; NewLineCount = None }

    /// <summary>The staged, unstaged and untracked diffs, with <paramref name="contextLines"/> lines of context.</summary>
    /// <summary>The commit HEAD would replace when amending, or None on a repository's first commit.</summary>
    let private tryAmendBase : Flow<GitEnv, GitError, string option> =
        plainGit [ "rev-parse"; "--verify"; "--quiet"; "HEAD^" ]
        |> Flow.map (fun output -> let hash = output.Trim() in if hash = "" then None else Some hash)
        |> Flow.orElseWith (fun _ -> Flow.ok None)

    /// <summary>
    /// The staged, unstaged and untracked diffs. When <paramref name="amending"/> is set the staged section is the
    /// index against the commit HEAD would replace, so it shows what the amended commit will contain, as git gui does.
    /// </summary>
    let fetchWorkingTreeChangesFor (amending: bool) (contextLines: int) : Flow<GitEnv, GitError, WorkingTreeChanges> =
        flow {
            let context = $"-U{normalizeContextLines contextLines}"
            let! entries = fetchWorkingTreeStatus
            let! amendBase = if amending then tryAmendBase else Flow.ok None
            let stagedAgainst = match amendBase with Some hash -> [ hash ] | None -> []
            let! staged = plainGit ([ "diff"; "--cached"; "--no-ext-diff"; "--find-renames"; context ] @ stagedAgainst) |> Flow.map WorkingTree.parsePatch
            let! unstaged = plainGit [ "diff"; "--no-ext-diff"; context ] |> Flow.map WorkingTree.parsePatch
            let! repoPath = Flow.envWith _.RepoPath
            let untracked =
                entries |> List.filter _.Untracked |> List.map (fun entry -> readUntracked repoPath entry.Path)
            return { Entries = entries; Staged = staged; Unstaged = unstaged; Untracked = untracked }
        }

    /// <summary>The changes as they stand, without the amend view.</summary>
    let fetchWorkingTreeChanges (contextLines: int) : Flow<GitEnv, GitError, WorkingTreeChanges> =
        fetchWorkingTreeChangesFor false contextLines

    /// <summary>
    /// One uncommitted file's change in a section with its whole content, for the whole-file view and context
    /// expansion. A file with no change there (or binary) comes back without hunks.
    /// </summary>
    let fetchWorkingTreeFile (section: WorkingTree.Section) (oldPath: string) (newPath: string) : Flow<GitEnv, GitError, FileDiff> =
        flow {
            let paths =
                if FileChange.isMissing oldPath || oldPath = newPath then [ newPath ]
                elif FileChange.isMissing newPath then [ oldPath ]
                else [ oldPath; newPath ]

            match section with
            | WorkingTree.Untracked ->
                let! repoPath = Flow.envWith _.RepoPath
                return readUntracked repoPath newPath
            | WorkingTree.Staged
            | WorkingTree.Unstaged ->
                let staged = if section = WorkingTree.Staged then [ "--cached"; "--find-renames" ] else []
                let! files =
                    plainGit ([ "diff" ] @ staged @ [ "--no-ext-diff"; $"-U{fullContextLines}"; "--" ] @ paths)
                    |> Flow.map WorkingTree.parsePatch

                match files with
                | file :: _ -> return file
                | [] -> return { OldPath = oldPath; NewPath = newPath; Hunks = []; NewLineCount = None }
        }

    /// <summary>Runs <see cref="fetchWorkingTreeFile"/> for a repository, for callers outside Elmish.</summary>
    let loadWorkingTreeFile (repoPath: string) (section: WorkingTree.Section) (oldPath: string) (newPath: string) : Result<FileDiff, GitError> =
        Flow.run (environment repoPath) (fetchWorkingTreeFile section oldPath newPath) |> Exit.toResult

    // ----- Staging and committing, for the commit window (see dev-docs/commit-window-plan.md) -----

    /// <summary>One file's raw diff in a section, with three lines of context, as patches are built from it.</summary>
    let fetchRawFileDiff (section: WorkingTree.Section) (path: string) : Flow<GitEnv, GitError, string> =
        let staged = if section = WorkingTree.Staged then [ "--cached" ] else []
        plainGit ([ "diff" ] @ staged @ [ "--no-ext-diff"; "-U3"; "--"; path ])

    /// <summary>Stages whole files, including deletions and untracked files.</summary>
    let stageFiles (paths: string list) : Flow<GitEnv, GitError, unit> =
        if paths.IsEmpty then Flow.succeed ()
        else plainGit ([ "add"; "--all"; "--" ] @ paths) |> Flow.map ignore

    /// <summary>
    /// Unstages whole files back to HEAD; before the first commit, removes them from the index. While amending they go
    /// back to the commit being replaced, which takes them out of the amended commit rather than out of HEAD.
    /// </summary>
    let unstageFilesFor (amending: bool) (paths: string list) : Flow<GitEnv, GitError, unit> =
        if paths.IsEmpty then Flow.succeed ()
        else
            flow {
                let! amendBase = if amending then tryAmendBase else Flow.ok None
                let! hasHead = plainGit [ "rev-parse"; "--verify"; "--quiet"; "HEAD" ] |> Flow.map (fun _ -> true) |> Flow.orElseWith (fun _ -> Flow.ok false)
                match amendBase, hasHead with
                | Some hash, _ -> do! plainGit ([ "restore"; "--staged"; "--source"; hash; "--" ] @ paths) |> Flow.map ignore
                | None, true -> do! plainGit ([ "restore"; "--staged"; "--" ] @ paths) |> Flow.map ignore
                | None, false -> do! plainGit ([ "rm"; "--cached"; "--quiet"; "--" ] @ paths) |> Flow.map ignore
            }

    let unstageFiles (paths: string list) : Flow<GitEnv, GitError, unit> = unstageFilesFor false paths

    /// <summary>How a built patch is applied.</summary>
    type PatchTarget =
        /// <summary>Adds the patch's changes to the index.</summary>
        | StageInIndex
        /// <summary>Takes the patch's changes back out of the index.</summary>
        | UnstageFromIndex
        /// <summary>Removes the patch's changes from the working tree files.</summary>
        | DiscardFromWorkingTree

    /// <summary>Applies a patch built by <see cref="PatchBuilder.build"/>.</summary>
    let applyPatch (target: PatchTarget) (patch: string) : Flow<GitEnv, GitError, unit> =
        let options =
            match target with
            | StageInIndex -> [ "--cached" ]
            | UnstageFromIndex -> [ "--cached"; "--reverse" ]
            | DiscardFromWorkingTree -> [ "--reverse" ]
        workTreeGit (Some patch) ([ "apply"; "--whitespace=nowarn" ] @ options @ [ "-" ]) |> Flow.map ignore

    /// <summary>
    /// Stages, unstages or discards the chosen lines of one file. An untracked file is added with
    /// <c>--intent-to-add</c> first, so that git has a file in the index to apply the patch to.
    /// </summary>
    let applyLines (target: PatchTarget) (path: string) (lines: PatchBuilder.SelectedLine list) : Flow<GitEnv, GitError, unit> =
        flow {
            let section, direction =
                match target with
                | StageInIndex -> WorkingTree.Unstaged, PatchBuilder.Forward
                // Discarding reverse-applies to the working tree, so the patch's new side has to match the file:
                // lines that stay must appear as context, which is what the reverse form builds.
                | DiscardFromWorkingTree -> WorkingTree.Unstaged, PatchBuilder.Reverse
                | UnstageFromIndex -> WorkingTree.Staged, PatchBuilder.Reverse

            if target = StageInIndex then
                let! status = fetchWorkingTreeStatus
                if status |> List.exists (fun entry -> entry.Untracked && entry.Path = path) then
                    do! plainGit [ "add"; "--intent-to-add"; "--"; path ] |> Flow.map ignore

            let! raw = fetchRawFileDiff section path
            match PatchBuilder.build direction raw lines with
            | Ok patch -> do! applyPatch target patch
            | Error error -> return! Flow.fail (GitError.OperationFailed("Apply lines", PatchBuilder.describeError error))
        }

    /// <summary>The working tree's root, for reading and restoring files.</summary>
    let private workingRoot : Flow<GitEnv, GitError, string> =
        Flow.envWith (fun env -> match workingDirectory env.RepoPath with null -> env.RepoPath | directory -> directory)

    /// <summary>
    /// Copies the files a discard is about to change, so it can be undone. Discarded working tree content exists
    /// nowhere else: git has no reflog for it.
    /// </summary>
    let backupBeforeDiscard (description: string) (paths: string list) : Flow<GitEnv, GitError, Trash.Backup> =
        flow {
            // git itself says where the git directory is: RepoPath may be the working tree, and backups must not
            // land inside it, where they would show up as untracked files.
            let! gitDir = plainGit [ "rev-parse"; "--absolute-git-dir" ] |> Flow.map _.Trim()
            let! root = workingRoot
            let! now = Clock.now
            let backup = Trash.capture gitDir root description now paths
            Trash.prune gitDir 20
            return backup
        }

    /// <summary>Puts a discard's files back where they were.</summary>
    let undoDiscard (backup: Trash.Backup) : Flow<GitEnv, GitError, unit> =
        flow {
            let! root = workingRoot
            // The undo is a safety net; it mustn't become a second way to lose work.
            match Trash.changedSince root backup with
            | [] -> Trash.restore root backup
            | changed ->
                let names = String.Join(", ", changed)
                return!
                    Flow.fail (
                        GitError.OperationFailed(
                            "Undo",
                            $"{names} changed since the discard. The discarded content is still in {backup.Directory}."))
        }

    /// <summary>Discards the chosen lines of one file, after copying it so the discard can be undone.</summary>
    let discardLinesWithBackup (path: string) (lines: PatchBuilder.SelectedLine list) : Flow<GitEnv, GitError, Trash.Backup> =
        flow {
            let description = if lines.Length = 1 then $"1 line in {path}" else $"{lines.Length} lines in {path}"
            let! backup = backupBeforeDiscard description [ path ]
            do! applyLines DiscardFromWorkingTree path lines
            let! root = workingRoot
            return Trash.seal root backup
        }

    /// <summary>Throws away working tree changes to whole files; untracked files are deleted. Both are backed up first.</summary>
    let discardFilesWithBackup (tracked: string list) (untracked: string list) : Flow<GitEnv, GitError, Trash.Backup> =
        flow {
            let count = tracked.Length + untracked.Length
            let description = if count = 1 then "1 file" else $"{count} files"
            let! backup = backupBeforeDiscard description (tracked @ untracked)
            if not tracked.IsEmpty then
                do! plainGit ([ "restore"; "--worktree"; "--" ] @ tracked) |> Flow.map ignore
            if not untracked.IsEmpty then
                do! plainGit ([ "clean"; "--force"; "--quiet"; "--" ] @ untracked) |> Flow.map ignore
            let! root = workingRoot
            return Trash.seal root backup
        }

    let discardFiles (tracked: string list) (untracked: string list) : Flow<GitEnv, GitError, unit> =
        discardFilesWithBackup tracked untracked |> Flow.map ignore

    /// <summary>What HEAD is: the branch name, "detached at abc1234", or "no commits yet" on an unborn branch's name.</summary>
    let fetchCurrentBranch : Flow<GitEnv, GitError, string> =
        flow {
            let! branch = plainGit [ "symbolic-ref"; "--short"; "-q"; "HEAD" ] |> Flow.map _.Trim() |> Flow.orElseWith (fun _ -> Flow.ok "")
            if branch <> "" then
                return branch
            else
                let! head = plainGit [ "rev-parse"; "--short"; "HEAD" ] |> Flow.map _.Trim() |> Flow.orElseWith (fun _ -> Flow.ok "")
                return if head = "" then "no commits yet" else $"detached at {head}"
        }

    /// <summary>The last commit's full message, for amending; empty before the first commit.</summary>
    let fetchLastCommitMessage : Flow<GitEnv, GitError, string> =
        plainGit [ "log"; "-1"; "--format=%B" ] |> Flow.orElseWith (fun _ -> Flow.ok "")

    type CommitOptions = { Amend: bool; SignOff: bool }

    /// <summary>Commits the index with the message on stdin, running hooks; the error carries their output.</summary>
    let commit (options: CommitOptions) (message: string) : Flow<GitEnv, GitError, unit> =
        let flags =
            [ if options.Amend then
                  "--amend"
              if options.SignOff then
                  "--signoff" ]
        workTreeGit (Some message) ([ "commit"; "--file=-"; "--cleanup=strip" ] @ flags) |> Flow.map ignore

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

    /// Raw blob bytes at a revision, used by safe whole-file and repository-image previews.
    let loadFileBytes (repoPath: string) (hash: string) (path: string) : Result<byte array, GitError> =
        result {
            use repo = new Repository(repoPath)
            let! (commit: LibGit2Sharp.Commit) = loadCommit repo hash
            let blob = tryBlob commit path
            if isNull blob then
                return! Error (GitError.OperationFailed("Load file", $"File not found: {path}"))
            elif blob.Size > 10L * 1024L * 1024L then
                return! Error (GitError.OperationFailed("Load file", "File is larger than the 10 MB preview limit"))
            else
                use stream = blob.GetContentStream()
                use output = new System.IO.MemoryStream()
                stream.CopyTo output
                return output.ToArray()
        }

    let private commitImageCache = ConcurrentDictionary<struct (string * string * bool * string), byte array>()

    /// Raw repository image bytes from one side of a commit diff. The old side is resolved against
    /// the first parent; the new side against the commit itself.
    let loadCommitSideFileBytes (repoPath: string) (hash: string) (oldSide: bool) (path: string) : Result<byte array, GitError> =
        let key = struct (Path.GetFullPath repoPath, hash, oldSide, path)
        match commitImageCache.TryGetValue key with
        | true, bytes -> Ok bytes
        | _ -> result {
            use repo = new Repository(repoPath)
            let! (commit: LibGit2Sharp.Commit) = loadCommit repo hash
            let revision = if oldSide then commit.Parents |> Seq.tryHead |> Option.toObj else commit
            if isNull revision then return! Error (GitError.OperationFailed("Load image", $"Revision has no parent: {hash}"))
            let blob = tryBlob revision path
            if isNull blob then return! Error (GitError.OperationFailed("Load image", $"File not found: {path}"))
            elif blob.Size > 10L * 1024L * 1024L then return! Error (GitError.OperationFailed("Load image", "Image is larger than the 10 MB preview limit"))
            else
                use stream = blob.GetContentStream()
                use output = new MemoryStream()
                stream.CopyTo output
                let bytes = output.ToArray()
                commitImageCache.[key] <- bytes
                return bytes
        }

    /// Raw repository image bytes from one side of an uncommitted diff. Staged compares HEAD/index;
    /// unstaged compares index/worktree; untracked has only a worktree side.
    let loadWorkingTreeSideFileBytes (repoPath: string) (section: WorkingTree.Section) (oldSide: bool) (path: string) : Result<byte array, GitError> =
        let readBlob (blob: Blob) =
            result {
                if isNull blob then return! Error (GitError.OperationFailed("Load image", $"File not found: {path}"))
                elif blob.Size > 10L * 1024L * 1024L then return! Error (GitError.OperationFailed("Load image", "Image is larger than the 10 MB preview limit"))
                else
                    use stream = blob.GetContentStream()
                    use output = new MemoryStream()
                    stream.CopyTo output
                    return output.ToArray()
            }
        try
            use repo = new Repository(repoPath)
            match section, oldSide with
            | WorkingTree.Staged, true ->
                match repo.Head.Tip with
                | null -> Error (GitError.OperationFailed("Load image", "HEAD does not exist"))
                | head -> readBlob (tryBlob head path)
            | WorkingTree.Staged, false
            | WorkingTree.Unstaged, true ->
                let entry = repo.Index.[path]
                readBlob (if isNull entry then null else repo.Lookup<Blob>(entry.Id))
            | WorkingTree.Unstaged, false
            | WorkingTree.Untracked, false ->
                let root = Path.GetFullPath(repo.Info.WorkingDirectory)
                let fullPath = Path.GetFullPath(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)))
                let relative = Path.GetRelativePath(root, fullPath)
                // This compatibility API is the explicit synchronous boundary used by the UI preview loader.
                // axial-allow-effect: filesystem
                if Path.IsPathRooted relative || relative = ".." || relative.StartsWith(".." + string Path.DirectorySeparatorChar, StringComparison.Ordinal) || not (File.Exists fullPath) then
                    Error (GitError.OperationFailed("Load image", $"File not found: {path}"))
                else
                    let info = FileInfo fullPath
                    if info.Length > 10L * 1024L * 1024L then Error (GitError.OperationFailed("Load image", "Image is larger than the 10 MB preview limit"))
                    // axial-allow-effect: filesystem
                    else Ok(File.ReadAllBytes fullPath)
            | WorkingTree.Untracked, true -> Error (GitError.OperationFailed("Load image", "Untracked files have no old revision"))
        with error -> Error (GitError.OperationFailed("Load image", error.Message))

    let private markdownHttp = new HttpClient(Timeout = TimeSpan.FromSeconds 10.0)

    let private loadRemoteImage (url: string) =
        try
            use response = markdownHttp.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult()
            response.EnsureSuccessStatusCode() |> ignore
            let length = response.Content.Headers.ContentLength
            if length.HasValue && length.Value > 10L * 1024L * 1024L then Error "Remote image is larger than the 10 MB preview limit"
            else
                use input = response.Content.ReadAsStream()
                use output = new MemoryStream()
                let buffer = Array.zeroCreate<byte> 81920
                let mutable total, read = 0, 0
                while total <= 10 * 1024 * 1024 && (read <- input.Read(buffer, 0, buffer.Length); read > 0) do
                    output.Write(buffer, 0, read)
                    total <- total + read
                if total > 10 * 1024 * 1024 then Error "Remote image is larger than the 10 MB preview limit"
                else Ok(output.ToArray())
        with error -> Error error.Message

    let loadRemoteMarkdownImage cancellationToken (url: string) =
        task {
            match Uri.TryCreate(url, UriKind.Absolute) with
            | false, _ -> return Error(GitError.OperationFailed("Load remote image", "The image target is not an HTTP or HTTPS URL"))
            | true, uri when uri.Scheme <> Uri.UriSchemeHttp && uri.Scheme <> Uri.UriSchemeHttps ->
                return Error(GitError.OperationFailed("Load remote image", "The image target is not an HTTP or HTTPS URL"))
            | _ ->
                try
                    use! response = markdownHttp.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    response.EnsureSuccessStatusCode() |> ignore
                    let length = response.Content.Headers.ContentLength
                    if length.HasValue && length.Value > 10L * 1024L * 1024L then
                        return Error(GitError.OperationFailed("Load remote image", "Remote image is larger than the 10 MB preview limit"))
                    else
                        use! input = response.Content.ReadAsStreamAsync(cancellationToken)
                        use output = new MemoryStream()
                        let buffer = Array.zeroCreate<byte> 81920
                        let mutable total, read = 0, 0
                        while total <= 10 * 1024 * 1024 && (read <- input.Read(buffer.AsSpan()); read > 0) do
                            cancellationToken.ThrowIfCancellationRequested()
                            output.Write(buffer, 0, read)
                            total <- total + read
                        if total > 10 * 1024 * 1024 then
                            return Error(GitError.OperationFailed("Load remote image", "Remote image is larger than the 10 MB preview limit"))
                        else return Ok(output.ToArray())
                with
                | :? OperationCanceledException as error -> return raise error
                | error -> return Error(GitError.OperationFailed("Load remote image", error.Message))
        }

    let private imageData allowRemote loader (request: MarkdownImageRequest) =
        match request.Target with
        | RepositoryPath path ->
            match loader request.Side path with
            | Ok bytes -> { Side = request.Side; Source = request.Source; Bytes = Some bytes; Error = None }
            | Error error -> { Side = request.Side; Source = request.Source; Bytes = None; Error = Some(GitError.describe error) }
        | RemoteUrl url when allowRemote ->
            match loadRemoteImage url with
            | Ok bytes -> { Side = request.Side; Source = request.Source; Bytes = Some bytes; Error = None }
            | Error error -> { Side = request.Side; Source = request.Source; Bytes = None; Error = Some error }
        | RemoteUrl _ -> { Side = request.Side; Source = request.Source; Bytes = None; Error = Some "Remote image blocked" }
        | HeadingTarget _ | InvalidTarget _ -> { Side = request.Side; Source = request.Source; Bytes = None; Error = Some "Invalid image target" }

    let loadChangedFileBytes (repoPath: string) (hash: string) (oldPath: string) (newPath: string) : Result<byte array, GitError> =
        result {
            use repo = new Repository(repoPath)
            let! (commit: LibGit2Sharp.Commit) = loadCommit repo hash
            let revision, path =
                if FileChange.isDeleted oldPath newPath then commit.Parents |> Seq.tryHead |> Option.toObj, oldPath
                else commit, newPath
            if isNull revision then return! Error (GitError.OperationFailed("Load file", $"File not found: {path}"))
            let blob = tryBlob revision path
            if isNull blob then return! Error (GitError.OperationFailed("Load file", $"File not found: {path}"))
            elif blob.Size > 10L * 1024L * 1024L then return! Error (GitError.OperationFailed("Load file", "File is larger than the 10 MB preview limit"))
            else
                use stream = blob.GetContentStream()
                use output = new System.IO.MemoryStream()
                stream.CopyTo output
                return output.ToArray()
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
                let blob = tryBlob commit (FileChange.currentPath oldPath newPath)

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

    let private revisionBlobText (repo: Repository) (revision: string) (path: string) =
        if FileChange.isMissing path then Ok ""
        else
            let commit = repo.Lookup<LibGit2Sharp.Commit>(revision)
            if isNull commit then Error(GitError.OperationFailed("Render Markdown", $"Revision not found: {revision}"))
            else
                let blob = tryBlob commit path
                if isNull blob then Ok "" else Ok(blob.GetContentText())

    let private revisionBlobBytes (repoPath: string) (revision: string) (path: string) =
        try
            use repo = new Repository(repoPath)
            let commit = repo.Lookup<LibGit2Sharp.Commit>(revision)
            if isNull commit then Error(GitError.OperationFailed("Load image", $"Revision not found: {revision}"))
            else
                let blob = tryBlob commit path
                if isNull blob then Error(GitError.OperationFailed("Load image", $"File not found: {path}"))
                elif blob.Size > 10L * 1024L * 1024L then Error(GitError.OperationFailed("Load image", "Image is larger than the 10 MB preview limit"))
                else
                    use input = blob.GetContentStream()
                    use output = new MemoryStream()
                    input.CopyTo output
                    Ok(output.ToArray())
        with error -> Error(GitError.OperationFailed("Load image", error.Message))

    let loadRevisionRenderedMarkdown (repoPath: string) (comparison: RevisionComparison) (oldPath: string) (newPath: string) (allowRemote: bool) =
        result {
            use repo = new Repository(repoPath)
            let! oldSource = revisionBlobText repo comparison.BaseHash oldPath
            let! newSource = revisionBlobText repo comparison.TargetHash newPath
            if Text.Encoding.UTF8.GetByteCount(oldSource) > 1024 * 1024 || Text.Encoding.UTF8.GetByteCount(newSource) > 1024 * 1024 then
                return! Error(GitError.OperationFailed("Render Markdown", "Files over 1 MB stay in source view"))
            let oldBlocks, newBlocks = Markdown.parse oldSource, Markdown.parse newSource
            if oldBlocks.Length > 5000 || newBlocks.Length > 5000 then
                return! Error(GitError.OperationFailed("Render Markdown", "Files over 5,000 blocks stay in source view"))
            let oldDocumentPath = FileChange.previousPath oldPath newPath
            let newDocumentPath = FileChange.currentPath oldPath newPath
            let loader side path = revisionBlobBytes repoPath (if side = MarkdownImageSide.Old then comparison.BaseHash else comparison.TargetHash) path
            let images = Markdown.imageRequests oldDocumentPath newDocumentPath oldSource newSource |> List.map (imageData allowRemote loader)
            let file = SourceDiff.between oldPath newPath oldSource newSource
            let diffRows = Markdown.renderDiff oldSource newSource |> Markdown.markChangedImages images
            return { File = file; DiffRows = diffRows; OldRows = Markdown.renderDocument oldSource
                     NewRows = Markdown.renderDocument newSource; Images = images }
        }

    let loadRevisionWholeFilePayload repoPath comparison oldPath newPath allowRemote =
        result {
            let previewPath = FileChange.currentPath oldPath newPath
            match Markdown.previewKind previewPath with
            | MarkdownPreview ->
                let! rendered = loadRevisionRenderedMarkdown repoPath comparison oldPath newPath allowRemote
                return { File = rendered.File; Rendered = Some rendered; ImageBytes = None }
            | ImagePreview _ ->
                use repo = new Repository(repoPath)
                let! oldSource = revisionBlobText repo comparison.BaseHash oldPath
                let! newSource = revisionBlobText repo comparison.TargetHash newPath
                let file = SourceDiff.between oldPath newPath oldSource newSource
                let revision, path =
                    if FileChange.isDeleted oldPath newPath then comparison.BaseHash, oldPath else comparison.TargetHash, newPath
                let! bytes = revisionBlobBytes repoPath revision path
                return { File = file; Rendered = None; ImageBytes = Some bytes }
            | FormattedPreview _ | SourceOnly ->
                use repo = new Repository(repoPath)
                let! oldSource = revisionBlobText repo comparison.BaseHash oldPath
                let! newSource = revisionBlobText repo comparison.TargetHash newPath
                return { File = SourceDiff.between oldPath newPath oldSource newSource; Rendered = None; ImageBytes = None }
        }

    /// <summary>
    /// A file's diff as it reads once both sides are reformatted — minified JSON diffed line by line is noise, so
    /// the preview diffs the indented text instead. Whole sides are needed, not the hunks around the changes, or
    /// neither side would parse.
    /// </summary>
    let private formattedDiff (format: PreviewFormat) oldPath newPath (file: Models.FileDiff) =
        let lines oldSide =
            file.Hunks |> List.collect _.Lines
            |> List.filter (fun line -> if oldSide then line.Type <> Added else line.Type <> Removed)
            |> List.map _.Content |> String.concat "\n"
        let reformat side =
            let source = lines side
            // An empty side is one the file did not have: added and deleted files format the side that exists.
            if String.IsNullOrWhiteSpace source then Ok ""
            else Markdown.formatForPreview format source
        match reformat true, reformat false with
        | Ok oldText, Ok newText -> Ok(SourceDiff.between oldPath newPath oldText newText)
        | Error message, _ | _, Error message -> Error(GitError.OperationFailed("Preview", message))

    let loadCommitFormattedFile (repoPath: string) (hash: string) (oldPath: string) (newPath: string) : Result<Models.FileDiff, GitError> =
        result {
            let! file = loadWholeFile repoPath hash oldPath newPath
            let previewPath = FileChange.currentPath oldPath newPath
            match Markdown.previewKind previewPath with
            | FormattedPreview format -> return! formattedDiff format oldPath newPath file
            | _ -> return! Error(GitError.OperationFailed("Preview", "This file has no formatted view"))
        }

    /// <summary>
    /// The bytes of the side of an image worth looking at: the new one, or the old one when the file was deleted.
    /// A deleted and an added image each have only one side, so there is nothing to compare them against.
    /// </summary>
    let loadCommitPreviewImage (repoPath: string) (hash: string) (oldPath: string) (newPath: string) : Result<byte array, GitError> =
        let deleted = FileChange.isDeleted oldPath newPath
        loadCommitSideFileBytes repoPath hash deleted (if deleted then oldPath else newPath)

    let loadWorkingTreePreviewImage (repoPath: string) (section: WorkingTree.Section) (oldPath: string) (newPath: string) =
        let deleted = FileChange.isDeleted oldPath newPath
        loadWorkingTreeSideFileBytes repoPath section deleted (if deleted then oldPath else newPath)

    let loadRevisionPreviewImage (repoPath: string) (comparison: RevisionComparison) (oldPath: string) (newPath: string) =
        let deleted = FileChange.isDeleted oldPath newPath
        revisionBlobBytes repoPath (if deleted then comparison.BaseHash else comparison.TargetHash) (if deleted then oldPath else newPath)

    let loadWorkingTreeFormattedFile (repoPath: string) (section: WorkingTree.Section) (oldPath: string) (newPath: string) : Result<Models.FileDiff, GitError> =
        result {
            let! file = loadWorkingTreeFile repoPath section oldPath newPath
            let previewPath = FileChange.currentPath oldPath newPath
            match Markdown.previewKind previewPath with
            | FormattedPreview format -> return! formattedDiff format oldPath newPath file
            | _ -> return! Error(GitError.OperationFailed("Preview", "This file has no formatted view"))
        }

    let loadRevisionFormattedFile (repoPath: string) (comparison: RevisionComparison) (oldPath: string) (newPath: string) : Result<Models.FileDiff, GitError> =
        result {
            use repo = new Repository(repoPath)
            let! oldSource = revisionBlobText repo comparison.BaseHash oldPath
            let! newSource = revisionBlobText repo comparison.TargetHash newPath
            let file = SourceDiff.between oldPath newPath oldSource newSource
            let previewPath = FileChange.currentPath oldPath newPath
            match Markdown.previewKind previewPath with
            | FormattedPreview format -> return! formattedDiff format oldPath newPath file
            | _ -> return! Error(GitError.OperationFailed("Preview", "This file has no formatted view"))
        }

    /// Complete rendered Markdown payload for a commit comparison. Parsing, revision selection, path resolution,
    /// image policy and Git reads happen together in Core; the UI only decodes the returned bytes.
    let loadCommitRenderedMarkdown (repoPath: string) (hash: string) (oldPath: string) (newPath: string) (allowRemote: bool) : Result<RenderedMarkdownContent, GitError> =
        result {
            let! file = loadWholeFile repoPath hash oldPath newPath
            let lines oldSide =
                file.Hunks |> List.collect _.Lines
                |> List.filter (fun line -> if oldSide then line.Type <> Added else line.Type <> Removed)
                |> List.map _.Content |> String.concat "\n"
            let oldSource, newSource = lines true, lines false
            if Text.Encoding.UTF8.GetByteCount(oldSource) > 1024 * 1024 || Text.Encoding.UTF8.GetByteCount(newSource) > 1024 * 1024 then
                return! Error(GitError.OperationFailed("Render Markdown", "Files over 1 MB stay in source view"))
            let oldBlocks, newBlocks = Markdown.parse oldSource, Markdown.parse newSource
            if oldBlocks.Length > 5000 || newBlocks.Length > 5000 then
                return! Error(GitError.OperationFailed("Render Markdown", "Files over 5,000 blocks stay in source view"))
            let oldDocumentPath = FileChange.previousPath oldPath newPath
            let newDocumentPath = FileChange.currentPath oldPath newPath
            let loader side path = loadCommitSideFileBytes repoPath hash (side = MarkdownImageSide.Old) path
            let images = Markdown.imageRequests oldDocumentPath newDocumentPath oldSource newSource |> List.map (imageData allowRemote loader)
            let diffRows = Markdown.renderDiff oldSource newSource |> Markdown.markChangedImages images
            return { File = file; DiffRows = diffRows
                     OldRows = Markdown.renderDocument oldSource; NewRows = Markdown.renderDocument newSource
                     Images = images }
        }

    let loadWorkingTreeRenderedMarkdown (repoPath: string) (section: WorkingTree.Section) (oldPath: string) (newPath: string) (allowRemote: bool) : Result<RenderedMarkdownContent, GitError> =
        result {
            let! file = loadWorkingTreeFile repoPath section oldPath newPath
            let lines oldSide =
                file.Hunks |> List.collect _.Lines
                |> List.filter (fun line -> if oldSide then line.Type <> Added else line.Type <> Removed)
                |> List.map _.Content |> String.concat "\n"
            let oldSource, newSource = lines true, lines false
            if Text.Encoding.UTF8.GetByteCount(oldSource) > 1024 * 1024 || Text.Encoding.UTF8.GetByteCount(newSource) > 1024 * 1024 then
                return! Error(GitError.OperationFailed("Render Markdown", "Files over 1 MB stay in source view"))
            let oldBlocks, newBlocks = Markdown.parse oldSource, Markdown.parse newSource
            if oldBlocks.Length > 5000 || newBlocks.Length > 5000 then
                return! Error(GitError.OperationFailed("Render Markdown", "Files over 5,000 blocks stay in source view"))
            let oldDocumentPath = FileChange.previousPath oldPath newPath
            let newDocumentPath = FileChange.currentPath oldPath newPath
            let loader side path = loadWorkingTreeSideFileBytes repoPath section (side = MarkdownImageSide.Old) path
            let images = Markdown.imageRequests oldDocumentPath newDocumentPath oldSource newSource |> List.map (imageData allowRemote loader)
            let diffRows = Markdown.renderDiff oldSource newSource |> Markdown.markChangedImages images
            return { File = file; DiffRows = diffRows
                     OldRows = Markdown.renderDocument oldSource; NewRows = Markdown.renderDocument newSource
                     Images = images }
        }

    let loadCommitWholeFilePayload repoPath hash oldPath newPath allowRemote =
        result {
            let previewPath = FileChange.currentPath oldPath newPath
            match Markdown.previewKind previewPath with
            | MarkdownPreview ->
                let! rendered = loadCommitRenderedMarkdown repoPath hash oldPath newPath allowRemote
                return { File = rendered.File; Rendered = Some rendered; ImageBytes = None }
            | ImagePreview _ ->
                let! file = loadWholeFile repoPath hash oldPath newPath
                let! bytes = loadChangedFileBytes repoPath hash oldPath newPath
                return { File = file; Rendered = None; ImageBytes = Some bytes }
            | FormattedPreview _ | SourceOnly ->
                let! file = loadWholeFile repoPath hash oldPath newPath
                return { File = file; Rendered = None; ImageBytes = None }
        }

    let loadWorkingTreeWholeFilePayload repoPath section oldPath newPath allowRemote =
        result {
            let previewPath = FileChange.currentPath oldPath newPath
            match Markdown.previewKind previewPath with
            | MarkdownPreview ->
                let! rendered = loadWorkingTreeRenderedMarkdown repoPath section oldPath newPath allowRemote
                return { File = rendered.File; Rendered = Some rendered; ImageBytes = None }
            | ImagePreview _ ->
                let! file = loadWorkingTreeFile repoPath section oldPath newPath
                let path = FileChange.currentPath oldPath newPath
                let! bytes = loadWorkingTreeSideFileBytes repoPath section false path
                return { File = file; Rendered = None; ImageBytes = Some bytes }
            | FormattedPreview _ | SourceOnly ->
                let! file = loadWorkingTreeFile repoPath section oldPath newPath
                return { File = file; Rendered = None; ImageBytes = None }
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
            if FileChange.isMissing path then
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
