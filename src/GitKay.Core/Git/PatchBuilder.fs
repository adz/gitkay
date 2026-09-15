namespace GitKay.Core

open System

/// <summary>
/// Builds a patch holding only some of a file's changed lines, as git gui does, so hunks and single lines can be
/// staged, unstaged or discarded with <c>git apply</c>.
/// </summary>
module PatchBuilder =

    /// <summary>
    /// How the patch will be applied. <c>Forward</c> patches come from the unstaged diff and apply as they are (stage,
    /// or discard with <c>--reverse</c> on the working tree); <c>Reverse</c> patches come from the staged diff and apply
    /// with <c>--reverse</c> to the index (unstage).
    /// </summary>
    type Direction =
        | Forward
        | Reverse

    /// <summary>A changed line chosen in the shown diff: its hunk, its position among the hunk's lines, and what it says.</summary>
    type SelectedLine =
        { Hunk: int
          Line: int
          Type: Models.LineType
          Content: string }

    type BuildError =
        /// <summary>The file's diff no longer has the chosen line there: it changed since it was shown.</summary>
        | DiffChanged
        /// <summary>None of the chosen lines are changes.</summary>
        | NothingSelected

    let describeError error =
        match error with
        | DiffChanged -> "The file changed since its diff was shown; rescan and try again."
        | NothingSelected -> "No changed lines are selected."

    type private RawHunk =
        { OldStart: int
          NewStart: int
          Lines: string list }

    let private hunkHeader = Text.RegularExpressions.Regex(@"^@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@(.*)$")

    /// Splits one file's raw patch into its header lines (up to the first hunk) and its hunks.
    let private split (patch: string) =
        let lines = patch.Replace("\r\n", "\n").Split '\n' |> Array.toList
        let lines = match List.rev lines with "" :: rest -> List.rev rest | _ -> lines
        let header = lines |> List.takeWhile (fun line -> not (line.StartsWith "@@"))
        let rec hunks (remaining: string list) acc =
            match remaining with
            | [] -> List.rev acc
            | head :: rest ->
                let m = hunkHeader.Match head
                let body = rest |> List.takeWhile (fun line -> not (line.StartsWith "@@"))
                let after = rest |> List.skip body.Length
                if m.Success then
                    hunks after ({ OldStart = int m.Groups[1].Value; NewStart = int m.Groups[2].Value; Lines = body } :: acc)
                else
                    hunks after acc
        header, hunks (lines |> List.skip header.Length) []

    let private count (prefix: char) (lines: string list) =
        lines |> List.filter (fun line -> line.Length > 0 && (line[0] = prefix || line[0] = ' ')) |> List.length

    let private range (start: int) (length: int) =
        // A zero-length range names the line before it, as git prints them.
        if length = 1 then string start else $"{start},{length}"

    /// <summary>
    /// The patch for <paramref name="selected"/> lines of one file's raw <c>git diff</c> output. Lines are numbered as the
    /// diff parser numbers them: per hunk, counting every line except "\ No newline at end of file" markers.
    /// </summary>
    let build (direction: Direction) (patch: string) (selected: SelectedLine list) : Result<string, BuildError> =
        let header, hunks = split patch
        let chosen = selected |> List.map (fun line -> (line.Hunk, line.Line), line) |> Map.ofList

        let verified =
            selected
            |> List.forall (fun line ->
                match List.tryItem line.Hunk hunks with
                | None -> false
                | Some hunk ->
                    let numbered = hunk.Lines |> List.filter (fun text -> not (text.StartsWith "\\"))
                    match List.tryItem line.Line numbered with
                    | Some text when text.Length > 0 ->
                        let kind = match text[0] with '+' -> Models.Added | '-' -> Models.Removed | _ -> Models.Context
                        kind = line.Type && text.Substring 1 = line.Content
                    | _ -> false)

        if not verified then
            Error DiffChanged
        else
            // The side the patch is matched against keeps its numbering; the other side's start moves by what
            // earlier emitted hunks added or removed.
            let mutable delta = 0
            let built = ResizeArray<string>()

            hunks
            |> List.iteri (fun hunkIndex hunk ->
                let lines = ResizeArray<string>()
                let mutable lineIndex = 0
                let mutable previousKept = true
                let mutable hasChange = false

                for text in hunk.Lines do
                    if text.StartsWith "\\" then
                        if previousKept then lines.Add text
                    else
                        let isChosen = chosen.ContainsKey(hunkIndex, lineIndex)
                        let body = if text.Length > 0 then text.Substring 1 else ""
                        let kept =
                            match (if text.Length = 0 then ' ' else text[0]), direction, isChosen with
                            | '+', _, true -> hasChange <- true; Some text
                            | '-', _, true -> hasChange <- true; Some text
                            | '+', Forward, false -> None
                            | '-', Forward, false -> Some(" " + body)
                            | '+', Reverse, false -> Some(" " + body)
                            | '-', Reverse, false -> None
                            | _ -> Some(" " + body)
                        previousKept <- kept.IsSome
                        kept |> Option.iter lines.Add
                        lineIndex <- lineIndex + 1

                if hasChange then
                    let lines = List.ofSeq lines
                    let oldCount = count '-' lines
                    let newCount = count '+' lines
                    let oldStart, newStart =
                        match direction with
                        | Forward ->
                            let anchor = if oldCount = 0 then hunk.OldStart + 1 else hunk.OldStart
                            let newStart = anchor + delta
                            hunk.OldStart, (if newCount = 0 then newStart - 1 else newStart)
                        | Reverse ->
                            let anchor = if newCount = 0 then hunk.NewStart + 1 else hunk.NewStart
                            let oldStart = anchor - delta
                            (if oldCount = 0 then oldStart - 1 else oldStart), hunk.NewStart
                    delta <- delta + newCount - oldCount
                    built.Add $"@@ -{range oldStart oldCount} +{range newStart newCount} @@"
                    built.AddRange lines)

            if built.Count = 0 then Error NothingSelected
            else Ok(String.Join("\n", Seq.append header built) + "\n")
