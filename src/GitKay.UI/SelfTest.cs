using System;
using System.IO;
using System.Linq;

namespace GitKay.UI;

/// <summary>
/// <c>gitkay --self-test</c>: runs without a window and exercises code that only fails in the shipped NativeAOT binary
/// (reflection-based formatting, Axial diagnostics, LibGit2Sharp). Release builds run it after publishing.
/// </summary>
public static class SelfTest {
    public static int Run(string repositoryPath) {
        var failures = 0;
        void Report(string name, bool passed, string detail) {
            Console.WriteLine(passed ? $"ok   {name}" : $"FAIL {name}: {detail}");
            if (!passed) failures++;
        }

        void Check(string name, Func<string?> run) {
            try {
                var problem = run();
                Report(name, problem == null, problem ?? "");
            }
            catch (Exception exception) {
                Report(name, false, $"{exception.GetType().FullName}: {exception.Message}");
            }
        }

        foreach (var result in GitKay.Core.SelfTest.run(repositoryPath))
            Report(result.Name, result.Passed, result.Detail);

        Check("libgit2 blob cache configured", () =>
            NativeGitOptions.Status.StartsWith("blob cache", StringComparison.Ordinal) || NativeGitOptions.Status.StartsWith("skipped", StringComparison.Ordinal)
                ? null
                : NativeGitOptions.Status);

        Check("streaming search reports matches as found", () => {
            var repository = GitKay.Core.GitService.tryDiscoverRepositoryPath();
            var env = GitKay.Core.GitService.environment(repository);
            var history = Axial.Flow.run(env, GitKay.Core.GitService.fetchHistory(Microsoft.FSharp.Core.FSharpOption<int>.Some(200), false, Microsoft.FSharp.Collections.FSharpList<GitKay.Core.GitStartup.StartupTarget>.Empty));
            if (!history.IsSuccess) return "history failed";
            var commits = ((Axial.Exit<Microsoft.FSharp.Collections.FSharpList<GitKay.Core.Models.Commit>, GitKay.Core.GitError>.Success)history).Item;
            var found = 0;
            var search = Axial.Flow.run(env, GitKay.Core.GitService.searchCommitsStreaming(3, commits, GitKay.Core.GitSearch.Mode.Diff, false, "e",
                Microsoft.FSharp.Core.FuncConvert.FromAction<int, int>((_, _) => { }),
                Microsoft.FSharp.Core.FuncConvert.FromAction<GitKay.Core.GitSearch.Result>(_ => System.Threading.Interlocked.Increment(ref found))));
            if (!search.IsSuccess) return "search failed: " + search;
            var results = ((Axial.Exit<Microsoft.FSharp.Collections.FSharpList<GitKay.Core.GitSearch.Result>, GitKay.Core.GitError>.Success)search).Item;
            return results.Length == found ? null : $"{results.Length} results but {found} streamed";
        });

        Check("uncommitted changes parse and load", () => {
            var entries = GitKay.Core.WorkingTree.parseStatus(
                "1 MM N... 100644 100644 100644 abc abc a.txt\0" +
                "2 R. N... 100644 100644 100644 abc abc R100 new name.txt\0old name.txt\0" +
                "? notes/draft.md\0");
            if (GitKay.Core.WorkingTree.summary(entries) != "2 staged · 1 unstaged · 1 untracked") return "status summary: " + GitKay.Core.WorkingTree.summary(entries);
            var patch = GitKay.Core.WorkingTree.parsePatch("diff --git a/a.txt b/a.txt\n--- a/a.txt\n+++ b/a.txt\n@@ -1 +1 @@\n-one\n+two\n");
            if (patch.Length != 1 || patch.Head.Hunks.Head.Lines.Length != 2) return "patch did not parse";
            var repository = GitKay.Core.GitService.tryDiscoverRepositoryPath();
            var loaded = Axial.Exit.toResult(Axial.Flow.run(GitKay.Core.GitService.environment(repository), GitKay.Core.GitService.fetchWorkingTreeChanges(3)));
            return loaded.IsOk ? null : "git status and diff failed: " + GitKay.Core.GitErrorModule.describe(loaded.ErrorValue);
        });

        Check("partial patches build for staging lines", () => {
            var raw = "diff --git a/a.txt b/a.txt\n--- a/a.txt\n+++ b/a.txt\n@@ -1,2 +1,3 @@\n one\n-two\n+TWO\n+three\n";
            var chosen = Microsoft.FSharp.Collections.ListModule.OfSeq(new[] {
                new GitKay.Core.PatchBuilder.SelectedLine(0, 2, GitKay.Core.Models.LineType.Added, "TWO") });
            var patch = GitKay.Core.PatchBuilder.build(GitKay.Core.PatchBuilder.Direction.Forward, raw, chosen);
            if (!patch.IsOk) return GitKay.Core.PatchBuilder.describeError(patch.ErrorValue);
            return patch.ResultValue.Contains("@@ -1,2 +1,3 @@\n one\n two\n+TWO\n") ? null : "unexpected patch: " + patch.ResultValue;
        });

        Check("presentation formatting without reflection", () => {
            // F# printf specifiers resolve through MakeGenericMethod, which NativeAOT cannot generate: these read
            // fine under the JIT and threw NotSupportedException in a published binary until they were rewritten.
            var sizes = new[] {
                (512L, "512 B"), (1536L, "1.5 KB"), (2048L, "2 KB"), (3L * 1024 * 1024, "3 MB"),
            };
            foreach (var (count, expected) in sizes) {
                var actual = GitKay.Core.Presentation.Sizes.describeBytes(count);
                if (actual != expected) return $"{count} formatted as {actual}, expected {expected}";
            }
            return null;
        });

        Check("folders pair and diff without a repository", () => {
            var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gitkay-selftest-" + Guid.NewGuid().ToString("N"));
            var left = System.IO.Path.Combine(root, "left");
            var right = System.IO.Path.Combine(root, "right");
            try {
                System.IO.Directory.CreateDirectory(left);
                System.IO.Directory.CreateDirectory(right);
                System.IO.File.WriteAllText(System.IO.Path.Combine(left, "a.txt"), "one\ntwo");
                System.IO.File.WriteAllText(System.IO.Path.Combine(right, "a.txt"), "one\nTWO");
                var leftEntries = GitKay.Core.FolderSource.read(left);
                var rightEntries = GitKay.Core.FolderSource.read(right);
                if (leftEntries.IsError || rightEntries.IsError) return "could not read the folders";
                var pairs = GitKay.Core.Folder.pair(leftEntries.ResultValue, rightEntries.ResultValue);
                if (pairs.Length != 1) return $"expected one pair, got {pairs.Length}";
                var diff = GitKay.Core.FolderSource.diff(pairs[0]);
                var (added, removed) = GitKay.Core.SourceDiff.counts(diff).ToValueTuple();
                return added == 1 && removed == 1 ? null : $"expected +1 -1, got +{added} -{removed}";
            }
            finally {
                try { System.IO.Directory.Delete(root, true); } catch { }
            }
        });

        Check("settings round-trip through the Reified codec", () => {
            // Twenty-odd fields: records this wide threw TypeLoadException under NativeAOT before Reified 0.8.1.
            var settings = new GitKay.Core.Settings(true, true, 7, GitKay.Core.DiffLayout.SideBySide, "Inter", "Iosevka", 14.5, 12, 10, 0.25, true, false, GitKay.Core.ThemeMode.DarkTheme, 4.0, false, false, true, GitKay.Core.PaneFocusEffect.PaneShadow, GitKay.Core.PaneEffectColor.PurpleEffectColor, GitKay.Core.PaneEffectIntensity.QuarterIntensity, true, GitKay.Core.PaneBorderStyle.CustomBorder, GitKay.Core.PaneEffectColor.TealEffectColor, 2.0, true, GitKay.Core.ChromeBackground.TintedChrome, GitKay.Core.PaneEffectColor.SandEffectColor);
            var read = GitKay.Serialization.SettingsJson.decode(GitKay.Serialization.SettingsJson.encode(settings));
            if (!read.IsOk || !read.ResultValue.Equals(settings)) return "values changed in the round trip";
            var defaults = GitKay.Serialization.SettingsJson.decode("{\"ShowStashes\":true}");
            return defaults.IsOk && defaults.ResultValue.ShowStashes && defaults.ResultValue.DiffContextLines == 3 && defaults.ResultValue.Theme.IsSystemTheme
                ? null
                : "missing fields did not take defaults";
        });

        Check("UI state round-trips through the Reified codec", () => {
            var layout = new GitKay.Core.UiLayout(0.4, 300.0, null, null, null, null);
            var state = GitKay.Core.UiStateModule.withSelectedCommit("/repo", "abc123",
                GitKay.Core.UiStateModule.withWindowSize(1200.0, 800.0, GitKay.Core.UiStateModule.withLayout(layout, GitKay.Core.UiStateModule.empty)));
            var read = GitKay.Serialization.UiStateJson.decode(GitKay.Serialization.UiStateJson.encode(state));
            return read.IsOk && read.ResultValue.Equals(GitKay.Core.UiStateModule.normalize(state)) ? null : "values changed in the round trip";
        });

        Check("diagnostics window data refreshes", () => {
            var diagnostics = new DiagnosticsProjection();
            diagnostics.Refresh(force: true);
            if (diagnostics.Settled.Count == 0) return "no settled flows shown";
            if (!diagnostics.Settled.Any(row => row.Status == "Failed")) return "failed flow status not shown";
            if (diagnostics.Failures.Count == 0) return "no failures shown";
            return diagnostics.Stats.Count == 0 ? "no flow totals" : null;
        });

        Check("diagnostics snapshot and report", () => {
            var diagnostics = new DiagnosticsProjection();
            diagnostics.Refresh(force: true);
            var snapshot = diagnostics.BuildSnapshot();
            if (!snapshot.Contains("Running flows")) return "snapshot missing running flows";
            var path = DiagnosticsLog.WriteReport("selftest", snapshot);
            if (path == null || !File.Exists(path)) return "report not written";
            File.Delete(path);
            return null;
        });

        Check("flow details render", () => {
            var settled = Axial.Elmish.CmdDiagnostics.Settled();
            if (settled.Length == 0) return "no settled fibers";
            var text = DiagnosticsProjection.DescribeSettled(settled[^1]);
            var failed = settled.FirstOrDefault(fiber => fiber.Name == "self-test failure");
            var failedText = failed == null ? "" : DiagnosticsProjection.DescribeSettled(failed);
            if (!text.Contains("Status")) return "details missing status";
            return failed != null && !failedText.Contains("Revision not found") ? "failure cause missing from details" : null;
        });

        Check("markdown parses, aligns, and decodes PNG", () => {
            var markdown = "# Heading\n\nA **small** paragraph.\n\n![pixel](pixel.png)";
            var changed = "# Heading\n\nA **larger** paragraph.\n\n![pixel](pixel.png)";
            var blocks = GitKay.Core.Markdown.parse(markdown);
            var rows = GitKay.Core.Markdown.renderDiff(markdown, changed);
            if (blocks.Length != 3 || !rows.Any(row => row.Change == GitKay.Core.MarkdownChangeKind.Modified))
                return "Markdown parse or alignment failed";
            var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
            using var bitmap = SkiaSharp.SKBitmap.Decode(png);
            return bitmap != null && bitmap.Width == 1 && bitmap.Height == 1 ? null : "PNG decoding failed";
        });

        Check("history targets render", () => {
            var projection = new MainProjection();
            projection.Update(GitKay.Core.App.init([]).Item1);
            return null;
        });

        Console.WriteLine(failures == 0 ? "Self-test passed." : $"Self-test: {failures} failure(s).");
        return failures == 0 ? 0 : 1;
    }
}
