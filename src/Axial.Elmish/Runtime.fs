namespace Axial.Elmish

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Threading
open Axial

/// One root cancellation lifetime shared by every Axial-backed Cmd in a running Elmish
/// program. Disposing it cancels every outstanding flow, including ones held by a
/// `AxialLatestSlot` and any long-lived hosts.
[<Sealed>]
type AxialElmishRuntime() =
    let cts = new CancellationTokenSource()
    let hosts = ResizeArray<IDisposable>()

    /// The token every Axial-backed Cmd should observe. Cancelled when the runtime is disposed.
    member _.Token = cts.Token

    /// Starts a flow that runs for the lifetime of this runtime and is interrupted when it is disposed. Use it for
    /// long-lived stream consumers — a debounce, a watcher — whose lifetime is the application's, not one command's.
    /// The flow runs on the thread pool; its failures are not surfaced here.
    member _.Host(environment: 'env, flow: Flow<'env, 'error, unit>) : IDisposable =
        let handle = App.startWithCancellation cts.Token environment flow
        let host = handle :> IDisposable
        lock hosts (fun () -> hosts.Add host)

        { new IDisposable with
            member _.Dispose() =
                lock hosts (fun () -> hosts.Remove host |> ignore)
                host.Dispose() }

    interface IDisposable with
        member _.Dispose() =
            let outstanding = lock hosts (fun () -> let copy = hosts |> Seq.toArray in hosts.Clear(); copy)
            for host in outstanding do
                host.Dispose()

            if not cts.IsCancellationRequested then
                cts.Cancel()

            cts.Dispose()

/// Keeps only the most recently started flow alive. Tokens are linked to the owning
/// runtime, so an app-level shutdown cancels whatever the slot currently holds.
[<Sealed>]
type AxialLatestSlot(runtime: AxialElmishRuntime) =
    let mutable current: CancellationTokenSource option = None

    /// Cancels whatever this slot currently holds and returns the token for a new occupant.
    member _.Start() : CancellationToken =
        current
        |> Option.iter (fun cancellation ->
            cancellation.Cancel()
            cancellation.Dispose())

        let linked = CancellationTokenSource.CreateLinkedTokenSource(runtime.Token)
        current <- Some linked
        linked.Token

    /// Cancels whatever this slot currently holds without starting a replacement.
    member _.Cancel() =
        current
        |> Option.iter (fun cancellation ->
            cancellation.Cancel()
            cancellation.Dispose())

        current <- None

/// One `AxialLatestSlot` per key, created on first use. For latest-wins effects scoped to an
/// application key (e.g. one slot per selected file) rather than to the whole program.
[<Sealed>]
type AxialLatestSlotRegistry<'key when 'key: equality>(runtime: AxialElmishRuntime) =
    let slots = ConcurrentDictionary<'key, AxialLatestSlot>()

    /// The slot for this key, creating it on first use.
    member _.Item
        with get (key: 'key) : AxialLatestSlot = slots.GetOrAdd(key, fun _ -> AxialLatestSlot(runtime))

    /// Cancels every slot and forgets it, so a later lookup with the same key starts fresh.
    member _.CancelAll() =
        for slot in slots.Values do
            slot.Cancel()

        slots.Clear()
