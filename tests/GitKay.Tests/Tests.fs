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
    let ``buildDiffCacheEntry should cache summary, file list, and selected file content`` () =
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
        test <@ entry.SelectedFileContent.ContainsKey { OldPath = "foo.txt"; NewPath = "foo.txt" } @>
        test <@ entry.SelectedFileContent.ContainsKey { OldPath = "/dev/null"; NewPath = "bar.txt" } @>

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

    let private sampleDiff : Models.FileDiff list =
        [
            {
                OldPath = "foo.txt"
                NewPath = "foo.txt"
                Hunks = []
            }
        ]

    let private emptyModel : App.Model =
        {
            Status = ""
            StartupTargets = []
            Commits = []
            SelectedCommitHash = None
            SelectedDiffHash = None
            SelectedDiff = None
            SelectionStartedAtTicks = None
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
        test <@ next.SelectedDiff = None @>
        test <@ next.SelectionStartedAtTicks.IsSome @>

    [<Fact>]
    let ``HistoryLoaded should keep an existing selected commit when it still exists`` () =
        let commits =
            [
                sampleCommit "first" "First"
                sampleCommit "second" "Second"
            ]

        let initial =
            {
                emptyModel with
                    SelectedCommitHash = Some "second"
                    SelectedDiffHash = Some "second"
                    SelectedDiff = Some sampleDiff
            }

        let next, _ = App.update (App.Msg.HistoryLoaded (Ok commits)) initial

        test <@ next.SelectedCommitHash = Some "second" @>
        test <@ next.SelectedDiffHash = Some "second" @>
        test <@ next.SelectedDiff = Some sampleDiff @>
        test <@ next.SelectionStartedAtTicks = None @>

    [<Fact>]
    let ``SelectCommit should update selection without clearing diff state`` () =
        let initial =
            {
                emptyModel with
                    SelectedCommitHash = Some "old"
                    SelectedDiffHash = Some "old"
                    SelectedDiff = Some sampleDiff
                    SelectionStartedAtTicks = Some 1L
            }

        let next, _ = App.update (App.Msg.SelectCommit("new", 42L)) initial

        test <@ next.SelectedCommitHash = Some "new" @>
        test <@ next.SelectedDiffHash = Some "old" @>
        test <@ next.SelectedDiff = Some sampleDiff @>
        test <@ next.SelectionStartedAtTicks = Some 42L @>

    [<Fact>]
    let ``MainProjection should sync the selected file to the left diff focus row`` () =
        let projection = MainProjection()
        projection.SetDispatch ignore

        let commit =
            sampleCommit "12345678" "Subject"

        let model =
            {
                emptyModel with
                    Status = "Loaded"
                    Commits = Graph.calculateLanes [ commit ]
                    SelectedCommitHash = Some commit.Hash
                    SelectedDiffHash = Some commit.Hash
                    SelectedDiff =
                        Some
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
            }

        projection.Update model

        test <@ projection.SelectedDiffFiles.Count = 2 @>
        test <@ projection.SelectedDiffFile.DisplayPath = "foo.txt" @>

        match projection.SelectedDiffRow with
        | :? DiffFileHeaderProjection as header -> test <@ header.DisplayPath = "foo.txt" @>
        | other -> failwithf "Expected a file header, got %A" other

        projection.SelectedDiffFile <- projection.SelectedDiffFiles.[1]

        match projection.SelectedDiffRow with
        | :? DiffFileHeaderProjection as header -> test <@ header.DisplayPath = "bar.txt (new file)" @>
        | other -> failwithf "Expected a file header after selection sync, got %A" other

    [<Fact>]
    let ``DiffLoaded should ignore stale diff results`` () =
        let initial =
            {
                emptyModel with
                    SelectedCommitHash = Some "new"
                    SelectedDiffHash = None
                    SelectedDiff = None
                    SelectionStartedAtTicks = Some 42L
            }

        let next, _ = App.update (App.Msg.DiffLoaded("old", 1L, Ok sampleDiff)) initial

        test <@ next.SelectedCommitHash = Some "new" @>
        test <@ next.SelectedDiffHash = None @>
        test <@ next.SelectedDiff = None @>
        test <@ next.SelectionStartedAtTicks = Some 42L @>

    [<Fact>]
    let ``DiffLoaded should ignore stale results for the same commit when a newer request exists`` () =
        let initial =
            {
                emptyModel with
                    SelectedCommitHash = Some "new"
                    SelectedDiffHash = None
                    SelectedDiff = None
                    SelectionStartedAtTicks = Some 42L
            }

        let stale, _ = App.update (App.Msg.DiffLoaded("new", 41L, Ok sampleDiff)) initial

        test <@ stale.SelectedCommitHash = Some "new" @>
        test <@ stale.SelectedDiffHash = None @>
        test <@ stale.SelectedDiff = None @>
        test <@ stale.SelectionStartedAtTicks = Some 42L @>

        let current, _ = App.update (App.Msg.DiffLoaded("new", 42L, Ok sampleDiff)) initial

        test <@ current.SelectedCommitHash = Some "new" @>
        test <@ current.SelectedDiffHash = Some "new" @>
        test <@ current.SelectedDiff = Some sampleDiff @>
        test <@ current.SelectionStartedAtTicks = None @>
