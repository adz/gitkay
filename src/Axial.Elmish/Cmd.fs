namespace Axial.Elmish

open System.Threading
open System.Threading.Tasks
open Axial
open global.Elmish

[<RequireQualifiedAccess>]
module Cmd =
    module OfFlow =
        let private dispatchOrNothing onSuccess onError =
            function
            | Exit.Success value -> Some(onSuccess value)
            | Exit.Failure(Cause.Fail error) -> Some(onError error)
            | Exit.Failure cause when Cause.isInterrupted cause -> None
            | Exit.Failure(Cause.Die error) -> raise error
            | Exit.Failure cause -> failwith (Cause.prettyPrint string cause)

        let private run (token: CancellationToken) (env: 'env) (workflow: Flow<'env, 'error, 'value>) onSuccess onError : Cmd<'msg> =
            [ fun dispatch ->
                task {
                    let! exit = workflow.StartAsValueTask(env, cancellationToken = token)
                    dispatchOrNothing onSuccess onError exit |> Option.iter dispatch
                }
                |> ignore ]

        /// Runs a flow against a runtime's shared root lifetime. An interruption (e.g. runtime
        /// shutdown) dispatches nothing.
        let ofFlow (runtime: AxialElmishRuntime) (env: 'env) (workflow: Flow<'env, 'error, 'value>) onSuccess onError : Cmd<'msg> =
            run runtime.Token env workflow onSuccess onError

        /// Runs a flow as the latest occupant of a slot: starting a new one interrupts whatever
        /// the slot currently held. An interruption dispatches nothing.
        let ofFlowLatest (slot: AxialLatestSlot) (env: 'env) (workflow: Flow<'env, 'error, 'value>) onSuccess onError : Cmd<'msg> =
            run (slot.Start()) env workflow onSuccess onError
