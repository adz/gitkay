namespace GitKay.Core

open Elmish
open System
open System.Diagnostics
open System.Threading
open FsFlow
open GitKay.Core.Models

module App =

    type private SelectionJob =
        {
            RequestId: int64
            Cancellation: CancellationTokenSource
        }

    let mutable private currentSelectionJob: SelectionJob option = None
    let mutable private currentDiffJob: SelectionJob option = None
    let mutable private currentSearchJob: SelectionJob option = None

    type Model =
        {
            Status: string
            StartupTargets: GitService.StartupTarget list
            ShowBranchRefs: bool
            ShowStashes: bool
            SearchQuery: string
            SearchScopeKey: string
            SearchResults: GitService.SearchResult list option
            Commits: Graph.CommitGraphInfo list
            SelectedCommitHash: string option
            SelectedDiffHash: string option
            SelectedDiffFiles: GitService.DiffFileSummary list option
            SelectedDiff: Models.FileDiff list option
            SelectedDiffFileKey: GitService.DiffFileKey option
            SelectionStartedAtTicks: int64 option
            SelectedDiffStartedAtTicks: int64 option
            SearchStartedAtTicks: int64 option
        }

    type Msg =
        | RereadRefs
        | SetShowBranchRefs of bool
        | SetShowStashes of bool
        | HistoryLoaded of Result<Models.Commit list, string>
        | SelectCommit of hash:string * startedAtTicks:int64
        | DiffFilesLoaded of hash:string * startedAtTicks:int64 * Result<GitService.DiffFileSummary list, string>
        | DiffLoaded of hash:string * startedAtTicks:int64 * Result<Models.FileDiff list, string>
        | SelectDiffFile of hash:string * oldPath:string * newPath:string
        | SetSearchQuery of string
        | SetSearchScope of string
        | RunSearch of query:string * scopeKey:string * startedAtTicks:int64
        | SearchResultsLoaded of query:string * scopeKey:string * startedAtTicks:int64 * Result<GitService.SearchResult list, string>
        | CreateTag of hash:string * name:string
        | CreateBranch of hash:string * name:string
        | CherryPick of hash:string
        | ResetTo of hash:string * hard:bool
        | Revert of hash:string
        | OperationResult of Result<string, string>
        | NoOp

    let private loadHistory (includeStashes: bool) (targets: GitService.StartupTarget list) =
        Cmd.OfFunc.either (GitService.fetchHistory includeStashes) targets HistoryLoaded (fun ex -> HistoryLoaded (Error ex.Message))

    let private clearCurrentSelectionJob requestId =
        match currentSelectionJob with
        | Some job when job.RequestId = requestId ->
            job.Cancellation.Dispose()
            currentSelectionJob <- None
        | _ ->
            ()

    let private cancelCurrentSelectionJob () =
        match currentSelectionJob with
        | Some job ->
            job.Cancellation.Cancel()
            job.Cancellation.Dispose()
            currentSelectionJob <- None
        | None ->
            ()

    let private clearCurrentSearchJob requestId =
        match currentSearchJob with
        | Some job when job.RequestId = requestId ->
            job.Cancellation.Dispose()
            currentSearchJob <- None
        | _ ->
            ()

    let private cancelCurrentSearchJob () =
        match currentSearchJob with
        | Some job ->
            job.Cancellation.Cancel()
            job.Cancellation.Dispose()
            currentSearchJob <- None
        | None ->
            ()

    let private logTiming (message: string) =
        let line = sprintf "[timing] %s" message
        Trace.WriteLine line
        try
            Console.Error.WriteLine line
        with _ ->
            ()

    let private loadDiffFilesFlow (hash: string) =
        flow {
            do! Flow.Runtime.ensureNotCanceled "Selection canceled."
            let! files = GitService.fetchDiffFileList hash |> Flow.fromResult
            return files
        }

    let private loadDiffFlow (hash: string) =
        flow {
            do! Flow.Runtime.ensureNotCanceled "Selection canceled."
            let! diff = GitService.fetchDiff hash |> Flow.fromResult
            return diff
        }

    let private loadSearchResultsFlow (commits: Graph.CommitGraphInfo list) (query: string) (scopeKey: string) =
        flow {
            do! Flow.Runtime.ensureNotCanceled "Search canceled."
            let scope = GitService.parseSearchScope scopeKey
            let commitList = commits |> List.map (fun info -> info.Commit)
            let! results = GitService.searchCommits commitList query scope |> Flow.fromResult
            return results
        }

    let private startDiffFilesLoad (hash: string) (startedAtTicks: int64) =
        cancelCurrentSelectionJob ()
        let cancellation = new CancellationTokenSource()
        currentSelectionJob <-
            Some
                {
                    RequestId = startedAtTicks
                    Cancellation = cancellation
                }

        Cmd.ofEffect (fun dispatch ->
            async {
                try
                    let! result = Flow.toAsyncResult () cancellation.Token (loadDiffFilesFlow hash)

                    if not cancellation.IsCancellationRequested then
                        dispatch (DiffFilesLoaded(hash, startedAtTicks, result))
                with
                | :? OperationCanceledException ->
                    ()
                | ex when not cancellation.IsCancellationRequested ->
                    dispatch (DiffFilesLoaded(hash, startedAtTicks, Error ex.Message))
            }
            |> Async.Start)

    let private clearCurrentDiffJob requestId =
        match currentDiffJob with
        | Some job when job.RequestId = requestId ->
            job.Cancellation.Dispose()
            currentDiffJob <- None
        | _ ->
            ()

    let private cancelCurrentDiffJob () =
        match currentDiffJob with
        | Some job ->
            job.Cancellation.Cancel()
            job.Cancellation.Dispose()
            currentDiffJob <- None
        | None ->
            ()

    let private startDiffLoad (hash: string) (startedAtTicks: int64) =
        cancelCurrentDiffJob ()
        let cancellation = new CancellationTokenSource()
        currentDiffJob <-
            Some
                {
                    RequestId = startedAtTicks
                    Cancellation = cancellation
                }

        Cmd.ofEffect (fun dispatch ->
            async {
                try
                    let! result = Flow.toAsyncResult () cancellation.Token (loadDiffFlow hash)

                    if not cancellation.IsCancellationRequested then
                        dispatch (DiffLoaded(hash, startedAtTicks, result))
                with
                | :? OperationCanceledException ->
                    ()
                | ex when not cancellation.IsCancellationRequested ->
                    dispatch (DiffLoaded(hash, startedAtTicks, Error ex.Message))
            }
            |> Async.Start)

    let private startSearchLoad (commits: Graph.CommitGraphInfo list) (query: string) (scopeKey: string) (startedAtTicks: int64) =
        cancelCurrentSearchJob ()
        let cancellation = new CancellationTokenSource()
        currentSearchJob <-
            Some
                {
                    RequestId = startedAtTicks
                    Cancellation = cancellation
                }

        Cmd.ofEffect (fun dispatch ->
            async {
                try
                    let! result = Flow.toAsyncResult () cancellation.Token (loadSearchResultsFlow commits query scopeKey)

                    if not cancellation.IsCancellationRequested then
                        dispatch (SearchResultsLoaded(query, scopeKey, startedAtTicks, result))
                with
                | :? OperationCanceledException ->
                    ()
                | ex when not cancellation.IsCancellationRequested ->
                    dispatch (SearchResultsLoaded(query, scopeKey, startedAtTicks, Error ex.Message))
            }
            |> Async.Start)

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

        let selectedHash =
            match model.SelectedCommitHash, selectionExistsInHistory, commits with
            | Some hash, true, _ -> Some hash
            | _, _, firstCommit :: _ -> Some firstCommit.Commit.Hash
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
                    Status = sprintf "Loaded %d commits" commits.Length
                    Commits = commits
                    SearchResults = None
                    SearchStartedAtTicks = None
                    SelectedCommitHash = selectedHash
                    SelectedDiffHash = if diffReadyForSelection then model.SelectedDiffHash else None
                    SelectedDiffFiles = if diffReadyForSelection then model.SelectedDiffFiles else None
                    SelectedDiff = if diffReadyForSelection then model.SelectedDiff else None
                    SelectedDiffFileKey = if diffReadyForSelection then model.SelectedDiffFileKey else None
                    SelectionStartedAtTicks = nextSelectionStartedAtTicks
                    SelectedDiffStartedAtTicks = nextDiffStartedAtTicks
            }

        let cmd =
            match selectedHash, nextSelectionStartedAtTicks with
            | Some hash, Some startedAtTicks ->
                Cmd.batch [ startDiffFilesLoad hash startedAtTicks; startDiffLoad hash startedAtTicks ]
            | _ ->
                cancelCurrentSelectionJob ()
                if not diffReadyForSelection then
                    cancelCurrentDiffJob ()
                Cmd.none

        nextModel, cmd

    let init (startupArgs: string array) : Model * Cmd<Msg> =
        match GitService.parseStartupTargets startupArgs with
        | Error err ->
            {
                Status = sprintf "Error: %s" err
                StartupTargets = []
                ShowBranchRefs = false
                ShowStashes = false
                SearchQuery = ""
                SearchScopeKey = "all"
                SearchResults = None
                Commits = []
                SelectedCommitHash = None
                SelectedDiffHash = None
                SelectedDiffFiles = None
                SelectedDiff = None
                SelectedDiffFileKey = None
                SelectionStartedAtTicks = None
                SelectedDiffStartedAtTicks = None
                SearchStartedAtTicks = None
            },
            Cmd.none
        | Ok startupTargets ->
            let model =
                {
                    Status = "Loading history..."
                    StartupTargets = startupTargets
                    ShowBranchRefs = false
                    ShowStashes = false
                    SearchQuery = ""
                    SearchScopeKey = "all"
                    SearchResults = None
                    Commits = []
                    SelectedCommitHash = None
                    SelectedDiffHash = None
                    SelectedDiffFiles = None
                    SelectedDiff = None
                    SelectedDiffFileKey = None
                    SelectionStartedAtTicks = None
                    SelectedDiffStartedAtTicks = None
                    SearchStartedAtTicks = None
                }

            model, loadHistory false startupTargets

    let update msg model : Model * Cmd<Msg> =
        match msg with
        | RereadRefs ->
            cancelCurrentSearchJob ()
            let nextModel = { model with Status = "Refreshing..." }
            nextModel, loadHistory model.ShowStashes model.StartupTargets
        | SetShowBranchRefs showBranchRefs ->
            { model with ShowBranchRefs = showBranchRefs }, Cmd.none
        | SetShowStashes showStashes ->
            cancelCurrentSearchJob ()
            let nextModel =
                {
                    model with
                        ShowStashes = showStashes
                        Status = "Refreshing..."
                }

            nextModel, loadHistory showStashes model.StartupTargets
        | HistoryLoaded (Ok commits) ->
            cancelCurrentSearchJob ()
            let graphInfo = Graph.calculateLanes commits
            historyLoadSelection model graphInfo
        | HistoryLoaded (Error err) ->
            cancelCurrentSearchJob ()
            { model with Status = sprintf "Error: %s" err; SearchResults = None; SearchStartedAtTicks = None }, Cmd.none
        | SelectCommit (hash, startedAtTicks) ->
            cancelCurrentSelectionJob ()
            cancelCurrentDiffJob ()
            let nextModel =
                {
                    model with
                        SelectedCommitHash = Some hash
                        SelectedDiffHash = None
                        SelectedDiffFiles = None
                        SelectedDiff = None
                        SelectedDiffFileKey = None
                        SelectionStartedAtTicks = Some startedAtTicks
                        SelectedDiffStartedAtTicks = Some startedAtTicks
                }

            let cmd = Cmd.batch [ startDiffFilesLoad hash startedAtTicks; startDiffLoad hash startedAtTicks ]
            nextModel, cmd
        | SetSearchQuery query ->
            let trimmed = query.Trim()

            if String.IsNullOrWhiteSpace trimmed then
                cancelCurrentSearchJob ()
                { model with SearchQuery = query; SearchResults = None; SearchStartedAtTicks = None }, Cmd.none
            else
                { model with SearchQuery = query }, Cmd.none
        | SetSearchScope scopeKey ->
            { model with SearchScopeKey = scopeKey }, Cmd.none
        | RunSearch (query, scopeKey, startedAtTicks) ->
            cancelCurrentSearchJob ()

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
                            Status = sprintf "Searching %s..." query
                    }

                let cmd = startSearchLoad model.Commits query scopeKey startedAtTicks
                nextModel, cmd
        | SearchResultsLoaded (query, scopeKey, startedAtTicks, Ok results) ->
            match model.SearchQuery, model.SearchScopeKey, model.SearchStartedAtTicks with
            | currentQuery, currentScopeKey, Some currentStartedAtTicks when currentQuery = query && currentScopeKey = scopeKey && currentStartedAtTicks = startedAtTicks ->
                clearCurrentSearchJob startedAtTicks
                { model with SearchResults = Some results; SearchStartedAtTicks = None; Status = sprintf "Search: %d hit(s) for \"%s\"" results.Length query }, Cmd.none
            | _ ->
                model, Cmd.none
        | SearchResultsLoaded (query, scopeKey, startedAtTicks, Error err) ->
            match model.SearchQuery, model.SearchScopeKey, model.SearchStartedAtTicks with
            | currentQuery, currentScopeKey, Some currentStartedAtTicks when currentQuery = query && currentScopeKey = scopeKey && currentStartedAtTicks = startedAtTicks ->
                clearCurrentSearchJob startedAtTicks
                { model with SearchResults = None; SearchStartedAtTicks = None; Status = sprintf "Search Error: %s" err }, Cmd.none
            | _ ->
                model, Cmd.none
        | DiffFilesLoaded (hash, startedAtTicks, Ok files) ->
            match model.SelectedCommitHash, model.SelectionStartedAtTicks with
            | Some currentHash, Some currentStartedAtTicks when currentHash = hash && currentStartedAtTicks = startedAtTicks ->
                let elapsed = Stopwatch.GetElapsedTime(startedAtTicks)
                logTiming (sprintf "commit click -> file list ready hash=%s elapsed=%.1fms files=%d" hash elapsed.TotalMilliseconds files.Length)
            | _ ->
                ()

            if model.SelectedCommitHash = Some hash && model.SelectionStartedAtTicks = Some startedAtTicks then
                clearCurrentSelectionJob startedAtTicks

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
                logTiming (sprintf "commit click -> file list error hash=%s elapsed=%.1fms error=%s" hash elapsed.TotalMilliseconds err)
            | _ ->
                ()

            if model.SelectedCommitHash = Some hash && model.SelectionStartedAtTicks = Some startedAtTicks then
                clearCurrentSelectionJob startedAtTicks
                { model with Status = sprintf "Diff Error: %s" err; SelectedDiffHash = None; SelectedDiffFiles = None; SelectedDiff = None; SelectedDiffFileKey = None; SelectionStartedAtTicks = None; SelectedDiffStartedAtTicks = None }, Cmd.none
            else
                model, Cmd.none
        | DiffLoaded (hash, startedAtTicks, Ok diff) ->
            match model.SelectedCommitHash, model.SelectedDiffStartedAtTicks with
            | Some currentCommitHash, Some currentStartedAtTicks
                when currentCommitHash = hash && currentStartedAtTicks = startedAtTicks ->
                let elapsed = Stopwatch.GetElapsedTime(startedAtTicks)
                logTiming (sprintf "commit click -> unified diff ready hash=%s elapsed=%.1fms files=%d" hash elapsed.TotalMilliseconds diff.Length)
            | _ ->
                ()

            if model.SelectedCommitHash = Some hash && model.SelectedDiffStartedAtTicks = Some startedAtTicks then
                clearCurrentDiffJob startedAtTicks
                { model with SelectedDiff = Some diff; SelectedDiffStartedAtTicks = None }, Cmd.none
            else
                model, Cmd.none
        | DiffLoaded (hash, startedAtTicks, Error err) ->
            match model.SelectedCommitHash, model.SelectedDiffStartedAtTicks with
            | Some currentCommitHash, Some currentStartedAtTicks
                when currentCommitHash = hash && currentStartedAtTicks = startedAtTicks ->
                let elapsed = Stopwatch.GetElapsedTime(startedAtTicks)
                logTiming (sprintf "commit click -> unified diff error hash=%s elapsed=%.1fms error=%s" hash elapsed.TotalMilliseconds err)
            | _ ->
                ()

            if model.SelectedCommitHash = Some hash && model.SelectedDiffStartedAtTicks = Some startedAtTicks then
                clearCurrentDiffJob startedAtTicks
                { model with Status = sprintf "Diff Error: %s" err; SelectedDiff = None; SelectedDiffStartedAtTicks = None }, Cmd.none
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
        | CreateTag (hash, name) ->
            model, Cmd.OfFunc.either (fun () -> GitService.createTag hash name) () OperationResult (fun ex -> OperationResult (Error ex.Message))
        | CreateBranch (hash, name) ->
            model, Cmd.OfFunc.either (fun () -> GitService.createBranch hash name) () OperationResult (fun ex -> OperationResult (Error ex.Message))
        | CherryPick hash ->
            model, Cmd.OfFunc.either (fun () -> GitService.cherryPick hash) () OperationResult (fun ex -> OperationResult (Error ex.Message))
        | ResetTo (hash, hard) ->
            model, Cmd.OfFunc.either (fun () -> GitService.resetTo hash hard) () OperationResult (fun ex -> OperationResult (Error ex.Message))
        | Revert hash ->
            model, Cmd.OfFunc.either (fun () -> GitService.revert hash) () OperationResult (fun ex -> OperationResult (Error ex.Message))
        | OperationResult (Ok _) ->
            model, Cmd.ofMsg RereadRefs
        | OperationResult (Error err) ->
            { model with Status = sprintf "Git Error: %s" err }, Cmd.none
        | NoOp ->
            model, Cmd.none

    let program (startupArgs: string array) =
        Program.mkProgram (fun () -> init startupArgs) update (fun _ _ -> ())
