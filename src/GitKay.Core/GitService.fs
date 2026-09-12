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
        member internal _.Diff = ConcurrentDictionary<string, DiffCacheEntry>()
        member internal _.FileContent = ConcurrentDictionary<DiffFileContentCacheKey, FileDiff>()

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

    let private loadDiffCacheEntry (hash: string) : Flow<GitEnv, GitError, DiffCacheEntry> =
        flow {
            let! env = Flow.env
            use repo = new Repository(env.RepoPath)
            let! (commit: LibGit2Sharp.Commit) = 
                repo.Lookup<LibGit2Sharp.Commit>(hash) |> requireNotNull (GitError.CommitNotFound hash)

            let isRootCommit = commit.Parents |> Seq.isEmpty
            use patch =
                match commit.Parents |> Seq.tryHead with
                | Some parent -> repo.Diff.Compare<Patch>(parent.Tree, commit.Tree)
                | None -> repo.Diff.Compare<Patch>(null, commit.Tree)

            return buildDiffCacheEntryFromPatch hash isRootCommit patch
        }

    let private getDiffCacheEntry (hash: string) : Flow<GitEnv, GitError, DiffCacheEntry * bool> =
        flow {
            let! cache = Flow.envWith _.Cache
            match cache.Diff.TryGetValue hash with
            | true, entry -> return (entry, true)
            | false, _ ->
                let! entry = loadDiffCacheEntry hash
                cache.Diff.TryAdd(hash, entry) |> ignore
                return (entry, false)
        }

    let private loadDiffFileContent (contextLines: int) (hash: string) (oldPath: string) (newPath: string) : Flow<GitEnv, GitError, FileDiff> =
        flow {
            let! env = Flow.env
            let candidatePaths =
                if oldPath = "/dev/null" then
                    [ newPath ]
                elif newPath = "/dev/null" then
                    [ oldPath ]
                elif oldPath = newPath then
                    [ oldPath ]
                else
                    [ oldPath; newPath ]

            use repo = new Repository(env.RepoPath)
            let! (commit: LibGit2Sharp.Commit) =
                repo.Lookup<LibGit2Sharp.Commit>(hash) |> requireNotNull (GitError.CommitNotFound hash)

            let compareOptions = buildCompareOptions contextLines
            use patch =
                match commit.Parents |> Seq.tryHead with
                | Some (parent: LibGit2Sharp.Commit) -> repo.Diff.Compare<Patch>(parent.Tree, commit.Tree, candidatePaths, ExplicitPathsOptions(), compareOptions)
                | None -> repo.Diff.Compare<Patch>(null, commit.Tree, candidatePaths, ExplicitPathsOptions(), compareOptions)

            match parseDiff patch.Content with
            | [ file ] -> return file
            | [] -> return! Error (GitError.DiffFileNotFound(hash, oldPath, newPath))
            | _ -> return! Error (GitError.MultipleDiffFilesMatched(hash, oldPath, newPath))
        }

    let private loadCommit (repo: Repository) (hash: string) =
        repo.Lookup<LibGit2Sharp.Commit>(hash) |> requireNotNull (GitError.CommitNotFound hash)

    let private loadCommitterSignature (repo: Repository) : Flow<GitEnv, GitError, Signature> =
        flow {
            let! now = Clock.now
            return!
                repo.Config.BuildSignature(now)
                |> requireNotNull GitError.CommitterIdentityMissing
        }

    let private resolveStartupTargets (includeStashes: bool) (repo: Repository) (targets: StartupTarget list) =
        let hasAll = targets |> List.exists ((=) StartupTarget.All)

        if hasAll || List.isEmpty targets then
            Ok (box (History.defaultRoots includeStashes repo))
        else
            let resolveTarget target =
                match target with
                | StartupTarget.All ->
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
                        result {
                            try
                                let! obj =
                                    repo.Lookup(name) |> requireNotNull (GitError.RevisionNotFound name)

                                match obj with
                                | :? LibGit2Sharp.Commit as c -> return [ box c ]
                                | :? TagAnnotation as t -> 
                                    match t.Target with
                                    | :? LibGit2Sharp.Commit as c -> return [ box c ]
                                    | _ -> return! Error (GitError.TagDoesNotPointToCommit name)
                                | _ -> return [ box obj ]
                            with _ ->
                                return! Error (GitError.InvalidRevision name)
                        }

            targets
            |> Result.traverse resolveTarget
            |> Result.map (List.concat >> box)

    let fetchHistory (limit: int option) (includeStashes: bool) (targets: StartupTarget list) : Flow<GitEnv, GitError, Models.Commit list> =
        flow {
            let! env = Flow.env
            use repo = new Repository(env.RepoPath)
            let! roots = resolveStartupTargets includeStashes repo targets
            
            let refsByCommit = History.commitRefs includeStashes repo
            let filter = CommitFilter()
            filter.IncludeReachableFrom <- roots
            filter.SortBy <- CommitSortStrategies.Topological ||| CommitSortStrategies.Time

            let query = repo.Commits.QueryBy(filter)
            let distinctQuery = query |> Seq.distinctBy (fun commit -> commit.Sha)
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
            return!
                entry.FileList
                |> Flow.traverse (fun file -> loadDiffFileContent contextLines hash file.OldPath file.NewPath)
        }

    let fetchDiffSummary (hash: string) : Flow<GitEnv, GitError, DiffSummary> =
        flow {
            let! entry, _ = getDiffCacheEntry hash
            return entry.Summary
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

    let searchCommits (contextLines: int) (commits: Models.Commit list) (query: string) (scope: GitSearch.Scope) : Flow<GitEnv, GitError, GitSearch.Result list> =
        GitSearch.searchCommitsWithDiffLoader commits query scope (fun hash -> fetchDiff contextLines hash)

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
