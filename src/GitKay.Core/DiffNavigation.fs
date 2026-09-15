namespace GitKay.Core

open System

/// <summary>What changed within a line that exists on both sides of a diff.</summary>
module DiffText =

    /// <summary>
    /// The changed middle of two versions of a line, after their common prefix and suffix, as
    /// (start, old length, new length). None when the versions are equal or either is empty.
    /// </summary>
    let changedSpan (oldText: string) (newText: string) : struct (int * int * int) voption =
        if String.IsNullOrEmpty oldText || String.IsNullOrEmpty newText || oldText = newText then ValueNone
        else
            let shared = min oldText.Length newText.Length
            let mutable prefix = 0
            while prefix < shared && oldText[prefix] = newText[prefix] do
                prefix <- prefix + 1
            let mutable suffix = 0
            while suffix < shared - prefix && oldText[oldText.Length - suffix - 1] = newText[newText.Length - suffix - 1] do
                suffix <- suffix + 1
            ValueSome(struct (prefix, oldText.Length - prefix - suffix, newText.Length - prefix - suffix))

/// <summary>Moving and selecting over a diff's rows, independent of how the view draws them.</summary>
module DiffNavigation =

    type RowKind =
        | FileHeader
        | HunkHeader
        | CollapsedGap
        | Line

    /// <summary>A row as navigation sees it; line numbers are -1 where the line doesn't exist.</summary>
    [<Struct>]
    type Row = { Kind: RowKind; OldLine: int; NewLine: int }

    /// <summary>A character position: a row index and a column in that row's text.</summary>
    [<Struct>]
    type TextPosition = { Row: int; Column: int }

    /// <summary>
    /// vim's {count}G in a diff: the row of line <paramref name="number"/> in the file holding <paramref name="focus"/>
    /// (the first file when nothing is focused), or the nearest shown line after it when that line is hidden context.
    /// Old-file layouts count old line numbers and the others new ones; a line that only exists on the other side
    /// (a removed line in unified view) is chosen only when no line has the number on the counted side. -1 when the
    /// file shows no such line.
    /// </summary>
    let goToLine (rows: Row[]) (focus: int) (layout: DiffLayout) (number: int) =
        let mutable fileStart = max 0 (min focus (rows.Length - 1))
        while fileStart > 0 && rows[fileStart].Kind <> FileHeader do
            fileStart <- fileStart - 1
        // (line number, 1 when it's only on the other side): smaller is better.
        let rank (row: Row) =
            let counted, other = if layout = OldFile then row.OldLine, row.NewLine else row.NewLine, row.OldLine
            if counted >= 0 then struct (counted, 0) elif other >= 0 then struct (other, 1) else struct (Int32.MaxValue, 1)
        let mutable best = -1
        let mutable bestRank = struct (Int32.MaxValue, 1)
        let mutable index = fileStart + (if rows.Length > 0 && rows[fileStart].Kind = FileHeader then 1 else 0)
        while index < rows.Length && rows[index].Kind <> FileHeader do
            if rows[index].Kind = Line then
                let struct (candidate, _) as candidateRank = rank rows[index]
                if candidate >= number && candidateRank < bestRank then
                    best <- index
                    bestRank <- candidateRank
            index <- index + 1
        best

    /// <summary>
    /// vim's ]c / [c: the first line of the next or previous hunk (or its gap or header when no line follows), or -1.
    /// </summary>
    let hunkTarget (rows: Row[]) (focus: int) (forward: bool) =
        let step = if forward then 1 else -1
        let start = if focus >= 0 then focus elif forward then -1 else rows.Length
        let mutable index = start + step
        let mutable target = -1
        while target < 0 && index >= 0 && index < rows.Length do
            match rows[index].Kind with
            | HunkHeader | CollapsedGap ->
                let first = if index + 1 < rows.Length && rows[index + 1].Kind = Line then index + 1 else index
                // Going back from a hunk's first line passes its own header on the way.
                if (forward && first > start) || (not forward && first < start) then target <- first
                index <- index + step
            | _ -> index <- index + step
        target

    /// <summary>The two ends of a selection in reading order.</summary>
    let ordered (anchor: TextPosition) (active: TextPosition) =
        if anchor.Row < active.Row || (anchor.Row = active.Row && anchor.Column <= active.Column) then struct (anchor, active)
        else struct (active, anchor)

    /// <summary>
    /// vim's V: whole lines from the anchor row to the active row, trimmed to rows that are lines. None when the range
    /// holds no line. <paramref name="lengthAt"/> gives a line row's text length.
    /// </summary>
    let linewise (rows: Row[]) (anchorRow: int) (activeRow: int) (lengthAt: int -> int) =
        let mutable first = min anchorRow activeRow
        let mutable last = max anchorRow activeRow
        while first < last && rows[first].Kind <> Line do
            first <- first + 1
        while last > first && rows[last].Kind <> Line do
            last <- last - 1
        if last < 0 || last >= rows.Length || rows[last].Kind <> Line then ValueNone
        else ValueSome(struct ({ Row = first; Column = 0 }, { Row = last; Column = lengthAt last }))

    /// <summary>
    /// vim's v: from the anchor to the active position, including the character under the caret at the far end.
    /// Columns are clamped by <paramref name="lengthAt"/>.
    /// </summary>
    let characterwise (anchor: TextPosition) (active: TextPosition) (lengthAt: int -> int) =
        let struct (first, last) = ordered anchor active
        struct (first, { last with Column = min (last.Column + 1) (lengthAt last.Row) })

    /// <summary>
    /// The text between two positions, one line per line row, skipping rows that aren't lines. <paramref name="textAt"/>
    /// gives a row's text, or null for rows without text.
    /// </summary>
    let selectedText (first: TextPosition) (last: TextPosition) (textAt: int -> string) =
        [ for row in first.Row .. last.Row do
              match textAt row with
              | null -> ()
              | text ->
                  let from = if row = first.Row then min first.Column text.Length else 0
                  let until = if row = last.Row then min last.Column text.Length else text.Length
                  text.Substring(from, max 0 (until - from)) ]
        |> String.concat "\n"
