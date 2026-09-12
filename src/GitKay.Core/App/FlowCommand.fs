namespace GitKay.Core

open System.Threading
open System.Threading.Tasks
open Axial
open Elmish

module Cmd =
    module OfFlow =
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
