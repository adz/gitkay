namespace GitKay.Core

open System
open System.Diagnostics
open System.IO
open System.Net.Http
open Elmish
open Axial
open Axial.Console
open Axial.ElmishFlows
open Axial.FileSystem
open Axial.HttpClient
open Axial.Process

/// <summary>
/// The desktop host boundary: the live services, the Elmish runtime's options, the one blocking start, the flags read
/// from the process before a program starts, and the update instrumentation that feeds crash and hang reports. The
/// programs themselves (`App`, `CommitWindow`) receive the runtime and flags and stay pure.
/// </summary>
module Host =

    // One client for every remote image request, owned by the process.
    let private sharedHttpClient = new HttpClient()

    /// <summary>The live services a repository environment is built from.</summary>
    let liveServices : GitService.GitServices =
        { Process = Process.live FileSystem.live Console.live
          Http = Http.live sharedHttpClient
          FileSystem = FileSystem.live }

    /// <summary>Runtime log entries (the [timing] lines) go to System.Diagnostics.Trace, where --log and the diagnostics
    /// log listen. Debug entries are dropped.</summary>
    let private traceSink : ILogSink =
        { new ILogSink with
            member _.IsEnabled level = level >= LogLevel.Information
            member _.Write entry = Trace.WriteLine entry.Message }

    let private options (post: (unit -> unit) -> unit) : ElmishRuntimeOptions =
        { Post = post
          OnDefect = fun defect -> CmdDiagnostics.ReportFailure(defect.Name, defect.Rendered)
          HistoryCapacity = 500
          Clock = None
          LogSink = Some traceSink }

    /// <summary>Starts the runtime for a repository: the layer builds its environment (services and caches) inside
    /// the runtime's root scope, so stopping the runtime releases them. Provisioning only allocates caches and never
    /// waits for the UI dispatcher, so blocking on it here is safe.</summary>
    let private start (post: (unit -> unit) -> unit) (services: GitService.GitServices) (repoPath: string) : App.Runtime =
       
        match ElmishRuntime.startLayer (options post) (GitService.layer services repoPath) () |> Async.RunSynchronously with
        | Ok runtime -> runtime
        | Error never -> Never.absurd never

    /// <summary>Starts the app's runtime, delivering command results through <paramref name="post"/> (the UI
    /// dispatcher's post, which runs inline when already on the UI thread). Stop it once, at exit.</summary>
    let startRepositoryRuntime (post: Action<Action>) (repoPath: string) : App.Runtime =
        let runtime = start (fun callback -> post.Invoke(Action callback)) liveServices repoPath
        // The diagnostics windows and hang reports read the app runtime's fibers.
        CmdDiagnostics.SetRegistry runtime.Registry
        runtime

    /// <summary>A runtime that delivers results on whichever thread completes them: for the self-test, benchmarks and
    /// tests, which have no UI dispatcher.</summary>
    let startDetachedRuntime (services: GitService.GitServices) (repoPath: string) : App.Runtime =
        start (fun callback -> callback ()) services repoPath

    /// <summary>Stops a runtime and waits, bounded, for its commands, services and caches to finish. Called once, at
    /// exit; the finalizers do not need the UI dispatcher, so waiting on the UI thread is safe.</summary>
    let stop (runtime: App.Runtime) (timeout: TimeSpan) =
        if not (runtime.StopAndWait timeout) then
            Trace.WriteLine("[runtime] did not stop within " + timeout.TotalSeconds.ToString("F1") + "s at exit")

    /// <summary>Reads what the main program needs from the process: the command line with path arguments resolved
    /// against the launch directory, and the repository's working tree.</summary>
    let flags (repoPath: string) (startupArgs: string array) (searchDebounce: TimeSpan) : App.Flags =
        let repoPath = if isNull repoPath then "" else repoPath
        let launchDirectory = Environment.CurrentDirectory
        let workingDirectory =
            if String.IsNullOrEmpty repoPath then None
            else
                match GitService.workingDirectory repoPath with
                | null -> None
                | directory when Directory.Exists directory -> Some directory
                | _ -> None
        { RepoPath = repoPath
          WorkingDirectory = workingDirectory
          StartupOptions =
            GitStartup.parseStartupOptions startupArgs
            |> Result.map (fun options ->
                let pathExists full = File.Exists full || Directory.Exists full
                { options with StartupTargets = GitService.resolvePathArguments pathExists repoPath launchDirectory options.StartupTargets })
          SearchDebounce = searchDebounce }

    /// <summary>Records each message and how long its update took, for crash and hang reports. The records are the
    /// host's: the update it wraps is pure.</summary>
    let private instrument (name: 'msg -> string) (update: 'msg -> 'model -> 'model * Cmd<'msg>) =
        fun msg model ->
            let label = name msg
            let startedAt = Timing.mark ()
            Diagnostics.beginMessage label
            try
                update msg model
            finally
                Diagnostics.endMessage label startedAt

    /// <summary>The main window's program, instrumented for diagnostics.</summary>
    let mainProgram (runtime: App.Runtime) (flags: App.Flags) =
        let searchInbox = Inbox.create 64 runtime
        Program.mkProgram (fun () -> App.init runtime flags) (instrument App.messageName (App.update runtime searchInbox)) (fun _ _ -> ())
        |> Program.withSubscription (App.subscriptions runtime searchInbox)

/// <summary>
/// Runs a Core flow on behalf of the C# UI, on the app's runtime, in place of <c>Task.Run</c>: the flow is a named
/// command fiber the diagnostics window lists, stopping the runtime interrupts it, its failure is a typed
/// <c>Result</c>, and the result arrives through the runtime's Post, on the UI thread.
/// </summary>
module UiFlow =

    /// <summary>Runs <paramref name="work"/> and hands its result to <paramref name="onResult"/>.</summary>
    let run (name: string) (runtime: ElmishRuntime<'env>) (work: Flow<'env, 'error, 'value>) (onResult: Action<Result<'value, 'error>>) : unit =
        for start in Cmd.OfFlow.either name runtime work Ok Error do
            start onResult.Invoke

/// <summary>
/// The external programs the C# UI starts, as Core <c>Launch</c> flows run on the app's runtime: a launch that fails
/// to start is a typed <c>ProcessError</c>, logged and handed to <c>onFailure</c> (which may be null) as text.
/// </summary>
module UiLaunch =

    let private platform =
        if OperatingSystem.IsWindows() then Launch.Windows
        elif OperatingSystem.IsMacOS() then Launch.MacOS
        else Launch.OtherPlatform

    /// The first file of this name on PATH.
    let private findOnPath (name: string) =
        (Environment.GetEnvironmentVariable "PATH" |> Option.ofObj |> Option.defaultValue "").Split(Path.PathSeparator)
        |> Array.filter (String.IsNullOrWhiteSpace >> not)
        |> Array.map (fun directory -> Path.Combine(directory, name))
        |> Array.tryFind File.Exists

    let private run (name: string) (runtime: ElmishRuntime<'env>) (launch: Flow<'env, ProcessError, unit>) (onFailure: Action<string>) =
        UiFlow.run name runtime launch (Action<Result<unit, ProcessError>>(function
            | Ok () -> ()
            | Error error ->
                let message = ProcessError.describe error
                Trace.WriteLine $"[launch] {name}: {message}"
                if not (isNull onFailure) then onFailure.Invoke message))

    /// <summary>Opens an http or https link in the browser; any other target is refused.</summary>
    let openLink (runtime: ElmishRuntime<'env>) (url: string) (onFailure: Action<string>) =
        run "open link" runtime (Launch.openLink url) onFailure

    /// <summary>Opens a file or folder GitKay wrote with the default handler.</summary>
    let openOwnedPath (runtime: ElmishRuntime<'env>) (path: string) (onFailure: Action<string>) =
        run "open path" runtime (Launch.openOwnedPath path) onFailure

    /// <summary>Opens VS Code with these arguments in <paramref name="workingDirectory"/>.</summary>
    let openInVsCode (runtime: ElmishRuntime<'env>) (arguments: string seq) (workingDirectory: string) (onFailure: Action<string>) =
        let codeOnPath =
            match platform with
            | Launch.Windows -> findOnPath "code.cmd" |> Option.map Launch.codeExecutableFor |> Option.filter File.Exists
            | _ -> findOnPath "code"
        run "open in VS Code" runtime (Launch.openInVsCode platform codeOnPath (List.ofSeq arguments) workingDirectory) onFailure

    /// <summary>Starts another GitKay in <paramref name="directory"/> with these arguments.</summary>
    let startGitKay (runtime: ElmishRuntime<'env>) (directory: string) (arguments: string seq) (onFailure: Action<string>) =
        match Environment.ProcessPath with
        | null -> if not (isNull onFailure) then onFailure.Invoke "Unknown GitKay executable path"
        | executable ->
            // `dotnet gitkay.dll` runs under the dotnet host; pass the app's entry dll along.
            let entryAssembly =
                if Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase) then
                    Some(Path.Combine(AppContext.BaseDirectory, "gitkay.dll"))
                else None
            run "start GitKay" runtime (Launch.startGitKay executable entryAssembly directory (List.ofSeq arguments)) onFailure
