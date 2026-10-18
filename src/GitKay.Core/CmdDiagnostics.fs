namespace GitKay.Core

open System
open Axial

/// A command whose flow ended in a typed failure or defect, with its rendered cause.
type FlowFailure = { At: DateTimeOffset; Name: string; Cause: string }

/// Diagnostics for every Axial-backed Cmd. The registry and failure handler live here so the C# diagnostics windows
/// can read them through one stable surface; `App` installs the running runtime's registry at startup.
[<Sealed; AbstractClass>]
type CmdDiagnostics private () =
    static let mutable registry = FiberRegistry(500)
    static let mutable onFailure: string -> string -> unit = fun _ _ -> ()

    static member internal SetRegistry(value: FiberRegistry) = registry <- value

    /// Live fibers of every running Cmd, including fibers they fork, over the settled history and totals.
    static member Registry = registry

    /// How many fibers have started since the registry was installed.
    static member StartedCount = registry.StartedCount

    /// Settled fibers, oldest first, up to the registry's history capacity.
    static member Settled() = registry.Settled() |> List.toArray

    /// Totals per fiber name, ordered by name.
    static member Stats() = registry.Stats() |> List.toArray

    /// The most recent defects nobody observed, oldest first.
    static member UnobservedDefects() = registry.UnobservedDefects() |> List.toArray

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

    static member internal ReportFailure(name: string, cause: string) =
        // The registry already records the fiber's rendered cause; this only taps the log handler.
        try onFailure name cause with _ -> ()

    static member OnFailure = onFailure

    /// Names of commands that are running now.
    static member RunningCommands() =
        registry.Snapshot()
        |> List.choose (fun dump -> dump.Annotations |> Map.tryFind "elmish.command")
        |> List.toArray

    /// Interrupts every live command fiber with this name; returns how many were signalled.
    static member Cancel(name: string) : int = registry.InterruptByName name

    /// Sets the failure handler from C#.
    static member SetFailureHandler(handler: System.Action<string, string>) =
        onFailure <- fun name cause -> handler.Invoke(name, cause)
