namespace GitKay.Tests

open System
open System.Collections.Concurrent
open System.IO
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open Avalonia.Media
open Avalonia.Input
open LibGit2Sharp
open Axial
open GitKay.Core
open GitKay.UI

module private RefHelpers =
    let commitRef (kind: Models.CommitRefKind) (name: string) (isCurrentHead: bool option) : Models.CommitRef =
        {
            Name = name
            Kind = kind
            IsCurrentHead = defaultArg isCurrentHead false
        }

    let branchRef name = commitRef Models.CommitRefKind.Branch name None
    let currentBranchRef name = commitRef Models.CommitRefKind.Branch name (Some true)
    let remoteRef name = commitRef Models.CommitRefKind.Remote name None
    let tagRef name = commitRef Models.CommitRefKind.Tag name None
    let stashRef name = commitRef Models.CommitRefKind.Stash name None

module GitServiceTests =

    let private runFlow root flow =
        Flow.run (GitService.environment root) flow
        |> Exit.toResult

    let private sampleFile oldPath newPath lines : Models.FileDiff =
        {
            OldPath = oldPath
            NewPath = newPath
            NewLineCount = None
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
        let result = GitParsing.parseCommitLine line
        
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
        let result = GitParsing.parseCommitLine line
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

        let result = GitParsing.parseDiff diff
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
        test <@ addedProjection.OldContent = "" @>
        test <@ addedProjection.NewContent = "line" @>

        let removedLine : Models.DiffLine =
            {
                Type = Models.Removed
                Content = "line"
                OldLineNo = Some 4
                NewLineNo = None
            }

        let removedProjection = DiffLineProjection removedLine
        test <@ removedProjection.OldLineNoText = "4" @>
        test <@ removedProjection.NewLineNoText = "" @>
        test <@ removedProjection.OldContent = "line" @>
        test <@ removedProjection.NewContent = "" @>

    [<Fact>]
    let ``SyntaxHighlighting should classify common code tokens`` () =
        let tokens = SyntaxHighlighting.Tokenize("""let total = 42 // comment""")

        test <@ tokens |> Seq.exists (fun token -> token.Text = "let" && token.Kind = HighlightKind.Keyword) @>
        test <@ tokens |> Seq.exists (fun token -> token.Text = "42" && token.Kind = HighlightKind.Number) @>
        test <@ tokens |> Seq.exists (fun token -> token.Text.StartsWith("//") && token.Kind = HighlightKind.Comment) @>

    [<Fact>]
    let ``SyntaxHighlighting should make progress past non-comment markers`` () =
        let text = "url=https://example.test/path value#fragment"
        let tokens = SyntaxHighlighting.Tokenize(text)

        test <@ tokens |> Seq.sumBy (fun token -> token.Text.Length) = text.Length @>

    [<Fact>]
    let ``GraphRowControl should bias the commit marker toward the text baseline`` () =
        test <@ GraphRowControl.GetCommitMarkerCenterY 24.0 = 14.0 @>

    [<Fact>]
    let ``MainWindowNavigation should clamp selection movement within the list`` () =
        Assert.Equal(0, MainWindowNavigation.GetNextIndex(-1, 3, 1))
        Assert.Equal(2, MainWindowNavigation.GetNextIndex(-1, 3, -1))
        Assert.Equal(0, MainWindowNavigation.GetNextIndex(0, 3, -1))
        Assert.Equal(2, MainWindowNavigation.GetNextIndex(2, 3, 1))
        Assert.Equal(2, MainWindowNavigation.GetNextIndex(1, 3, 1))
        Assert.Equal(0, MainWindowNavigation.GetNextIndex(1, 3, -1))
        Assert.Equal(-1, MainWindowNavigation.GetNextIndex(1, 0, 1))

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

        let result = GitParsing.parseBlamePorcelain blame
        test <@ result.[20].Hash = "abc123abc123abc123abc123abc123abc123abc1" @>
        test <@ result.[20].AuthorName = "Jane Doe" @>
        test <@ result.[21].AuthorEmail = "john@example.com" @>

    [<Fact>]
    let ``parseStartupTargets should accept branch, sha, tag, and all`` () =
        let result =
            GitStartup.parseStartupTargets
                [|
                    "--branch"
                    "main"
                    "--sha=abc123"
                    "--tag"
                    "v1.0.0"
                |]

        match result with
        | Error err -> failwith (GitStartup.describeError err)
        | Ok targets ->
            test <@ targets = [ GitStartup.StartupTarget.Branch "main"; GitStartup.StartupTarget.Sha "abc123"; GitStartup.StartupTarget.Tag "v1.0.0" ] @>

        let allResult = GitStartup.parseStartupTargets [| "--all" |]
        match allResult with
        | Error err -> failwith (GitStartup.describeError err)
        | Ok targets -> test <@ targets = [ GitStartup.StartupTarget.All ] @>

    [<Fact>]
    let ``parseStartupOptions should accept the major app options`` () =
        let result =
            GitStartup.parseStartupOptions
                [|
                    "--all"
                    "--show-branch-refs"
                    "--show-stashes"
                    "--diff-context=10"
                    "--diff-presentation"
                    "side-by-side"
                    "--search"
                    "needle"
                    "--search-scope=message"
                    "--select=abc123"
                |]

        match result with
        | Error err -> failwith (GitStartup.describeError err)
        | Ok options ->
            test <@ options.StartupTargets = [ GitStartup.StartupTarget.All ] @>
            test <@ options.ShowBranchRefs @>
            test <@ options.ShowStashes @>
            test <@ options.DiffContextLines = 10 @>
            test <@ options.DiffLayout = DiffLayout.SideBySide @>
            test <@ options.SearchQuery = "needle" @>
            test <@ options.SearchScopeKey = "commit" @>
            test <@ options.SelectedCommitHash = Some "abc123" @>

    [<Fact>]
    let ``tryDiscoverRepositoryPath should locate the current repository`` () =
        withTempRepository (fun root _ ->
            match GitService.tryDiscoverRepositoryPath() with
            | null -> failwith "Expected a repository path."
            | repoPath ->
                test <@ repoPath.Contains(root) @>)

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
    let ``fetchDiffFileContent should cache file content per commit hash and context lines`` () =
        withTempRepository (fun root repo ->
            let firstCommit = commitFile repo root "foo.txt" "one" "first commit"
            let secondCommit = commitFile repo root "foo.txt" "two" "second commit"

            match runFlow root (GitService.fetchDiffFileContent 3 firstCommit.Sha "foo.txt" "foo.txt") with
            | Error err -> failwith (GitError.describe err)
            | Ok firstFile ->
                test <@ firstFile.Hunks.Head.Lines |> List.exists (fun line -> line.Type = Models.Added && line.Content = "one") @>

            match runFlow root (GitService.fetchDiffFileContent 3 secondCommit.Sha "foo.txt" "foo.txt") with
            | Error err -> failwith (GitError.describe err)
            | Ok secondFile ->
                test <@ secondFile.Hunks.Head.Lines |> List.exists (fun line -> line.Type = Models.Added && line.Content = "two") @>)

    [<Fact>]
    let ``fetchDiffFileContent should respect diff context line counts`` () =
        withTempRepository (fun root repo ->
            let _ = commitFile repo root "foo.txt" "line1\nline2\nline3\nline4\nline5\n" "base commit"
            let updatedCommit = commitFile repo root "foo.txt" "line1\nline2\nline3 changed\nline4\nline5\n" "updated commit"

            match runFlow root (GitService.fetchDiffFileList updatedCommit.Sha) with
            | Error err -> failwith (GitError.describe err)
            | Ok files ->
                let file = files.Head

                match runFlow root (GitService.fetchDiffFileContent 0 updatedCommit.Sha file.OldPath file.NewPath) with
                | Error err -> failwith (GitError.describe err)
                | Ok noContext ->
                    test <@ noContext.Hunks.Head.Lines |> List.forall (fun line -> line.Type <> Models.Context) @>

                    match runFlow root (GitService.fetchDiffFileContent 3 updatedCommit.Sha file.OldPath file.NewPath) with
                    | Error err -> failwith (GitError.describe err)
                    | Ok withContext ->
                        test <@ withContext.Hunks.Head.Lines |> List.exists (fun line -> line.Type = Models.Context) @>
                        test <@ withContext.Hunks.Head.Lines.Length > noContext.Hunks.Head.Lines.Length @>)

    [<Fact>]
    let ``fetchDiffFileFullContext should include every unchanged line of one file`` () =
        withTempRepository (fun root repo ->
            let lines = [ 1 .. 60 ] |> List.map (sprintf "line%d")
            let _ = commitFile repo root "foo.txt" (String.Join("\n", lines) + "\n") "base commit"
            let changed = lines |> List.map (fun line -> if line = "line30" then "line30 changed" else line)
            let updatedCommit = commitFile repo root "foo.txt" (String.Join("\n", changed) + "\n") "updated commit"

            match runFlow root (GitService.fetchDiffFileFullContext updatedCommit.Sha "foo.txt" "foo.txt") with
            | Error err -> failwith (GitError.describe err)
            | Ok file ->
                let all = file.Hunks |> List.collect _.Lines
                test <@ file.Hunks.Length = 1 @>
                test <@ all |> List.filter (fun line -> line.Type = Models.Context) |> List.length = 59 @>
                test <@ all |> List.exists (fun line -> line.Type = Models.Added && line.Content = "line30 changed") @>
                test <@ file.NewLineCount = Some 60 @>)

    [<Fact>]
    let ``fetchDiffFileList should identify added and deleted files like their loaded content`` () =
        withTempRepository (fun root repo ->
            let _ = commitFile repo root "old.txt" "gone\n" "base commit"
            File.Delete(Path.Combine(root, "old.txt"))
            File.WriteAllText(Path.Combine(root, "new.txt"), "fresh\n")
            Commands.Stage(repo, "*")
            let signature = Signature("Test", "test@example.com", DateTimeOffset.Now)
            let changed = repo.Commit("add and delete", signature, signature)

            match runFlow root (GitService.fetchDiffFileList changed.Sha) with
            | Error err -> failwith (GitError.describe err)
            | Ok files ->
                let keys = files |> List.map (fun file -> file.OldPath, file.NewPath) |> List.sort
                test <@ keys = [ "/dev/null", "new.txt"; "old.txt", "/dev/null" ] @>

                for file in files do
                    match runFlow root (GitService.fetchDiffFileContent 3 changed.Sha file.OldPath file.NewPath) with
                    | Error err -> failwith (GitError.describe err)
                    | Ok content -> test <@ (content.OldPath, content.NewPath) = (file.OldPath, file.NewPath) @>)

    [<Fact>]
    let ``GitCache should retain diff file lists between calls`` () =
        withTempRepository (fun root repo ->
            let commit = commitFile repo root "foo.txt" "one\n" "first commit"
            let env = GitService.environment root
            let first = Flow.run env (GitService.fetchDiffFileList commit.Sha) |> Exit.toResult
            File.WriteAllText(Path.Combine(root, ".git", "objects", "sentinel"), "")
            let second = Flow.run env (GitService.fetchDiffFileList commit.Sha) |> Exit.toResult
            match first, second with
            | Ok a, Ok b -> test <@ obj.ReferenceEquals(a, b) @>
            | _ -> failwith "expected file lists")

    [<Fact>]
    let ``resolveCommit should accept short hashes, branches, tags and relative revisions`` () =
        withTempRepository (fun root repo ->
            let first = commitFile repo root "a.txt" "1" "first"
            let second = commitFile repo root "a.txt" "2" "second"
            repo.ApplyTag("v1", first.Sha) |> ignore
            let resolve revision =
                match runFlow root (GitService.resolveCommit revision) with
                | Ok hash -> Some hash
                | Error _ -> None
            test <@ resolve (second.Sha.Substring(0, 7)) = Some second.Sha @>
            test <@ resolve "HEAD~1" = Some first.Sha @>
            test <@ resolve "v1" = Some first.Sha @>
            test <@ resolve repo.Head.FriendlyName = Some second.Sha @>
            test <@ resolve "no-such-thing" = None @>)

    [<Fact>]
    let ``parseStartupOptions should report what it couldn't read`` () =
        let error args = match GitStartup.parseStartupOptions args with Ok _ -> None | Error e -> Some(GitStartup.describeError e)
        test <@ error [| "--bogus" |] = Some "Unrecognized startup argument: --bogus" @>
        test <@ error [| "--diff-context" |] = Some "Missing diff context line count after --diff-context." @>
        test <@ error [| "--diff-context=many" |] = Some "Invalid diff context line count: many" @>
        test <@ error [| "--diff-presentation"; "sideways" |] = Some "Invalid diff presentation mode: sideways" @>
        test <@ error [| "--search-scope=nowhere" |] = Some "Invalid search scope: nowhere" @>
        test <@ error [| "--log=" |] = Some "Missing log file after --log." @>
        test <@ error [| "-S" |] = Some "Unrecognized startup argument: -S" @>
        // Once --all is given, --branch/--sha/--tag are ignored, including a missing value.
        test <@ error [| "--all"; "--branch" |] = None && error [| "--branch" |] = Some "Missing branch name after --branch." @>
        test <@ error [| "--search="; "-G"; "--x"; "--"; "-weird" |] = None @>

    [<Fact>]
    let ``parseStartupOptions should accept gitk-style select-commit`` () =
        let selected args = match GitStartup.parseStartupOptions args with Ok options -> options.SelectedCommitHash | Error e -> failwith (GitStartup.describeError e)
        test <@ selected [| "--select-commit=HEAD~2" |] = Some "HEAD~2" @>
        test <@ selected [| "--select-commit"; "main" |] = Some "main" @>
        test <@ selected [| "--select"; "abc123" |] = Some "abc123" @>

    [<Fact>]
    let ``parseHunks should keep content lines that look like file headers`` () =
        let hunks = GitParsing.parseHunks "@@ -1,2 +1,3 @@\n context\n+++ b/foo.txt\n--- a/foo.txt\n+added\n\\ No newline at end of file\n"
        let lines = hunks.Head.Lines |> List.map (fun line -> line.Type, line.Content, line.OldLineNo, line.NewLineNo)
        test <@ hunks.Length = 1 @>
        test <@ lines = [ Models.Context, "context", Some 1, Some 1
                          Models.Added, "++ b/foo.txt", None, Some 2
                          Models.Removed, "-- a/foo.txt", Some 2, None
                          Models.Added, "added", None, Some 3 ] @>

    [<Fact>]
    let ``fetchDiffFileContent should handle added, deleted and binary files`` () =
        withTempRepository (fun root repo ->
            let _ = commitFile repo root "old.txt" "gone\n" "base commit"
            File.Delete(Path.Combine(root, "old.txt"))
            File.WriteAllText(Path.Combine(root, "new.txt"), "a\nb")
            File.WriteAllBytes(Path.Combine(root, "image.bin"), [| 0uy; 1uy; 2uy; 0uy |])
            Commands.Stage(repo, "*")
            let signature = Signature("Test", "test@example.com", DateTimeOffset.Now)
            let changed = repo.Commit("mixed", signature, signature)

            let load oldPath newPath =
                match runFlow root (GitService.fetchDiffFileContent 3 changed.Sha oldPath newPath) with
                | Ok file -> file
                | Error err -> failwith (GitError.describe err)

            let added = load "/dev/null" "new.txt"
            test <@ added.NewLineCount = Some 2 @>
            test <@ added.Hunks.Head.Lines |> List.map (fun line -> line.Type, line.Content) = [ Models.Added, "a"; Models.Added, "b" ] @>
            let deleted = load "old.txt" "/dev/null"
            test <@ deleted.NewLineCount = None @>
            test <@ deleted.Hunks.Head.Lines |> List.map (fun line -> line.Type, line.Content) = [ Models.Removed, "gone" ] @>
            let binary = load "/dev/null" "image.bin"
            test <@ binary.Hunks = [] @>)

    [<Fact>]
    let ``fetchDiffFileContent should load root commit file contents`` () =
        withTempRepository (fun root repo ->
            let firstCommit = commitFile repo root "foo.txt" "one" "first commit"

            match runFlow root (GitService.fetchDiffFileList firstCommit.Sha) with
            | Error err -> failwith (GitError.describe err)
            | Ok files ->
                test <@ files.Length = 1 @>
                test <@ files.Head.OldPath = "/dev/null" @>
                test <@ files.Head.NewPath = "foo.txt" @>

                match runFlow root (GitService.fetchDiffFileContent 3 firstCommit.Sha files.Head.OldPath files.Head.NewPath) with
                | Error err -> failwith (GitError.describe err)
                | Ok file ->
                    test <@ file.OldPath = "/dev/null" @>
                    test <@ file.NewPath = "foo.txt" @>
                    test <@ file.Hunks.Head.Lines |> List.exists (fun line -> line.Type = Models.Added && line.Content = "one") @>)

    [<Fact>]
    let ``createTag and createBranch should update repository refs without the CLI`` () =
        withTempRepository (fun root repo ->
            let commit = commitFile repo root "base.txt" "base" "base commit"

            match runFlow root (GitService.createTag commit.Sha "v1.0.0") with
            | Ok _ -> ()
            | Error err -> failwith (GitError.describe err)

            match runFlow root (GitService.createBranch commit.Sha "topic") with
            | Ok _ -> ()
            | Error err -> failwith (GitError.describe err)

            test <@ repo.Tags["v1.0.0"] <> null @>
            test <@ repo.Tags["v1.0.0"].Target.Id.Sha = commit.Sha @>
            test <@ repo.Branches["topic"] <> null @>
            test <@ repo.Branches["topic"].Tip.Sha = commit.Sha @>)

    [<Fact>]
    let ``revision comparison diffs from merge base to arbitrary target revision`` () =
        withTempRepository (fun root repo ->
            let common = commitFile repo root "common.txt" "common" "common"
            let main = repo.CreateBranch("main", common)
            let topic = repo.CreateBranch("topic", common)
            Commands.Checkout(repo, topic) |> ignore
            let topicCommit = commitFile repo root "topic.txt" "topic" "topic"
            Commands.Checkout(repo, main) |> ignore
            let _ = commitFile repo root "main.txt" "main" "main"

            match runFlow root (GitService.resolveRevisionComparison "main" topicCommit.Sha) with
            | Error err -> failwith (GitError.describe err)
            | Ok comparison ->
                test <@ comparison.BaseHash = common.Sha @>
                test <@ comparison.TargetHash = topicCommit.Sha @>
                match runFlow root (GitService.fetchRevisionDiff false 3 comparison) with
                | Error err -> failwith (GitError.describe err)
                | Ok files ->
                    test <@ files |> List.map _.NewPath = [ "topic.txt" ] @>

            test <@ GitService.defaultComparisonBase root = "main" @>
            test <@ GitService.comparisonRevisions root |> Array.contains "topic" @>)

    [<Fact>]
    let ``fetchHistory should hide stashes by default`` () =
        withTempRepository (fun root repo ->
            let _ = commitFile repo root "app.txt" "base" "base commit"
            File.WriteAllText(Path.Combine(root, "app.txt"), "stashed changes")
            let signature = Signature("GitKay Tests", "gitkay@example.com", DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero))

            match repo.Stashes.Add(signature, "wip") with
            | null -> failwith "Stash creation failed."
            | stash ->
                let stashHash = stash.WorkTree.Sha

                match runFlow root (GitService.fetchHistory None false [ GitStartup.StartupTarget.All ]) with
                | Error err -> failwith (GitError.describe err)
                | Ok commits ->
                    test <@ commits |> List.exists (fun commit -> commit.Hash = stashHash) |> not @>
                    test <@ commits |> List.exists (fun commit -> commit.Refs |> List.exists (fun ref -> ref.Kind = Models.CommitRefKind.Stash)) |> not @>
                    test <@ commits |> List.exists (fun commit -> commit.Refs |> List.exists (fun ref -> ref.Name = "stash@{0}")) |> not @>)

    [<Fact>]
    let ``fetchHistory should include stashes when explicitly enabled`` () =
        withTempRepository (fun root repo ->
            let _ = commitFile repo root "app.txt" "base" "base commit"
            File.WriteAllText(Path.Combine(root, "app.txt"), "stashed changes")
            let signature = Signature("GitKay Tests", "gitkay@example.com", DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero))

            match repo.Stashes.Add(signature, "wip") with
            | null -> failwith "Stash creation failed."
            | stash ->
                let stashHash = stash.WorkTree.Sha

                match runFlow root (GitService.fetchHistory None true [ GitStartup.StartupTarget.All ]) with
                | Error err -> failwith (GitError.describe err)
                | Ok commits ->
                    test <@ commits |> List.exists (fun commit -> commit.Hash = stashHash) @>
                    test <@ commits |> List.exists (fun commit -> commit.Refs |> List.exists (fun ref -> ref.Kind = Models.CommitRefKind.Stash)) @>
                    test <@ commits |> List.exists (fun commit -> commit.Refs |> List.exists (fun ref -> ref.Name = "stash@{0}") ) @>)

    [<Fact>]
    let ``resetTo should move HEAD and working tree to the target commit`` () =
        withTempRepository (fun root repo ->
            let baseCommit = commitFile repo root "app.txt" "base" "base commit"
            let _ = repo.CreateBranch("main", baseCommit)
            Commands.Checkout(repo, repo.Branches["main"]) |> ignore

            let updatedCommit = commitFile repo root "app.txt" "updated" "updated commit"

            match runFlow root (GitService.resetTo baseCommit.Sha true) with
            | Ok _ -> ()
            | Error err -> failwith (GitError.describe err)

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

            match runFlow root (GitService.resetTo baseCommit.Sha false) with
            | Ok _ -> ()
            | Error err -> failwith (GitError.describe err)

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

            match runFlow root (GitService.cherryPick featureCommit.Sha) with
            | Ok _ -> ()
            | Error err -> failwith (GitError.describe err)

            use pickedRepo = new Repository(root)
            test <@ File.ReadAllText(Path.Combine(root, "app.txt")) = "feature" @>
            test <@ pickedRepo.Head.Tip.Sha <> featureCommit.Sha @>

            let pickedCommit = pickedRepo.Head.Tip

            match runFlow root (GitService.revert pickedCommit.Sha) with
            | Ok _ -> ()
            | Error err -> failwith (GitError.describe err)

            use revertedRepo = new Repository(root)
            test <@ File.ReadAllText(Path.Combine(root, "app.txt")) = "base" @>
            test <@ revertedRepo.Head.Tip.Sha <> pickedCommit.Sha @>
            test <@ revertedRepo.Head.Tip.Sha <> baseCommit.Sha @>)

    let private searchCommit hash subject message (author: string) (refs: Models.CommitRef list) timestamp : Models.Commit =
        { Hash = hash; AuthorName = author; AuthorEmail = author.ToLowerInvariant().Replace(" ", ".") + "@example.com"
          Timestamp = timestamp; Parents = []; Subject = subject; Message = message; Refs = refs }

    let private searchCommits () =
        [ searchCommit "abc12345abc12345abc12345abc12345abc12345" "Fix parser" "Fix parser\n\nNeedle body" "Jane Doe" [ RefHelpers.branchRef "main"; RefHelpers.tagRef "v1.0" ] 1710000000L
          searchCommit "def67890def67890def67890def67890def67890" "Add docs" "Add docs" "John Smith" [ RefHelpers.remoteRef "origin/release/1.0" ] 1720000000L ]

    let private searchDiffLoader hash =
        match hash with
        | "abc12345abc12345abc12345abc12345abc12345" ->
            Flow.ok
                [ sampleFile "src/needle.txt" "src/needle.txt"
                      [ { Type = Models.Context; Content = "context needle"; OldLineNo = Some 1; NewLineNo = Some 1 }
                        { Type = Models.Added; Content = "let findCommit = 1"; OldLineNo = None; NewLineNo = Some 2 } ] ]
        | _ -> Flow.ok [ sampleFile "docs/readme.md" "docs/readme.md" [ { Type = Models.Removed; Content = "old docs"; OldLineNo = Some 1; NewLineNo = None } ] ]

    let private search mode useRegex query =
        match runFlow "" (GitSearch.searchCommitsWithDiffLoader DateTimeOffset.Now (searchCommits ()) mode useRegex query searchDiffLoader) with
        | Error err -> failwith (GitError.describe err)
        | Ok results -> results

    let private hashes (results: GitSearch.Result list) = results |> List.map (fun r -> r.Commit.Hash.Substring(0, 3))

    [<Fact>]
    let ``path searches should read changed paths only, never full diffs, and report progress`` () =
        let diffLoads = ref 0
        let progress = Collections.Generic.List<int * int>()
        let loaders : GitSearch.Loaders<GitService.GitEnv> =
            { OpenReader =
                fun () ->
                    { ChangedPaths = fun hash -> Flow.ok (if hash.StartsWith "abc" then [ "src/needle.txt", "src/needle.txt" ] else [ "docs/readme.md", "docs/readme.md" ])
                      MatchChangedLines = fun _ predicates -> diffLoads.Value <- diffLoads.Value + 1; Flow.ok (predicates |> List.map (fun _ -> false))
                      Release = ignore }
              Workers = 2
              Progress = fun checkedCount total -> lock progress (fun () -> progress.Add((checkedCount, total)))
              Found = ignore }
        let results =
            match runFlow "" (GitSearch.searchCommitsWith DateTimeOffset.Now (searchCommits ()) GitSearch.Path false "needle" loaders) with
            | Ok results -> results
            | Error err -> failwith (GitError.describe err)
        test <@ hashes results = [ "abc" ] && diffLoads.Value = 0 @>
        test <@ progress.[0] = (0, 2) && progress.[progress.Count - 1] = (2, 2) @>

    [<Fact>]
    let ``invalid after and before dates should be reported, valid ones not`` () =
        let now = DateTimeOffset(2026, 9, 14, 0, 0, 0, TimeSpan.Zero)
        test <@ GitSearch.invalidDates now GitSearch.Commit "fix after:2024-05-01 before:\"2 weeks ago\"" = [] @>
        test <@ GitSearch.invalidDates now GitSearch.Commit "after:someday before:yesterday" = [ { Field = GitSearch.After; Text = "someday" } ] @>
        test <@ GitSearch.describeDateProblem { Field = GitSearch.Before; Text = "soon" } = "“soon” isn't a date, so before: is ignored — try 2024-05-01, “2 weeks ago” or yesterday" @>

    [<Fact>]
    let ``partial search results should show while the search runs and only for the current search`` () =
        let model0, _ = App.init [||]
        let running = { model0 with SearchQuery = "needle"; SearchStartedAtTicks = Some 42L }
        let result : GitSearch.Result =
            { Commit = (searchCommits ()).Head; MatchKinds = [ GitSearch.TextMatch ]; MatchSummary = "text"; MatchedPaths = []; MatchedRefs = [] }
        let partial, _ = App.update (App.Msg.SearchPartialResults(42L, [ result ])) running
        test <@ partial.SearchResults = Some [ result ] && partial.SearchStartedAtTicks = Some 42L @>
        let stale, _ = App.update (App.Msg.SearchPartialResults(7L, [ result ])) running
        test <@ stale.SearchResults = None @>

    [<Fact>]
    let ``parallel search should report every match through Found and keep history order`` () =
        let found = Collections.Concurrent.ConcurrentBag<string>()
        let commits = [ for index in 0 .. 99 -> searchCommit (sprintf "%040d" index) $"commit {index}" "" "Jane" [] (int64 index) ]
        let loaders : GitSearch.Loaders<GitService.GitEnv> =
            { OpenReader =
                fun () ->
                    { ChangedPaths = fun hash -> Flow.ok [ (if hash.EndsWith "7" then "hit.txt" else "miss.txt"), "x" ]
                      MatchChangedLines = fun _ predicates -> Flow.ok (predicates |> List.map (fun _ -> false))
                      Release = ignore }
              Workers = 4
              Progress = fun _ _ -> ()
              Found = fun result -> found.Add result.Commit.Hash }
        let results =
            match runFlow "" (GitSearch.searchCommitsWith DateTimeOffset.Now commits GitSearch.Path false "hit" loaders) with
            | Ok results -> results
            | Error err -> failwith (GitError.describe err)
        let expected = [ for index in 0 .. 99 do if index % 10 = 7 then yield sprintf "%040d" index ]
        test <@ (results |> List.map _.Commit.Hash) = expected @>
        test <@ (found |> Seq.sort |> List.ofSeq) = expected @>

    [<Fact>]
    let ``cancelling a running search should stop it and keep the query`` () =
        let model0, _ = App.init [||]
        let running = { model0 with SearchQuery = "Path"; SearchStartedAtTicks = Some 42L; SearchProgress = Some(10, 100) }
        let progressed, _ = App.update (App.Msg.SearchProgressed(42L, 50, 100)) running
        test <@ progressed.SearchProgress = Some(50, 100) @>
        let stale, _ = App.update (App.Msg.SearchProgressed(7L, 99, 100)) running
        test <@ stale.SearchProgress = Some(10, 100) @>
        let cancelled, _ = App.update App.Msg.CancelSearch progressed
        test <@ cancelled.SearchStartedAtTicks = None && cancelled.SearchProgress = None && cancelled.SearchQuery = "Path" @>
        test <@ cancelled.Status = "Search cancelled: Path" @>

    [<Fact>]
    let ``commit mode should match headline, message, hash and refs but not authors or diffs`` () =
        test <@ hashes (search GitSearch.Commit false "needle") = [ "abc" ] @>
        test <@ hashes (search GitSearch.Commit false "release") = [ "def" ] @>
        test <@ hashes (search GitSearch.Commit false "def678") = [ "def" ] @>
        test <@ hashes (search GitSearch.Commit false "Smith") = [] @>
        test <@ hashes (search GitSearch.Commit false "findCommit") = [] @>
        let hit = (search GitSearch.Commit false "release").Head
        test <@ hit.MatchKinds = [ GitSearch.RefMatch ] && hit.MatchedRefs = [ "origin/release/1.0" ] @>
        test <@ hit.MatchSummary = "ref; refs: origin/release/1.0" @>
        // A term that doesn't match excludes the commit even when an earlier term matched.
        test <@ hashes (search GitSearch.Commit false "release author:nobody") = [] @>

    [<Fact>]
    let ``path and diff modes should search changed files and only added or removed lines`` () =
        test <@ hashes (search GitSearch.Path false "src/") = [ "abc" ] @>
        test <@ (search GitSearch.Path false "src/").Head.MatchedPaths = [ "src/needle.txt" ] @>
        test <@ hashes (search GitSearch.Diff false "findCommit") = [ "abc" ] @>
        test <@ hashes (search GitSearch.Diff false "context needle") = [] @>
        test <@ hashes (search GitSearch.Diff false "old docs") = [ "def" ] @>

    [<Fact>]
    let ``prefixes combine with AND across fields and dates`` () =
        test <@ hashes (search GitSearch.Commit false "author:jane") = [ "abc" ] @>
        test <@ hashes (search GitSearch.Commit false "author:jane path:docs") = [] @>
        test <@ hashes (search GitSearch.Commit false "author:john diff:docs") = [ "def" ] @>
        test <@ hashes (search GitSearch.Diff false "ref:main findCommit") = [ "abc" ] @>
        test <@ hashes (search GitSearch.Commit false "after:2024-06-01") = [ "def" ] @>
        test <@ hashes (search GitSearch.Commit false "before:2024-06-01") = [ "abc" ] @>
        test <@ hashes (search GitSearch.Commit false "message:\"Fix parser\"") = [ "abc" ] @>

    [<Fact>]
    let ``regex option should apply to every field and tolerate invalid patterns`` () =
        test <@ hashes (search GitSearch.Diff true "find\\w+ =") = [ "abc" ] @>
        test <@ hashes (search GitSearch.Commit true "^Add") = [ "def" ] @>
        test <@ hashes (search GitSearch.Path true "^src/.*\\.txt$") = [ "abc" ] @>
        test <@ hashes (search GitSearch.Commit true "author:^john") = [ "def" ] @>
        test <@ hashes (search GitSearch.Commit true "Fix (") = [] @>

    [<Fact>]
    let ``parseQuery and formatQuery should round-trip prefixed and quoted terms`` () =
        let terms = GitSearch.parseQuery GitSearch.Commit "fonts author:\"Jane Doe\" path:src/ bogus:x"
        test <@ terms |> List.map (fun t -> t.Field, t.Text) = [ GitSearch.CommitInfo, "fonts"; GitSearch.Author, "Jane Doe"; GitSearch.ChangedPath, "src/"; GitSearch.CommitInfo, "bogus:x" ] @>
        test <@ GitSearch.formatQuery GitSearch.Commit terms = "fonts author:\"Jane Doe\" path:src/ bogus:x" @>
        test <@ GitSearch.formatQuery GitSearch.Path (GitSearch.parseQuery GitSearch.Path "src/ author:jane") = "src/ author:jane" @>

    [<Fact>]
    let ``working tree status parses porcelain v2 records`` () =
        let output =
            String.concat "\000"
                [ "1 MM N... 100644 100644 100644 aaaa bbbb a.txt"
                  "2 R. N... 100644 100644 100644 cccc cccc R100 renamed file.txt"
                  "r.txt"
                  "u UU N... 100644 100644 100644 100644 dd ee ff conflict.txt"
                  "? d/sp ace.txt"
                  "! ignored.log"
                  "" ]
        let entries = WorkingTree.parseStatus output
        test <@ entries |> List.map (fun e -> e.Path, e.OriginalPath, e.Staged, e.Unstaged, e.Untracked) =
                    [ "a.txt", "a.txt", WorkingTree.Modified, WorkingTree.Modified, false
                      "renamed file.txt", "r.txt", WorkingTree.Renamed, WorkingTree.Unchanged, false
                      "conflict.txt", "conflict.txt", WorkingTree.Conflicted, WorkingTree.Conflicted, false
                      "d/sp ace.txt", "d/sp ace.txt", WorkingTree.Unchanged, WorkingTree.Unchanged, true ] @>
        test <@ WorkingTree.summary entries = "3 staged · 2 unstaged · 1 untracked" @>
        test <@ entries |> List.map WorkingTree.marker = [ "◐"; "●"; "◐"; "+" ] @>
        test <@ WorkingTree.summary [] = "" && WorkingTree.parseStatus "" = [] @>

    [<Fact>]
    let ``patch parsing keeps header-like content, spaces in paths and pure renames`` () =
        let patch =
            String.concat "\n"
                [ "diff --git a/sp ace.txt b/sp ace.txt"
                  "index 1..2 100644"
                  "--- a/sp ace.txt\t"
                  "+++ b/sp ace.txt\t"
                  "@@ -1,2 +1,2 @@"
                  "--- removed dashes"
                  "+++ added pluses"
                  " kept"
                  "\\ No newline at end of file"
                  "diff --git a/old.txt b/new.txt"
                  "similarity index 100%"
                  "rename from old.txt"
                  "rename to new.txt"
                  "diff --git a/gone.txt b/gone.txt"
                  "deleted file mode 100644"
                  "--- a/gone.txt"
                  "+++ /dev/null"
                  "@@ -1 +0,0 @@"
                  "-bye"
                  "" ]
        let files = WorkingTree.parsePatch patch
        test <@ files |> List.map (fun f -> f.OldPath, f.NewPath, f.Hunks.Length) = [ "sp ace.txt", "sp ace.txt", 1; "old.txt", "new.txt", 0; "gone.txt", "/dev/null", 1 ] @>
        let lines = files.Head.Hunks.Head.Lines |> List.map (fun l -> l.Type, l.Content, l.OldLineNo, l.NewLineNo)
        test <@ lines = [ Models.Removed, "-- removed dashes", Some 1, None; Models.Added, "++ added pluses", None, Some 1; Models.Context, "kept", Some 2, Some 2 ] @>

    [<Fact>]
    let ``working tree changes load staged, unstaged and untracked diffs`` () =
        withTempRepository (fun root repo ->
            commitFile repo root "a.txt" "one\ntwo\n" "init" |> ignore
            commitFile repo root "old name.txt" "same\n" "second" |> ignore
            writeFile root "a.txt" "one\nTWO\n"
            Commands.Stage(repo, "a.txt")
            writeFile root "a.txt" "one\nTWO\nthree\n"
            Commands.Move(repo, "old name.txt", "new name.txt")
            writeFile root "notes/draft file.md" "# Draft\nhello\n"
            // The app's RepoPath is the git directory, not the working tree.
            match runFlow (Path.Combine(root, ".git")) (GitService.fetchWorkingTreeChanges 3) with
            | Error error -> failwith (GitError.describe error)
            | Ok changes ->
                let paths (files: Models.FileDiff list) = files |> List.map (fun f -> f.OldPath, f.NewPath)
                test <@ WorkingTree.summary changes.Entries = "2 staged · 1 unstaged · 1 untracked" @>
                test <@ paths changes.Staged = [ "a.txt", "a.txt"; "old name.txt", "new name.txt" ] @>
                test <@ paths changes.Unstaged = [ "a.txt", "a.txt" ] && paths changes.Untracked = [ "/dev/null", "notes/draft file.md" ] @>
                let stagedLines = changes.Staged.Head.Hunks |> List.collect _.Lines |> List.filter (fun l -> l.Type <> Models.Context) |> List.map _.Content
                let unstagedLines = changes.Unstaged.Head.Hunks |> List.collect _.Lines |> List.filter (fun l -> l.Type <> Models.Context) |> List.map _.Content
                test <@ stagedLines = [ "two"; "TWO" ] && unstagedLines = [ "three" ] @>
                test <@ changes.Untracked.Head.Hunks.Head.Lines |> List.map _.Content = [ "# Draft"; "hello" ] @>)

    [<Fact>]
    let ``working tree file loads one section's change with its whole content`` () =
        withTempRepository (fun root repo ->
            let content = [ 1..30 ] |> List.map string |> String.concat "\n"
            commitFile repo root "a.txt" (content + "\n") "init" |> ignore
            writeFile root "a.txt" ((content.Replace("15", "fifteen")) + "\n")
            Commands.Stage(repo, "a.txt")
            writeFile root "a.txt" ((content.Replace("15", "fifteen").Replace("29", "twenty-nine")) + "\n")
            let changed (file: Models.FileDiff) = file.Hunks |> List.collect _.Lines |> List.filter (fun l -> l.Type <> Models.Context) |> List.map _.Content
            let lineCount (file: Models.FileDiff) = file.Hunks |> List.collect _.Lines |> List.filter (fun l -> l.Type <> Models.Removed) |> List.length
            let gitDir = Path.Combine(root, ".git")
            match runFlow gitDir (GitService.fetchWorkingTreeFile WorkingTree.Staged "a.txt" "a.txt"), runFlow gitDir (GitService.fetchWorkingTreeFile WorkingTree.Unstaged "a.txt" "a.txt") with
            | Ok staged, Ok unstaged ->
                test <@ changed staged = [ "15"; "fifteen" ] && lineCount staged = 30 @>
                test <@ changed unstaged = [ "29"; "twenty-nine" ] && lineCount unstaged = 30 @>
            | Error error, _ | _, Error error -> failwith (GitError.describe error))

    // ----- Commit window: building partial patches and applying them with git -----

    let private numbered (lines: string list) = String.concat "\n" lines + "\n"

    /// Selected lines of a file's diff, by hunk and position, read from the parsed diff so the numbering matches the UI.
    let private pick (raw: string) (choose: int -> int -> Models.DiffLine -> bool) =
        match WorkingTree.parsePatch raw with
        | file :: _ ->
            file.Hunks
            |> List.mapi (fun hunkIndex hunk ->
                hunk.Lines
                |> List.mapi (fun lineIndex line -> hunkIndex, lineIndex, line)
                |> List.filter (fun (h, l, line) -> line.Type <> Models.Context && choose h l line))
            |> List.concat
            |> List.map (fun (h, l, line) -> ({ Hunk = h; Line = l; Type = line.Type; Content = line.Content } : PatchBuilder.SelectedLine))
        | [] -> []

    let private run gitDir flow =
        match runFlow gitDir flow with
        | Ok value -> value
        | Error error -> failwith (GitError.describe error)

    [<Fact>]
    let ``staging chosen lines stages only those lines`` () =
        withTempRepository (fun root repo ->
            let gitDir = Path.Combine(root, ".git")
            commitFile repo root "a.txt" (numbered [ "1"; "2"; "3"; "4"; "5"; "6"; "7"; "8"; "9"; "10"; "11"; "12" ]) "init" |> ignore
            writeFile root "a.txt" (numbered [ "1"; "TWO"; "3"; "4"; "5"; "6"; "7"; "8"; "9"; "10"; "ELEVEN"; "12"; "13" ])
            let raw = run gitDir (GitService.fetchRawFileDiff WorkingTree.Unstaged "a.txt")
            // Stage only "ELEVEN" replacing "11" (the second hunk), leaving "TWO" and the added "13" unstaged.
            let chosen = pick raw (fun _ _ line -> line.Content = "11" || line.Content = "ELEVEN")
            run gitDir (GitService.applyLines GitService.StageInIndex "a.txt" chosen)
            let staged = run gitDir (GitService.fetchRawFileDiff WorkingTree.Staged "a.txt")
            let unstaged = run gitDir (GitService.fetchRawFileDiff WorkingTree.Unstaged "a.txt")
            let changes (raw: string) = pick raw (fun _ _ _ -> true) |> List.map (fun line -> line.Type, line.Content)
            test <@ changes staged = [ Models.Removed, "11"; Models.Added, "ELEVEN" ] @>
            test <@ changes unstaged = [ Models.Removed, "2"; Models.Added, "TWO"; Models.Added, "13" ] @>
            test <@ File.ReadAllText(Path.Combine(root, "a.txt")) = numbered [ "1"; "TWO"; "3"; "4"; "5"; "6"; "7"; "8"; "9"; "10"; "ELEVEN"; "12"; "13" ] @>)

    [<Fact>]
    let ``unstaging and discarding chosen lines touch only those lines`` () =
        withTempRepository (fun root repo ->
            let gitDir = Path.Combine(root, ".git")
            commitFile repo root "a.txt" (numbered [ "one"; "two"; "three" ]) "init" |> ignore
            writeFile root "a.txt" (numbered [ "one"; "TWO"; "three"; "four"; "five" ])
            run gitDir (GitService.stageFiles [ "a.txt" ])
            let staged = run gitDir (GitService.fetchRawFileDiff WorkingTree.Staged "a.txt")
            // Unstage the added "five"; the rest stays staged.
            run gitDir (GitService.applyLines GitService.UnstageFromIndex "a.txt" (pick staged (fun _ _ line -> line.Content = "five")))
            let changes (raw: string) = pick raw (fun _ _ _ -> true) |> List.map (fun line -> line.Type, line.Content)
            test <@ changes (run gitDir (GitService.fetchRawFileDiff WorkingTree.Staged "a.txt")) = [ Models.Removed, "two"; Models.Added, "TWO"; Models.Added, "four" ] @>
            test <@ changes (run gitDir (GitService.fetchRawFileDiff WorkingTree.Unstaged "a.txt")) = [ Models.Added, "five" ] @>
            // Discard the unstaged "five" from the file itself.
            let unstaged = run gitDir (GitService.fetchRawFileDiff WorkingTree.Unstaged "a.txt")
            run gitDir (GitService.applyLines GitService.DiscardFromWorkingTree "a.txt" (pick unstaged (fun _ _ _ -> true)))
            test <@ File.ReadAllText(Path.Combine(root, "a.txt")) = numbered [ "one"; "TWO"; "three"; "four" ] @>)

    [<Fact>]
    let ``staging chosen lines of an untracked file adds it with intent-to-add first`` () =
        withTempRepository (fun root repo ->
            let gitDir = Path.Combine(root, ".git")
            commitFile repo root "a.txt" "one\n" "init" |> ignore
            writeFile root "new.txt" (numbered [ "first"; "second"; "third" ])
            // The UI selects from the diff GitKay builds for an untracked file, which git itself won't show yet.
            let changes0 = run gitDir (GitService.fetchWorkingTreeChanges 3)
            let untracked = changes0.Untracked |> List.find (fun file -> file.NewPath = "new.txt")
            let chosen =
                untracked.Hunks
                |> List.mapi (fun hunkIndex hunk ->
                    hunk.Lines
                    |> List.mapi (fun lineIndex line -> hunkIndex, lineIndex, line)
                    |> List.filter (fun (_, _, line) -> line.Content = "second"))
                |> List.concat
                |> List.map (fun (h, l, line) -> ({ Hunk = h; Line = l; Type = line.Type; Content = line.Content } : PatchBuilder.SelectedLine))
            run gitDir (GitService.applyLines GitService.StageInIndex "new.txt" chosen)
            let changes (raw: string) = pick raw (fun _ _ _ -> true) |> List.map (fun line -> line.Type, line.Content)
            test <@ changes (run gitDir (GitService.fetchRawFileDiff WorkingTree.Staged "new.txt")) = [ Models.Added, "second" ] @>
            test <@ changes (run gitDir (GitService.fetchRawFileDiff WorkingTree.Unstaged "new.txt")) = [ Models.Added, "first"; Models.Added, "third" ] @>
            test <@ File.ReadAllText(Path.Combine(root, "new.txt")) = numbered [ "first"; "second"; "third" ] @>)

    [<Fact>]
    let ``patch building keeps no-newline markers and refuses a changed diff`` () =
        let raw =
            numbered
                [ "diff --git a/a.txt b/a.txt"
                  "--- a/a.txt"
                  "+++ b/a.txt"
                  "@@ -1,2 +1,2 @@"
                  " one"
                  "-two"
                  "\\ No newline at end of file"
                  "+TWO"
                  "\\ No newline at end of file" ]
        let all = pick raw (fun _ _ _ -> true)
        match PatchBuilder.build PatchBuilder.Forward raw all with
        | Ok patch -> test <@ patch.Contains "-two\n\\ No newline at end of file\n+TWO\n\\ No newline at end of file" @>
        | Error error -> failwith (PatchBuilder.describeError error)
        let stale = all |> List.map (fun line -> { line with Content = line.Content + "!" })
        test <@ PatchBuilder.build PatchBuilder.Forward raw stale = Error PatchBuilder.DiffChanged @>
        test <@ PatchBuilder.build PatchBuilder.Forward raw [] = Error PatchBuilder.NothingSelected @>

    [<Fact>]
    let ``amending shows what the commit will contain, and unstaging takes a file out of it`` () =
        withTempRepository (fun root repo ->
            let gitDir = Path.Combine(root, ".git")
            commitFile repo root "a.txt" "one\n" "first" |> ignore
            writeFile root "a.txt" "two\n"
            writeFile root "b.txt" "new\n"
            run gitDir (GitService.stageFiles [ "a.txt"; "b.txt" ])
            run gitDir (GitService.commit { Amend = false; SignOff = false } "second\n")

            // Without amending the index matches HEAD, so nothing is staged.
            let plain = run gitDir (GitService.fetchWorkingTreeChanges 3)
            test <@ plain.Staged.IsEmpty @>

            // Amending shows the commit's own files, as git gui does.
            let amending = run gitDir (GitService.fetchWorkingTreeChangesFor true 3)
            test <@ amending.Staged |> List.map (fun file -> file.NewPath) = [ "a.txt"; "b.txt" ] @>

            // Unstaging while amending takes that file back to the commit being replaced.
            run gitDir (GitService.unstageFilesFor true [ "b.txt" ])
            let afterUnstage = run gitDir (GitService.fetchWorkingTreeChangesFor true 3)
            test <@ afterUnstage.Staged |> List.map (fun file -> file.NewPath) = [ "a.txt" ] @>)

    [<Fact>]
    let ``discarding lines, files and untracked files can be undone`` () =
        withTempRepository (fun root repo ->
            let gitDir = Path.Combine(root, ".git")
            commitFile repo root "a.txt" (numbered [ "one"; "two"; "three" ]) "init" |> ignore
            writeFile root "a.txt" (numbered [ "ONE"; "two"; "THREE" ])
            writeFile root "new.txt" (numbered [ "scratch" ])
            let read name = File.ReadAllText(Path.Combine(root, name))
            let edited = read "a.txt"

            // Lines: discard just the first change, then put it back.
            let raw = run gitDir (GitService.fetchRawFileDiff WorkingTree.Unstaged "a.txt")
            let firstChange = pick raw (fun _ _ line -> line.Content = "one" || line.Content = "ONE")
            let lineBackup = run gitDir (GitService.discardLinesWithBackup "a.txt" firstChange)
            test <@ read "a.txt" = numbered [ "one"; "two"; "THREE" ] @>
            run gitDir (GitService.undoDiscard lineBackup)
            test <@ read "a.txt" = edited @>

            // A whole file, and an untracked file that gets deleted.
            let fileBackup = run gitDir (GitService.discardFilesWithBackup [ "a.txt" ] [ "new.txt" ])
            test <@ read "a.txt" = numbered [ "one"; "two"; "three" ] && not (File.Exists(Path.Combine(root, "new.txt"))) @>
            run gitDir (GitService.undoDiscard fileBackup)
            test <@ read "a.txt" = edited && read "new.txt" = numbered [ "scratch" ] @>
            test <@ Trash.isIntact fileBackup @>)

    [<Fact>]
    let ``an undo refuses to overwrite edits made since the discard`` () =
        withTempRepository (fun root repo ->
            let gitDir = Path.Combine(root, ".git")
            commitFile repo root "a.txt" "one\n" "init" |> ignore
            writeFile root "a.txt" "edited\n"
            let backup = run gitDir (GitService.discardFilesWithBackup [ "a.txt" ] [])
            test <@ File.ReadAllText(Path.Combine(root, "a.txt")) = "one\n" @>

            // Working on the file again after the discard: the undo must not silently replace this.
            writeFile root "a.txt" "written after the discard\n"
            match runFlow gitDir (GitService.undoDiscard backup) with
            | Ok () -> failwith "the undo should have refused"
            | Error error ->
                let message = GitError.describe error
                test <@ message.Contains "a.txt changed since the discard" && message.Contains backup.Directory @>
            test <@ File.ReadAllText(Path.Combine(root, "a.txt")) = "written after the discard\n" @>)

    [<Fact>]
    let ``undoing a deleted untracked file puts it back, unless it was written again`` () =
        withTempRepository (fun root repo ->
            let gitDir = Path.Combine(root, ".git")
            commitFile repo root "a.txt" "one\n" "init" |> ignore
            writeFile root "notes.txt" "scratch\n"
            let backup = run gitDir (GitService.discardFilesWithBackup [] [ "notes.txt" ])
            test <@ not (File.Exists(Path.Combine(root, "notes.txt"))) @>
            run gitDir (GitService.undoDiscard backup)
            test <@ File.ReadAllText(Path.Combine(root, "notes.txt")) = "scratch\n" @>

            // Delete it again, then write a new file at the same path: the undo leaves that alone.
            let second = run gitDir (GitService.discardFilesWithBackup [] [ "notes.txt" ])
            writeFile root "notes.txt" "a different note\n"
            match runFlow gitDir (GitService.undoDiscard second) with
            | Ok () -> failwith "the undo should have refused"
            | Error error -> test <@ (GitError.describe error).Contains "notes.txt changed since the discard" @>
            test <@ File.ReadAllText(Path.Combine(root, "notes.txt")) = "a different note\n" @>)

    [<Fact>]
    let ``commit uses the message, amends, and reports hook failures`` () =
        withTempRepository (fun root repo ->
            let gitDir = Path.Combine(root, ".git")
            commitFile repo root "a.txt" "one\n" "init" |> ignore
            writeFile root "a.txt" "two\n"
            run gitDir (GitService.stageFiles [ "a.txt" ])
            run gitDir (GitService.commit { Amend = false; SignOff = true } "Change a\n\nBody line\n")
            let message = run gitDir GitService.fetchLastCommitMessage
            test <@ message.StartsWith "Change a\n\nBody line\n" && message.Contains "Signed-off-by: GitKay Tests <gitkay@example.com>" @>
            run gitDir (GitService.commit { Amend = true; SignOff = false } "Change a, amended\n")
            test <@ (run gitDir GitService.fetchLastCommitMessage).StartsWith "Change a, amended" @>
            let hook = Path.Combine(gitDir, "hooks", "pre-commit")
            Directory.CreateDirectory(Path.GetDirectoryName hook) |> ignore
            File.WriteAllText(hook, "#!/bin/sh\necho 'lint failed: tabs' >&2\nexit 1\n")
            File.SetUnixFileMode(hook, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
            writeFile root "a.txt" "three\n"
            run gitDir (GitService.stageFiles [ "a.txt" ])
            match runFlow gitDir (GitService.commit { Amend = false; SignOff = false } "Blocked\n") with
            | Ok () -> failwith "the hook should have stopped the commit"
            | Error error -> test <@ (GitError.describe error).Contains "lint failed: tabs" @>)

    [<Fact>]
    let ``untracked markdown file can load a rendered working-tree preview`` () =
        withTempRepository (fun root repo ->
            commitFile repo root "seed.txt" "seed\n" "seed" |> ignore
            writeFile root "README.md" "# Untracked\n\nPreview me.\n"
            let rendered = GitService.loadWorkingTreeRenderedMarkdown root WorkingTree.Untracked "/dev/null" "README.md" false
            match rendered with
            | Error error -> failwith (GitError.describe error)
            | Ok content ->
                test <@ content.OldRows.IsEmpty @>
                test <@ content.NewRows |> List.exists (fun row -> row.CurrentText = "Untracked") @>)

    [<Fact>]
    let ``root commit markdown file can load a rendered preview`` () =
        withTempRepository (fun root repo ->
            let added = commitFile repo root "README.md" "# Root\n\nPreview me.\n" "initial docs"
            let rendered = GitService.loadCommitRenderedMarkdown root added.Sha "/dev/null" "README.md" false
            match rendered with
            | Error error -> failwith (GitError.describe error)
            | Ok content -> test <@ content.NewRows |> List.exists (fun row -> row.CurrentText = "Root") @>)

    [<Fact>]
    let ``added markdown file can load a rendered commit preview`` () =
        withTempRepository (fun root repo ->
            commitFile repo root "seed.txt" "seed\n" "seed" |> ignore
            let added = commitFile repo root "README.md" "# Added\n\nPreview me.\n" "add docs"
            let payload = GitService.loadCommitWholeFilePayload root added.Sha "/dev/null" "README.md" false
            match payload with
            | Error error -> failwith (GitError.describe error)
            | Ok payload ->
                test <@ payload.Rendered.IsSome @>
                let content = payload.Rendered.Value
                test <@ content.OldRows.IsEmpty @>
                test <@ content.NewRows |> List.exists (fun row -> row.CurrentText = "Added") @>
                test <@ content.DiffRows |> List.forall (fun row -> row.Change = MarkdownChangeKind.Added) @>)

    [<Fact>]
    let ``arbitrary revision markdown uses the selected base and target blobs`` () =
        withTempRepository (fun root repo ->
            let first = commitFile repo root "README.md" "# Base\n\nold text\n" "base"
            writeFile root "image.png" "old-image"
            Commands.Stage(repo, "image.png")
            let signature = Signature("GitKay Tests", "gitkay@example.com", DateTimeOffset(2024, 1, 2, 0, 0, 0, TimeSpan.Zero))
            repo.Commit("base image", signature, signature) |> ignore
            writeFile root "README.md" "# Target\n\nnew text\n"
            File.WriteAllText(Path.Combine(root, "image.png"), "new-image")
            Commands.Stage(repo, "README.md")
            Commands.Stage(repo, "image.png")
            let target = repo.Commit("target", signature, signature)
            let comparison : GitService.RevisionComparison =
                { BaseRevision = first.Sha; TargetRevision = target.Sha; BaseHash = first.Sha; TargetHash = target.Sha }
            let rendered = GitService.loadRevisionRenderedMarkdown root comparison "README.md" "README.md" false |> Result.defaultWith (GitError.describe >> failwith)
            test <@ rendered.OldRows |> List.exists (fun row -> row.PreviousText = "Base") @>
            test <@ rendered.NewRows |> List.exists (fun row -> row.CurrentText = "Target") @>)

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
            NewLineCount = None
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

    [<Fact>]
    let ``calculateLanes should keep an occupied parent lane anchored through a fork`` () =
        let commits : Models.Commit list =
            [
                {
                    Hash = "seed"
                    AuthorName = "Author"
                    AuthorEmail = "author@example.com"
                    Timestamp = 1710000000L
                    Parents = [ "a"; "b" ]
                    Subject = "Seed"
                    Message = "Seed"
                    Refs = []
                }
                {
                    Hash = "a"
                    AuthorName = "Author"
                    AuthorEmail = "author@example.com"
                    Timestamp = 1709999940L
                    Parents = [ "b" ]
                    Subject = "A"
                    Message = "A"
                    Refs = []
                }
                {
                    Hash = "b"
                    AuthorName = "Author"
                    AuthorEmail = "author@example.com"
                    Timestamp = 1709999880L
                    Parents = []
                    Subject = "B"
                    Message = "B"
                    Refs = []
                }
            ]

        let graph = Graph.calculateLanes commits

        test <@ graph.Length = 3 @>

        let seedRow = graph.[0]
        test <@ seedRow.Lane = 0 @>
        test <@ seedRow.Segments |> List.map (fun segment -> segment.TargetLane) |> List.sort = [ 0; 1 ] @>

        let forkRow = graph.[1]
        test <@ forkRow.Lane = 0 @>
        test <@ forkRow.Segments |> List.exists (fun segment -> segment.IsCommit && segment.TargetLane = 1) @>
        test <@ forkRow.Segments |> List.exists (fun segment -> not segment.IsCommit && segment.Lane = 1 && segment.TargetLane = 1) @>

    let private sampleSearchResult (commit: Models.Commit) matchKinds matchSummary : GitSearch.Result =
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
        : GitSearch.Result =
        {
            Commit = commit
            MatchKinds = matchKinds
            MatchSummary = matchSummary
            MatchedPaths = matchedPaths
            MatchedRefs = matchedRefs
        }

    let private emptyModel : App.Model =
        {
            StartupSelection = App.NoStartupSelection
            StartupShowOnlyMatches = false
            GitEnv = GitService.environment ""
            Status = ""
            StartupTargets = []
            ShowBranchRefs = false
            ShowStashes = false
            DiffContextLines = 3
            IgnoreWhitespace = false
            DiffLayout = DiffLayout.Unified
            SearchQuery = ""
            SearchScopeKey = "all"
            SearchUseRegex = false
            SearchResults = None
            Commits = []
            HasFullHistory = false
            Selection = App.NoSelection
            RevisionComparison = None
            SelectedDiffHash = None
            SelectedDiffFiles = None
            SelectedDiff = None
            SelectedDiffFileKey = None
            DiffExpansions = Map.empty
            RenderedMarkdown = None
            FormattedFile = None
            WholeFile = None
            SelectionStartedAtTicks = None
            SelectedDiffStartedAtTicks = None
            SearchStartedAtTicks = None
            SearchProgress = None
            WorkingTree = []
            WorkingTreeChanges = None
            WorkingTreeStartedAtTicks = None
            LastDiscard = None
        }

    [<Fact>]
    let ``init should store parsed startup options`` () =
        let model, _ =
            App.init
                [|
                    "--branch"
                    "topic"
                    "--tag"
                    "v1.0"
                    "--show-branch-refs"
                    "--show-stashes"
                    "--diff-context=10"
                    "--diff-presentation=side-by-side"
                    "--search=needle"
                    "--search-scope=message"
                |]

        test <@ model.Status = "Loading history..." @>
        test <@ model.StartupTargets = [ GitStartup.StartupTarget.Branch "topic"; GitStartup.StartupTarget.Tag "v1.0" ] @>
        test <@ model.ShowBranchRefs @>
        test <@ model.ShowStashes @>
        test <@ model.DiffContextLines = 10 @>
        test <@ model.DiffLayout = DiffLayout.SideBySide @>
        test <@ model.SearchQuery = "needle" @>
        test <@ model.SearchScopeKey = "commit" @>
        test <@ model.SearchStartedAtTicks = None @>

    [<Fact>]
    let ``init should store an initial selected commit hash`` () =
        let model, _ = App.init [| "--select=deadbeef" |]

        test <@ model.SelectedCommitHash = Some "deadbeef" @>

    [<Fact>]
    let ``HistoryLoaded should auto-select the first commit when nothing is selected`` () =
        let commits =
            [
                sampleCommit "first" "First"
                sampleCommit "second" "Second"
            ]

        let next, _ = App.update (App.Msg.HistoryLoaded (true, Ok commits)) emptyModel

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
    let ``HistoryLoaded should start the initial search when a startup query is present`` () =
        let commits =
            [
                sampleCommit "first" "First"
                sampleCommit "second" "Second"
            ]

        let next, _ =
            App.update
                (App.Msg.HistoryLoaded (true, Ok commits))
                {
                    emptyModel with
                        SearchQuery = "needle"
                        SearchScopeKey = "message"
                }
        test <@ next.Status = "Searching needle..." @>
        test <@ next.SearchResults = None @>
        test <@ next.SearchStartedAtTicks.IsSome @>
        test <@ next.SelectedCommitHash = Some "first" @>

    [<Fact>]
    let ``SetShowStashes should update the view state`` () =
        let next, _ = App.update (App.Msg.SetShowStashes true) emptyModel

        test <@ next.ShowStashes @>
        test <@ next.Status = "Refreshing..." @>

    [<Fact>]
    let ``SetDiffContextLines should update the view state and reload the current diff`` () =
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
                    Selection = App.CommitSelected "commit"
                    SelectedDiffHash = Some "commit"
                    SelectedDiffFiles = Some [ sampleSummary "foo.txt" "foo.txt" "foo.txt" ]
                    SelectedDiff = Some [ selectedFile ]
                    SelectedDiffFileKey = Some { OldPath = "foo.txt"; NewPath = "foo.txt" }
                    SelectedDiffStartedAtTicks = Some 2L
            }

        let next, _ = App.update (App.Msg.SetDiffContextLines 10) initial

        test <@ next.DiffContextLines = 10 @>
        test <@ next.SelectedDiffHash = Some "commit" @>
        test <@ next.SelectedDiffFiles = initial.SelectedDiffFiles @>
        test <@ next.SelectedDiff = None @>
        test <@ next.SelectedDiffFileKey = initial.SelectedDiffFileKey @>
        test <@ next.SelectedDiffStartedAtTicks.IsSome @>

    [<Fact>]
    let ``SetDiffLayout should update the view state`` () =
        let next, _ = App.update (App.Msg.SetDiffLayout DiffLayout.SideBySide) emptyModel

        test <@ next.DiffLayout = DiffLayout.SideBySide @>

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
                    Selection = App.CommitSelected "second"
                    SelectedDiffHash = Some "second"
                    SelectedDiffFiles = Some [ sampleSummary "foo.txt" "foo.txt" "foo.txt" ]
                    SelectedDiff = Some [ selectedFile ]
                    SelectedDiffFileKey = Some { OldPath = "foo.txt"; NewPath = "foo.txt" }
            }

        let next, _ = App.update (App.Msg.HistoryLoaded (true, Ok commits)) initial

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
                    Selection = App.CommitSelected "missing"
                    SelectedDiffHash = Some "missing"
                    SelectedDiffFiles = Some [ sampleSummary "foo.txt" "foo.txt" "foo.txt" ]
                    SelectedDiffFileKey = Some { OldPath = "foo.txt"; NewPath = "foo.txt" }
                    SelectedDiff = Some [ selectedFile ]
                    SelectionStartedAtTicks = Some 1L
                    SelectedDiffStartedAtTicks = Some 2L
            }

        let next, _ = App.update (App.Msg.HistoryLoaded (true, Ok commits)) initial

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
                    Selection = App.CommitSelected "old"
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

    let private workingEntry path : WorkingTree.Entry =
        { Path = path; OriginalPath = path; Staged = WorkingTree.Unchanged; Unstaged = WorkingTree.Modified; Untracked = false }

    [<Fact>]
    let ``SelectWorkingTree clears the commit selection and has no commit hash`` () =
        let initial = { emptyModel with Selection = App.CommitSelected "old"; SelectedDiffHash = Some "old"; SelectedDiff = Some [] }
        let next, _ = App.update (App.Msg.SelectWorkingTree 5L) initial
        test <@ next.Selection = App.WorkingTreeSelected && next.SelectedCommitHash = None @>
        test <@ next.SelectedDiffHash = None && next.SelectedDiff = None && next.WorkingTreeStartedAtTicks = Some 5L @>

    [<Fact>]
    let ``WorkingTreeChangesLoaded applies only the current load`` () =
        let selected, _ = App.update (App.Msg.SelectWorkingTree 5L) emptyModel
        let changes : GitService.WorkingTreeChanges = { Entries = [ workingEntry "a.txt" ]; Staged = []; Unstaged = []; Untracked = [] }
        let stale, _ = App.update (App.Msg.WorkingTreeChangesLoaded(4L, Ok changes)) selected
        test <@ stale.WorkingTreeChanges = None @>
        let current, _ = App.update (App.Msg.WorkingTreeChangesLoaded(5L, Ok changes)) selected
        test <@ current.WorkingTreeChanges = Some changes && current.WorkingTree = changes.Entries && current.WorkingTreeStartedAtTicks = None @>

    [<Fact>]
    let ``WorkingTreeStatusLoaded reloads changes only when selected and changed`` () =
        let entries = [ workingEntry "a.txt" ]
        let unselected, _ = App.update (App.Msg.WorkingTreeStatusLoaded(Ok entries)) emptyModel
        test <@ unselected.WorkingTree = entries && unselected.WorkingTreeStartedAtTicks = None @>
        let selected = { unselected with Selection = App.WorkingTreeSelected }
        let same, _ = App.update (App.Msg.WorkingTreeStatusLoaded(Ok entries)) selected
        test <@ same.WorkingTreeStartedAtTicks = None @>
        let changed, _ = App.update (App.Msg.WorkingTreeStatusLoaded(Ok [ workingEntry "b.txt" ])) selected
        test <@ changed.WorkingTreeStartedAtTicks.IsSome @>

    [<Fact>]
    let ``staging from the history reports what happened, and a discard can be undone`` () =
        let backup : Trash.Backup =
            { Directory = "/tmp/backup"; Entries = []; Description = "1 file"; CreatedAt = DateTimeOffset.UnixEpoch }
        let staged, _ = App.update (App.Msg.WorkingTreeOperationDone("Staged 2 files", Ok None)) emptyModel
        test <@ staged.Status = "Staged 2 files" && staged.LastDiscard = None @>

        let discarded, _ = App.update (App.Msg.WorkingTreeOperationDone("Discarded 1 file", Ok(Some backup))) emptyModel
        test <@ discarded.Status = "Discarded 1 file · Ctrl+Z to undo" && discarded.LastDiscard = Some backup @>

        let undone, _ = App.update App.Msg.UndoWorkingTreeDiscard discarded
        test <@ undone.LastDiscard = None @>
        let nothing, _ = App.update App.Msg.UndoWorkingTreeDiscard undone
        test <@ nothing.Status = "Nothing to undo" @>

        let failed, _ = App.update (App.Msg.WorkingTreeOperationDone("Staging", Error(GitError.OperationFailed("add", "locked")))) emptyModel
        test <@ failed.Status.StartsWith "Staging failed" @>

    [<Fact>]
    let ``a clean working tree moves the selection off the uncommitted changes row`` () =
        let a = sampleCommit "a" "subject"
        let selected = { emptyModel with Commits = Graph.calculateLanes [ a ]; Selection = App.WorkingTreeSelected; WorkingTree = [ workingEntry "a.txt" ] }
        let next, _ = App.update (App.Msg.WorkingTreeStatusLoaded(Ok [])) selected
        test <@ next.Selection = App.NoSelection && next.WorkingTree.IsEmpty && next.WorkingTreeChanges.IsNone @>

    [<Fact>]
    let ``history reload keeps the working tree row selected`` () =
        let a = sampleCommit "a" "subject"
        let selected = { emptyModel with Selection = App.WorkingTreeSelected }
        let next, _ = App.update (App.Msg.HistoryLoaded(false, Ok [ a ])) selected
        test <@ next.Selection = App.WorkingTreeSelected @>

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
                    Selection = App.CommitSelected "commit"
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
                    Selection = App.CommitSelected "commit"
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
                    Selection = App.CommitSelected "commit"
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
                    SearchResults = Some [ sampleSearchResult (sampleCommit "oldhash" "Old") [ GitSearch.MessageMatch ] "message" ]
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
                (App.Msg.SearchResultsLoaded("needle", "all", 42L, Ok [ sampleSearchResult commit [ GitSearch.MessageMatch; GitSearch.PathMatch ] "message; paths: src/needle.txt" ]))
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
                    Selection = App.CommitSelected commit.Hash
                    SelectedDiffHash = Some commit.Hash
                    SelectedDiffFiles = Some [ fooSummary; barSummary ]
                    SelectedDiff = Some [ fooFile; barFile ]
                    SelectedDiffFileKey = Some { OldPath = "foo.txt"; NewPath = "foo.txt" }
            }

        projection.Update model

        test <@ projection.SelectedDiffFiles.Count = 2 @>
        test <@ projection.SelectedDiffFile.DisplayPath = "foo.txt" @>
        test <@ projection.SelectedDiffRows.Count = 7 @>

        match projection.SelectedDiffRow with
        | :? DiffFileHeaderProjection as header -> test <@ header.DisplayPath = "foo.txt" @>
        | other -> failwithf "Expected a file header, got %A" other

        projection.SelectedDiffFile <- projection.SelectedDiffFiles.[1]

        match projection.SelectedDiffRow with
        | :? DiffFileHeaderProjection as header -> test <@ header.DisplayPath = "bar.txt (new file)" @>
        | other -> failwithf "Expected a file header after selection sync, got %A" other

        test <@ projection.SelectedDiffRows.Count = 7 @>

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
                    Selection = App.CommitSelected commit.Hash
                    SelectedDiffHash = Some commit.Hash
                    SelectedDiffFiles = Some [ fooSummary; barSummary ]
                    SelectedDiff = Some [ fooFile; barFile ]
                    SelectedDiffFileKey = Some { OldPath = "foo.txt"; NewPath = "foo.txt" }
            }

        projection.Update model

        projection.SelectedDiffFile <- projection.SelectedDiffFiles.[1]

        test <@ projection.SelectedDiffFile.DisplayPath = "bar.txt (new file)" @>
        test <@ projection.SelectedDiffRows.Count = 7 @>

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
    let ``MainProjection should keep the full commit list visible when search matches`` () =
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
                    SearchResults =
                        Some
                            [
                                sampleSearchResultWithContext
                                    commit
                                    [ GitSearch.MessageMatch; GitSearch.PathMatch ]
                                    "message; paths: src/needle.txt"
                                    [ "src/needle.txt" ]
                                    []
                            ]
            }

        projection.Update model

        test <@ projection.HasSearchResults @>
        test <@ projection.SearchResults.Count = 1 @>
        test <@ projection.Commits.Count = 2 @>

        let matchingVm = projection.Commits |> Seq.find (fun item -> item.FullHash = commit.Hash)
        let otherVm = projection.Commits |> Seq.find (fun item -> item.FullHash = otherCommit.Hash)

        test <@ matchingVm.HasSearchMatch @>
        test <@ matchingVm.HasSubjectMatch @>
        test <@ matchingVm.HasDiffMatch @>
        test <@ matchingVm.PathMatchCount = 1 @>
        test <@ not otherVm.HasSearchMatch @>

    [<Fact>]
    let ``MainProjection should sync and dispatch the stash visibility toggle`` () =
        let projection = MainProjection()
        let messages = ConcurrentQueue<App.Msg>()
        projection.SetDispatch (fun msg -> messages.Enqueue msg |> ignore)

        projection.Update
            {
                emptyModel with
                    ShowStashes = true
            }

        test <@ projection.ShowStashes @>
        test <@ messages.IsEmpty @>

        projection.ShowStashes <- false

        let dispatched = messages.ToArray()

        test <@ dispatched = [| App.Msg.SetShowStashes false |] @>

    [<Fact>]
    let ``MainProjection should keep the search panel hidden by default and preserve an explicit open state`` () =
        let projection = MainProjection()
        projection.SetDispatch ignore

        test <@ not projection.IsSearchPanelExpanded @>

        projection.IsSearchPanelExpanded <- true

        test <@ projection.IsSearchPanelExpanded @>

        projection.Update emptyModel

        test <@ projection.IsSearchPanelExpanded @>

    [<Fact>]
    let ``MainProjection should default to unified diff presentation and allow local mode changes`` () =
        let projection = MainProjection()
        let messages = ConcurrentQueue<App.Msg>()
        projection.SetDispatch (fun msg -> messages.Enqueue msg |> ignore)

        test <@ projection.IsUnifiedDiffMode @>
        test <@ projection.SelectedDiffPresentationModeLabel = "Diff" @>

        projection.SelectedDiffPresentationMode <- projection.DiffPresentationModes |> Seq.find (fun mode -> mode.Key = "side-by-side")

        test <@ projection.IsSideBySideDiffMode @>
        test <@ projection.SelectedDiffPresentationModeLabel = "Side-by-side" @>
        test <@ messages.ToArray() = [| App.Msg.SetDiffLayout DiffLayout.SideBySide |] @>

    [<Fact>]
    let ``MainProjection should sync diff presentation mode from model startup state`` () =
        let projection = MainProjection()
        projection.SetDispatch ignore

        projection.Update { emptyModel with DiffLayout = DiffLayout.SideBySide }

        test <@ projection.IsSideBySideDiffMode @>
        test <@ projection.SelectedDiffPresentationModeLabel = "Side-by-side" @>
        test <@ projection.SelectedDiffPresentationMode.Key = "side-by-side" @>

    [<Fact>]
    let ``MainProjection should sync and dispatch diff context line count changes`` () =
        let projection = MainProjection()
        let messages = ConcurrentQueue<App.Msg>()
        projection.SetDispatch (fun msg -> messages.Enqueue msg |> ignore)

        projection.Update { emptyModel with DiffContextLines = 5 }

        test <@ projection.DiffContextLineCount = 5 @>
        test <@ projection.SelectedDiffContextLineCount <> null @>
        test <@ projection.SelectedDiffContextLineCount.Count = 5 @>
        test <@ messages.IsEmpty @>

        projection.SelectedDiffContextLineCount <- projection.DiffContextLineCounts |> Seq.find (fun option -> option.Count = 10)

        let dispatched = messages.ToArray()

        test <@ dispatched = [| App.Msg.SetDiffContextLines 10 |] @>

    [<Fact>]
    let ``MainProjection should apply and capture persisted settings without dispatching`` () =
        let projection = MainProjection()
        let messages = ConcurrentQueue<App.Msg>()
        projection.SetDispatch (fun msg -> messages.Enqueue msg |> ignore)

        let settings =
            { Settings.defaults with
                ShowBranchRefs = true
                ShowStashes = true
                DiffContextLines = 10
                DiffLayout = DiffLayout.SideBySide
                CommitRowFontFamily = "Avenir Next"
                CommitRowMonoFontFamily = "Iosevka"
                CommitRowTextFontSize = 11.5
                CommitRowMetaFontSize = 9.5
                CommitRowBadgeFontSize = 8.5
                SearchDebounceSeconds = 1.25 }

        projection.ApplySettings settings

        test <@ projection.ShowBranchRefs @>
        test <@ projection.ShowStashes @>
        test <@ projection.DiffContextLineCount = 10 @>
        test <@ projection.SelectedDiffPresentationMode.Key = "side-by-side" @>
        test <@ projection.CommitRowFontFamily = "Avenir Next" @>
        test <@ projection.CommitRowMonoFontFamily = "Iosevka" @>
        test <@ projection.CommitRowTextFontSize = 11.5 @>
        test <@ projection.CommitRowMetaFontSize = 9.5 @>
        test <@ projection.CommitRowBadgeFontSize = 8.5 @>
        test <@ projection.SearchDebounceSeconds = 1.25 @>
        test <@ messages.IsEmpty @>

        let captured = projection.CaptureSettings()

        test <@ captured = Settings.normalize settings @>

    [<Fact>]
    let ``AppSettingsStore should round-trip settings and produce startup args`` () =
        let root = Path.Combine(Path.GetTempPath(), "gitkay-settings-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore

        try
            let path = Path.Combine(root, "settings.json")
            let store = AppSettingsStore(path)
            let settings =
                { Settings.defaults with
                    ShowBranchRefs = true
                    ShowStashes = true
                    DiffContextLines = 10
                    DiffLayout = DiffLayout.SideBySide
                    CommitRowFontFamily = "Avenir Next"
                    CommitRowMonoFontFamily = "Iosevka"
                    CommitRowTextFontSize = 11.5
                    CommitRowMetaFontSize = 9.5
                    CommitRowBadgeFontSize = 8.5
                    SearchDebounceSeconds = 1.25 }

            store.Save(settings)

            let loaded = store.Load()

            test <@ loaded = Settings.normalize settings @>
            test <@ File.Exists(path) @>
            test <@ GitStartup.settingsArguments loaded = [ "--show-branch-refs"; "--show-stashes"; "--diff-context=10"; "--diff-presentation=side-by-side" ] @>
            test <@ GitStartup.settingsArguments Settings.defaults = [] @>
            let parsed = GitStartup.parseStartupOptions (GitStartup.settingsArguments loaded |> Array.ofList)
            test <@ parsed |> Result.map (fun o -> o.ShowBranchRefs, o.ShowStashes, o.DiffContextLines, o.DiffLayout) = Ok(true, true, 10, DiffLayout.SideBySide) @>
        finally
            try
                Directory.Delete(root, true)
            with _ ->
                ()

    [<Fact>]
    let ``AppUiStateStore should round-trip window size and repo selection`` () =
        let root = Path.Combine(Path.GetTempPath(), "gitkay-ui-state-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore

        try
            let path = Path.Combine(root, "ui-state.json")
            let store = AppUiStateStore(path)
            let repoKey = Path.Combine(root, ".git")
            let state =
                UiState.empty
                |> UiState.withWindowSize (Some 1280.0) (Some 720.0)
                |> UiState.withSelectedCommit repoKey "abc123"

            store.Save(state)

            let loaded = store.Load()

            test <@ loaded.WindowWidth = Some 1280.0 && loaded.WindowHeight = Some 720.0 @>
            test <@ UiState.selectedCommit repoKey loaded = Some "abc123" @>
            // Another spelling of the same repository path finds the same selection.
            test <@ UiState.selectedCommit (repoKey + string Path.DirectorySeparatorChar) loaded = Some "abc123" @>
            test <@ File.Exists(path) @>
            test <@ loaded.Layout.HistoryPaneRatio = None @>

            let layout = { UiLayout.empty with HistoryPaneRatio = Some 0.3; FileListWidth = Some 260.0; HashColumnWidth = Some 70.0; DateColumnWidth = Some 2.0e6 }
            store.Save(UiState.withLayout layout state)
            let reloaded = store.Load()
            test <@ reloaded.Layout = { UiLayout.empty with HistoryPaneRatio = Some 0.3; FileListWidth = Some 260.0; HashColumnWidth = Some 70.0; DateColumnWidth = Some 10000.0 } @>
            test <@ UiState.selectedCommit repoKey reloaded = Some "abc123" @>

            // A file that isn't UI state is ignored rather than failing.
            File.WriteAllText(path, "not json")
            test <@ store.Load() = UiState.empty @>
        finally
            try
                Directory.Delete(root, true)
            with _ ->
                ()

    [<Fact>]
    let ``MainProjection should debounce live search updates as the query changes`` () =
        let projection = MainProjection()
        let messages = ConcurrentQueue<App.Msg>()
        projection.SetDispatch (fun msg -> messages.Enqueue msg |> ignore)
        // Generous margins: a loaded machine that stalls between the two edits would otherwise run the first search too.
        projection.SearchDebounceSeconds <- 1.0

        projection.SearchQuery <- "nee"
        Task.Delay(50).Wait()
        projection.SearchQuery <- "needle"

        Task.Delay(2500).Wait()

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
        test <@ runSearches = [| ("needle", "commit") |] @>

    [<Fact>]
    let ``SearchResultProjection should surface matched fields, files, and counts`` () =
        let projection = SearchResultProjection()
        let commit =
            sampleCommit "feedfacefeedfacefeedfacefeedfacefeedface" "Search hit"

        projection.Update
            (sampleSearchResultWithContext
                commit
                [ GitSearch.MessageMatch; GitSearch.PathMatch ]
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
                    SearchResults = Some [ sampleSearchResult matchingCommit [ GitSearch.MessageMatch; GitSearch.PathMatch ] "message; paths: src/needle.txt" ]
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
    let ``MainProjection marks diff files the applied search's path term matches`` () =
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
                    Selection = App.CommitSelected commit.Hash
                    SelectedDiffHash = Some commit.Hash
                    SelectedDiffFiles = Some [ summary ]
                    SelectedDiff = Some [ file ]
                    SelectedDiffFileKey = Some { OldPath = "src/needle.txt"; NewPath = "src/needle.txt" }
                    SearchQuery = "needle"
                    SearchScopeKey = "diff"
            }

        projection.Update { model with SearchQuery = "path:needle"; SearchScopeKey = "commit" }
        test <@ projection.SelectedDiffFiles.Count = 1 && projection.SelectedDiffFiles.[0].IsPathSearchMatch @>

        projection.Update { model with SearchQuery = "path:elsewhere"; SearchScopeKey = "commit" }
        test <@ not projection.SelectedDiffFiles.[0].IsPathSearchMatch @>

        // A diff term marks lines (drawn by the diff view), not file paths.
        projection.Update model
        test <@ not projection.SelectedDiffFiles.[0].IsPathSearchMatch @>

    [<Fact>]
    let ``MainProjection should omit opposite-side rows in old and new diff modes`` () =
        let projection = MainProjection()
        let commit = sampleCommit "abcdefabcdefabcdefabcdefabcdefabcdefabcd" "Change lines"
        let file =
            sampleFile "sample.txt" "sample.txt"
                [
                    { Type = Models.Context; Content = "same"; OldLineNo = Some 1; NewLineNo = Some 1 }
                    { Type = Models.Removed; Content = "old"; OldLineNo = Some 2; NewLineNo = None }
                    { Type = Models.Added; Content = "new"; OldLineNo = None; NewLineNo = Some 2 }
                ]
        let summary = sampleSummary "sample.txt" "sample.txt" "sample.txt"
        let model =
            { emptyModel with
                Status = "Loaded"
                Commits = Graph.calculateLanes [ commit ]
                Selection = App.CommitSelected commit.Hash
                SelectedDiffHash = Some commit.Hash
                SelectedDiffFiles = Some [ summary ]
                SelectedDiff = Some [ file ]
                SelectedDiffFileKey = Some { OldPath = "sample.txt"; NewPath = "sample.txt" } }

        projection.Update model
        projection.SelectedDiffPresentationMode <- projection.DiffPresentationModes |> Seq.find (fun mode -> mode.Key = "new")
        let newLines = projection.SelectedDiffRows |> Seq.choose (function :? DiffLineProjection as line -> Some line | _ -> None) |> Seq.toArray
        test <@ newLines.Length = 2 @>
        test <@ newLines |> Array.forall (fun line -> not line.IsRemoved) @>

        projection.SelectedDiffPresentationMode <- projection.DiffPresentationModes |> Seq.find (fun mode -> mode.Key = "old")
        let oldLines = projection.SelectedDiffRows |> Seq.choose (function :? DiffLineProjection as line -> Some line | _ -> None) |> Seq.toArray
        test <@ oldLines.Length = 2 @>
        test <@ oldLines |> Array.forall (fun line -> not line.IsAdded) @>

    [<Fact>]
    let ``MainProjection should project a visible action row between separated hunks`` () =
        let projection = MainProjection()
        let commit = sampleCommit "1234512345123451234512345123451234512345" "Separated hunks"
        let line number content : Models.DiffLine = { Type = Models.Context; Content = content; OldLineNo = Some number; NewLineNo = Some number }
        let file : Models.FileDiff =
            { OldPath = "sample.txt"; NewPath = "sample.txt"
              NewLineCount = None
              Hunks =
                [ { Header = "@@ -1 +1 @@"; Lines = [ line 1 "first" ] }
                  { Header = "@@ -12 +12 @@"; Lines = [ line 12 "second" ] } ] }
        let summary = sampleSummary "sample.txt" "sample.txt" "sample.txt"
        projection.Update
            { emptyModel with
                Status = "Loaded"
                Commits = Graph.calculateLanes [ commit ]
                Selection = App.CommitSelected commit.Hash
                SelectedDiffHash = Some commit.Hash
                SelectedDiffFiles = Some [ summary ]
                SelectedDiff = Some [ file ]
                SelectedDiffFileKey = Some { OldPath = "sample.txt"; NewPath = "sample.txt" } }

        let gaps = projection.SelectedDiffRows |> Seq.choose (function :? DiffGapProjection as gap -> Some gap | _ -> None) |> Seq.toArray
        test <@ gaps |> Array.map (fun gap -> gap.Gap.Kind) = [| DiffExpansion.Internal; DiffExpansion.Trailing |] @>
        test <@ gaps.[0].HiddenLineCount = Nullable 10 @>
        test <@ gaps.[1].Gap.HiddenCount = None @>
        test <@ gaps.[0].HeaderText = "@@ -12 +12 @@" && isNull gaps.[1].HeaderText @>
        let headers = projection.SelectedDiffRows |> Seq.filter (fun row -> row :? DiffHunkHeaderProjection) |> Seq.length
        test <@ headers = 1 @>

    [<Fact>]
    let ``MainProjection should dispatch file-scoped gap expansion without changing context preference`` () =
        let projection = MainProjection()
        let messages = ResizeArray<App.Msg>()
        let commit = sampleCommit "1234512345123451234512345123451234512345" "Separated hunks"
        let line number : Models.DiffLine = { Type = Models.Context; Content = string number; OldLineNo = Some number; NewLineNo = Some number }
        let file : Models.FileDiff =
            { OldPath = "sample.txt"; NewPath = "sample.txt"; NewLineCount = None
              Hunks = [ { Header = "@@ -20 +20 @@"; Lines = [ line 20 ] } ] }
        projection.Update
            { emptyModel with
                Selection = App.CommitSelected commit.Hash
                SelectedDiffHash = Some commit.Hash
                SelectedDiffFiles = Some [ sampleSummary "sample.txt" "sample.txt" "sample.txt" ]
                SelectedDiff = Some [ file ] }
        projection.SetDispatch (fun message -> messages.Add message)

        let gap = projection.SelectedDiffRows |> Seq.pick (function :? DiffGapProjection as gap -> Some gap | _ -> None)
        test <@ gap.Gap.Kind = DiffExpansion.Leading @>
        projection.ExpandDiffGapCommand.Execute(DiffGapExpansionRequest(gap.Gap, DiffExpansion.Up))

        match List.ofSeq messages with
        | [ App.Msg.ExpandDiffGap (hash, requestedGap, direction, _) ] ->
            test <@ hash = commit.Hash && requestedGap = gap.Gap && direction = DiffExpansion.Up @>
        | other -> failwithf "unexpected messages %A" other

    [<Fact>]
    let ``MainProjection should reveal expanded context for only the expanded file`` () =
        let projection = MainProjection()
        let commit = sampleCommit "1234512345123451234512345123451234512345" "Expansion"
        let ctx number : Models.DiffLine = { Type = Models.Context; Content = sprintf "l%d" number; OldLineNo = Some number; NewLineNo = Some number }
        let fileA : Models.FileDiff = { OldPath = "a.txt"; NewPath = "a.txt"; NewLineCount = None; Hunks = [ { Header = "@@ -30 +30 @@"; Lines = [ ctx 30 ] } ] }
        let fileB : Models.FileDiff = { OldPath = "b.txt"; NewPath = "b.txt"; NewLineCount = None; Hunks = [ { Header = "@@ -30 +30 @@"; Lines = [ ctx 30 ] } ] }
        let fullA : Models.FileDiff = { fileA with Hunks = [ { Header = "@@ -1,40 +1,40 @@"; Lines = [ 1 .. 40 ] |> List.map ctx } ] }
        let baseModel =
            { emptyModel with
                Selection = App.CommitSelected commit.Hash
                SelectedDiffHash = Some commit.Hash
                SelectedDiffFiles = Some [ sampleSummary "a.txt" "a.txt" "a.txt"; sampleSummary "b.txt" "b.txt" "b.txt" ]
                SelectedDiff = Some [ fileA; fileB ] }
        projection.Update baseModel
        let rowsB () =
            projection.SelectedDiffRows
            |> Seq.skipWhile (function :? DiffFileHeaderProjection as header -> header.DisplayPath <> "b.txt" | _ -> true)
            |> Seq.toArray
        let before = rowsB ()

        let expansion : App.FileExpansion = { FullContext = Some fullA; Revealed = [ { Start = 20; End = 29 } ]; PendingRequestId = None }
        projection.Update { baseModel with DiffExpansions = Map.ofList [ { OldPath = "a.txt"; NewPath = "a.txt" }, expansion ] }

        let linesA =
            projection.SelectedDiffRows
            |> Seq.takeWhile (function :? DiffFileHeaderProjection as header -> header.DisplayPath <> "b.txt" | _ -> true)
            |> Seq.choose (function :? DiffLineProjection as line -> line.NewLineNo |> Option.ofNullable | _ -> None)
            |> Seq.toList
        test <@ linesA = [ 20 .. 30 ] @>
        test <@ rowsB () |> Array.map (fun row -> row.GetType()) = (before |> Array.map (fun row -> row.GetType())) @>
        test <@ projection.DiffContextLineCount = emptyModel.DiffContextLines @>

    [<Fact>]
    let ``CommitProjection should surface refs in the row summary`` () =
        let projection = CommitProjection()
        projection.ShowBranchRefs <- true
        projection.ShowStashes <- true
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
                HasIncoming = false
                Color = 0
            }

        test <@ projection.HasRefs @>
        test <@ projection.RefsSummary = "main · origin/main · v1.0 +1" @>
        test <@ projection.HasRefBadges @>
        test <@ projection.RefBadges.Count = 4 @>
        test <@ projection.RefBadges.[0].Kind = CommitRefKind.Tag @>
        test <@ projection.RefBadges.[1].Kind = CommitRefKind.Branch @>
        test <@ projection.RefBadges.[2].Kind = CommitRefKind.Remote @>
        test <@ projection.RefBadges.[3].Kind = CommitRefKind.Stash @>
        test <@ projection.RefBadges.[0].Text = "v1.0" @>
        test <@ projection.RefBadges.[3].Text = "stash@{0}" @>

    [<Fact>]
    let ``CommitProjection should hide non-current branch refs until enabled`` () =
        let projection = CommitProjection()
        let commit =
            {
                sampleCommit "feedfacefeedfacefeedfacefeedfacefeedface" "Search hit"
                with
                    Refs =
                        [
                            RefHelpers.branchRef "topic"
                            RefHelpers.currentBranchRef "main"
                            RefHelpers.tagRef "v1.0"
                        ]
            }

        projection.Update
            {
                Commit = commit
                Lane = 0
                Segments = []
                HasIncoming = false
                Color = 0
            }

        test <@ projection.RefBadges.Count = 2 @>
        test <@ projection.RefBadges |> Seq.map (fun badge -> badge.Text) |> Seq.toList = [ "v1.0"; "main" ] @>

        projection.ShowBranchRefs <- true

        test <@ projection.RefBadges.Count = 3 @>

    [<Fact>]
    let ``CommitProjection should initialize segment keys before syncing`` () =
        let projection = CommitProjection()
        let commit =
            sampleCommit "feedfacefeedfacefeedfacefeedfacefeedface" "Search hit"

        projection.Update
            {
                Commit = commit
                Lane = 0
                Segments =
                    [
                        {
                            Lane = 0
                            TargetLane = 0
                            IsCommit = true
                            Color = 0
                        }
                        {
                            Lane = 1
                            TargetLane = 1
                            IsCommit = false
                            Color = 1
                        }
                    ]
                HasIncoming = false
                Color = 0
            }

        test <@ projection.Segments.Count = 2 @>
        test <@ projection.Segments |> Seq.forall (fun segment -> segment.Key <> "") @>

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
                    Selection = App.CommitSelected "new"
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
                    Selection = App.CommitSelected "new"
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

    [<Fact>]
    let ``rendered markdown rejects stale results and applies only the current request`` () =
        let key : GitService.DiffFileKey = { OldPath = "README.md"; NewPath = "README.md" }
        let pending, _ = App.update (App.Msg.SetRenderedMarkdown(key, "", true, false, 20L)) emptyModel
        let file : Models.FileDiff = { OldPath = key.OldPath; NewPath = key.NewPath; Hunks = []; NewLineCount = None }
        let content : RenderedMarkdownContent = { File = file; DiffRows = []; OldRows = []; NewRows = []; Images = [] }
        let stale, _ = App.update (App.Msg.RenderedMarkdownLoaded(key, "", 19L, Ok content)) pending
        let current, _ = App.update (App.Msg.RenderedMarkdownLoaded(key, "", 20L, Ok content)) stale
        test <@ stale.RenderedMarkdown.Value.Content = None @>
        test <@ current.RenderedMarkdown.Value.Content = Some content @>

    [<Fact>]
    let ``remote image completion updates active diff and whole-file payloads`` () =
        let key : GitService.DiffFileKey = { OldPath = "README.md"; NewPath = "README.md" }
        let file : Models.FileDiff = { OldPath = key.OldPath; NewPath = key.NewPath; Hunks = []; NewLineCount = None }
        let blocked = { Side = MarkdownImageSide.New; Source = "https://example.test/image.png"; Bytes = None; Error = Some "Remote image blocked" }
        let content : RenderedMarkdownContent = { File = file; DiffRows = []; OldRows = []; NewRows = []; Images = [ blocked ] }
        let payload : GitService.WholeFilePayload = { File = file; Rendered = Some content; ImageBytes = None }
        let initial =
            { emptyModel with
                RenderedMarkdown = Some { Key = key; Section = ""; RequestId = 1L; Content = Some content }
                WholeFile = Some { Key = key; Section = ""; RequestId = 2L; Preview = true; Payload = Some payload } }
        let updated, _ = App.update (App.Msg.RemoteMarkdownImageLoaded(blocked.Source, Ok [| 1uy; 2uy |])) initial
        test <@ updated.RenderedMarkdown.Value.Content.Value.Images.Head.Bytes = Some [| 1uy; 2uy |] @>
        test <@ updated.WholeFile.Value.Payload.Value.Rendered.Value.Images.Head.Error = None @>

    [<Fact>]
    let ``whole file payload rejects completion after dismissal`` () =
        let key : GitService.DiffFileKey = { OldPath = "README.md"; NewPath = "README.md" }
        let pending, _ = App.update (App.Msg.OpenWholeFile(key, "", true, false, 30L)) emptyModel
        let dismissed, _ = App.update (App.Msg.DismissWholeFile 30L) pending
        let file : Models.FileDiff = { OldPath = key.OldPath; NewPath = key.NewPath; Hunks = []; NewLineCount = None }
        let payload : GitService.WholeFilePayload = { File = file; Rendered = None; ImageBytes = None }
        let stale, _ = App.update (App.Msg.WholeFileLoaded(key, "", 30L, Ok payload)) dismissed
        test <@ stale.WholeFile = None @>


module DiffExpansionTests =

    let private ctx number : Models.DiffLine =
        { Type = Models.Context; Content = sprintf "l%d" number; OldLineNo = Some number; NewLineNo = Some number }

    let private gapOf kind start count : DiffExpansion.DiffGap =
        { OldPath = "f"; NewPath = "f"; Kind = kind; OldStart = start; NewStart = start; HiddenCount = count }

    [<Fact>]
    let ``normalize should sort and merge overlapping and adjacent ranges`` () =
        let merged = DiffExpansion.normalize [ { Start = 20; End = 25 }; { Start = 1; End = 5 }; { Start = 6; End = 8 }; { Start = 24; End = 30 }; { Start = 40; End = 39 } ]
        test <@ merged = [ { Start = 1; End = 8 }; { Start = 20; End = 30 } ] @>

    [<Fact>]
    let ``revealRange should reveal ten lines from the requested edge or the whole gap`` () =
        let gap = gapOf DiffExpansion.Internal 11 (Some 25)
        test <@ DiffExpansion.revealRange DiffExpansion.Down gap = Some { Start = 11; End = 20 } @>
        test <@ DiffExpansion.revealRange DiffExpansion.Up gap = Some { Start = 26; End = 35 } @>
        test <@ DiffExpansion.revealRange DiffExpansion.All gap = Some { Start = 11; End = 35 } @>
        let small = gapOf DiffExpansion.Internal 11 (Some 4)
        test <@ DiffExpansion.revealRange DiffExpansion.Down small = Some { Start = 11; End = 14 } @>
        test <@ DiffExpansion.revealRange DiffExpansion.Up small = Some { Start = 11; End = 14 } @>
        let trailing = gapOf DiffExpansion.Trailing 50 None
        test <@ DiffExpansion.revealRange DiffExpansion.Up trailing = None @>
        test <@ DiffExpansion.revealRange DiffExpansion.Down trailing = Some { Start = 50; End = 59 } @>

    [<Fact>]
    let ``availableDirections should only offer bounded controls that apply`` () =
        test <@ DiffExpansion.availableDirections (gapOf DiffExpansion.Leading 1 (Some 30)) = [ DiffExpansion.Up; DiffExpansion.All ] @>
        test <@ DiffExpansion.availableDirections (gapOf DiffExpansion.Internal 1 (Some 30)) = [ DiffExpansion.Down; DiffExpansion.Up; DiffExpansion.All ] @>
        test <@ DiffExpansion.availableDirections (gapOf DiffExpansion.Trailing 1 None) = [ DiffExpansion.Down; DiffExpansion.All ] @>
        test <@ DiffExpansion.availableDirections (gapOf DiffExpansion.Internal 1 (Some 10)) = [ DiffExpansion.All ] @>

    let private configured : Models.FileDiff =
        { OldPath = "f"; NewPath = "f"
          NewLineCount = None
          Hunks =
            [ { Header = "@@ -10,2 +10,2 @@ fn a"; Lines = [ ctx 10; { Type = Models.Removed; Content = "x"; OldLineNo = Some 11; NewLineNo = None }; { Type = Models.Added; Content = "y"; OldLineNo = None; NewLineNo = Some 11 } ] }
              { Header = "@@ -40 +40 @@ fn b"; Lines = [ ctx 40 ] } ] }

    let private full : Models.FileDiff =
        let lines =
            [ for n in 1 .. 50 do
                if n = 11 then
                    yield ({ Type = Models.Removed; Content = "x"; OldLineNo = Some 11; NewLineNo = None } : Models.DiffLine)
                    yield ({ Type = Models.Added; Content = "y"; OldLineNo = None; NewLineNo = Some 11 } : Models.DiffLine)
                else
                    yield ctx n ]
        { configured with Hunks = [ { Header = "@@ -1,50 +1,50 @@"; Lines = lines } ] }

    let private shape blocks =
        blocks
        |> List.map (function
            | DiffExpansion.GapBlock gap -> sprintf "gap:%A:%d:%A" gap.Kind gap.NewStart gap.HiddenCount
            | DiffExpansion.HunkBlock hunk -> sprintf "hunk:%d" hunk.Lines.Length)

    [<Fact>]
    let ``project without full context should emit leading internal and trailing gaps`` () =
        test <@ shape (DiffExpansion.project configured None []) = [ "gap:Leading:1:Some 9"; "hunk:3"; "gap:Internal:12:Some 28"; "hunk:1"; "gap:Trailing:41:None" ] @>

    [<Fact>]
    let ``project should use the new file line count to bound or omit the trailing gap`` () =
        test <@ shape (DiffExpansion.project { configured with NewLineCount = Some 50 } None []) |> List.last = "gap:Trailing:41:Some 10" @>
        test <@ shape (DiffExpansion.project { configured with NewLineCount = Some 40 } None []) |> List.last = "hunk:1" @>

    [<Fact>]
    let ``project without full context should keep hunks unchanged while revealed ranges wait for context`` () =
        let blocks = DiffExpansion.project configured None [ { Start = 1; End = 9 } ]
        test <@ blocks |> List.choose (function DiffExpansion.HunkBlock hunk -> Some hunk | _ -> None) = configured.Hunks @>

    [<Fact>]
    let ``project should omit surrounding gaps for added and deleted files`` () =
        let added = { configured with OldPath = "/dev/null"; NewLineCount = None; Hunks = [ configured.Hunks.Head ] }
        test <@ shape (DiffExpansion.project added None []) = [ "hunk:3" ] @>

    [<Fact>]
    let ``project with full context should know trailing counts and reveal ranges`` () =
        test <@ shape (DiffExpansion.project configured (Some full) []) = [ "gap:Leading:1:Some 9"; "hunk:3"; "gap:Internal:12:Some 28"; "hunk:1"; "gap:Trailing:41:Some 10" ] @>
        let revealed = DiffExpansion.project configured (Some full) [ { Start = 12; End = 21 }; { Start = 5; End = 9 } ]
        test <@ shape revealed = [ "gap:Leading:1:Some 4"; "hunk:18"; "gap:Internal:22:Some 18"; "hunk:1"; "gap:Trailing:41:Some 10" ] @>

    [<Fact>]
    let ``project should merge hunks when a gap is fully revealed and keep header context`` () =
        let blocks = DiffExpansion.project configured (Some full) [ { Start = 12; End = 39 } ]
        test <@ shape blocks = [ "gap:Leading:1:Some 9"; "hunk:32"; "gap:Trailing:41:Some 10" ] @>
        match blocks.[1] with
        | DiffExpansion.HunkBlock hunk -> test <@ hunk.Header = "@@ -10,31 +10,31 @@ fn a" @>
        | _ -> failwith "expected hunk"

module DiffExpansionFlowTests =

    let private hash = "abcdefabcdefabcdefabcdefabcdefabcdefabcd"
    let private key : GitService.DiffFileKey = { OldPath = "f"; NewPath = "f" }
    let private gap : DiffExpansion.DiffGap =
        { OldPath = "f"; NewPath = "f"; Kind = DiffExpansion.Internal; OldStart = 12; NewStart = 12; HiddenCount = Some 28 }
    let private file : Models.FileDiff = { OldPath = "f"; NewPath = "f"; NewLineCount = None; Hunks = [] }

    let private selectedModel () =
        let model, _ = App.init [||]
        { model with
            Selection = App.CommitSelected hash
            SelectedDiffHash = Some hash
            SelectedDiffFiles = Some [ { OldPath = "f"; NewPath = "f"; DisplayPath = "f" } ]
            SelectedDiff = Some [ file ] }

    [<Fact>]
    let ``ExpandDiffGap should record revealed range and start one file load without touching context preference`` () =
        let model = selectedModel ()
        let next, cmd = App.update (App.Msg.ExpandDiffGap(hash, gap, DiffExpansion.Down, 7L)) model
        let expansion = next.DiffExpansions.[key]
        test <@ expansion.Revealed = [ { Start = 12; End = 21 } ] @>
        test <@ expansion.PendingRequestId = Some 7L @>
        test <@ not (List.isEmpty cmd) @>
        test <@ next.DiffContextLines = model.DiffContextLines && next.SelectedDiff = model.SelectedDiff @>

        let again, againCmd = App.update (App.Msg.ExpandDiffGap(hash, gap, DiffExpansion.Up, 8L)) next
        test <@ again.DiffExpansions.[key].Revealed = [ { Start = 12; End = 21 }; { Start = 30; End = 39 } ] @>
        test <@ again.DiffExpansions.[key].PendingRequestId = Some 7L @>
        test <@ List.isEmpty againCmd @>

    [<Fact>]
    let ``DiffFileContextLoaded should accept only the current commit file and request`` () =
        let pending, _ = App.update (App.Msg.ExpandDiffGap(hash, gap, DiffExpansion.All, 7L)) (selectedModel ())
        let stale, _ = App.update (App.Msg.DiffFileContextLoaded(hash, key, 6L, Ok file)) pending
        test <@ stale.DiffExpansions.[key].FullContext = None @>
        let otherCommit, _ = App.update (App.Msg.DiffFileContextLoaded("0000", key, 7L, Ok file)) pending
        test <@ otherCommit.DiffExpansions.[key].FullContext = None @>
        let otherFile, _ = App.update (App.Msg.DiffFileContextLoaded(hash, { OldPath = "g"; NewPath = "g" }, 7L, Ok file)) pending
        test <@ otherFile.DiffExpansions = pending.DiffExpansions @>
        let accepted, _ = App.update (App.Msg.DiffFileContextLoaded(hash, key, 7L, Ok file)) pending
        test <@ accepted.DiffExpansions.[key].FullContext = Some file && accepted.DiffExpansions.[key].PendingRequestId = None @>

    [<Fact>]
    let ``ExpandDiffGap should ignore stale commits and unknown files`` () =
        let model = selectedModel ()
        let staleCommit, cmd = App.update (App.Msg.ExpandDiffGap("0000", gap, DiffExpansion.Down, 7L)) model
        test <@ staleCommit.DiffExpansions.IsEmpty && List.isEmpty cmd @>
        let unknown, _ = App.update (App.Msg.ExpandDiffGap(hash, { gap with OldPath = "g"; NewPath = "g" }, DiffExpansion.Down, 7L)) model
        test <@ unknown.DiffExpansions.IsEmpty @>

    [<Fact>]
    let ``SelectCommit should discard expansion state`` () =
        let pending, _ = App.update (App.Msg.ExpandDiffGap(hash, gap, DiffExpansion.All, 7L)) (selectedModel ())
        let next, _ = App.update (App.Msg.SelectCommit("1111", 9L)) pending
        test <@ next.DiffExpansions.IsEmpty @>
        let late, _ = App.update (App.Msg.DiffFileContextLoaded(hash, key, 7L, Ok file)) next
        test <@ late.DiffExpansions.IsEmpty @>


module FileHeaderTests =

    let private hash = "abcdefabcdefabcdefabcdefabcdefabcdefabcd"
    let private key : GitService.DiffFileKey = { OldPath = "f"; NewPath = "f" }
    let private ctx number : Models.DiffLine = { Type = Models.Context; Content = sprintf "l%d" number; OldLineNo = Some number; NewLineNo = Some number }
    let private file : Models.FileDiff =
        { OldPath = "f"; NewPath = "f"; NewLineCount = Some 40
          Hunks = [ { Header = "@@ -20,2 +20,2 @@"; Lines = [ ctx 20; { Type = Models.Added; Content = "x"; OldLineNo = None; NewLineNo = Some 21 } ] } ] }

    let private selectedModel () =
        let model, _ = App.init [||]
        { model with
            Selection = App.CommitSelected hash
            SelectedDiffHash = Some hash
            SelectedDiffFiles = Some [ { OldPath = "f"; NewPath = "f"; DisplayPath = "f" } ]
            SelectedDiff = Some [ file ] }

    [<Fact>]
    let ``DiffStatBar blocks should scale like GitHub and keep both colours visible`` () =
        let check added removed expected = 
            let struct (g, r) = DiffStatBar.Blocks(added, removed)
            test <@ (g, r) = expected @>
        check 0 0 (0, 0)
        check 3 0 (3, 0)
        check 9 0 (5, 0)
        check 6 1 (4, 1)
        check 100 1 (4, 1)
        check 1 100 (1, 4)

    [<Fact>]
    let ``ExpandDiffFile should reveal the whole file and CollapseDiffFileContext should clear it`` () =
        let expanded, cmd = App.update (App.Msg.ExpandDiffFile(hash, key, 5L)) (selectedModel ())
        test <@ expanded.DiffExpansions.[key].Revealed = [ { Start = 1; End = Int32.MaxValue } ] @>
        test <@ expanded.DiffExpansions.[key].PendingRequestId = Some 5L && not (List.isEmpty cmd) @>
        let collapsed, _ = App.update (App.Msg.CollapseDiffFileContext(hash, key)) expanded
        test <@ collapsed.DiffExpansions.[key].Revealed = [] @>
        let stale, _ = App.update (App.Msg.CollapseDiffFileContext("0000", key)) expanded
        test <@ stale.DiffExpansions = expanded.DiffExpansions @>

    [<Fact>]
    let ``MainProjection should collapse a file to its header and report stats and change kind`` () =
        let projection = MainProjection()
        projection.Update (selectedModel ())
        let fileProjection = projection.SelectedDiffFiles |> Seq.head
        test <@ fileProjection.AddedLines = 1 && fileProjection.RemovedLines = 0 @>
        test <@ fileProjection.ChangeKind = "modified" && fileProjection.HasHiddenContext @>
        let before = projection.SelectedDiffRows.Count
        projection.ToggleDiffFileCollapsedCommand.Execute fileProjection
        test <@ before > 1 && projection.SelectedDiffRows.Count = 1 @>
        projection.ToggleDiffFileCollapsedCommand.Execute fileProjection
        test <@ projection.SelectedDiffRows.Count = before @>

    [<Fact>]
    let ``MainProjection context toggle should dispatch expand then collapse for a file`` () =
        let projection = MainProjection()
        let messages = ResizeArray<App.Msg>()
        let model = selectedModel ()
        projection.Update model
        projection.SetDispatch (fun message -> messages.Add message)
        let fileProjection = projection.SelectedDiffFiles |> Seq.head
        projection.ToggleDiffFileContextCommand.Execute fileProjection
        match List.ofSeq messages with
        | [ App.Msg.ExpandDiffFile (h, k, _) ] -> test <@ h = hash && k = key @>
        | other -> failwithf "unexpected %A" other


        let full : Models.FileDiff = { file with Hunks = [ { Header = "@@ -1,40 +1,41 @@"; Lines = [ for n in 1 .. 40 -> ctx n ] } ] }
        let expansion : App.FileExpansion = { FullContext = Some full; Revealed = [ { Start = 1; End = Int32.MaxValue } ]; PendingRequestId = None }
        projection.Update { model with DiffExpansions = Map.ofList [ key, expansion ] }
        messages.Clear()
        test <@ not fileProjection.HasHiddenContext && fileProjection.HasRevealedContext @>
        projection.ToggleDiffFileContextCommand.Execute fileProjection
        test <@ List.ofSeq messages = [ App.Msg.CollapseDiffFileContext(hash, key) ] @>



module DiffFileTreeTests =

    let private file (oldPath: string) (newPath: string) =
        DiffFileProjection({ OldPath = oldPath; NewPath = newPath; DisplayPath = (if newPath = "/dev/null" then oldPath else newPath) } : GitService.DiffFileSummary)

    let private describe (rows: obj seq) =
        rows
        |> Seq.map (function
            | :? DiffFileFolderRow as folder -> sprintf "%d:[%s]" (int (folder.Indent.Left / DiffFileTree.IndentWidth)) folder.Name
            | :? DiffFileProjection as f -> sprintf "%d:%s" (int (f.ListIndent.Left / DiffFileTree.IndentWidth)) f.ListLabel
            | other -> string other)
        |> List.ofSeq

    let private files () =
        [ file "src/FsLiveDocs.Cli/Program.fs" "src/FsLiveDocs.Cli/Program.fs"
          file "NEXT_VERSION" "NEXT_VERSION"
          file "/dev/null" "dev-docs/releases/0.6.2.md"
          file "src/FsLiveDocs.Cli/CommandLine.fs" "src/FsLiveDocs.Cli/CommandLine.fs"
          file "Directory.Build.props" "/dev/null" ]

    [<Fact>]
    let ``patch mode should list full paths in diff order`` () =
        let rows = DiffFileTree.BuildRows(files (), false, Collections.Generic.HashSet<string>())
        test <@ describe (Seq.cast rows) = [ "0:src/FsLiveDocs.Cli/Program.fs"; "0:NEXT_VERSION"; "0:dev-docs/releases/0.6.2.md"; "0:src/FsLiveDocs.Cli/CommandLine.fs"; "0:Directory.Build.props" ] @>

    [<Fact>]
    let ``diff file headers should follow patch and tree display order`` () =
        let projection = MainProjection()
        let summaries : GitService.DiffFileSummary list =
            [ { OldPath = "z/last.fs"; NewPath = "z/last.fs"; DisplayPath = "z/last.fs" }
              { OldPath = "a/second.fs"; NewPath = "a/second.fs"; DisplayPath = "a/second.fs" }
              { OldPath = "a/first.fs"; NewPath = "a/first.fs"; DisplayPath = "a/first.fs" } ]
        let model, _ = App.init [||]
        projection.Update { model with Selection = App.CommitSelected "abc"; SelectedDiffHash = Some "abc"; SelectedDiffFiles = Some summaries }
        let headerPaths () =
            projection.SelectedDiffRows
            |> Seq.choose (function :? DiffFileHeaderProjection as header -> Some header.DisplayPath | _ -> None)
            |> List.ofSeq
        test <@ headerPaths () = [ "z/last.fs"; "a/second.fs"; "a/first.fs" ] @>
        projection.SetDiffFileListModeCommand.Execute "tree"
        test <@ headerPaths () = [ "a/first.fs"; "a/second.fs"; "z/last.fs" ] @>
        let listed =
            projection.DiffFileListRows
            |> Seq.choose (function :? DiffFileProjection as file -> Some file.DisplayPath | _ -> None)
            |> List.ofSeq
        test <@ listed = headerPaths () @>

    [<Fact>]
    let ``tree mode should merge single-folder chains, list folders first and honour collapsed folders`` () =
        let rows = DiffFileTree.BuildRows(files (), true, Collections.Generic.HashSet<string>())
        test <@ describe (Seq.cast rows) = [ "0:[dev-docs/releases]"; "1:0.6.2.md"; "0:[src/FsLiveDocs.Cli]"; "1:CommandLine.fs"; "1:Program.fs"; "0:Directory.Build.props"; "0:NEXT_VERSION" ] @>
        let collapsed = DiffFileTree.BuildRows(files (), true, Collections.Generic.HashSet<string>([ "src/FsLiveDocs.Cli" ]))
        test <@ describe (Seq.cast collapsed) = [ "0:[dev-docs/releases]"; "1:0.6.2.md"; "0:[src/FsLiveDocs.Cli]"; "0:Directory.Build.props"; "0:NEXT_VERSION" ] @>

    [<Fact>]
    let ``all files mode should list unchanged files and expand only folders holding changes`` () =
        let changed = [ file "src/FsLiveDocs.Cli/Program.fs" "src/FsLiveDocs.Cli/Program.fs"; file "Directory.Build.props" "/dev/null" ]
        let all = [ "src/FsLiveDocs.Cli/Program.fs"; "src/FsLiveDocs.Cli/Other.fs"; "docs/README.md"; "LICENSE" ]
        let describeAll (rows: obj seq) =
            rows
            |> Seq.map (function
                | :? RepoFileRow as unchanged -> sprintf "%d:~%s" (int (unchanged.Indent.Left / DiffFileTree.IndentWidth)) unchanged.Name
                | row -> describe [ row ] |> List.head)
            |> List.ofSeq
        let rows = DiffFileTree.BuildAllFilesRows(changed, all, Collections.Generic.HashSet<string>())
        test <@ describeAll (Seq.cast rows) = [ "0:[docs]"; "0:[src/FsLiveDocs.Cli]"; "1:~Other.fs"; "1:Program.fs"; "0:Directory.Build.props"; "0:~LICENSE" ] @>
        let toggled = DiffFileTree.BuildAllFilesRows(changed, all, Collections.Generic.HashSet<string>([ "docs"; "src/FsLiveDocs.Cli" ]))
        test <@ describeAll (Seq.cast toggled) = [ "0:[docs]"; "1:~README.md"; "0:[src/FsLiveDocs.Cli]"; "0:Directory.Build.props"; "0:~LICENSE" ] @>

    [<Fact>]
    let ``diff zoom should step, clamp and reset`` () =
        let projection = MainProjection()
        projection.ZoomDiff 1
        test <@ projection.DiffFontSize = 13.0 @>
        projection.DiffFontSize <- 100.0
        test <@ projection.DiffFontSize = MainProjection.MaxDiffFontSize @>
        projection.ZoomDiff 0
        test <@ projection.DiffFontSize = 12.0 @>

    [<Fact>]
    let ``folder rows should total the added and removed lines of loaded files beneath them`` () =
        let a = file "src/a.fs" "src/a.fs"
        let b = file "src/deep/b.fs" "src/deep/b.fs"
        let c = file "other.txt" "other.txt"
        let hunk lines : Models.DiffHunk = { Header = "@@ -1 +1 @@"; Lines = lines }
        let line kind : Models.DiffLine = { Type = kind; Content = "x"; OldLineNo = Some 1; NewLineNo = Some 1 }
        a.ApplyContent { OldPath = "src/a.fs"; NewPath = "src/a.fs"; NewLineCount = None; Hunks = [ hunk [ line Models.Added; line Models.Added; line Models.Removed ] ] }
        b.ApplyContent { OldPath = "src/deep/b.fs"; NewPath = "src/deep/b.fs"; NewLineCount = None; Hunks = [ hunk [ line Models.Added ] ] }
        let rows = DiffFileTree.BuildRows([ a; b; c ], true, Collections.Generic.HashSet<string>())
        let src = rows |> Seq.pick (function :? DiffFileFolderRow as folder when folder.Name = "src" -> Some folder | _ -> None)
        test <@ src.AddedText = "+3" && src.RemovedText = "−1" && src.HasChanges @>

    [<Fact>]
    let ``error statuses should be recognised so the footer can draw attention`` () =
        test <@ MainProjection.IsErrorStatus "Error: Could not locate a Git repository." @>
        test <@ MainProjection.IsErrorStatus "Could not start VS Code (is 'code' on PATH?)" @>
        test <@ MainProjection.IsErrorStatus "No commit found for 'ab'" @>
        test <@ not (MainProjection.IsErrorStatus "Loaded 1000 commits") @>
        test <@ not (MainProjection.IsErrorStatus "Copied 3 lines") @>

    [<Fact>]
    let ``MainProjection folder selection should toggle the folder and keep the selected file`` () =
        let projection = MainProjection()
        let summaries : GitService.DiffFileSummary list =
            [ { OldPath = "a/x.fs"; NewPath = "a/x.fs"; DisplayPath = "a/x.fs" }
              { OldPath = "a/y.fs"; NewPath = "a/y.fs"; DisplayPath = "a/y.fs" } ]
        let model, _ = App.init [||]
        projection.Update { model with Selection = App.CommitSelected "abc"; SelectedDiffHash = Some "abc"; SelectedDiffFiles = Some summaries }
        projection.SetDiffFileListModeCommand.Execute "tree"
        test <@ projection.DiffFileListRows.Count = 3 @>
        let selected = projection.SelectedDiffFile
        let folder = projection.DiffFileListRows.[0] :?> DiffFileFolderRow
        // Selecting a folder row is ignored; toggling happens on click.
        projection.SelectedDiffFileListRow <- folder
        test <@ projection.DiffFileListRows.Count = 3 && obj.ReferenceEquals(projection.SelectedDiffFileListRow, selected) @>
        projection.ToggleDiffFolderCommand.Execute folder
        test <@ projection.DiffFileListRows.Count = 1 && obj.ReferenceEquals(projection.SelectedDiffFile, selected) @>
        // A collapsed folder reopens on the next toggle (a fresh row instance for the same path).
        projection.ToggleDiffFolderCommand.Execute (projection.DiffFileListRows.[0] :?> DiffFileFolderRow)
        test <@ projection.DiffFileListRows.Count = 3 @>
        projection.ToggleDiffFolderCommand.Execute (projection.DiffFileListRows.[0] :?> DiffFileFolderRow)
        projection.SetDiffFileListModeCommand.Execute "patch"
        test <@ projection.DiffFileListRows.Count = 2 @>

module SearchProjectionTests =

    [<Fact>]
    let ``advanced fields should read and write prefixed terms in the query`` () =
        let projection = MainProjection()
        projection.SearchQuery <- "fonts author:jane"
        test <@ projection.AdvancedAuthor = "jane" && projection.HasAuthorFilter @>
        projection.AdvancedPath <- "src/GitKay UI"
        test <@ projection.SearchQuery = "fonts author:jane path:\"src/GitKay UI\"" @>
        projection.AdvancedAuthor <- ""
        test <@ projection.SearchQuery = "fonts path:\"src/GitKay UI\"" && not projection.HasAuthorFilter @>

    [<Fact>]
    let ``column filter should set the field, show only matches and run the search`` () =
        let projection = MainProjection()
        let messages = ResizeArray<App.Msg>()
        projection.SetDispatch (fun message -> messages.Add message)
        projection.SearchQuery <- "parser"
        messages.Clear()
        projection.ApplyColumnFilter("author", "Jane Doe")
        test <@ projection.SearchQuery = "parser author:\"Jane Doe\"" && projection.ShowOnlySearchMatches @>
        test <@ messages |> Seq.exists (function App.Msg.RunSearch ("parser author:\"Jane Doe\"", "commit", _) -> true | _ -> false) @>

    [<Fact>]
    let ``recent searches should be most recent first, de-duplicated and filtered by typed text`` () =
        let projection = MainProjection()
        projection.LoadRecentSearches [ "author:jane"; "fonts"; "fonts"; "path:src/" ]
        projection.SearchQuery <- "src"
        projection.SearchOrNextCommitCommand.Execute null
        test <@ List.ofSeq projection.RecentSearches = [ "src"; "author:jane"; "fonts"; "path:src/" ] @>
        projection.SearchQuery <- "f"
        projection.UpdateRecentSearchMatches true
        test <@ List.ofSeq projection.RecentSearchMatches = [ "fonts" ] && projection.IsRecentSearchesOpen @>
        projection.SearchQuery <- "zzz"
        projection.UpdateRecentSearchMatches true
        test <@ not projection.IsRecentSearchesOpen @>

    [<Fact>]
    let ``search modes should update the placeholder and dispatch the mode`` () =
        let projection = MainProjection()
        let messages = ResizeArray<App.Msg>()
        projection.SetDispatch (fun message -> messages.Add message)
        projection.SetSearchModeCommand.Execute "path"
        test <@ projection.IsPathSearchMode && projection.SearchPlaceholder.Contains "File or folder" @>
        test <@ messages |> Seq.contains (App.Msg.SetSearchScope "path") @>
        projection.SearchUseRegex <- true
        test <@ messages |> Seq.contains (App.Msg.SetSearchRegex true) @>

    [<Fact>]
    let ``Enter should find next, and select the first match once pending results arrive`` () =
        let commitOf hash subject : Models.Commit =
            { Hash = hash; AuthorName = "A"; AuthorEmail = "a@example.com"; Timestamp = 1L; Parents = []; Subject = subject; Message = subject; Refs = [] }
        let first = commitOf "1111111111111111111111111111111111111111" "one"
        let second = commitOf "2222222222222222222222222222222222222222" "needle two"
        let third = commitOf "3333333333333333333333333333333333333333" "needle three"
        let projection = MainProjection()
        let messages = ResizeArray<App.Msg>()
        let model0, _ = App.init [||]
        let baseModel = { model0 with Commits = Graph.calculateLanes [ first; second; third ]; Selection = App.CommitSelected first.Hash }
        projection.Update baseModel
        projection.SetDispatch (fun message -> messages.Add message)

        // Enter before results exist: runs the search and waits.
        projection.SearchQuery <- "needle"
        projection.SearchOrNextCommitCommand.Execute null
        let selections () = messages |> Seq.choose (function App.Msg.SelectCommit (hash, _) -> Some hash | _ -> None) |> List.ofSeq
        test <@ messages |> Seq.exists (function App.Msg.RunSearch ("needle", _, _) -> true | _ -> false) @>
        test <@ selections () = [] @>

        // Results arrive: the first match is selected without another Enter.
        let result (commit: Models.Commit) : GitSearch.Result = { Commit = commit; MatchKinds = [ GitSearch.MessageMatch ]; MatchSummary = "message"; MatchedPaths = []; MatchedRefs = [] }
        let searched = { baseModel with SearchQuery = "needle"; SearchResults = Some [ result second; result third ] }
        projection.Update searched
        test <@ selections () = [ second.Hash ] @>

        // Next Enter moves on to the following match.
        projection.Update { searched with Selection = App.CommitSelected second.Hash }
        projection.SearchOrNextCommitCommand.Execute null
        test <@ selections () = [ second.Hash; third.Hash ] @>

    [<Fact>]
    let ``unobserved D-Bus platform errors should not be treated as fatal`` () =
        // Any Tmds.DBus error counts; this is the one Avalonia's Linux integration raises when a service is absent.
        let dbus = Tmds.DBus.Protocol.DBusErrorReplyException("org.freedesktop.DBus.Error.ServiceUnknown", "The name is not activatable")
        test <@ FatalErrorPresenter.IsIgnorablePlatformError(AggregateException(dbus :> exn)) @>
        test <@ not (FatalErrorPresenter.IsIgnorablePlatformError(AggregateException(InvalidOperationException("real") :> exn))) @>
        test <@ not (FatalErrorPresenter.IsIgnorablePlatformError(AggregateException(dbus :> exn, InvalidOperationException("real") :> exn))) @>

    [<Fact>]
    let ``pane directions should follow the commits-over-diff-and-files layout`` () =
        let move pane key = MainWindow.PaneInDirection(pane, key) |> Option.ofNullable
        test <@ move MainWindow.Pane.Commits Key.J = Some MainWindow.Pane.Diff @>
        test <@ move MainWindow.Pane.Diff Key.L = Some MainWindow.Pane.Files @>
        test <@ move MainWindow.Pane.Files Key.H = Some MainWindow.Pane.Diff @>
        test <@ move MainWindow.Pane.Files Key.Up = Some MainWindow.Pane.Commits @>
        test <@ move MainWindow.Pane.Commits Key.L = None @>

    [<Fact>]
    let ``an active search with no results should count as active so show-only-matches shows nothing`` () =
        let projection = MainProjection()
        let model, _ = App.init [||]
        projection.Update { model with SearchQuery = "author:Adzz"; SearchResults = Some [] }
        test <@ projection.IsCommitSearchActive @>
        projection.Update { model with SearchQuery = ""; SearchResults = None }
        test <@ not projection.IsCommitSearchActive @>

module DiffSearchBorrowTests =

    [<Fact>]
    let ``empty find box borrows the commit search diff term without being overwritten`` () =
        let commit : Models.Commit =
            { Hash = "abcabcabcabcabcabcabcabcabcabcabcabcabca"; AuthorName = "A"; AuthorEmail = "a@x"; Timestamp = 1L; Parents = []; Subject = "s"; Message = "s"; Refs = [] }
        let line kind content newNo : Models.DiffLine = { Type = kind; Content = content; OldLineNo = Some newNo; NewLineNo = Some newNo }
        let file : Models.FileDiff =
            { OldPath = "a.fs"; NewPath = "a.fs"; NewLineCount = Some 3
              Hunks = [ { Header = "@@ -1,3 +1,3 @@"; Lines = [ line Models.Context "let font = 1" 1; line Models.Added "other" 2; line Models.Added "font size" 3 ] } ] }
        let model0, _ = App.init [||]
        let model =
            { model0 with
                Commits = Graph.calculateLanes [ commit ]
                Selection = App.CommitSelected commit.Hash
                SelectedDiffHash = Some commit.Hash
                SelectedDiffFiles = Some [ { OldPath = "a.fs"; NewPath = "a.fs"; DisplayPath = "a.fs" } ]
                SelectedDiff = Some [ file ]
                SearchQuery = "font"
                SearchScopeKey = "diff"
                SearchResults = Some [ { Commit = commit; MatchKinds = [ GitSearch.TextMatch ]; MatchSummary = "text"; MatchedPaths = []; MatchedRefs = [] } ] }
        let projection = MainProjection()
        projection.Update model

        test <@ projection.CommitSearchDiffTerm = "font" @>
        test <@ projection.CommitFindQuery = "" && projection.CommitFindPlaceholder.Contains "font" @>

        projection.FindInCommitCommand.Execute null
        let selectedContent () = match projection.SelectedDiffRow with :? DiffLineProjection as l -> l.Content | _ -> ""
        test <@ selectedContent () = "let font = 1" && projection.CommitFindStatusText = "1 of 2 · search" @>

        // Your own text takes over; the commit search term is untouched.
        projection.CommitFindQuery <- "other"
        test <@ selectedContent () = "other" && projection.CommitFindStatusText = "1 of 1" && projection.CommitSearchDiffTerm = "font" @>

        // Clearing your text goes back to borrowing.
        projection.CommitFindQuery <- ""
        projection.FindInCommitCommand.Execute null
        test <@ projection.CommitFindStatusText.EndsWith "· search" @>

        // A commit-mode search has no diff term to borrow.
        projection.Update { model with SearchQuery = "author:jane"; SearchScopeKey = "commit" }
        test <@ projection.CommitSearchDiffTerm = "" @>

    [<Fact>]
    let ``parent and child links should follow the graph and navigate`` () =
        let commitOf hash parents subject : Models.Commit =
            { Hash = hash; AuthorName = "A"; AuthorEmail = "a@x"; Timestamp = 1L; Parents = parents; Subject = subject; Message = subject; Refs = [] }
        let h n = String.replicate 40 (string n)
        let merge = commitOf (h 3) [ h 2; h 1 ] "merge"
        let side = commitOf (h 2) [ h 1 ] "side"
        let root = commitOf (h 1) [] "root"
        let projection = MainProjection()
        let model0, _ = App.init [||]
        let model = { model0 with Commits = Graph.calculateLanes [ merge; side; root ]; Selection = App.CommitSelected merge.Hash }
        let selections = ResizeArray<string>()
        projection.Update model
        projection.SetDispatch (function App.Msg.SelectCommit (hash, _) -> selections.Add hash | _ -> ())
        test <@ projection.SelectedCommitParents |> Seq.map (fun l -> l.Subject) |> List.ofSeq = [ "side"; "root" ] @>
        test <@ projection.SelectedCommitChildren.Count = 0 @>
        projection.GoToParent 1
        test <@ List.ofSeq selections = [ root.Hash ] @>
        projection.Update { model with Selection = App.CommitSelected root.Hash }
        test <@ projection.SelectedCommitChildren |> Seq.map (fun l -> l.Subject) |> List.ofSeq = [ "merge"; "side" ] @>
        projection.GoToChild ()
        test <@ List.ofSeq selections = [ root.Hash; merge.Hash ] @>

    [<Fact>]
    let ``uncommitted changes row sits above HEAD and selects the working tree`` () =
        let commitOf hash subject refs : Models.Commit =
            { Hash = hash; AuthorName = "A"; AuthorEmail = "a@x"; Timestamp = 1L; Parents = []; Subject = subject; Message = subject; Refs = refs }
        let projection = MainProjection()
        let model0, _ = App.init [||]
        let head = commitOf (String.replicate 40 "a") "Head" [ RefHelpers.currentBranchRef "main" ]
        let entry : WorkingTree.Entry = { Path = "a.txt"; OriginalPath = "a.txt"; Staged = WorkingTree.Modified; Unstaged = WorkingTree.Unchanged; Untracked = false }
        let model = { model0 with Commits = Graph.calculateLanes [ head ]; Selection = App.CommitSelected head.Hash }
        projection.Update model
        test <@ projection.Commits.Count = 1 @>

        let dirty = { model with WorkingTree = [ entry ] }
        projection.Update dirty
        test <@ projection.Commits.Count = 2 && projection.Commits.[0].IsWorkingTree && projection.Commits.[0].Subject = "Uncommitted changes" @>

        let selections = ResizeArray<App.Msg>()
        projection.SetDispatch (fun msg -> selections.Add msg)
        projection.SelectedCommit <- projection.Commits.[0]
        test <@ selections |> Seq.exists (function App.Msg.SelectWorkingTree _ -> true | _ -> false) @>

        projection.Update { dirty with Selection = App.WorkingTreeSelected }
        test <@ projection.SelectedCommit.IsWorkingTree @>

        projection.Update { dirty with WorkingTree = [] }
        test <@ projection.Commits.Count = 1 && not projection.Commits.[0].IsWorkingTree @>

    [<Fact>]
    let ``uncommitted changes show Staged, Unstaged and Untracked sections in the files list and diff`` () =
        let projection = MainProjection()
        let model0, _ = App.init [||]
        let line content : Models.DiffLine = { Type = Models.Added; Content = content; OldLineNo = None; NewLineNo = Some 1 }
        let fileDiff oldPath newPath : Models.FileDiff =
            { OldPath = oldPath; NewPath = newPath; Hunks = [ { Header = "@@ -0,0 +1 @@"; Lines = [ line "x" ] } ]; NewLineCount = None }
        let entry path staged unstaged untracked : WorkingTree.Entry =
            { Path = path; OriginalPath = path; Staged = staged; Unstaged = unstaged; Untracked = untracked }
        let changes : GitService.WorkingTreeChanges =
            { Entries = [ entry "a.txt" WorkingTree.Modified WorkingTree.Modified false; entry "new.txt" WorkingTree.Unchanged WorkingTree.Unchanged true ]
              Staged = [ fileDiff "a.txt" "a.txt" ]
              Unstaged = [ fileDiff "a.txt" "a.txt" ]
              Untracked = [ fileDiff "/dev/null" "new.txt" ] }
        projection.Update { model0 with Selection = App.WorkingTreeSelected; WorkingTree = changes.Entries; WorkingTreeChanges = Some changes }

        test <@ projection.IsWorkingTreeDiffShown && projection.SelectedCommitHash = null @>
        test <@ projection.SelectedDiffFiles.Count = 3 @>
        let sections = projection.DiffFileListRows |> Seq.choose (function :? DiffFileSectionRow as row -> Some row.Name | _ -> None) |> List.ofSeq
        test <@ sections = [ "Staged"; "Unstaged"; "Untracked" ] @>
        let diffSections = projection.SelectedDiffRows |> Seq.choose (function :? DiffSectionHeaderProjection as row -> Some row.Name | _ -> None) |> List.ofSeq
        test <@ diffSections = [ "Staged"; "Unstaged"; "Untracked" ] @>
        test <@ projection.SelectedDiffFiles.[0].Marker = "◐" @>

        projection.ToggleDiffSection "Staged"
        let headers = projection.SelectedDiffRows |> Seq.filter (fun row -> row :? DiffFileHeaderProjection) |> Seq.length
        test <@ headers = 2 @>

    [<Fact>]
    let ``working tree watcher reports edits once, index changes, and ignores other git files`` () =
        let root = IO.Path.Combine(IO.Path.GetTempPath(), "gitkay-watch-" + Guid.NewGuid().ToString("N"))
        let gitDir = IO.Path.Combine(root, ".git")
        IO.Directory.CreateDirectory(IO.Path.Combine(gitDir, "objects")) |> ignore
        try
            let mutable count = 0
            use watcher = WorkingTreeWatcher.TryStart(root, gitDir, fun () -> Threading.Interlocked.Increment(&count) |> ignore)
            test <@ not (isNull watcher) @>
            let settle () = Threading.Thread.Sleep 900
            // How many reports the edits produce depends on how the machine batches them; that there is at least one,
            // and none at all for the files GitKay ignores, is what the watcher promises.
            let waitForReport previous =
                let deadline = DateTime.UtcNow.AddSeconds 10.0
                while count <= previous && DateTime.UtcNow < deadline do Threading.Thread.Sleep 50
                count

            IO.File.WriteAllText(IO.Path.Combine(gitDir, "objects", "ab"), "x")
            settle ()
            test <@ count = 0 @>

            for i in 1..5 do IO.File.WriteAllText(IO.Path.Combine(root, $"file{i}.txt"), "x")
            let afterEdits = waitForReport 0
            test <@ afterEdits >= 1 @>

            IO.File.WriteAllText(IO.Path.Combine(gitDir, "index"), "x")
            test <@ waitForReport afterEdits > afterEdits @>
        finally
            IO.Directory.Delete(root, true)

module SearchFieldSuggestionTests =

    [<Fact>]
    let ``search fields suggest refs, authors and hashes from the loaded history`` () =
        let projection = MainProjection()
        let model0, _ = App.init [||]
        let commitOf hash name email refs : Models.Commit =
            { Hash = hash; AuthorName = name; AuthorEmail = email; Timestamp = 1L; Parents = []; Subject = "Subject"; Message = "Subject"; Refs = refs }
        let a = commitOf (String.replicate 40 "a") "Ada Lovelace" "ada@example.com" [ RefHelpers.branchRef "main"; RefHelpers.tagRef "v1.0" ]
        let b = commitOf (String.replicate 40 "b") "Alan Turing" "alan@example.com" [ RefHelpers.remoteRef "origin/main" ]
        projection.Update { model0 with Commits = Graph.calculateLanes [ a; b ]; ShowBranchRefs = true }

        test <@ List.ofSeq projection.RefSuggestions = [ "main"; "origin/main"; "v1.0" ] @>
        test <@ List.ofSeq projection.AuthorSuggestions = [ "Ada Lovelace"; "ada@example.com"; "Alan Turing"; "alan@example.com" ] @>
        test <@ List.ofSeq projection.HashSuggestions = [ CommitFormat.shortHash a.Hash; CommitFormat.shortHash b.Hash ] @>

module SearchDateTests =

    let private now = DateTimeOffset(DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Local))
    let private parse text = GitSearch.tryParseDate now text
    let private local (year, month, day, hour, minute, second) =
        Some(DateTimeOffset(DateTime(year, month, day, hour, minute, second, DateTimeKind.Local)).ToUnixTimeSeconds())

    [<Fact>]
    let ``dates parse at any precision, with or without a time`` () =
        test <@ parse "2026" = local (2026, 1, 1, 0, 0, 0) @>
        test <@ parse "2026-09" = local (2026, 9, 1, 0, 0, 0) @>
        test <@ parse "2026/09/16" = local (2026, 9, 16, 0, 0, 0) @>
        test <@ parse "2026-09-16T07" = local (2026, 9, 16, 7, 0, 0) @>
        test <@ parse "2026-09-16T07:13" = local (2026, 9, 16, 7, 13, 0) @>
        test <@ parse "2026-09-16 07:13:45" = local (2026, 9, 16, 7, 13, 45) @>

    [<Fact>]
    let ``impossible dates and other text are not dates`` () =
        test <@ parse "2026-13" = None && parse "2026-02-30" = None && parse "2026-09-16T24:00" = None @>
        test <@ parse "last tuesday" = None @>
        // Relative dates still work.
        test <@ parse "2 weeks ago" = Some(now.AddDays(-14.0).ToUnixTimeSeconds()) @>
        test <@ parse "yesterday" = Some(now.AddDays(-1.0).ToUnixTimeSeconds()) @>

