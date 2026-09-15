namespace GitKay.Core

/// <summary>
/// How a file's hunks and collapsed gaps become rows for a diff layout. Generic over the view's row objects, so views
/// keep their identities (selection and scroll anchors follow them) while the layout rules live here.
/// </summary>
module DiffRows =

    /// <summary>A file's content in order: collapsed context, or a hunk with its lines.</summary>
    type Block<'gap, 'hunk, 'line> =
        | Gap of 'gap
        | Hunk of hunk: 'hunk * lines: 'line list

    type Row<'gap, 'hunk, 'line> =
        /// <summary>Collapsed context; when a hunk follows, the gap row also stands in for that hunk's header.</summary>
        | GapRow of gap: 'gap * nextHunk: 'hunk option
        | HunkHeaderRow of 'hunk
        | LineRow of 'line
        /// <summary>A removed line directly followed by an added one, shown side by side as one row.</summary>
        | PairRow of removed: 'line * added: 'line

    let private pairRemovedWithAdded (kindOf: 'line -> Models.LineType) (lines: 'line list) =
        let rec go (remaining: 'line list) (rows: Row<'gap, 'hunk, 'line> list) =
            match remaining with
            | removed :: added :: rest when kindOf removed = Models.Removed && kindOf added = Models.Added ->
                go rest (PairRow(removed, added) :: rows)
            | line :: rest -> go rest (LineRow line :: rows)
            | [] -> rows
        go lines []

    /// <summary>
    /// Rows for <paramref name="blocks"/> in <paramref name="layout"/>. New and old file layouts drop the other side's
    /// lines, and a hunk left with no lines disappears with its header. A hunk header directly after a gap merges into it.
    /// </summary>
    let layout (layout: DiffLayout) (kindOf: 'line -> Models.LineType) (blocks: Block<'gap, 'hunk, 'line> list) : Row<'gap, 'hunk, 'line> list =
        let keeps line =
            match layout, kindOf line with
            | NewFile, Models.Removed
            | OldFile, Models.Added -> false
            | _ -> true

        // Rows are built newest-first, then reversed once.
        let addHeader hunk rows =
            match rows with
            | GapRow(gap, None) :: earlier -> GapRow(gap, Some hunk) :: earlier
            | _ -> HunkHeaderRow hunk :: rows

        blocks
        |> List.fold
            (fun rows block ->
                match block with
                | Gap gap -> GapRow(gap, None) :: rows
                | Hunk(hunk, lines) ->
                    match layout, List.filter keeps lines with
                    | _, [] -> rows
                    | SideBySide, kept -> pairRemovedWithAdded kindOf kept @ addHeader hunk rows
                    | _, kept -> (kept |> List.rev |> List.map LineRow) @ addHeader hunk rows)
            []
        |> List.rev
