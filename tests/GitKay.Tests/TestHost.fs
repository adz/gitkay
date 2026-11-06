namespace GitKay.Tests

open Axial.ElmishFlows
open GitKay.Core

/// The app's runtime and programs as the tests use them. A test builds a repository environment with the same layer
/// the app starts its runtime with; the runtime is left running, as the old per-repository cache scopes were.
[<AutoOpen>]
module TestHost =

    /// A repository environment from the app's layer, with live services.
    let gitEnvironment (root: string) : GitService.GitEnv =
        (Host.startDetachedRuntime Host.liveServices root).Environment

    let private sharedRuntime = lazy (Host.startDetachedRuntime Host.liveServices "")
    let private sharedInbox = lazy (Inbox.create 64 sharedRuntime.Value)

    /// A runtime for tests that only build commands from update and never run them.
    let commandRuntime () : App.Runtime = sharedRuntime.Value

    /// App.init as the host calls it, for the repository the test process runs in.
    let appInit (startupArgs: string array) =
        App.init (commandRuntime ()) (App.flags (GitService.tryDiscoverRepositoryPath ()) startupArgs)

    /// App.update on the shared runtime and search inbox.
    let appUpdate (msg: App.Msg) (model: App.Model) = App.update (commandRuntime ()) sharedInbox.Value msg model

    /// CommitWindow.update on the shared runtime.
    let commitUpdate (msg: CommitWindow.Msg) (model: CommitWindow.Model) = CommitWindow.update (commandRuntime ()) msg model
