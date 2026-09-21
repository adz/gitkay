namespace GitKay.Core

open System

/// <summary>
/// Two folders compared, or one folder read, with no repository involved. This module is pure: it pairs and diffs
/// what a walk found, so all of it is testable without a disk. Reading the disk is
/// <see cref="T:GitKay.Core.FolderSource"/>'s job.
/// </summary>
module Folder =

    /// <summary>
    /// What a folder walk refuses to descend into: build output and version control's own state. Walking them turns
    /// a comparison of two projects into a comparison of two caches.
    /// </summary>
    let skippedDirectories =
        set [ ".git"; ".hg"; ".svn"; "node_modules"; "bin"; "obj"; ".vs"; ".idea"; "artifacts"; ".venv"; "__pycache__" ]

    let isSkippedDirectory (name: string) = skippedDirectories.Contains name

    /// <summary>A file found under a root, named by its path relative to that root.</summary>
    type Entry =
        { /// <summary>Relative to the root, with forward slashes whatever the host uses.</summary>
          RelativePath: string
          /// <summary>Where the file actually is, for reading it.</summary>
          FullPath: string
          Size: int64 }

    /// <summary>One file as the comparison sees it: on the left, the right, or both.</summary>
    type Pair =
        { /// <summary>Its path on the left, or <c>FileChange.missing</c> when it is only on the right.</summary>
          OldPath: string
          /// <summary>Its path on the right, or <c>FileChange.missing</c> when it is only on the left.</summary>
          NewPath: string
          Left: Entry option
          Right: Entry option }

    /// <summary>What happened to this file between the two folders, in the same vocabulary a commit uses.</summary>
    let kindOf (pair: Pair) = FileChange.kind pair.OldPath pair.NewPath

    /// <summary>The path a reader knows this file by.</summary>
    let displayPathOf (pair: Pair) = FileChange.displayPath pair.OldPath pair.NewPath

    /// <summary>
    /// Matches two sets of entries by relative path. Ordered by path, so two runs over the same folders agree, and
    /// pure, so the pairing is tested without a disk.
    /// </summary>
    let pair (left: Entry seq) (right: Entry seq) : Pair list =
        let byPath (entries: Entry seq) = entries |> Seq.map (fun entry -> entry.RelativePath, entry) |> Map.ofSeq
        let left, right = byPath left, byPath right
        let paths = Set.union (left |> Map.keys |> Set.ofSeq) (right |> Map.keys |> Set.ofSeq)
        [ for path in Set.toList paths ->
            let onLeft, onRight = Map.tryFind path left, Map.tryFind path right
            { OldPath = (if onLeft.IsSome then path else FileChange.missing)
              NewPath = (if onRight.IsSome then path else FileChange.missing)
              Left = onLeft
              Right = onRight } ]

    /// <summary>
    /// Whether two sides could possibly hold the same bytes. Different sizes settle it without reading either file,
    /// which is what makes comparing two large trees bearable; equal sizes still have to be read to be sure.
    /// </summary>
    let couldDiffer (pair: Pair) =
        match pair.Left, pair.Right with
        | Some left, Some right -> left.Size <> right.Size
        | _ -> true

    /// <summary>A NUL byte in the first few KB, which is how git decides the same question.</summary>
    let looksBinary (bytes: byte array) =
        bytes |> Seq.truncate (min bytes.Length 8000) |> Seq.exists ((=) 0uy)

    /// <summary>The diff of one pair from the text of its two sides. A side that does not exist is empty text.</summary>
    let diffOf (pair: Pair) (oldText: string) (newText: string) =
        SourceDiff.between pair.OldPath pair.NewPath oldText newText

    /// <summary>
    /// The side of a pair worth previewing: the new one, or the old one for a file that is only on the left. Every
    /// pair has at least one side, so this always answers.
    /// </summary>
    let previewSide (pair: Pair) =
        match pair.Right, pair.Left with
        | Some right, _ -> Some right
        | None, left -> left

    /// <summary>An entry with nothing to diff — binary, or unreadable — still listed, with no lines.</summary>
    let emptyDiffOf (pair: Pair) : Models.FileDiff =
        { OldPath = pair.OldPath; NewPath = pair.NewPath; Hunks = []; NewLineCount = None }
