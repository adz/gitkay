namespace GitKay.Core

open Elmish
open System
open System.Diagnostics
open GitKay.Core.Models

module App =

    type Model =
        {
            Status: string
            Commits: Graph.CommitGraphInfo list
            SelectedHash: string option
            SelectedDiff: Models.FileDiff list option
            SelectionStartedAtTicks: int64 option
        }

    type Msg =
        | RereadRefs
        | HistoryLoaded of Result<Models.Commit list, string>
        | SelectCommit of hash:string * startedAtTicks:int64
        | DiffLoaded of hash:string * Result<Models.FileDiff list, string>
        | CreateTag of hash:string * name:string
        | CreateBranch of hash:string * name:string
        | CherryPick of hash:string
        | ResetTo of hash:string * hard:bool
        | Revert of hash:string
        | OperationResult of Result<string, string>
        | NoOp

    let private logTiming (message: string) =
        let line = sprintf "[timing] %s" message
        Trace.WriteLine line
        try
            Console.Error.WriteLine line
        with _ ->
            ()

    let init () : Model * Cmd<Msg> =
        let model =
            {
                Status = "Loading history..."
                Commits = []
                SelectedHash = None
                SelectedDiff = None
                SelectionStartedAtTicks = None
            }

        let cmd = Cmd.OfFunc.either GitService.fetchHistory () HistoryLoaded (fun ex -> HistoryLoaded (Error ex.Message))
        model, cmd

    let update msg model : Model * Cmd<Msg> =
        match msg with
        | RereadRefs ->
            let nextModel = { model with Status = "Refreshing..." }
            let cmd = Cmd.OfFunc.either GitService.fetchHistory () HistoryLoaded (fun ex -> HistoryLoaded (Error ex.Message))
            nextModel, cmd
        | HistoryLoaded (Ok commits) ->
            let graphInfo = Graph.calculateLanes commits
            { model with Status = sprintf "Loaded %d commits" commits.Length; Commits = graphInfo }, Cmd.none
        | HistoryLoaded (Error err) ->
            { model with Status = sprintf "Error: %s" err }, Cmd.none
        | SelectCommit (hash, startedAtTicks) ->
            let nextModel =
                {
                    model with
                        SelectedHash = Some hash
                        SelectedDiff = None
                        SelectionStartedAtTicks = Some startedAtTicks
                }

            let cmd =
                Cmd.OfFunc.either
                    GitService.fetchDiff
                    hash
                    (fun diff -> DiffLoaded(hash, diff))
                    (fun ex -> DiffLoaded(hash, Error ex.Message))

            nextModel, cmd
        | DiffLoaded (hash, Ok diff) ->
            match model.SelectedHash, model.SelectionStartedAtTicks with
            | Some currentHash, Some startedAtTicks when currentHash = hash ->
                let elapsed = Stopwatch.GetElapsedTime(startedAtTicks)
                logTiming (sprintf "commit click -> diff ready hash=%s elapsed=%.1fms" hash elapsed.TotalMilliseconds)
            | _ ->
                ()

            let nextSelectionStartedAtTicks =
                if model.SelectedHash = Some hash then
                    None
                else
                    model.SelectionStartedAtTicks

            { model with SelectedDiff = Some diff; SelectionStartedAtTicks = nextSelectionStartedAtTicks }, Cmd.none
        | DiffLoaded (hash, Error err) ->
            match model.SelectedHash, model.SelectionStartedAtTicks with
            | Some currentHash, Some startedAtTicks when currentHash = hash ->
                let elapsed = Stopwatch.GetElapsedTime(startedAtTicks)
                logTiming (sprintf "commit click -> diff error hash=%s elapsed=%.1fms error=%s" hash elapsed.TotalMilliseconds err)
            | _ ->
                ()

            let nextSelectionStartedAtTicks =
                if model.SelectedHash = Some hash then
                    None
                else
                    model.SelectionStartedAtTicks

            { model with Status = sprintf "Diff Error: %s" err; SelectionStartedAtTicks = nextSelectionStartedAtTicks }, Cmd.none
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

    let program =
        Program.mkProgram init update (fun _ _ -> ())
