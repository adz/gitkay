namespace GitKay.Core

open Elmish
open GitKay.Core.Models

module App =

    type Model =
        {
            Status: string
            Commits: Graph.CommitGraphInfo list
            SelectedHash: string option
            SelectedDiff: Models.FileDiff list option
        }

    type Msg =
        | RereadRefs
        | HistoryLoaded of Result<Models.Commit list, string>
        | SelectCommit of string
        | DiffLoaded of Result<Models.FileDiff list, string>
        | CreateTag of hash:string * name:string
        | CreateBranch of hash:string * name:string
        | CherryPick of hash:string
        | ResetTo of hash:string * hard:bool
        | Revert of hash:string
        | OperationResult of Result<string, string>
        | NoOp

    let init () : Model * Cmd<Msg> =
        let model = { Status = "Loading history..."; Commits = []; SelectedHash = None; SelectedDiff = None }
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
        | SelectCommit hash ->
            let nextModel = { model with SelectedHash = Some hash; SelectedDiff = None }
            let cmd = Cmd.OfFunc.either GitService.fetchDiff hash DiffLoaded (fun ex -> DiffLoaded (Error ex.Message))
            nextModel, cmd
        | DiffLoaded (Ok diff) ->
            { model with SelectedDiff = Some diff }, Cmd.none
        | DiffLoaded (Error err) ->
            { model with Status = sprintf "Diff Error: %s" err }, Cmd.none
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
