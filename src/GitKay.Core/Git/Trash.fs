namespace GitKay.Core

open System
open System.IO

/// <summary>
/// Copies of files GitKay is about to throw away, kept inside the git directory so a discard can be undone.
/// Discarding working tree changes (and deleting an untracked file) is the only thing GitKay does that git itself
/// can't recover: there is no reflog for content that was never committed.
/// </summary>
module Trash =

    /// <summary>One file's copy: where it lives in the working tree, and the copy's path (None when it didn't exist).</summary>
    type Entry =
        { Path: string
          BackupFile: string option
          /// <summary>What the file looked like right after the discard, so later edits aren't overwritten by an undo.</summary>
          AfterDiscard: string option }

    type Backup =
        { Directory: string
          Entries: Entry list
          /// <summary>What was discarded, for the message offering the undo.</summary>
          Description: string
          CreatedAt: DateTimeOffset }

    /// <summary>Where backups live; inside the git directory so they follow the repository and stay out of the tree.</summary>
    let directory (gitDir: string) = Path.Combine(gitDir, "gitkay", "discarded")

    let private safeName (path: string) =
        // Keep the shape of the path so a backup is recognisable, without letting it escape the backup directory.
        let cleaned = path.Replace('\\', '/').Split('/') |> Array.filter (fun part -> part <> "" && part <> "." && part <> "..")
        Path.Combine(cleaned)

    /// <summary>Copies each path that exists into a new backup folder. Paths are relative to the working tree.</summary>
    let capture (gitDir: string) (workingDirectory: string) (description: string) (now: DateTimeOffset) (paths: string list) : Backup =
        let target = Path.Combine(directory gitDir, now.ToString "yyyyMMdd-HHmmss-fff")
        let entries =
            paths
            |> List.distinct
            |> List.map (fun path ->
                let source = Path.Combine(workingDirectory, safeName path)
                if not (File.Exists source) then // axial-allow-effect: filesystem
                    { Path = path; BackupFile = None; AfterDiscard = None }
                else
                    let destination = Path.Combine(target, safeName path)
                    Directory.CreateDirectory(Path.GetDirectoryName destination) |> ignore // axial-allow-effect: filesystem
                    File.Copy(source, destination, true) // axial-allow-effect: filesystem
                    { Path = path; BackupFile = Some destination; AfterDiscard = None })
        { Directory = target; Entries = entries; Description = description; CreatedAt = now }

    /// <summary>A file's content, as a hash; None when it isn't there.</summary>
    let private hashOf (file: string) =
        if not (File.Exists file) then None // axial-allow-effect: filesystem
        else
            try
                use stream = File.OpenRead file // axial-allow-effect: filesystem
                Some(Convert.ToHexString(Security.Cryptography.SHA256.HashData stream))
            with :? IOException | :? UnauthorizedAccessException ->
                None

    /// <summary>
    /// Records what each file looks like now the discard has run, so an undo can tell its own work from edits made
    /// afterwards. Call it straight after the discard.
    /// </summary>
    let seal (workingDirectory: string) (backup: Backup) : Backup =
        { backup with
            Entries =
                backup.Entries
                |> List.map (fun entry -> { entry with AfterDiscard = hashOf (Path.Combine(workingDirectory, safeName entry.Path)) }) }

    /// <summary>Files that changed since the discard, which an undo would otherwise overwrite.</summary>
    let changedSince (workingDirectory: string) (backup: Backup) : string list =
        backup.Entries
        |> List.filter (fun entry -> hashOf (Path.Combine(workingDirectory, safeName entry.Path)) <> entry.AfterDiscard)
        |> List.map _.Path

    /// <summary>Puts the backed-up files back; a file that didn't exist before is removed again.</summary>
    let restore (workingDirectory: string) (backup: Backup) : unit =
        for entry in backup.Entries do
            let destination = Path.Combine(workingDirectory, safeName entry.Path)
            match entry.BackupFile with
            | Some source when File.Exists source -> // axial-allow-effect: filesystem
                Directory.CreateDirectory(Path.GetDirectoryName destination) |> ignore // axial-allow-effect: filesystem
                File.Copy(source, destination, true) // axial-allow-effect: filesystem
            | Some _ -> ()
            | None ->
                // It didn't exist when the backup was taken, so undoing a discard means it shouldn't exist now either.
                if File.Exists destination then File.Delete destination // axial-allow-effect: filesystem

    /// <summary>Whether every file in a backup is still there to restore from.</summary>
    let isIntact (backup: Backup) =
        backup.Entries
        |> List.forall (fun entry -> match entry.BackupFile with Some file -> File.Exists file | None -> true) // axial-allow-effect: filesystem

    /// <summary>Deletes all but the newest <paramref name="keep"/> backups, so they don't pile up.</summary>
    let prune (gitDir: string) (keep: int) : unit =
        let root = directory gitDir
        if Directory.Exists root then // axial-allow-effect: filesystem
            Directory.GetDirectories root // axial-allow-effect: filesystem
            |> Array.sortDescending
            |> Array.skip (min keep (Directory.GetDirectories root).Length) // axial-allow-effect: filesystem
            |> Array.iter (fun folder ->
                try Directory.Delete(folder, true) with :? IOException | :? UnauthorizedAccessException -> ()) // axial-allow-effect: filesystem