module WorkingTreeStagingTests =

    let private numbered (lines: string list) = String.concat "\n" lines + "\n"

    /// A repository with one committed file, cleaned up afterwards.
    let private withRepository (committed: string) (action: string -> unit) =
        let root = Path.Combine(Path.GetTempPath(), "gitkay-staging-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        try
            Repository.Init root |> ignore
            use repo = new Repository(root)
            repo.Config.Add("user.name", "GitKay Tests") |> ignore
            repo.Config.Add("user.email", "gitkay@example.com") |> ignore
            File.WriteAllText(Path.Combine(root, "numbers.txt"), committed)
            Commands.Stage(repo, "numbers.txt")
            let author = Signature("GitKay Tests", "gitkay@example.com", DateTimeOffset.Now)
            repo.Commit("init", author, author) |> ignore
            action root
        finally
            try Directory.Delete(root, true) with _ -> ()

    /// The history window showing the uncommitted changes of a repository.
    let private showing (gitDir: string) =
        let projection = MainProjection(RepositoryPath = gitDir)
        let model0, _ = App.init [||]
        let changes =
            match Flow.run (GitService.environment gitDir) (GitService.fetchWorkingTreeChanges 3) |> Exit.toResult with
            | Ok changes -> changes
            | Error error -> failwith (GitError.describe error)
        projection.Update { model0 with Selection = App.WorkingTreeSelected; WorkingTree = changes.Entries; WorkingTreeChanges = Some changes }
        projection

    let private lineRows (projection: MainProjection) =
        projection.SelectedDiffRows |> Seq.choose (function :? DiffLineProjection as line -> Some line | _ -> None) |> List.ofSeq

    let private changedContent (gitDir: string) (section: WorkingTree.Section) (path: string) =
        match Flow.run (GitService.environment gitDir) (GitService.fetchRawFileDiff section path) |> Exit.toResult with
        | Error error -> failwith (GitError.describe error)
        | Ok raw ->
            WorkingTree.parsePatch raw
            |> List.collect _.Hunks
            |> List.collect _.Lines
            |> List.filter (fun line -> line.Type <> Models.Context)
            |> List.map _.Content

    [<Fact>]
    let ``ignoring whitespace drops the lines that differ only in spacing`` () =
        withRepository (numbered [ "one"; "two"; "three" ]) (fun root ->
            // Two changes in one commit: a real edit, and a line that only gained indentation.
            File.WriteAllText(Path.Combine(root, "numbers.txt"), numbered [ "one"; "    two"; "THREE" ])
            use repo = new Repository(root)
            Commands.Stage(repo, "numbers.txt")
            let author = Signature("GitKay Tests", "gitkay@example.com", DateTimeOffset.Now)
            let commit = repo.Commit("edit", author, author)

            let changedLines ignoreWhitespace =
                match Flow.run (GitService.environment root) (GitService.fetchDiffWith ignoreWhitespace 3 commit.Sha) |> Exit.toResult with
                | Error error -> failwith (GitError.describe error)
                | Ok files ->
                    files
                    |> List.collect _.Hunks
                    |> List.collect _.Lines
                    |> List.filter (fun line -> line.Type <> Models.Context)
                    |> List.map (fun line -> line.Content.Trim())

            let normally = changedLines false
            test <@ normally |> List.contains "two" && normally |> List.contains "THREE" @>

            // With whitespace ignored the indentation-only line is gone; the real change stays.
            let ignoring = changedLines true
            test <@ ignoring |> List.contains "THREE" @>
            test <@ ignoring |> List.filter (fun line -> line = "two") = [] @>)

    [<Fact>]
    let ``staging the hunk at the cursor from the history window stages only that hunk`` () =
        withRepository (numbered [ for n in 1..15 -> string n ]) (fun root ->
            let gitDir = Path.Combine(root, ".git")
            File.WriteAllText(Path.Combine(root, "numbers.txt"), numbered [ for n in 1..15 -> if n = 2 then "TWO" elif n = 14 then "FOURTEEN" else string n ])

            let projection = showing gitDir
            let file = projection.SelectedDiffFiles |> Seq.find (fun file -> file.Key.Section = "Unstaged")
            projection.SelectedDiffFile <- file
            // The cursor sits on the second change; with no selection that means its whole hunk.
            let cursor = lineRows projection |> List.find (fun line -> line.Content = "FOURTEEN")
            let lines = projection.LinesOf(file, [ cursor :> IDiffRowProjection ], true)
            test <@ lines |> Seq.map _.Content |> List.ofSeq = [ "14"; "FOURTEEN" ] @>

            match Flow.run (GitService.environment gitDir) (GitService.applyLines GitService.StageInIndex "numbers.txt" (List.ofSeq lines)) |> Exit.toResult with
            | Error error -> failwith (GitError.describe error)
            | Ok () ->
                test <@ changedContent gitDir WorkingTree.Staged "numbers.txt" = [ "14"; "FOURTEEN" ] @>
                test <@ changedContent gitDir WorkingTree.Unstaged "numbers.txt" = [ "2"; "TWO" ] @>)

    [<Fact>]
    let ``the cursor still maps onto the right hunk after context is expanded`` () =
        withRepository (numbered [ for n in 1..40 -> string n ]) (fun root ->
            let gitDir = Path.Combine(root, ".git")
            File.WriteAllText(Path.Combine(root, "numbers.txt"), numbered [ for n in 1..40 -> if n = 5 then "FIVE" elif n = 35 then "THIRTY-FIVE" else string n ])

            let projection = showing gitDir
            let file = projection.SelectedDiffFiles |> Seq.find (fun file -> file.Key.Section = "Unstaged")
            projection.SelectedDiffFile <- file
            let before = lineRows projection |> List.length

            // Reveal the whole file, which adds context rows between the two hunks.
            projection.ToggleDiffFileContextCommand.Execute file
            let deadline = DateTime.UtcNow.AddSeconds 10.0
            while (lineRows projection |> List.length) = before && DateTime.UtcNow < deadline do
                Threading.Thread.Sleep 50
            test <@ (lineRows projection |> List.length) > before @>

            let cursor = lineRows projection |> List.find (fun line -> line.Content = "THIRTY-FIVE")
            let lines = projection.LinesOf(file, [ cursor :> IDiffRowProjection ], true)
            test <@ lines |> Seq.map _.Content |> List.ofSeq = [ "35"; "THIRTY-FIVE" ] @>

            match Flow.run (GitService.environment gitDir) (GitService.applyLines GitService.StageInIndex "numbers.txt" (List.ofSeq lines)) |> Exit.toResult with
            | Error error -> failwith (GitError.describe error)
            | Ok () ->
                test <@ changedContent gitDir WorkingTree.Staged "numbers.txt" = [ "35"; "THIRTY-FIVE" ] @>
                test <@ changedContent gitDir WorkingTree.Unstaged "numbers.txt" = [ "5"; "FIVE" ] @>)

module CommitWindowTests =

    [<Fact>]
    let ``a commit draft is kept per repository until it is committed`` () =
        let state = UiState.empty |> UiState.withCommitDraft "/repo/a" "Half-written message\n\nbody" |> UiState.withCommitDraft "/repo/b" "Other"
        test <@ UiState.commitDraft "/repo/a" state = Some "Half-written message\n\nbody" @>
        test <@ UiState.commitDraft "/repo/b" state = Some "Other" @>
        // Committing clears it; blank drafts aren't kept.
        let cleared = state |> UiState.withCommitDraft "/repo/a" ""
        test <@ UiState.commitDraft "/repo/a" cleared = None && UiState.commitDraft "/repo/b" cleared = Some "Other" @>
        match GitKay.Serialization.UiStateJson.encode state |> GitKay.Serialization.UiStateJson.decode with
        | Ok read -> test <@ UiState.commitDraft "/repo/a" read = Some "Half-written message\n\nbody" @>
        | Error message -> failwith message

    [<Fact>]
    let ``gitkay gui, gitkay commit and gitkay-gui open the commit window`` () =
        test <@ GitStartup.launchMode "/usr/bin/gitkay" [| "gui"; "--log"; "x" |] = (GitStartup.Commit, [| "--log"; "x" |]) @>
        test <@ fst (GitStartup.launchMode "/usr/bin/gitkay" [| "commit" |]) = GitStartup.Commit @>
        test <@ fst (GitStartup.launchMode @"C:\GitKay\gitkay-gui.exe" [||]) = GitStartup.Commit @>
        test <@ GitStartup.launchMode "/usr/bin/gitkay" [| "main"; "--all" |] = (GitStartup.History, [| "main"; "--all" |]) @>

    let private diff path : Models.FileDiff = { OldPath = path; NewPath = path; Hunks = []; NewLineCount = None }

    let private changes (unstaged: string list) (staged: string list) (untracked: string list) : GitService.WorkingTreeChanges =
        { Entries = []
          Staged = staged |> List.map diff
          Unstaged = unstaged |> List.map diff
          Untracked = untracked |> List.map (fun path -> { diff path with OldPath = "/dev/null" }) }

    let private model0 () = fst (CommitWindow.init (GitService.environment "") "" 3)

    [<Fact>]
    let ``reselect keeps the file, then its position, then the other list`` () =
        let before = changes [ "a"; "b"; "c" ] [] [ "n" ]
        test <@ CommitWindow.reselect None None before = Some(CommitWindow.UnstagedList, "a") @>
        test <@ CommitWindow.reselect (Some before) (Some(CommitWindow.UnstagedList, "b")) (changes [ "a"; "b"; "c" ] [] []) = Some(CommitWindow.UnstagedList, "b") @>
        // "b" was staged: the file now at its position is shown.
        test <@ CommitWindow.reselect (Some before) (Some(CommitWindow.UnstagedList, "b")) (changes [ "a"; "c" ] [ "b" ] [ "n" ]) = Some(CommitWindow.UnstagedList, "c") @>
        test <@ CommitWindow.reselect (Some before) (Some(CommitWindow.UnstagedList, "n")) (changes [ "a" ] [] []) = Some(CommitWindow.UnstagedList, "a") @>
        test <@ CommitWindow.reselect (Some(changes [ "a" ] [] [])) (Some(CommitWindow.UnstagedList, "a")) (changes [] [ "a" ] []) = Some(CommitWindow.StagedList, "a") @>

    [<Fact>]
    let ``commit needs a subject and staged changes unless amending`` () =
        let loaded, _ = CommitWindow.update (CommitWindow.ChangesLoaded(Ok(changes [ "a" ] [] []))) (model0 ())
        test <@ CommitWindow.commitBlocker loaded = Some "Write a commit message" @>
        let written = { loaded with Message = "Fix it" }
        test <@ CommitWindow.commitBlocker written = Some "Stage changes to commit" @>
        test <@ CommitWindow.commitBlocker { written with Amend = true } = None @>
        let staged, _ = CommitWindow.update (CommitWindow.ChangesLoaded(Ok(changes [] [ "a" ] []))) written
        test <@ CommitWindow.commitBlocker staged = None @>
        let blocked, _ = CommitWindow.update CommitWindow.Commit loaded
        test <@ blocked.Status = "Write a commit message" && blocked.Busy.IsNone @>

    [<Fact>]
    let ``messages get a blank line before the body and lose trailing spaces`` () =
        test <@ CommitWindow.normalizeMessage "Subject  \nBody\n\n" = "Subject\n\nBody\n" @>
        test <@ CommitWindow.normalizeMessage "Subject\r\n\r\nBody" = "Subject\n\nBody\n" @>
        test <@ CommitWindow.subject "\n" = "" @>

    [<Fact>]
    let ``amend fills an empty draft and turning it off restores it`` () =
        let amending, _ = CommitWindow.update (CommitWindow.SetAmend true) (model0 ())
        let loaded, _ = CommitWindow.update (CommitWindow.AmendMessageLoaded "Last subject\n\n") amending
        test <@ loaded.Message = "Last subject\n" && loaded.Amend @>
        let off, _ = CommitWindow.update (CommitWindow.SetAmend false) loaded
        test <@ off.Message = "" && not off.Amend @>
        let edited, _ = CommitWindow.update (CommitWindow.SetMessage "Rewritten") loaded
        let keptOff, _ = CommitWindow.update (CommitWindow.SetAmend false) edited
        test <@ keptOff.Message = "Rewritten" @>
        let draft = { model0 () with Message = "My draft" }
        let amendingDraft, _ = CommitWindow.update (CommitWindow.SetAmend true) draft
        let notReplaced, _ = CommitWindow.update (CommitWindow.AmendMessageLoaded "Last subject") amendingDraft
        test <@ notReplaced.Message = "My draft" @>

    [<Fact>]
    let ``a discard offers an undo until it is taken`` () =
        let backup : Trash.Backup =
            { Directory = "/tmp/backup"; Entries = []; Description = "2 lines in a.txt"; CreatedAt = DateTimeOffset.UnixEpoch }
        let discarded, _ = CommitWindow.update (CommitWindow.DiscardSucceeded backup) (model0 ())
        test <@ discarded.LastDiscard = Some backup && discarded.Status = "Discarded 2 lines in a.txt · Ctrl+Z to undo" @>

        // The rescan that follows leaves the offer on screen.
        let rescanned, _ = CommitWindow.update (CommitWindow.ChangesLoaded(Ok(changes [ "a" ] [] []))) discarded
        test <@ rescanned.Status = discarded.Status && rescanned.LastDiscard = Some backup @>

        let undoing, _ = CommitWindow.update CommitWindow.UndoDiscard rescanned
        test <@ undoing.LastDiscard = None && undoing.Busy = Some "Undoing" @>
        let nothing, _ = CommitWindow.update CommitWindow.UndoDiscard undoing
        test <@ nothing.Status = "Nothing to undo" @>

    [<Fact>]
    let ``a successful commit clears the message and counts it; a failure keeps the output`` () =
        let written = { model0 () with Message = "Fix it"; Amend = true; Busy = Some "Committing" }
        let committed, _ = CommitWindow.update (CommitWindow.OperationSucceeded("Committing", true)) written
        test <@ committed.Message = "" && not committed.Amend && committed.Commits = 1 && committed.Busy.IsNone @>
        let failed, _ = CommitWindow.update (CommitWindow.OperationFailed("Committing", GitError.OperationFailed("commit", "hook said no"))) written
        test <@ failed.Message = "Fix it" && failed.FailureOutput = Some "commit failed: hook said no" @>
        let rescanned, _ = CommitWindow.update (CommitWindow.ChangesLoaded(Ok(changes [ "a" ] [] []))) failed
        test <@ rescanned.Status = "Committing failed" @>

module PaletteTests =

    let private commitOf hash subject (refs: Models.CommitRef list) : Models.Commit =
        { Hash = hash; AuthorName = "A"; AuthorEmail = "a@x"; Timestamp = 1L; Parents = []; Subject = subject; Message = subject; Refs = refs }

    [<Fact>]
    let ``fuzzy match should prefer word starts and consecutive letters`` () =
        test <@ GitKay.Kit.Fuzzy.score "gcf" "Go to changed file…" >= 0 @>
        test <@ GitKay.Kit.Fuzzy.score "xyz" "Settings" = -1 @>
        test <@ GitKay.Kit.Fuzzy.score "side" "View: side-by-side" > GitKay.Kit.Fuzzy.score "side" "Show stashes inside" @>

    [<Fact>]
    let ``palette should rank commands, switch modes by prefix and run the selection`` () =
        let projection = MainProjection()
        let model0, _ = App.init [||]
        let a = commitOf (String.replicate 40 "a") "Add fonts" [ RefHelpers.branchRef "main" ]
        let b = commitOf (String.replicate 40 "b") "Fix parser" [ RefHelpers.tagRef "v1.0" ]
        projection.Update { model0 with Commits = Graph.calculateLanes [ a; b ]; Selection = App.CommitSelected a.Hash; ShowBranchRefs = true }
        let selections = ResizeArray<string>()
        projection.SetDispatch (function App.Msg.SelectCommit (hash, _) -> selections.Add hash | _ -> ())

        projection.OpenPalette PaletteMode.Commands
        projection.PaletteQuery <- "side by"
        test <@ projection.PaletteItems.[0].Title = "View: side-by-side" @>
        projection.RunPaletteItem()
        test <@ projection.IsSideBySideDiffMode && not projection.IsPaletteOpen @>

        projection.OpenPalette PaletteMode.Commands
        projection.PaletteQuery <- "@v1"
        test <@ projection.PaletteMode = PaletteMode.Refs && projection.PaletteItems.[0].Title = "v1.0" @>
        projection.RunPaletteItem()
        test <@ List.ofSeq selections = [ b.Hash ] @>

    [<Fact>]
    let ``back and forward should retrace visited commits`` () =
        let projection = MainProjection()
        let model0, _ = App.init [||]
        let commits = [ for n in 1 .. 3 -> commitOf (String.replicate 40 (string n)) $"c{n}" [] ]
        let model = { model0 with Commits = Graph.calculateLanes commits }
        let selections = ResizeArray<string>()
        let visit (hash: string) = projection.Update { model with Selection = App.CommitSelected hash }
        visit commits.[0].Hash
        visit commits.[1].Hash
        visit commits.[2].Hash
        projection.SetDispatch (function App.Msg.SelectCommit (hash, _) -> selections.Add hash | _ -> ())
        test <@ projection.CanGoBack && not projection.CanGoForward @>
        projection.GoBackCommand.Execute null
        test <@ List.ofSeq selections = [ commits.[1].Hash ] @>
        visit commits.[1].Hash
        test <@ projection.CanGoForward @>
        projection.GoForwardCommand.Execute null
        test <@ List.ofSeq selections = [ commits.[1].Hash; commits.[2].Hash ] @>

    [<Fact>]
    let ``returning to a commit should restore its collapsed files`` () =
        let commitOf hash : Models.Commit = { Hash = hash; AuthorName = "A"; AuthorEmail = "a@x"; Timestamp = 1L; Parents = []; Subject = hash; Message = hash; Refs = [] }
        let a = commitOf (String.replicate 40 "a")
        let b = commitOf (String.replicate 40 "b")
        let summary path : GitService.DiffFileSummary = { OldPath = path; NewPath = path; DisplayPath = path }
        let fileOf path : Models.FileDiff =
            { OldPath = path; NewPath = path; NewLineCount = Some 1
              Hunks = [ { Header = "@@ -1 +1 @@"; Lines = [ { Type = Models.Added; Content = "x"; OldLineNo = None; NewLineNo = Some 1 } ] } ] }
        let model0, _ = App.init [||]
        let modelFor (commit: Models.Commit) =
            { model0 with
                Commits = Graph.calculateLanes [ a; b ]
                Selection = App.CommitSelected commit.Hash
                SelectedDiffHash = Some commit.Hash
                SelectedDiffFiles = Some [ summary "one.fs"; summary "two.fs" ]
                SelectedDiff = Some [ fileOf "one.fs"; fileOf "two.fs" ] }
        let projection = MainProjection()
        projection.Update (modelFor a)
        let one () = projection.SelectedDiffFiles |> Seq.find (fun f -> f.Key.NewPath = "one.fs")
        projection.ToggleDiffFileCollapsedCommand.Execute (one ())
        test <@ (one ()).IsCollapsed @>

        projection.Update { (modelFor b) with SelectedDiffHash = None; SelectedDiffFiles = None; SelectedDiff = None }
        projection.Update (modelFor b)
        test <@ not (one ()).IsCollapsed @>

        projection.Update { (modelFor a) with SelectedDiffHash = None; SelectedDiffFiles = None; SelectedDiff = None }
        projection.Update (modelFor a)
        test <@ (one ()).IsCollapsed @>


module StartupSelectionTests =

    [<Fact>]
    let ``positional short hash should select rather than limit history`` () =
        match GitStartup.parseStartupOptions [| "d17398a" |] with
        | Ok options ->
            test <@ options.SelectedCommitHash = Some "d17398a" && options.StartupTargets = [] @>
        | Error e -> failwith (GitStartup.describeError e)
        match GitStartup.parseStartupOptions [| "main" |] with
        | Ok options -> test <@ options.SelectedCommitHash = None && options.StartupTargets = [ GitStartup.StartupTarget.Revision "main" ] @>
        | Error e -> failwith (GitStartup.describeError e)

    let private commitOf hash : Models.Commit =
        { Hash = hash; AuthorName = "A"; AuthorEmail = "a@x"; Timestamp = 1L; Parents = []; Subject = hash; Message = hash; Refs = [] }

    [<Fact>]
    let ``unknown sha should report and fall back to the normal view`` () =
        let model0, _ = App.init [| "abcdef1" |]
        test <@ model0.StartupSelection = App.ResolvingSelection "abcdef1" @>
        let notFound, _ = App.update (App.Msg.SelectionRevisionResolved("abcdef1", Error (GitError.RevisionNotFound "abcdef1"))) model0
        let a = commitOf (String.replicate 40 "a")
        let loaded, _ = App.update (App.Msg.HistoryLoaded(true, Ok [ a ])) notFound
        test <@ loaded.Status = "No commit found for 'abcdef1'" @>
        test <@ loaded.SelectedCommitHash = Some a.Hash && loaded.Commits.Length = 1 @>

    [<Fact>]
    let ``resolved sha should be selected when history arrives, even after a partial first page`` () =
        let model0, _ = App.init [| "bbbbbbb" |]
        let a = commitOf (String.replicate 40 "a")
        let b = commitOf (String.replicate 40 "b")
        let found, _ = App.update (App.Msg.SelectionRevisionResolved("bbbbbbb", Ok b.Hash)) model0
        let partial, _ = App.update (App.Msg.HistoryLoaded(false, Ok (List.replicate 1000 a))) found
        test <@ partial.SelectedCommitHash = Some a.Hash && partial.StartupSelection = App.SelectionFound b.Hash @>
        let full, _ = App.update (App.Msg.HistoryLoaded(true, Ok [ a; b ])) partial
        test <@ full.SelectedCommitHash = Some b.Hash && full.StartupSelection = App.NoStartupSelection @>


module CliTests =

    let private parse args = match GitStartup.parseStartupOptions args with Ok o -> o | Error e -> failwith (GitStartup.describeError e)

    [<Fact>]
    let ``ranges, excludes and several tips should become targets`` () =
        test <@ (parse [| "main"; "topic" |]).StartupTargets = [ GitStartup.Revision "main"; GitStartup.Revision "topic" ] @>
        test <@ (parse [| "v1..main" |]).StartupTargets = [ GitStartup.Revision "main"; GitStartup.Exclude "v1" ] @>
        test <@ (parse [| "a...b" |]).StartupTargets = [ GitStartup.Revision "a"; GitStartup.Revision "b"; GitStartup.ExcludeMergeBase("a", "b") ] @>
        test <@ (parse [| "main"; "^old" |]).StartupTargets = [ GitStartup.Revision "main"; GitStartup.Exclude "old" ] @>
        test <@ (parse [| "..main" |]).StartupTargets = [ GitStartup.Revision "main"; GitStartup.Exclude "HEAD" ] @>
        test <@ (parse [| "abc1234"; "def5678" |]).SelectedCommitHash = None @>

    [<Fact>]
    let ``paths after double dash should limit history`` () =
        let options = parse [| "main"; "--"; "src/"; "README.md" |]
        test <@ options.StartupTargets = [ GitStartup.Revision "main"; GitStartup.Path "src/"; GitStartup.Path "README.md" ] @>

    [<Fact>]
    let ``git log filters should become search prefixes and limit the list`` () =
        let options = parse [| "--author=Jane Doe"; "--grep"; "fix"; "--since=2.weeks.ago"; "--until"; "2026-01-01"; "-Sneedle" |]
        test <@ options.SearchQuery = "author:\"Jane Doe\" message:fix after:2.weeks.ago before:2026-01-01 diff:needle" @>
        test <@ options.ShowOnlyMatches && not options.SearchUseRegex @>
        let regex = parse [| "-G"; "find\\w+" |]
        test <@ regex.SearchQuery = "diff:find\\w+" && regex.SearchUseRegex @>

    [<Fact>]
    let ``relative dates should parse against a given now`` () =
        let now = DateTimeOffset(2026, 9, 13, 12, 0, 0, TimeSpan.Zero)
        let expect days = Some(now.AddDays(-days).ToUnixTimeSeconds())
        let parsed text = GitSearch.tryParseDate now text
        let twoWeeks, threeDays, yesterday, nonsense = parsed "2.weeks.ago", parsed "3 days ago", parsed "yesterday", parsed "nonsense"
        let e14, e3, e1 = expect 14.0, expect 3.0, expect 1.0
        test <@ twoWeeks = e14 && threeDays = e3 && yesterday = e1 && nonsense = None @>

    [<Fact>]
    let ``history should honour ranges, excludes and paths`` () =
        let root = Path.Combine(Path.GetTempPath(), "gitkay-cli-" + Guid.NewGuid().ToString("N"))
        Repository.Init root |> ignore
        try
            use repo = new Repository(root)
            let signature = Signature("T", "t@x", DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero))
            let commit (file: string) (content: string) =
                File.WriteAllText(Path.Combine(root, file), content)
                Commands.Stage(repo, file)
                repo.Commit(file + content, signature, signature)
            let c1 = commit "a.txt" "1"
            let c2 = commit "b.txt" "2"
            let c3 = commit "a.txt" "3"
            repo.ApplyTag("v1", c1.Sha) |> ignore
            let history targets =
                match Flow.run (GitService.environment root) (GitService.fetchHistory None false targets) |> Exit.toResult with
                | Ok commits -> commits |> List.map (fun c -> c.Hash)
                | Error e -> failwith (GitError.describe e)
            test <@ history (GitStartup.revisionTargets "v1..HEAD") = [ c3.Sha; c2.Sha ] @>
            test <@ history [ GitStartup.Revision "HEAD"; GitStartup.Exclude c2.Sha ] = [ c3.Sha ] @>
            test <@ history [ GitStartup.Path "a.txt" ] = [ c3.Sha; c1.Sha ] @>
        finally
            try Directory.Delete(root, true) with _ -> ()

    [<Fact>]
    let ``a bare file argument should filter history like gitk, relative to the launch directory`` () =
        let root = Path.Combine(Path.GetTempPath(), "gitkay-patharg-" + Guid.NewGuid().ToString("N"))
        Repository.Init root |> ignore
        try
            use repo = new Repository(root)
            let signature = Signature("T", "t@x", DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero))
            let commit (file: string) (content: string) =
                let full = Path.Combine(root, file)
                Directory.CreateDirectory(Path.GetDirectoryName full) |> ignore
                File.WriteAllText(full, content)
                Commands.Stage(repo, file)
                repo.Commit(file + content, signature, signature)
            let c1 = commit "src/a.txt" "1"
            commit "b.txt" "2" |> ignore
            let c3 = commit "src/a.txt" "3"

            let fromSrc = GitService.resolvePathArguments root (Path.Combine(root, "src")) [ GitStartup.Revision "a.txt"; GitStartup.Revision "HEAD" ]
            test <@ fromSrc = [ GitStartup.Path "src/a.txt"; GitStartup.Revision "HEAD" ] @>
            let windowsStyle = GitService.resolvePathArguments root root [ GitStartup.Path "src\\a.txt" ]
            test <@ windowsStyle = [ GitStartup.Path "src/a.txt" ] || windowsStyle = [ GitStartup.Path "src\\a.txt" ] @>
            let missing = GitService.resolvePathArguments root (Path.Combine(root, "src")) [ GitStartup.Path "gone/old.txt" ]
            test <@ missing = [ GitStartup.Path "gone/old.txt" ] @>

            let history targets =
                match Flow.run (GitService.environment root) (GitService.fetchHistory (Some 1000) false targets) |> Exit.toResult with
                | Ok commits -> commits |> List.map (fun c -> c.Hash)
                | Error e -> failwith (GitError.describe e)
            test <@ history fromSrc = [ c3.Sha; c1.Sha ] @>
            test <@ history [ GitStartup.Path "src\\a.txt" ] = [ c3.Sha; c1.Sha ] @>
        finally
            try Directory.Delete(root, true) with _ -> ()

    [<Fact>]
    let ``default history should follow HEAD like gitk, and --all should include sibling branches`` () =
        let root = Path.Combine(Path.GetTempPath(), "gitkay-head-" + Guid.NewGuid().ToString("N"))
        Repository.Init root |> ignore
        try
            use repo = new Repository(root)
            let signature = Signature("T", "t@x", DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero))
            let commit (file: string) (content: string) =
                File.WriteAllText(Path.Combine(root, file), content)
                Commands.Stage(repo, file)
                repo.Commit(file + content, signature, signature)
            let trunk = commit "a.txt" "1"
            let sibling = repo.CreateBranch("sibling")
            Commands.Checkout(repo, sibling) |> ignore
            let siblingCommit = commit "b.txt" "2"
            Commands.Checkout(repo, repo.Branches.["master"] |> Option.ofObj |> Option.defaultWith (fun () -> repo.Branches.["main"])) |> ignore
            let head = commit "c.txt" "3"

            let history targets =
                match Flow.run (GitService.environment root) (GitService.fetchHistory None false targets) |> Exit.toResult with
                | Ok commits -> commits |> List.map (fun c -> c.Hash) |> Set.ofList
                | Error e -> failwith (GitError.describe e)
            test <@ history [] = Set.ofList [ head.Sha; trunk.Sha ] @>
            test <@ history [ GitStartup.All ] = Set.ofList [ head.Sha; trunk.Sha; siblingCommit.Sha ] @>
        finally
            try Directory.Delete(root, true) with _ -> ()

    [<Fact>]
    let ``filtering history to a file should keep the branch scope and replace earlier paths`` () =
        let projection = MainProjection()
        let messages = Collections.Generic.List<App.Msg>()
        projection.SetDispatch(fun msg -> messages.Add msg)
        let model, _ = App.init [||]
        projection.Update { model with StartupTargets = [ GitStartup.All; GitStartup.Path "old.txt" ] }
        test <@ projection.IsAllBranches && projection.HistoryPathFilter = "old.txt" @>
        projection.FilterHistoryToFile(FileTarget("src/a.fs", "src/a.fs", "src/a.fs", null))
        test <@ List.ofSeq messages = [ App.Msg.SetHistoryTargets [ GitStartup.All; GitStartup.Path "src/a.fs" ] ] @>
        messages.Clear()
        projection.IsAllBranches <- false
        test <@ List.ofSeq messages = [ App.Msg.SetHistoryTargets [ GitStartup.Path "old.txt" ] ] @>

    [<Fact>]
    let ``a path-limited first page should not be treated as the complete history`` () =
        let model0, _ = App.init [||]
        let model = { model0 with StartupTargets = [ GitStartup.Path "src/" ] }
        let next, _ = App.update (App.Msg.HistoryLoaded(false, Ok [])) model
        test <@ not next.HasFullHistory @>
        let unfiltered, _ = App.update (App.Msg.HistoryLoaded(false, Ok [])) model0
        test <@ unfiltered.HasFullHistory @>

    [<Fact>]
    let ``listCommitFiles and loadWholeFile should read every file at a commit`` () =
        let root = Path.Combine(Path.GetTempPath(), "gitkay-whole-" + Guid.NewGuid().ToString("N"))
        Repository.Init root |> ignore
        try
            use repo = new Repository(root)
            let signature = Signature("T", "t@x", DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero))
            let commit (file: string) (content: string) =
                let full = Path.Combine(root, file)
                Directory.CreateDirectory(Path.GetDirectoryName full) |> ignore
                File.WriteAllText(full, content)
                Commands.Stage(repo, file)
                repo.Commit(file, signature, signature)
            commit "src/a.txt" "one\ntwo\nthree\n" |> ignore
            let second = commit "b.txt" "b\n"
            let unwrap = function Ok value -> value | Error e -> failwith (GitError.describe e)

            let files = GitService.listCommitFiles root second.Sha |> unwrap |> List.sort
            test <@ files = [ "b.txt"; "src/a.txt" ] @>

            // Unchanged in the second commit: every line as context.
            let unchanged = GitService.loadWholeFile root second.Sha "src/a.txt" "src/a.txt" |> unwrap
            let lines = unchanged.Hunks |> List.collect _.Lines
            test <@ lines |> List.map _.Content = [ "one"; "two"; "three" ] && lines |> List.forall (fun line -> line.Type = Models.Context) @>
            test <@ (lines |> List.last).NewLineNo = Some 3 @>

            // Changed (added) in the second commit: the diff itself.
            let added = GitService.loadWholeFile root second.Sha "/dev/null" "b.txt" |> unwrap
            test <@ added.Hunks |> List.collect _.Lines |> List.map _.Type = [ Models.Added ] @>
            test <@ GitService.workingDirectory root = Path.TrimEndingDirectorySeparator(Path.GetFullPath root) @>

            // Embedded assets are revision-correct on both sides of a rendered diff.
            let writeImage value =
                File.WriteAllBytes(Path.Combine(root, "image.png"), [| value |])
                Commands.Stage(repo, "image.png")
                repo.Commit($"image {value}", signature, signature)
            let imageOld = writeImage 1uy
            let imageNew = writeImage 2uy
            let oldBytes = GitService.loadCommitSideFileBytes root imageNew.Sha true "image.png" |> unwrap
            let newBytes = GitService.loadCommitSideFileBytes root imageNew.Sha false "image.png" |> unwrap
            test <@ imageOld.Sha <> imageNew.Sha && oldBytes = [| 1uy |] && newBytes = [| 2uy |] @>
        finally
            try Directory.Delete(root, true) with _ -> ()

    [<Fact>]
    let ``too-short revisions should fail cleanly rather than crash the history load`` () =
        let root = Path.Combine(Path.GetTempPath(), "gitkay-short-" + Guid.NewGuid().ToString("N"))
        Repository.Init root |> ignore
        try
            use repo = new Repository(root)
            let signature = Signature("T", "t@x", DateTimeOffset.Now)
            File.WriteAllText(Path.Combine(root, "a.txt"), "1")
            Commands.Stage(repo, "a.txt")
            repo.Commit("one", signature, signature) |> ignore
            match Flow.run (GitService.environment root) (GitService.fetchHistory None false [ GitStartup.Revision "ab" ]) with
            | Exit.Failure(Cause.Fail _) -> ()
            | other -> failwithf "expected a clean failure, got %A" other
        finally
            try Directory.Delete(root, true) with _ -> ()

    [<Fact>]
    let ``unresolvable command line revisions should fall back to the normal history`` () =
        let model0, _ = App.init [| "ab"; "--"; "src/" |]
        let next, _ = App.update (App.Msg.HistoryLoaded(false, Error (GitError.InvalidRevision "ab"))) model0
        test <@ next.StartupTargets = [ GitStartup.Path "src/" ] @>
        test <@ next.Status = "No commit found for 'ab'" @>

