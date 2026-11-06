namespace GitKay.Serialization

open System
open System.Diagnostics
open System.IO
open Axial
open Axial.FileSystem
open GitKay.Core

/// <summary>
/// The host's side of the stores: the live file system, the files' place in the user's application data, the
/// synchronous calls the C# UI makes on the UI thread (each runs one small file read or write as a flow), and the
/// coalescing background writer. Log entries go to System.Diagnostics.Trace, as the rest of the app's do.
/// </summary>
[<RequireQualifiedAccess>]
module Stores =

    /// The live file system.
    let live : StoreEnv = { Files = FileSystem.live }

    let private traceSink : ILogSink =
        { new ILogSink with
            member _.IsEnabled level = level >= LogLevel.Information
            member _.Write entry = Trace.WriteLine entry.Message }

    /// The value of a store flow's exit: a store never fails, it falls back, so only a defect is left to raise.
    let value (exit: Exit<'value, Never>) : 'value =
        match exit with
        | Exit.Success value -> value
        | Exit.Failure cause -> failwith (Cause.prettyPrint (fun (never: Never) -> Never.absurd never) cause)

    /// A store flow with its log entries going to Trace.
    let traced (work: Flow<'env, 'error, 'value>) = work |> Flow.withLogSink traceSink

    /// Runs a store flow on the live file system and waits for it.
    let run (work: Flow<StoreEnv, Never, 'value>) : 'value = work |> traced |> Flow.run live |> value

    /// A file in GitKay's folder of the user's application data (the temp folder when there is none).
    let appDataFile fileName =
        let appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
        let directory = if String.IsNullOrWhiteSpace appData then Path.GetTempPath() else appData
        Path.Combine(directory, "gitkay", fileName)

[<RequireQualifiedAccess>]
module SettingsStore =
    let private defaultPath () = Stores.appDataFile "settings.json"

    /// Loads settings from a path, returning defaults when the file is missing, unreadable, or invalid, and — when
    /// the file existed but could not be used — why it was ignored.
    let loadReporting path : LoadResult<Settings> =
        let path = if isNull path then defaultPath () else path
        Stores.run (SettingsFile.load path)

    /// Loads settings from a path, returning defaults when the file is missing, unreadable, or invalid.
    let load path : Settings = (loadReporting path).Value

    /// Loads settings from the user's application data directory.
    let loadDefault () : Settings = load (defaultPath ())

    /// Loads settings from the user's application data directory, reporting why the file was ignored when it was.
    let loadDefaultReporting () : LoadResult<Settings> = loadReporting (defaultPath ())

    /// Saves settings to a path. Write failures are logged and ignored.
    let save path settings =
        let path = if isNull path then defaultPath () else path
        Stores.run (SettingsFile.save path settings)

    /// Saves settings in the user's application data directory.
    let saveDefault settings = save (defaultPath ()) settings

[<RequireQualifiedAccess>]
module UiStateStore =
    let private defaultPath () = Stores.appDataFile "ui-state.json"

    /// Loads UI state from a path, returning an empty state when the file is missing, unreadable, or invalid, and —
    /// when the file existed but could not be used — why it was ignored.
    let loadReporting path : LoadResult<UiState> =
        let path = if isNull path then defaultPath () else path
        Stores.run (UiStateFile.load path)

    /// Loads UI state from a path, returning an empty state when the file is missing, unreadable, or invalid.
    let load path : UiState = (loadReporting path).Value

    /// Loads UI state from the user's application data directory.
    let loadDefault () : UiState = load (defaultPath ())

    /// Loads UI state from the user's application data directory, reporting why the file was ignored when it was.
    let loadDefaultReporting () : LoadResult<UiState> = loadReporting (defaultPath ())

    /// Saves UI state to a path. Write failures are logged and ignored.
    let save path state =
        let path = if isNull path then defaultPath () else path
        Stores.run (UiStateFile.save path state)

    /// Saves UI state to a path without making the caller wait for the disk. Saves made while one is being written
    /// collapse into the newest.
    let saveInBackground path (state: UiState) : System.Threading.Tasks.Task =
        let path = if isNull path then defaultPath () else path
        BackgroundWriter.enqueue (fun () -> Stores.run (UiStateFile.save path state))

    /// Saves UI state in the user's application data directory.
    let saveDefault state = save (defaultPath ()) state

    /// Saves UI state in the user's application data directory without making the caller wait for the disk.
    let saveInBackgroundDefault (state: UiState) : System.Threading.Tasks.Task =
        saveInBackground (defaultPath ()) state

/// <summary>What the desktop host reads before its first window: the saved settings and UI state, and the repository
/// the launch directory is in (empty when none).</summary>
type StartupReads =
    { Settings: LoadResult<Settings>
      UiState: LoadResult<UiState>
      Repository: string }

[<RequireQualifiedAccess>]
module Startup =

    /// <summary>The startup reads as one flow. The host runs it off the UI thread with
    /// <c>Startup.read.StartAsTask(Stores.live)</c> and takes its value with <c>Stores.value</c>.</summary>
    let read : Flow<StoreEnv, Never, StartupReads> =
        flow {
            let! settings = SettingsFile.load (Stores.appDataFile "settings.json")
            let! uiState = UiStateFile.load (Stores.appDataFile "ui-state.json")
            let! repository = Flow.fromBlocking (fun _ -> GitService.tryDiscoverRepositoryPath ())
            return { Settings = settings; UiState = uiState; Repository = if isNull repository then "" else repository }
        }
        |> Stores.traced
