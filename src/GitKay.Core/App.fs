namespace GitKay.Core

open Elmish
open System
open System.Diagnostics
open Axial
open Axial.Elmish
open GitKay.Core.Models

module App =

    /// Shared cancellation lifetime for every Axial-backed Cmd this program issues.
    let runtime = new AxialElmishRuntime()

    /// Tears down the runtime. Call alongside disposing the ElmishHostConnection so no flow
    /// outlives the window.
    let stopRuntime () = (runtime :> IDisposable).Dispose()

    let private selectionJob = AxialLatestSlot(runtime)
    let private diffJob = AxialLatestSlot(runtime)
    let private workingTreeStatusJob = AxialLatestSlot(runtime)
    let private workingTreeChangesJob = AxialLatestSlot(runtime)
    let private searchJob = AxialLatestSlot(runtime)
    let private contextJobs = AxialLatestSlotRegistry<GitService.DiffFileKey>(runtime)
    let private renderedMarkdownJob = AxialLatestSlot(runtime)
    let private formattedFileJob = AxialLatestSlot(runtime)
    let private wholeFileJob = AxialLatestSlot(runtime)
    let private remoteMarkdownImageJob = AxialLatestSlot(runtime)

    /// Presentation-only expansion state for one file of the selected diff.
    type FileExpansion =
        {
            /// Complete-context diff for the file, loaded lazily on first expansion.
            FullContext: Models.FileDiff option
            /// Revealed new-file line ranges; independent of the configured context preference.
            Revealed: DiffExpansion.LineRange list
            /// The in-flight full-context request, if any.
            PendingRequestId: int64 option
        }

    /// A commit requested on the command line (gitkay <sha>, --select): resolved through git, then selected
    /// once history containing it has loaded.
    type StartupSelection =
        | NoStartupSelection
        | ResolvingSelection of revision: string
        | SelectionFound of hash: string
        | SelectionNotFound of revision: string

    /// What the commit list has selected: a commit, the uncommitted changes row, or nothing. Commit-only actions
    /// (parents, branch actions, copying a hash) read the hash through `SelectedCommitHash`, which is None for the
    /// working tree.
    type Selection =
        | NoSelection
        | CommitSelected of hash: string
        | WorkingTreeSelected

    type RenderedMarkdownState =
        { Key: GitService.DiffFileKey
          Section: string
          RequestId: int64
          Content: RenderedMarkdownContent option }

    /// <summary>What the diff pane shows in place of a file's source when it is previewed.</summary>
    type FilePreview =
        /// <summary>The file reformatted and re-diffed, such as indented JSON or XML.</summary>
        | FormattedText of Models.FileDiff
        /// <summary>The image itself, as its bytes. One side only: an image has nothing to diff against.</summary>
        | PreviewImage of byte array

    /// <summary>One file shown as something other than its source in the diff pane.</summary>
    type FormattedFileState =
        { Key: GitService.DiffFileKey
          Section: string
          RequestId: int64
          Content: FilePreview option }

    type WholeFileState =
        { Key: GitService.DiffFileKey
          Section: string
          RequestId: int64
          Preview: bool
          Payload: GitService.WholeFilePayload option }

    type Model =
        {
            StartupSelection: StartupSelection
            /// Command-line filters (--author, --grep, ...) ask the list to show only matching commits.
            StartupShowOnlyMatches: bool
            GitEnv: GitService.GitEnv
            Status: string
            StartupTargets: GitStartup.StartupTarget list
            ShowBranchRefs: bool
            ShowStashes: bool
            DiffContextLines: int
            /// <summary>Whether whitespace-only changes are left out of the diff, as git's -w does.</summary>
            IgnoreWhitespace: bool
            DiffLayout: DiffLayout
            SearchQuery: string
            SearchScopeKey: string
            /// Treat search text as regular expressions.
            SearchUseRegex: bool
            SearchResults: GitSearch.Result list option
            Commits: Graph.CommitGraphInfo list
            HasFullHistory: bool
            Selection: Selection
            /// An arbitrary revision comparison currently replacing the selected commit's normal diff.
            RevisionComparison: GitService.RevisionComparison option
            SelectedDiffHash: string option
            SelectedDiffFiles: GitService.DiffFileSummary list option
            SelectedDiff: Models.FileDiff list option
            SelectedDiffFileKey: GitService.DiffFileKey option
            DiffExpansions: Map<GitService.DiffFileKey, FileExpansion>
            /// The selected file's rendered payload. Bytes remain managed data until the Avalonia view decodes them.
            RenderedMarkdown: RenderedMarkdownState option
            FormattedFile: FormattedFileState option
            WholeFile: WholeFileState option
            SelectionStartedAtTicks: int64 option
            SelectedDiffStartedAtTicks: int64 option
            SearchStartedAtTicks: int64 option
            /// Commits checked and total for the running search.
            SearchProgress: (int * int) option
            /// Status of the working tree and index against HEAD; empty when clean or not yet read.
            WorkingTree: WorkingTree.Entry list
            /// Diffs of the uncommitted changes, loaded while the working tree row is selected.
            WorkingTreeChanges: GitService.WorkingTreeChanges option
            /// The in-flight working tree diff load, if any.
            WorkingTreeStartedAtTicks: int64 option
            /// The last discard's backup, while it can still be undone.
            LastDiscard: Trash.Backup option
        }

        member model.SelectedCommitHash =
            match model.Selection with
            | CommitSelected hash -> Some hash
            | NoSelection | WorkingTreeSelected -> None

        member model.IsWorkingTreeSelected = model.Selection = WorkingTreeSelected

    type Msg =
        | RereadRefs
        | SetShowBranchRefs of bool
        | SetShowStashes of bool
        /// Replaces the history's revisions and paths (all branches toggle, file filter) and reloads it.
        | SetHistoryTargets of GitStartup.StartupTarget list
        | SetDiffContextLines of int
        | SetIgnoreWhitespace of bool
        | SetDiffLayout of DiffLayout
        | HistoryLoaded of isFull:bool * Result<Models.Commit list, GitError>
        | SelectCommit of hash:string * startedAtTicks:int64
        /// Rereads git status (refresh, focus, file watcher).
        | RefreshWorkingTree
        | WorkingTreeStatusLoaded of Result<WorkingTree.Entry list, GitError>
        /// Selects the uncommitted changes row and loads its diffs.
        | SelectWorkingTree of startedAtTicks:int64
        | WorkingTreeChangesLoaded of startedAtTicks:int64 * Result<GitService.WorkingTreeChanges, GitError>
        /// Stages, unstages or discards whole uncommitted files from the history window.
        | StageWorkingTree of paths:string list
        | UnstageWorkingTree of paths:string list
        | DiscardWorkingTree of tracked:string list * untracked:string list
        /// Stages, unstages or discards the chosen lines of one uncommitted file.
        | ApplyWorkingTreeLines of target:GitService.PatchTarget * path:string * lines:PatchBuilder.SelectedLine list
        | UndoWorkingTreeDiscard
        | WorkingTreeOperationDone of description:string * Result<Trash.Backup option, GitError>
        | DiffFilesLoaded of hash:string * startedAtTicks:int64 * Result<GitService.DiffFileSummary list, GitError>
        | DiffLoaded of hash:string * startedAtTicks:int64 * Result<Models.FileDiff list, GitError>
        /// Opens a three-dot comparison between arbitrary branches, tags or commits.
        | OpenRevisionComparison of baseRevision:string * targetRevision:string * startedAtTicks:int64
        | RevisionComparisonLoaded of baseRevision:string * targetRevision:string * startedAtTicks:int64 * Result<GitService.RevisionComparison * Models.FileDiff list, GitError>
        | CloseRevisionComparison
        | SelectDiffFile of hash:string * oldPath:string * newPath:string
        | SetFormattedFile of key:GitService.DiffFileKey * section:string * enabled:bool * requestId:int64
        | FormattedFileLoaded of key:GitService.DiffFileKey * section:string * requestId:int64 * Result<FilePreview, GitError>
        | SetRenderedMarkdown of key:GitService.DiffFileKey * section:string * enabled:bool * allowRemoteImages:bool * requestId:int64
        | RenderedMarkdownLoaded of key:GitService.DiffFileKey * section:string * requestId:int64 * Result<RenderedMarkdownContent, GitError>
        | OpenWholeFile of key:GitService.DiffFileKey * section:string * preview:bool * allowRemoteImages:bool * requestId:int64
        | WholeFileLoaded of key:GitService.DiffFileKey * section:string * requestId:int64 * Result<GitService.WholeFilePayload, GitError>
        | DismissWholeFile of requestId:int64
        | LoadRemoteMarkdownImage of source:string
        | RemoteMarkdownImageLoaded of source:string * Result<byte array, GitError>
        /// A startup --select revision (short hash, branch, tag, HEAD~n) resolved to a full hash.
        | SelectionRevisionResolved of revision:string * Result<string, GitError>
        | ExpandDiffGap of hash:string * gap:DiffExpansion.DiffGap * direction:DiffExpansion.ExpandDirection * requestedAtTicks:int64
        | ExpandDiffFile of hash:string * key:GitService.DiffFileKey * requestedAtTicks:int64
        | CollapseDiffFileContext of hash:string * key:GitService.DiffFileKey
        | DiffFileContextLoaded of hash:string * key:GitService.DiffFileKey * requestId:int64 * Result<Models.FileDiff, GitError>
        | SetSearchQuery of string
        | SetSearchScope of string
        | SetSearchRegex of bool
        | RunSearch of query:string * scopeKey:string * startedAtTicks:int64
        | SearchResultsLoaded of query:string * scopeKey:string * startedAtTicks:int64 * Result<GitSearch.Result list, GitError>
        /// Commits checked and total, for the search started at startedAtTicks.
        | SearchProgressed of startedAtTicks:int64 * checkedCount:int * total:int
        /// Matches found so far, in history order, while the search continues.
        | SearchPartialResults of startedAtTicks:int64 * GitSearch.Result list
        /// Stops the running search, keeping its query.
        | CancelSearch
        | CreateTag of hash:string * name:string
        | CreateBranch of hash:string * name:string
        | CherryPick of hash:string
        | ResetTo of hash:string * hard:bool
        | Revert of hash:string
        | OperationResult of Result<unit, GitError>
        | NoOp

    let private loadHistory (env: GitService.GitEnv) (limit: int option) (includeStashes: bool) (targets: GitStartup.StartupTarget list) =
        let isFull = limit.IsNone
        Cmd.OfFlow.ofFlow (if isFull then "history (full)" else $"history (first {limit.Value})") runtime env (GitService.fetchHistory limit includeStashes targets) (fun r -> HistoryLoaded(isFull, Ok r)) (fun ex -> HistoryLoaded (isFull, Error ex))

    let private historyLimit = 1000

    let private plural (count: int) (noun: string) = if count = 1 then "1 " + noun else $"{count} {noun}s"
    let private fileCount (count: int) = plural count "file"
    let private lineCount (count: int) = plural count "line"

    /// <summary>Runs a staging, unstaging or discard from the history window, then rereads the working tree.</summary>
    let private workingTreeOperation (model: Model) (description: string) (work: Flow<GitService.GitEnv, GitError, Trash.Backup option>) =
        { model with Status = description + "…" },
        Cmd.OfFlow.ofFlow description runtime model.GitEnv work
            (fun backup -> WorkingTreeOperationDone(description, Ok backup))
            (fun error -> WorkingTreeOperationDone(description, Error error))

    let private logTiming (message: string) =
        let line = "[timing] " + message
        Trace.WriteLine line

    let private loadDiffFilesFlow (hash: string) =
        flow {
            do! Flow.Runtime.ensureNotCanceled (GitError.OperationCanceled "Selection")
            return! GitService.fetchDiffFileList hash
        }

    let private loadDiffFlow (ignoreWhitespace: bool) (contextLines: int) (hash: string) =
        flow {
            do! Flow.Runtime.ensureNotCanceled (GitError.OperationCanceled "Selection")
            return! GitService.fetchDiffWith ignoreWhitespace contextLines hash
        }

    let private loadSearchResultsFlow (contextLines: int) (commits: Graph.CommitGraphInfo list) (query: string) (scopeKey: string) (useRegex: bool) progress found =
        flow {
            do! Flow.Runtime.ensureNotCanceled (GitError.OperationCanceled "Search")
            let commitList = commits |> List.map (fun info -> info.Commit)
            return! GitService.searchCommitsStreaming contextLines commitList (GitSearch.parseMode scopeKey) useRegex query progress found
        }


    let private startDiffFilesLoad (env: GitService.GitEnv) (hash: string) (startedAtTicks: int64) =
        Cmd.OfFlow.ofFlowLatest
            $"diff files {hash}"
            selectionJob
            env
            (loadDiffFilesFlow hash)
            (fun result -> DiffFilesLoaded(hash, startedAtTicks, Ok result))
            (fun err -> DiffFilesLoaded(hash, startedAtTicks, Error err))

    let private startDiffLoad (env: GitService.GitEnv) (hash: string) (startedAtTicks: int64) (contextLines: int) (ignoreWhitespace: bool) =
        Cmd.OfFlow.ofFlowLatest
            $"diff {hash}"
            diffJob
            env
            (loadDiffFlow ignoreWhitespace contextLines hash)
            (fun result -> DiffLoaded(hash, startedAtTicks, Ok result))
            (fun err -> DiffLoaded(hash, startedAtTicks, Error err))

    let private comparisonId (baseRevision: string) (targetRevision: string) = $"comparison:{baseRevision}...{targetRevision}"

    let private startRevisionComparisonLoad env baseRevision targetRevision startedAtTicks contextLines ignoreWhitespace =
        let work = flow {
            let! comparison = GitService.resolveRevisionComparison baseRevision targetRevision
            let! diff = GitService.fetchRevisionDiff ignoreWhitespace contextLines comparison
            return comparison, diff
        }
        Cmd.OfFlow.ofFlowLatest
            $"compare {baseRevision}...{targetRevision}" diffJob env work
            (fun result -> RevisionComparisonLoaded(baseRevision, targetRevision, startedAtTicks, Ok result))
            (fun error -> RevisionComparisonLoaded(baseRevision, targetRevision, startedAtTicks, Error error))

    let private startWorkingTreeStatusLoad (env: GitService.GitEnv) =
        Cmd.OfFlow.ofFlowLatest
            "working tree status"
            workingTreeStatusJob
            env
            GitService.fetchWorkingTreeStatus
            (fun entries -> WorkingTreeStatusLoaded(Ok entries))
            (fun err -> WorkingTreeStatusLoaded(Error err))

    let private startWorkingTreeChangesLoad (env: GitService.GitEnv) (contextLines: int) (startedAtTicks: int64) =
        Cmd.OfFlow.ofFlowLatest
            "working tree changes"
            workingTreeChangesJob
            env
            (GitService.fetchWorkingTreeChanges contextLines)
            (fun changes -> WorkingTreeChangesLoaded(startedAtTicks, Ok changes))
            (fun err -> WorkingTreeChangesLoaded(startedAtTicks, Error err))

    let private startSearchLoad (env: GitService.GitEnv) (contextLines: int) (commits: Graph.CommitGraphInfo list) (query: string) (scopeKey: string) (useRegex: bool) (startedAtTicks: int64) =
        // Progress dispatches are throttled so a fast search doesn't flood the message queue.
        Cmd.ofEffect (fun dispatch ->
            let gate = obj ()
            let mutable lastReport = 0L
            let found = ResizeArray<GitSearch.Result>()
            let mutable foundSinceReport = false
            let order = commits |> List.mapi (fun index info -> info.Commit.Hash, index) |> dict

            // Matches stream in with progress reports: sorted into history order, at most every 200ms.
            let report checkedCount total force =
                let now = Stopwatch.GetTimestamp()
                lock gate (fun () ->
                    if force || checkedCount = total || Stopwatch.GetElapsedTime(lastReport, now).TotalMilliseconds >= 200.0 then
                        lastReport <- now
                        if foundSinceReport then
                            foundSinceReport <- false
                            let snapshot = found |> Seq.sortBy (fun result -> order.[result.Commit.Hash]) |> List.ofSeq
                            dispatch (SearchPartialResults(startedAtTicks, snapshot))
                        dispatch (SearchProgressed(startedAtTicks, checkedCount, total)))

            let progress checkedCount total = report checkedCount total false
            let onFound (result: GitSearch.Result) =
                lock gate (fun () ->
                    found.Add result
                    foundSinceReport <- true)

            let command =
                Cmd.OfFlow.ofFlowLatest
                    $"search [{scopeKey}] {query}"
                    searchJob
                    env
                    (loadSearchResultsFlow contextLines commits query scopeKey useRegex progress onFound)
                    (fun result -> SearchResultsLoaded(query, scopeKey, startedAtTicks, Ok result))
                    (fun err -> SearchResultsLoaded(query, scopeKey, startedAtTicks, Error err))

            for effect in command do
                effect dispatch)

    let private startDiffFileContextLoad (env: GitService.GitEnv) (comparison: GitService.RevisionComparison option) (hash: string) (key: GitService.DiffFileKey) (requestId: int64) =
        let workflow =
            flow {
                do! Flow.Runtime.ensureNotCanceled (GitError.OperationCanceled "Context expansion")
                match comparison with
                | Some comparison -> return! GitService.fetchRevisionDiffFileFullContext comparison key.OldPath key.NewPath
                | None -> return! GitService.fetchDiffFileFullContext hash key.OldPath key.NewPath
            }

        Cmd.OfFlow.ofFlowLatest
            $"full context {hash} {key.NewPath}"
            contextJobs.[key]
            env
            workflow
            (fun result -> DiffFileContextLoaded(hash, key, requestId, Ok result))
            (fun err -> DiffFileContextLoaded(hash, key, requestId, Error err))

    let private diffFileKeyOfSummary (summary: GitService.DiffFileSummary) : GitService.DiffFileKey =
        {
            OldPath = summary.OldPath
            NewPath = summary.NewPath
        }

    let private tryFindDiffFileSummary (key: GitService.DiffFileKey) (files: GitService.DiffFileSummary list) =
        files
        |> List.tryFind (fun file -> file.OldPath = key.OldPath && file.NewPath = key.NewPath)

    let private historyLoadSelection (model: Model) (commits: Graph.CommitGraphInfo list) =
        let selectionExistsInHistory =
            match model.SelectedCommitHash with
            | Some hash -> commits |> List.exists (fun info -> info.Commit.Hash = hash)
            | None -> false

        let startupHash =
            match model.StartupSelection with
            | SelectionFound hash when commits |> List.exists (fun info -> info.Commit.Hash = hash) -> Some hash
            | _ -> None

        let keepsWorkingTree = startupHash.IsNone && model.IsWorkingTreeSelected

        let selectedHash =
            match startupHash, model.SelectedCommitHash, selectionExistsInHistory, commits with
            | _ when keepsWorkingTree -> None
            | Some hash, _, _, _ -> Some hash
            | None, Some hash, true, _ -> Some hash
            | _, _, _, firstCommit :: _ -> Some firstCommit.Commit.Hash
            | _ -> None

        let diffReadyForSelection =
            match selectedHash, model.SelectedDiffHash, model.SelectedDiffFiles with
            | Some hash, Some diffHash, Some _ when hash = diffHash && model.SelectedDiff.IsSome -> true
            | _ -> false

        let nextSelectionStartedAtTicks =
            if selectedHash.IsSome && not diffReadyForSelection then
                Some(Stopwatch.GetTimestamp())
            else
                None

        let nextDiffStartedAtTicks =
            if diffReadyForSelection then None
            elif selectedHash.IsSome then nextSelectionStartedAtTicks
            else None

        let nextModel =
            {
                model with
                    Status = $"Loaded {commits.Length} commits"
                    Commits = commits
                    SearchResults = None
                    SearchStartedAtTicks = None
                    SearchProgress = None
                    Selection =
                        match selectedHash with
                        | Some hash -> CommitSelected hash
                        | None when keepsWorkingTree -> WorkingTreeSelected
                        | None -> NoSelection
                    SelectedDiffHash = if diffReadyForSelection then model.SelectedDiffHash else None
                    SelectedDiffFiles = if diffReadyForSelection then model.SelectedDiffFiles else None
                    SelectedDiff = if diffReadyForSelection then model.SelectedDiff else None
                    SelectedDiffFileKey = if diffReadyForSelection then model.SelectedDiffFileKey else None
                    DiffExpansions = if diffReadyForSelection then model.DiffExpansions else Map.empty
                    SelectionStartedAtTicks = nextSelectionStartedAtTicks
                    SelectedDiffStartedAtTicks = nextDiffStartedAtTicks
            }

        let cmd =
            match selectedHash, nextSelectionStartedAtTicks with
            | Some hash, Some startedAtTicks ->
                Cmd.batch [ startDiffFilesLoad model.GitEnv hash startedAtTicks; startDiffLoad model.GitEnv hash startedAtTicks model.DiffContextLines model.IgnoreWhitespace ]
            | _ when keepsWorkingTree -> Cmd.none
            | _ ->
                selectionJob.Cancel()
                if not diffReadyForSelection then
                    diffJob.Cancel()
                    contextJobs.CancelAll()
                Cmd.none

        nextModel, cmd

    let private revealDiffRange (model: Model) (hash: string) (key: GitService.DiffFileKey) (range: DiffExpansion.LineRange option) (requestedAtTicks: int64) =
        let fileIsSelected =
            model.SelectedDiffHash = Some hash
            && model.SelectedDiffFiles |> Option.exists (tryFindDiffFileSummary key >> Option.isSome)

        match fileIsSelected, range with
        | true, Some range ->
            let current =
                model.DiffExpansions
                |> Map.tryFind key
                |> Option.defaultValue { FullContext = None; Revealed = []; PendingRequestId = None }

            let revealed = { current with Revealed = DiffExpansion.addRange range current.Revealed }

            match current.FullContext, current.PendingRequestId with
            | None, None ->
                let expansion = { revealed with PendingRequestId = Some requestedAtTicks }
                { model with DiffExpansions = model.DiffExpansions |> Map.add key expansion },
                startDiffFileContextLoad model.GitEnv model.RevisionComparison hash key requestedAtTicks
            | _ ->
                { model with DiffExpansions = model.DiffExpansions |> Map.add key revealed }, Cmd.none
        | _ ->
            model, Cmd.none

    /// Reports the outcome of a command-line selection once history is in, and finishes it on the full load.
    let private applyStartupSelectionNotice (isFull: bool) (model: Model) =
        match model.StartupSelection with
        | SelectionNotFound revision ->
            { model with Status = $"No commit found for '{revision}'"; StartupSelection = if isFull then NoStartupSelection else model.StartupSelection }
        | SelectionFound hash when model.SelectedCommitHash = Some hash ->
            { model with StartupSelection = NoStartupSelection }
        | SelectionFound hash when isFull ->
            { model with Status = $"Commit {hash.Substring(0, min 8 hash.Length)} isn't in the loaded history"; StartupSelection = NoStartupSelection }
        | _ -> model

    let init (startupArgs: string array) : Model * Cmd<Msg> =
        let repoPath = GitService.tryDiscoverRepositoryPath ()
        let gitEnv = GitService.environment repoPath

        match GitStartup.parseStartupOptions startupArgs with
        | Error err ->
            {
                StartupSelection = NoStartupSelection
                StartupShowOnlyMatches = false
                GitEnv = gitEnv
                Status = "Error: " + GitStartup.describeError err
                StartupTargets = []
                ShowBranchRefs = false
                ShowStashes = false
                DiffContextLines = 3
                IgnoreWhitespace = false
                DiffLayout = Settings.defaults.DiffLayout
                SearchQuery = ""
                SearchScopeKey = "commit"
                SearchUseRegex = false
                SearchResults = None
                Commits = []
                HasFullHistory = false
                Selection = NoSelection
                RevisionComparison = None
                SelectedDiffHash = None
                SelectedDiffFiles = None
                SelectedDiff = None
                SelectedDiffFileKey = None
                DiffExpansions = Map.empty
                RenderedMarkdown = None
                FormattedFile = None
                WholeFile = None
                SelectionStartedAtTicks = None
                SelectedDiffStartedAtTicks = None
                SearchStartedAtTicks = None
                SearchProgress = None
                WorkingTree = []
                WorkingTreeChanges = None
                WorkingTreeStartedAtTicks = None
                LastDiscard = None
            },
            Cmd.none
        | Ok startupOptions ->
            let startupOptions =
                { startupOptions with
                    StartupTargets =
                        GitService.resolvePathArguments gitEnv.RepoPath Environment.CurrentDirectory startupOptions.StartupTargets } // axial-allow-effect: environment
            let model =
                {
                    StartupSelection =
                        match startupOptions.SelectedCommitHash with
                        | Some revision -> ResolvingSelection revision
                        | None -> NoStartupSelection
                    StartupShowOnlyMatches = startupOptions.ShowOnlyMatches
                    GitEnv = gitEnv
                    Status = "Loading history..."
                    StartupTargets = startupOptions.StartupTargets
                    ShowBranchRefs = startupOptions.ShowBranchRefs
                    ShowStashes = startupOptions.ShowStashes
                    DiffContextLines = startupOptions.DiffContextLines
                    IgnoreWhitespace = false
                    DiffLayout = startupOptions.DiffLayout
                    SearchQuery = startupOptions.SearchQuery
                    SearchScopeKey = startupOptions.SearchScopeKey
                    SearchUseRegex = startupOptions.SearchUseRegex
                    Selection =
                        match startupOptions.SelectedCommitHash with
                        | Some revision -> CommitSelected revision
                        | None -> NoSelection
                    SearchResults = None
                    Commits = []
                    HasFullHistory = false
                    RevisionComparison = None
                    SelectedDiffHash = None
                    SelectedDiffFiles = None
                    SelectedDiff = None
                    SelectedDiffFileKey = None
                    DiffExpansions = Map.empty
                    RenderedMarkdown = None
                    FormattedFile = None
                    WholeFile = None
                    SelectionStartedAtTicks = None
                    SelectedDiffStartedAtTicks = None
                    SearchStartedAtTicks = None
                    SearchProgress = None
                    WorkingTree = []
                    WorkingTreeChanges = None
                    WorkingTreeStartedAtTicks = None
                    LastDiscard = None
                }

            if String.IsNullOrEmpty gitEnv.RepoPath then
                { model with Status = "Error: Could not locate a Git repository." }, Cmd.none
            else
                let resolveSelection =
                    match startupOptions.SelectedCommitHash with
                    | Some revision ->
                        Cmd.OfFlow.ofFlow $"resolve {revision}" runtime gitEnv (GitService.resolveCommit revision)
                            (fun hash -> SelectionRevisionResolved(revision, Ok hash))
                            (fun err -> SelectionRevisionResolved(revision, Error err))
                    | None -> Cmd.none

                model,
                Cmd.batch
                    [ loadHistory gitEnv (Some historyLimit) startupOptions.ShowStashes startupOptions.StartupTargets
                      startWorkingTreeStatusLoad gitEnv
                      resolveSelection ]

    let private startRenderedMarkdownLoad (model: Model) (key: GitService.DiffFileKey) section allowRemoteImages requestId =
        let load () =
            if model.IsWorkingTreeSelected then
                match WorkingTree.tryParseSection section with
                | Some parsed -> GitService.loadWorkingTreeRenderedMarkdown model.GitEnv.RepoPath parsed key.OldPath key.NewPath allowRemoteImages
                | None -> Error(GitError.OperationFailed("Render Markdown", $"Unknown working-tree section: {section}"))
            else
                match model.RevisionComparison, model.SelectedDiffHash with
                | Some comparison, _ -> GitService.loadRevisionRenderedMarkdown model.GitEnv.RepoPath comparison key.OldPath key.NewPath allowRemoteImages
                | None, Some hash -> GitService.loadCommitRenderedMarkdown model.GitEnv.RepoPath hash key.OldPath key.NewPath allowRemoteImages
                | _ -> Error(GitError.OperationFailed("Render Markdown", "No revision is selected"))
        Cmd.OfFlow.ofFlowLatest
            $"render markdown {key.NewPath}"
            renderedMarkdownJob
            model.GitEnv
            // LibGit2Sharp blob reads do not accept cancellation; latest-slot cancellation still rejects stale results.
            // axial-allow-discarded-cancellation
            (Flow.fromTaskResult (fun _ -> Threading.Tasks.Task.Run load))
            (fun content -> RenderedMarkdownLoaded(key, section, requestId, Ok content))
            (fun error -> RenderedMarkdownLoaded(key, section, requestId, Error error))

    let private startFormattedFileLoad (model: Model) (key: GitService.DiffFileKey) section requestId =
        let previewPath = FileChange.currentPath key.OldPath key.NewPath
        let isImage = (Markdown.previewKind previewPath).IsImagePreview
        let load () =
            if model.IsWorkingTreeSelected then
                match WorkingTree.tryParseSection section with
                | Some parsed ->
                    if isImage then GitService.loadWorkingTreePreviewImage model.GitEnv.RepoPath parsed key.OldPath key.NewPath |> Result.map PreviewImage
                    else GitService.loadWorkingTreeFormattedFile model.DiffContextLines model.GitEnv.RepoPath parsed key.OldPath key.NewPath |> Result.map FormattedText
                | None -> Error(GitError.OperationFailed("Preview", $"Unknown working-tree section: {section}"))
            else
                match model.RevisionComparison, model.SelectedDiffHash with
                | Some comparison, _ ->
                    if isImage then GitService.loadRevisionPreviewImage model.GitEnv.RepoPath comparison key.OldPath key.NewPath |> Result.map PreviewImage
                    else GitService.loadRevisionFormattedFile model.DiffContextLines model.GitEnv.RepoPath comparison key.OldPath key.NewPath |> Result.map FormattedText
                | None, Some hash ->
                    if isImage then GitService.loadCommitPreviewImage model.GitEnv.RepoPath hash key.OldPath key.NewPath |> Result.map PreviewImage
                    else GitService.loadCommitFormattedFile model.DiffContextLines model.GitEnv.RepoPath hash key.OldPath key.NewPath |> Result.map FormattedText
                | _ -> Error(GitError.OperationFailed("Preview", "No revision is selected"))
        Cmd.OfFlow.ofFlowLatest
            $"format file {key.NewPath}"
            formattedFileJob
            model.GitEnv
            // LibGit2Sharp blob reads do not accept cancellation; latest-slot cancellation still rejects stale results.
            // axial-allow-discarded-cancellation
            (Flow.fromTaskResult (fun _ -> Threading.Tasks.Task.Run load))
            (fun content -> FormattedFileLoaded(key, section, requestId, Ok content))
            (fun error -> FormattedFileLoaded(key, section, requestId, Error error))

    let private startWholeFileLoad (model: Model) (key: GitService.DiffFileKey) section allowRemoteImages requestId =
        let load () =
            if model.IsWorkingTreeSelected then
                if String.IsNullOrWhiteSpace section then
                    GitService.loadCommitWholeFilePayload model.GitEnv.RepoPath "HEAD" key.OldPath key.NewPath allowRemoteImages
                else
                    match WorkingTree.tryParseSection section with
                    | Some parsed -> GitService.loadWorkingTreeWholeFilePayload model.GitEnv.RepoPath parsed key.OldPath key.NewPath allowRemoteImages
                    | None -> Error(GitError.OperationFailed("Open file", $"Unknown working-tree section: {section}"))
            else
                match model.RevisionComparison, model.SelectedDiffHash with
                | Some comparison, _ ->
                    GitService.loadRevisionWholeFilePayload model.GitEnv.RepoPath comparison key.OldPath key.NewPath allowRemoteImages
                | None, Some hash ->
                    GitService.loadCommitWholeFilePayload model.GitEnv.RepoPath hash key.OldPath key.NewPath allowRemoteImages
                | _ -> Error(GitError.OperationFailed("Open file", "No commit revision is selected"))
        Cmd.OfFlow.ofFlowLatest
            $"whole file {key.NewPath}"
            wholeFileJob
            model.GitEnv
            // LibGit2Sharp reads have no cancellation overload; the latest slot rejects stale completion.
            // axial-allow-discarded-cancellation
            (Flow.fromTaskResult (fun _ -> Threading.Tasks.Task.Run load))
            (fun payload -> WholeFileLoaded(key, section, requestId, Ok payload))
            (fun error -> WholeFileLoaded(key, section, requestId, Error error))

    let private startRemoteMarkdownImageLoad (model: Model) source =
        Cmd.OfFlow.ofFlowLatest
            $"remote markdown image {source}"
            remoteMarkdownImageJob
            model.GitEnv
            (Flow.fromTaskResult (fun cancellationToken -> GitService.loadRemoteMarkdownImage cancellationToken source))
            (fun bytes -> RemoteMarkdownImageLoaded(source, Ok bytes))
            (fun error -> RemoteMarkdownImageLoaded(source, Error error))

    let update msg model : Model * Cmd<Msg> =
        match msg with
        | RereadRefs ->
            searchJob.Cancel()
            let nextModel = { model with Status = "Refreshing..."; HasFullHistory = false }
            nextModel, Cmd.batch [ loadHistory model.GitEnv (Some historyLimit) model.ShowStashes model.StartupTargets; startWorkingTreeStatusLoad model.GitEnv ]
        | RefreshWorkingTree ->
            model, startWorkingTreeStatusLoad model.GitEnv
        | StageWorkingTree [] | UnstageWorkingTree [] | DiscardWorkingTree([], []) | ApplyWorkingTreeLines(_, _, []) ->
            model, Cmd.none
        | StageWorkingTree paths ->
            workingTreeOperation model $"Staged {fileCount paths.Length}" (GitService.stageFiles paths |> Flow.map (fun () -> None))
        | UnstageWorkingTree paths ->
            workingTreeOperation model $"Unstaged {fileCount paths.Length}" (GitService.unstageFiles paths |> Flow.map (fun () -> None))
        | DiscardWorkingTree(tracked, untracked) ->
            workingTreeOperation model $"Discarded {fileCount (tracked.Length + untracked.Length)}"
                (GitService.discardFilesWithBackup tracked untracked |> Flow.map Some)
        | ApplyWorkingTreeLines(GitService.DiscardFromWorkingTree, path, lines) ->
            workingTreeOperation model $"Discarded {lineCount lines.Length} of {path}"
                (GitService.discardLinesWithBackupAt model.DiffContextLines path lines |> Flow.map Some)
        | ApplyWorkingTreeLines(target, path, lines) ->
            let verb = if target = GitService.StageInIndex then "Staged" else "Unstaged"
            workingTreeOperation model $"{verb} {lineCount lines.Length} of {path}"
                (GitService.applyLinesWithContext model.DiffContextLines target path lines |> Flow.map (fun () -> None))
        | UndoWorkingTreeDiscard ->
            match model.LastDiscard with
            | None -> { model with Status = "Nothing to undo" }, Cmd.none
            | Some backup ->
                { model with LastDiscard = None },
                Cmd.OfFlow.ofFlow "undo discard" runtime model.GitEnv (GitService.undoDiscard backup |> Flow.map (fun () -> None))
                    (fun _ -> WorkingTreeOperationDone($"Restored {backup.Description}", Ok None))
                    (fun error -> WorkingTreeOperationDone("Undo", Error error))
        | WorkingTreeOperationDone(description, Ok backup) ->
            let status =
                match backup with
                | Some (_: Trash.Backup) -> $"{description} · Ctrl+Z to undo"
                | None -> description
            { model with Status = status; LastDiscard = backup |> Option.orElse (if description.StartsWith "Restored" then None else model.LastDiscard) },
            Cmd.ofMsg RefreshWorkingTree
        | WorkingTreeOperationDone(description, Error error) ->
            { model with Status = $"{description} failed: " + GitError.describe error }, Cmd.ofMsg RefreshWorkingTree
        | WorkingTreeStatusLoaded (Ok entries) ->
            let changed = entries <> model.WorkingTree

            if model.IsWorkingTreeSelected && entries.IsEmpty then
                // Everything was committed or discarded: the row goes, so select the newest commit instead.
                let nextModel = { model with WorkingTree = []; WorkingTreeChanges = None; WorkingTreeStartedAtTicks = None; Selection = NoSelection }
                match model.Commits with
                | first :: _ -> nextModel, Cmd.ofMsg (SelectCommit(first.Commit.Hash, Stopwatch.GetTimestamp()))
                | [] -> nextModel, Cmd.none
            elif model.IsWorkingTreeSelected && changed then
                let startedAtTicks = Stopwatch.GetTimestamp()
                { model with WorkingTree = entries; WorkingTreeStartedAtTicks = Some startedAtTicks },
                startWorkingTreeChangesLoad model.GitEnv model.DiffContextLines startedAtTicks
            elif changed then
                { model with WorkingTree = entries }, Cmd.none
            else
                model, Cmd.none
        | WorkingTreeStatusLoaded (Error err) ->
            // Without git (or outside a work tree) the row simply doesn't appear.
            logTiming $"working tree status error={GitError.describe err}"
            { model with WorkingTree = [] }, Cmd.none
        | SelectWorkingTree startedAtTicks ->
            selectionJob.Cancel()
            diffJob.Cancel()
            contextJobs.CancelAll()
            renderedMarkdownJob.Cancel()
            formattedFileJob.Cancel()
            { model with
                Selection = WorkingTreeSelected
                RevisionComparison = None
                SelectedDiffHash = None
                SelectedDiffFiles = None
                SelectedDiff = None
                SelectedDiffFileKey = None
                DiffExpansions = Map.empty
                RenderedMarkdown = None
                FormattedFile = None
                SelectionStartedAtTicks = None
                SelectedDiffStartedAtTicks = None
                WorkingTreeStartedAtTicks = Some startedAtTicks },
            startWorkingTreeChangesLoad model.GitEnv model.DiffContextLines startedAtTicks
        | WorkingTreeChangesLoaded (startedAtTicks, result) when model.IsWorkingTreeSelected && model.WorkingTreeStartedAtTicks = Some startedAtTicks ->
            match result with
            | Ok changes ->
                { model with WorkingTree = changes.Entries; WorkingTreeChanges = Some changes; WorkingTreeStartedAtTicks = None }, Cmd.none
            | Error err ->
                { model with Status = "Diff Error: " + GitError.describe err; WorkingTreeChanges = None; WorkingTreeStartedAtTicks = None }, Cmd.none
        | WorkingTreeChangesLoaded _ ->
            model, Cmd.none
        | SetHistoryTargets targets ->
            searchJob.Cancel()
            let nextModel = { model with StartupTargets = targets; Status = "Loading history..."; HasFullHistory = false }
            nextModel, loadHistory model.GitEnv (Some historyLimit) model.ShowStashes targets
        | SetShowBranchRefs showBranchRefs ->
            { model with ShowBranchRefs = showBranchRefs }, Cmd.none
        | SetShowStashes showStashes ->
            searchJob.Cancel()
            let nextModel =
                {
                    model with
                        ShowStashes = showStashes
                        Status = "Refreshing..."
                        HasFullHistory = false
                }

            nextModel, loadHistory model.GitEnv (Some historyLimit) showStashes model.StartupTargets
        | SetIgnoreWhitespace ignoreWhitespace ->
            if ignoreWhitespace = model.IgnoreWhitespace then
                model, Cmd.none
            else
                // The diff itself changes, so whatever is shown is reread the way the context-line choice rereads it.
                let nextModel =
                    { model with
                        IgnoreWhitespace = ignoreWhitespace
                        SelectedDiff = None
                        SelectedDiffStartedAtTicks = model.SelectedCommitHash |> Option.map (fun _ -> Stopwatch.GetTimestamp()) }

                match model.RevisionComparison, model.Selection with
                | Some comparison, _ ->
                    let startedAtTicks = Stopwatch.GetTimestamp()
                    { nextModel with SelectedDiffStartedAtTicks = Some startedAtTicks },
                    startRevisionComparisonLoad model.GitEnv comparison.BaseRevision comparison.TargetRevision startedAtTicks model.DiffContextLines ignoreWhitespace
                | None, CommitSelected hash ->
                    let startedAtTicks = nextModel.SelectedDiffStartedAtTicks.Value
                    nextModel, startDiffLoad model.GitEnv hash startedAtTicks model.DiffContextLines ignoreWhitespace
                | None, (WorkingTreeSelected | NoSelection) -> nextModel, Cmd.none
        | SetDiffContextLines diffContextLines ->
            let normalizedContextLines = max 0 diffContextLines

            if normalizedContextLines = model.DiffContextLines then
                model, Cmd.none
            else
                let nextModel =
                    {
                        model with
                            DiffContextLines = normalizedContextLines
                            SelectedDiff = None
                            SelectedDiffStartedAtTicks = model.SelectedCommitHash |> Option.map (fun _ -> Stopwatch.GetTimestamp())
                    }

                match model.RevisionComparison, model.Selection with
                | Some comparison, _ ->
                    let startedAtTicks = Stopwatch.GetTimestamp()
                    { nextModel with SelectedDiffStartedAtTicks = Some startedAtTicks },
                    startRevisionComparisonLoad model.GitEnv comparison.BaseRevision comparison.TargetRevision startedAtTicks normalizedContextLines model.IgnoreWhitespace
                | None, CommitSelected hash ->
                    let startedAtTicks = nextModel.SelectedDiffStartedAtTicks.Value
                    nextModel, startDiffLoad model.GitEnv hash startedAtTicks normalizedContextLines model.IgnoreWhitespace
                | None, WorkingTreeSelected ->
                    let startedAtTicks = Stopwatch.GetTimestamp()
                    { nextModel with WorkingTreeStartedAtTicks = Some startedAtTicks },
                    startWorkingTreeChangesLoad model.GitEnv normalizedContextLines startedAtTicks
                | None, NoSelection ->
                    diffJob.Cancel()
                    nextModel, Cmd.none
        | SetDiffLayout layout -> { model with DiffLayout = layout }, Cmd.none
        | HistoryLoaded (isFull, Ok commits) ->
            // A path-limited first page scans a bounded number of commits, so few matches doesn't mean it's complete.
            let hasPaths = model.StartupTargets |> List.exists (function GitStartup.StartupTarget.Path _ -> true | _ -> false)
            if isFull || (not hasPaths && commits.Length < historyLimit) then
                searchJob.Cancel()
                let graphInfo = Graph.calculateLanes commits
                let nextModel, historyCmd = historyLoadSelection model graphInfo
                // Say what the history is limited to, so a branch or path launch visibly differs from the full history.
                let revisionNote =
                    match model.StartupTargets |> List.choose (function
                        | GitStartup.StartupTarget.Revision r | GitStartup.StartupTarget.Branch r
                        | GitStartup.StartupTarget.Tag r | GitStartup.StartupTarget.Sha r -> Some r
                        | GitStartup.StartupTarget.Exclude r -> Some("^" + r)
                        | _ -> None) with
                    | [] -> ""
                    | revisions -> " from " + String.Join(" ", revisions)
                let pathNote =
                    revisionNote
                    + match model.StartupTargets |> List.choose (function GitStartup.StartupTarget.Path path -> Some path | _ -> None) with
                      | [] -> ""
                      | paths -> " touching " + String.Join(", ", paths)
                let nextModel = { nextModel with HasFullHistory = true; Status = nextModel.Status + pathNote } |> applyStartupSelectionNotice true
                let searchQuery = nextModel.SearchQuery.Trim()

                if String.IsNullOrWhiteSpace searchQuery then
                    nextModel, historyCmd
                else
                    let startedAtTicks = Stopwatch.GetTimestamp()
                    let searchModel =
                        {
                            nextModel with
                                SearchStartedAtTicks = Some startedAtTicks
                                Status = $"Searching {nextModel.SearchQuery}..."
                        }

                    searchModel, Cmd.batch [ historyCmd; startSearchLoad model.GitEnv searchModel.DiffContextLines graphInfo nextModel.SearchQuery nextModel.SearchScopeKey model.SearchUseRegex startedAtTicks ]
            else
                // Partial history loaded
                let graphInfo = Graph.calculateLanes commits
                let nextModel, historyCmd = historyLoadSelection model graphInfo
                let nextModel = { nextModel with Status = $"Loaded {commits.Length} commits (loading more...)" } |> applyStartupSelectionNotice false
                
                let loadFullCmd = loadHistory model.GitEnv None model.ShowStashes model.StartupTargets
                
                let searchQuery = nextModel.SearchQuery.Trim()
                if String.IsNullOrWhiteSpace searchQuery then
                    nextModel, Cmd.batch [ historyCmd; loadFullCmd ]
                else
                    let startedAtTicks = Stopwatch.GetTimestamp()
                    let searchModel =
                        {
                            nextModel with
                                SearchStartedAtTicks = Some startedAtTicks
                                Status = $"Searching {nextModel.SearchQuery}..."
                        }

                    searchModel, Cmd.batch [ historyCmd; loadFullCmd; startSearchLoad model.GitEnv searchModel.DiffContextLines graphInfo nextModel.SearchQuery nextModel.SearchScopeKey model.SearchUseRegex startedAtTicks ]
        | HistoryLoaded (_, Error err) when model.Commits.IsEmpty
                                            && model.StartupTargets |> List.exists (function GitStartup.StartupTarget.Path _ | GitStartup.StartupTarget.All -> false | _ -> true) ->
            // Command-line revisions that don't resolve: fall back to the normal history (keeping any paths) and say so.
            let described =
                model.StartupTargets
                |> List.choose (function
                    | GitStartup.StartupTarget.Revision r | GitStartup.StartupTarget.Branch r | GitStartup.StartupTarget.Sha r
                    | GitStartup.StartupTarget.Tag r | GitStartup.StartupTarget.Exclude r -> Some r
                    | GitStartup.StartupTarget.ExcludeMergeBase(a, b) -> Some $"{a}...{b}"
                    | _ -> None)
                |> String.concat " "
            let fallbackTargets = model.StartupTargets |> List.filter (function GitStartup.StartupTarget.Path _ -> true | _ -> false)
            let nextModel =
                { model with
                    StartupTargets = fallbackTargets
                    StartupSelection = SelectionNotFound described
                    Status = $"No commit found for '{described}'" }
            nextModel, loadHistory model.GitEnv (Some historyLimit) model.ShowStashes fallbackTargets
        | HistoryLoaded (_, Error err) ->
            searchJob.Cancel()
            { model with Status = "Error: " + GitError.describe err; SearchResults = None; SearchStartedAtTicks = None }, Cmd.none
        | OpenRevisionComparison (baseRevision, targetRevision, startedAtTicks) ->
            selectionJob.Cancel()
            diffJob.Cancel()
            contextJobs.CancelAll()
            renderedMarkdownJob.Cancel()
            formattedFileJob.Cancel()
            { model with
                Status = $"Comparing {baseRevision}…{targetRevision}…"
                RevisionComparison = None
                SelectedDiffHash = None
                SelectedDiffFiles = None
                SelectedDiff = None
                SelectedDiffFileKey = None
                DiffExpansions = Map.empty
                RenderedMarkdown = None
                FormattedFile = None
                SelectionStartedAtTicks = None
                SelectedDiffStartedAtTicks = Some startedAtTicks },
            startRevisionComparisonLoad model.GitEnv baseRevision targetRevision startedAtTicks model.DiffContextLines model.IgnoreWhitespace
        | RevisionComparisonLoaded (baseRevision, targetRevision, startedAtTicks, Ok (comparison, diff))
            when model.SelectedDiffStartedAtTicks = Some startedAtTicks ->
            let id = comparisonId baseRevision targetRevision
            let files: GitService.DiffFileSummary list =
                diff |> List.map (fun file -> { OldPath = file.OldPath; NewPath = file.NewPath; DisplayPath = FileChange.displayPath file.OldPath file.NewPath })
            let selected = files |> List.tryHead |> Option.map diffFileKeyOfSummary
            { model with
                Status = $"Compared {baseRevision}…{targetRevision}: {files.Length} files"
                RevisionComparison = Some comparison
                SelectedDiffHash = Some id
                SelectedDiffFiles = Some files
                SelectedDiff = Some diff
                SelectedDiffFileKey = selected
                DiffExpansions = Map.empty
                SelectedDiffStartedAtTicks = None }, Cmd.none
        | RevisionComparisonLoaded (_, _, startedAtTicks, Error error) when model.SelectedDiffStartedAtTicks = Some startedAtTicks ->
            { model with Status = "Comparison Error: " + GitError.describe error; SelectedDiffStartedAtTicks = None }, Cmd.none
        | RevisionComparisonLoaded _ -> model, Cmd.none
        | CloseRevisionComparison ->
            match model.SelectedCommitHash with
            | Some hash -> model, Cmd.ofMsg (SelectCommit(hash, Stopwatch.GetTimestamp()))
            | None -> { model with RevisionComparison = None }, Cmd.none
        | SelectCommit (hash, startedAtTicks) ->
            selectionJob.Cancel()
            diffJob.Cancel()
            contextJobs.CancelAll()
            renderedMarkdownJob.Cancel()
            formattedFileJob.Cancel()
            let nextModel =
                {
                    model with
                        Selection = CommitSelected hash
                        RevisionComparison = None
                        SelectedDiffHash = None
                        SelectedDiffFiles = None
                        SelectedDiff = None
                        SelectedDiffFileKey = None
                        DiffExpansions = Map.empty
                        RenderedMarkdown = None
                        FormattedFile = None
                        SelectionStartedAtTicks = Some startedAtTicks
                        SelectedDiffStartedAtTicks = Some startedAtTicks
                        WorkingTreeChanges = None
                        WorkingTreeStartedAtTicks = None
                }

            workingTreeChangesJob.Cancel()
            let cmd = Cmd.batch [ startDiffFilesLoad model.GitEnv hash startedAtTicks; startDiffLoad model.GitEnv hash startedAtTicks model.DiffContextLines model.IgnoreWhitespace ]
            nextModel, cmd
        | SetSearchQuery query ->
            let trimmed = query.Trim()

            if String.IsNullOrWhiteSpace trimmed then
                searchJob.Cancel()
                { model with SearchQuery = query; SearchResults = None; SearchStartedAtTicks = None }, Cmd.none
            else
                { model with SearchQuery = query }, Cmd.none
        | SetSearchScope scopeKey ->
            { model with SearchScopeKey = scopeKey }, Cmd.none
        | SetSearchRegex useRegex ->
            { model with SearchUseRegex = useRegex }, Cmd.none
        | RunSearch (query, scopeKey, startedAtTicks) ->
            searchJob.Cancel()

            if String.IsNullOrWhiteSpace query then
                { model with SearchQuery = query; SearchScopeKey = scopeKey; SearchResults = None; SearchStartedAtTicks = None; Status = "Search cleared" }, Cmd.none
            else
                let nextModel =
                    {
                        model with
                            SearchQuery = query
                            SearchScopeKey = scopeKey
                            SearchResults = None
                            SearchStartedAtTicks = Some startedAtTicks
                            Status = $"Searching {query}..."
                    }

                let cmd = startSearchLoad model.GitEnv model.DiffContextLines model.Commits query scopeKey model.SearchUseRegex startedAtTicks
                nextModel, cmd
        | SearchProgressed (startedAtTicks, checkedCount, total) ->
            match model.SearchStartedAtTicks with
            | Some current when current = startedAtTicks && checkedCount < total ->
                { model with
                    SearchProgress = Some(checkedCount, total)
                    Status =
                        let matches = model.SearchResults |> Option.map List.length |> Option.defaultValue 0
                        $"Searching {model.SearchQuery}... {checkedCount:N0} of {total:N0} commits, {matches:N0} matches so far (Esc to cancel)" },
                Cmd.none
            | _ -> model, Cmd.none
        | SearchPartialResults (startedAtTicks, results) ->
            match model.SearchStartedAtTicks with
            | Some current when current = startedAtTicks -> { model with SearchResults = Some results }, Cmd.none
            | _ -> model, Cmd.none
        | CancelSearch ->
            match model.SearchStartedAtTicks with
            | Some _ ->
                searchJob.Cancel()
                { model with SearchStartedAtTicks = None; SearchProgress = None; Status = $"Search cancelled: {model.SearchQuery}" }, Cmd.none
            | None -> model, Cmd.none
        | SearchResultsLoaded (query, scopeKey, startedAtTicks, Ok results) ->
            match model.SearchQuery, model.SearchScopeKey, model.SearchStartedAtTicks with
            | currentQuery, currentScopeKey, Some currentStartedAtTicks when currentQuery = query && currentScopeKey = scopeKey && currentStartedAtTicks = startedAtTicks ->
                { model with SearchResults = Some results; SearchStartedAtTicks = None; SearchProgress = None; Status = $"Search: {results.Length} hit(s) for \"{query}\"" }, Cmd.none
            | _ ->
                model, Cmd.none
        | SearchResultsLoaded (query, scopeKey, startedAtTicks, Error err) ->
            match model.SearchQuery, model.SearchScopeKey, model.SearchStartedAtTicks with
            | currentQuery, currentScopeKey, Some currentStartedAtTicks when currentQuery = query && currentScopeKey = scopeKey && currentStartedAtTicks = startedAtTicks ->
                let status =
                    match err with
                    | GitError.OperationCanceled _ -> $"Search cancelled: {query}"
                    | _ -> "Search Error: " + GitError.describe err
                { model with SearchResults = None; SearchStartedAtTicks = None; SearchProgress = None; Status = status }, Cmd.none
            | _ ->
                model, Cmd.none
        | DiffFilesLoaded (hash, startedAtTicks, Ok files) ->
            match model.SelectedCommitHash, model.SelectionStartedAtTicks with
            | Some currentHash, Some currentStartedAtTicks when currentHash = hash && currentStartedAtTicks = startedAtTicks ->
                let elapsed = Stopwatch.GetElapsedTime(startedAtTicks)
                logTiming $"commit click -> file list ready hash={hash} elapsed={elapsed.TotalMilliseconds:F1}ms files={files.Length}"
            | _ ->
                ()

            if model.SelectedCommitHash = Some hash && model.SelectionStartedAtTicks = Some startedAtTicks then

                let selectedFileSummary =
                    match model.SelectedDiffFileKey with
                    | Some key ->
                        tryFindDiffFileSummary key files
                    | None ->
                        None
                    |> Option.orElse (files |> List.tryHead)

                let selectedFileKey: GitService.DiffFileKey option =
                    selectedFileSummary |> Option.map diffFileKeyOfSummary

                let nextModel =
                    {
                        model with
                            SelectedDiffHash = Some hash
                            SelectedDiffFiles = Some files
                            SelectedDiff = model.SelectedDiff
                            SelectedDiffFileKey = selectedFileKey
                            SelectionStartedAtTicks = None
                            SelectedDiffStartedAtTicks = model.SelectedDiffStartedAtTicks
                    }

                nextModel, Cmd.none
            else
                model, Cmd.none
        | DiffFilesLoaded (hash, startedAtTicks, Error err) ->
            match model.SelectedCommitHash, model.SelectionStartedAtTicks with
            | Some currentHash, Some currentStartedAtTicks when currentHash = hash && currentStartedAtTicks = startedAtTicks ->
                let elapsed = Stopwatch.GetElapsedTime(startedAtTicks)
                logTiming $"commit click -> file list error hash={hash} elapsed={elapsed.TotalMilliseconds:F1}ms error={GitError.describe err}"
            | _ ->
                ()

            if model.SelectedCommitHash = Some hash && model.SelectionStartedAtTicks = Some startedAtTicks then
                contextJobs.CancelAll()
                { model with Status = "Diff Error: " + GitError.describe err; SelectedDiffHash = None; SelectedDiffFiles = None; SelectedDiff = None; SelectedDiffFileKey = None; DiffExpansions = Map.empty; SelectionStartedAtTicks = None; SelectedDiffStartedAtTicks = None }, Cmd.none
            else
                model, Cmd.none
        | DiffLoaded (hash, startedAtTicks, Ok diff) ->
            match model.SelectedCommitHash, model.SelectedDiffStartedAtTicks with
            | Some currentCommitHash, Some currentStartedAtTicks
                when currentCommitHash = hash && currentStartedAtTicks = startedAtTicks ->
                let elapsed = Stopwatch.GetElapsedTime(startedAtTicks)
                logTiming $"commit click -> unified diff ready hash={hash} elapsed={elapsed.TotalMilliseconds:F1}ms files={diff.Length}"
            | _ ->
                ()

            if model.SelectedCommitHash = Some hash && model.SelectedDiffStartedAtTicks = Some startedAtTicks then
                { model with SelectedDiff = Some diff; SelectedDiffStartedAtTicks = None }, Cmd.none
            else
                model, Cmd.none
        | DiffLoaded (hash, startedAtTicks, Error err) ->
            match model.SelectedCommitHash, model.SelectedDiffStartedAtTicks with
            | Some currentCommitHash, Some currentStartedAtTicks
                when currentCommitHash = hash && currentStartedAtTicks = startedAtTicks ->
                let elapsed = Stopwatch.GetElapsedTime(startedAtTicks)
                logTiming $"commit click -> unified diff error hash={hash} elapsed={elapsed.TotalMilliseconds:F1}ms error={GitError.describe err}"
            | _ ->
                ()

            if model.SelectedCommitHash = Some hash && model.SelectedDiffStartedAtTicks = Some startedAtTicks then
                { model with Status = "Diff Error: " + GitError.describe err; SelectedDiff = None; SelectedDiffStartedAtTicks = None }, Cmd.none
            else
                model, Cmd.none
        | SelectDiffFile (hash, oldPath, newPath) ->
            let key: GitService.DiffFileKey =
                {
                    OldPath = oldPath
                    NewPath = newPath
                }

            if model.SelectedDiffHash = Some hash then
                { model with SelectedDiffFileKey = Some key }, Cmd.none
            else
                model, Cmd.none
        | SetFormattedFile (key, section, enabled, requestId) ->
            if not enabled then
                { model with FormattedFile = None }, Cmd.none
            else
                { model with FormattedFile = Some { Key = key; Section = section; RequestId = requestId; Content = None }; Status = "Formatting…" },
                startFormattedFileLoad model key section requestId
        | FormattedFileLoaded (key, section, requestId, result) ->
            match model.FormattedFile with
            | Some pending when pending.Key = key && pending.Section = section && pending.RequestId = requestId ->
                match result with
                | Ok content -> { model with FormattedFile = Some { pending with Content = Some content }; Status = "" }, Cmd.none
                | Error error -> { model with FormattedFile = None; Status = "Preview unavailable: " + GitError.describe error }, Cmd.none
            | _ -> model, Cmd.none
        | SetRenderedMarkdown (key, section, enabled, allowRemoteImages, requestId) ->
            if not enabled then
                { model with RenderedMarkdown = None }, Cmd.none
            else
                { model with RenderedMarkdown = Some { Key = key; Section = section; RequestId = requestId; Content = None }; Status = "Rendering Markdown…" },
                startRenderedMarkdownLoad model key section allowRemoteImages requestId
        | RenderedMarkdownLoaded (key, section, requestId, result) ->
            match model.RenderedMarkdown with
            | Some pending when pending.Key = key && pending.Section = section && pending.RequestId = requestId ->
                match result with
                | Ok content -> { model with RenderedMarkdown = Some { pending with Content = Some content }; Status = "" }, Cmd.none
                | Error error -> { model with RenderedMarkdown = None; Status = "Markdown preview unavailable: " + GitError.describe error }, Cmd.none
            | _ -> model, Cmd.none
        | OpenWholeFile (key, section, preview, allowRemoteImages, requestId) ->
            { model with WholeFile = Some { Key = key; Section = section; RequestId = requestId; Preview = preview; Payload = None }; Status = "Loading file…" },
            startWholeFileLoad model key section allowRemoteImages requestId
        | WholeFileLoaded (key, section, requestId, result) ->
            match model.WholeFile with
            | Some pending when pending.Key = key && pending.Section = section && pending.RequestId = requestId ->
                match result with
                | Ok payload -> { model with WholeFile = Some { pending with Payload = Some payload }; Status = "" }, Cmd.none
                | Error error -> { model with WholeFile = None; Status = GitError.describe error }, Cmd.none
            | _ -> model, Cmd.none
        | DismissWholeFile requestId ->
            match model.WholeFile with
            | Some current when current.RequestId = requestId -> { model with WholeFile = None }, Cmd.none
            | _ -> model, Cmd.none
        | LoadRemoteMarkdownImage source ->
            model, startRemoteMarkdownImageLoad model source
        | RemoteMarkdownImageLoaded (source, result) ->
            let updateImages images =
                images |> List.map (fun image ->
                    if image.Source <> source then image
                    else match result with
                         | Ok bytes -> { image with Bytes = Some bytes; Error = None }
                         | Error error -> { image with Error = Some(GitError.describe error) })
            let updateContent content = { content with Images = updateImages content.Images }
            let updatePayload (payload: GitService.WholeFilePayload) =
                { payload with Rendered = payload.Rendered |> Option.map updateContent }
            let renderedMarkdown =
                model.RenderedMarkdown
                |> Option.map (fun state -> { state with Content = state.Content |> Option.map updateContent })
            let wholeFile =
                model.WholeFile
                |> Option.map (fun state -> { state with Payload = state.Payload |> Option.map updatePayload })
            { model with RenderedMarkdown = renderedMarkdown; WholeFile = wholeFile }, Cmd.none
        | ExpandDiffGap (hash, gap, direction, requestedAtTicks) ->
            let key: GitService.DiffFileKey = { OldPath = gap.OldPath; NewPath = gap.NewPath }
            revealDiffRange model hash key (DiffExpansion.revealRange direction gap) requestedAtTicks
        | ExpandDiffFile (hash, key, requestedAtTicks) ->
            revealDiffRange model hash key (Some { Start = 1; End = Int32.MaxValue }) requestedAtTicks
        | CollapseDiffFileContext (hash, key) ->
            match model.SelectedDiffHash, Map.tryFind key model.DiffExpansions with
            | Some currentHash, Some expansion when currentHash = hash ->
                { model with DiffExpansions = model.DiffExpansions |> Map.add key { expansion with Revealed = [] } }, Cmd.none
            | _ ->
                model, Cmd.none
        | DiffFileContextLoaded (hash, key, requestId, result) ->
            match model.SelectedDiffHash, Map.tryFind key model.DiffExpansions with
            | Some currentHash, Some expansion when currentHash = hash && expansion.PendingRequestId = Some requestId ->
                match result with
                | Ok file ->
                    let expansion = { expansion with FullContext = Some file; PendingRequestId = None }
                    { model with DiffExpansions = model.DiffExpansions |> Map.add key expansion }, Cmd.none
                | Error err ->
                    { model with
                        Status = "Context Error: " + GitError.describe err
                        DiffExpansions = model.DiffExpansions |> Map.remove key },
                    Cmd.none
            | _ ->
                model, Cmd.none
        | CreateTag (hash, name) ->
            model, Cmd.OfFlow.ofFlow "create tag" runtime model.GitEnv (GitService.createTag hash name) (fun () -> OperationResult (Ok ())) (fun err -> OperationResult (Error err))
        | CreateBranch (hash, name) ->
            model, Cmd.OfFlow.ofFlow "create branch" runtime model.GitEnv (GitService.createBranch hash name) (fun () -> OperationResult (Ok ())) (fun err -> OperationResult (Error err))
        | CherryPick hash ->
            model, Cmd.OfFlow.ofFlow "cherry-pick" runtime model.GitEnv (GitService.cherryPick hash) (fun () -> OperationResult (Ok ())) (fun err -> OperationResult (Error err))
        | ResetTo (hash, hard) ->
            model, Cmd.OfFlow.ofFlow "reset" runtime model.GitEnv (GitService.resetTo hash hard) (fun () -> OperationResult (Ok ())) (fun err -> OperationResult (Error err))
        | Revert hash ->
            model, Cmd.OfFlow.ofFlow "revert" runtime model.GitEnv (GitService.revert hash) (fun () -> OperationResult (Ok ())) (fun err -> OperationResult (Error err))
        | OperationResult (Ok _) ->
            model, Cmd.ofMsg RereadRefs
        | OperationResult (Error err) ->
            { model with Status = "Git Error: " + GitError.describe err }, Cmd.none
        | SelectionRevisionResolved (_, Ok hash) ->
            let historyHasCommit = model.Commits |> List.exists (fun info -> info.Commit.Hash = hash)

            if model.SelectedCommitHash = Some hash then
                { model with StartupSelection = NoStartupSelection }, Cmd.none
            elif historyHasCommit then
                // History already loaded (and picked a default): select the requested commit now.
                { model with StartupSelection = NoStartupSelection }, Cmd.ofMsg (SelectCommit(hash, Stopwatch.GetTimestamp()))
            else
                // History still loading, or the commit is older than the first page: selected when it arrives.
                { model with StartupSelection = SelectionFound hash }, Cmd.none
        | SelectionRevisionResolved (revision, Error _) ->
            // Fall back to the normal view: history is unaffected; just say what couldn't be found.
            let nextModel = { model with StartupSelection = SelectionNotFound revision }
            if model.Commits.IsEmpty then nextModel, Cmd.none
            else applyStartupSelectionNotice model.HasFullHistory nextModel, Cmd.none
        | NoOp ->
            model, Cmd.none

    /// Records each message and its update time for crash and hang reports.
    let private instrumentedUpdate msg model =
        let name =
            match msg with
            | RereadRefs -> "RereadRefs"
            | NoOp -> "NoOp"
            | _ -> Diagnostics.messageTypeName (box msg)
        let startedAt = Stopwatch.GetTimestamp()
        Diagnostics.beginMessage name
        try
            update msg model
        finally
            Diagnostics.endMessage name startedAt

    let program (startupArgs: string array) =
        Program.mkProgram (fun () -> init startupArgs) instrumentedUpdate (fun _ _ -> ())
