namespace GitKay.Core

open GitKay.Core.Models

/// <summary>
/// A diff between two pieces of text. Nothing here knows where the text came from, so the same function serves a
/// commit's blobs, a working-tree file and two folders on disk.
/// </summary>
module SourceDiff =

    /// <summary>
    /// Lines as the diff sees them. Newline style is normalized so it is never reported as a change, and an empty
    /// source is no lines at all -- splitting it would invent one blank line, so an added file would report a line
    /// removed that never existed.
    /// </summary>
    let private linesOf (source: string) =
        if source = "" then Array.empty else source.Replace("\r\n", "\n").Split '\n'

    /// <summary>
    /// A whole-file diff of two sources, numbered from one. The line numbers are accumulated as the edit script is
    /// walked; the mutation never leaves this function.
    /// </summary>
    let between (oldPath: string) (newPath: string) (oldSource: string) (newSource: string) =
        let oldLines, newLines = linesOf oldSource, linesOf newSource
        let mutable oldLine, newLine = 0, 0
        let lines =
            GitKay.Kit.Myers.diff oldLines newLines
            |> List.map (function
                | GitKay.Kit.Equal(_, text) ->
                    oldLine <- oldLine + 1; newLine <- newLine + 1
                    { Type = Context; Content = text; OldLineNo = Some oldLine; NewLineNo = Some newLine }
                | GitKay.Kit.Delete text ->
                    oldLine <- oldLine + 1
                    { Type = Removed; Content = text; OldLineNo = Some oldLine; NewLineNo = None }
                | GitKay.Kit.Insert text ->
                    newLine <- newLine + 1
                    { Type = Added; Content = text; OldLineNo = None; NewLineNo = Some newLine })
        { OldPath = oldPath; NewPath = newPath
          Hunks = if lines.IsEmpty then [] else [ { Header = $"@@ -1,{oldLines.Length} +1,{newLines.Length} @@"; Lines = lines } ]
          NewLineCount = Some newLines.Length }

    /// <summary>
    /// The same diff, but hunked the way git writes one: each run of changes with <paramref name="contextLines"/>
    /// unchanged lines either side, and everything further away left out. A whole-file diff of a large document is
    /// thousands of rows the reader did not ask for, and inserting them moves whatever they were looking at.
    /// </summary>
    let withContext (contextLines: int) (file: FileDiff) : FileDiff =
        let lines = file.Hunks |> List.collect _.Lines |> Array.ofList
        if lines.Length = 0 then file
        else

        let context = max 0 contextLines
        let changed = lines |> Array.map (fun line -> line.Type <> Context)
        if not (Array.exists id changed) then { file with Hunks = [] }
        else

        // Every line close enough to a change to be worth showing.
        let keep = Array.create lines.Length false
        changed |> Array.iteri (fun index isChanged ->
            if isChanged then
                for near in max 0 (index - context) .. min (lines.Length - 1) (index + context) do keep.[near] <- true)

        // Consecutive kept lines become one hunk, numbered from the lines themselves.
        let hunks = ResizeArray<DiffHunk>()
        let current = ResizeArray<DiffLine>()
        let flush () =
            if current.Count > 0 then
                let first = current.[0]
                let oldStart = first.OldLineNo |> Option.defaultValue 0
                let newStart = first.NewLineNo |> Option.defaultValue 0
                let oldCount = current |> Seq.filter (fun line -> line.Type <> Added) |> Seq.length
                let newCount = current |> Seq.filter (fun line -> line.Type <> Removed) |> Seq.length
                hunks.Add { Header = $"@@ -%d{oldStart},%d{oldCount} +%d{newStart},%d{newCount} @@"; Lines = List.ofSeq current }
                current.Clear()

        for index in 0 .. lines.Length - 1 do
            if keep.[index] then current.Add lines.[index]
            else flush ()
        flush ()
        { file with Hunks = List.ofSeq hunks }

    /// <summary>How many lines the diff added and removed, for a change summary.</summary>
    let counts (file: FileDiff) =
        let lines = file.Hunks |> List.collect _.Lines
        lines |> List.filter (fun line -> line.Type = Added) |> List.length,
        lines |> List.filter (fun line -> line.Type = Removed) |> List.length
