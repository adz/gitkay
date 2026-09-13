namespace GitKay.Core

open System.Threading
open System.Threading.Tasks
open Axial
open Elmish
open GitKay.Core.AppInfrastructure

module Cmd =
    module OfFlow =
        let private dispatchExit onSuccess onError onCancel =
            function
            | Exit.Success value -> onSuccess value
            | Exit.Failure(Cause.Fail error) -> onError error
            | Exit.Failure cause when Cause.isInterrupted cause -> onCancel ()
            | Exit.Failure(Cause.Die error) -> raise error
            | Exit.Failure cause -> failwith (Cause.prettyPrint string cause)

        /// Runs a flow against a shared `AxialElmishRuntime`'s root lifetime. A shutdown
        /// interruption dispatches nothing.
        let viaRuntime (runtime: AxialElmishRuntime) (env: 'env) (workflow: Flow<'env, 'error, 'value>) onSuccess onError : Cmd<'msg> =
            [ fun dispatch ->
                task {
                    let! exit = workflow.StartAsValueTask(env, cancellationToken = runtime.Token)
                    dispatchExit (onSuccess >> Some) (onError >> Some) (fun () -> None) exit |> Option.iter dispatch
                }
                |> ignore ]

        /// Runs a flow as the latest occupant of an `AxialLatestSlot`: starting a new one
        /// interrupts whatever the slot currently held.
        let latest (slot: AxialLatestSlot) (env: 'env) (workflow: Flow<'env, 'error, 'value>) onSuccess onError onCancel =
            let token = slot.Start()

            Cmd.OfValueTask.perform
                (fun () -> workflow.StartAsValueTask(env, cancellationToken = token))
                ()
                (dispatchExit onSuccess onError onCancel)

        let either (env: 'env) (workflow: Flow<'env, 'error, 'value>) onSuccess onError =
            Cmd.OfValueTask.perform
                (fun () ->
                    task {
                        let! exit = Flow.startTask env workflow
                        return Exit.toResult exit
                    }
                    |> ValueTask<Result<'value, 'error>>)
                ()
                (function
                 | Ok value -> onSuccess value
                 | Error error -> onError error)

        let eitherWithCancellation
            (env: 'env)
            (cancellationToken: CancellationToken)
            (workflow: Flow<'env, 'error, 'value>)
            onSuccess
            onError
            onCancel
            =
            Cmd.OfValueTask.perform
                (fun () -> workflow.StartAsValueTask(env, cancellationToken = cancellationToken))
                ()
                (function
                 | Exit.Success value -> onSuccess value
                 | Exit.Failure(Cause.Fail error) -> onError error
                 | Exit.Failure cause when Cause.isInterrupted cause -> onCancel ()
                 | Exit.Failure(Cause.Die error) -> raise error
                 | Exit.Failure cause -> failwith (Cause.prettyPrint string cause))
