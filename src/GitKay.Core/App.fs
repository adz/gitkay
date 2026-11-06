namespace GitKay.Core

open Elmish
open System
open Axial
open Axial.ElmishFlows
open Axial.FileSystem
open GitKay.Core.Models

module App =

    /// The runtime every command of the main window runs on. The host starts it with the repository layer
    /// (`Host.startRepositoryRuntime`), so its environment holds the repository's services and caches.
    type Runtime = ElmishRuntime<GitService.GitEnv>

    let private selectionKey = "selectionJob"
    let private diffKey = "diffJob"
    let private workingTreeStatusKey = "workingTreeStatusJob"
    let private workingTreeChangesKey = "workingTreeChangesJob"
    let private searchKey = "searchJob"
    let private workingTreeContextKey = "workingTreeContextJob"
    let private renderedMarkdownKey = "renderedMarkdownJob"
    let private formattedFileKey = "formattedFileJob"
    let private wholeFileKey = "wholeFileJob"
    let private remoteMarkdownImageKey = "remoteMarkdownImageJob"

    /// The latest-wins key for a per-file context load; distinct from every file-level slot above.
    let private contextKey (key: GitService.DiffFileKey) = "context:" + key.OldPath + "\u0000" + key.NewPath

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

    /// A working-tree file's identity in the expansion map: its section (staged/unstaged/untracked) and paths.
    type WorkingTreeFileKey =
        { Section: WorkingTree.Section
          OldPath: string
          NewPath: string }

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

    /// <summary>One debounced search input: the text typed, the scope it runs under, and the generation it belongs to,
    /// so an explicit search, a cancel or a clear retires one that is still waiting out the quiet period.</summary>
    type SearchInput = { Query: string; ScopeKey: string; Generation: int64 }

    /// <summary>A running search's progress since its last report: commits checked of the total, and the matches found so
    /// far in history order when new ones arrived (None when this report found nothing new).</summary>
    type SearchUpdate = { Checked: int; Total: int; Matches: GitSearch.Result list option }

    /// <summary>What the host knows before the program starts: the launch arguments with their paths already resolved
    /// against the launch directory, and the repository they open. Reading these is the host's job, so init stays pure.</summary>
    type Flags =
        { /// The repository's git directory, or empty when none was found.
          RepoPath: string
          /// The repository's working tree, when it has one that exists; the file watcher watches it.
          WorkingDirectory: string option
          /// The parsed command line, or why it could not be parsed.
          StartupOptions: Result<GitStartup.StartupOptions, GitStartup.StartupError>
          /// How long typing must pause before a queued search runs, from the saved settings.
          SearchDebounce: TimeSpan }

    type Model =
        {
            StartupSelection: StartupSelection
            /// Command-line filters (--author, --grep, ...) ask the list to show only matching commits.
            StartupShowOnlyMatches: bool
            /// The repository's git directory, or empty when none was found.
            RepoPath: string
            /// The working tree the file watcher watches, when there is one.
            WorkingDirectory: string option
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
            /// Revealed context and full-context loads for uncommitted files, keyed by section and path.
            WorkingTreeExpansions: Map<WorkingTreeFileKey, FileExpansion>
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
            /// Successfully staged line/hunk patches, newest first, for repeated u in the working-tree diff.
            LastStagedPatches: (string * string) list
            StageUndoPending: bool
            /// How long typing must pause before the search runs; the settings window changes it.
            SearchDebounce: TimeSpan
            /// Bumped by every queued, explicit, cancelled or cleared search: a debounced input from an older generation
            /// is stale when it settles.
            SearchGeneration: int64
            /// The last request id update issued itself. Its ids count down from -1, so they never equal an id the UI
            /// issues (a positive monotonic mark).
            LastRequestId: int64
            /// Consecutive failures of the working tree watcher with no change seen in between. Each one restarts it,
            /// up to a limit; a watcher that cannot start at all gives up instead of retrying forever.
            WatchFailures: int
            /// How many times the watcher has been restarted; part of its subscription id, so a restart is a new one.
            WatchRestarts: int
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
        | WorkingTreeLinesStaged of path:string * patch:string
        | UndoLastWorkingTreeStage
        | LastWorkingTreeStageUndone
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
        | ExpandWorkingTreeFile of key:WorkingTreeFileKey * range:DiffExpansion.LineRange * requestedAtTicks:int64
        | CollapseWorkingTreeFileContext of key:WorkingTreeFileKey
        | WorkingTreeFileContextLoaded of key:WorkingTreeFileKey * requestId:int64 * Result<Models.FileDiff, GitError>
        | SetSearchQuery of string
        | SetSearchScope of string
        | SetSearchRegex of bool
        | RunSearch of query:string * scopeKey:string * startedAtTicks:int64
        | SearchResultsLoaded of query:string * scopeKey:string * startedAtTicks:int64 * Result<GitSearch.Result list, GitError>
        /// Progress and the matches found so far, for the search started at startedAtTicks; batched while it runs.
        | SearchProgressed of startedAtTicks:int64 * SearchUpdate
        /// The text, scope or regex choice changed: search once typing pauses for the debounce window.
        | QueueSearch of query:string * scopeKey:string
        /// A queued search waited out the debounce window.
        | SearchSettled of SearchInput
        /// Changes how long typing must pause before a queued search runs.
        | SetSearchDebounce of TimeSpan
        /// The working tree, index or HEAD changed on disk, after the watcher's quiet period.
        | WorkingTreeFilesChanged
        /// The working tree watcher stopped, with why.
        | WorkingTreeWatchFailed of string
        /// Stops the running search, keeping its query.
        | CancelSearch
        | CreateTag of hash:string * name:string
        | CreateBranch of hash:string * name:string
        | CherryPick of hash:string
        | ResetTo of hash:string * hard:bool
        | Revert of hash:string
        | OperationResult of Result<unit, GitError>
        | NoOp

    /// Runs a named flow on the runtime, against the repository environment it was started with.
    let private ofFlow name (runtime: Runtime) (work: Flow<GitService.GitEnv, 'error, 'value>) onSuccess onError : Cmd<Msg> =
        Cmd.OfFlow.either name runtime work onSuccess onError

    /// Runs a named flow as the latest occupant of a stable key; starting a newer one cancels the previous.
    let private ofFlowLatest key (_name: string) (runtime: Runtime) (work: Flow<GitService.GitEnv, 'error, 'value>) onSuccess onError : Cmd<Msg> =
        Cmd.OfFlow.Latest.either key runtime work onSuccess onError

    /// Cancels whatever a latest-wins key currently holds, as a command.
    let private cancelLatest (runtime: Runtime) key : Cmd<Msg> = Cmd.OfFlow.Latest.cancel key runtime

    /// Cancels every per-file context load still recorded in the expansion map.
    let private cancelAllContextJobs (runtime: Runtime) (model: Model) : Cmd<Msg> =
        let cancels = model.DiffExpansions |> Map.keys |> Seq.map (contextKey >> cancelLatest runtime) |> List.ofSeq
        match cancels with
        | [] -> Cmd.none
        | _ -> Cmd.batch cancels

    let private loadHistory (runtime: Runtime) (limit: int option) (includeStashes: bool) (targets: GitStartup.StartupTarget list) =
        let isFull = limit.IsNone
        ofFlow (if isFull then "history (full)" else $"history (first {limit.Value})") runtime (GitService.fetchHistory limit includeStashes targets) (fun r -> HistoryLoaded(isFull, Ok r)) (fun ex -> HistoryLoaded (isFull, Error ex))

    let private historyLimit = 1000

    let private plural (count: int) (noun: string) = if count = 1 then "1 " + noun else $"{count} {noun}s"
    let private fileCount (count: int) = plural count "file"
    let private lineCount (count: int) = plural count "line"

    /// <summary>Runs a staging, unstaging or discard from the history window, then rereads the working tree.</summary>
    let private workingTreeOperation (runtime: Runtime) (model: Model) (description: string) (work: Flow<GitService.GitEnv, GitError, Trash.Backup option>) =
        { model with Status = description + "…" },
        ofFlow description runtime work
            (fun backup -> WorkingTreeOperationDone(description, Ok backup))
            (fun error -> WorkingTreeOperationDone(description, Error error))

    /// <summary>Issues a request id from update itself, for a load update starts without a UI event to name it.</summary>
    let private issueRequest (model: Model) =
        let id = model.LastRequestId - 1L
        id, { model with LastRequestId = id }

    /// <summary>Logs how long a load took and what it returned. The time is measured inside the flow on the runtime's
    /// clock, so update never reads a clock; the log goes to the runtime's log sink.</summary>
    let private timed (label: string) (describe: 'value -> string) (work: Flow<GitService.GitEnv, GitError, 'value>) =
        flow {
            let! started = Clock.elapsed
            let milliseconds () = Clock.elapsed |> Flow.map (fun now -> (now - started).TotalMilliseconds)
            let! value =
                work
                |> Flow.tapError (fun error ->
                    flow {
                        let! elapsed = milliseconds ()
                        do! Log.info $"[timing] {label} error elapsed={elapsed:F1}ms error={GitError.describe error}"
                    })
            let! elapsed = milliseconds ()
            do! Log.info $"[timing] {label} ready elapsed={elapsed:F1}ms {describe value}"
            return value
        }

    let private loadDiffFilesFlow (hash: string) =
        flow {
            do! Flow.ensureNotCanceled
            return! GitService.fetchDiffFileList hash
        }
        |> timed $"commit click -> file list hash={hash}" (fun files -> $"files={List.length files}")

    let private loadDiffFlow (ignoreWhitespace: bool) (contextLines: int) (hash: string) =
        flow {
            do! Flow.ensureNotCanceled
            return! GitService.fetchDiffWith ignoreWhitespace contextLines hash
        }
        |> timed $"commit click -> unified diff hash={hash}" (fun files -> $"files={List.length files}")

    let private startDiffFilesLoad (runtime: Runtime) (hash: string) (startedAtTicks: int64) =
        ofFlowLatest selectionKey $"diff files {hash}" runtime (loadDiffFilesFlow hash)
            (fun result -> DiffFilesLoaded(hash, startedAtTicks, Ok result))
            (fun err -> DiffFilesLoaded(hash, startedAtTicks, Error err))

    let private startDiffLoad (runtime: Runtime) (hash: string) (startedAtTicks: int64) (contextLines: int) (ignoreWhitespace: bool) =
        ofFlowLatest diffKey $"diff {hash}" runtime (loadDiffFlow ignoreWhitespace contextLines hash)
            (fun result -> DiffLoaded(hash, startedAtTicks, Ok result))
            (fun err -> DiffLoaded(hash, startedAtTicks, Error err))

    let private comparisonId (baseRevision: string) (targetRevision: string) = $"comparison:{baseRevision}...{targetRevision}"

    let private startRevisionComparisonLoad (runtime: Runtime) baseRevision targetRevision startedAtTicks contextLines ignoreWhitespace =
        let work = flow {
            let! comparison = GitService.resolveRevisionComparison baseRevision targetRevision
            let! diff = GitService.fetchRevisionDiff ignoreWhitespace contextLines comparison
            return comparison, diff
        }
        ofFlowLatest diffKey $"compare {baseRevision}...{targetRevision}" runtime work
            (fun result -> RevisionComparisonLoaded(baseRevision, targetRevision, startedAtTicks, Ok result))
            (fun error -> RevisionComparisonLoaded(baseRevision, targetRevision, startedAtTicks, Error error))

    let private startWorkingTreeStatusLoad (runtime: Runtime) =
        // Without git (or outside a work tree) the status fails and the row simply doesn't appear; the log says why.
        let work =
            GitService.fetchWorkingTreeStatus
            |> Flow.tapError (fun error -> Log.info $"[timing] working tree status error={GitError.describe error}")
        ofFlowLatest workingTreeStatusKey "working tree status" runtime work
            (fun entries -> WorkingTreeStatusLoaded(Ok entries))
            (fun err -> WorkingTreeStatusLoaded(Error err))

    let private startWorkingTreeChangesLoad (runtime: Runtime) (contextLines: int) (startedAtTicks: int64) =
        ofFlowLatest workingTreeChangesKey "working tree changes" runtime (GitService.fetchWorkingTreeChanges contextLines)
            (fun changes -> WorkingTreeChangesLoaded(startedAtTicks, Ok changes))
            (fun err -> WorkingTreeChangesLoaded(startedAtTicks, Error err))

    /// <summary>What a running search reports, in the order its workers report it.</summary>
    type private SearchEvent =
        | Progressed of checkedCount: int * total: int
        | Found of GitSearch.Result
        | Finished of GitSearch.Result list

    /// <summary>Runs a search, reporting (commits checked, total) and each match as found, from any thread.</summary>
    type SearchRun = (int -> int -> unit) -> (GitSearch.Result -> unit) -> Flow<GitService.GitEnv, GitError, GitSearch.Result list>

    /// <summary>A search as a stream of its events. The search runs in a fiber the stream owns: its worker callbacks
    /// offer to an unbounded queue (they must not block or drop a match), and ending or interrupting the stream
    /// interrupts the search.</summary>
    let private searchEvents (search: SearchRun) : FlowStream<GitService.GitEnv, GitError, SearchEvent> =
        let acquire =
            flow {
                let! (events: Queue<Result<SearchEvent, GitError>>) = Queue.unbounded ()
                let offer event = Queue.tryOffer event events |> ignore
                let search =
                    search
                        (fun checkedCount total -> offer (Ok(Progressed(checkedCount, total))))
                        (fun result -> offer (Ok(Found result)))
                let producer : Flow<GitService.GitEnv, Never, unit> =
                    flow {
                        let! outcome = search |> Flow.map Ok |> Flow.recover (Error >> Flow.succeed)
                        offer (outcome |> Result.map Finished)
                        do! Dequeue.shutdown events
                    }
                let! searching = Flow.forkNamed "search producer" producer
                return events, searching
            }
        let release (_, searching) = Fiber.interrupt searching |> Flow.ignore
        FlowStream.using (Resource.create acquire release) (fun (events, _) ->
            FlowStream.fromDequeue events
            |> FlowStream.mapFlow (function
                | Ok event -> Flow.succeed event
                | Error error -> Flow.fail error))

    /// <summary>How often a running search reports: at most one progress message per window, however fast workers find
    /// matches, so a quick search can't flood the UI dispatcher.</summary>
    let searchReportWindow = TimeSpan.FromMilliseconds 200.0

    type private SearchReports =
        { Found: GitSearch.Result list
          Progress: (int * int) option
          Report: Msg option }

    /// <summary>The search's messages: batched progress with the matches so far in history order, then its results.
    /// A typed failure ends it with an error result.</summary>
    let searchMessages (search: SearchRun) (commits: Graph.CommitGraphInfo list) (query: string) (scopeKey: string) (startedAtTicks: int64)
        : FlowStream<GitService.GitEnv, GitError, Msg> =
        let order = commits |> List.mapi (fun index info -> info.Commit.Hash, index) |> dict
        let report (state: SearchReports) (batch: SearchEvent list) =
            let found = batch |> List.choose (function Found result -> Some result | _ -> None)
            let progress =
                batch
                |> List.choose (function Progressed(checkedCount, total) -> Some(checkedCount, total) | _ -> None)
                |> List.tryLast
                |> Option.orElse state.Progress
            let all = state.Found @ found
            let finished = batch |> List.tryPick (function Finished results -> Some results | _ -> None)
            let message =
                match finished, progress with
                | Some results, _ -> Some(SearchResultsLoaded(query, scopeKey, startedAtTicks, Ok results))
                | None, Some(checkedCount, total) ->
                    let matches = if found.IsEmpty then None else Some(all |> List.sortBy (fun result -> order.[result.Commit.Hash]))
                    Some(SearchProgressed(startedAtTicks, { Checked = checkedCount; Total = total; Matches = matches }))
                | None, None -> None
            { Found = all; Progress = progress; Report = message }

        searchEvents search
        |> FlowStream.groupedWithin 4096 searchReportWindow
        |> FlowStream.scan report { Found = []; Progress = None; Report = None }
        |> FlowStream.choose _.Report

    let private startSearchLoad (runtime: Runtime) (contextLines: int) (commits: Graph.CommitGraphInfo list) (query: string) (scopeKey: string) (useRegex: bool) (startedAtTicks: int64) =
        let commitList = commits |> List.map (fun info -> info.Commit)
        let search = GitService.searchCommitsStreaming contextLines commitList (GitSearch.parseMode scopeKey) useRegex query
        Cmd.OfFlowStream.latest searchKey runtime
            (searchMessages search commits query scopeKey startedAtTicks)
            id
            (fun err -> SearchResultsLoaded(query, scopeKey, startedAtTicks, Error err))

    let private startDiffFileContextLoad (runtime: Runtime) (comparison: GitService.RevisionComparison option) (hash: string) (key: GitService.DiffFileKey) (requestId: int64) =
        let workflow =
            flow {
                do! Flow.ensureNotCanceled
                match comparison with
                | Some comparison -> return! GitService.fetchRevisionDiffFileFullContext comparison key.OldPath key.NewPath
                | None -> return! GitService.fetchDiffFileFullContext hash key.OldPath key.NewPath
            }

        ofFlowLatest (contextKey key) $"full context {hash} {key.NewPath}" runtime workflow
            (fun result -> DiffFileContextLoaded(hash, key, requestId, Ok result))
            (fun err -> DiffFileContextLoaded(hash, key, requestId, Error err))

    let private startWorkingTreeContextLoad (runtime: Runtime) (key: WorkingTreeFileKey) (requestId: int64) =
        let workflow =
            flow {
                do! Flow.ensureNotCanceled
                return! GitService.fetchWorkingTreeFile key.Section key.OldPath key.NewPath
            }

        ofFlowLatest workingTreeContextKey $"working tree context {key.NewPath}" runtime workflow
            (fun result -> WorkingTreeFileContextLoaded(key, requestId, Ok result))
            (fun err -> WorkingTreeFileContextLoaded(key, requestId, Error err))

    let private diffFileKeyOfSummary (summary: GitService.DiffFileSummary) : GitService.DiffFileKey =
        {
            OldPath = summary.OldPath
            NewPath = summary.NewPath
        }

    let private tryFindDiffFileSummary (key: GitService.DiffFileKey) (files: GitService.DiffFileSummary list) =
        files
        |> List.tryFind (fun file -> file.OldPath = key.OldPath && file.NewPath = key.NewPath)

    let private historyLoadSelection (runtime: Runtime) (model: Model) (commits: Graph.CommitGraphInfo list) =
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

        let nextSelectionStartedAtTicks, model =
            if selectedHash.IsSome && not diffReadyForSelection then
                let requestId, model = issueRequest model
                Some requestId, model
            else
                None, model

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
                Cmd.batch [ startDiffFilesLoad runtime hash startedAtTicks; startDiffLoad runtime hash startedAtTicks model.DiffContextLines model.IgnoreWhitespace ]
            | _ when keepsWorkingTree -> Cmd.none
            | _ ->
                if not diffReadyForSelection then
                    Cmd.batch [ cancelLatest runtime selectionKey; cancelLatest runtime diffKey; cancelAllContextJobs runtime model ]
                else
                    cancelLatest runtime selectionKey

        nextModel, cmd

    let private revealDiffRange (runtime: Runtime) (model: Model) (hash: string) (key: GitService.DiffFileKey) (range: DiffExpansion.LineRange option) (requestedAtTicks: int64) =
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
                startDiffFileContextLoad runtime model.RevisionComparison hash key requestedAtTicks
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

    /// <summary>Flags for a program whose command line needs no path resolution (no repository-relative paths are
    /// looked up): tests, and the self-test. The desktop host resolves paths with <c>Host.flags</c>.</summary>
    let flags (repoPath: string) (startupArgs: string array) : Flags =
        { RepoPath = repoPath
          WorkingDirectory = None
          StartupOptions = GitStartup.parseStartupOptions startupArgs
          SearchDebounce = TimeSpan.FromSeconds Settings.defaults.SearchDebounceSeconds }

    let private blankModel (flags: Flags) : Model =
        {
            StartupSelection = NoStartupSelection
            StartupShowOnlyMatches = false
            RepoPath = flags.RepoPath
            WorkingDirectory = flags.WorkingDirectory
            Status = ""
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
            WorkingTreeExpansions = Map.empty
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
            LastStagedPatches = []
            StageUndoPending = false
            SearchDebounce = flags.SearchDebounce
            SearchGeneration = 0L
            LastRequestId = 0L
            WatchFailures = 0
            WatchRestarts = 0
        }

    let init (runtime: Runtime) (flags: Flags) : Model * Cmd<Msg> =
        match flags.StartupOptions with
        | Error err ->
            { blankModel flags with Status = "Error: " + GitStartup.describeError err }, Cmd.none
        | Ok startupOptions ->
            let model =
                { blankModel flags with
                    StartupSelection =
                        match startupOptions.SelectedCommitHash with
                        | Some revision -> ResolvingSelection revision
                        | None -> NoStartupSelection
                    StartupShowOnlyMatches = startupOptions.ShowOnlyMatches
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
                        | None -> NoSelection }

            if String.IsNullOrEmpty flags.RepoPath then
                { model with Status = "Error: Could not locate a Git repository." }, Cmd.none
            else
                let resolveSelection =
                    match startupOptions.SelectedCommitHash with
                    | Some revision ->
                        ofFlow $"resolve {revision}" runtime (GitService.resolveCommit revision)
                            (fun hash -> SelectionRevisionResolved(revision, Ok hash))
                            (fun err -> SelectionRevisionResolved(revision, Error err))
                    | None -> Cmd.none

                model,
                Cmd.batch
                    [ loadHistory runtime (Some historyLimit) startupOptions.ShowStashes startupOptions.StartupTargets
                      startWorkingTreeStatusLoad runtime
                      resolveSelection ]

    let private startRenderedMarkdownLoad (runtime: Runtime) (model: Model) (key: GitService.DiffFileKey) section allowRemoteImages requestId =
        let load (env: GitService.GitEnv) =
            if model.IsWorkingTreeSelected then
                match WorkingTree.tryParseSection section with
                | Some parsed -> GitService.loadWorkingTreeRenderedMarkdown env parsed key.OldPath key.NewPath allowRemoteImages
                | None -> Error(GitError.OperationFailed("Render Markdown", $"Unknown working-tree section: {section}"))
            else
                match model.RevisionComparison, model.SelectedDiffHash with
                | Some comparison, _ -> GitService.loadRevisionRenderedMarkdown model.RepoPath comparison key.OldPath key.NewPath allowRemoteImages
                | None, Some hash -> GitService.loadCommitRenderedMarkdown model.RepoPath hash key.OldPath key.NewPath allowRemoteImages
                | _ -> Error(GitError.OperationFailed("Render Markdown", "No revision is selected"))
        ofFlowLatest renderedMarkdownKey $"render markdown {key.NewPath}" runtime
            // LibGit2Sharp blob reads do not accept cancellation; latest-slot cancellation still rejects stale results.
            (Flow.env |> Flow.bind (fun env -> Flow.fromResult (load env)) |> Flow.runOn RunOn.DedicatedThread)
            (fun content -> RenderedMarkdownLoaded(key, section, requestId, Ok content))
            (fun error -> RenderedMarkdownLoaded(key, section, requestId, Error error))

    let private startFormattedFileLoad (runtime: Runtime) (model: Model) (key: GitService.DiffFileKey) section requestId =
        let previewPath = FileChange.currentPath key.OldPath key.NewPath
        let isImage = (Markdown.previewKind previewPath).IsImagePreview
        let load (env: GitService.GitEnv) =
            if model.IsWorkingTreeSelected then
                match WorkingTree.tryParseSection section with
                | Some parsed ->
                    if isImage then GitService.loadWorkingTreePreviewImage env parsed key.OldPath key.NewPath |> Result.map PreviewImage
                    else GitService.loadWorkingTreeFormattedFile model.DiffContextLines env parsed key.OldPath key.NewPath |> Result.map FormattedText
                | None -> Error(GitError.OperationFailed("Preview", $"Unknown working-tree section: {section}"))
            else
                match model.RevisionComparison, model.SelectedDiffHash with
                | Some comparison, _ ->
                    if isImage then GitService.loadRevisionPreviewImage model.RepoPath comparison key.OldPath key.NewPath |> Result.map PreviewImage
                    else GitService.loadRevisionFormattedFile model.DiffContextLines model.RepoPath comparison key.OldPath key.NewPath |> Result.map FormattedText
                | None, Some hash ->
                    if isImage then GitService.loadCommitPreviewImage model.RepoPath hash key.OldPath key.NewPath |> Result.map PreviewImage
                    else GitService.loadCommitFormattedFile model.DiffContextLines model.RepoPath hash key.OldPath key.NewPath |> Result.map FormattedText
                | _ -> Error(GitError.OperationFailed("Preview", "No revision is selected"))
        ofFlowLatest formattedFileKey $"format file {key.NewPath}" runtime
            // LibGit2Sharp blob reads do not accept cancellation; latest-slot cancellation still rejects stale results.
            (Flow.env |> Flow.bind (fun env -> Flow.fromResult (load env)) |> Flow.runOn RunOn.DedicatedThread)
            (fun content -> FormattedFileLoaded(key, section, requestId, Ok content))
            (fun error -> FormattedFileLoaded(key, section, requestId, Error error))

    let private startWholeFileLoad (runtime: Runtime) (model: Model) (key: GitService.DiffFileKey) section allowRemoteImages requestId =
        let load (env: GitService.GitEnv) =
            if model.IsWorkingTreeSelected then
                if String.IsNullOrWhiteSpace section then
                    GitService.loadCommitWholeFilePayload model.RepoPath "HEAD" key.OldPath key.NewPath allowRemoteImages
                else
                    match WorkingTree.tryParseSection section with
                    | Some parsed -> GitService.loadWorkingTreeWholeFilePayload env parsed key.OldPath key.NewPath allowRemoteImages
                    | None -> Error(GitError.OperationFailed("Open file", $"Unknown working-tree section: {section}"))
            else
                match model.RevisionComparison, model.SelectedDiffHash with
                | Some comparison, _ ->
                    GitService.loadRevisionWholeFilePayload model.RepoPath comparison key.OldPath key.NewPath allowRemoteImages
                | None, Some hash ->
                    GitService.loadCommitWholeFilePayload model.RepoPath hash key.OldPath key.NewPath allowRemoteImages
                | _ -> Error(GitError.OperationFailed("Open file", "No commit revision is selected"))
        ofFlowLatest wholeFileKey $"whole file {key.NewPath}" runtime
            // LibGit2Sharp reads have no cancellation overload; the latest slot rejects stale completion.
            (Flow.env |> Flow.bind (fun env -> Flow.fromResult (load env)) |> Flow.runOn RunOn.DedicatedThread)
            (fun payload -> WholeFileLoaded(key, section, requestId, Ok payload))
            (fun error -> WholeFileLoaded(key, section, requestId, Error error))

    let private startRemoteMarkdownImageLoad (runtime: Runtime) (model: Model) source =
        ofFlowLatest remoteMarkdownImageKey $"remote markdown image {source}" runtime
            (GitService.loadRemoteMarkdownImage source)
            (fun bytes -> RemoteMarkdownImageLoaded(source, Ok bytes))
            (fun error -> RemoteMarkdownImageLoaded(source, Error error))

    /// <summary>The program's update. Pure: every effect is a command on <paramref name="runtime"/>, and a queued search
    /// goes to <paramref name="searchInbox"/>, which <c>subscriptions</c> debounces.</summary>
    let rec update (runtime: Runtime) (searchInbox: Inbox<SearchInput>) (msg: Msg) (model: Model) : Model * Cmd<Msg> =
        let runSearch query scopeKey requestId model = update runtime searchInbox (RunSearch(query, scopeKey, requestId)) model
        match msg with
        | RereadRefs ->
            let nextModel = { model with Status = "Refreshing..."; HasFullHistory = false }
            nextModel, Cmd.batch [ cancelLatest runtime searchKey; loadHistory runtime (Some historyLimit) model.ShowStashes model.StartupTargets; startWorkingTreeStatusLoad runtime ]
        | RefreshWorkingTree ->
            model, startWorkingTreeStatusLoad runtime
        | StageWorkingTree [] | UnstageWorkingTree [] | DiscardWorkingTree([], []) | ApplyWorkingTreeLines(_, _, []) ->
            model, Cmd.none
        | StageWorkingTree paths ->
            workingTreeOperation runtime { model with LastStagedPatches = [] } $"Staged {fileCount paths.Length}" (GitService.stageFiles paths |> Flow.map (fun () -> None))
        | UnstageWorkingTree paths ->
            workingTreeOperation runtime { model with LastStagedPatches = [] } $"Unstaged {fileCount paths.Length}" (GitService.unstageFiles paths |> Flow.map (fun () -> None))
        | DiscardWorkingTree(tracked, untracked) ->
            workingTreeOperation runtime { model with LastStagedPatches = [] } $"Discarded {fileCount (tracked.Length + untracked.Length)}"
                (GitService.discardFilesWithBackup tracked untracked |> Flow.map Some)
        | ApplyWorkingTreeLines(GitService.DiscardFromWorkingTree, path, lines) ->
            workingTreeOperation runtime { model with LastStagedPatches = [] } $"Discarded {lineCount lines.Length} of {path}"
                (GitService.discardLinesWithBackupAt model.DiffContextLines path lines |> Flow.map Some)
        | ApplyWorkingTreeLines(GitService.StageInIndex, path, lines) ->
            let description = $"Staging {lineCount lines.Length} of {path}"
            { model with Status = description + "…" },
            ofFlow description runtime (GitService.stageLinesWithUndoAt model.DiffContextLines path lines)
                (fun patch -> WorkingTreeLinesStaged(path, patch))
                (fun error -> WorkingTreeOperationDone(description, Error error))
        | ApplyWorkingTreeLines(target, path, lines) ->
            let verb = if target = GitService.StageInIndex then "Staged" else "Unstaged"
            workingTreeOperation runtime { model with LastStagedPatches = [] } $"{verb} {lineCount lines.Length} of {path}"
                (GitService.applyLinesWithContext model.DiffContextLines target path lines |> Flow.map (fun () -> None))
        | WorkingTreeLinesStaged(path, patch) ->
            { model with LastStagedPatches = (path, patch) :: model.LastStagedPatches; Status = "Staged · u to undo" }, Cmd.ofMsg RefreshWorkingTree
        | UndoLastWorkingTreeStage ->
            match model.StageUndoPending, model.LastStagedPatches with
            | true, _ -> model, Cmd.none
            | false, [] -> { model with Status = "Nothing to unstage" }, Cmd.none
            | false, (_, patch) :: _ ->
                { model with StageUndoPending = true; Status = "Unstaging the last selection…" },
                ofFlow "undo last stage" runtime (GitService.undoStagedPatch patch)
                    (fun () -> LastWorkingTreeStageUndone)
                    (fun error -> WorkingTreeOperationDone("Undo stage", Error error))
        | LastWorkingTreeStageUndone ->
            let remaining = match model.LastStagedPatches with _ :: rest -> rest | [] -> []
            { model with LastStagedPatches = remaining; StageUndoPending = false; Status = "Last stage undone" }, Cmd.ofMsg RefreshWorkingTree
        | UndoWorkingTreeDiscard ->
            match model.LastDiscard with
            | None -> { model with Status = "Nothing to undo" }, Cmd.none
            | Some backup ->
                { model with LastDiscard = None },
                ofFlow "undo discard" runtime (GitService.undoDiscard backup |> Flow.map (fun () -> None))
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
            { model with StageUndoPending = false; Status = $"{description} failed: " + GitError.describe error }, Cmd.ofMsg RefreshWorkingTree
        | WorkingTreeStatusLoaded (Ok entries) ->
            let changed = entries <> model.WorkingTree

            if model.IsWorkingTreeSelected && entries.IsEmpty then
                // Everything was committed or discarded: the row goes, so select the newest commit instead.
                let nextModel = { model with WorkingTree = []; WorkingTreeChanges = None; WorkingTreeStartedAtTicks = None; Selection = NoSelection }
                match model.Commits with
                | first :: _ ->
                    let requestId, nextModel = issueRequest nextModel
                    nextModel, Cmd.ofMsg (SelectCommit(first.Commit.Hash, requestId))
                | [] -> nextModel, Cmd.none
            elif model.IsWorkingTreeSelected && changed then
                let startedAtTicks, model = issueRequest model
                { model with WorkingTree = entries; WorkingTreeStartedAtTicks = Some startedAtTicks },
                startWorkingTreeChangesLoad runtime model.DiffContextLines startedAtTicks
            elif changed then
                { model with WorkingTree = entries }, Cmd.none
            else
                model, Cmd.none
        | WorkingTreeStatusLoaded (Error err) ->
            // Without git (or outside a work tree) the row simply doesn't appear; the load logged why.
            ignore err
            { model with WorkingTree = [] }, Cmd.none
        | SelectWorkingTree startedAtTicks ->
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
            Cmd.batch
                [ cancelLatest runtime selectionKey
                  cancelLatest runtime diffKey
                  cancelAllContextJobs runtime model
                  cancelLatest runtime renderedMarkdownKey
                  cancelLatest runtime formattedFileKey
                  startWorkingTreeChangesLoad runtime model.DiffContextLines startedAtTicks ]
        | WorkingTreeChangesLoaded (startedAtTicks, result) when model.IsWorkingTreeSelected && model.WorkingTreeStartedAtTicks = Some startedAtTicks ->
            match result with
            | Ok changes ->
                // A refresh drops revealed context for files whose change is different, so a stale expansion never
                // re-projects old hunks over new content.
                let fileDiffMap (c: GitService.WorkingTreeChanges) =
                    GitService.workingTreeSections c
                    |> List.collect (fun (section, files) ->
                        files |> List.map (fun f -> { Section = section; OldPath = f.OldPath; NewPath = f.NewPath }, f))
                    |> Map.ofList
                let prunedExpansions =
                    match model.WorkingTreeChanges with
                    | None -> model.WorkingTreeExpansions
                    | Some oldChanges ->
                        let oldFiles = fileDiffMap oldChanges
                        let newFiles = fileDiffMap changes
                        model.WorkingTreeExpansions
                        |> Map.filter (fun key _ ->
                            match Map.tryFind key oldFiles, Map.tryFind key newFiles with
                            | Some oldFile, Some newFile -> oldFile = newFile
                            | None, Some _ -> true
                            | _ -> false)
                { model with
                    WorkingTree = changes.Entries
                    WorkingTreeChanges = Some changes
                    WorkingTreeStartedAtTicks = None
                    WorkingTreeExpansions = prunedExpansions }, Cmd.none
            | Error err ->
                { model with Status = "Diff Error: " + GitError.describe err; WorkingTreeChanges = None; WorkingTreeStartedAtTicks = None }, Cmd.none
        | WorkingTreeChangesLoaded _ ->
            model, Cmd.none
        | SetHistoryTargets targets ->
            let nextModel = { model with StartupTargets = targets; Status = "Loading history..."; HasFullHistory = false }
            nextModel, Cmd.batch [ cancelLatest runtime searchKey; loadHistory runtime (Some historyLimit) model.ShowStashes targets ]
        | SetShowBranchRefs showBranchRefs ->
            { model with ShowBranchRefs = showBranchRefs }, Cmd.none
        | SetShowStashes showStashes ->
            let nextModel =
                {
                    model with
                        ShowStashes = showStashes
                        Status = "Refreshing..."
                        HasFullHistory = false
                }

            nextModel, Cmd.batch [ cancelLatest runtime searchKey; loadHistory runtime (Some historyLimit) showStashes model.StartupTargets ]
        | SetIgnoreWhitespace ignoreWhitespace ->
            if ignoreWhitespace = model.IgnoreWhitespace then
                model, Cmd.none
            else
                // The diff itself changes, so whatever is shown is reread the way the context-line choice rereads it.
                let requestId, issued = issueRequest model
                let nextModel =
                    { issued with
                        IgnoreWhitespace = ignoreWhitespace
                        SelectedDiff = None
                        SelectedDiffStartedAtTicks = model.SelectedCommitHash |> Option.map (fun _ -> requestId) }

                match model.RevisionComparison, model.Selection with
                | Some comparison, _ ->
                    let startedAtTicks = requestId
                    { nextModel with SelectedDiffStartedAtTicks = Some startedAtTicks },
                    startRevisionComparisonLoad runtime comparison.BaseRevision comparison.TargetRevision startedAtTicks model.DiffContextLines ignoreWhitespace
                | None, CommitSelected hash ->
                    let startedAtTicks = nextModel.SelectedDiffStartedAtTicks.Value
                    nextModel, startDiffLoad runtime hash startedAtTicks model.DiffContextLines ignoreWhitespace
                | None, (WorkingTreeSelected | NoSelection) -> nextModel, Cmd.none
        | SetDiffContextLines diffContextLines ->
            let normalizedContextLines = max 0 diffContextLines

            if normalizedContextLines = model.DiffContextLines then
                model, Cmd.none
            else
                let requestId, issued = issueRequest model
                let nextModel =
                    {
                        issued with
                            DiffContextLines = normalizedContextLines
                            SelectedDiff = None
                            SelectedDiffStartedAtTicks = model.SelectedCommitHash |> Option.map (fun _ -> requestId)
                    }

                match model.RevisionComparison, model.Selection with
                | Some comparison, _ ->
                    let startedAtTicks = requestId
                    { nextModel with SelectedDiffStartedAtTicks = Some startedAtTicks },
                    startRevisionComparisonLoad runtime comparison.BaseRevision comparison.TargetRevision startedAtTicks normalizedContextLines model.IgnoreWhitespace
                | None, CommitSelected hash ->
                    let startedAtTicks = nextModel.SelectedDiffStartedAtTicks.Value
                    nextModel, startDiffLoad runtime hash startedAtTicks normalizedContextLines model.IgnoreWhitespace
                | None, WorkingTreeSelected ->
                    let startedAtTicks = requestId
                    { nextModel with WorkingTreeStartedAtTicks = Some startedAtTicks },
                    startWorkingTreeChangesLoad runtime normalizedContextLines startedAtTicks
                | None, NoSelection ->
                    nextModel, cancelLatest runtime diffKey
        | SetDiffLayout layout -> { model with DiffLayout = layout }, Cmd.none
        | HistoryLoaded (isFull, Ok commits) ->
            // A path-limited first page scans a bounded number of commits, so few matches doesn't mean it's complete.
            let hasPaths = model.StartupTargets |> List.exists (function GitStartup.StartupTarget.Path _ -> true | _ -> false)
            if isFull || (not hasPaths && commits.Length < historyLimit) then
                let graphInfo = Graph.calculateLanes commits
                let nextModel, historyCmd = historyLoadSelection runtime model graphInfo
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
                    nextModel, Cmd.batch [ cancelLatest runtime searchKey; historyCmd ]
                else
                    let startedAtTicks, nextModel = issueRequest nextModel
                    let searchModel =
                        {
                            nextModel with
                                SearchStartedAtTicks = Some startedAtTicks
                                Status = $"Searching {nextModel.SearchQuery}..."
                        }

                    searchModel, Cmd.batch [ cancelLatest runtime searchKey; historyCmd; startSearchLoad runtime searchModel.DiffContextLines graphInfo nextModel.SearchQuery nextModel.SearchScopeKey model.SearchUseRegex startedAtTicks ]
            else
                // Partial history loaded
                let graphInfo = Graph.calculateLanes commits
                let nextModel, historyCmd = historyLoadSelection runtime model graphInfo
                let nextModel = { nextModel with Status = $"Loaded {commits.Length} commits (loading more...)" } |> applyStartupSelectionNotice false
                
                let loadFullCmd = loadHistory runtime None model.ShowStashes model.StartupTargets
                
                let searchQuery = nextModel.SearchQuery.Trim()
                if String.IsNullOrWhiteSpace searchQuery then
                    nextModel, Cmd.batch [ historyCmd; loadFullCmd ]
                else
                    let startedAtTicks, nextModel = issueRequest nextModel
                    let searchModel =
                        {
                            nextModel with
                                SearchStartedAtTicks = Some startedAtTicks
                                Status = $"Searching {nextModel.SearchQuery}..."
                        }

                    searchModel, Cmd.batch [ historyCmd; loadFullCmd; startSearchLoad runtime searchModel.DiffContextLines graphInfo nextModel.SearchQuery nextModel.SearchScopeKey model.SearchUseRegex startedAtTicks ]
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
            nextModel, loadHistory runtime (Some historyLimit) model.ShowStashes fallbackTargets
        | HistoryLoaded (_, Error err) ->
            { model with Status = "Error: " + GitError.describe err; SearchResults = None; SearchStartedAtTicks = None }, cancelLatest runtime searchKey
        | OpenRevisionComparison (baseRevision, targetRevision, startedAtTicks) ->
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
            Cmd.batch
                [ cancelLatest runtime selectionKey
                  cancelLatest runtime diffKey
                  cancelAllContextJobs runtime model
                  cancelLatest runtime renderedMarkdownKey
                  cancelLatest runtime formattedFileKey
                  startRevisionComparisonLoad runtime baseRevision targetRevision startedAtTicks model.DiffContextLines model.IgnoreWhitespace ]
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
            | Some hash ->
                let requestId, model = issueRequest model
                model, Cmd.ofMsg (SelectCommit(hash, requestId))
            | None -> { model with RevisionComparison = None }, Cmd.none
        | SelectCommit (hash, startedAtTicks) ->
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

            let cmd =
                Cmd.batch
                    [ cancelLatest runtime selectionKey
                      cancelLatest runtime diffKey
                      cancelAllContextJobs runtime model
                      cancelLatest runtime renderedMarkdownKey
                      cancelLatest runtime formattedFileKey
                      cancelLatest runtime workingTreeChangesKey
                      startDiffFilesLoad runtime hash startedAtTicks
                      startDiffLoad runtime hash startedAtTicks model.DiffContextLines model.IgnoreWhitespace ]
            nextModel, cmd
        | SetSearchQuery query ->
            let trimmed = query.Trim()

            if String.IsNullOrWhiteSpace trimmed then
                { model with SearchQuery = query; SearchResults = None; SearchStartedAtTicks = None }, cancelLatest runtime searchKey
            else
                { model with SearchQuery = query }, Cmd.none
        | SetSearchScope scopeKey ->
            { model with SearchScopeKey = scopeKey }, Cmd.none
        | SetSearchRegex useRegex ->
            { model with SearchUseRegex = useRegex }, Cmd.none
        | RunSearch (query, scopeKey, startedAtTicks) ->
            // An explicit search (or a clear) retires any queued one still waiting out the debounce window.
            let model = { model with SearchGeneration = model.SearchGeneration + 1L }
            if String.IsNullOrWhiteSpace query then
                { model with SearchQuery = query; SearchScopeKey = scopeKey; SearchResults = None; SearchStartedAtTicks = None; Status = "Search cleared" }, cancelLatest runtime searchKey
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

                let cmd = Cmd.batch [ cancelLatest runtime searchKey; startSearchLoad runtime model.DiffContextLines model.Commits query scopeKey model.SearchUseRegex startedAtTicks ]
                nextModel, cmd
        | SearchProgressed (startedAtTicks, progress) ->
            match model.SearchStartedAtTicks with
            | Some current when current = startedAtTicks ->
                let model =
                    match progress.Matches with
                    | Some results -> { model with SearchResults = Some results }
                    | None -> model
                if progress.Checked < progress.Total then
                    { model with
                        SearchProgress = Some(progress.Checked, progress.Total)
                        Status =
                            let matches = model.SearchResults |> Option.map List.length |> Option.defaultValue 0
                            $"Searching {model.SearchQuery}... {progress.Checked:N0} of {progress.Total:N0} commits, {matches:N0} matches so far (Esc to cancel)" },
                    Cmd.none
                else
                    model, Cmd.none
            | _ -> model, Cmd.none
        | QueueSearch (query, scopeKey) ->
            if String.IsNullOrWhiteSpace query then
                model, Cmd.none
            else
                let generation = model.SearchGeneration + 1L
                { model with SearchGeneration = generation },
                Inbox.offer { Query = query; ScopeKey = scopeKey; Generation = generation } searchInbox
        | SearchSettled input ->
            if input.Generation <> model.SearchGeneration || String.IsNullOrWhiteSpace input.Query then
                model, Cmd.none
            else
                let requestId, model = issueRequest model
                // Run it as the explicit search would, so the generation moves on and retires nothing newer.
                runSearch input.Query input.ScopeKey requestId model
        | SetSearchDebounce window ->
            { model with SearchDebounce = (if window < TimeSpan.Zero then TimeSpan.Zero else window) }, Cmd.none
        | WorkingTreeFilesChanged ->
            { model with WatchFailures = 0 }, startWorkingTreeStatusLoad runtime
        | WorkingTreeWatchFailed _ ->
            // Overflowed or broken watchers lose events: reread now, and start a new watcher unless this one keeps failing.
            { model with WatchFailures = model.WatchFailures + 1; WatchRestarts = model.WatchRestarts + 1 },
            startWorkingTreeStatusLoad runtime
        | CancelSearch ->
            let model = { model with SearchGeneration = model.SearchGeneration + 1L }
            match model.SearchStartedAtTicks with
            | Some _ ->
                { model with SearchStartedAtTicks = None; SearchProgress = None; Status = $"Search cancelled: {model.SearchQuery}" }, cancelLatest runtime searchKey
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
            if model.SelectedCommitHash = Some hash && model.SelectionStartedAtTicks = Some startedAtTicks then
                { model with Status = "Diff Error: " + GitError.describe err; SelectedDiffHash = None; SelectedDiffFiles = None; SelectedDiff = None; SelectedDiffFileKey = None; DiffExpansions = Map.empty; SelectionStartedAtTicks = None; SelectedDiffStartedAtTicks = None }, cancelAllContextJobs runtime model
            else
                model, Cmd.none
        | DiffLoaded (hash, startedAtTicks, Ok diff) ->
            if model.SelectedCommitHash = Some hash && model.SelectedDiffStartedAtTicks = Some startedAtTicks then
                { model with SelectedDiff = Some diff; SelectedDiffStartedAtTicks = None }, Cmd.none
            else
                model, Cmd.none
        | DiffLoaded (hash, startedAtTicks, Error err) ->
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
                startFormattedFileLoad runtime model key section requestId
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
                startRenderedMarkdownLoad runtime model key section allowRemoteImages requestId
        | RenderedMarkdownLoaded (key, section, requestId, result) ->
            match model.RenderedMarkdown with
            | Some pending when pending.Key = key && pending.Section = section && pending.RequestId = requestId ->
                match result with
                | Ok content -> { model with RenderedMarkdown = Some { pending with Content = Some content }; Status = "" }, Cmd.none
                | Error error -> { model with RenderedMarkdown = None; Status = "Markdown preview unavailable: " + GitError.describe error }, Cmd.none
            | _ -> model, Cmd.none
        | OpenWholeFile (key, section, preview, allowRemoteImages, requestId) ->
            { model with WholeFile = Some { Key = key; Section = section; RequestId = requestId; Preview = preview; Payload = None }; Status = "Loading file…" },
            startWholeFileLoad runtime model key section allowRemoteImages requestId
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
            model, startRemoteMarkdownImageLoad runtime model source
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
            revealDiffRange runtime model hash key (DiffExpansion.revealRange direction gap) requestedAtTicks
        | ExpandDiffFile (hash, key, requestedAtTicks) ->
            revealDiffRange runtime model hash key (Some { Start = 1; End = Int32.MaxValue }) requestedAtTicks
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
        | ExpandWorkingTreeFile (key, range, requestedAtTicks) ->
            if model.IsWorkingTreeSelected then
                let current =
                    model.WorkingTreeExpansions
                    |> Map.tryFind key
                    |> Option.defaultValue { FullContext = None; Revealed = []; PendingRequestId = None }
                let revealed = { current with Revealed = DiffExpansion.addRange range current.Revealed }
                match current.FullContext, current.PendingRequestId with
                | None, None ->
                    let expansion = { revealed with PendingRequestId = Some requestedAtTicks }
                    { model with WorkingTreeExpansions = model.WorkingTreeExpansions |> Map.add key expansion },
                    startWorkingTreeContextLoad runtime key requestedAtTicks
                | _ ->
                    { model with WorkingTreeExpansions = model.WorkingTreeExpansions |> Map.add key revealed }, Cmd.none
            else
                model, Cmd.none
        | CollapseWorkingTreeFileContext key ->
            match Map.tryFind key model.WorkingTreeExpansions with
            | Some expansion ->
                { model with WorkingTreeExpansions = model.WorkingTreeExpansions |> Map.add key { expansion with Revealed = [] } }, Cmd.none
            | None ->
                model, Cmd.none
        | WorkingTreeFileContextLoaded (key, requestId, result) ->
            match Map.tryFind key model.WorkingTreeExpansions with
            | Some expansion when expansion.PendingRequestId = Some requestId ->
                match result with
                | Ok file ->
                    let expansion = { expansion with FullContext = Some file; PendingRequestId = None }
                    { model with WorkingTreeExpansions = model.WorkingTreeExpansions |> Map.add key expansion }, Cmd.none
                | Error err ->
                    { model with
                        Status = "Context Error: " + GitError.describe err
                        WorkingTreeExpansions = model.WorkingTreeExpansions |> Map.remove key },
                    Cmd.none
            | _ ->
                model, Cmd.none
        | CreateTag (hash, name) ->
            model, ofFlow "create tag" runtime (GitService.createTag hash name) (fun () -> OperationResult (Ok ())) (fun err -> OperationResult (Error err))
        | CreateBranch (hash, name) ->
            model, ofFlow "create branch" runtime (GitService.createBranch hash name) (fun () -> OperationResult (Ok ())) (fun err -> OperationResult (Error err))
        | CherryPick hash ->
            model, ofFlow "cherry-pick" runtime (GitService.cherryPick hash) (fun () -> OperationResult (Ok ())) (fun err -> OperationResult (Error err))
        | ResetTo (hash, hard) ->
            model, ofFlow "reset" runtime (GitService.resetTo hash hard) (fun () -> OperationResult (Ok ())) (fun err -> OperationResult (Error err))
        | Revert hash ->
            model, ofFlow "revert" runtime (GitService.revert hash) (fun () -> OperationResult (Ok ())) (fun err -> OperationResult (Error err))
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
                let requestId, model = issueRequest model
                { model with StartupSelection = NoStartupSelection }, Cmd.ofMsg (SelectCommit(hash, requestId))
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

    /// <summary>A message's name for diagnostics breadcrumbs, without reflection. Fieldless cases share the union's
    /// type, so the ones worth telling apart are named here.</summary>
    let messageName (msg: Msg) =
        match msg with
        | RereadRefs -> "RereadRefs"
        | NoOp -> "NoOp"
        | WorkingTreeFilesChanged -> "WorkingTreeFilesChanged"
        | _ -> Diagnostics.messageTypeName (box msg)

    /// <summary>Restarts after this many watcher failures in a row stop; a watcher that cannot start at all (too many
    /// directories for inotify, an unreadable tree) gives up rather than retrying forever.</summary>
    let maxWatchFailures = 3

    /// <summary>The program's long-lived inputs, owned by Elmish: the search debounce and the working tree watcher. The
    /// debounce window and the watcher's restart count are part of their ids, so changing the window or restarting a
    /// failed watcher replaces the subscription.</summary>
    let subscriptions (runtime: Runtime) (searchInbox: Inbox<SearchInput>) (model: Model) : Sub<Msg> =
        let window = model.SearchDebounce
        let settled : FlowStream<GitService.GitEnv, Never, Msg> =
            let inputs = Inbox.stream searchInbox
            (if window > TimeSpan.Zero then inputs |> FlowStream.debounce window else inputs)
            |> FlowStream.map SearchSettled
        let search = Sub.OfFlowStream.subscribe [ "search-debounce"; string window.Ticks ] runtime settled Never.absurd

        let watch =
            match model.WorkingDirectory with
            | Some directory when not (String.IsNullOrEmpty model.RepoPath) && model.WatchFailures < maxWatchFailures ->
                Sub.OfFlowStream.subscribe
                    [ "working-tree-watch"; directory; string model.WatchRestarts ]
                    runtime
                    (WorkingTreeWatch.changes directory model.RepoPath |> FlowStream.map (fun () -> WorkingTreeFilesChanged))
                    (fun error -> WorkingTreeWatchFailed(error.ToString()))
            | _ -> Sub.none

        Sub.batch [ search; watch ]

    /// <summary>The main window's program, uninstrumented. The desktop host builds the instrumented one with
    /// <c>Host.mainProgram</c>.</summary>
    let program (runtime: Runtime) (flags: Flags) =
        let searchInbox = Inbox.create 64 runtime
        Program.mkProgram (fun () -> init runtime flags) (update runtime searchInbox) (fun _ _ -> ())
        |> Program.withSubscription (subscriptions runtime searchInbox)
