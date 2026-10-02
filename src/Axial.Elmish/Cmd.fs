namespace Axial.Elmish

open System
open System.Threading
open System.Threading.Tasks
open Axial
open global.Elmish

/// A command whose flow ended in a typed failure or defect, with its rendered cause.
type FlowFailure = { At: DateTimeOffset; Name: string; Cause: string }

/// Diagnostics for every Axial-backed Cmd: each runs as a named fiber in one registry. The registry owns the live
/// fiber tree, the bounded history of settled fibers with their rendered failure causes, per-name totals,
/// unobserved defects and the started count, so nothing is bookkept twice here. A hang report dumps what is still
/// running, and the diagnostics window reads the registry directly.
[<Sealed; AbstractClass>]
type CmdDiagnostics private () =
    static let registry = FiberRegistry(500)
    static let mutable onFailure: string -> string -> unit = fun _ _ -> ()

    /// Live fibers of every running Cmd, including fibers they fork, over the settled history and totals.
    static member Registry = registry

    /// How many fibers have started since the registry was installed.
    static member StartedCount = registry.StartedCount

    /// Settled fibers, oldest first, up to the registry's history capacity.
    static member Settled() = registry.Settled() |> List.toArray

    /// Failed fibers with rendered causes, oldest first. Includes unobserved defects the registry recorded unless the
    /// same fiber already appears in the settled history.
    static member Failures() =
        let settled = registry.Settled()

        let recordedFailures =
            settled
            |> List.choose (fun fiber ->
                fiber.Failure
                |> Option.map (fun cause ->
                    { At = fiber.Fiber.SettledAt |> Option.defaultValue fiber.Fiber.StartedAt
                      Name = fiber.Fiber.Name |> Option.defaultValue "(unnamed)"
                      Cause = cause }))

        let recordedIds =
            settled
            |> List.filter (fun fiber -> fiber.Failure.IsSome)
            |> List.map (fun fiber -> fiber.Fiber.Id)
            |> Set.ofList

        let unobserved =
            registry.UnobservedDefects()
            |> List.collect (fun defect ->
                match defect.Fiber with
                | Some fiber when Set.contains fiber.Id recordedIds -> []
                | Some fiber ->
                    [ { At = fiber.SettledAt |> Option.defaultValue fiber.StartedAt
                        Name = fiber.Name |> Option.defaultValue "(unobserved)"
                        Cause = defect.Defect } ]
                | None ->
                    [ { At = DateTimeOffset.UtcNow // axial-allow-effect: clock
                        Name = "(unobserved)"
                        Cause = defect.Defect } ])

        List.toArray (recordedFailures @ unobserved)

    /// The most recent defects nobody observed, oldest first.
    static member UnobservedDefects() = registry.UnobservedDefects() |> List.toArray

    /// Totals per fiber name, ordered by name.
    static member Stats() = registry.Stats() |> List.toArray

    static member internal ReportFailure(name: string, cause: string) =
        // The registry already records the fiber's rendered cause; this only taps the log handler.
        try onFailure name cause with _ -> ()

    static member OnFailure = onFailure

    /// Names of commands that are running now.
    static member RunningCommands() =
        registry.Snapshot()
        |> List.choose (fun dump -> dump.Annotations |> Map.tryFind "gitkay.cmd")
        |> List.toArray

    /// Interrupts every live command fiber with this name; returns how many were signalled.
    static member Cancel(name: string) : int = registry.InterruptByName name

    /// Sets the failure handler from C#.
    static member SetFailureHandler(handler: System.Action<string, string>) =
        onFailure <- fun name cause -> handler.Invoke(name, cause)

/// Renders an error through its own ToString. `string error` on a generic value compiles to F#'s structured printer for
/// unions and records, which throws under NativeAOT; error types render themselves (see GitError).
module internal ErrorText =
    let render (error: obj) =
        match error with
        | null -> "null"
        | value -> value.ToString()

[<RequireQualifiedAccess>]
module Cmd =
    module OfFlow =
        let private renderError (error: 'error) = ErrorText.render (box error)

        let private dispatchOrNothing onSuccess onError =
            function
            | Exit.Success value -> Some(onSuccess value)
            | Exit.Failure(Cause.Fail error) -> Some(onError error)
            | Exit.Failure cause when Cause.isInterrupted cause -> None
            | Exit.Failure(Cause.Die error) -> raise error
            | Exit.Failure cause -> failwith (Cause.prettyPrint renderError cause)

        /// Forks the workflow as a named fiber tracked by the diagnostics registry, and joins it.
        let private instrument (name: string) (workflow: Flow<'env, 'error, 'value>) : Flow<'env, 'error, 'value> =
            flow {
                let! fiber = Flow.forkNamed name workflow
                return! Fiber.join fiber
            }
            |> Flow.annotate "gitkay.cmd" name
            |> Flow.withFiberRegistry CmdDiagnostics.Registry

        let private run (name: string) (token: CancellationToken) (env: 'env) (workflow: Flow<'env, 'error, 'value>) onSuccess onError : Cmd<'msg> =
            [ fun dispatch ->
                task {
                    // A linked source per command lets the runtime shut it down without touching its slot.
                    use cancellation = CancellationTokenSource.CreateLinkedTokenSource(token)
                    let token = cancellation.Token
                    // Start on the thread pool: commands are dispatched on the UI thread, and a flow's synchronous
                    // prefix (LibGit2Sharp walks, diffs) would otherwise run there and freeze the window.
                    let! exit = Task.Run<Exit<_, _>>(Func<Task<Exit<_, _>>>(fun () -> (instrument name workflow).StartAsValueTask(env, cancellationToken = token).AsTask()))

                    match exit with
                    | Exit.Failure cause when not (Cause.isInterrupted cause) ->
                        CmdDiagnostics.ReportFailure(name, Cause.prettyPrint renderError cause)
                    | _ -> ()

                    dispatchOrNothing onSuccess onError exit |> Option.iter dispatch
                }
                |> ignore ]

        /// Runs a named flow against a runtime's shared root lifetime. An interruption (e.g. runtime
        /// shutdown) dispatches nothing. The name identifies the flow in diagnostics.
        let ofFlow (name: string) (runtime: AxialElmishRuntime) (env: 'env) (workflow: Flow<'env, 'error, 'value>) onSuccess onError : Cmd<'msg> =
            run name runtime.Token env workflow onSuccess onError

        /// Runs a named flow as the latest occupant of a slot: starting a new one interrupts whatever
        /// the slot currently held. An interruption dispatches nothing.
        let ofFlowLatest (name: string) (slot: AxialLatestSlot) (env: 'env) (workflow: Flow<'env, 'error, 'value>) onSuccess onError : Cmd<'msg> =
            run name (slot.Start()) env workflow onSuccess onError
