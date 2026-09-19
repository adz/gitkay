using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Avalonia.VisualTree;
using Axial;
using App = GitKay.Core.App;
using GitKay.Core;
using GitKay.UI;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;

/// <summary>
/// Hot-path benchmarks against a real repository: history, graph, commit selection, context expansion,
/// UI projection and tokenization. Usage: hotpaths [repoPath] [label]
/// </summary>
internal static class HotPaths {
    public static void Run(string[] args) {
        // GitKay's own repository by default: it is here, it grows, and its commits are the ones we care about.
        var repo = args.ElementAtOrDefault(0) ?? GitService.tryDiscoverRepositoryPath();
        var label = args.ElementAtOrDefault(1) ?? "run";
        var results = new List<string>();
        void Report(string line) { Console.WriteLine(line); results.Add(line); }

        Report($"# hotpaths label={label} repo={repo} {DateTime.Now:O}");

        var env = GitService.environment(repo);
        var history = Unwrap(Flow.run(env, GitService.fetchHistory(FSharpOption<int>.None, false, FSharpList<GitStartup.StartupTarget>.Empty)));
        var commits = history.ToArray();

        // Representative commits of GitKay's own history, pinned so every round measures identical work: the median,
        // the 90th percentile and the largest by changed lines. A pin that isn't in the history throws rather than
        // silently measuring a different commit — rebased pins want a deliberate new baseline, not a substitute.
        (string hash, int lines) Pin(int index, string fallback) =>
            (commits.First(c => c.Hash.StartsWith(args.ElementAtOrDefault(index) ?? fallback, StringComparison.Ordinal)).Hash, 0);
        var medium = Pin(2, "305ba67");
        var large = Pin(3, "b54218a");
        var huge = Pin(4, "1313b62");
        Report($"commits={commits.Length} medium={medium.hash[..8]} large={large.hash[..8]} huge={huge.hash[..8]}");

        Measure(Report, "history.fetch(all)", 15, () =>
            Unwrap(Flow.run(GitService.environment(repo), GitService.fetchHistory(FSharpOption<int>.None, false, FSharpList<GitStartup.StartupTarget>.Empty))));

        Measure(Report, "graph.calculateLanes", 60, () => Graph.calculateLanes(history));

        foreach (var (name, target) in new[] { ("medium", medium), ("large", large), ("huge", huge) }) {
            var iterations = name == "huge" ? 6 : 15;
            Measure(Report, $"select.cold({name})", iterations, () => {
                var fresh = GitService.environment(repo);
                Unwrap(Flow.run(fresh, GitService.fetchDiffFileList(target.hash)));
                return Unwrap(Flow.run(fresh, GitService.fetchDiff(3, target.hash)));
            });
        }

        // As the app does on selection: file list and diff start concurrently in a fresh environment.
        foreach (var (name, target) in new[] { ("medium", medium), ("large", large), ("huge", huge) }) {
            Measure(Report, $"select.app({name})", name == "huge" ? 6 : 15, () => {
                var fresh = GitService.environment(repo);
                var list = System.Threading.Tasks.Task.Run(() => Unwrap(Flow.run(fresh, GitService.fetchDiffFileList(target.hash))));
                var diff = System.Threading.Tasks.Task.Run(() => Unwrap(Flow.run(fresh, GitService.fetchDiff(3, target.hash))));
                System.Threading.Tasks.Task.WaitAll(list, diff);
                return diff.Result;
            });
        }

        // Expansion projection on the largest text file in the large commit.
        var largeDiff = Unwrap(Flow.run(env, GitService.fetchDiff(3, large.hash))).ToArray();
        var biggest = largeDiff
            .Where(f => f.OldPath != "/dev/null" && f.NewPath != "/dev/null" && f.Hunks.Length > 1)
            .OrderByDescending(f => f.NewLineCount?.Value ?? 0)
            .FirstOrDefault() ?? largeDiff.OrderByDescending(f => f.Hunks.Sum(h => h.Lines.Length)).First();
        var full = Unwrap(Flow.run(env, GitService.fetchDiffFileFullContext(large.hash, biggest.OldPath, biggest.NewPath)));
        var revealed = ListModule.OfSeq(new[] { new DiffExpansion.LineRange(1, 40), new DiffExpansion.LineRange(200, 260) });
        Report($"expand file={biggest.NewPath} newLines={biggest.NewLineCount} hunks={biggest.Hunks.Length}");
        Measure(Report, "expand.project(full+revealed)", 200, () => DiffExpansion.project(biggest, FSharpOption<Models.FileDiff>.Some(full), revealed));
        Measure(Report, "expand.project(configured)", 2000, () => DiffExpansion.project(biggest, FSharpOption<Models.FileDiff>.None, FSharpList<DiffExpansion.LineRange>.Empty));

        // UI projection: selecting the large and huge commits (a new projection each time; updates are diffed).
        var (baseModel, _) = App.init(Array.Empty<string>()).ToValueTuple();
        foreach (var (name, target) in new[] { ("large", large), ("huge", huge) }) {
            var files = Unwrap(Flow.run(env, GitService.fetchDiffFileList(target.hash)));
            var diff = Unwrap(Flow.run(env, GitService.fetchDiff(3, target.hash)));
            var model = WithSelection(baseModel, target.hash, files, diff);
            var rows = 0;
            Measure(Report, $"projection.update({name})", name == "huge" ? 8 : 20, () => {
                var projection = new MainProjection();
                projection.Update(model);
                rows = projection.SelectedDiffRows.Count;
                return projection;
            });
            Report($"  rows={rows}");
        }

        var lines = largeDiff.SelectMany(f => f.Hunks).SelectMany(h => h.Lines).Select(l => l.Content).Where(t => t.Length > 0).ToArray();
        Measure(Report, $"tokenize.cold({lines.Length} lines)", 30, () => {
            SyntaxHighlighting.ClearTokenCache();
            var count = 0;
            foreach (var line in lines) count += SyntaxHighlighting.Tokenize(line).Count;
            return count;
        });

        var outDir = Path.Combine(AppContext.BaseDirectory, "hotpaths-results");
        Directory.CreateDirectory(outDir);
        File.WriteAllLines(Path.Combine(outDir, $"{label}.txt"), results);
    }

