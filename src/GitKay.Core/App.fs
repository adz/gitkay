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
    let mutable private currentFileSelectionJob: SelectionJob option = None

    type Model =
        {
            Status: string
            StartupTargets: GitService.StartupTarget list
            Commits: Graph.CommitGraphInfo list
            SelectedCommitHash: string option
            SelectedDiffHash: string option
            SelectedDiffFiles: GitService.DiffFileSummary list option
            SelectedDiffFileKey: GitService.DiffFileKey option
            SelectedDiffFile: Models.FileDiff option
            SelectionStartedAtTicks: int64 option
            SelectedDiffFileStartedAtTicks: int64 option
        }

    type Msg =
        | RereadRefs
        | HistoryLoaded of Result<Models.Commit list, string>
        | SelectCommit of hash:string * startedAtTicks:int64
        | DiffFilesLoaded of hash:string * startedAtTicks:int64 * Result<GitService.DiffFileSummary list, string>
        | SelectDiffFile of hash:string * oldPath:string * newPath:string * startedAtTicks:int64
        | DiffFileLoaded of hash:string * oldPath:string * newPath:string * startedAtTicks:int64 * Result<Models.FileDiff, string>
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

    let private clearCurrentFileSelectionJob requestId =
        match currentFileSelectionJob with
        | Some job when job.RequestId = requestId ->
            job.Cancellation.Dispose()
            currentFileSelectionJob <- None
        | _ ->
            ()

    let private cancelCurrentFileSelectionJob () =
        match currentFileSelectionJob with
        | Some job ->
            job.Cancellation.Cancel()
            job.Cancellation.Dispose()
            currentFileSelectionJob <- None
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

    let private loadSelectedDiffFileFlow (hash: string) (oldPath: string) (newPath: string) =
        flow {
            do! Flow.Runtime.ensureNotCanceled "Selection canceled."
            let! diff = GitService.fetchDiffFileContent hash oldPath newPath |> Flow.fromResult
            return diff
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

    let private startDiffFileLoad (hash: string) (oldPath: string) (newPath: string) (startedAtTicks: int64) =
        cancelCurrentFileSelectionJob ()
        let cancellation = new CancellationTokenSource()
        currentFileSelectionJob <-
            Some
                {
                    RequestId = startedAtTicks
                    Cancellation = cancellation
                }

        Cmd.ofEffect (fun dispatch ->
            async {
                try
                    let! result = Flow.toAsyncResult () cancellation.Token (loadSelectedDiffFileFlow hash oldPath newPath)

                    if not cancellation.IsCancellationRequested then
                        dispatch (DiffFileLoaded(hash, oldPath, newPath, startedAtTicks, result))
                with
                | :? OperationCanceledException ->
                    ()
                | ex when not cancellation.IsCancellationRequested ->
                    dispatch (DiffFileLoaded(hash, oldPath, newPath, startedAtTicks, Error ex.Message))
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
            | Some hash, Some diffHash, Some _ when hash = diffHash -> true
            | _ -> false

        let nextSelectionStartedAtTicks =
            if selectedHash.IsSome && not diffReadyForSelection then
                Some(Stopwatch.GetTimestamp())
            else
                None

        let selectedDiffFileStartedAtTicks =
            if diffReadyForSelection then model.SelectedDiffFileStartedAtTicks else None

        let nextModel =
            {
                model with
                    Status = sprintf "Loaded %d commits" commits.Length
                    Commits = commits
                    SelectedCommitHash = selectedHash
                    SelectedDiffHash = if diffReadyForSelection then model.SelectedDiffHash else None
                    SelectedDiffFiles = if diffReadyForSelection then model.SelectedDiffFiles else None
                    SelectedDiffFileKey = if diffReadyForSelection then model.SelectedDiffFileKey else None
                    SelectedDiffFile = if diffReadyForSelection then model.SelectedDiffFile else None
                    SelectionStartedAtTicks = nextSelectionStartedAtTicks
                    SelectedDiffFileStartedAtTicks = selectedDiffFileStartedAtTicks
            }

        let cmd =
            match selectedHash, nextSelectionStartedAtTicks with
            | Some hash, Some startedAtTicks ->
                startDiffFilesLoad hash startedAtTicks
            | _ ->
                cancelCurrentSelectionJob ()
                if not diffReadyForSelection then
                    cancelCurrentFileSelectionJob ()
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
                SelectedDiffFiles = None
                SelectedDiffFileKey = None
                SelectedDiffFile = None
                SelectionStartedAtTicks = None
                SelectedDiffFileStartedAtTicks = None
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
                    SelectedDiffFiles = None
                    SelectedDiffFileKey = None
                    SelectedDiffFile = None
                    SelectionStartedAtTicks = None
                    SelectedDiffFileStartedAtTicks = None
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
            cancelCurrentFileSelectionJob ()
            let nextModel =
                {
                    model with
                        SelectedCommitHash = Some hash
                        SelectedDiffHash = None
                        SelectedDiffFiles = None
                        SelectedDiffFileKey = None
                        SelectedDiffFile = None
                        SelectionStartedAtTicks = Some startedAtTicks
                        SelectedDiffFileStartedAtTicks = None
                }

            let cmd = startDiffFilesLoad hash startedAtTicks
            nextModel, cmd
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

                let fileStartedAtTicks = Stopwatch.GetTimestamp()
                let nextModel =
                    {
                        model with
                            SelectedDiffHash = Some hash
                            SelectedDiffFiles = Some files
                            SelectedDiffFileKey = selectedFileKey
                            SelectedDiffFile = None
                            SelectionStartedAtTicks = None
                            SelectedDiffFileStartedAtTicks = selectedFileKey |> Option.map (fun _ -> fileStartedAtTicks)
                    }

                let cmd =
                    match selectedFileSummary with
                    | Some file ->
                        startDiffFileLoad hash file.OldPath file.NewPath fileStartedAtTicks
                    | None ->
                        Cmd.none

                nextModel, cmd
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
                { model with Status = sprintf "Diff Error: %s" err; SelectedDiffHash = None; SelectedDiffFiles = None; SelectedDiffFileKey = None; SelectedDiffFile = None; SelectionStartedAtTicks = None; SelectedDiffFileStartedAtTicks = None }, Cmd.none
            else
                model, Cmd.none
        | SelectDiffFile (hash, oldPath, newPath, startedAtTicks) ->
            let key: GitService.DiffFileKey =
                {
                    OldPath = oldPath
                    NewPath = newPath
                }

            if model.SelectedCommitHash = Some hash && model.SelectedDiffHash = Some hash then
                let nextModel =
                    {
                        model with
                            SelectedDiffFileKey = Some key
                            SelectedDiffFile = None
                            SelectedDiffFileStartedAtTicks = Some startedAtTicks
                    }

                let cmd = startDiffFileLoad hash oldPath newPath startedAtTicks
                nextModel, cmd
            else
                model, Cmd.none
        | DiffFileLoaded (hash, oldPath, newPath, startedAtTicks, Ok file) ->
            match model.SelectedCommitHash, model.SelectedDiffHash, model.SelectedDiffFileStartedAtTicks with
            | Some currentCommitHash, Some currentDiffHash, Some currentStartedAtTicks
                when currentCommitHash = hash && currentDiffHash = hash && currentStartedAtTicks = startedAtTicks ->
                let elapsed = Stopwatch.GetElapsedTime(startedAtTicks)
                logTiming (sprintf "file click -> diff ready hash=%s path=%s elapsed=%.1fms" hash newPath elapsed.TotalMilliseconds)
            | _ ->
                ()

            if model.SelectedCommitHash = Some hash && model.SelectedDiffHash = Some hash && model.SelectedDiffFileStartedAtTicks = Some startedAtTicks then
                clearCurrentFileSelectionJob startedAtTicks
                { model with SelectedDiffFile = Some file; SelectedDiffFileStartedAtTicks = None }, Cmd.none
            else
                model, Cmd.none
        | DiffFileLoaded (hash, oldPath, newPath, startedAtTicks, Error err) ->
            match model.SelectedCommitHash, model.SelectedDiffHash, model.SelectedDiffFileStartedAtTicks with
            | Some currentCommitHash, Some currentDiffHash, Some currentStartedAtTicks
                when currentCommitHash = hash && currentDiffHash = hash && currentStartedAtTicks = startedAtTicks ->
                let elapsed = Stopwatch.GetElapsedTime(startedAtTicks)
                logTiming (sprintf "file click -> diff error hash=%s path=%s elapsed=%.1fms error=%s" hash newPath elapsed.TotalMilliseconds err)
            | _ ->
                ()

            if model.SelectedCommitHash = Some hash && model.SelectedDiffHash = Some hash && model.SelectedDiffFileStartedAtTicks = Some startedAtTicks then
                clearCurrentFileSelectionJob startedAtTicks
                { model with Status = sprintf "Diff Error: %s" err; SelectedDiffFile = None; SelectedDiffFileStartedAtTicks = None }, Cmd.none
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
