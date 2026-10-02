namespace GitKay.Serialization

open System
open System.Diagnostics
open System.IO
open GitKay.Core

/// Why a persisted file could not be used: its path and what was wrong with it.
type LoadFailure =
    { Path: string
      Reason: string }

/// A persisted file read: the value, and — when the file existed but could not be used — why it was ignored.
type LoadResult<'value> =
    { Value: 'value
      Failure: LoadFailure option }

[<RequireQualifiedAccess>]
module private JsonFileStore =
    let load (path: string) fallback label (decode: string -> Result<'value, string>) : LoadResult<'value> =
        try
            if not (File.Exists path) then
                { Value = fallback; Failure = None }
            else
                match decode (File.ReadAllText path) with
                | Ok value -> { Value = value; Failure = None }
                | Error error ->
                    Trace.WriteLine($"[{label}] {path} ignored: {error}")
                    { Value = fallback; Failure = Some { Path = path; Reason = error } }
        with exception' ->
            Trace.WriteLine($"[{label}] {path} unreadable: {exception'.Message}")
            { Value = fallback; Failure = Some { Path = path; Reason = exception'.Message } }

    /// Writes encoded text, creating the directory when needed. Write failures are logged and ignored.
    let writeJson (path: string) label (json: string) =
        try
            let directory = Path.GetDirectoryName path
            if not (String.IsNullOrWhiteSpace directory) then Directory.CreateDirectory directory |> ignore
            File.WriteAllText(path, json)
        with exception' ->
            Trace.WriteLine($"[{label}] {path} not saved: {exception'.Message}")

    let save (path: string) label (encode: 'value -> string) value =
        writeJson path label (encode value)

/// Serializes writes and coalesces the ones that arrive while a write is in flight, so a burst of saves (one per
/// selected commit) never blocks the caller and never interleaves on the file. The newest value always wins.
[<RequireQualifiedAccess>]
module private BackgroundWriter =
    let private writeGate = obj ()
    let private pendingGate = obj ()
    let private waiting = System.Collections.Generic.List<System.Threading.Tasks.TaskCompletionSource>()
    let mutable private pending: (unit -> unit) option = None
    let mutable private writerRunning = false

    let private writeWhilePending () =
        let mutable finished = false

        while not finished do
            let next =
                lock pendingGate (fun () ->
                    match pending with
                    | None ->
                        writerRunning <- false
                        finished <- true
                        None
                    | Some write ->
                        pending <- None
                        Some(write, waiting.ToArray()))

            match next with
            | None -> ()
            | Some(write, completions) ->
                lock writeGate write
                for completion in completions do
                    completion.TrySetResult() |> ignore

    /// Queues a write and returns a task that completes once the file holds it (or a newer queued value supersedes it).
    let enqueue (write: unit -> unit) : System.Threading.Tasks.Task =
        let completion = System.Threading.Tasks.TaskCompletionSource()

        let startWriter =
            lock pendingGate (fun () ->
                pending <- Some write
                waiting.Add completion

                if writerRunning then
                    false
                else
                    writerRunning <- true
                    true)

        if startWriter then
            System.Threading.Tasks.Task.Run(writeWhilePending) |> ignore

        completion.Task

[<RequireQualifiedAccess>]
module private StorePaths =
    let appDataFile fileName =
        let appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
        let directory = if String.IsNullOrWhiteSpace appData then Path.GetTempPath() else appData
        Path.Combine(directory, "gitkay", fileName)

[<RequireQualifiedAccess>]
module SettingsStore =
    let private defaultPath () = StorePaths.appDataFile "settings.json"

    /// Loads settings from a path, returning defaults when the file is missing, unreadable, or invalid, and — when
    /// the file existed but could not be used — why it was ignored.
    let loadReporting path : LoadResult<Settings> =
        let path = if isNull path then defaultPath () else path
        JsonFileStore.load path Settings.defaults "settings" SettingsJson.decode

    /// Loads settings from a path, returning defaults when the file is missing, unreadable, or invalid.
    let load path : Settings = (loadReporting path).Value

    /// Loads settings from the user's application data directory.
    let loadDefault () : Settings = load (defaultPath ())

    /// Loads settings from the user's application data directory, reporting why the file was ignored when it was.
    let loadDefaultReporting () : LoadResult<Settings> = loadReporting (defaultPath ())

    /// Saves settings to a path. Write failures are logged and ignored.
    let save path settings =
        let path = if isNull path then defaultPath () else path
        JsonFileStore.save path "settings" SettingsJson.encode settings

    /// Saves settings in the user's application data directory.
    let saveDefault settings = save (defaultPath ()) settings

[<RequireQualifiedAccess>]
module UiStateStore =
    let private defaultPath () = StorePaths.appDataFile "ui-state.json"

    /// Loads UI state from a path, returning an empty state when the file is missing, unreadable, or invalid, and —
    /// when the file existed but could not be used — why it was ignored.
    let loadReporting path : LoadResult<UiState> =
        let path = if isNull path then defaultPath () else path
        JsonFileStore.load path UiState.empty "ui-state" UiStateJson.decode

    /// Loads UI state from a path, returning an empty state when the file is missing, unreadable, or invalid.
    let load path : UiState = (loadReporting path).Value

    /// Loads UI state from the user's application data directory.
    let loadDefault () : UiState = load (defaultPath ())

    /// Loads UI state from the user's application data directory, reporting why the file was ignored when it was.
    let loadDefaultReporting () : LoadResult<UiState> = loadReporting (defaultPath ())

    /// Saves UI state to a path. Write failures are logged and ignored.
    let save path state =
        let path = if isNull path then defaultPath () else path
        JsonFileStore.save path "ui-state" UiStateJson.encode state

    /// Saves UI state to a path without making the caller wait for the disk. Saves made while one is being written
    /// collapse into the newest.
    let saveInBackground path (state: UiState) : System.Threading.Tasks.Task =
        let path = if isNull path then defaultPath () else path
        let json = UiStateJson.encode state
        BackgroundWriter.enqueue (fun () -> JsonFileStore.writeJson path "ui-state" json)

    /// Saves UI state in the user's application data directory.
    let saveDefault state = save (defaultPath ()) state

    /// Saves UI state in the user's application data directory without making the caller wait for the disk.
    let saveInBackgroundDefault (state: UiState) : System.Threading.Tasks.Task =
        saveInBackground (defaultPath ()) state
