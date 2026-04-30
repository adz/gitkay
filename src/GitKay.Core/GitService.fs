namespace GitKay.Core

open System
open System.Diagnostics
open System.Text.RegularExpressions
open GitKay.Core.Models
open LibGit2Sharp

module GitService =

    let private logTiming (message: string) =
        let line = sprintf "[timing] %s" message
        Trace.WriteLine line
        try
            Console.Error.WriteLine line
        with _ ->
            ()

    let private discoverRepositoryPath () =
        Repository.Discover(Environment.CurrentDirectory)

    let private withRepository (action: Repository -> Result<'T, string>) =
        try
            let repoPath = discoverRepositoryPath ()

            if String.IsNullOrWhiteSpace repoPath then
                Error "Could not locate a Git repository."
            else
                use repo = new Repository(repoPath)
                action repo
        with ex ->
            Error ex.Message

    let private quoteArg (value: string) =
        "\"" + value.Replace("\"", "\\\"") + "\""

    let executeGitCommand (args: string) =
        let startInfo = ProcessStartInfo("git", args)
        startInfo.RedirectStandardOutput <- true
        startInfo.RedirectStandardError <- true
        startInfo.UseShellExecute <- false
        startInfo.CreateNoWindow <- true
        
        use process' = new Process()
        process'.StartInfo <- startInfo
        process'.Start() |> ignore
        
        let output = process'.StandardOutput.ReadToEnd()
        let error = process'.StandardError.ReadToEnd()
        process'.WaitForExit()
        
        if process'.ExitCode <> 0 then
            Error error
        else
            Ok output

    let private getFirstParent (hash: string) =
        match executeGitCommand (sprintf "rev-list --parents -n 1 %s" hash) with
        | Ok output ->
            output.Trim().Split(' ', System.StringSplitOptions.RemoveEmptyEntries)
            |> Array.tryItem 1
        | Error _ ->
            None

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

    let private fetchBlameRange (revision: string) (path: string) (startLine: int) (endLine: int) =
        if startLine > endLine || path = "/dev/null" then
            Map.empty
        else
            let args =
                sprintf "blame --line-porcelain -L %d,%d %s -- %s" startLine endLine revision (quoteArg path)

            match executeGitCommand args with
            | Ok output -> parseBlamePorcelain output
            | Error _ -> Map.empty

    let private toBlamedDiffLine (line: DiffLine) =
        {
            Line = line
            Blame = None
        }

    let private toBlamedDiffHunk (hunk: DiffHunk) =
        {
            Header = hunk.Header
            Lines = hunk.Lines |> List.map toBlamedDiffLine
        }

    let private toBlamedFileDiff (file: FileDiff) =
        {
            OldPath = file.OldPath
            NewPath = file.NewPath
            Hunks = file.Hunks |> List.map toBlamedDiffHunk
        }

    let private toCommitModel (commit: LibGit2Sharp.Commit) : Models.Commit =
        {
            Hash = commit.Sha
            Timestamp = commit.Author.When.ToUnixTimeSeconds()
            AuthorName = commit.Author.Name
            AuthorEmail = commit.Author.Email
            Parents = commit.Parents |> Seq.map (fun parent -> parent.Sha) |> Seq.toList
            Subject = commit.MessageShort
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

    let fetchHistory () =
        withRepository (fun repo ->
            let filter = CommitFilter()
            filter.IncludeReachableFrom <- repo.Refs
            filter.SortBy <- CommitSortStrategies.Topological ||| CommitSortStrategies.Time

            repo.Commits.QueryBy(filter)
            |> Seq.map toCommitModel
            |> Seq.toList
            |> Ok)

    let fetchDiff (hash: string) =
        let startedAtTicks = Stopwatch.GetTimestamp()

        let result =
            withRepository (fun repo ->
                let commit = repo.Lookup<LibGit2Sharp.Commit>(hash)

                if isNull commit then
                    Error (sprintf "Commit not found: %s" hash)
                else
                    use patch =
                        match commit.Parents |> Seq.tryHead with
                        | Some parent -> repo.Diff.Compare<Patch>(parent.Tree, commit.Tree)
                        | None -> repo.Diff.Compare<Patch>(null, commit.Tree)

                    Ok (parseDiff patch.Content))

        let elapsed = Stopwatch.GetElapsedTime(startedAtTicks)

        match result with
        | Ok files ->
            logTiming (sprintf "diff load hash=%s elapsed=%.1fms files=%d" hash elapsed.TotalMilliseconds files.Length)
            Ok files
        | Error err ->
            logTiming (sprintf "diff load hash=%s elapsed=%.1fms error=%s" hash elapsed.TotalMilliseconds err)
            Error err

    let fetchDiffWithBlame (hash: string) =
        match fetchDiff hash with
        | Error err -> Error err
        | Ok files ->
            Ok (files |> List.map toBlamedFileDiff)

    let createTag (hash: string) (name: string) =
        executeGitCommand (sprintf "tag %s %s" name hash)

    let createBranch (hash: string) (name: string) =
        executeGitCommand (sprintf "branch %s %s" name hash)

    let cherryPick (hash: string) =
        executeGitCommand (sprintf "cherry-pick %s" hash)

    let resetTo (hash: string) (hard: bool) =
        let mode = if hard then "--hard" else "--soft"
        executeGitCommand (sprintf "reset %s %s" mode hash)

    let revert (hash: string) =
        executeGitCommand (sprintf "revert --no-edit %s" hash)