    private static App.Model WithSelection(App.Model model, string hash, FSharpList<GitService.DiffFileSummary> files, FSharpList<Models.FileDiff> diff) =>
        new(App.StartupSelection.NoStartupSelection, false, model.GitEnv, "Loaded", model.StartupTargets, model.ShowBranchRefs, model.ShowStashes, model.DiffContextLines,
            model.IgnoreWhitespace, model.DiffLayout, model.SearchQuery, model.SearchScopeKey, model.SearchUseRegex, model.SearchResults, model.Commits,
            true, App.Selection.NewCommitSelected(hash), FSharpOption<GitService.RevisionComparison>.None, FSharpOption<string>.Some(hash),
            FSharpOption<FSharpList<GitService.DiffFileSummary>>.Some(files), FSharpOption<FSharpList<Models.FileDiff>>.Some(diff),
            FSharpOption<GitService.DiffFileKey>.None, MapModule.Empty<GitService.DiffFileKey, App.FileExpansion>(),
            FSharpOption<long>.None, FSharpOption<long>.None, FSharpOption<long>.None, FSharpOption<Tuple<int, int>>.None, model.WorkingTree, model.WorkingTreeChanges, model.WorkingTreeStartedAtTicks, model.LastDiscard);

    private static T Unwrap<T>(Exit<T, GitError> exit) =>
        exit.IsSuccess ? ((Exit<T, GitError>.Success)exit).Item : throw new InvalidOperationException(exit.ToString());

