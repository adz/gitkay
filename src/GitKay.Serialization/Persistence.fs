namespace GitKay.Serialization

open System
open System.IO
open Axial
open Axial.FileSystem
open GitKay.Core

/// Why a persisted file could not be used: its path and what was wrong with it.
type LoadFailure =
    { Path: string
      Reason: string }

/// A persisted file read: the value, and — when the file existed but could not be used — why it was ignored.
type LoadResult<'value> =
    { Value: 'value
      Failure: LoadFailure option }

/// <summary>The file system the stores read and write.</summary>
type StoreEnv =
    { Files: IFileSystem }
    interface IHasFileSystem with member this.FileSystem = this.Files

/// <summary>
/// JSON files read and written through the environment's file system. A missing file reads as the fallback; an
/// unreadable or invalid one reads as the fallback too, with why it was ignored. A failed write is logged and
/// ignored: settings and UI state are conveniences, and losing one save must not stop the app.
/// </summary>
[<RequireQualifiedAccess>]
module JsonFile =

    /// What went wrong, as the settings failure dialog shows it.
    let private reason (error: FileSystemError) =
        match error with
        | FileSystemError.Unauthorized(_, message)
        | FileSystemError.InvalidPath(_, message)
        | FileSystemError.PathTooLong(_, message)
        | FileSystemError.Io(_, message)
        | FileSystemError.Unsupported(_, message)
        | FileSystemError.Unexpected(_, message) -> message
        | other -> other.ToString()

    let load (path: string) (fallback: 'value) (label: string) (decode: string -> Result<'value, string>)
        : Flow<'env, Never, LoadResult<'value>> when 'env :> IHasFileSystem =
        flow {
            let! exists = FileSystem.fileExists path
            if not exists then
                return { Value = fallback; Failure = None }
            else
                let! text = FileSystem.readAllText path
                let decoded =
                    try
                        decode text
                    with error ->
                        // A decoder that throws on a malformed file reads as an invalid file.
                        Error error.Message
                match decoded with
                | Ok value -> return { Value = value; Failure = None }
                | Error error ->
                    do! Log.warning $"[{label}] {path} ignored: {error}"
                    return { Value = fallback; Failure = Some { Path = path; Reason = error } }
        }
        |> Flow.recover (fun error ->
            flow {
                do! Log.warning $"[{label}] {path} unreadable: {reason error}"
                return { Value = fallback; Failure = Some { Path = path; Reason = reason error } }
            })

    /// Writes encoded text, creating the directory when needed.
    let write (path: string) (label: string) (json: string) : Flow<'env, Never, unit> when 'env :> IHasFileSystem =
        flow {
            let directory = Path.GetDirectoryName path
            if not (String.IsNullOrWhiteSpace directory) then
                do! FileSystem.createDirectory directory
            do! FileSystem.writeAllText path json
        }
        |> Flow.recover (fun error -> Log.warning $"[{label}] {path} not saved: {reason error}")

/// <summary>The settings file.</summary>
[<RequireQualifiedAccess>]
module SettingsFile =
    /// Loads settings, returning defaults when the file is missing, unreadable, or invalid, and why it was ignored.
    let load path = JsonFile.load path Settings.defaults "settings" SettingsJson.decode

    /// Saves settings.
    let save path settings = JsonFile.write path "settings" (SettingsJson.encode settings)

/// <summary>The UI state file.</summary>
[<RequireQualifiedAccess>]
module UiStateFile =
    /// Loads UI state, returning an empty state when the file is missing, unreadable, or invalid, and why it was ignored.
    let load path = JsonFile.load path UiState.empty "ui-state" UiStateJson.decode

    /// Saves UI state.
    let save path state = JsonFile.write path "ui-state" (UiStateJson.encode state)
