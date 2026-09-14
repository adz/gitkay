namespace Axial.Elmish

open System
open System.Threading
open System.Threading.Tasks
open Axial
open global.Elmish

/// A fiber that settled: a command's root fiber or one it forked.
type SettledFiber =
    { Id: int64
      Name: string
      ParentId: int64 option
      Annotations: Map<string, string>
      StartedAt: DateTimeOffset
      SettledAt: DateTimeOffset
      Status: FiberStatus
      /// A defect (exception) the fiber died with, if any.
      Defect: string option }

    member this.Duration = this.SettledAt - this.StartedAt

/// Per-name totals across every settled fiber since start.
type FlowStats =
    { Name: string
      Count: int
      Failed: int
      Interrupted: int
      TotalMs: float
      MaxMs: float }

/// A command whose flow ended in a typed failure or defect, with its rendered cause.
type FlowFailure = { At: DateTimeOffset; Name: string; Cause: string }

/// Diagnostics for every Axial-backed Cmd: each runs as a named fiber in one registry, so a hang report can dump
/// what is still running, and failures are rendered with Cause.prettyPrint. A fiber observer keeps a bounded history
/// of settled fibers and per-name totals for the diagnostics window.
[<Sealed; AbstractClass>]
type CmdDiagnostics private () =
    static let registry = FiberRegistry()
    static let gate = obj ()
    static let settled = System.Collections.Generic.Queue<SettledFiber>()
    static let failures = System.Collections.Generic.Queue<FlowFailure>()
    static let stats = System.Collections.Generic.Dictionary<string, FlowStats>()
    static let mutable started = 0L
    static let mutable onFailure: string -> string -> unit = fun _ _ -> ()
    static let capacity = 500

    static let record (metadata: FiberMetadata) (defect: exn option) =
        let settledAt = metadata.SettledAt |> Option.defaultValue DateTimeOffset.UtcNow // axial-allow-effect: clock
        let name = metadata.Name |> Option.defaultValue $"fiber #{metadata.Id.Value}"
        let fiber =
            { Id = metadata.Id.Value
              Name = name
              ParentId = metadata.ParentId |> Option.map _.Value
              Annotations = metadata.Annotations
              StartedAt = metadata.StartedAt
              SettledAt = settledAt
              Status = metadata.Status
              Defect = defect |> Option.map string }

        lock gate (fun () ->
            if settled.Count >= capacity then settled.Dequeue() |> ignore
            settled.Enqueue fiber
            let ms = fiber.Duration.TotalMilliseconds
            let previous =
                match stats.TryGetValue name with
                | true, value -> value
                | _ -> { Name = name; Count = 0; Failed = 0; Interrupted = 0; TotalMs = 0.0; MaxMs = 0.0 }
            stats[name] <-
                { previous with
                    Count = previous.Count + 1
                    Failed = previous.Failed + (if fiber.Status = FiberStatus.Failed then 1 else 0)
                    Interrupted = previous.Interrupted + (if fiber.Status = FiberStatus.Interrupted then 1 else 0)
                    TotalMs = previous.TotalMs + ms
                    MaxMs = max previous.MaxMs ms })

    static let observer =
        { FiberObserver.none with
            OnStart = fun _ -> System.Threading.Interlocked.Increment(&started) |> ignore
            OnEnd = fun metadata defect -> record metadata defect
            OnUnobservedDefect =
                fun metadata defect ->
                    lock gate (fun () ->
                        if failures.Count >= capacity then failures.Dequeue() |> ignore
                        let name = metadata |> Option.bind _.Name |> Option.defaultValue "unobserved"
                        failures.Enqueue { At = DateTimeOffset.UtcNow; Name = name; Cause = "Unobserved defect: " + string defect }) } // axial-allow-effect: clock

    /// Live fibers of every running Cmd, including fibers they fork.
    static member Registry = registry

    /// Observes every Cmd fiber's lifecycle for the settled history and totals.
    static member Observer = observer

    static member StartedCount = System.Threading.Interlocked.Read(&started)

    /// Settled fibers, oldest first (bounded).
    static member Settled() = lock gate (fun () -> Array.ofSeq settled)

    /// Command failures with rendered causes, oldest first (bounded).
    static member Failures() = lock gate (fun () -> Array.ofSeq failures)

    /// Totals per fiber name.
    static member Stats() = lock gate (fun () -> Array.ofSeq stats.Values)

    static member internal ReportFailure(name: string, cause: string) =
        lock gate (fun () ->
            if failures.Count >= capacity then failures.Dequeue() |> ignore
            failures.Enqueue { At = DateTimeOffset.UtcNow; Name = name; Cause = cause }) // axial-allow-effect: clock
        try onFailure name cause with _ -> ()

    static member OnFailure = onFailure

    /// Sets the failure handler from C#.
    static member SetFailureHandler(handler: System.Action<string, string>) =
        onFailure <- fun name cause -> handler.Invoke(name, cause)

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

        /// Forks the workflow as a named fiber tracked by the diagnostics registry, and joins it.
        let private instrument (name: string) (workflow: Flow<'env, 'error, 'value>) : Flow<'env, 'error, 'value> =
            flow {
                let! fiber = Flow.forkNamed name workflow
                return! Flow.join fiber
            }
            |> Flow.annotate "gitkay.cmd" name
            |> Flow.addFiberObserver CmdDiagnostics.Observer
            |> Flow.withFiberRegistry CmdDiagnostics.Registry

        let private run (name: string) (token: CancellationToken) (env: 'env) (workflow: Flow<'env, 'error, 'value>) onSuccess onError : Cmd<'msg> =
            [ fun dispatch ->
                task {
                    // Start on the thread pool: commands are dispatched on the UI thread, and a flow's synchronous
                    // prefix (LibGit2Sharp walks, diffs) would otherwise run there and freeze the window.
                    let! exit = Task.Run<Exit<_, _>>(Func<Task<Exit<_, _>>>(fun () -> (instrument name workflow).StartAsValueTask(env, cancellationToken = token).AsTask()))

                    match exit with
                    | Exit.Failure cause when not (Cause.isInterrupted cause) ->
                        CmdDiagnostics.ReportFailure(name, Cause.prettyPrint string cause)
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
