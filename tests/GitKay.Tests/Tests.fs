namespace GitKay.Tests

open System
open System.Collections.Concurrent
open System.IO
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open Avalonia.Media
open LibGit2Sharp
open GitKay.Core
open GitKay.UI

module private RefHelpers =
    let commitRef (kind: Models.CommitRefKind) (name: string) : Models.CommitRef = { Name = name; Kind = kind }
    let branchRef name = commitRef Models.CommitRefKind.Branch name
    let remoteRef name = commitRef Models.CommitRefKind.Remote name
    let tagRef name = commitRef Models.CommitRefKind.Tag name
    let stashRef name = commitRef Models.CommitRefKind.Stash name

module GitServiceTests =

    let private sampleFile oldPath newPath lines : Models.FileDiff =
        {
            OldPath = oldPath
            NewPath = newPath
            Hunks =
                [
                    {
                        Header = "@@ -1 +1 @@"
                        Lines = lines
                    }
                ]
        }

    let private withTempRepository (action: string -> Repository -> 'T) =
        let root = Path.Combine(Path.GetTempPath(), "gitkay-tests-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore

        try
            Repository.Init(root) |> ignore
            use repo = new Repository(root)
            repo.Config.Add("user.name", "GitKay Tests") |> ignore
            repo.Config.Add("user.email", "gitkay@example.com") |> ignore
            let previousDirectory = Environment.CurrentDirectory
            Environment.CurrentDirectory <- root

            try
                action root repo
            finally
                Environment.CurrentDirectory <- previousDirectory
        finally
            try
                Directory.Delete(root, true)
            with _ ->
                ()

    let private writeFile (root: string) (relativePath: string) (contents: string) =
        let fullPath = Path.Combine(root, relativePath)
        let directory = Path.GetDirectoryName(fullPath)

        if not (String.IsNullOrWhiteSpace directory) then
            Directory.CreateDirectory(directory) |> ignore

        File.WriteAllText(fullPath, contents)

    let private commitFile (repo: Repository) (root: string) (relativePath: string) (contents: string) (message: string) =
        writeFile root relativePath contents
        Commands.Stage(repo, relativePath)
        let signature = Signature("GitKay Tests", "gitkay@example.com", DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero))
        repo.Commit(message, signature, signature)

    [<Fact>]
    let ``parseCommitLine should correctly parse a valid git log line`` () =
        let line = "1479a0237c09d57a408759556d11f0a2830f69a5|1746014400|Your Name|you@example.com|a1b2c3d4 e5f6g7h8|Initial commit"
        let result = GitService.parseCommitLine line
        
        test <@ result.IsSome @>
        let commit = result.Value
        test <@ commit.Hash = "1479a0237c09d57a408759556d11f0a2830f69a5" @>
        test <@ commit.Timestamp = 1746014400L @>
        test <@ commit.AuthorName = "Your Name" @>
        test <@ commit.AuthorEmail = "you@example.com" @>
        test <@ commit.Parents = ["a1b2c3d4"; "e5f6g7h8"] @>
        test <@ commit.Subject = "Initial commit" @>
        test <@ commit.Message = "Initial commit" @>
        test <@ commit.Refs = [] @>

    [<Fact>]
    let ``parseCommitLine should return None for invalid line`` () =
        let line = "invalid line"
        let result = GitService.parseCommitLine line
        test <@ result.IsNone @>

    [<Fact>]
    let ``parseDiff should assign line numbers within hunks`` () =
        let diff =
            """
diff --git a/foo.txt b/foo.txt
--- a/foo.txt
+++ b/foo.txt
@@ -1,3 +1,4 @@
 line1
-line2
+line2 changed
 line3
+line4
"""

        let result = GitService.parseDiff diff
        test <@ result.Length = 1 @>

        let file = result.Head
        test <@ file.OldPath = "foo.txt" @>
        test <@ file.NewPath = "foo.txt" @>

        let hunk = file.Hunks.Head
        let lines = hunk.Lines
        test <@ lines.[0].OldLineNo = Some 1 && lines.[0].NewLineNo = Some 1 @>
        test <@ lines.[1].OldLineNo = Some 2 && lines.[1].NewLineNo = None @>
        test <@ lines.[2].OldLineNo = None && lines.[2].NewLineNo = Some 2 @>
        test <@ lines.[3].OldLineNo = Some 3 && lines.[3].NewLineNo = Some 3 @>
        test <@ lines.[4].OldLineNo = None && lines.[4].NewLineNo = Some 4 @>

    [<Fact>]
    let ``DiffLineProjection should render plain line numbers`` () =
        let line : Models.DiffLine =
            {
                Type = Models.Context
                Content = " line"
                OldLineNo = Some 12
                NewLineNo = Some 34
            }

        let projection = DiffLineProjection line
        test <@ projection.OldLineNoText = "12" @>
        test <@ projection.NewLineNoText = "34" @>

        let addedLine : Models.DiffLine =
            {
                Type = Models.Added
                Content = "line"
                OldLineNo = None
                NewLineNo = Some 9
            }

        let addedProjection = DiffLineProjection addedLine
        test <@ addedProjection.OldLineNoText = "" @>
        test <@ addedProjection.NewLineNoText = "9" @>

    [<Fact>]
    let ``parseBlamePorcelain should map blame metadata by line`` () =
        let blame =
            """
abc123abc123abc123abc123abc123abc123abc1 10 20
author Jane Doe
author-mail <jane@example.com>
author-time 1710000000
author-tz +0000
summary Example line
\tline one
def456def456def456def456def456def456def4 11 21
author John Smith
author-mail <john@example.com>
author-time 1710003600
author-tz +0000
summary Another line
\tline two
"""

        let result = GitService.parseBlamePorcelain blame
        test <@ result.[20].Hash = "abc123abc123abc123abc123abc123abc123abc1" @>
        test <@ result.[20].AuthorName = "Jane Doe" @>
        test <@ result.[21].AuthorEmail = "john@example.com" @>

    [<Fact>]
    let ``parseStartupTargets should accept branch, sha, tag, and all`` () =
        let result =
            GitService.parseStartupTargets
                [|
                    "--branch"
                    "main"
                    "--sha=abc123"
                    "--tag"
                    "v1.0.0"
                |]

        match result with
        | Error err -> failwith err
        | Ok targets ->
            test <@ targets = [ GitService.StartupTarget.Branch "main"; GitService.StartupTarget.Sha "abc123"; GitService.StartupTarget.Tag "v1.0.0" ] @>

        let allResult = GitService.parseStartupTargets [| "--all" |]
        match allResult with
        | Error err -> failwith err
        | Ok targets -> test <@ targets = [ GitService.StartupTarget.All ] @>

    [<Fact>]
    let ``buildDiffCacheEntry should cache summary and file list`` () =
        let files =
            [
                sampleFile
                    "foo.txt"
                    "foo.txt"
                    [
                        {
                            Type = Models.Removed
                            Content = "line1"
                            OldLineNo = Some 1
                            NewLineNo = None
                        }
                        {
                            Type = Models.Added
                            Content = "line1 updated"
                            OldLineNo = None
                            NewLineNo = Some 1
                        }
                    ]
                sampleFile
                    "/dev/null"
                    "bar.txt"
                    [
                        {
                            Type = Models.Added
                            Content = "new file line"
                            OldLineNo = None
                            NewLineNo = Some 1
                        }
                    ]
            ]

        let entry = GitService.buildDiffCacheEntry "abc123" files

        test <@ entry.Summary.Hash = "abc123" @>
        test <@ entry.Summary.FileCount = 2 @>
        test <@ entry.Summary.AddedLines = 2 @>
        test <@ entry.Summary.RemovedLines = 1 @>
        test <@ entry.FileList.[0].DisplayPath = "foo.txt" @>
        test <@ entry.FileList.[1].DisplayPath = "bar.txt (new file)" @>

    [<Fact>]
    let ``fetchDiffFileContent should cache file content per commit hash`` () =
        withTempRepository (fun root repo ->
            let firstCommit = commitFile repo root "foo.txt" "one" "first commit"
            let secondCommit = commitFile repo root "foo.txt" "two" "second commit"

            match GitService.fetchDiffFileContent firstCommit.Sha "foo.txt" "foo.txt" with
            | Error err -> failwith err
            | Ok firstFile ->
                test <@ firstFile.Hunks.Head.Lines |> List.exists (fun line -> line.Type = Models.Added && line.Content = "one") @>

            match GitService.fetchDiffFileContent secondCommit.Sha "foo.txt" "foo.txt" with
            | Error err -> failwith err
            | Ok secondFile ->
                test <@ secondFile.Hunks.Head.Lines |> List.exists (fun line -> line.Type = Models.Added && line.Content = "two") @>)

    [<Fact>]
    let ``createTag and createBranch should update repository refs without the CLI`` () =
        withTempRepository (fun root repo ->
            let commit = commitFile repo root "base.txt" "base" "base commit"

            match GitService.createTag commit.Sha "v1.0.0" with
            | Ok _ -> ()
            | Error err -> failwith err

            match GitService.createBranch commit.Sha "topic" with
            | Ok _ -> ()
            | Error err -> failwith err

            test <@ repo.Tags["v1.0.0"] <> null @>
            test <@ repo.Tags["v1.0.0"].Target.Id.Sha = commit.Sha @>
            test <@ repo.Branches["topic"] <> null @>
            test <@ repo.Branches["topic"].Tip.Sha = commit.Sha @>)

    [<Fact>]
    let ``fetchHistory should include stashes and label them distinctly`` () =
        withTempRepository (fun root repo ->
            let _ = commitFile repo root "app.txt" "base" "base commit"
            File.WriteAllText(Path.Combine(root, "app.txt"), "stashed changes")
            let signature = Signature("GitKay Tests", "gitkay@example.com", DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero))

            match repo.Stashes.Add(signature, "wip") with
            | null -> failwith "Stash creation failed."
            | _ ->
                match GitService.fetchHistory [ GitService.StartupTarget.All ] with
                | Error err -> failwith err
                | Ok commits ->
                    test <@ commits |> List.exists (fun commit -> commit.Refs |> List.exists (fun ref -> ref.Kind = Models.CommitRefKind.Stash)) @>
                    test <@ commits |> List.exists (fun commit -> commit.Refs |> List.exists (fun ref -> ref.Name = "stash@{0}") ) @>)

    [<Fact>]
    let ``resetTo should move HEAD and working tree to the target commit`` () =
        withTempRepository (fun root repo ->
            let baseCommit = commitFile repo root "app.txt" "base" "base commit"
            let _ = repo.CreateBranch("main", baseCommit)
            Commands.Checkout(repo, repo.Branches["main"]) |> ignore

            let updatedCommit = commitFile repo root "app.txt" "updated" "updated commit"

            match GitService.resetTo baseCommit.Sha true with
            | Ok _ -> ()
            | Error err -> failwith err

            use checkRepo = new Repository(root)
            test <@ checkRepo.Head.Tip.Sha = baseCommit.Sha @>
            test <@ File.ReadAllText(Path.Combine(root, "app.txt")) = "base" @>
            test <@ checkRepo.Head.Tip.Sha <> updatedCommit.Sha @>)

    [<Fact>]
    let ``soft reset should move HEAD without rewriting the working tree`` () =
        withTempRepository (fun root repo ->
            let baseCommit = commitFile repo root "app.txt" "base" "base commit"
            let _ = repo.CreateBranch("main", baseCommit)
            Commands.Checkout(repo, repo.Branches["main"]) |> ignore

            let updatedCommit = commitFile repo root "app.txt" "updated" "updated commit"

            match GitService.resetTo baseCommit.Sha false with
            | Ok _ -> ()
            | Error err -> failwith err

            use checkRepo = new Repository(root)
            test <@ checkRepo.Head.Tip.Sha = baseCommit.Sha @>
            test <@ File.ReadAllText(Path.Combine(root, "app.txt")) = "updated" @>
            test <@ checkRepo.Head.Tip.Sha <> updatedCommit.Sha @>)

    [<Fact>]
    let ``cherryPick and revert should use repository-backed operations`` () =
        withTempRepository (fun root repo ->
            let baseCommit = commitFile repo root "app.txt" "base" "base commit"
            let topicBranch = repo.CreateBranch("topic", baseCommit)
            Commands.Checkout(repo, topicBranch) |> ignore

            let featureCommit = commitFile repo root "app.txt" "feature" "feature commit"
            repo.Reset(ResetMode.Hard, baseCommit)

            match GitService.cherryPick featureCommit.Sha with
            | Ok _ -> ()
            | Error err -> failwith err

            use pickedRepo = new Repository(root)
            test <@ File.ReadAllText(Path.Combine(root, "app.txt")) = "feature" @>
            test <@ pickedRepo.Head.Tip.Sha <> featureCommit.Sha @>

            let pickedCommit = pickedRepo.Head.Tip

            match GitService.revert pickedCommit.Sha with
            | Ok _ -> ()
            | Error err -> failwith err

            use revertedRepo = new Repository(root)
            test <@ File.ReadAllText(Path.Combine(root, "app.txt")) = "base" @>
            test <@ revertedRepo.Head.Tip.Sha <> pickedCommit.Sha @>
            test <@ revertedRepo.Head.Tip.Sha <> baseCommit.Sha @>)

    [<Fact>]
    let ``searchCommitsWithDiffLoader should match metadata, refs, paths, and text`` () =
        let commits : Models.Commit list =
            [
                {
                    Hash = "abc12345abc12345abc12345abc12345abc12345"
                    AuthorName = "Jane Doe"
                    AuthorEmail = "jane@example.com"
                    Timestamp = 1710000000L
                    Parents = []
                    Subject = "Fix parser"
                    Message = "Fix parser\n\nNeedle body"
                    Refs = [ RefHelpers.branchRef "main"; RefHelpers.tagRef "v1.0" ]
                }
                {
                    Hash = "def67890def67890def67890def67890def67890"
                    AuthorName = "John Smith"
                    AuthorEmail = "john@example.com"
                    Timestamp = 1710003600L
                    Parents = []
                    Subject = "Add docs"
                    Message = "Add docs"
                    Refs = [ RefHelpers.remoteRef "origin/release/1.0" ]
                }
            ]

        let diffLoader hash =
            match hash with
            | "abc12345abc12345abc12345abc12345abc12345" ->
                Ok
                    [
                        sampleFile
                            "src/needle.txt"
                            "src/needle.txt"
                            [
                                {
                                    Type = Models.Context
                                    Content = "needle line"
                                    OldLineNo = Some 1
                                    NewLineNo = Some 1
                                }
                            ]
                    ]
            | _ -> Ok []

        match GitService.searchCommitsWithDiffLoader commits "needle" GitService.SearchScope.All diffLoader with
        | Error err -> failwith err
        | Ok results ->
            test <@ results.Length = 1 @>
            let hit = results.Head
            test <@ hit.Commit.Hash = "abc12345abc12345abc12345abc12345abc12345" @>
            test <@ hit.MatchKinds |> List.contains "message" @>
            test <@ hit.MatchKinds |> List.contains "path" @>
            test <@ hit.MatchKinds |> List.contains "text" @>
            test <@ hit.MatchSummary.Contains "paths: src/needle.txt" @>

        match GitService.searchCommitsWithDiffLoader commits "release" GitService.SearchScope.Ref diffLoader with
        | Error err -> failwith err
        | Ok results ->
            test <@ results.Length = 1 @>
            let hit = results.Head
            test <@ hit.Commit.Hash = "def67890def67890def67890def67890def67890" @>
            test <@ hit.MatchKinds = [ "ref" ] @>
            test <@ hit.MatchSummary.Contains "refs: origin/release/1.0" @>

    [<Fact>]
    let ``searchCommitsWithDiffLoader should match diff text in the all scope even when metadata and paths do not match`` () =
        let commit : Models.Commit =
            {
                Hash = "feedfacefeedfacefeedfacefeedfacefeedface"
                AuthorName = "Jane Doe"
                AuthorEmail = "jane@example.com"
                Timestamp = 1710000000L
                Parents = []
                Subject = "No metadata hit"
                Message = "No metadata hit"
                Refs = []
            }

        let diffLoader hash =
            match hash with
            | "feedfacefeedfacefeedfacefeedfacefeedface" ->
                Ok
                    [
                        sampleFile
                            "src/other.txt"
                            "src/other.txt"
                            [
                                {
                                    Type = Models.Context
                                    Content = "needle line in diff text"
                                    OldLineNo = Some 1
                                    NewLineNo = Some 1
                                }
                            ]
                    ]
            | _ -> Ok []

        match GitService.searchCommitsWithDiffLoader [ commit ] "needle" GitService.SearchScope.All diffLoader with
        | Error err -> failwith err
        | Ok results ->
            test <@ results.Length = 1 @>
            let hit = results.Head
            test <@ hit.Commit.Hash = commit.Hash @>
            test <@ hit.MatchKinds = [ "text" ] @>
            test <@ hit.MatchSummary = "text" @>
            test <@ hit.MatchedPaths = [] @>
            test <@ hit.MatchedRefs = [] @>

