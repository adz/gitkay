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

    type Model =
        {
            Status: string
            StartupTargets: GitService.StartupTarget list
            Commits: Graph.CommitGraphInfo list
            SelectedCommitHash: string option
            SelectedDiffHash: string option
            SelectedDiff: Models.FileDiff list option
            SelectionStartedAtTicks: int64 option
        }

    type Msg =
        | RereadRefs
        | HistoryLoaded of Result<Models.Commit list, string>
        | SelectCommit of hash:string * startedAtTicks:int64
        | DiffLoaded of hash:string * startedAtTicks:int64 * Result<Models.FileDiff list, string>
        | CreateTag of hash:string * name:string
        | CreateBranch of hash:string * name:string
        | CherryPick of hash:string
        | ResetTo of hash:string * hard:bool
        | Revert of hash:string
        | OperationResult of Result<string, string>
        | NoOp

    let private loadHistory (targets: GitService.StartupTarget list) =
        Cmd.OfFunc.either GitService.fetchHistory targets HistoryLoaded (fun ex -> HistoryLoaded (Error ex.Message))

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

    let private logTiming (message: string) =
        let line = sprintf "[timing] %s" message
        Trace.WriteLine line
        try
            Console.Error.WriteLine line
        with _ ->
            ()

    let private warmDiffCacheFlow (hash: string) =
        flow {
            do! Flow.Runtime.ensureNotCanceled "Selection canceled."
            let! _ = GitService.fetchDiffSummary hash |> Flow.fromResult
            return ()
        }

    let private loadSelectedDiffFlow (hash: string) =
        flow {
            do! warmDiffCacheFlow hash
            do! Flow.Runtime.ensureNotCanceled "Selection canceled."
            let! diff = GitService.fetchDiff hash |> Flow.fromResult
            return diff
        }

    let private startSelectionLoad (hash: string) (startedAtTicks: int64) =
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
                    let! result = Flow.toAsyncResult () cancellation.Token (loadSelectedDiffFlow hash)

                    if not cancellation.IsCancellationRequested then
                        dispatch (DiffLoaded(hash, startedAtTicks, result))
                with
                | :? OperationCanceledException ->
                    ()
                | ex when not cancellation.IsCancellationRequested ->
                    dispatch (DiffLoaded(hash, startedAtTicks, Error ex.Message))
            }
            |> Async.Start)

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
            match selectedHash, model.SelectedDiffHash, model.SelectedDiff with
            | Some hash, Some diffHash, Some _ when hash = diffHash -> true
            | _ -> false

        let shouldLoadDiff = selectedHash.IsSome && not diffReadyForSelection
        let startedAtTicks = if shouldLoadDiff then Some(Stopwatch.GetTimestamp()) else None

        let nextModel =
            {
                model with
                    Status = sprintf "Loaded %d commits" commits.Length
                    Commits = commits
                    SelectedCommitHash = selectedHash
                    SelectedDiffHash = if diffReadyForSelection then model.SelectedDiffHash else None
                    SelectedDiff = if diffReadyForSelection then model.SelectedDiff else None
                    SelectionStartedAtTicks = startedAtTicks
            }

        let cmd =
            match selectedHash, startedAtTicks with
            | Some hash, Some startedAtTicks ->
                startSelectionLoad hash startedAtTicks
            | _ ->
                cancelCurrentSelectionJob ()
                Cmd.none

        nextModel, cmd

    let init (startupArgs: string array) : Model * Cmd<Msg> =
        match GitService.parseStartupTargets startupArgs with
        | Error err ->
            {
                Status = sprintf "Error: %s" err
                StartupTargets = []
                Commits = []
                SelectedCommitHash = None
                SelectedDiffHash = None
                SelectedDiff = None
                SelectionStartedAtTicks = None
            },
            Cmd.none
        | Ok startupTargets ->
            let model =
                {
                    Status = "Loading history..."
                    StartupTargets = startupTargets
                    Commits = []
                    SelectedCommitHash = None
                    SelectedDiffHash = None
                    SelectedDiff = None
                    SelectionStartedAtTicks = None
                }

            model, loadHistory startupTargets

    let update msg model : Model * Cmd<Msg> =
        match msg with
        | RereadRefs ->
            let nextModel = { model with Status = "Refreshing..." }
            nextModel, loadHistory model.StartupTargets
        | HistoryLoaded (Ok commits) ->
            let graphInfo = Graph.calculateLanes commits
            historyLoadSelection model graphInfo
        | HistoryLoaded (Error err) ->
            { model with Status = sprintf "Error: %s" err }, Cmd.none
        | SelectCommit (hash, startedAtTicks) ->
            cancelCurrentSelectionJob ()
            let nextModel =
                {
                    model with
                        SelectedCommitHash = Some hash
                        SelectionStartedAtTicks = Some startedAtTicks
                }

            let cmd = startSelectionLoad hash startedAtTicks
            nextModel, cmd
        | DiffLoaded (hash, startedAtTicks, Ok diff) ->
            match model.SelectedCommitHash, model.SelectionStartedAtTicks with
            | Some currentHash, Some currentStartedAtTicks when currentHash = hash && currentStartedAtTicks = startedAtTicks ->
                let elapsed = Stopwatch.GetElapsedTime(startedAtTicks)
                logTiming (sprintf "commit click -> diff ready hash=%s elapsed=%.1fms" hash elapsed.TotalMilliseconds)
            | _ ->
                ()

            if model.SelectedCommitHash = Some hash && model.SelectionStartedAtTicks = Some startedAtTicks then
                clearCurrentSelectionJob startedAtTicks
                { model with SelectedDiffHash = Some hash; SelectedDiff = Some diff; SelectionStartedAtTicks = None }, Cmd.none
            else
                model, Cmd.none
        | DiffLoaded (hash, startedAtTicks, Error err) ->
            match model.SelectedCommitHash, model.SelectionStartedAtTicks with
            | Some currentHash, Some currentStartedAtTicks when currentHash = hash && currentStartedAtTicks = startedAtTicks ->
                let elapsed = Stopwatch.GetElapsedTime(startedAtTicks)
                logTiming (sprintf "commit click -> diff error hash=%s elapsed=%.1fms error=%s" hash elapsed.TotalMilliseconds err)
            | _ ->
                ()

            if model.SelectedCommitHash = Some hash && model.SelectionStartedAtTicks = Some startedAtTicks then
                clearCurrentSelectionJob startedAtTicks
                { model with Status = sprintf "Diff Error: %s" err; SelectedDiffHash = None; SelectedDiff = None; SelectionStartedAtTicks = None }, Cmd.none
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
