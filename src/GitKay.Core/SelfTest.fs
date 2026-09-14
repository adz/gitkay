// The self-test runs at a process entry point and waits on real commands and time.
// axial-allow-effect-file: sleep
// axial-allow-effect-file: clock
namespace GitKay.Core

open System
open System.Threading
open System.Threading.Tasks
open Axial
open Axial.Elmish

/// Core half of `gitkay --self-test`: exercises paths that break under NativeAOT (rendering F# types, Axial command
/// instrumentation, LibGit2Sharp reads) in the shipped binary, where unit tests running on the JIT can't see them.
module SelfTest =

    type Check = { Name: string; Passed: bool; Detail: string }

    let private check name (run: unit -> string option) =
        try
            match run () with
            | None -> { Name = name; Passed = true; Detail = "" }
            | Some problem -> { Name = name; Passed = false; Detail = problem }
        with error ->
            { Name = name; Passed = false; Detail = error.GetType().FullName + ": " + error.Message }

    let private expect (actual: string) (expected: string) =
        if actual = expected then None else Some("expected <" + expected + "> got <" + actual + ">")

    /// Runs one command the way the Elmish program does and waits for its dispatch (or settles without one).
    let private runCommand (command: Elmish.Cmd<string>) =
        let dispatched = TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously)
        for subscription in command do
            subscription (fun message -> dispatched.TrySetResult message |> ignore)
        if dispatched.Task.Wait(TimeSpan.FromSeconds 10.0) then Some dispatched.Task.Result else None

    let run (repoPath: string) : Check list =
        use runtime = new AxialElmishRuntime()
        let env = GitService.environment repoPath

        [ check "GitError renders without reflection" (fun () ->
              expect (GitError.CommitNotFound("abc").ToString()) "Commit not found: abc")

          check "message names without reflection" (fun () ->
              expect (Diagnostics.messageTypeName (box (App.Msg.SetShowStashes true))) "SetShowStashes")

          check "command success is instrumented" (fun () ->
              let outcome = runCommand (Cmd.OfFlow.ofFlow "self-test success" runtime env (Flow.succeed 42) (fun value -> $"ok {value}") (fun error -> error.ToString()))
              match outcome with
              | Some "ok 42" -> None
              | other -> Some("dispatched " + (other |> Option.defaultValue "nothing")))

          check "command failure is rendered" (fun () ->
              let failing: Flow<GitService.GitEnv, GitError, int> = Flow.fail (GitError.RevisionNotFound "nope")
              match runCommand (Cmd.OfFlow.ofFlow "self-test failure" runtime env failing (fun _ -> "ok") (fun error -> error.ToString())) with
              | Some "Revision not found: nope" ->
                  CmdDiagnostics.Failures()
                  |> Array.tryFind (fun failure -> failure.Name = "self-test failure")
                  |> function
                      | Some failure when failure.Cause.Contains "Revision not found: nope" -> None
                      | Some failure -> Some("failure cause " + failure.Cause)
                      | None -> Some "failure not recorded"
              | other -> Some("dispatched " + (other |> Option.defaultValue "nothing")))

          check "command defect is recorded" (fun () ->
              let dying: Flow<GitService.GitEnv, GitError, int> = Flow.delay (fun () -> raise (InvalidOperationException "self-test defect")) // axial-allow-raise
              runCommand (Cmd.OfFlow.ofFlowLatest "self-test defect" (AxialLatestSlot runtime) env dying (fun _ -> "ok") (fun _ -> "error")) |> ignore
              Thread.Sleep 200
              if CmdDiagnostics.Settled() |> Array.exists (fun fiber -> fiber.Name = "self-test defect") then None
              else Some "defect fiber not recorded")

          check "command cancellation settles" (fun () ->
              let slot = AxialLatestSlot runtime
              let slow: Flow<GitService.GitEnv, GitError, int> = Flow.Runtime.sleep (TimeSpan.FromSeconds 30.0) |> Flow.map (fun () -> 1)
              for subscription in Cmd.OfFlow.ofFlowLatest "self-test cancelled" slot env slow (fun _ -> "ok") (fun _ -> "error") do
                  subscription ignore
              Thread.Sleep 200
              slot.Cancel()
              Thread.Sleep 500
              let dump = CmdDiagnostics.Registry.DumpAt(DateTimeOffset.UtcNow)
              if dump.Contains "self-test cancelled" then Some("still running: " + dump) else None)

          check "command cancel from diagnostics" (fun () ->
              let slow: Flow<GitService.GitEnv, GitError, int> = Flow.Runtime.sleep (TimeSpan.FromSeconds 30.0) |> Flow.map (fun () -> 1)
              for subscription in Cmd.OfFlow.ofFlow "self-test diagnostics cancel" runtime env slow (fun _ -> "ok") (fun _ -> "error") do
                  subscription ignore
              Thread.Sleep 300
              let cancelled = CmdDiagnostics.Cancel "self-test diagnostics cancel"
              Thread.Sleep 500
              if cancelled <> 1 then Some $"cancelled {cancelled} commands"
              elif CmdDiagnostics.RunningCommands() |> Array.contains "self-test diagnostics cancel" then Some "still running"
              else None)

          check "history, search and whole file load" (fun () ->
              match Flow.run env (GitService.fetchHistory (Some 50) false []) |> Exit.toResult with
              | Error error -> Some(GitError.describe error)
              | Ok [] -> Some "no commits"
              | Ok commits ->
                  match Flow.run env (GitService.searchCommits 3 commits GitSearch.Commit false "e") |> Exit.toResult with
                  | Error error -> Some(GitError.describe error)
                  | Ok _ ->
                      match GitService.listCommitFiles repoPath commits.Head.Hash with
                      | Error error -> Some(GitError.describe error)
                      | Ok [] -> None
                      | Ok (path :: _) ->
                          match GitService.loadWholeFile repoPath commits.Head.Hash path path with
                          | Ok _ -> None
                          | Error error -> Some(GitError.describe error)) ]
