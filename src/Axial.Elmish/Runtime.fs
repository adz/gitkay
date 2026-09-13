namespace Axial.Elmish

open System
open System.Collections.Concurrent
open System.Threading

/// One root cancellation lifetime shared by every Axial-backed Cmd in a running Elmish
/// program. Disposing it cancels every outstanding flow, including ones held by a
/// `AxialLatestSlot`.
[<Sealed>]
type AxialElmishRuntime() =
    let cts = new CancellationTokenSource()

    /// The token every Axial-backed Cmd should observe. Cancelled when the runtime is disposed.
    member _.Token = cts.Token

    interface IDisposable with
        member _.Dispose() =
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