module SettingsSerializationTests =
    open GitKay.Serialization

    let private decoded json =
        match SettingsJson.decode json with
        | Ok settings -> settings
        | Error message -> failwith message

    [<Fact>]
    let ``settings round-trip every field`` () =
        let settings =
            { ShowBranchRefs = true; ShowStashes = true; DiffContextLines = 7; DiffLayout = DiffLayout.SideBySide
              CommitRowFontFamily = "Inter"; CommitRowMonoFontFamily = "Iosevka"; CommitRowTextFontSize = 14.5
              CommitRowMetaFontSize = 12.0; CommitRowBadgeFontSize = 10.0; SearchDebounceSeconds = 0.25
              PreviewByDefault = true; LoadRemoteMarkdownImages = true; Theme = DarkTheme
              PaneGap = 5.0; HoverFocusesPane = false; PaneDimUnfocused = false; PaneFocusHighlight = true
              PaneFocusEffect = PaneShadow; PaneEffectColor = TealEffectColor; PaneEffectIntensity = QuarterIntensity
              PaneBorder = true; PaneBorderStyle = CustomBorder; PaneBorderColor = PinkEffectColor
              PaneBorderThickness = 3.0; SplitterLinesHidden = true
              ChromeBackground = TintedChrome; ChromeColor = SandEffectColor }
        test <@ settings |> SettingsJson.encode |> SettingsJson.decode = Ok settings @>

    [<Fact>]
    let ``pane settings clamp the gap and thickness and fall back to the default effect`` () =
        let read = decoded """{"PaneGap":40,"PaneBorderThickness":40,"PaneFocusEffect":"sparkle","PaneEffectColor":"chartreuse"}"""
        test <@ read.PaneGap = 8.0 && read.PaneBorderThickness = 4.0 && read.PaneFocusEffect = Settings.defaults.PaneFocusEffect && read.PaneEffectColor = Settings.defaults.PaneEffectColor @>
        test <@ (decoded """{"PaneGap":0,"PaneFocusEffect":"none"}""") = { Settings.defaults with PaneGap = 0.0; PaneFocusEffect = NoPaneEffect } @>

    [<Fact>]
    let ``json previews as indented text and unreadable json stays as it is`` () =
        test <@ Markdown.previewKind "config.json" = FormattedPreview JsonFormat @>
        // The extra markdown spellings are previewed too.
        test <@ Markdown.previewKind "notes.mdown" = MarkdownPreview && Markdown.previewKind "notes.mkd" = MarkdownPreview @>
        test <@ Markdown.previewKind "main.fs" = SourceOnly @>

        // One long line is exactly the file a preview is for.
        let formatted = Markdown.formatForPreview JsonFormat """{"a":1,"b":[2,3]}"""
        match formatted with
        | Ok text ->
            test <@ text.Split('\n').Length > 1 @>
            test <@ text.Contains "  \"a\": 1" @>
        | Error message -> failwith message

        // A file that will not parse is reported rather than replaced with nothing.
        test <@ Markdown.formatForPreview JsonFormat "{ not json" |> Result.isError @>
        // A format with no formatter is no longer expressible: PreviewFormat is closed over the ones that exist.
        test <@ Markdown.formatForPreview JsonFormat "" |> Result.isError @>

    [<Fact>]
    let ``forgetting per-file preview choices clears only those keys`` () =
        let state =
            UiState.empty
            |> UiState.withViewPreferences
                [ "markdown-preview|/repo|README.md", "source"
                  "markdown-preview|/repo|docs/spec.md", "rendered"
                  "FileTree", "true" ]
        let cleared = state |> UiState.withoutViewPreferences "markdown-preview|"
        // The count is what the command reports before forgetting them.
        test <@ state |> UiState.countViewPreferences "markdown-preview|" = 2 @>
        test <@ Map.toList cleared.ViewPreferences = [ "FileTree", "true" ] @>
        test <@ cleared |> UiState.countViewPreferences "markdown-preview|" = 0 @>
        // An empty prefix would forget everything; it forgets nothing instead.
        test <@ (state |> UiState.withoutViewPreferences "") = state @>

    [<Fact>]
    let ``preview by default still reads the settings file written when it was Markdown only`` () =
        // The on-disk name predates previewing JSON and XML; a file saved by an older build must keep its choice.
        let read = decoded """{"RenderMarkdownByDefault":false}"""
        test <@ read.PreviewByDefault = false @>
        test <@ (decoded """{"RenderMarkdownByDefault":true}""").PreviewByDefault @>
        // And it is still written under that name, so an older build reads what this one saves.
        let json = { Settings.defaults with PreviewByDefault = false } |> SettingsJson.encode
        test <@ json.Contains "\"RenderMarkdownByDefault\":false" @>

    [<Fact>]
    let ``an unknown chrome background or colour falls back to the default`` () =
        let read = decoded """{"ChromeBackground":"hologram","ChromeColor":"chartreuse"}"""
        test <@ read.ChromeBackground = Settings.defaults.ChromeBackground && read.ChromeColor = Settings.defaults.ChromeColor @>
        test <@ (decoded """{"ChromeBackground":"transparent","ChromeColor":"lime"}""") = { Settings.defaults with ChromeBackground = TransparentChrome; ChromeColor = LimeEffectColor } @>

    [<Fact>]
    let ``settings missing from the file take their defaults`` () =
        let read = decoded """{"ShowBranchRefs":true,"CommitRowTextFontSize":15}"""
        test <@ read = { Settings.defaults with ShowBranchRefs = true; CommitRowTextFontSize = 15.0 } @>

    [<Fact>]
    let ``settings written by GitKay 0.3.0 still load`` () =
        let json = """{"ShowBranchRefs":true,"ShowStashes":false,"DiffContextLines":3,"DiffPresentationModeKey":"diff","CommitRowFontFamily":"Courier New","CommitRowMonoFontFamily":"Inconsolata","CommitRowTextFontSize":13,"CommitRowMetaFontSize":13,"CommitRowBadgeFontSize":11,"SearchDebounceSeconds":0.5,"ThemeMode":"system"}"""
        let read = decoded json
        test <@ read.ShowBranchRefs && read.CommitRowMonoFontFamily = "Inconsolata" && read.CommitRowMetaFontSize = 13.0 && read.DiffLayout = DiffLayout.Unified @>

    [<Fact>]
    let ``unknown names and out-of-range values normalize; malformed text is an error`` () =
        let read = decoded """{"DiffPresentationModeKey":"sideways","ThemeMode":"LIGHT","DiffContextLines":-4,"CommitRowTextFontSize":0,"CommitRowFontFamily":"  "}"""
        test <@ read = { Settings.defaults with Theme = LightTheme; DiffContextLines = 0 } @>
        test <@ (SettingsJson.decode "not json" |> Result.isError) && (SettingsJson.decode """{"DiffContextLines":"three"}""" |> Result.isError) @>


    [<Fact>]
    let ``UI state with Windows-style repository paths round-trips`` () =
        let state =
            UiState.empty
            |> UiState.withSelectedCommit @"C:\Users\ada\repo" "abc123"
            |> UiState.withSelectedCommit "path with \"quotes\"" "def456"
        let json = UiStateJson.encode state
        test <@ UiStateJson.decode json = Ok(UiState.normalize state) @>

    [<Fact>]
    let ``recent searches and view preferences live in the UI state file`` () =
        let state =
            UiState.empty
            |> UiState.withRecentSearches [ "author:ada"; "fix "; "author:ada" ]
            |> UiState.withViewPreferences [ "FileTree", "true"; "  ", "ignored" ]
        let read = UiStateJson.encode state |> UiStateJson.decode
        test <@ read = Ok(UiState.normalize state) @>
        // Duplicates and blanks are dropped on the way in, so the file stays a preference list rather than a log.
        test <@ read |> Result.map (fun s -> s.RecentSearches, Map.toList s.ViewPreferences) = Ok([ "author:ada"; "fix" ], [ "FileTree", "true" ]) @>

