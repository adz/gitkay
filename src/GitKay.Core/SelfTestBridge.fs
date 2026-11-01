namespace GitKay.Core

open System
open System.Threading
open System.Threading.Tasks

/// The blocking bridge for <c>gitkay --self-test</c>. The self-test runs at a process entry point, so it waits on real
/// commands and real time; that raw TPL coordination belongs here in a capability-role file, not in the self-test checks.
module SelfTestBridge =

    /// Runs one Elmish command and waits for its dispatch, or returns None when no message arrives in ten seconds.
    let runCommand (command: Elmish.Cmd<string>) : string option =
        let dispatched = TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously)
        for subscription in command do
            subscription (fun message -> dispatched.TrySetResult message |> ignore)
        if dispatched.Task.Wait(TimeSpan.FromSeconds 10.0) then Some dispatched.Task.Result else None

    /// Waits for a real interval while a command settles.
    let sleepMs (milliseconds: int) = Thread.Sleep milliseconds
