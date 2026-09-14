// axial-allow-effect-file: clock
namespace GitKay.Core

open System
open System.Collections.Generic
open System.Diagnostics

/// In-memory breadcrumbs for crash and hang reports: the most recent Elmish messages and how long each update took.
module Diagnostics =

    type Breadcrumb = { At: DateTimeOffset; Message: string; ElapsedMs: float }

    let private capacity = 200
    let private gate = obj ()
    let private recent = Queue<Breadcrumb>(capacity)
    let mutable private inFlight: (string * int64) option = None

    /// Updates slower than this are written to the log as they happen.
    let slowUpdateThresholdMs = 50.0

    /// A union case's name from its runtime type, without reflection (trimming and NativeAOT safe): cases with fields
    /// compile to nested classes named after the case. Fieldless cases share the union's own type, so callers name
    /// those explicitly.
    let messageTypeName (message: obj) =
        match message with
        | null -> "null"
        | value -> value.GetType().Name

    let beginMessage (name: string) =
        lock gate (fun () -> inFlight <- Some(name, Stopwatch.GetTimestamp()))

    let endMessage (name: string) (startedAt: int64) =
        let elapsed = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds

        lock gate (fun () ->
            inFlight <- None
            if recent.Count >= capacity then recent.Dequeue() |> ignore
            recent.Enqueue { At = DateTimeOffset.Now; Message = name; ElapsedMs = elapsed })

        if elapsed >= slowUpdateThresholdMs then
            Trace.WriteLine $"[slow] update {name} took {elapsed:F0}ms"

    /// A message whose update is still running, with how long it has been running.
    let inFlightMessage () =
        lock gate (fun () -> inFlight |> Option.map (fun (name, started) -> name, Stopwatch.GetElapsedTime(started).TotalMilliseconds))

    let recentMessages () = lock gate (fun () -> List.ofSeq recent)

    /// Recent messages, newest last, formatted for a report.
    let describeRecent (count: int) =
        let lines =
            recentMessages ()
            |> List.rev
            |> List.truncate count
            |> List.rev
            |> List.map (fun crumb ->
                let at = crumb.At.ToString "HH:mm:ss.fff"
                $"  {at}  {crumb.Message}  ({crumb.ElapsedMs:F1}ms)")

        let running =
            match inFlightMessage () with
            | Some(name, ms) -> [ $"  still running: {name} for {ms:F0}ms" ]
            | None -> []

        String.Join(Environment.NewLine, lines @ running)