    private static void Measure<T>(Action<string> report, string name, int iterations, Func<T> action) {
        for (var i = 0; i < Math.Max(2, iterations / 5); i++) action();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var samples = new double[iterations];
        var allocatedBefore = GC.GetTotalAllocatedBytes(true);
        for (var i = 0; i < iterations; i++) {
            var started = Stopwatch.GetTimestamp();
            GC.KeepAlive(action());
            samples[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
        var allocated = (GC.GetTotalAllocatedBytes(true) - allocatedBefore) / iterations;
        Array.Sort(samples);
        var median = samples[samples.Length / 2];
        var p90 = samples[Math.Min(samples.Length - 1, (int)(samples.Length * 0.9))];
        report($"{name,-34} median={median,9:F3}ms p90={p90,9:F3}ms min={samples[0],9:F3}ms alloc/op={allocated / 1024.0,10:F1}KB n={iterations}");
    }
}

/// <summary>Headless MainWindow interaction benchmarks: file list mode switch and splitter drag. Usage: ui [repoPath] [hash] [label]</summary>
internal static class UiInteractions {
    public static void Run(string[] args) {
        if (args.ElementAtOrDefault(2) is { } settingsLabel && settingsLabel.StartsWith("settingswindow", StringComparison.Ordinal)) {
            if (settingsLabel.Contains("dark")) Avalonia.Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
            var settingsWindow = new SettingsWindow { DataContext = new MainProjection(), Width = 760, Height = 620 };
            settingsWindow.Show();
            // "settingswindow-tab3" opens the fourth tab, so each page can be checked on its own.
            if (settingsLabel.Split("tab").ElementAtOrDefault(1) is { } index && int.TryParse(index, out var tab)
                && Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(settingsWindow).OfType<Avalonia.Controls.TabControl>().FirstOrDefault() is { } tabs)
                tabs.SelectedIndex = tab;
            for (var i = 0; i < 20; i++) { System.Threading.Thread.Sleep(50); Pump(settingsWindow); }
            using var shot = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(settingsWindow);
            var settingsPath = args.ElementAtOrDefault(3) ?? $"{settingsLabel}.png";
            shot!.Save(settingsPath);
            Console.WriteLine($"saved {settingsPath}");
            settingsWindow.Close();
            return;
        }
        if (args.ElementAtOrDefault(2) is { } commitLabel && commitLabel.StartsWith("commitwindow", StringComparison.Ordinal)) {
            RunCommitWindow(args[0], commitLabel, args.ElementAtOrDefault(3) ?? $"{commitLabel}.png");
            return;
        }
        var repo = args.ElementAtOrDefault(0) ?? GitService.tryDiscoverRepositoryPath();
        var hash = args.ElementAtOrDefault(1) ?? "48db7ad8";
        var label = args.ElementAtOrDefault(2) ?? "run";
        var env = GitService.environment(repo);
        var commits = Unwrap(System.Threading.Tasks.Task.Run(() => Flow.run(env, GitService.fetchHistory(FSharpOption<int>.None, false, FSharpList<GitStartup.StartupTarget>.Empty))).Result);
        var full = commits.First(c => c.Hash.StartsWith(hash, StringComparison.Ordinal)).Hash;
        var files = Unwrap(System.Threading.Tasks.Task.Run(() => Flow.run(env, GitService.fetchDiffFileList(full))).Result);
        var diff = Unwrap(System.Threading.Tasks.Task.Run(() => Flow.run(env, GitService.fetchDiff(3, full))).Result);
        var (baseModel, _) = App.init(Array.Empty<string>()).ToValueTuple();
        var graph = Graph.calculateLanes(commits);
        var model = new App.Model(App.StartupSelection.NoStartupSelection, false, baseModel.GitEnv, "Loaded", baseModel.StartupTargets, false, false, 3, false, DiffLayout.Unified, "", "commit", false,
            FSharpOption<FSharpList<GitSearch.Result>>.None, graph, true, App.Selection.NewCommitSelected(full), FSharpOption<GitService.RevisionComparison>.None, FSharpOption<string>.Some(full),
            FSharpOption<FSharpList<GitService.DiffFileSummary>>.Some(files), FSharpOption<FSharpList<Models.FileDiff>>.Some(diff),
            FSharpOption<GitService.DiffFileKey>.None, MapModule.Empty<GitService.DiffFileKey, App.FileExpansion>(),
            FSharpOption<long>.None, FSharpOption<long>.None, FSharpOption<long>.None, FSharpOption<Tuple<int, int>>.None, baseModel.WorkingTree, baseModel.WorkingTreeChanges, baseModel.WorkingTreeStartedAtTicks, baseModel.LastDiscard);

        var projection = new MainProjection();
        var windowWidth = label.Contains("narrow") ? 1100 : 1600;
        var window = new MainWindow { Width = windowWidth, Height = 1000, DataContext = projection };
        window.Show();
        projection.Update(model);
        Pump(window);
        Console.WriteLine($"# ui label={label} files={files.Length} rows={projection.SelectedDiffRows.Count}");
        if (label.Contains("scrolljank")) {
            // A slow scroll, the way a trackpad delivers it: small steps with a pause between them, long enough for
            // the idle timer to fire and prefetch to run. Reports what each step cost and what the surface did.
            var scroller = Avalonia.Controls.NameScopeExtensions.Find<Avalonia.Controls.ScrollViewer>(window, "DiffRowsScrollViewer")!;
            for (var warm = 0; warm < 10; warm++) { Pump(window); System.Threading.Thread.Sleep(20); }
            DiffSurfaceControl.DiagRebuilds = DiffSurfaceControl.DiagPrefetches = DiffSurfaceControl.DiagCacheClears =
                DiffSurfaceControl.DiagLayoutsBuilt = DiffSurfaceControl.DiagHighlights = DiffSurfaceControl.DiagRenders = 0;
            DiffSurfaceControl.DiagRenderMs = DiffSurfaceControl.DiagRenderMaxMs = DiffSurfaceControl.DiagPrefetchMs = 0;
            var steps = new List<double>();
            for (var step = 0; step < 60; step++) {
                var watch = Stopwatch.StartNew();
                scroller.Offset = scroller.Offset.WithY(scroller.Offset.Y + 24);
                window.UpdateLayout();
                Pump(window);
                steps.Add(watch.Elapsed.TotalMilliseconds);
                // The pause is what lets the idle timer fire, as a slow hand does.
                System.Threading.Thread.Sleep(50);
                Pump(window);
            }

            var sorted = steps.OrderBy(value => value).ToArray();
            Console.WriteLine($"scroll steps={steps.Count} p50={sorted[sorted.Length / 2]:F1}ms p90={sorted[(int)(sorted.Length * 0.9)]:F1}ms max={sorted[^1]:F1}ms");
            Console.WriteLine($"scroll rebuilds={DiffSurfaceControl.DiagRebuilds} prefetches={DiffSurfaceControl.DiagPrefetches} cacheClears={DiffSurfaceControl.DiagCacheClears} layouts={DiffSurfaceControl.DiagLayoutsBuilt} highlights={DiffSurfaceControl.DiagHighlights}");
            Console.WriteLine($"scroll renders={DiffSurfaceControl.DiagRenders} renderTotal={DiffSurfaceControl.DiagRenderMs:F0}ms renderMax={DiffSurfaceControl.DiagRenderMaxMs:F1}ms prefetchTotal={DiffSurfaceControl.DiagPrefetchMs:F0}ms");
            Console.WriteLine("scroll slowest=" + string.Join(", ", steps.Select((value, index) => (value, index)).OrderByDescending(pair => pair.value).Take(5).Select(pair => $"#{pair.index}:{pair.value:F0}ms")));
            return;
        }

        if (label.StartsWith("worktree", StringComparison.Ordinal)) {
            var changes = Unwrap(System.Threading.Tasks.Task.Run(() => Flow.run(env, GitService.fetchWorkingTreeChanges(3))).Result);
            projection.RepositoryPath = repo;
            projection.IsAllFilesMode = label.Contains("allfiles");
            projection.IsDiffFileTreeMode = label.Contains("tree");
            projection.Update(new App.Model(model.StartupSelection, false, model.GitEnv, "Loaded", model.StartupTargets, false, false, 3, false, DiffLayout.Unified, "", "commit", false,
                model.SearchResults, model.Commits, true, App.Selection.WorkingTreeSelected, FSharpOption<GitService.RevisionComparison>.None, FSharpOption<string>.None,
                FSharpOption<FSharpList<GitService.DiffFileSummary>>.None, FSharpOption<FSharpList<Models.FileDiff>>.None, FSharpOption<GitService.DiffFileKey>.None,
                model.DiffExpansions, FSharpOption<long>.None, FSharpOption<long>.None, FSharpOption<long>.None, FSharpOption<Tuple<int, int>>.None,
                changes.Entries, FSharpOption<GitService.WorkingTreeChanges>.Some(changes), FSharpOption<long>.None, FSharpOption<GitKay.Core.Trash.Backup>.None));
            Pump(window);
            if (label.Contains("expand")) {
                projection.ToggleDiffFileContextCommand.Execute(projection.SelectedDiffFiles[0]);
                for (var i = 0; i < 40 && projection.SelectedDiffFiles[0].IsContextLoading | projection.SelectedDiffFiles[0].HasHiddenContext; i++) {
                    System.Threading.Thread.Sleep(50);
                    Pump(window);
                }
            }
            if (label.Contains("stage")) {
                // Stage the hunk at the cursor with s, from the history window's uncommitted view. There is no Elmish
                // host here, so the dispatched message is what this checks.
                var dispatched = new List<App.Msg>();
                projection.SetDispatch(dispatched.Add);
                var diffSurface = Avalonia.Controls.NameScopeExtensions.Find<DiffSurfaceControl>(window, "DiffRowsListBox")!;
                diffSurface.Focus();
                diffSurface.SelectedItem = projection.SelectedDiffRows.OfType<DiffLineProjection>().First(line => line.IsAdded || line.IsRemoved);
                Pump(window);
                Avalonia.Headless.HeadlessWindowExtensions.KeyPressQwerty(window, Avalonia.Input.PhysicalKey.S, Avalonia.Input.RawInputModifiers.None);
                Pump(window);
                var staged = dispatched.OfType<App.Msg.ApplyWorkingTreeLines>().FirstOrDefault();
                Console.WriteLine(staged == null
                    ? $"after s: nothing dispatched ({dispatched.Count} messages), status={projection.Status}"
                    : $"after s: {staged.target} {staged.path} lines={staged.lines.Length}");
            }
            Pump(window);
            using var frame = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window);
            var output = args.ElementAtOrDefault(3) ?? $"{label}.png";
            frame!.Save(output);
            Console.WriteLine($"saved {output} files={projection.SelectedDiffFiles.Count} rows={projection.SelectedDiffRows.Count}");
            return;
        }
        if (label.StartsWith("screenshot", StringComparison.Ordinal)) {
            // A plain window for the README: no search text, just the history and the selected commit's diff.
            if (label.Contains("readme")) {
                if (label.Contains("dark")) Avalonia.Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
                if (label.Contains("light")) Avalonia.Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
                projection.ShowBranchRefs = true;
                Pump(window);
                foreach (var file in projection.SelectedDiffFiles.Take(3)) {
                    var sample = file.Hunks.SelectMany(hunk => hunk.Lines).FirstOrDefault(line => line.Content.Contains(" for "))
                                 ?? file.Hunks.FirstOrDefault()?.Lines.FirstOrDefault();
                    if (sample == null) continue;
                    var tokens = SyntaxHighlighting.Tokenize(sample.Content, sample.Flavour);
                    Console.WriteLine($"file={file.DisplayPath} flavour={sample.Flavour} len={sample.Content.Length} tokens={string.Join(" | ", tokens.Take(6).Select(t => t.Kind + ":" + t.Text.Substring(0, Math.Min(12, t.Text.Length))))}");
                }
                for (var i = 0; i < 20; i++) { System.Threading.Thread.Sleep(50); Pump(window); }
                using var readme = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window);
                var readmePath = args.ElementAtOrDefault(3) ?? $"{label}.png";
                readme!.Save(readmePath);
                Console.WriteLine($"saved {readmePath} commits={projection.Commits.Count} rows={projection.SelectedDiffRows.Count}");
                return;
            }
            var pathDiff = label.Contains("pathdiff");
            var searchText = pathDiff ? "path:DiffSurface Typeface" : "font";
            var searchMode = pathDiff ? GitSearch.Mode.Diff : GitSearch.Mode.Commit;
            var results = Unwrap(System.Threading.Tasks.Task.Run(() => Flow.run(env, GitService.searchCommits(3, commits, searchMode, false, searchText))).Result);
            var searched = new App.Model(App.StartupSelection.NoStartupSelection, false, model.GitEnv, model.Status, model.StartupTargets, false, false, 3, false, DiffLayout.Unified, searchText, GitSearch.modeKey(searchMode), false,
                FSharpOption<FSharpList<GitSearch.Result>>.Some(results), model.Commits, true, model.Selection, model.RevisionComparison, model.SelectedDiffHash,
                model.SelectedDiffFiles, model.SelectedDiff, model.SelectedDiffFileKey, model.DiffExpansions, FSharpOption<long>.None, FSharpOption<long>.None, FSharpOption<long>.None, FSharpOption<Tuple<int, int>>.None, model.WorkingTree, model.WorkingTreeChanges, model.WorkingTreeStartedAtTicks, model.LastDiscard);
            projection.Update(searched);
            projection.IsAdvancedSearchExpanded = label.Contains("advanced");
            projection.CommitFindQuery = pathDiff ? "" : "Font";
            projection.IsShortcutHelpOpen = label.Contains("help");
            projection.IsCtrlHintsVisible = label.Contains("hints");
            if (label.Contains("palette")) { projection.OpenPalette(PaletteMode.Commands); projection.PaletteQuery = "vi"; }
            if (label.Contains("dark")) Avalonia.Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
            if (label.Contains("light")) Avalonia.Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
            Pump(window);
            if (label.Contains("datepicker")) {
                var pick = window.GetVisualDescendants().OfType<Avalonia.Controls.Button>().First(button => (button.Tag as string) == "after");
                pick.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
                Pump(window);
                var popups = window.GetVisualDescendants().OfType<Avalonia.Controls.Calendar>().Count();
                Console.WriteLine($"calendar popups={popups}");
            }
            if (label.Contains("stagefrommain")) {
                // Select the uncommitted row, then stage the hunk at the cursor with s.
                projection.SelectedCommit = projection.Commits.First(row => row.IsWorkingTree);
                for (var i = 0; i < 40; i++) { System.Threading.Thread.Sleep(50); Pump(window); }
                var diffSurface = Avalonia.Controls.NameScopeExtensions.Find<DiffSurfaceControl>(window, "DiffRowsListBox")!;
                Console.WriteLine($"uncommitted files={projection.SelectedDiffFiles.Count} shown={projection.IsWorkingTreeDiffShown}");
                diffSurface.Focus();
                diffSurface.SelectedItem = projection.SelectedDiffRows.OfType<DiffLineProjection>().First(line => line.IsAdded || line.IsRemoved);
                Avalonia.Headless.HeadlessWindowExtensions.KeyPressQwerty(window, Avalonia.Input.PhysicalKey.S, Avalonia.Input.RawInputModifiers.None);
                for (var i = 0; i < 60; i++) { System.Threading.Thread.Sleep(50); Pump(window); }
                Console.WriteLine($"after s status={projection.Status}");
            }
            if (label.Contains("hoverfind")) {
                var box = Avalonia.Controls.NameScopeExtensions.Find<Avalonia.Controls.TextBox>(window, "CommitFindBox")!;
                // Walk up the visual tree to the window, summing each element's offset.
                var point = new Avalonia.Point(box.Bounds.Width / 2, box.Bounds.Height / 2);
                for (Avalonia.Visual? visual = box; visual != null && visual != window; visual = Avalonia.VisualTree.VisualExtensions.GetVisualParent(visual))
                    point += new Avalonia.Vector(visual.Bounds.X, visual.Bounds.Y);
                Avalonia.Headless.HeadlessWindowExtensions.MouseMove(window, point, Avalonia.Input.RawInputModifiers.None);
                Pump(window);
                Console.WriteLine($"find hovered={box.IsPointerOver} at={point}");
            }
            if (label.Contains("scrolled")) {
                var diffScroll = Avalonia.Controls.NameScopeExtensions.Find<Avalonia.Controls.ScrollViewer>(window, "DiffRowsScrollViewer")!;
                diffScroll.Offset = new Avalonia.Vector(0, 1150);
                Pump(window);
            }
            if (label.Contains("selection")) {
                var diffSurface = Avalonia.Controls.NameScopeExtensions.Find<DiffSurfaceControl>(window, "DiffRowsListBox")!;
                var first = diffSurface.FirstLineRowIndex(2);
                var last = diffSurface.FirstLineRowIndex(4);
                diffSurface.SelectText(first, 4, last, 12);
                Console.WriteLine("copy text:\n" + diffSurface.GetCopyText() + "\n--- end");
                Pump(window);
            }
            Pump(window);
            using var bitmap = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window);
            var path = args.ElementAtOrDefault(3) ?? $"{label}.png";
            bitmap!.Save(path);
            Console.WriteLine($"saved {path} matches={results.Length}");
            return;
        }

