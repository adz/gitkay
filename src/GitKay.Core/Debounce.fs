namespace GitKay.Core

open System
open Axial
open Axial.PlatformService

/// A quiet-period debounce over Axial streams. Values are offered from any thread; a host flow keeps only the one
/// that stays quiet for the window and hands it to the sink. The window can be replaced without stopping the host:
/// the consumer flow is rebuilt against the same input queue, so values still queued survive a settings change,
/// but the one value the old consumer was holding for the quiet window is dropped.
///
/// Built on FlowStream.debounce, so the quiet period is measured by the supplied IClock and everything about the
/// debounce is testable against a ManualClock.
[<Sealed>]
type Debounce<'value>(clock: IClock, initialQuiet: TimeSpan, sink: Action<'value>) =
    let environment = ClockEnvironment(clock)

    let inputs: Queue<'value> =
        match Flow.run environment (Queue.unbounded ()) |> Exit.toResult with
        | Ok queue -> queue
        | Error never -> Never.absurd never

    let windows: Queue<TimeSpan> =
        match Flow.run environment (Queue.unbounded ()) |> Exit.toResult with
        | Ok queue -> queue
        | Error never -> Never.absurd never

    let mutable quiet = initialQuiet
    let mutable sink = sink

    let consume (window: TimeSpan) =
        FlowStream.fromDequeue inputs
        |> FlowStream.debounce window
        |> FlowStream.runForEach (fun value -> sink.Invoke value)

    // The host keeps running while only the consumer is rebuilt, so a new window takes effect immediately. Values
    // still queued survive the change; only the one value the interrupted consumer was holding for the quiet window
    // is dropped.
    let rec supervise (window: TimeSpan) : Flow<ClockEnvironment, Never, unit> =
        flow {
            let! consumer = Flow.fork (consume window)
            let! next = Dequeue.take windows
            let! _ = Fiber.interrupt consumer
            return! supervise next
        }

    /// Offers a value; only the last one before a quiet window is delivered.
    member _.Offer(value: 'value) = Queue.tryOffer value inputs |> ignore

    /// Replaces the sink that receives debounced values.
    member _.SetSink(action: Action<'value>) = sink <- action

    /// Replaces the quiet window. An in-flight debounce is abandoned; later values use the new window.
    member _.SetWindow(window: TimeSpan) =
        quiet <- window
        Queue.tryOffer window windows |> ignore

    /// The host flow. Run it for the application's lifetime with <c>AxialElmishRuntime.Host</c>.
    member _.Run: Flow<ClockEnvironment, Never, unit> = supervise quiet

/// The application's stream-backed debounces.
[<RequireQualifiedAccess>]
module Debounces =

    /// One search input: the text typed, the scope it should run under, and the generation it belongs to so an
    /// immediate search can retire a debounced one that is still pending.
    type SearchInput = { Query: string; ScopeKey: string; Generation: int64 }

    /// A search debounce owned by one window, on the live clock. Each window owns its stream, so a second window (or
    /// a test) never replaces another's sink.
    let createSearch () =
        Debounce<SearchInput>(Clock.live, TimeSpan.FromSeconds 0.5, Action<SearchInput>(fun _ -> ()))

    /// Runs a window's debounce for the lifetime of the application runtime.
    let startHost (runtime: Axial.Elmish.AxialElmishRuntime) (debounce: Debounce<'value>) : IDisposable =
        runtime.Host(ClockEnvironment(Clock.live), debounce.Run)

    /// The working-tree watcher's quiet period: a build or checkout that touches many files refreshes once. There is
    /// one working tree, so one shared debounce.
    let watch =
        Debounce<unit>(Clock.live, TimeSpan.FromMilliseconds 300.0, Action<unit>(fun () -> ()))

    /// Reports a working-tree or index change to the watcher debounce.
    let watchChanged () = watch.Offer(())

    /// Sets the action run once working-tree events have been quiet for the window.
    let setWatchSink (sink: Action) =
        watch.SetSink(Action<unit>(fun () -> sink.Invoke()))

    /// Starts the shared working-tree watcher debounce for the lifetime of the application runtime.
    let startWatchHost (runtime: Axial.Elmish.AxialElmishRuntime) : IDisposable = startHost runtime watch
