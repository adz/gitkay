// The self-test runs at a process entry point and waits on real commands and time.
// axial-allow-effect-file: sleep
namespace GitKay.Core

open System
open System.Threading
open System.Threading.Tasks
open Axial
open Axial.ElmishFlows

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
        // axial-allow-effect: blocking (self-test entry point)
        if dispatched.Task.Wait(TimeSpan.FromSeconds 10.0) then Some dispatched.Task.Result else None

    let run (repoPath: string) : Check list =
        use env = GitService.environment repoPath
        let runtime : ElmishRuntime<GitService.GitEnv> =
            ElmishRuntime.start
                { Post = (fun callback -> callback ())
                  OnDefect = (fun _ -> ())
                  HistoryCapacity = 500 }
                env
        use _ = runtime

        // The shipped self-test reads diagnostics through CmdDiagnostics after the F# checks above have run; point it
        // at this local runtime's registry so those reads see the commands exercised here.
        CmdDiagnostics.SetRegistry runtime.Registry

        let runCancel name =
            for subscription in (Cmd.OfFlow.Latest.cancel name runtime : Elmish.Cmd<string>) do
                subscription ignore

        [ check "vim keys interpret and render actions" (fun () ->
              let context: Vim.KeyContext =
                  { Pane = Vim.VimPane.Diff; LineText = "call(first.second, third)"; Caret = 11; OtherSideText = null
                    Side = 0; HasSelection = false; HalfPageRows = 10; PageRows = 20 }
              let press symbol : Vim.KeyStroke = { Name = "?"; Symbol = symbol; Control = false; Shift = false; Alt = false }
              let mutable state = Vim.initial
              let rendered = Collections.Generic.List<string>()
              for symbol in [ "2"; "y"; "a"; "(" ] do
                  let struct (next, actions, _) = Vim.step state context (press symbol)
                  state <- next
                  for action in actions do rendered.Add(action.ToString())
              expect (String.Join("; ", rendered)) "CopyRange 4..25; SetCaret 4")
          check "GitError renders without reflection" (fun () ->
              expect (GitError.CommitNotFound("abc").ToString()) "Commit not found: abc")

          check "message names without reflection" (fun () ->
              expect (Diagnostics.messageTypeName (box (App.Msg.SetShowStashes true))) "SetShowStashes")

          check "command success is instrumented" (fun () ->
              let outcome = runCommand (Cmd.OfFlow.either "self-test success" runtime (Flow.succeed 42) (fun value -> $"ok {value}") (fun error -> error.ToString()))
              match outcome with
              | Some "ok 42" -> None
              | other -> Some("dispatched " + (other |> Option.defaultValue "nothing")))

          check "command failure is rendered" (fun () ->
              let failing: Flow<GitService.GitEnv, GitError, int> = Flow.fail (GitError.RevisionNotFound "nope")
              match runCommand (Cmd.OfFlow.either "self-test failure" runtime failing (fun _ -> "ok") (fun error -> error.ToString())) with
              | Some "Revision not found: nope" ->
                  runtime.Registry.Settled()
                  |> List.tryFind (fun fiber -> fiber.Fiber.Name = Some "self-test failure")
                  |> function
                      | Some fiber when (fiber.Failure |> Option.defaultValue "").Contains "Revision not found: nope" -> None
                      | Some fiber -> Some("failure cause " + (fiber.Failure |> Option.defaultValue ""))
                      | None -> Some "failure not recorded"
              | other -> Some("dispatched " + (other |> Option.defaultValue "nothing")))

          check "command defect is recorded" (fun () ->
              let dying: Flow<GitService.GitEnv, GitError, int> = Flow.delay (fun () -> raise (InvalidOperationException "self-test defect")) // axial-allow-raise
              runCommand (Cmd.OfFlow.Latest.either "self-test defect" runtime dying (fun _ -> "ok") (fun _ -> "error")) |> ignore
              Thread.Sleep 200
              if runtime.Registry.Settled() |> List.exists (fun fiber -> fiber.Fiber.Name = Some "self-test defect") then None
              else Some "defect fiber not recorded")

          check "command cancellation settles" (fun () ->
              let slow: Flow<GitService.GitEnv, GitError, int> = Flow.sleep (TimeSpan.FromSeconds 30.0) |> Flow.map (fun () -> 1)
              for subscription in Cmd.OfFlow.Latest.either "self-test cancelled" runtime slow (fun _ -> "ok") (fun _ -> "error") do
                  subscription ignore
              Thread.Sleep 200
              runCancel "self-test cancelled"
              Thread.Sleep 500
              let dump = runtime.Registry.DumpAt(env.Runtime.Clock)
              if dump.Contains "self-test cancelled" then Some("still running: " + dump) else None)

          check "command cancel from diagnostics" (fun () ->
              let slow: Flow<GitService.GitEnv, GitError, int> = Flow.sleep (TimeSpan.FromSeconds 30.0) |> Flow.map (fun () -> 1)
              for subscription in Cmd.OfFlow.either "self-test diagnostics cancel" runtime slow (fun _ -> "ok") (fun _ -> "error") do
                  subscription ignore
              Thread.Sleep 300
              let cancelled = runtime.Registry.InterruptByName "self-test diagnostics cancel"
              Thread.Sleep 500
              if cancelled <> 1 then Some $"cancelled {cancelled} commands"
              elif runtime.Registry.Snapshot() |> List.exists (fun dump -> dump.Name = Some "self-test diagnostics cancel") then Some "still running"
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
