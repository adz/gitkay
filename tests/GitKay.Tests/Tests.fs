namespace GitKay.Tests

open Xunit
open Swensen.Unquote
open GitKay.Core
open GitKay.UI

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

module AppTests =

    let private sampleCommit hash subject : Models.Commit =
        {
            Hash = hash
            AuthorName = "Author"
            AuthorEmail = "author@example.com"
            Timestamp = 1710000000L
            Parents = []
            Subject = subject
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

    let private emptyModel : App.Model =
        {
            Status = ""
            StartupTargets = []
            Commits = []
            SelectedCommitHash = None
            SelectedDiffHash = None
            SelectedDiffFiles = None
            SelectedDiffFileKey = None
            SelectedDiffFile = None
            SelectionStartedAtTicks = None
            SelectedDiffFileStartedAtTicks = None
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
        test <@ next.SelectedDiffFileKey = None @>
        test <@ next.SelectedDiffFile = None @>
        test <@ next.SelectionStartedAtTicks.IsSome @>
        test <@ next.SelectedDiffFileStartedAtTicks = None @>

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
                    SelectedDiffFileKey = Some { OldPath = "foo.txt"; NewPath = "foo.txt" }
                    SelectedDiffFile = Some selectedFile
            }

        let next, _ = App.update (App.Msg.HistoryLoaded (Ok commits)) initial

        test <@ next.SelectedCommitHash = Some "second" @>
        test <@ next.SelectedDiffHash = Some "second" @>
        test <@ next.SelectedDiffFiles = initial.SelectedDiffFiles @>
        test <@ next.SelectedDiffFileKey = initial.SelectedDiffFileKey @>
        test <@ next.SelectedDiffFile = initial.SelectedDiffFile @>
        test <@ next.SelectionStartedAtTicks = None @>
        test <@ next.SelectedDiffFileStartedAtTicks = None @>

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
                    SelectedDiffFile = Some selectedFile
                    SelectionStartedAtTicks = Some 1L
                    SelectedDiffFileStartedAtTicks = Some 2L
            }

        let next, _ = App.update (App.Msg.SelectCommit("new", 42L)) initial

        test <@ next.SelectedCommitHash = Some "new" @>
        test <@ next.SelectedDiffHash = None @>
        test <@ next.SelectedDiffFiles = None @>
        test <@ next.SelectedDiffFileKey = None @>
        test <@ next.SelectedDiffFile = None @>
        test <@ next.SelectionStartedAtTicks = Some 42L @>
        test <@ next.SelectedDiffFileStartedAtTicks = None @>

    [<Fact>]
    let ``DiffFilesLoaded should select the first file and queue its hydration`` () =
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
            }

        let next, _ = App.update (App.Msg.DiffFilesLoaded("commit", 7L, Ok files)) initial

        test <@ next.SelectedCommitHash = Some "commit" @>
        test <@ next.SelectedDiffHash = Some "commit" @>
        test <@ next.SelectedDiffFiles = Some files @>
        test <@ next.SelectedDiffFileKey = Some { OldPath = "foo.txt"; NewPath = "foo.txt" } @>
        test <@ next.SelectedDiffFile = None @>
        test <@ next.SelectionStartedAtTicks = None @>
        test <@ next.SelectedDiffFileStartedAtTicks.IsSome @>

    [<Fact>]
    let ``SelectDiffFile should update the selected file without loading every file`` () =
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
                    SelectedDiffFileKey = Some { OldPath = "foo.txt"; NewPath = "foo.txt" }
                    SelectedDiffFile = Some (sampleFile "foo.txt" "foo.txt" [])
                    SelectedDiffFileStartedAtTicks = Some 1L
            }

        let next, _ = App.update (App.Msg.SelectDiffFile("commit", "/dev/null", "bar.txt", 42L)) initial

        test <@ next.SelectedDiffFileKey = Some { OldPath = "/dev/null"; NewPath = "bar.txt" } @>
        test <@ next.SelectedDiffFile = None @>
        test <@ next.SelectedDiffFileStartedAtTicks = Some 42L @>

    [<Fact>]
    let ``MainProjection should sync the selected file to the left diff focus row`` () =
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
                    SelectedDiffFileKey = Some { OldPath = "foo.txt"; NewPath = "foo.txt" }
                    SelectedDiffFile = Some fooFile
            }

        projection.Update model

        test <@ projection.SelectedDiffFiles.Count = 2 @>
        test <@ projection.SelectedDiffFile.DisplayPath = "foo.txt" @>
        test <@ projection.SelectedDiffRows.Count = 3 @>

        match projection.SelectedDiffRow with
        | :? DiffFileHeaderProjection as header -> test <@ header.DisplayPath = "foo.txt" @>
        | other -> failwithf "Expected a file header, got %A" other

        projection.SelectedDiffFile <- projection.SelectedDiffFiles.[1]

        match projection.SelectedDiffRow with
        | :? DiffFileHeaderProjection as header -> test <@ header.DisplayPath = "bar.txt (new file)" @>
        | other -> failwithf "Expected a file header after selection sync, got %A" other

        test <@ projection.SelectedDiffRows.Count = 1 @>

        let hydratedBarFile =
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

        let hydratedModel =
            {
                model with
                    SelectedDiffFileKey = Some { OldPath = "/dev/null"; NewPath = "bar.txt" }
                    SelectedDiffFile = Some hydratedBarFile
            }

        projection.Update hydratedModel

        test <@ projection.SelectedDiffRows.Count = 3 @>

        match projection.SelectedDiffRow with
        | :? DiffFileHeaderProjection as header -> test <@ header.DisplayPath = "bar.txt (new file)" @>
        | other -> failwithf "Expected a file header after hydration, got %A" other

    [<Fact>]
    let ``DiffFileLoaded should ignore stale file results`` () =
        let initial =
            {
                emptyModel with
                    SelectedCommitHash = Some "new"
                    SelectedDiffHash = Some "new"
                    SelectedDiffFiles = Some [ sampleSummary "foo.txt" "foo.txt" "foo.txt" ]
                    SelectedDiffFileKey = Some { OldPath = "foo.txt"; NewPath = "foo.txt" }
                    SelectedDiffFileStartedAtTicks = Some 42L
            }

        let loadedFile = sampleFile "foo.txt" "foo.txt" []

        let next, _ = App.update (App.Msg.DiffFileLoaded("old", "foo.txt", "foo.txt", 1L, Ok loadedFile)) initial

        test <@ next.SelectedCommitHash = Some "new" @>
        test <@ next.SelectedDiffHash = Some "new" @>
        test <@ next.SelectedDiffFiles = initial.SelectedDiffFiles @>
        test <@ next.SelectedDiffFileKey = initial.SelectedDiffFileKey @>
        test <@ next.SelectedDiffFile = None @>
        test <@ next.SelectedDiffFileStartedAtTicks = Some 42L @>

    [<Fact>]
    let ``DiffFileLoaded should apply the selected file when the request is current`` () =
        let initial =
            {
                emptyModel with
                    SelectedCommitHash = Some "new"
                    SelectedDiffHash = Some "new"
                    SelectedDiffFiles = Some [ sampleSummary "foo.txt" "foo.txt" "foo.txt" ]
                    SelectedDiffFileKey = Some { OldPath = "foo.txt"; NewPath = "foo.txt" }
                    SelectedDiffFileStartedAtTicks = Some 42L
            }

        let loadedFile =
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

        let current, _ = App.update (App.Msg.DiffFileLoaded("new", "foo.txt", "foo.txt", 42L, Ok loadedFile)) initial

        test <@ current.SelectedCommitHash = Some "new" @>
        test <@ current.SelectedDiffHash = Some "new" @>
        test <@ current.SelectedDiffFiles = initial.SelectedDiffFiles @>
        test <@ current.SelectedDiffFileKey = initial.SelectedDiffFileKey @>
        test <@ current.SelectedDiffFile = Some loadedFile @>
        test <@ current.SelectedDiffFileStartedAtTicks = None @>
