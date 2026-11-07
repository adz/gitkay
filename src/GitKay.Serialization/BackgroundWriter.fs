namespace GitKay.Serialization

/// <summary>The one OS-thread bridge the stores need: a writer that takes UI state saves off the UI thread. It is raw
/// TPL coordination around a synchronous write, so it lives in this capability file.</summary>
/// Serializes writes and coalesces the ones that arrive while a write is in flight, so a burst of saves (one per
/// selected commit) never blocks the caller and never interleaves on the file. The newest value always wins.
[<RequireQualifiedAccess>]
module internal BackgroundWriter =
    let private writeGate = obj ()
    let private pendingGate = obj ()
    let private waiting = System.Collections.Generic.List<System.Threading.Tasks.TaskCompletionSource>()
    let mutable private pending: (unit -> unit) option = None
    let mutable private writerRunning = false

    let private writeWhilePending () =
        let mutable finished = false

        while not finished do
            let next =
                lock pendingGate (fun () ->
                    match pending with
                    | None ->
                        writerRunning <- false
                        finished <- true
                        None
                    | Some write ->
                        pending <- None
                        Some(write, waiting.ToArray()))

            match next with
            | None -> ()
            | Some(write, completions) ->
                lock writeGate write
                for completion in completions do
                    completion.TrySetResult() |> ignore

    /// Queues a write and returns a task that completes once the file holds it (or a newer queued value supersedes it).
    let enqueue (write: unit -> unit) : System.Threading.Tasks.Task =
        let completion = System.Threading.Tasks.TaskCompletionSource()

        let startWriter =
            lock pendingGate (fun () ->
                pending <- Some write
                waiting.Add completion

                if writerRunning then
                    false
                else
                    writerRunning <- true
                    true)

        // The loop runs until the queue drains, so the writer is never discarded mid-write.
        if startWriter then System.Threading.Tasks.Task.Run(writeWhilePending) |> ignore

        completion.Task