module HistoryScopeTests =
    [<Fact>]
    let ``history of a branch replaces other tips and all branches but keeps file filters`` () =
        let targets = [ GitStartup.All; GitStartup.Branch "topic"; GitStartup.Exclude "old"; GitStartup.Path "src/a.fs" ]
        test <@ GitStartup.historyOf "main" targets = [ GitStartup.Revision "main"; GitStartup.Path "src/a.fs" ] @>
        test <@ GitStartup.tipNames (GitStartup.historyOf "main" targets) = [ "main" ] && GitStartup.tipNames [ GitStartup.All ] = [] @>
        test <@ GitStartup.withoutTips targets = [ GitStartup.All; GitStartup.Path "src/a.fs" ] @>

    [<Fact>]
    let ``Only commits on a branch asks for its history, and clearing goes back`` () =
        let projection = MainProjection()
        let messages = ConcurrentQueue<App.Msg>()
        projection.SetDispatch(fun message -> messages.Enqueue message)
        let model0, _ = App.init [||]
        projection.Update { model0 with StartupTargets = [ GitStartup.Path "src/a.fs" ] }
        projection.ShowHistoryOf "origin/main"
        test <@ messages.ToArray() |> Array.last = App.Msg.SetHistoryTargets [ GitStartup.Revision "origin/main"; GitStartup.Path "src/a.fs" ] @>
        projection.Update { model0 with StartupTargets = [ GitStartup.Revision "origin/main"; GitStartup.Path "src/a.fs" ] }
        test <@ projection.HasHistoryTipFilter && projection.HistoryTipFilter = "origin/main" @>
        projection.ClearHistoryTipFilter()
        test <@ messages.ToArray() |> Array.last = App.Msg.SetHistoryTargets [ GitStartup.Path "src/a.fs" ] @>

