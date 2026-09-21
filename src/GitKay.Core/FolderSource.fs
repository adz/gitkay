// axial-allow-effect-file: filesystem
// This module is the filesystem boundary for folder comparison and folder previewing: it reads, and everything it
// reads is handed to GitKay.Core.Folder, which is pure.
namespace GitKay.Core

open System
open System.IO

module FolderSource =

    let private relativePath (root: string) (full: string) =
        Path.GetRelativePath(root, full).Replace(Path.DirectorySeparatorChar, '/')

    /// <summary>
    /// Every file under a directory. A directory that cannot be read is skipped rather than failing the whole walk:
    /// a folder you can mostly read is still worth seeing.
    /// </summary>
    let rec private walk (root: string) (directory: string) : Folder.Entry list =
        let files =
            try
                Directory.EnumerateFiles directory
                |> Seq.map (fun file ->
                    { Folder.RelativePath = relativePath root file
                      Folder.FullPath = file
                      Folder.Size = FileInfo(file).Length })
                |> List.ofSeq
            with _ -> []
        let nested =
            try
                Directory.EnumerateDirectories directory
                |> Seq.filter (Path.GetFileName >> Folder.isSkippedDirectory >> not)
                |> Seq.collect (walk root)
                |> List.ofSeq
            with _ -> []
        files @ nested

    /// <summary>Every file under a folder, ordered by path, or why the folder could not be read.</summary>
    let read (root: string) : Result<Folder.Entry list, GitError> =
        if String.IsNullOrWhiteSpace root then Error(GitError.OperationFailed("Read folder", "No folder was given"))
        elif not (Directory.Exists root) then Error(GitError.OperationFailed("Read folder", $"Not a folder: {root}"))
        else
            let full = Path.GetFullPath root
            Ok(walk full full |> List.sortBy _.RelativePath)

    let bytesOf (entry: Folder.Entry) : Result<byte array, GitError> =
        try Ok(File.ReadAllBytes entry.FullPath)
        with error -> Error(GitError.OperationFailed("Read file", error.Message))

    /// <summary>One side's text, or nothing when that side does not exist or holds something that is not text.</summary>
    let private sideText (entry: Folder.Entry option) =
        match entry with
        | None -> Some ""
        | Some entry ->
            match bytesOf entry with
            | Error _ -> None
            | Ok bytes -> if Folder.looksBinary bytes then None else Some(Text.Encoding.UTF8.GetString bytes)

    /// <summary>
    /// The diff for one pair. Binary or unreadable files are listed with no lines rather than diffed as mangled
    /// text: that the file changed is true and worth showing, and is all that can honestly be said about it.
    /// </summary>
    let diff (pair: Folder.Pair) : Models.FileDiff =
        match sideText pair.Left, sideText pair.Right with
        | Some oldText, Some newText -> Folder.diffOf pair oldText newText
        | _ -> Folder.emptyDiffOf pair

    /// <summary>
    /// Both sides as text, for a caller that wants to reformat them before diffing. A side that does not exist is
    /// empty text; a side that cannot be read as text is an error, so the caller shows the source instead.
    /// </summary>
    let sideTexts (pair: Folder.Pair) : Result<string * string, GitError> =
        match sideText pair.Left, sideText pair.Right with
        | Some oldText, Some newText -> Ok(oldText, newText)
        | _ -> Error(GitError.OperationFailed("Read file", "This file cannot be read as text"))

    /// <summary>The bytes of the side worth previewing, for an image.</summary>
    let previewBytes (pair: Folder.Pair) : Result<byte array, GitError> =
        match Folder.previewSide pair with
        | Some entry -> bytesOf entry
        | None -> Error(GitError.OperationFailed("Read file", "This file has no side to read"))

    /// <summary>Whether both sides hold exactly the same bytes. Only asked when their sizes already match.</summary>
    let holdsSameBytes (pair: Folder.Pair) =
        match pair.Left, pair.Right with
        | Some left, Some right ->
            match bytesOf left, bytesOf right with
            | Ok leftBytes, Ok rightBytes -> leftBytes = rightBytes
            | _ -> false
        | _ -> false

    /// <summary>The pairs worth showing: everything except files that are identical on both sides.</summary>
    let changedPairs (pairs: Folder.Pair list) =
        pairs |> List.filter (fun pair -> Folder.couldDiffer pair || not (holdsSameBytes pair))
