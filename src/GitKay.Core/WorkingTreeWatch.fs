namespace GitKay.Core

open System
open System.IO
open Axial
open Axial.FileSystem

/// <summary>
/// Watches the working tree and the git directory's index and HEAD, and reports a change once events have been quiet
/// for a moment, so a build or checkout touching many files refreshes once. The watch is an Axial stream over the
/// environment's file system, so a test drives it with a test file system and a manual clock.
/// </summary>
module WorkingTreeWatch =

    /// <summary>The quiet period: a burst of file events refreshes once, this long after the last of them.</summary>
    let quietPeriod = TimeSpan.FromMilliseconds 300.0

    let private normalize (path: string) = Path.TrimEndingDirectorySeparator(Path.GetFullPath path)

    /// <summary>Whether a path is a directory or inside it.</summary>
    let isUnder (path: string) (directory: string) =
        let full = Path.GetFullPath path
        let root = normalize directory
        full.Equals(root, StringComparison.Ordinal)
        || full.StartsWith(root + string Path.DirectorySeparatorChar, StringComparison.Ordinal)

    /// <summary>Whether a changed path can move the uncommitted changes: anything in the working tree, and inside the
    /// git directory only the index and HEAD.</summary>
    let matters (gitDirectory: string) (path: string) =
        let gitDirectory = normalize gitDirectory
        if not (isUnder path gitDirectory) then
            true
        else
            match Path.GetRelativePath(gitDirectory, path) with
            | "index"
            | "HEAD" -> true
            | _ -> false

    /// <summary>One element per quiet period in which the working tree, the index or HEAD changed. A linked worktree or a
    /// separate git directory lives outside the working tree, so it is watched as well. The stream fails when a watcher
    /// cannot start or overflows; the caller rereads and starts a new one.</summary>
    let changes (workingDirectory: string) (gitDirectory: string) : FlowStream<'env, FileSystemError, unit> when 'env :> IHasFileSystem =
        let gitDirectory = normalize gitDirectory
        let tree = Files.watch true workingDirectory
        let events =
            if isUnder gitDirectory workingDirectory then tree
            else FlowStream.mergePar [ tree; Files.watch false gitDirectory ]
        events
        |> FlowStream.filter (fun change -> matters gitDirectory change.Path)
        |> FlowStream.map ignore
        |> FlowStream.debounce quietPeriod
