namespace GitKay.Core

open System
open System.IO
open Axial
open Axial.FileSystem

/// Copies of files GitKay is about to throw away, kept inside the git directory so a discard can be undone.
module Trash =
    type Entry =
        { Path: string
          BackupFile: string option
          AfterDiscard: string option }

    type Backup =
        { Directory: string
          Entries: Entry list
          Description: string
          CreatedAt: DateTimeOffset }

    let directory (gitDir: string) = Path.Combine(gitDir, "gitkay", "discarded")

    let private safeName (path: string) =
        let cleaned = path.Replace('\\', '/').Split('/') |> Array.filter (fun part -> part <> "" && part <> "." && part <> "..")
        Path.Combine(cleaned)

    let capture (gitDir: string) (workingDirectory: string) (description: string) (now: DateTimeOffset) (paths: string list)
        : Flow<'env, FileSystemError, Backup> when 'env :> IHasFileSystem =
        flow {
            let target = Path.Combine(directory gitDir, now.ToString "yyyyMMdd-HHmmss-fff")
            let! entries =
                paths |> List.distinct |> Flow.traverse (fun path -> flow {
                    let source = Path.Combine(workingDirectory, safeName path)
                    let! exists = FileSystem.fileExists source
                    if not exists then
                        return { Path = path; BackupFile = None; AfterDiscard = None }
                    else
                        let destination = Path.Combine(target, safeName path)
                        do! FileSystem.createDirectory (Path.GetDirectoryName destination)
                        do! FileSystem.copyFile source destination true
                        return { Path = path; BackupFile = Some destination; AfterDiscard = None }
                })
            return { Directory = target; Entries = entries; Description = description; CreatedAt = now }
        }

    let private hashOf (file: string) : Flow<'env, FileSystemError, string option> when 'env :> IHasFileSystem =
        flow {
            let! exists = FileSystem.fileExists file
            if not exists then return None
            else
                return!
                    (flow {
                        let! stream = FileSystem.openRead file
                        use stream = stream
                        return Some(Convert.ToHexString(Security.Cryptography.SHA256.HashData stream))
                    }
                    |> Flow.orElseWith (function
                        | FileSystemError.Io _ | FileSystemError.Unauthorized _ -> Flow.ok None
                        | error -> Flow.fail error))
        }

    let seal (workingDirectory: string) (backup: Backup) : Flow<'env, FileSystemError, Backup> when 'env :> IHasFileSystem =
        flow {
            let! entries = backup.Entries |> Flow.traverse (fun entry -> flow {
                let! hash = hashOf (Path.Combine(workingDirectory, safeName entry.Path))
                return { entry with AfterDiscard = hash }
            })
            return { backup with Entries = entries }
        }

    let changedSince (workingDirectory: string) (backup: Backup) : Flow<'env, FileSystemError, string list> when 'env :> IHasFileSystem =
        flow {
            let! changes = backup.Entries |> Flow.traverse (fun entry -> flow {
                let! hash = hashOf (Path.Combine(workingDirectory, safeName entry.Path))
                return if hash <> entry.AfterDiscard then Some entry.Path else None
            })
            return List.choose id changes
        }

    let restore (workingDirectory: string) (backup: Backup) : Flow<'env, FileSystemError, unit> when 'env :> IHasFileSystem =
        flow {
            for entry in backup.Entries do
                let destination = Path.Combine(workingDirectory, safeName entry.Path)
                match entry.BackupFile with
                | Some source ->
                    let! exists = FileSystem.fileExists source
                    if exists then
                        do! FileSystem.createDirectory (Path.GetDirectoryName destination)
                        do! FileSystem.copyFile source destination true
                | None ->
                    let! exists = FileSystem.fileExists destination
                    if exists then do! FileSystem.deleteFile destination
        }

    let isIntact (backup: Backup) : Flow<'env, FileSystemError, bool> when 'env :> IHasFileSystem =
        flow {
            let! present = backup.Entries |> Flow.traverse (fun entry ->
                match entry.BackupFile with
                | Some file -> FileSystem.fileExists file
                | None -> Flow.ok true)
            return List.forall id present
        }

    let prune (gitDir: string) (keep: int) : Flow<'env, FileSystemError, unit> when 'env :> IHasFileSystem =
        flow {
            let root = directory gitDir
            let! exists = FileSystem.directoryExists root
            if exists then
                let! folders = FileSystem.getDirectories root "*" SearchOption.TopDirectoryOnly
                for folder in folders |> Array.sortDescending |> Array.skip (min keep folders.Length) do
                    do!
                        FileSystem.deleteDirectory folder true
                        |> Flow.orElseWith (function
                            | FileSystemError.Io _ | FileSystemError.Unauthorized _ -> Flow.ok ()
                            | error -> Flow.fail error)
        }