module MarkdownTests =
    [<Fact>]
    let ``markdown parser converts common rich blocks and drops front matter`` () =
        let source = "---\ntitle: Demo\n---\n# Heading\n\nA **strong** and *soft* paragraph.\n\n- one\n- two\n\n| A | B |\n| - | -: |\n| x | y |"
        let blocks = Markdown.parse source |> List.map _.Block
        test <@ blocks |> List.exists (function Heading(1, _) -> true | _ -> false) @>
        test <@ blocks |> List.exists (function Paragraph xs -> xs |> List.exists (function Strong _ -> true | _ -> false) | _ -> false) @>
        test <@ blocks |> List.exists (function ListItem _ -> true | _ -> false) @>
        test <@ blocks |> List.exists (function Table _ -> true | _ -> false) @>
        test <@ blocks |> List.exists (function HtmlBlock text when text.Contains "title:" -> true | _ -> false) |> not @>

    [<Fact>]
    let ``markdown parser preserves links images code and source locations`` () =
        let blocks = Markdown.parse "Before [docs](guide.md) ![logo](img/logo.png) `code`."
        test <@ blocks.Head.FirstLine = 1 && blocks.Head.LastLine = 1 @>
        match blocks.Head.Block with
        | Paragraph xs ->
            test <@ xs |> List.exists (function Link("guide.md", _) -> true | _ -> false) @>
            test <@ xs |> List.exists (function Image("img/logo.png", "logo", _) -> true | _ -> false) @>
            test <@ xs |> List.exists (function Code "code" -> true | _ -> false) @>
        | _ -> failwith "expected paragraph"

    [<Fact>]
    let ``rendered markdown alignment ignores rewrapping and marks changed words`` () =
        let oldDocument = Markdown.parse "A paragraph wrapped\nover two lines.\n\nSame."
        let newDocument = Markdown.parse "A paragraph wrapped over two lines.\n\nChanged."
        let changes = Markdown.align oldDocument newDocument
        test <@ changes.Head.IsUnchanged @>
        match changes.Tail.Head with
        | Modified(_, _, words) ->
            test <@ words |> List.exists (fun span -> span.Kind = MarkdownWordSpanKind.Deleted) @>
            test <@ words |> List.exists (fun span -> span.Kind = MarkdownWordSpanKind.Inserted) @>
        | _ -> failwith "expected modified paragraph"

    [<Fact>]
    let ``markdown projection owns source slices paths and preview classification`` () =
        let rows = Markdown.renderDiff "Old paragraph." "New paragraph."
        test <@ rows.Head.Change = MarkdownChangeKind.Modified @>
        test <@ rows.Head.Source = "New paragraph." @>
        test <@ Markdown.previewKind "README.md" = MarkdownPreview @>
        test <@ Markdown.previewKind "logo.webp" = ImagePreview "webp" @>
        test <@ Markdown.resolveTarget "docs/guide.md" "../img/logo.png" = RepositoryPath "img/logo.png" @>
        test <@ Markdown.resolveTarget "docs/guide.md" "#usage" = HeadingTarget "usage" @>
        test <@ Markdown.headingSlug "What's New: API_v2!" = "whats-new-api_v2" @>

    [<Fact>]
    let ``footnote references and definitions remain navigable rendered content`` () =
        let blocks = Markdown.parse "Text with note[^a].\n\n[^a]: Footnote body."
        test <@ blocks |> List.exists (fun located ->
            match located.Block with
            | Paragraph inlines -> inlines |> List.exists (function Link("#fn-a", [ Text "[a]" ]) -> true | _ -> false)
            | _ -> false) @>
        test <@ blocks |> List.exists (fun located -> match located.Block with Footnote("a", _) -> true | _ -> false) @>

    [<Fact>]
    let ``unchanged blocks moved in the document are paired instead of added and removed`` () =
        let oldText = "First paragraph.\n\nSecond paragraph.\n\nLast paragraph."
        let newText = "Second paragraph.\n\nFirst paragraph.\n\nLast paragraph."
        let changes = Markdown.align (Markdown.parse oldText) (Markdown.parse newText)
        test <@ changes |> List.filter (function MovedFrom _ | MovedTo _ -> true | _ -> false) |> List.length = 2 @>
        test <@ changes |> List.exists (function Added _ | Removed _ -> true | _ -> false) |> not @>
        let rows = Markdown.projectRows oldText newText changes
        test <@ rows |> List.filter (fun row -> row.Change = MarkdownChangeKind.Moved) |> List.forall (fun row -> row.MoveCounterpartLine.IsSome) @>

    [<Fact>]
    let ``changes only collapses long unchanged runs with surrounding context`` () =
        let oldText = [ for n in 1..12 -> if n = 7 then "old" else $"same {n}" ] |> String.concat "\n\n"
        let newText = oldText.Replace("old", "new")
        let display = Markdown.renderDiff oldText newText |> Markdown.changesOnly 2
        test <@ display |> List.exists (function UnchangedSections 2 -> true | _ -> false) @>
        test <@ display |> List.exists (function RenderedBlock row when row.Change <> MarkdownChangeKind.Unchanged -> true | _ -> false) @>

    [<Fact>]
    let ``tables quotes footnotes and nested lists flatten into independently aligned leaves`` () =
        let table = Markdown.parse "| A |\n| - |\n| one |\n| two |"
        let quote = Markdown.parse "> first\n>\n> second"
        let nested = Markdown.parse "- parent\n  - child"
        test <@ table |> List.filter (fun block -> match block.Block with Table _ -> true | _ -> false) |> List.length = 3 @>
        test <@ quote |> List.filter (fun block -> match block.Block with Quote _ -> true | _ -> false) |> List.length = 2 @>
        test <@ nested |> List.filter (fun block -> match block.Block with ListItem _ -> true | _ -> false) |> List.length = 2 @>

    [<Fact>]
    let ``word changes retain inline formatting and code changes align by line`` () =
        let prose = Markdown.renderDiff "A **small bold phrase** here." "A **large bold phrase** here."
        test <@ prose.Head.Words |> List.exists (fun span -> span.Kind = MarkdownWordSpanKind.Inserted && span.Text = "large" && span.Style = MarkdownSpanStyle.Strong) @>
        let code = Markdown.renderDiff "```fsharp\nlet answer = 41\nprintfn \"done\"\n```" "```fsharp\nlet answer = 42\nprintfn \"done\"\n```"
        test <@ code.Head.CodeLines |> List.exists (fun line -> line.Change = MarkdownChangeKind.Modified && line.Previous = Some "let answer = 41" && line.Current = Some "let answer = 42") @>

    [<Fact>]
    let ``an image blob change promotes unchanged markdown to a modified row`` () =
        let rows = Markdown.renderDiff "![logo](logo.png)" "![logo](logo.png)"
        let images =
            [ { Side = MarkdownImageSide.Old; Source = "logo.png"; Bytes = Some [| 1uy |]; Error = None }
              { Side = MarkdownImageSide.New; Source = "logo.png"; Bytes = Some [| 2uy |]; Error = None } ]
        let marked = Markdown.markChangedImages images rows
        test <@ marked.Head.Change = MarkdownChangeKind.Modified @>

    [<Fact>]
    let ``task list state belongs to the list marker and not its text`` () =
        let blocks = Markdown.parse "- [x] done\n- [ ] later" |> List.map _.Block
        test <@ blocks =
            [ ListItem(0, Bullet '-', Some true, [ Paragraph [ Text "done" ] ])
              ListItem(0, Bullet '-', Some false, [ Paragraph [ Text "later" ] ]) ] @>