        if (label == "gap-selection-probe") {
            var diffSurface = Avalonia.Controls.NameScopeExtensions.Find<DiffSurfaceControl>(window, "DiffRowsListBox")!;
            var rowsBefore = projection.SelectedDiffRows.ToList();
            var gapIndex = rowsBefore.FindIndex(r => r is DiffGapProjection && rowsBefore.IndexOf(r) > 20);
            projection.SelectedDiffRow = rowsBefore[gapIndex];
            Pump(window);
            diffSurface.Focus();
            // Simulate the expansion rebuild: the gap row is replaced by the lines it hid.
            var replaced = rowsBefore.ToList();
            replaced.RemoveAt(gapIndex);
            projection.SelectedDiffRows.Clear();
            projection.SelectedDiffRows.AddRange(replaced);
            Pump(window);
            Avalonia.Headless.HeadlessWindowExtensions.KeyPress(window, Avalonia.Input.Key.Down, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.ArrowDown, null);
            Pump(window);
            var selectedIndex = replaced.IndexOf(projection.SelectedDiffRow!);
            Console.WriteLine($"gap was at {gapIndex}; after rebuild + Down selected index={selectedIndex} (expected {gapIndex + 1})");
            return;
        }

        if (label == "hover-probe") {
            window.Activate();
            Avalonia.Controls.Control Find(string name) => Avalonia.Controls.NameScopeExtensions.Find<Avalonia.Controls.Control>(window, name)!;
            Avalonia.Point Center(string name) { var c = Find(name); return Avalonia.VisualExtensions.TranslatePoint(c, new Avalonia.Point(c.Bounds.Width / 2, Math.Min(c.Bounds.Height / 2, 200)), window)!.Value; }
            void Move(string name) { Avalonia.Headless.HeadlessWindowExtensions.MouseMove(window, Center(name), Avalonia.Input.RawInputModifiers.None); Pump(window); }
            string Focused() => (Avalonia.Controls.TopLevel.GetTopLevel(window)!.FocusManager!.GetFocusedElement() as Avalonia.Controls.Control)?.Name ?? (Avalonia.Controls.TopLevel.GetTopLevel(window)!.FocusManager!.GetFocusedElement()?.GetType().Name ?? "none");
            Find("CommitListBox").Focus(); Pump(window);
            Move("DiffRowsScrollViewer");
            Console.WriteLine($"hover diff -> focused={Focused()} fade(commits)={Find("CommitPaneFocus").Opacity} fade(diff)={Find("DiffPaneFocus").Opacity}");
            Avalonia.Headless.HeadlessWindowExtensions.KeyPress(window, Avalonia.Input.Key.F, Avalonia.Input.RawInputModifiers.Control, Avalonia.Input.PhysicalKey.F, "f"); Pump(window);
            Console.WriteLine($"Ctrl+F while hovering diff -> focused={Focused()}");
            Move("CommitScrollViewer");
            Console.WriteLine($"hover commits while typing in find box -> focused={Focused()}");
            Avalonia.Headless.HeadlessWindowExtensions.KeyPress(window, Avalonia.Input.Key.D3, Avalonia.Input.RawInputModifiers.Control, Avalonia.Input.PhysicalKey.Digit3, null); Pump(window);
            Console.WriteLine($"Ctrl+3 from text box -> focused={Focused()}");
            // Mouse stays over the diff; keyboard jumps to commits; Ctrl+F must follow the keyboard.
            Move("DiffRowsScrollViewer");
            Avalonia.Headless.HeadlessWindowExtensions.KeyPress(window, Avalonia.Input.Key.D1, Avalonia.Input.RawInputModifiers.Control, Avalonia.Input.PhysicalKey.Digit1, null); Pump(window);
            Avalonia.Headless.HeadlessWindowExtensions.KeyPress(window, Avalonia.Input.Key.F, Avalonia.Input.RawInputModifiers.Control, Avalonia.Input.PhysicalKey.F, "f"); Pump(window);
            Console.WriteLine($"hover diff, Ctrl+1, Ctrl+F -> focused={Focused()}");
            return;
        }

