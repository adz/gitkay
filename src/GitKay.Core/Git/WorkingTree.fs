namespace GitKay.Core

open System

/// <summary>
/// Changes not yet committed: what <c>git status</c> reports, split into the staged, unstaged and untracked sections
/// shown above the history.
/// </summary>
module WorkingTree =

    /// <summary>What happened to a path on one side (index against HEAD, or working tree against index).</summary>
    type Change =
        | Unchanged
        | Modified
        | Added
        | Deleted
        | Renamed
        | Copied
        | TypeChanged
        /// <summary>A merge conflict the index hasn't resolved.</summary>
        | Conflicted

    type Entry =
        { Path: string
          /// <summary>The path before a rename or copy in the index; the path itself otherwise.</summary>
          OriginalPath: string
          Staged: Change
          Unstaged: Change
          Untracked: bool }

    type Section =
        | Staged
        | Unstaged
        | Untracked

    let private change (code: char) =
        match code with
        | 'M' -> Modified
        | 'A' -> Added
        | 'D' -> Deleted
        | 'R' -> Renamed
        | 'C' -> Copied
        | 'T' -> TypeChanged
        | 'U' -> Conflicted
        | _ -> Unchanged

    /// <summary>
    /// Entries from <c>git status --porcelain=v2 -z --untracked-files=all</c>. Ignored files and header lines are skipped;
    /// a record git adds in a later version is skipped rather than failing the whole status.
    /// </summary>
    let parseStatus (output: string) : Entry list =
        let fields = if String.IsNullOrEmpty output then [] else output.Split('\000', StringSplitOptions.None) |> List.ofArray
        // Ordinary and unmerged records carry a fixed number of space-separated fields before the path, which may
        // itself contain spaces.
        let pathAfter (fieldCount: int) (record: string) =
            let mutable index = 0
            let mutable spaces = 0
            while index < record.Length && spaces < fieldCount do
                if record[index] = ' ' then spaces <- spaces + 1
                index <- index + 1
            if spaces = fieldCount then Some(record.Substring index) else None

        let rec read (remaining: string list) (entries: Entry list) =
            match remaining with
            | [] -> List.rev entries
            | "" :: rest -> read rest entries
            | record :: rest when record.StartsWith "? " ->
                let path = record.Substring 2
                read rest ({ Path = path; OriginalPath = path; Staged = Unchanged; Unstaged = Unchanged; Untracked = true } :: entries)
            | record :: rest when record.StartsWith "1 " && record.Length > 4 ->
                match pathAfter 8 record with
                | Some path ->
                    let entry = { Path = path; OriginalPath = path; Staged = change record[2]; Unstaged = change record[3]; Untracked = false }
                    read rest (entry :: entries)
                | None -> read rest entries
            | record :: original :: rest when record.StartsWith "2 " && record.Length > 4 ->
                match pathAfter 9 record with
                | Some path ->
                    let entry = { Path = path; OriginalPath = original; Staged = change record[2]; Unstaged = change record[3]; Untracked = false }
                    read rest (entry :: entries)
                | None -> read rest entries
            | record :: rest when record.StartsWith "u " && record.Length > 4 ->
                match pathAfter 10 record with
                | Some path ->
                    let entry = { Path = path; OriginalPath = path; Staged = Conflicted; Unstaged = Conflicted; Untracked = false }
                    read rest (entry :: entries)
                | None -> read rest entries
            | _ :: rest -> read rest entries

        read fields []

    /// <summary>Whether an entry has changes in a section.</summary>
    let isIn (section: Section) (entry: Entry) =
        match section with
        | Staged -> entry.Staged <> Unchanged
        | Unstaged -> not entry.Untracked && entry.Unstaged <> Unchanged
        | Untracked -> entry.Untracked

    /// <summary>The sections with changes, in display order, each with its entries in path order.</summary>
    let sections (entries: Entry list) : (Section * Entry list) list =
        [ Staged; Unstaged; Untracked ]
        |> List.choose (fun section ->
            match entries |> List.filter (isIn section) |> List.sortWith (fun a b -> String.CompareOrdinal(a.Path, b.Path)) with
            | [] -> None
            | inSection -> Some(section, inSection))

    /// <summary>Reads a section back from <see cref="sectionName"/>.</summary>
    let tryParseSection name =
        match name with
        | "Staged" -> Some Staged
        | "Unstaged" -> Some Unstaged
        | "Untracked" -> Some Untracked
        | _ -> None

    let sectionName section =
        match section with
        | Staged -> "Staged"
        | Unstaged -> "Unstaged"
        | Untracked -> "Untracked"

    /// <summary>"2 staged · 1 unstaged · 3 untracked", naming only the sections with changes; empty when clean.</summary>
    let summary (entries: Entry list) =
        sections entries
        |> List.map (fun (section, inSection) -> $"{inSection.Length} {(sectionName section).ToLowerInvariant()}")
        |> String.concat " · "

    /// <summary>A file's marker in the all-files tree: ● staged, ○ unstaged, ◐ both, + untracked; empty when unchanged.</summary>
    let marker (entry: Entry) =
        match isIn Staged entry, isIn Unstaged entry, entry.Untracked with
        | _, _, true -> "+"
        | true, true, _ -> "◐"
        | true, false, _ -> "●"
        | false, true, _ -> "○"
        | _ -> ""

    /// <summary>An untracked file as a diff adding every line; None for binary content.</summary>
    let untrackedDiff (path: string) (content: string) : Models.FileDiff =
        let isBinary = content.Contains '\000'
        let lines =
            if isBinary || content.Length = 0 then [||]
            else
                let trimmed = if content.EndsWith "\n" then content.Substring(0, content.Length - 1) else content
                trimmed.Split '\n' |> Array.map (fun line -> line.TrimEnd '\r')
        let hunks : Models.DiffHunk list =
            if lines.Length = 0 then []
            else
                [ { Header = $"@@ -0,0 +1,{lines.Length} @@"
                    Lines =
                      lines
                      |> Array.mapi (fun index line ->
                          ({ Type = Models.Added; Content = line; OldLineNo = None; NewLineNo = Some(index + 1) }: Models.DiffLine))
                      |> List.ofArray } ]
        { OldPath = "/dev/null"; NewPath = path; Hunks = hunks; NewLineCount = Some lines.Length }

    /// <summary>
    /// File diffs from <c>git diff</c> output (with a/ and b/ prefixes). Hunks are read by their line counts, so
    /// content that looks like a header (a removed line "-- x", an added "++ y") stays content, and paths may contain
    /// spaces. Pure renames and mode changes, which have no hunks, keep their paths from the header lines.
    /// </summary>
    let parsePatch (output: string) : Models.FileDiff list =
        let lines = if String.IsNullOrEmpty output then [||] else output.Replace("\r\n", "\n").Split '\n'
        let stripPrefix (prefix: string) (path: string) =
            let path = path.TrimEnd '\t'
            if path = "/dev/null" then path elif path.StartsWith prefix then path.Substring prefix.Length else path
        // "diff --git a/<p> b/<p>": without rename lines both halves name the same path, so split at the middle.
        let headerPaths (line: string) =
            let rest = line.Substring "diff --git ".Length
            let middle = (rest.Length - 1) / 2
            if rest.Length % 2 = 1 && rest[middle] = ' ' && rest.StartsWith "a/" && rest.Substring(middle + 1).StartsWith "b/" then
                rest.Substring(2, middle - 2), rest.Substring(middle + 3)
            else
                rest, rest
        let hunkCounts (header: string) =
            let m = Text.RegularExpressions.Regex.Match(header, @"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@")
            if not m.Success then None
            else
                let number (group: int) fallback = if m.Groups[group].Success then int m.Groups[group].Value else fallback
                Some(number 1 0, number 2 1, number 3 0, number 4 1)

        let files = ResizeArray<Models.FileDiff>()
        let mutable index = 0
        while index < lines.Length do
            let line = lines[index]
            if not (line.StartsWith "diff --git ") then
                index <- index + 1
            else
                let headerOld, headerNew = headerPaths line
                let mutable oldPath = headerOld
                let mutable newPath = headerNew
                let hunks = ResizeArray<Models.DiffHunk>()
                index <- index + 1
                while index < lines.Length && not (lines[index].StartsWith "diff --git ") do
                    let current = lines[index]
                    if current.StartsWith "rename from " then oldPath <- current.Substring "rename from ".Length
                    elif current.StartsWith "rename to " then newPath <- current.Substring "rename to ".Length
                    elif current.StartsWith "new file mode" then oldPath <- "/dev/null"
                    elif current.StartsWith "deleted file mode" then newPath <- "/dev/null"
                    elif current.StartsWith "--- " then oldPath <- stripPrefix "a/" (current.Substring 4)
                    elif current.StartsWith "+++ " then newPath <- stripPrefix "b/" (current.Substring 4)

                    match (if current.StartsWith "@@" then hunkCounts current else None) with
                    | Some(oldStart, oldCount, newStart, newCount) ->
                        let body = ResizeArray<Models.DiffLine>()
                        let mutable oldLine = oldStart
                        let mutable newLine = newStart
                        let mutable oldLeft = oldCount
                        let mutable newLeft = newCount
                        index <- index + 1
                        while index < lines.Length && (oldLeft > 0 || newLeft > 0 || (lines[index].StartsWith "\\")) do
                            let text = lines[index]
                            let content = if text.Length > 0 then text.Substring 1 else ""
                            match (if text.Length = 0 then ' ' else text[0]) with
                            | '+' ->
                                body.Add { Type = Models.Added; Content = content; OldLineNo = None; NewLineNo = Some newLine }
                                newLine <- newLine + 1
                                newLeft <- newLeft - 1
                            | '-' ->
                                body.Add { Type = Models.Removed; Content = content; OldLineNo = Some oldLine; NewLineNo = None }
                                oldLine <- oldLine + 1
                                oldLeft <- oldLeft - 1
                            | '\\' -> () // "\ No newline at end of file"
                            | _ ->
                                body.Add { Type = Models.Context; Content = content; OldLineNo = Some oldLine; NewLineNo = Some newLine }
                                oldLine <- oldLine + 1
                                newLine <- newLine + 1
                                oldLeft <- oldLeft - 1
                                newLeft <- newLeft - 1
                            index <- index + 1
                        hunks.Add { Header = current; Lines = List.ofSeq body }
                    | None -> index <- index + 1
                files.Add { OldPath = oldPath; NewPath = newPath; Hunks = List.ofSeq hunks; NewLineCount = None }
        List.ofSeq files
