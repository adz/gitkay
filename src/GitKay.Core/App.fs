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
            DiffLayout: DiffLayout
            SearchQuery: string
            SearchScopeKey: string
            /// Treat search text as regular expressions.
            SearchUseRegex: bool
            SearchResults: GitSearch.Result list option
            Commits: Graph.CommitGraphInfo list
            HasFullHistory: bool
            Selection: Selection
            SelectedDiffHash: string option
            SelectedDiffFiles: GitService.DiffFileSummary list option
            SelectedDiff: Models.FileDiff list option
            SelectedDiffFileKey: GitService.DiffFileKey option
            DiffExpansions: Map<GitService.DiffFileKey, FileExpansion>
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
        | SetDiffLayout of DiffLayout
        | HistoryLoaded of isFull:bool * Result<Models.Commit list, GitError>
        | SelectCommit of hash:string * startedAtTicks:int64
        /// Rereads git status (refresh, focus, file watcher).
        | RefreshWorkingTree
        | WorkingTreeStatusLoaded of Result<WorkingTree.Entry list, GitError>
        /// Selects the uncommitted changes row and loads its diffs.
        | SelectWorkingTree of startedAtTicks:int64
        | WorkingTreeChangesLoaded of startedAtTicks:int64 * Result<GitService.WorkingTreeChanges, GitError>
        | DiffFilesLoaded of hash:string * startedAtTicks:int64 * Result<GitService.DiffFileSummary list, GitError>
        | DiffLoaded of hash:string * startedAtTicks:int64 * Result<Models.FileDiff list, GitError>
        | SelectDiffFile of hash:string * oldPath:string * newPath:string
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

    let private logTiming (message: string) =
        let line = "[timing] " + message
        Trace.WriteLine line

    let private loadDiffFilesFlow (hash: string) =
        flow {
            do! Flow.Runtime.ensureNotCanceled (GitError.OperationCanceled "Selection")
            return! GitService.fetchDiffFileList hash
        }

    let private loadDiffFlow (contextLines: int) (hash: string) =
        flow {
            do! Flow.Runtime.ensureNotCanceled (GitError.OperationCanceled "Selection")
            return! GitService.fetchDiff contextLines hash
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

    let private startDiffLoad (env: GitService.GitEnv) (hash: string) (startedAtTicks: int64) (contextLines: int) =
        Cmd.OfFlow.ofFlowLatest
            $"diff {hash}"
            diffJob
            env
            (loadDiffFlow contextLines hash)
            (fun result -> DiffLoaded(hash, startedAtTicks, Ok result))
            (fun err -> DiffLoaded(hash, startedAtTicks, Error err))

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

    let private startDiffFileContextLoad (env: GitService.GitEnv) (hash: string) (key: GitService.DiffFileKey) (requestId: int64) =
        let workflow =
            flow {
                do! Flow.Runtime.ensureNotCanceled (GitError.OperationCanceled "Context expansion")
                return! GitService.fetchDiffFileFullContext hash key.OldPath key.NewPath
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
                Cmd.batch [ startDiffFilesLoad model.GitEnv hash startedAtTicks; startDiffLoad model.GitEnv hash startedAtTicks model.DiffContextLines ]
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
                startDiffFileContextLoad model.GitEnv hash key requestedAtTicks
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
                DiffLayout = Settings.defaults.DiffLayout
                SearchQuery = ""
                SearchScopeKey = "commit"
                SearchUseRegex = false
                SearchResults = None
                Commits = []
                HasFullHistory = false
                Selection = NoSelection
                SelectedDiffHash = None
                SelectedDiffFiles = None
                SelectedDiff = None
                SelectedDiffFileKey = None
                DiffExpansions = Map.empty
                SelectionStartedAtTicks = None
                SelectedDiffStartedAtTicks = None
                SearchStartedAtTicks = None
                SearchProgress = None
                WorkingTree = []
                WorkingTreeChanges = None
                WorkingTreeStartedAtTicks = None
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
                    SelectedDiffHash = None
                    SelectedDiffFiles = None
                    SelectedDiff = None
                    SelectedDiffFileKey = None
                    DiffExpansions = Map.empty
                    SelectionStartedAtTicks = None
                    SelectedDiffStartedAtTicks = None
                    SearchStartedAtTicks = None
                    SearchProgress = None
                    WorkingTree = []
                    WorkingTreeChanges = None
                    WorkingTreeStartedAtTicks = None
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

    let update msg model : Model * Cmd<Msg> =
        match msg with
        | RereadRefs ->
            searchJob.Cancel()
            let nextModel = { model with Status = "Refreshing..."; HasFullHistory = false }
            nextModel, Cmd.batch [ loadHistory model.GitEnv (Some historyLimit) model.ShowStashes model.StartupTargets; startWorkingTreeStatusLoad model.GitEnv ]
        | RefreshWorkingTree ->
            model, startWorkingTreeStatusLoad model.GitEnv
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
            { model with
                Selection = WorkingTreeSelected
                SelectedDiffHash = None
                SelectedDiffFiles = None
                SelectedDiff = None
                SelectedDiffFileKey = None
                DiffExpansions = Map.empty
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

                match model.Selection with
                | CommitSelected hash ->
                    let startedAtTicks = nextModel.SelectedDiffStartedAtTicks.Value
                    nextModel, startDiffLoad model.GitEnv hash startedAtTicks normalizedContextLines
                | WorkingTreeSelected ->
                    let startedAtTicks = Stopwatch.GetTimestamp()
                    { nextModel with WorkingTreeStartedAtTicks = Some startedAtTicks },
                    startWorkingTreeChangesLoad model.GitEnv normalizedContextLines startedAtTicks
                | NoSelection ->
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
        | SelectCommit (hash, startedAtTicks) ->
            selectionJob.Cancel()
            diffJob.Cancel()
            contextJobs.CancelAll()
            let nextModel =
                {
                    model with
                        Selection = CommitSelected hash
                        SelectedDiffHash = None
                        SelectedDiffFiles = None
                        SelectedDiff = None
                        SelectedDiffFileKey = None
                        DiffExpansions = Map.empty
                        SelectionStartedAtTicks = Some startedAtTicks
                        SelectedDiffStartedAtTicks = Some startedAtTicks
                        WorkingTreeChanges = None
                        WorkingTreeStartedAtTicks = None
                }

            workingTreeChangesJob.Cancel()
            let cmd = Cmd.batch [ startDiffFilesLoad model.GitEnv hash startedAtTicks; startDiffLoad model.GitEnv hash startedAtTicks model.DiffContextLines ]
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

            if model.SelectedCommitHash = Some hash && model.SelectedDiffHash = Some hash then
                { model with SelectedDiffFileKey = Some key }, Cmd.none
            else
                model, Cmd.none
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
