namespace GitKay.Core.AppInfrastructure

open System
open System.Threading

/// Owns the cancellation lifetime for one latest-wins operation.
type LatestJob() =
    let mutable current: (int64 * CancellationTokenSource) option = None

    member _.Start(requestId: int64) =
        current
        |> Option.iter (fun (_, cancellation) ->
            cancellation.Cancel()
            cancellation.Dispose())

        let cancellation = new CancellationTokenSource()
        current <- Some(requestId, cancellation)
        cancellation.Token

    member _.Complete(requestId: int64) =
        match current with
        | Some(currentId, cancellation) when currentId = requestId ->
            cancellation.Dispose()
            current <- None
        | _ -> ()

    member _.Cancel() =
        current
        |> Option.iter (fun (_, cancellation) ->
            cancellation.Cancel()
            cancellation.Dispose())
        current <- None
