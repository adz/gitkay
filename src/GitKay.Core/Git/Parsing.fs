namespace GitKay.Core

open System
open System.Text.RegularExpressions
open GitKay.Core.Models

/// Pure adapters from Git textual formats into the core model.
module GitParsing =
    let parseCommitLine (line: string) : Models.Commit option =
        let parts = line.TrimEnd('\r').Split('|')
        if parts.Length >= 6 then
            Some {
                Hash = parts.[0]
                Timestamp = int64 parts.[1]
                AuthorName = parts.[2]
                AuthorEmail = parts.[3]
                Parents = parts.[4].Split(' ', System.StringSplitOptions.RemoveEmptyEntries) |> Array.toList
                Subject = parts.[5]
                Message = parts.[5]
                Refs = []
            }
        else
            None

    let private parseDiffHeader (line: string) =
        if line.StartsWith("diff --git ") then
            let parts = line.Split(' ', System.StringSplitOptions.RemoveEmptyEntries)
            if parts.Length >= 4 then
                Some(parts.[2], parts.[3])
            else
                None
        else
            None

    let private parseHunkHeader (line: string) =
        let matchResult = Regex.Match(line, "^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@")
        if matchResult.Success then
            let oldStart = int matchResult.Groups.[1].Value
            let newStart = int matchResult.Groups.[3].Value
            Some(oldStart, newStart)
        else
            None

    let parseBlamePorcelain (output: string) : Map<int, BlameInfo> =
        let lines = output.Replace("\r", "").Split('\n', System.StringSplitOptions.RemoveEmptyEntries)
        let mutable index = 0
        let mutable results = []

        while index < lines.Length do
            let headerParts = lines.[index].Split(' ', System.StringSplitOptions.RemoveEmptyEntries)
            if headerParts.Length >= 3 then
                let commitHash = headerParts.[0]
                let finalLineNumber = int headerParts.[2]
                let mutable authorName = ""
                let mutable authorEmail = ""
                let mutable authorTimestamp = 0L

                index <- index + 1

                while index < lines.Length && not (Regex.IsMatch(lines.[index], "^[0-9a-fA-F]{7,40}\\s")) do
                    let metadata = lines.[index]
                    if metadata.StartsWith("author ") then
                        authorName <- metadata.Substring(7)
                    elif metadata.StartsWith("author-mail ") then
                        authorEmail <- metadata.Substring(12).Trim().Trim('<', '>')
                    elif metadata.StartsWith("author-time ") then
                        authorTimestamp <- int64 (metadata.Substring(12).Trim())
                    index <- index + 1

                results <- (finalLineNumber, {
                    Hash = commitHash
                    AuthorName = authorName
                    AuthorEmail = authorEmail
                    Timestamp = authorTimestamp
                }) :: results
            else
                index <- index + 1

        results |> List.rev |> Map.ofList

    let toCommitModel (commit: LibGit2Sharp.Commit) (refs: Models.CommitRef list) : Models.Commit =
        {
            Hash = commit.Sha
            Timestamp = commit.Author.When.ToUnixTimeSeconds()
            AuthorName = commit.Author.Name
            AuthorEmail = commit.Author.Email
            Parents = commit.Parents |> Seq.map (fun parent -> parent.Sha) |> Seq.toList
            Subject = commit.MessageShort
            Message = commit.Message
            Refs = refs
        }

    let parseDiff (output: string) : FileDiff list =
        let lines = output.Replace("\r", "").Split('\n')
        let mutable files = []
        let mutable currentFile : FileDiff option = None
        let mutable currentHunk : DiffHunk option = None
        let mutable oldLineNumber = 0
        let mutable newLineNumber = 0

        let flushHunk () =
            currentHunk
            |> Option.iter (fun h ->
                currentFile <- currentFile |> Option.map (fun f -> { f with Hunks = h :: f.Hunks }))
            currentHunk <- None

        let flushFile () =
            flushHunk ()
            currentFile |> Option.iter (fun f -> files <- f :: files)
            currentFile <- None

        for line in lines do
            if line.StartsWith("diff --git") then
                flushFile ()
                currentFile <-
                    match parseDiffHeader line with
                    | Some(oldPath, newPath) -> Some { OldPath = oldPath; NewPath = newPath; Hunks = [] }
                    | None -> Some { OldPath = ""; NewPath = ""; Hunks = [] }
                oldLineNumber <- 0
                newLineNumber <- 0
            elif line.StartsWith("--- ") then
                currentFile <-
                    currentFile
                    |> Option.map (fun f ->
                        let oldPath =
                            if line = "--- /dev/null" then "/dev/null"
                            elif line.StartsWith("--- a/") then line.Substring(6)
                            else line.Substring(4)

                        { f with OldPath = oldPath })
            elif line.StartsWith("+++ ") then
                currentFile <-
                    currentFile
                    |> Option.map (fun f ->
                        let newPath =
                            if line = "+++ /dev/null" then "/dev/null"
                            elif line.StartsWith("+++ b/") then line.Substring(6)
                            else line.Substring(4)

                        { f with NewPath = newPath })
            elif line.StartsWith("@@") then
                flushHunk ()
                match parseHunkHeader line with
                | Some(oldStart, newStart) ->
                    oldLineNumber <- oldStart
                    newLineNumber <- newStart
                    currentHunk <- Some { Header = line; Lines = [] }
                | None ->
                    currentHunk <- Some { Header = line; Lines = [] }
            elif line.StartsWith("+") && not (line.StartsWith("+++")) then
                currentHunk <-
                    currentHunk
                    |> Option.map (fun h ->
                        let diffLine : DiffLine =
                            {
                                Type = Added
                                Content = line.Substring(1)
                                OldLineNo = None
                                NewLineNo = Some newLineNumber
                            }

                        { h with Lines = diffLine :: h.Lines })
                newLineNumber <- newLineNumber + 1
            elif line.StartsWith("-") && not (line.StartsWith("---")) then
                currentHunk <-
                    currentHunk
                    |> Option.map (fun h ->
                        let diffLine : DiffLine =
                            {
                                Type = Removed
                                Content = line.Substring(1)
                                OldLineNo = Some oldLineNumber
                                NewLineNo = None
                            }

                        { h with Lines = diffLine :: h.Lines })
                oldLineNumber <- oldLineNumber + 1
            elif line.StartsWith(" ") then
                currentHunk <-
                    currentHunk
                    |> Option.map (fun h ->
                        let diffLine : DiffLine =
                            {
                                Type = Context
                                Content = line.Substring(1)
                                OldLineNo = Some oldLineNumber
                                NewLineNo = Some newLineNumber
                            }

                        {
                            h with
                                Lines = diffLine :: h.Lines
                        })
                oldLineNumber <- oldLineNumber + 1
                newLineNumber <- newLineNumber + 1

        flushFile ()

        files
        |> List.rev
        |> List.map (fun f -> { f with Hunks = f.Hunks |> List.rev |> List.map (fun h -> { h with Lines = h.Lines |> List.rev }) })