module FormattedPreviewTests =
    open GitKay.Core

    [<Fact>]
    let ``a formatted preview diffs the indented text, not the minified line`` () =
        // Two one-line JSON documents differ in one field; as source that is a whole-line rewrite.
        let oldSource = """{"name":"gitkay","version":"0.10.0","tags":["git","viewer"]}"""
        let newSource = """{"name":"gitkay","version":"0.11.0","tags":["git","viewer"]}"""
        let formatted source =
            match Markdown.formatForPreview JsonFormat source with
            | Ok text -> text
            | Error message -> failwith message
        let oldText, newText = formatted oldSource, formatted newSource

        // Indented, the change is one line out of several rather than the entire file.
        let changedLines =
            GitKay.Kit.Myers.diff (oldText.Split '\n') (newText.Split '\n')
            |> List.filter (function GitKay.Kit.Equal _ -> false | _ -> true)
            |> List.length
        test <@ oldText.Split('\n').Length >= 6 @>
        test <@ changedLines = 2 @>

module XmlPreviewTests =
    open GitKay.Core

    let private formatted source =
        match Markdown.formatForPreview XmlFormat source with
        | Ok text -> text
        | Error message -> failwith message

    [<Fact>]
    let ``xml previews with one fixed shape whatever indentation it was committed with`` () =
        test <@ Markdown.previewKind "App.axaml" = FormattedPreview XmlFormat @>
        test <@ Markdown.previewKind "GitKay.UI.csproj" = FormattedPreview XmlFormat @>
        test <@ Markdown.previewKind "notes.txt" = SourceOnly @>

        let minified = """<?xml version="1.0" encoding="utf-8"?><Project Sdk="x"><PropertyGroup><A>1</A><B>2</B></PropertyGroup></Project>"""
        let sprawling = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<Project    Sdk=\"x\">\n\n      <PropertyGroup>\n  <A>1</A>\n            <B>2</B>\n   </PropertyGroup>\n</Project>"

        // Deterministic: the same document formats identically however it arrived.
        test <@ formatted minified = formatted sprawling @>
        // Indented two spaces per level, and the declaration it had is kept.
        let lines = (formatted minified).Split '\n'
        test <@ lines[0] = """<?xml version="1.0" encoding="utf-8"?>""" @>
        test <@ lines |> Array.exists (fun line -> line = "    <A>1</A>") @>
        // Attributes and their order are the document's own.
        test <@ (formatted minified).Contains """<Project Sdk="x">""" @>

    [<Fact>]
    let ``xml that is not well formed is reported rather than shown as empty`` () =
        test <@ Markdown.formatForPreview XmlFormat "<a><b></a>" |> Result.isError @>
        test <@ Markdown.formatForPreview XmlFormat "" |> Result.isError @>