module AppTests =

    let private sampleCommit hash subject : Models.Commit =
        {
            Hash = hash
            AuthorName = "Author"
            AuthorEmail = "author@example.com"
            Timestamp = 1710000000L
            Parents = []
            Subject = subject
            Message = subject
            Refs = []
        }

    let private sampleFile oldPath newPath lines : Models.FileDiff =
        {
            OldPath = oldPath
            NewPath = newPath
            Hunks =
                [
                    {
                        Header = "@@ -1 +1 @@"
                        Lines = lines
                    }
                ]
        }

    let private sampleSummary oldPath newPath displayPath : GitService.DiffFileSummary =
        {
            OldPath = oldPath
            NewPath = newPath
            DisplayPath = displayPath
        }

    [<Fact>]
    let ``calculateLanes should keep merge edges separate from pass-through lanes`` () =
        let commits : Models.Commit list =
            [
                {
                    Hash = "merge"
                    AuthorName = "Author"
                    AuthorEmail = "author@example.com"
                    Timestamp = 1710000000L
                    Parents = [ "left"; "right" ]
                    Subject = "Merge branch"
                    Message = "Merge branch"
                    Refs = []
                }
                {
                    Hash = "left"
                    AuthorName = "Author"
                    AuthorEmail = "author@example.com"
                    Timestamp = 1709999940L
                    Parents = [ "base" ]
                    Subject = "Left"
                    Message = "Left"
                    Refs = []
                }
                {
                    Hash = "right"
                    AuthorName = "Author"
                    AuthorEmail = "author@example.com"
                    Timestamp = 1709999880L
                    Parents = [ "base" ]
                    Subject = "Right"
                    Message = "Right"
                    Refs = []
                }
                {
                    Hash = "base"
                    AuthorName = "Author"
                    AuthorEmail = "author@example.com"
                    Timestamp = 1709999820L
                    Parents = []
                    Subject = "Base"
                    Message = "Base"
                    Refs = []
                }
            ]

        let graph = Graph.calculateLanes commits

        test <@ graph.Length = 4 @>

        let mergeRow = graph.[0]
        test <@ mergeRow.Lane = 0 @>
        test <@ mergeRow.Segments.Length = 2 @>
        test <@ mergeRow.Segments |> List.forall (fun segment -> segment.IsCommit) @>
        test <@ mergeRow.Segments |> List.map (fun segment -> segment.TargetLane) |> List.sort = [ 0; 1 ] @>

        let leftRow = graph.[1]
        test <@ leftRow.Segments.Length = 2 @>
        test <@ leftRow.Segments |> List.exists (fun segment -> segment.IsCommit) @>
        test <@ leftRow.Segments |> List.exists (fun segment -> not segment.IsCommit) @>
        test <@ leftRow.Segments |> List.exists (fun segment -> not segment.IsCommit && segment.Lane = segment.TargetLane) @>

    [<Fact>]
    let ``calculateLanes should keep the neighboring branch lane anchored through a merge`` () =
        let commits : Models.Commit list =
            [
                {
                    Hash = "merge"
                    AuthorName = "Author"
                    AuthorEmail = "author@example.com"
                    Timestamp = 1710000000L
                    Parents = [ "left"; "right" ]
                    Subject = "Merge branch"
                    Message = "Merge branch"
                    Refs = []
                }
                {
                    Hash = "left"
                    AuthorName = "Author"
                    AuthorEmail = "author@example.com"
                    Timestamp = 1709999940L
                    Parents = [ "left-base" ]
                    Subject = "Left"
                    Message = "Left"
                    Refs = []
                }
                {
                    Hash = "right"
                    AuthorName = "Author"
                    AuthorEmail = "author@example.com"
                    Timestamp = 1709999880L
                    Parents = [ "right-base" ]
                    Subject = "Right"
                    Message = "Right"
                    Refs = []
                }
                {
                    Hash = "left-base"
                    AuthorName = "Author"
                    AuthorEmail = "author@example.com"
                    Timestamp = 1709999820L
                    Parents = []
                    Subject = "Left base"
                    Message = "Left base"
                    Refs = []
                }
                {
                    Hash = "right-base"
                    AuthorName = "Author"
                    AuthorEmail = "author@example.com"
                    Timestamp = 1709999760L
                    Parents = []
                    Subject = "Right base"
                    Message = "Right base"
                    Refs = []
                }
            ]

        let graph = Graph.calculateLanes commits

        test <@ graph.Length = 5 @>

        let mergeRow = graph.[0]
        test <@ mergeRow.Lane = 0 @>
        test <@ mergeRow.Segments |> List.map (fun segment -> segment.TargetLane) |> List.sort = [ 0; 1 ] @>

        let leftRow = graph.[1]
        test <@ leftRow.Lane = 0 @>
        test <@ leftRow.Segments |> List.exists (fun segment -> not segment.IsCommit && segment.Lane = 1 && segment.TargetLane = 1) @>
        test <@ leftRow.Segments |> List.exists (fun segment -> segment.IsCommit && segment.TargetLane = 0) @>

        let rightRow = graph.[2]
        test <@ rightRow.Lane = 1 @>
        test <@ rightRow.Segments |> List.exists (fun segment -> not segment.IsCommit && segment.Lane = 0 && segment.TargetLane = 0) @>
        test <@ rightRow.Segments |> List.exists (fun segment -> segment.IsCommit && segment.TargetLane = 1) @>

    let private sampleSearchResult (commit: Models.Commit) matchKinds matchSummary : GitService.SearchResult =
        {
            Commit = commit
            MatchKinds = matchKinds
            MatchSummary = matchSummary
            MatchedPaths = []
            MatchedRefs = []
        }

    let private sampleSearchResultWithContext
        (commit: Models.Commit)
        matchKinds
        matchSummary
        matchedPaths
        matchedRefs
        : GitService.SearchResult =
        {
            Commit = commit
            MatchKinds = matchKinds
            MatchSummary = matchSummary
            MatchedPaths = matchedPaths
            MatchedRefs = matchedRefs
        }

    let private emptyModel : App.Model =
        {
            Status = ""
            StartupTargets = []
            SearchQuery = ""
            SearchScopeKey = "all"
            SearchResults = None
            Commits = []
            SelectedCommitHash = None
            SelectedDiffHash = None
            SelectedDiffFiles = None
            SelectedDiff = None
            SelectedDiffFileKey = None
            SelectionStartedAtTicks = None
            SelectedDiffStartedAtTicks = None
            SearchStartedAtTicks = None
        }

    [<Fact>]
    let ``init should store parsed startup targets`` () =
        let model, _ = App.init [| "--branch"; "topic"; "--tag"; "v1.0" |]

        test <@ model.Status = "Loading history..." @>
        test <@ model.StartupTargets = [ GitService.StartupTarget.Branch "topic"; GitService.StartupTarget.Tag "v1.0" ] @>

    [<Fact>]
    let ``HistoryLoaded should auto-select the first commit when nothing is selected`` () =
        let commits =
            [
                sampleCommit "first" "First"
                sampleCommit "second" "Second"
            ]

        let next, _ = App.update (App.Msg.HistoryLoaded (Ok commits)) emptyModel

        test <@ next.Status = "Loaded 2 commits" @>
        test <@ next.Commits.Length = 2 @>
        test <@ next.SelectedCommitHash = Some "first" @>
        test <@ next.SelectedDiffHash = None @>
        test <@ next.SelectedDiffFiles = None @>
        test <@ next.SelectedDiff = None @>
        test <@ next.SelectedDiffFileKey = None @>
        test <@ next.SelectionStartedAtTicks.IsSome @>
        test <@ next.SelectedDiffStartedAtTicks.IsSome @>

    [<Fact>]
    let ``HistoryLoaded should keep an existing selected commit when it still exists`` () =
        let commits =
            [
                sampleCommit "first" "First"
                sampleCommit "second" "Second"
            ]

        let selectedFile =
            sampleFile
                "foo.txt"
                "foo.txt"
                [
                    {
                        Type = Models.Context
                        Content = "line1"
                        OldLineNo = Some 1
                        NewLineNo = Some 1
                    }
                ]

        let initial =
            {
                emptyModel with
                    SelectedCommitHash = Some "second"
                    SelectedDiffHash = Some "second"
                    SelectedDiffFiles = Some [ sampleSummary "foo.txt" "foo.txt" "foo.txt" ]
                    SelectedDiff = Some [ selectedFile ]
                    SelectedDiffFileKey = Some { OldPath = "foo.txt"; NewPath = "foo.txt" }
            }

        let next, _ = App.update (App.Msg.HistoryLoaded (Ok commits)) initial

        test <@ next.SelectedCommitHash = Some "second" @>
        test <@ next.SelectedDiffHash = Some "second" @>
        test <@ next.SelectedDiffFiles = initial.SelectedDiffFiles @>
        test <@ next.SelectedDiff = initial.SelectedDiff @>
        test <@ next.SelectedDiffFileKey = initial.SelectedDiffFileKey @>
        test <@ next.SelectionStartedAtTicks = None @>
        test <@ next.SelectedDiffStartedAtTicks = None @>

    [<Fact>]
    let ``HistoryLoaded should fall back to the first commit when the previous selection is missing`` () =
        let commits =
            [
                sampleCommit "first" "First"
                sampleCommit "second" "Second"
            ]

        let selectedFile =
            sampleFile
                "foo.txt"
                "foo.txt"
                [
                    {
                        Type = Models.Context
                        Content = "line1"
                        OldLineNo = Some 1
                        NewLineNo = Some 1
                    }
                ]

        let initial =
            {
                emptyModel with
                    SelectedCommitHash = Some "missing"
                    SelectedDiffHash = Some "missing"
                    SelectedDiffFiles = Some [ sampleSummary "foo.txt" "foo.txt" "foo.txt" ]
                    SelectedDiffFileKey = Some { OldPath = "foo.txt"; NewPath = "foo.txt" }
                    SelectedDiff = Some [ selectedFile ]
                    SelectionStartedAtTicks = Some 1L
                    SelectedDiffStartedAtTicks = Some 2L
            }

        let next, _ = App.update (App.Msg.HistoryLoaded (Ok commits)) initial

        test <@ next.Status = "Loaded 2 commits" @>
        test <@ next.SelectedCommitHash = Some "first" @>
        test <@ next.SelectedDiffHash = None @>
        test <@ next.SelectedDiffFiles = None @>
        test <@ next.SelectedDiff = None @>
        test <@ next.SelectedDiffFileKey = None @>
        test <@ next.SelectionStartedAtTicks.IsSome @>
        test <@ next.SelectedDiffStartedAtTicks.IsSome @>

    [<Fact>]
    let ``SelectCommit should clear diff state and start loading the new file list`` () =
        let selectedFile =
            sampleFile
                "foo.txt"
                "foo.txt"
                [
                    {
                        Type = Models.Context
                        Content = "line1"
                        OldLineNo = Some 1
                        NewLineNo = Some 1
                    }
                ]

        let initial =
            {
                emptyModel with
                    SelectedCommitHash = Some "old"
                    SelectedDiffHash = Some "old"
                    SelectedDiffFiles = Some [ sampleSummary "foo.txt" "foo.txt" "foo.txt" ]
                    SelectedDiffFileKey = Some { OldPath = "foo.txt"; NewPath = "foo.txt" }
                    SelectedDiff = Some [ selectedFile ]
                    SelectionStartedAtTicks = Some 1L
                    SelectedDiffStartedAtTicks = Some 2L
            }

        let next, _ = App.update (App.Msg.SelectCommit("new", 42L)) initial

        test <@ next.SelectedCommitHash = Some "new" @>
        test <@ next.SelectedDiffHash = None @>
        test <@ next.SelectedDiffFiles = None @>
        test <@ next.SelectedDiff = None @>
        test <@ next.SelectedDiffFileKey = None @>
        test <@ next.SelectionStartedAtTicks = Some 42L @>
        test <@ next.SelectedDiffStartedAtTicks = Some 42L @>

    [<Fact>]
    let ``DiffFilesLoaded should select the first file while the unified diff is still loading`` () =
        let files =
            [
                sampleSummary "foo.txt" "foo.txt" "foo.txt"
                sampleSummary "/dev/null" "bar.txt" "bar.txt (new file)"
            ]

        let initial =
            {
                emptyModel with
                    SelectedCommitHash = Some "commit"
                    SelectionStartedAtTicks = Some 7L
                    SelectedDiffStartedAtTicks = Some 7L
            }

        let next, _ = App.update (App.Msg.DiffFilesLoaded("commit", 7L, Ok files)) initial

        test <@ next.SelectedCommitHash = Some "commit" @>
        test <@ next.SelectedDiffHash = Some "commit" @>
        test <@ next.SelectedDiffFiles = Some files @>
        test <@ next.SelectedDiff = None @>
        test <@ next.SelectedDiffFileKey = Some { OldPath = "foo.txt"; NewPath = "foo.txt" } @>
        test <@ next.SelectionStartedAtTicks = None @>
        test <@ next.SelectedDiffStartedAtTicks = Some 7L @>

    [<Fact>]
    let ``DiffFilesLoaded should ignore stale file list results from an earlier selection`` () =
        let firstSelection, _ = App.update (App.Msg.SelectCommit("old", 1L)) emptyModel
        let currentSelection, _ = App.update (App.Msg.SelectCommit("new", 2L)) firstSelection

        let staleFiles =
            [
                sampleSummary "foo.txt" "foo.txt" "foo.txt"
            ]

        let next, _ = App.update (App.Msg.DiffFilesLoaded("old", 1L, Ok staleFiles)) currentSelection

        test <@ next.SelectedCommitHash = Some "new" @>
        test <@ next.SelectedDiffHash = None @>
        test <@ next.SelectedDiffFiles = None @>
        test <@ next.SelectedDiff = None @>
        test <@ next.SelectedDiffFileKey = None @>
        test <@ next.SelectionStartedAtTicks = Some 2L @>
        test <@ next.SelectedDiffStartedAtTicks = Some 2L @>

    [<Fact>]
    let ``DiffLoaded should hydrate the unified diff projection when the request is current`` () =
        let files =
            [
                sampleSummary "foo.txt" "foo.txt" "foo.txt"
                sampleSummary "/dev/null" "bar.txt" "bar.txt (new file)"
            ]

        let initial =
            {
                emptyModel with
                    SelectedCommitHash = Some "commit"
                    SelectedDiffHash = Some "commit"
                    SelectedDiffFiles = Some files
                    SelectedDiffStartedAtTicks = Some 42L
                    SelectedDiffFileKey = Some { OldPath = "foo.txt"; NewPath = "foo.txt" }
            }

        let diff =
            [
                sampleFile
                    "foo.txt"
                    "foo.txt"
                    [
                        {
                            Type = Models.Context
                            Content = "line1"
                            OldLineNo = Some 1
                            NewLineNo = Some 1
                        }
                    ]
                sampleFile
                    "/dev/null"
                    "bar.txt"
                    [
                        {
                            Type = Models.Added
                            Content = "new file line"
                            OldLineNo = None
                            NewLineNo = Some 1
                        }
                    ]
            ]

        let next, _ = App.update (App.Msg.DiffLoaded("commit", 42L, Ok diff)) initial

        test <@ next.SelectedDiffHash = Some "commit" @>
        test <@ next.SelectedDiff = Some diff @>
        test <@ next.SelectedDiffStartedAtTicks = None @>

    [<Fact>]
    let ``SelectDiffFile should update the navigation target without reloading the diff`` () =
        let files =
            [
                sampleSummary "foo.txt" "foo.txt" "foo.txt"
                sampleSummary "/dev/null" "bar.txt" "bar.txt (new file)"
            ]

        let initial =
            {
                emptyModel with
                    SelectedCommitHash = Some "commit"
                    SelectedDiffHash = Some "commit"
                    SelectedDiffFiles = Some files
                    SelectedDiff = Some [ sampleFile "foo.txt" "foo.txt" [] ]
                    SelectedDiffFileKey = Some { OldPath = "foo.txt"; NewPath = "foo.txt" }
            }

        let next, _ = App.update (App.Msg.SelectDiffFile("commit", "/dev/null", "bar.txt")) initial

        test <@ next.SelectedDiffFileKey = Some { OldPath = "/dev/null"; NewPath = "bar.txt" } @>
        test <@ next.SelectedDiff = initial.SelectedDiff @>
        test <@ next.SelectedDiffStartedAtTicks = None @>

    [<Fact>]
    let ``RunSearch should record the pending search and clear stale results`` () =
        let initial =
            {
                emptyModel with
                    SearchQuery = "old"
                    SearchScopeKey = "message"
                    SearchResults = Some [ sampleSearchResult (sampleCommit "oldhash" "Old") [ "message" ] "message" ]
                    SearchStartedAtTicks = Some 1L
            }

        let next, _ = App.update (App.Msg.RunSearch("needle", "ref", 42L)) initial

        test <@ next.SearchQuery = "needle" @>
        test <@ next.SearchScopeKey = "ref" @>
        test <@ next.SearchResults = None @>
        test <@ next.SearchStartedAtTicks = Some 42L @>
        test <@ next.Status = "Searching needle..." @>

    [<Fact>]
    let ``SearchResultsLoaded should apply the current search results`` () =
        let commit =
            sampleCommit "searchhash" "Search hit"

        let initial =
            {
                emptyModel with
                    SearchQuery = "needle"
                    SearchScopeKey = "all"
                    SearchStartedAtTicks = Some 42L
            }

        let next, _ =
            App.update
                (App.Msg.SearchResultsLoaded("needle", "all", 42L, Ok [ sampleSearchResult commit [ "message"; "path" ] "message; paths: src/needle.txt" ]))
                initial

        test <@ next.SearchResults.IsSome @>
        test <@ next.SearchResults.Value.Length = 1 @>
        test <@ next.SearchResults.Value.Head.Commit.Hash = "searchhash" @>
        test <@ next.SearchStartedAtTicks = None @>
        test <@ next.Status = "Search: 1 hit(s) for \"needle\"" @>

    [<Fact>]
    let ``MainProjection should render the unified diff while keeping file navigation in sync`` () =
        let projection = MainProjection()
        projection.SetDispatch ignore

        let commit =
            sampleCommit "12345678" "Subject"

        let fooFile =
            sampleFile
                "foo.txt"
                "foo.txt"
                [
                    {
                        Type = Models.Context
                        Content = "line1"
                        OldLineNo = Some 1
                        NewLineNo = Some 1
                    }
                ]

        let barFile =
            sampleFile
                "/dev/null"
                "bar.txt"
                [
                    {
                        Type = Models.Added
                        Content = "new file line"
                        OldLineNo = None
                        NewLineNo = Some 1
                    }
                ]

        let barSummary =
            sampleSummary "/dev/null" "bar.txt" "bar.txt (new file)"

        let fooSummary =
            sampleSummary "foo.txt" "foo.txt" "foo.txt"

        let model =
            {
                emptyModel with
                    Status = "Loaded"
                    Commits = Graph.calculateLanes [ commit ]
                    SelectedCommitHash = Some commit.Hash
                    SelectedDiffHash = Some commit.Hash
                    SelectedDiffFiles = Some [ fooSummary; barSummary ]
                    SelectedDiff = Some [ fooFile; barFile ]
                    SelectedDiffFileKey = Some { OldPath = "foo.txt"; NewPath = "foo.txt" }
            }

        projection.Update model

        test <@ projection.SelectedDiffFiles.Count = 2 @>
        test <@ projection.SelectedDiffFile.DisplayPath = "foo.txt" @>
        test <@ projection.SelectedDiffRows.Count = 6 @>

        match projection.SelectedDiffRow with
        | :? DiffFileHeaderProjection as header -> test <@ header.DisplayPath = "foo.txt" @>
        | other -> failwithf "Expected a file header, got %A" other

        projection.SelectedDiffFile <- projection.SelectedDiffFiles.[1]

        match projection.SelectedDiffRow with
        | :? DiffFileHeaderProjection as header -> test <@ header.DisplayPath = "bar.txt (new file)" @>
        | other -> failwithf "Expected a file header after selection sync, got %A" other

        test <@ projection.SelectedDiffRows.Count = 6 @>

    [<Fact>]
    let ``MainProjection should dispatch diff file selection and keep the focus row in sync`` () =
        let projection = MainProjection()
        let mutable lastMsg = None
        projection.SetDispatch (fun msg -> lastMsg <- Some msg)

        let commit =
            sampleCommit "12345678" "Subject"

        let fooFile =
            sampleFile
                "foo.txt"
                "foo.txt"
                [
                    {
                        Type = Models.Context
                        Content = "line1"
                        OldLineNo = Some 1
                        NewLineNo = Some 1
                    }
                ]

        let barFile =
            sampleFile
                "/dev/null"
                "bar.txt"
                [
                    {
                        Type = Models.Added
                        Content = "new file line"
                        OldLineNo = None
                        NewLineNo = Some 1
                    }
                ]

        let barSummary =
            sampleSummary "/dev/null" "bar.txt" "bar.txt (new file)"

        let fooSummary =
            sampleSummary "foo.txt" "foo.txt" "foo.txt"

        let model =
            {
                emptyModel with
                    Status = "Loaded"
                    Commits = Graph.calculateLanes [ commit ]
                    SelectedCommitHash = Some commit.Hash
                    SelectedDiffHash = Some commit.Hash
                    SelectedDiffFiles = Some [ fooSummary; barSummary ]
                    SelectedDiff = Some [ fooFile; barFile ]
                    SelectedDiffFileKey = Some { OldPath = "foo.txt"; NewPath = "foo.txt" }
            }

        projection.Update model

        projection.SelectedDiffFile <- projection.SelectedDiffFiles.[1]

        test <@ projection.SelectedDiffFile.DisplayPath = "bar.txt (new file)" @>
        test <@ projection.SelectedDiffRows.Count = 6 @>

        match projection.SelectedDiffRow with
        | :? DiffFileHeaderProjection as header -> test <@ header.DisplayPath = "bar.txt (new file)" @>
        | other -> failwithf "Expected the diff focus row to follow the selected file, got %A" other

        match lastMsg with
        | Some (App.Msg.SelectDiffFile(hash, oldPath, newPath)) ->
            test <@ hash = commit.Hash @>
            test <@ oldPath = "/dev/null" @>
            test <@ newPath = "bar.txt" @>
        | other -> failwithf "Expected a diff file selection message, got %A" other

    [<Fact>]
    let ``MainProjection should filter the commit list to matching commits`` () =
        let projection = MainProjection()
        projection.SetDispatch ignore

        let commit =
            sampleCommit "feedfacefeedfacefeedfacefeedfacefeedface" "Search hit"
        let otherCommit =
            sampleCommit "deadbeefdeadbeefdeadbeefdeadbeefdeadbeef" "Other"

        let model =
            {
                emptyModel with
                    Status = "Loaded"
                    Commits = Graph.calculateLanes [ commit; otherCommit ]
                    SearchQuery = "needle"
                    SearchScopeKey = "all"
                    SearchResults = Some [ sampleSearchResult commit [ "message" ] "message" ]
            }

        projection.Update model

        test <@ projection.HasSearchResults @>
        test <@ projection.SearchResults.Count = 1 @>
        test <@ projection.VisibleCommits.Count = 1 @>
        test <@ projection.VisibleCommits.[0].FullHash = commit.Hash @>

    [<Fact>]
    let ``MainProjection should debounce live search updates as the query changes`` () =
        let projection = MainProjection()
        let messages = ConcurrentQueue<App.Msg>()
        projection.SetDispatch (fun msg -> messages.Enqueue msg |> ignore)

        projection.SearchQuery <- "nee"
        Task.Delay(200).Wait()
        projection.SearchQuery <- "needle"

        Task.Delay(1300).Wait()

        let dispatched = messages.ToArray()
        let setQueries =
            dispatched
            |> Array.choose (function
                | App.Msg.SetSearchQuery query -> Some query
                | _ -> None)

        let runSearches =
            dispatched
            |> Array.choose (function
                | App.Msg.RunSearch(query, scopeKey, _) -> Some(query, scopeKey)
                | _ -> None)

        test <@ setQueries = [| "nee"; "needle" |] @>
        test <@ runSearches = [| ("needle", "all") |] @>

    [<Fact>]
    let ``SearchResultProjection should surface matched fields, files, and counts`` () =
        let projection = SearchResultProjection()
        let commit =
            sampleCommit "feedfacefeedfacefeedfacefeedfacefeedface" "Search hit"

        projection.Update
            (sampleSearchResultWithContext
                commit
                [ "message"; "path" ]
                "message; paths: src/needle.txt, docs/needle.txt"
                [ "src/needle.txt"; "docs/needle.txt"; "README.md" ]
                [ "main" ])

        test <@ projection.HasMatchedFields @>
        test <@ projection.MatchedFieldsLabel = "Fields (2): Message / subject, File / path" @>
        test <@ projection.HasMatchedPaths @>
        test <@ projection.MatchedPathsLabel = "Files (3): src/needle.txt, docs/needle.txt, README.md" @>
        test <@ projection.HasMatchedRefs @>
        test <@ projection.MatchedRefsLabel = "Ref (1): main" @>

    [<Fact>]
    let ``MainProjection should surface search matches inline in commit rows`` () =
        let projection = MainProjection()
        projection.SetDispatch ignore

        let matchingCommit =
            sampleCommit "feedfacefeedfacefeedfacefeedfacefeedface" "Search hit"

        let otherCommit =
            sampleCommit "deadbeefdeadbeefdeadbeefdeadbeefdeadbeef" "Other"

        let model =
            {
                emptyModel with
                    Status = "Loaded"
                    Commits = Graph.calculateLanes [ matchingCommit; otherCommit ]
                    SearchQuery = "needle"
                    SearchScopeKey = "all"
                    SearchResults = Some [ sampleSearchResult matchingCommit [ "message"; "path" ] "message; paths: src/needle.txt" ]
            }

        projection.Update model

        let matchingVm = projection.Commits |> Seq.find (fun commit -> commit.FullHash = matchingCommit.Hash)
        let otherVm = projection.Commits |> Seq.find (fun commit -> commit.FullHash = otherCommit.Hash)

        test <@ matchingVm.HasSearchMatch @>
        test <@ matchingVm.SearchMatchSummary = "message; paths: src/needle.txt" @>
        test <@ not (obj.ReferenceEquals(matchingVm.RowBackground, Brushes.Transparent)) @>
        test <@ not otherVm.HasSearchMatch @>
        test <@ otherVm.SearchMatchSummary = "" @>
        test <@ obj.ReferenceEquals(otherVm.RowBackground, Brushes.Transparent) @>

    [<Fact>]
    let ``MainProjection should surface search matches inline in the diff view`` () =
        let projection = MainProjection()
        projection.SetDispatch ignore

        let commit =
            sampleCommit "12345678" "Subject"

        let file =
            sampleFile
                "src/needle.txt"
                "src/needle.txt"
                [
                    {
                        Type = Models.Context
                        Content = "needle line"
                        OldLineNo = Some 1
                        NewLineNo = Some 1
                    }
                ]

        let summary =
            sampleSummary "src/needle.txt" "src/needle.txt" "src/needle.txt"

        let model =
            {
                emptyModel with
                    Status = "Loaded"
                    Commits = Graph.calculateLanes [ commit ]
                    SelectedCommitHash = Some commit.Hash
                    SelectedDiffHash = Some commit.Hash
                    SelectedDiffFiles = Some [ summary ]
                    SelectedDiff = Some [ file ]
                    SelectedDiffFileKey = Some { OldPath = "src/needle.txt"; NewPath = "src/needle.txt" }
                    SearchQuery = "needle"
                    SearchScopeKey = "all"
            }

        projection.Update model

        test <@ projection.SelectedDiffFiles.Count = 1 @>
        test <@ projection.SelectedDiffFiles.[0].HasSearchMatch @>
        test <@ projection.SelectedDiffFiles.[0].SearchMatchSummary = "path · text" @>
        test <@ projection.SelectedDiffRows.Count = 3 @>

        let diffLine =
            projection.SelectedDiffRows
            |> Seq.choose (function
                | :? DiffLineProjection as line -> Some line
                | _ -> None)
            |> Seq.head

        test <@ diffLine.IsSearchMatch @>

    [<Fact>]
    let ``CommitProjection should surface refs in the row summary`` () =
        let projection = CommitProjection()
        let commit =
            {
                sampleCommit "feedfacefeedfacefeedfacefeedfacefeedface" "Search hit"
                with
                    Refs =
                        [
                            RefHelpers.branchRef "main"
                            RefHelpers.remoteRef "origin/main"
                            RefHelpers.tagRef "v1.0"
                            RefHelpers.stashRef "stash@{0}"
                        ]
            }

        projection.Update
            {
                Commit = commit
                Lane = 0
                Segments = []
            }

        test <@ projection.HasRefs @>
        test <@ projection.RefsSummary = "main · origin/main · v1.0 +1" @>
        test <@ projection.HasRefBadges @>
        test <@ projection.RefBadges.Count = 4 @>
        test <@ projection.RefBadges.[0].Kind = CommitRefKind.Branch @>
        test <@ projection.RefBadges.[1].Kind = CommitRefKind.Remote @>
        test <@ projection.RefBadges.[2].Kind = CommitRefKind.Tag @>
        test <@ projection.RefBadges.[3].Kind = CommitRefKind.Stash @>
        test <@ projection.RefBadges.[0].Text = "main" @>
        test <@ projection.RefBadges.[3].Text = "stash@{0}" @>

    [<Fact>]
    let ``FatalErrorPresenter should format details for the dialog`` () =
        let ex = InvalidOperationException("boom")
        let details = FatalErrorPresenter.BuildDetails("Heading", ex)

        test <@ details.Contains("Heading") @>
        test <@ details.Contains("System.InvalidOperationException") @>
        test <@ details.Contains("boom") @>

    [<Fact>]
    let ``DiffLoaded should ignore stale unified diff results`` () =
        let initial =
            {
                emptyModel with
                    SelectedCommitHash = Some "new"
                    SelectedDiffHash = Some "new"
                    SelectedDiffFiles = Some [ sampleSummary "foo.txt" "foo.txt" "foo.txt" ]
                    SelectedDiffStartedAtTicks = Some 42L
                    SelectedDiffFileKey = Some { OldPath = "foo.txt"; NewPath = "foo.txt" }
            }

        let loadedDiff = [ sampleFile "foo.txt" "foo.txt" [] ]

        let next, _ = App.update (App.Msg.DiffLoaded("old", 1L, Ok loadedDiff)) initial

        test <@ next.SelectedCommitHash = Some "new" @>
        test <@ next.SelectedDiffHash = Some "new" @>
        test <@ next.SelectedDiffFiles = initial.SelectedDiffFiles @>
        test <@ next.SelectedDiffFileKey = initial.SelectedDiffFileKey @>
        test <@ next.SelectedDiff = None @>
        test <@ next.SelectedDiffStartedAtTicks = Some 42L @>

    [<Fact>]
    let ``DiffLoaded should apply the unified diff when the request is current`` () =
        let initial =
            {
                emptyModel with
                    SelectedCommitHash = Some "new"
                    SelectedDiffHash = Some "new"
                    SelectedDiffFiles = Some [ sampleSummary "foo.txt" "foo.txt" "foo.txt" ]
                    SelectedDiffStartedAtTicks = Some 42L
                    SelectedDiffFileKey = Some { OldPath = "foo.txt"; NewPath = "foo.txt" }
            }

        let loadedDiff =
            [
                sampleFile
                    "foo.txt"
                    "foo.txt"
                    [
                        {
                            Type = Models.Context
                            Content = "line1"
                            OldLineNo = Some 1
                            NewLineNo = Some 1
                        }
                    ]
            ]

        let current, _ = App.update (App.Msg.DiffLoaded("new", 42L, Ok loadedDiff)) initial

        test <@ current.SelectedCommitHash = Some "new" @>
        test <@ current.SelectedDiffHash = Some "new" @>
        test <@ current.SelectedDiffFiles = initial.SelectedDiffFiles @>
        test <@ current.SelectedDiffFileKey = initial.SelectedDiffFileKey @>
        test <@ current.SelectedDiff = Some loadedDiff @>
        test <@ current.SelectedDiffStartedAtTicks = None @>
