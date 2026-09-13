namespace GitKay.Core

open System
open System.Text.RegularExpressions
open GitKay.Core.Models

/// Pure range calculation and projection for file-scoped context expansion.
/// Line ranges are expressed in new-file line numbers; hidden context lines share both sides.
module DiffExpansion =

    /// Number of lines revealed by an incremental expansion.
    [<Literal>]
    let StepLines = 10

    type GapKind =
        | Leading
        | Internal
        | Trailing

    /// Inclusive range of new-file line numbers.
    type LineRange = { Start: int; End: int }

    /// Stable identity of one collapsed range within one file.
    type DiffGap =
        {
            OldPath: string
            NewPath: string
            Kind: GapKind
            /// First hidden old-file line.
            OldStart: int
            /// First hidden new-file line.
            NewStart: int
            /// Hidden line count, when known (a trailing gap is unknown until full context loads).
            HiddenCount: int option
        }

    type ExpandDirection =
        /// Reveal lines directly below the preceding visible content.
        | Down
        /// Reveal lines directly above the following visible content.
        | Up
        /// Reveal the complete gap.
        | All

    type DiffBlock =
        | GapBlock of DiffGap
        | HunkBlock of DiffHunk

    let private isDevNull (path: string) = path = "/dev/null"

    let private hunkHeaderPattern = Regex("^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@(.*)$", RegexOptions.Compiled)

    type private HunkBounds =
        {
            OldFirst: int
            OldAfter: int
            NewFirst: int
            NewAfter: int
        }

    let private hunkBounds (hunk: DiffHunk) =
        let oldCount = hunk.Lines |> List.sumBy (fun line -> if line.Type = Added || line.Type = Header then 0 else 1)
        let newCount = hunk.Lines |> List.sumBy (fun line -> if line.Type = Removed || line.Type = Header then 0 else 1)

        let fromLines () =
            let oldFirst = hunk.Lines |> List.tryPick _.OldLineNo
            let newFirst = hunk.Lines |> List.tryPick _.NewLineNo
            match oldFirst, newFirst with
            | Some o, Some n -> Some(o, n)
            | Some o, None -> Some(o, o)
            | None, Some n -> Some(n, n)
            | None, None -> None

        let starts =
            let matched = hunkHeaderPattern.Match hunk.Header
            if matched.Success then
                // A zero-count side names the line before the (empty) region.
                let oldStart = int matched.Groups.[1].Value
                let newStart = int matched.Groups.[3].Value
                Some((if oldCount = 0 then oldStart + 1 else oldStart), (if newCount = 0 then newStart + 1 else newStart))
            else
                fromLines ()

        starts
        |> Option.map (fun (oldFirst, newFirst) ->
            {
                OldFirst = oldFirst
                OldAfter = oldFirst + oldCount
                NewFirst = newFirst
                NewAfter = newFirst + newCount
            })

    let private headerSuffix (header: string) =
        let matched = hunkHeaderPattern.Match header
        if matched.Success then matched.Groups.[5].Value else ""

    /// Sorts ranges and merges overlapping or adjacent ones.
    let normalize (ranges: LineRange list) =
        ranges
        |> List.filter (fun range -> range.End >= range.Start)
        |> List.sortBy _.Start
        |> List.fold
            (fun merged range ->
                match merged with
                | last :: rest when int64 range.Start <= int64 last.End + 1L ->
                    { last with End = max last.End range.End } :: rest
                | _ -> range :: merged)
            []
        |> List.rev

    let addRange (range: LineRange) (ranges: LineRange list) = normalize (range :: ranges)

    let contains (line: int) (ranges: LineRange list) =
        ranges |> List.exists (fun range -> line >= range.Start && line <= range.End)

    /// The new-file range an expansion request reveals.
    let revealRange (direction: ExpandDirection) (gap: DiffGap) : LineRange option =
        let lastHidden = gap.HiddenCount |> Option.map (fun count -> gap.NewStart + count - 1)

        match direction, lastHidden with
        | _, Some last when last < gap.NewStart -> None
        | Down, Some last -> Some { Start = gap.NewStart; End = min last (gap.NewStart + StepLines - 1) }
        | Down, None -> Some { Start = gap.NewStart; End = gap.NewStart + StepLines - 1 }
        | Up, Some last -> Some { Start = max gap.NewStart (last - StepLines + 1); End = last }
        | Up, None -> None
        | All, Some last -> Some { Start = gap.NewStart; End = last }
        | All, None -> Some { Start = gap.NewStart; End = Int32.MaxValue }

    /// Directions a gap offers as explicit controls.
    let availableDirections (gap: DiffGap) =
        match gap.Kind, gap.HiddenCount with
        | _, Some count when count <= StepLines -> [ All ]
        | Leading, _ -> [ Up; All ]
        | Internal, _ -> [ Down; Up; All ]
        | Trailing, _ -> [ Down; All ]

    let private gap (file: FileDiff) kind oldStart newStart hiddenCount =
        {
            OldPath = file.OldPath
            NewPath = file.NewPath
            Kind = kind
            OldStart = oldStart
            NewStart = newStart
            HiddenCount = hiddenCount
        }

    /// Blocks for a configured-context diff when no full context is loaded.
    let private projectWithoutContext (file: FileDiff) =
        let hasSurroundingContext = not (isDevNull file.OldPath || isDevNull file.NewPath)
        let bounded = file.Hunks |> List.map (fun hunk -> hunk, hunkBounds hunk)

        let leading =
            match bounded with
            | (_, Some first) :: _ when hasSurroundingContext && first.NewFirst > 1 ->
                [ GapBlock(gap file Leading 1 1 (Some(first.NewFirst - 1))) ]
            | _ -> []

        let body =
            bounded
            |> List.pairwise
            |> List.collect (fun ((_, previous), (next, nextBounds)) ->
                match previous, nextBounds with
                | Some p, Some n when n.NewFirst > p.NewAfter ->
                    [ GapBlock(gap file Internal p.OldAfter p.NewAfter (Some(n.NewFirst - p.NewAfter))); HunkBlock next ]
                | _ -> [ HunkBlock next ])

        let trailing =
            match List.tryLast bounded with
            | Some(_, Some last) when hasSurroundingContext ->
                match file.NewLineCount with
                | Some lineCount when lineCount < last.NewAfter -> []
                | Some lineCount -> [ GapBlock(gap file Trailing last.OldAfter last.NewAfter (Some(lineCount - last.NewAfter + 1))) ]
                | None -> [ GapBlock(gap file Trailing last.OldAfter last.NewAfter None) ]
            | _ -> []

        match bounded with
        | [] -> []
        | (first, _) :: _ -> leading @ (HunkBlock first :: body) @ trailing

    let private synthesizeHunk (lines: DiffLine list) (suffix: string) =
        let oldLines = lines |> List.choose _.OldLineNo
        let newLines = lines |> List.choose _.NewLineNo
        let side (numbers: int list) =
            match numbers with
            | [] -> "0,0"
            | first :: _ -> $"{first},{numbers.Length}"
        { Header = "@@ -" + side oldLines + " +" + side newLines + " @@" + suffix; Lines = lines }

    /// Projects a configured-context file diff plus optional full-file context and revealed ranges
    /// into ordered hunk and gap blocks. Without full context the configured hunks are returned unchanged.
    let project (file: FileDiff) (fullContext: FileDiff option) (revealed: LineRange list) : DiffBlock list =
        let fullLines =
            fullContext
            |> Option.map (fun context -> context.Hunks |> List.collect _.Lines |> List.filter (fun line -> line.Type <> Header))
            |> Option.defaultValue []

        if List.isEmpty file.Hunks || List.isEmpty fullLines then
            projectWithoutContext file
        else
            let bounded = file.Hunks |> List.choose (fun hunk -> hunkBounds hunk |> Option.map (fun b -> hunk, b))
            let revealed = normalize revealed

            let isVisible (line: DiffLine) =
                match line.Type, line.NewLineNo with
                | Context, Some number ->
                    contains number revealed
                    || bounded |> List.exists (fun (_, b) -> number >= b.NewFirst && number < b.NewAfter)
                | Context, None -> false
                | _ -> true

            let suffixFor (segment: DiffLine list) =
                let first = segment |> List.tryPick _.NewLineNo
                let last = segment |> List.rev |> List.tryPick _.NewLineNo
                bounded
                |> List.tryFind (fun (_, b) ->
                    match first, last with
                    | Some f, Some l -> b.NewFirst >= f && b.NewFirst <= l + 1
                    | _ -> true)
                |> Option.map (fun (hunk, _) -> headerSuffix hunk.Header)
                |> Option.defaultValue ""

            // Split full-context lines into alternating visible segments and hidden runs.
            let runs =
                fullLines
                |> List.fold
                    (fun acc line ->
                        let visible = isVisible line
                        match acc with
                        | (runVisible, lines) :: rest when runVisible = visible -> (runVisible, line :: lines) :: rest
                        | _ -> (visible, [ line ]) :: acc)
                    []
                |> List.rev
                |> List.map (fun (visible, lines) -> visible, List.rev lines)

            let lastIndex = runs.Length - 1

            runs
            |> List.mapi (fun index (visible, lines) ->
                if visible then
                    HunkBlock(synthesizeHunk lines (suffixFor lines))
                else
                    let first = List.head lines
                    let kind =
                        if index = 0 then Leading
                        elif index = lastIndex then Trailing
                        else Internal
                    GapBlock(
                        gap file kind
                            (first.OldLineNo |> Option.defaultValue 0)
                            (first.NewLineNo |> Option.defaultValue 0)
                            (Some lines.Length)))