        if (label == "click-probe") {
            var diffSurface = Avalonia.Controls.NameScopeExtensions.Find<DiffSurfaceControl>(window, "DiffRowsListBox")!;
            var scroll = Avalonia.Controls.NameScopeExtensions.Find<Avalonia.Controls.ScrollViewer>(window, "DiffRowsScrollViewer")!;
            Pump(window);
            var failures = new List<string>();
            var rows = projection.SelectedDiffRows.ToArray();
            var viewportTop = Avalonia.VisualExtensions.TranslatePoint(scroll, new Avalonia.Point(0, 0), window)!.Value;
            for (var y = 6.0; y < scroll.Viewport.Height - 4; y += 20) {
                var point = new Avalonia.Point(viewportTop.X + 400, viewportTop.Y + y);
                var docY = scroll.Offset.Y + y;
                projection.SelectedDiffRow = null;
                Pump(window);
                Avalonia.Headless.HeadlessWindowExtensions.MouseDown(window, point, Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
                Avalonia.Headless.HeadlessWindowExtensions.MouseUp(window, point, Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
                System.Threading.Thread.Sleep(350); // avoid double-click counting
                Pump(window);
                var selected = projection.SelectedDiffRow;
                var expectedKind = diffSurface.RowKindAt(docY);
                if (expectedKind == "line" && selected is not DiffLineProjection)
                    failures.Add($"y={y} doc={docY:F0} kind={expectedKind} selected={selected?.GetType().Name ?? "null"} surface={diffSurface.SelectedItem?.GetType().Name ?? "null"} content={(diffSurface.SelectedItem as DiffLineProjection)?.Content} added={(rows.ElementAtOrDefault(diffSurface.RowIndexAt(docY)) as DiffLineProjection)?.IsAdded} hit={diffSurface.HitDebug(new Avalonia.Point(400, docY))}");
            }
            Console.WriteLine($"click failures: {failures.Count}");
            foreach (var f in failures.Take(12)) Console.WriteLine("  " + f);
            return;
        }

        if (label == "enter-probe") {
            var diffSurface = Avalonia.Controls.NameScopeExtensions.Find<DiffSurfaceControl>(window, "DiffRowsListBox")!;
            var file = projection.SelectedDiffFiles[0];
            projection.SelectedDiffRow = file.Header;
            Pump(window);
            diffSurface.Focus();
            void Press(Avalonia.Input.Key key) { Avalonia.Headless.HeadlessWindowExtensions.KeyPress(window, key, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.None, null); Avalonia.Threading.Dispatcher.UIThread.RunJobs(); }
            Press(Avalonia.Input.Key.Enter);
            Console.WriteLine($"after Enter on header: collapsed={file.IsCollapsed} rows={projection.SelectedDiffRows.Count}");
            Press(Avalonia.Input.Key.Enter);
            Console.WriteLine($"after second Enter: collapsed={file.IsCollapsed} rows={projection.SelectedDiffRows.Count}");
            var gap = projection.SelectedDiffRows.OfType<DiffGapProjection>().FirstOrDefault();
            if (gap != null) {
                projection.SelectedDiffRow = gap;
                Pump(window);
                var dispatched = new List<string>();
                projection.SetDispatch(msg => dispatched.Add(msg.GetType().Name + " " + msg));
                Press(Avalonia.Input.Key.Enter);
                Console.WriteLine($"after Enter on gap: dispatched={string.Join(" | ", dispatched.Select(d => d.Length > 80 ? d[..80] : d))}");
            }
            return;
        }

        if (label == "ctrl-probe") {
            window.Activate();
            CommitListBoxFocus(window);
            Avalonia.Headless.HeadlessWindowExtensions.KeyPress(window, Avalonia.Input.Key.LeftCtrl, Avalonia.Input.RawInputModifiers.Control, Avalonia.Input.PhysicalKey.ControlLeft, null);
            var until = DateTime.UtcNow.AddMilliseconds(700); while (DateTime.UtcNow < until) { System.Threading.Thread.Sleep(20); Avalonia.Threading.Dispatcher.UIThread.RunJobs(); }
            Console.WriteLine($"after hold: IsCtrlHintsVisible={projection.IsCtrlHintsVisible}");
            // Auto-repeat style release+press pair: must not hide.
            Avalonia.Headless.HeadlessWindowExtensions.KeyRelease(window, Avalonia.Input.Key.LeftCtrl, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.ControlLeft, null);
            Avalonia.Headless.HeadlessWindowExtensions.KeyPress(window, Avalonia.Input.Key.LeftCtrl, Avalonia.Input.RawInputModifiers.Control, Avalonia.Input.PhysicalKey.ControlLeft, null);
            var repeatUntil = DateTime.UtcNow.AddMilliseconds(150); while (DateTime.UtcNow < repeatUntil) { System.Threading.Thread.Sleep(10); Avalonia.Threading.Dispatcher.UIThread.RunJobs(); }
            Console.WriteLine($"after auto-repeat pair: IsCtrlHintsVisible={projection.IsCtrlHintsVisible}");
            Avalonia.Headless.HeadlessWindowExtensions.KeyRelease(window, Avalonia.Input.Key.LeftCtrl, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.ControlLeft, null);
            var releaseUntil = DateTime.UtcNow.AddMilliseconds(150); while (DateTime.UtcNow < releaseUntil) { System.Threading.Thread.Sleep(10); Avalonia.Threading.Dispatcher.UIThread.RunJobs(); }
            Console.WriteLine($"after release: IsCtrlHintsVisible={projection.IsCtrlHintsVisible}");
            return;
        }

        if (label == "profile-drag") {
            var dragGrid = Avalonia.Controls.NameScopeExtensions.Find<Avalonia.Controls.Grid>(window, "DiffSplitGrid")!;
            for (var i = 0; i < 60; i++) {
                dragGrid.ColumnDefinitions[2].Width = new Avalonia.Controls.GridLength(220 + (i % 20) * 12);
                Pump(window);
            }
            return;
        }
        if (label == "fonts") {
            foreach (var family in new[] { "Inter", "Segoe UI", "Arial", "sans-serif", "Inter,Segoe UI,Arial,sans-serif", "$Default", "SF Mono,Menlo,Consolas,Liberation Mono,Noto Sans Mono,monospace", "Cascadia Code,Consolas,Monospace", "Helvetica,Arial,Liberation Sans,Noto Sans,sans-serif" }) {
                var typeface = new Avalonia.Media.Typeface(new Avalonia.Media.FontFamily(family));
                var t0 = Stopwatch.GetTimestamp();
                var found = false;
                for (var i = 0; i < 20; i++) found = Avalonia.Media.FontManager.Current.TryGetGlyphTypeface(typeface, out _);
                Console.WriteLine($"  font {family,-60} found={found} per-lookup={(Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency / 20:F3}ms");
            }
            foreach (var text in Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<Avalonia.Controls.TextBlock>().Take(400)
                .Select(t => $"{t.FontFamily} | {t.FontWeight}").Distinct())
                Console.WriteLine($"  textblock font: {text}");
            return;
        }
        if (label == "profile-drag") {
            var dragGrid = Avalonia.Controls.NameScopeExtensions.Find<Avalonia.Controls.Grid>(window, "DiffSplitGrid")!;
            for (var i = 0; i < 60; i++) {
                dragGrid.ColumnDefinitions[2].Width = new Avalonia.Controls.GridLength(220 + (i % 20) * 12);
                Pump(window);
            }
            return;
        }
        int Count<T>() => Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<T>().Count();
        Console.WriteLine($"  realized: ListBoxItem={Count<Avalonia.Controls.ListBoxItem>()} DiffStatBar={Count<DiffStatBar>()} TextBlock={Count<Avalonia.Controls.TextBlock>()} visuals={Count<Avalonia.Visual>()}");

        Measure($"filelist.toggle(tree<->patch)", 10, () => {
            projection.IsDiffFileTreeMode = !projection.IsDiffFileTreeMode;
            Pump(window);
        });

        var grid = Avalonia.Controls.NameScopeExtensions.Find<Avalonia.Controls.Grid>(window, "DiffSplitGrid")!;
        var width = 220.0;
        Measure("splitter.drag(step)", 40, () => {
            width = width >= 600 ? 220 : width + 12;
            grid.ColumnDefinitions[2].Width = new Avalonia.Controls.GridLength(width);
            Pump(window);
        });

        Measure("window.frame(idle)", 40, () => Pump(window));
    }

    private static void CommitListBoxFocus(Avalonia.Controls.Window window) =>
        Avalonia.Controls.NameScopeExtensions.Find<Avalonia.Controls.Control>(window, "CommitListBox")!.Focus();

    /// <summary>The commit window on a repository: scanned, then optionally with the cursor's hunk staged.</summary>
    private static void RunCommitWindow(string repo, string label, string output) {
        if (label.Contains("dark")) Avalonia.Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        var window = new GitKay.UI.CommitWindow(repo, System.IO.Path.GetFileName(repo)) { Width = 1300, Height = 860 };
        window.Show();
        var projection = window.Projection!;
        void Settle() {
            for (var i = 0; i < 60; i++) { System.Threading.Thread.Sleep(50); Pump(window); }
        }
        Settle();
        if (label.Contains("stagehunk")) {
            var surface = Avalonia.Controls.NameScopeExtensions.Find<DiffSurfaceControl>(window, "Surface")!;
            surface.SelectedItem = projection.Rows.OfType<DiffLineProjection>().First(line => line.IsAdded || line.IsRemoved);
            projection.ApplyToSelection();
            Settle();
        }
        if (label.Contains("keyboard")) {
            // Real key presses: Ctrl+3 to the diff, j to a change, s stages its hunk.
            Avalonia.Headless.HeadlessWindowExtensions.KeyPressQwerty(window, Avalonia.Input.PhysicalKey.Digit3, Avalonia.Input.RawInputModifiers.Control);
            Pump(window);
            var surface = Avalonia.Controls.NameScopeExtensions.Find<DiffSurfaceControl>(window, "Surface")!;
            Console.WriteLine($"diff focused={surface.IsKeyboardFocusWithin}");
            surface.SelectedItem = projection.Rows.OfType<DiffLineProjection>().First(line => line.IsAdded || line.IsRemoved);
            Avalonia.Headless.HeadlessWindowExtensions.KeyPressQwerty(window, Avalonia.Input.PhysicalKey.S, Avalonia.Input.RawInputModifiers.None);
            Settle();
            Avalonia.Headless.HeadlessWindowExtensions.KeyPressQwerty(window, Avalonia.Input.PhysicalKey.Digit1, Avalonia.Input.RawInputModifiers.Control);
            Avalonia.Headless.HeadlessWindowExtensions.KeyPressQwerty(window, Avalonia.Input.PhysicalKey.J, Avalonia.Input.RawInputModifiers.None);
            Pump(window);
            Console.WriteLine($"after j selected={projection.SelectedFile?.Path}");
        }
        if (label.Contains("search")) {
            var surface = Avalonia.Controls.NameScopeExtensions.Find<DiffSurfaceControl>(window, "Surface")!;
            Avalonia.Headless.HeadlessWindowExtensions.KeyPressQwerty(window, Avalonia.Input.PhysicalKey.Digit3, Avalonia.Input.RawInputModifiers.Control);
            Pump(window);
            Avalonia.Headless.HeadlessWindowExtensions.KeyPressQwerty(window, Avalonia.Input.PhysicalKey.Slash, Avalonia.Input.RawInputModifiers.None);
            Pump(window);
            Avalonia.Headless.HeadlessWindowExtensions.KeyTextInput(window, "fourt");
            Pump(window);
            Console.WriteLine($"search open={projection.IsSearchOpen} row={(surface.SelectedItem as DiffLineProjection)?.Content}");
            Avalonia.Headless.HeadlessWindowExtensions.KeyPressQwerty(window, Avalonia.Input.PhysicalKey.Enter, Avalonia.Input.RawInputModifiers.None);
            Pump(window);
            Console.WriteLine($"after enter open={projection.IsSearchOpen} status={projection.Status}");
            Avalonia.Headless.HeadlessWindowExtensions.KeyPressQwerty(window, Avalonia.Input.PhysicalKey.P, Avalonia.Input.RawInputModifiers.Control);
            Pump(window);
            Avalonia.Headless.HeadlessWindowExtensions.KeyTextInput(window, "note");
            Pump(window);
            Console.WriteLine($"palette open={projection.IsFilePaletteOpen} first={(projection.PaletteFiles.Count > 0 ? projection.PaletteFiles[0].Path : "-")} count={projection.PaletteFiles.Count}");
            Avalonia.Headless.HeadlessWindowExtensions.KeyPressQwerty(window, Avalonia.Input.PhysicalKey.Enter, Avalonia.Input.RawInputModifiers.None);
            Settle();
            Console.WriteLine($"palette chose={projection.SelectedFile?.Path} open={projection.IsFilePaletteOpen}");
        }
        if (label.Contains("jolt")) {
            Avalonia.Headless.HeadlessWindowExtensions.KeyPressQwerty(window, Avalonia.Input.PhysicalKey.Digit3, Avalonia.Input.RawInputModifiers.Control);
            Settle();
            using (var before = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window)) before!.Save(output.Replace(".png", "-before.png"));
            Avalonia.Headless.HeadlessWindowExtensions.KeyPressQwerty(window, Avalonia.Input.PhysicalKey.Slash, Avalonia.Input.RawInputModifiers.None);
            Settle();
        }
        if (label.Contains("panes")) {
            var effect = label.Contains("shadow") ? GitKay.Core.PaneFocusEffect.PaneShadow
                : label.Contains("noeffect") ? GitKay.Core.PaneFocusEffect.NoPaneEffect
                : GitKay.Core.PaneFocusEffect.PaneGlow;
            var color = label.Contains("purple") ? GitKay.Core.PaneEffectColor.PurpleEffectColor
                : label.Contains("green") ? GitKay.Core.PaneEffectColor.GreenEffectColor
                : GitKay.Core.PaneEffectColor.AccentEffectColor;
            var intensity = label.Contains("quarter") ? GitKay.Core.PaneEffectIntensity.QuarterIntensity
                : label.Contains("eighth") ? GitKay.Core.PaneEffectIntensity.EighthIntensity
                : label.Contains("full") ? GitKay.Core.PaneEffectIntensity.FullIntensity
                : GitKay.Core.PaneEffectIntensity.HalfIntensity;
            var paneSettings = new PaneChrome.Settings(4, true, label.Contains("highlight"), effect, color, intensity,
                label.Contains("bordered"), GitKay.Core.PaneBorderStyle.SubtleBorder, color, 1.0, label.Contains("nolines"));
            window.SetPaneChrome(paneSettings);
            // Focus the unstaged list so the chosen effect shows in the gap.
            var list = Avalonia.Controls.NameScopeExtensions.Find<Avalonia.Controls.ListBox>(window, "UnstagedList")!;
            // A ListBox doesn't take focus itself; its item containers do, as the window's own pane focus does it.
            var row = list.SelectedItem ?? list.Items.Cast<object>().FirstOrDefault();
            if (row != null && list.ContainerFromItem(row) is { } container) container.Focus();
            else list.Focus();
            Settle();
            var effectBorder = Avalonia.Controls.NameScopeExtensions.Find<Avalonia.Controls.Border>(window, "UnstagedPaneEffect")!;
            Console.WriteLine($"focused={list.IsKeyboardFocusWithin} opacity={effectBorder.Opacity} shadow={effectBorder.BoxShadow.Count} margin={effectBorder.Margin} bounds={effectBorder.Bounds}");
        }
        if (label.Contains("filetree")) {
            projection.SetFileListModeCommand.Execute(label.Contains("fileall") ? "all" : "tree");
            Settle();
            Console.WriteLine($"rows={projection.UnstagedRows.Count} folders={projection.UnstagedRows.OfType<CommitFolderRow>().Count()} repo={projection.UnstagedRows.OfType<CommitRepoFileRow>().Count()}");
        }
        if (label.Contains("expandstage")) {
            var surface = Avalonia.Controls.NameScopeExtensions.Find<DiffSurfaceControl>(window, "Surface")!;
            // A file with two hunks and a gap between them.
            projection.SelectedUnstaged = projection.UnstagedFiles.First(row => row.Path.Contains("numbers"));
            Settle();
            var before = projection.Rows.Count;
            var gap = projection.Rows.OfType<DiffGapProjection>().FirstOrDefault();
            Console.WriteLine($"rows={before} gaps={projection.Rows.OfType<DiffGapProjection>().Count()}");
            if (gap != null) {
                projection.ExpandDiffGapCommand.Execute(new DiffGapExpansionRequest(gap.Gap, GitKay.Core.DiffExpansion.ExpandDirection.All, null));
                Settle();
                Console.WriteLine($"after expand rows={projection.Rows.Count}");
            }
            // Stage the hunk at the last change: its patch must still line up with the file's own diff.
            surface.SelectedItem = projection.Rows.OfType<DiffLineProjection>().Last(line => line.IsAdded || line.IsRemoved);
            projection.ApplyToSelection();
            Settle();
            Console.WriteLine($"status={projection.Status}");
        }
        if (label.Contains("amend")) {
            Console.WriteLine($"before amend unstaged={projection.UnstagedFiles.Count} staged={projection.StagedFiles.Count} rows={projection.Rows.Count} selected={projection.SelectedFile?.Path}");
            projection.Amend = true;
            Settle();
            Console.WriteLine($"after amend unstaged={projection.UnstagedFiles.Count} staged={projection.StagedFiles.Count} rows={projection.Rows.Count} selected={projection.SelectedFile?.Path} message={projection.Message.Split('\n')[0]}");
            if (label.Contains("commit")) {
                projection.Commit();
                Settle();
                Settle();
                Console.WriteLine($"after commit unstaged={projection.UnstagedFiles.Count} staged={projection.StagedFiles.Count} rows={projection.Rows.Count} selected={projection.SelectedFile?.Path} status={projection.Status}");
            }
        }
        if (label.Contains("discardundo")) {
            var file = projection.UnstagedFiles.First(row => !row.IsUntracked);
            projection.SelectedUnstaged = file;
            Settle();
            projection.ConfirmDiscard = _ => System.Threading.Tasks.Task.FromResult(true);
            projection.DiscardCommand.Execute(null);
            Settle();
            Console.WriteLine($"after discard unstaged={projection.UnstagedFiles.Count} undo={projection.CanUndoDiscard} status={projection.Status}");
            projection.UndoDiscard();
            Settle();
            Console.WriteLine($"after undo unstaged={projection.UnstagedFiles.Count} undo={projection.CanUndoDiscard} status={projection.Status}");
        }
        if (label.Contains("keys")) projection.IsKeysOpen = true;
        if (label.Contains("hints")) {
            using (var before = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window)) before!.Save(output.Replace(".png", "-before.png"));
            projection.IsCtrlHintsVisible = true;
            Settle();
        }
        if (label.Contains("message")) projection.Message = "Stage the first hunk\n\nShows the commit window in a screenshot.";
        Pump(window);
        using var frame = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window);
        frame!.Save(output);
        Console.WriteLine($"saved {output} unstaged={projection.UnstagedFiles.Count} staged={projection.StagedFiles.Count} status={projection.Status}");
        window.Close();
    }

    private static void Pump(Avalonia.Controls.Window window) {
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        using var frame = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    private static T Unwrap<T>(Exit<T, GitError> exit) =>
        exit.IsSuccess ? ((Exit<T, GitError>.Success)exit).Item : throw new InvalidOperationException(exit.ToString());

    private static void Measure(string name, int iterations, Action action) {
        for (var i = 0; i < 3; i++) action();
        var samples = new double[iterations];
        for (var i = 0; i < iterations; i++) {
            var started = Stopwatch.GetTimestamp();
            action();
            samples[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
        Array.Sort(samples);
        Console.WriteLine($"{name,-32} median={samples[samples.Length / 2],9:F2}ms p90={samples[(int)(samples.Length * 0.9)],9:F2}ms max={samples[^1],9:F2}ms n={iterations}");
    }
}