module PresentationTests =
    open GitKay.Core.Presentation

    [<Fact>]
    let ``byte counts read as sizes rather than digits`` () =
        test <@ Sizes.describeBytes 512L = "512 B" @>
        test <@ Sizes.describeBytes 2048L = "2 KB" @>
        test <@ Sizes.describeBytes 1536L = "1.5 KB" @>
        test <@ Sizes.describeBytes (3L * 1024L * 1024L) = "3 MB" @>

    [<Fact>]
    let ``an image fits the room it has and is never blown up past its own pixels`` () =
        // Wider than the pane: scaled down to fit.
        test <@ ImageView.fitScale 400.0 800 = 0.5 @>
        // Smaller than the pane: left alone rather than enlarged.
        test <@ ImageView.fitScale 400.0 100 = 1.0 @>
        // A zoom of its own wins over fitting; zero means fit.
        test <@ ImageView.scale 2.0 400.0 800 = 2.0 @>
        test <@ ImageView.scale 0.0 400.0 800 = 0.5 @>

    [<Fact>]
    let ``zoom steps stay inside their range and zero returns to fitting`` () =
        test <@ ImageView.step ZoomIn 1.0 = 1.25 @>
        test <@ ImageView.step ZoomOut 1.25 = 1.0 @>
        test <@ ImageView.step ZoomToFit 4.0 = 0.0 @>
        // Held at the ends rather than running away.
        test <@ ImageView.step ZoomIn ImageView.maxZoom = ImageView.maxZoom @>
        test <@ ImageView.step ZoomOut ImageView.minZoom = ImageView.minZoom @>

    [<Fact>]
    let ``drawn size is the pixels at a scale, and nothing at all for an empty image`` () =
        test <@ ImageView.drawnSize 0.5 (800, 600) = (400.0, 300.0) @>
        test <@ ImageView.drawnSize 1.0 (0, 0) = (0.0, 0.0) @>

    [<Fact>]
    let ``the collapsible item costs more to bring back than to keep`` () =
        // Room for everything: shown either way.
        test <@ FileRow.showsCollapsible 300.0 100.0 40.0 30.0 true @>
        test <@ FileRow.showsCollapsible 300.0 100.0 40.0 30.0 false @>
        // No room: dropped either way.
        test <@ not (FileRow.showsCollapsible 150.0 100.0 40.0 30.0 true) @>
        test <@ not (FileRow.showsCollapsible 150.0 100.0 40.0 30.0 false) @>
        // On the boundary the answer depends on where it came from, which is what stops the flicker.
        let boundary = 100.0 + 40.0 + 30.0 + 5.0
        test <@ FileRow.showsCollapsible boundary 100.0 40.0 30.0 true @>
        test <@ not (FileRow.showsCollapsible boundary 100.0 40.0 30.0 false) @>

    [<Fact>]
    let ``a blended colour lands between the two and stays opaque`` () =
        let under, over = (0uy, 0uy, 0uy), (255uy, 255uy, 255uy)
        test <@ Colour.blend under over 0uy = under @>
        test <@ Colour.blend under over 255uy = over @>
        let r, _, _ = Colour.blend under over 128uy
        test <@ r > 100uy && r < 155uy @>

    [<Fact>]
    let ``change blocks scale small changes and never read as one-sided`` () =
        // A one-line change fills one block, not the whole bar.
        test <@ ChangeBlocks.split 1 0 = (1, 0) @>
        test <@ ChangeBlocks.split 0 1 = (0, 1) @>
        test <@ ChangeBlocks.split 0 0 = (0, 0) @>
        // A lopsided change still shows the smaller side.
        let green, red = ChangeBlocks.split 500 1
        test <@ red = 1 && green = 4 @>
        let green, red = ChangeBlocks.split 1 500
        test <@ green = 1 && red = 4 @>
        // Anything large fills the bar. An even split cannot divide five blocks evenly; rounding gives red the odd one.
        test <@ ChangeBlocks.split 50 50 = (2, 3) @>
        let green, red = ChangeBlocks.split 100 100
        test <@ green + red = ChangeBlocks.count @>
