namespace GitKay.Tests

open System
open System.IO
open System.Threading
open Xunit
open Swensen.Unquote
open Axial
open Axial.ElmishFlows
open Axial.FileSystem
open GitKay.Core

/// The program's commands and subscriptions run on Axial's test runtime: a manual clock drives the search batching,
/// the search debounce and the watcher's quiet period, and the test reads what they dispatched.
module ElmishTests =

    let private commit (hash: string) (subject: string) : Models.Commit =
        { Hash = hash
          Parents = []
          AuthorName = "Jane"
          AuthorEmail = "jane@example.com"
          Timestamp = 1710000000L
          Subject = subject
          Message = subject
          Refs = [] }

    let private result (commit: Models.Commit) : GitSearch.Result =
        { Commit = commit; MatchKinds = [ GitSearch.MessageMatch ]; MatchSummary = "message"; MatchedPaths = []; MatchedRefs = [] }

    let private waitFor (condition: unit -> bool) =
        let met = SpinWait.SpinUntil((fun () -> condition ()), 10000)
        test <@ met @>

    /// Runs a command and drains what it delivers until `count` messages arrived (or ten seconds pass).
    let private dispatchedAfter count (runtime: TestRuntime<'env>) =
        waitFor (fun () ->
            TestRuntime.drain runtime
            List.length (TestRuntime.dispatched runtime) >= count)
        TestRuntime.dispatched runtime

    [<Fact>]
    let ``a search reports progress and matches in batches, then its results`` () =
        let clock = ManualClock(DateTimeOffset.UnixEpoch)
        use runtime = ElmishRuntime.test clock (gitEnvironment "")
        let first = commit "1111111111111111111111111111111111111111" "first needle"
        let second = commit "2222222222222222222222222222222222222222" "second needle"
        let commits = Graph.calculateLanes [ first; second ]
        // Reports progress and a match, works for 300 ms, then reports the rest. The first batch closes 200 ms after
        // its first event, while the search is still working.
        let search : App.SearchRun =
            fun progress found ->
                flow {
                    progress 0 2
                    found (result second)
                    do! Flow.sleep (TimeSpan.FromMilliseconds 300.0)
                    progress 1 2
                    found (result first)
                    progress 2 2
                    return [ result first; result second ]
                }
        let command =
            Cmd.OfFlowStream.latest "search" runtime.Runtime (App.searchMessages search commits "needle" "commit" 42L) id
                (fun error -> App.Msg.SearchResultsLoaded("needle", "commit", 42L, Error error))
        TestRuntime.run command runtime

        // The search sleeps and the open batch waits out its window: two timers on the manual clock.
        waitFor (fun () -> clock.Sleepers >= 2)
        clock.AdvanceBy(App.searchReportWindow)
        let afterWindow = dispatchedAfter 1 runtime |> List.map unbox<App.Msg>
        test <@ afterWindow = [ App.Msg.SearchProgressed(42L, { Checked = 0; Total = 2; Matches = Some [ result second ] }) ] @>

        waitFor (fun () -> clock.Sleepers >= 1)
        clock.AdvanceBy(TimeSpan.FromMilliseconds 100.0)
        let all = dispatchedAfter 2 runtime |> List.map unbox<App.Msg>
        // The last batch holds the finish, so it reports only the results: no progress message for checked = total.
        test <@ List.last all = App.Msg.SearchResultsLoaded("needle", "commit", 42L, Ok [ result first; result second ]) @>
        test <@ all.Length = 2 @>
        test <@ TestRuntime.defects runtime = [] @>

    [<Fact>]
    let ``a failed search ends with an error result`` () =
        let clock = ManualClock(DateTimeOffset.UnixEpoch)
        use runtime = ElmishRuntime.test clock (gitEnvironment "")
        let search : App.SearchRun = fun _ _ -> Flow.fail GitError.EmptySearchQuery
        let command =
            Cmd.OfFlowStream.latest "search" runtime.Runtime (App.searchMessages search [] "" "commit" 7L) id
                (fun error -> App.Msg.SearchResultsLoaded("", "commit", 7L, Error error))
        TestRuntime.run command runtime
        let messages = dispatchedAfter 1 runtime |> List.map unbox<App.Msg>
        test <@ messages = [ App.Msg.SearchResultsLoaded("", "commit", 7L, Error GitError.EmptySearchQuery) ] @>

    [<Fact>]
    let ``progress batches show matches so far only for the current search`` () =
        let model0, _ = appInit [||]
        let running = { model0 with SearchQuery = "needle"; SearchStartedAtTicks = Some 42L }
        let found = result (commit "1111111111111111111111111111111111111111" "needle")
        let progressed, _ = appUpdate (App.Msg.SearchProgressed(42L, { Checked = 64; Total = 100; Matches = Some [ found ] })) running
        test <@ progressed.SearchResults = Some [ found ] && progressed.SearchProgress = Some(64, 100) @>
        test <@ progressed.Status = "Searching needle... 64 of 100 commits, 1 matches so far (Esc to cancel)" @>
        let stale, _ = appUpdate (App.Msg.SearchProgressed(7L, { Checked = 99; Total = 100; Matches = Some [ found ] })) running
        test <@ stale = running @>

    [<Fact>]
    let ``a queued search runs only when its generation is still current`` () =
        let model0, _ = appInit [||]
        let queued, _ = appUpdate (App.Msg.QueueSearch("nee", "commit")) model0
        let requeued, _ = appUpdate (App.Msg.QueueSearch("needle", "commit")) queued
        test <@ requeued.SearchGeneration = model0.SearchGeneration + 2L @>

        // The first input settling (it could not, behind a debounce, but a late one can) is stale.
        let stale, _ = appUpdate (App.Msg.SearchSettled { Query = "nee"; ScopeKey = "commit"; Generation = queued.SearchGeneration }) requeued
        test <@ stale = requeued @>

        let settled, _ = appUpdate (App.Msg.SearchSettled { Query = "needle"; ScopeKey = "commit"; Generation = requeued.SearchGeneration }) requeued
        test <@ settled.SearchQuery = "needle" && settled.Status = "Searching needle..." && settled.SearchStartedAtTicks.IsSome @>
        // update issues its own request ids below zero, so they cannot equal one the UI took from its clock.
        test <@ settled.SearchStartedAtTicks.Value < 0L @>

        // An explicit search retires a queued one that is still waiting.
        let waiting, _ = appUpdate (App.Msg.QueueSearch("other", "commit")) model0
        let explicit, _ = appUpdate (App.Msg.RunSearch("needle", "commit", 5L)) waiting
        let late, _ = appUpdate (App.Msg.SearchSettled { Query = "other"; ScopeKey = "commit"; Generation = waiting.SearchGeneration }) explicit
        test <@ late = explicit @>

    [<Fact>]
    let ``the search debounce waits out its window on the runtime clock`` () =
        let clock = ManualClock(DateTimeOffset.UnixEpoch)
        use runtime = ElmishRuntime.test clock (gitEnvironment "")
        let inbox = Inbox.create 64 runtime.Runtime
        let model0, _ = App.init runtime.Runtime (App.flags "" [||])
        let model = { model0 with SearchDebounce = TimeSpan.FromMilliseconds 500.0 }
        let queued, offer = App.update runtime.Runtime inbox (App.Msg.QueueSearch("needle", "commit")) model
        use _subscription = TestRuntime.subscribe (App.subscriptions runtime.Runtime inbox queued) runtime |> List.head
        TestRuntime.run offer runtime

        waitFor (fun () -> clock.Sleepers >= 1)
        clock.AdvanceBy(TimeSpan.FromMilliseconds 499.0)
        Thread.Sleep 50
        TestRuntime.drain runtime
        test <@ TestRuntime.dispatched runtime = [] @>

        clock.AdvanceBy(TimeSpan.FromMilliseconds 1.0)
        let messages = dispatchedAfter 1 runtime |> List.map unbox<App.Msg>
        test <@ messages = [ App.Msg.SearchSettled { Query = "needle"; ScopeKey = "commit"; Generation = queued.SearchGeneration } ] @>

    [<Fact>]
    let ``the debounce window and watcher restarts are part of the subscription ids`` () =
        let model0, _ = appInit [||]
        let watched = { model0 with RepoPath = "/repo/.git"; WorkingDirectory = Some "/repo" }
        let inbox = Inbox.create 8 (commandRuntime ())
        let ids model = App.subscriptions (commandRuntime ()) inbox model |> List.map fst
        let half = ids { watched with SearchDebounce = TimeSpan.FromSeconds 0.5 }
        let whole = ids { watched with SearchDebounce = TimeSpan.FromSeconds 1.0 }
        test <@ half.Length = 2 && half.Head <> whole.Head && half.[1] = whole.[1] @>

        let failed, _ = appUpdate (App.Msg.WorkingTreeWatchFailed "overflow") watched
        test <@ (ids failed).[1] <> half.[1] @>
        // A watcher that keeps failing is given up on; a change seen resets the count.
        let rec failTimes n model = if n = 0 then model else failTimes (n - 1) (fst (appUpdate (App.Msg.WorkingTreeWatchFailed "no") model))
        let givenUp = failTimes App.maxWatchFailures watched
        test <@ (ids givenUp).Length = 1 @>
        let recovered, _ = appUpdate App.Msg.WorkingTreeFilesChanged (failTimes 1 watched)
        test <@ recovered.WatchFailures = 0 @>

    type private WatchEnv =
        { Files: IFileSystem }
        interface IHasFileSystem with member this.FileSystem = this.Files

    [<Fact>]
    let ``the working tree watch reports edits after its quiet period, index changes, and ignores other git files`` () =
        let root = Path.Combine(Path.GetTempPath(), "gitkay-watch-" + Guid.NewGuid().ToString("N"))
        let gitDir = Path.Combine(root, ".git")
        let files = FileSystem.test ()
        let clock = ManualClock(DateTimeOffset.UnixEpoch)
        use runtime = ElmishRuntime.test clock { Files = files }
        let changes = WorkingTreeWatch.changes root gitDir |> FlowStream.map (fun () -> "changed")
        use _subscription =
            TestRuntime.subscribe (Sub.OfFlowStream.subscribe [ "watch" ] runtime.Runtime changes (fun error -> error.ToString())) runtime
            |> List.head
        // The git directory is inside the working tree, so one recursive watcher covers both.
        waitFor (fun () -> files.ActiveWatchers = 1)
        let change path = files.RaiseChange { Kind = FileSystemChangeKind.Changed; Path = path; OldPath = None }

        change (Path.Combine(gitDir, "objects", "ab"))
        change (Path.Combine(gitDir, "refs", "heads", "main"))
        Thread.Sleep 50
        test <@ clock.Sleepers = 0 @>

        for i in 1..5 do change (Path.Combine(root, $"file{i}.txt"))
        waitFor (fun () -> clock.Sleepers >= 1)
        clock.AdvanceBy WorkingTreeWatch.quietPeriod
        let afterEdits = dispatchedAfter 1 runtime |> List.length
        test <@ afterEdits >= 1 @>

        change (Path.Combine(gitDir, "index"))
        waitFor (fun () -> clock.Sleepers >= 1)
        clock.AdvanceBy WorkingTreeWatch.quietPeriod
        waitFor (fun () ->
            TestRuntime.drain runtime
            List.length (TestRuntime.dispatched runtime) > afterEdits)
        test <@ TestRuntime.dispatched runtime |> List.forall (fun message -> unbox<string> message = "changed") @>

    [<Fact>]
    let ``a git directory outside the working tree is watched as well`` () =
        let root = Path.Combine(Path.GetTempPath(), "gitkay-watch-" + Guid.NewGuid().ToString("N"))
        let gitDir = Path.Combine(Path.GetTempPath(), "gitkay-gitdir-" + Guid.NewGuid().ToString("N"))
        let files = FileSystem.test ()
        use runtime = ElmishRuntime.test (ManualClock(DateTimeOffset.UnixEpoch)) { Files = files }
        let changes = WorkingTreeWatch.changes root gitDir |> FlowStream.map (fun () -> "changed")
        use _subscription =
            TestRuntime.subscribe (Sub.OfFlowStream.subscribe [ "watch" ] runtime.Runtime changes (fun error -> error.ToString())) runtime
            |> List.head
        waitFor (fun () -> files.ActiveWatchers = 2)

    [<Fact>]
    let ``a watcher that fails dispatches its failure`` () =
        let files = new TestFileSystem(watchFailure = IOException "inotify watch limit reached")
        use runtime = ElmishRuntime.test (ManualClock(DateTimeOffset.UnixEpoch)) { Files = files }
        let changes = WorkingTreeWatch.changes "/repo" "/repo/.git" |> FlowStream.map (fun () -> "changed")
        use _subscription =
            TestRuntime.subscribe (Sub.OfFlowStream.subscribe [ "watch" ] runtime.Runtime changes (fun _ -> "failed")) runtime
            |> List.head
        let messages = dispatchedAfter 1 runtime |> List.map unbox<string>
        test <@ messages = [ "failed" ] @>

/// External programs are launched through the process service: the test service records each launch.
module LaunchTests =

    open Axial.Process

    let private launched (work: Flow<ProcessHostEnvironment, ProcessError, unit>) =
        let host = ProcessHostEnvironment.test ()
        let exit = work |> Flow.run host.Environment
        exit, List.ofSeq host.Process.Launches

    [<Fact>]
    let ``links open only when they are http or https`` () =
        let opened, launches = launched (Launch.openLink "https://example.com/a b")
        test <@ opened = Exit.Success () @>
        match launches with
        | [ ProcessLaunch.ShellOpen target ] -> test <@ target = "https://example.com/a%20b" @>
        | other -> failwithf "expected one shell open, got %A" other
        let refused, nothing = launched (Launch.openLink "file:///etc/passwd")
        test <@ List.isEmpty nothing @>
        match refused with
        | Exit.Failure(Cause.Fail(ProcessError.ShellOpenRejected rejection)) -> test <@ rejection.Target = "file:///etc/passwd" @>
        | other -> failwithf "expected a refusal, got %O" other

    [<Fact>]
    let ``VS Code gets each argument as one, and the app bundle on macOS without the CLI`` () =
        let path = "/repo/a & b.txt"
        let _, direct = launched (Launch.openInVsCode Launch.OtherPlatform (Some "/usr/bin/code") [ "/repo"; "--goto"; path ] "/repo")
        match direct with
        | [ ProcessLaunch.Command command ] ->
            test <@ Process.fileName command = "/usr/bin/code" && Process.arguments command = [ "/repo"; "--goto"; path ] @>
        | other -> failwithf "expected one launch, got %A" other
        let _, bundle = launched (Launch.openInVsCode Launch.MacOS None [ "/repo" ] "/repo")
        match bundle with
        | [ ProcessLaunch.Command command ] ->
            test <@ Process.fileName command = "open" && Process.arguments command = [ "-b"; "com.microsoft.VSCode"; "--args"; "/repo" ] @>
        | other -> failwithf "expected one launch, got %A" other

    [<Fact>]
    let ``a GitKay started under the dotnet host gets its entry assembly first`` () =
        let _, launches = launched (Launch.startGitKay "/usr/bin/dotnet" (Some "/app/gitkay.dll") "/work" [ "browse"; "/work" ])
        match launches with
        | [ ProcessLaunch.Command command ] ->
            test <@ Process.arguments command = [ "/app/gitkay.dll"; "browse"; "/work" ] @>
        | other -> failwithf "expected one launch, got %A" other

    [<Fact>]
    let ``a launch that cannot start is a typed failure`` () =
        let host = ProcessHostEnvironment.test ()
        let notInstalled = ProcessError.StartFailed { Command = "code"; Message = "not installed" }
        host.Process.LaunchHandler <- Some(fun _ -> Flow.fail notInstalled)
        let exit = Launch.openInVsCode Launch.OtherPlatform None [ "/repo" ] "/repo" |> Flow.run host.Environment
        test <@ exit = Exit.Failure(Cause.Fail notInstalled) @>

/// The settings and UI state files go through the environment's file system.
module StoreTests =

    open GitKay.Serialization

    [<Fact>]
    let ``a missing settings file reads as defaults and a corrupt one says why it was ignored`` () =
        let files = FileSystem.test ()
        let env : StoreEnv = { Files = files }
        let path = Path.Combine(Path.GetTempPath(), "gitkay-store-" + Guid.NewGuid().ToString("N"), "settings.json")
        match SettingsFile.load path |> Flow.run env with
        | Exit.Success loaded -> test <@ loaded = { Value = Settings.defaults; Failure = None } @>
        | other -> failwithf "%O" other
        (files :> IFileSystem).WriteAllText(path, "{ not json")
        match SettingsFile.load path |> Flow.run env with
        | Exit.Success loaded -> test <@ loaded.Value = Settings.defaults && loaded.Failure.IsSome && loaded.Failure.Value.Path = path @>
        | other -> failwithf "%O" other

    [<Fact>]
    let ``saved UI state reads back, and a save that cannot write is ignored`` () =
        let files = FileSystem.test ()
        let env : StoreEnv = { Files = files }
        let path = Path.Combine(Path.GetTempPath(), "gitkay-store-" + Guid.NewGuid().ToString("N"), "ui-state.json")
        let state = UiState.withWindowSize (Some 800.0) (Some 600.0) UiState.empty
        test <@ UiStateFile.save path state |> Flow.run env = Exit.Success () @>
        match UiStateFile.load path |> Flow.run env with
        | Exit.Success loaded -> test <@ loaded.Value = UiState.normalize state && loaded.Failure = None @>
        | other -> failwithf "%O" other
        let unwritable : StoreEnv = { Files = FileSystem.test () }
        test <@ UiStateFile.save "" state |> Flow.run unwritable = Exit.Success () @>
