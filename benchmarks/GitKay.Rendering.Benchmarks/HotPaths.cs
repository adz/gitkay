using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
internal static class HotPaths
{
    public static void Run(string[] args)
    {
        var repo = args.ElementAtOrDefault(0) ?? "/home/adam/projects/Axial/main";
        var label = args.ElementAtOrDefault(1) ?? "run";
        var results = new List<string>();
        void Report(string line) { Console.WriteLine(line); results.Add(line); }

        Report($"# hotpaths label={label} repo={repo} {DateTime.Now:O}");

        var env = GitService.environment(repo);
        var history = Unwrap(Flow.run(env, GitService.fetchHistory(FSharpOption<int>.None, false, FSharpList<GitStartup.StartupTarget>.Empty)));
        var commits = history.ToArray();

        // Representative commits, pinned so every round measures identical work. Defaults are the Axial
        // commits chosen by changed-line percentile in the baseline run (median, 90th, very large).
        (string hash, int lines) Pin(int index, string fallback) =>
            (commits.First(c => c.Hash.StartsWith(args.ElementAtOrDefault(index) ?? fallback, StringComparison.Ordinal)).Hash, 0);
        var medium = Pin(2, "1240102f");
        var large = Pin(3, "48db7ad8");
        var huge = Pin(4, "86dcf33f");
        Report($"commits={commits.Length} medium={medium.hash[..8]} large={large.hash[..8]} huge={huge.hash[..8]}");

        Measure(Report, "history.fetch(all)", 15, () =>
            Unwrap(Flow.run(GitService.environment(repo), GitService.fetchHistory(FSharpOption<int>.None, false, FSharpList<GitStartup.StartupTarget>.Empty))));

        Measure(Report, "graph.calculateLanes", 60, () => Graph.calculateLanes(history));

        foreach (var (name, target) in new[] { ("medium", medium), ("large", large), ("huge", huge) })
        {
            var iterations = name == "huge" ? 6 : 15;
            Measure(Report, $"select.cold({name})", iterations, () =>
            {
                var fresh = GitService.environment(repo);
                Unwrap(Flow.run(fresh, GitService.fetchDiffFileList(target.hash)));
                return Unwrap(Flow.run(fresh, GitService.fetchDiff(3, target.hash)));
            });
        }

        // As the app does on selection: file list and diff start concurrently in a fresh environment.
        foreach (var (name, target) in new[] { ("medium", medium), ("large", large), ("huge", huge) })
        {
            Measure(Report, $"select.app({name})", name == "huge" ? 6 : 15, () =>
            {
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
        foreach (var (name, target) in new[] { ("large", large), ("huge", huge) })
        {
            var files = Unwrap(Flow.run(env, GitService.fetchDiffFileList(target.hash)));
            var diff = Unwrap(Flow.run(env, GitService.fetchDiff(3, target.hash)));
            var model = WithSelection(baseModel, target.hash, files, diff);
            var rows = 0;
            Measure(Report, $"projection.update({name})", name == "huge" ? 8 : 20, () =>
            {
                var projection = new MainProjection();
                projection.Update(model);
                rows = projection.SelectedDiffRows.Count;
                return projection;
            });
            Report($"  rows={rows}");
        }

        var lines = largeDiff.SelectMany(f => f.Hunks).SelectMany(h => h.Lines).Select(l => l.Content).Where(t => t.Length > 0).ToArray();
        Measure(Report, $"tokenize.cold({lines.Length} lines)", 30, () =>
        {
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
        new(App.StartupSelection.NoStartupSelection, model.GitEnv, "Loaded", model.StartupTargets, model.ShowBranchRefs, model.ShowStashes, model.DiffContextLines,
            model.DiffPresentationModeKey, model.SearchQuery, model.SearchScopeKey, model.SearchUseRegex, model.SearchResults, model.Commits,
            true, FSharpOption<string>.Some(hash), FSharpOption<string>.Some(hash),
            FSharpOption<FSharpList<GitService.DiffFileSummary>>.Some(files), FSharpOption<FSharpList<Models.FileDiff>>.Some(diff),
            FSharpOption<GitService.DiffFileKey>.None, MapModule.Empty<GitService.DiffFileKey, App.FileExpansion>(),
            FSharpOption<long>.None, FSharpOption<long>.None, FSharpOption<long>.None);

    private static T Unwrap<T>(Exit<T, GitError> exit) =>
        exit.IsSuccess ? ((Exit<T, GitError>.Success)exit).Item : throw new InvalidOperationException(exit.ToString());

    private static void Measure<T>(Action<string> report, string name, int iterations, Func<T> action)
    {
        for (var i = 0; i < Math.Max(2, iterations / 5); i++) action();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var samples = new double[iterations];
        var allocatedBefore = GC.GetTotalAllocatedBytes(true);
        for (var i = 0; i < iterations; i++)
        {
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
internal static class UiInteractions
{
    public static void Run(string[] args)
    {
        var repo = args.ElementAtOrDefault(0) ?? "/home/adam/projects/Axial/main";
        var hash = args.ElementAtOrDefault(1) ?? "48db7ad8";
        var label = args.ElementAtOrDefault(2) ?? "run";
        var env = GitService.environment(repo);
        var commits = Unwrap(Flow.run(env, GitService.fetchHistory(FSharpOption<int>.None, false, FSharpList<GitStartup.StartupTarget>.Empty)));
        var full = commits.First(c => c.Hash.StartsWith(hash, StringComparison.Ordinal)).Hash;
        var files = Unwrap(Flow.run(env, GitService.fetchDiffFileList(full)));
        var diff = Unwrap(Flow.run(env, GitService.fetchDiff(3, full)));
        var (baseModel, _) = App.init(Array.Empty<string>()).ToValueTuple();
        var graph = Graph.calculateLanes(commits);
        var model = new App.Model(App.StartupSelection.NoStartupSelection, baseModel.GitEnv, "Loaded", baseModel.StartupTargets, false, false, 3, "diff", "", "commit", false,
            FSharpOption<FSharpList<GitSearch.Result>>.None, graph, true, FSharpOption<string>.Some(full), FSharpOption<string>.Some(full),
            FSharpOption<FSharpList<GitService.DiffFileSummary>>.Some(files), FSharpOption<FSharpList<Models.FileDiff>>.Some(diff),
            FSharpOption<GitService.DiffFileKey>.None, MapModule.Empty<GitService.DiffFileKey, App.FileExpansion>(),
            FSharpOption<long>.None, FSharpOption<long>.None, FSharpOption<long>.None);

        var projection = new MainProjection();
        var window = new MainWindow { Width = 1600, Height = 1000, DataContext = projection };
        window.Show();
        projection.Update(model);
        Pump(window);
        Console.WriteLine($"# ui label={label} files={files.Length} rows={projection.SelectedDiffRows.Count}");
        if (label.StartsWith("screenshot", StringComparison.Ordinal))
        {
            var pathDiff = label.Contains("pathdiff");
            var searchText = pathDiff ? "path:DiffSurface Typeface" : "font";
            var searchMode = pathDiff ? GitSearch.Mode.Diff : GitSearch.Mode.Commit;
            var results = Unwrap(Flow.run(env, GitService.searchCommits(3, commits, searchMode, false, searchText)));
            var searched = new App.Model(App.StartupSelection.NoStartupSelection, model.GitEnv, model.Status, model.StartupTargets, false, false, 3, "diff", searchText, GitSearch.modeKey(searchMode), false,
                FSharpOption<FSharpList<GitSearch.Result>>.Some(results), model.Commits, true, model.SelectedCommitHash, model.SelectedDiffHash,
                model.SelectedDiffFiles, model.SelectedDiff, model.SelectedDiffFileKey, model.DiffExpansions, FSharpOption<long>.None, FSharpOption<long>.None, FSharpOption<long>.None);
            projection.Update(searched);
            projection.IsAdvancedSearchExpanded = label.Contains("advanced");
            projection.CommitFindQuery = pathDiff ? "" : "Font";
            projection.IsShortcutHelpOpen = label.Contains("help");
            projection.IsCtrlHintsVisible = label.Contains("hints");
            if (label.Contains("palette")) { projection.OpenPalette(PaletteMode.Commands); projection.PaletteQuery = "vi"; }
            if (label.Contains("dark")) Avalonia.Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
            if (label.Contains("light")) Avalonia.Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
            Pump(window);
            if (label.Contains("scrolled"))
            {
                var diffScroll = Avalonia.Controls.NameScopeExtensions.Find<Avalonia.Controls.ScrollViewer>(window, "DiffRowsScrollViewer")!;
                diffScroll.Offset = new Avalonia.Vector(0, 1150);
                Pump(window);
            }
            if (label.Contains("selection"))
            {
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

        if (label == "gap-selection-probe")
        {
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

        if (label == "hover-probe")
        {
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

        if (label == "click-probe")
        {
            var diffSurface = Avalonia.Controls.NameScopeExtensions.Find<DiffSurfaceControl>(window, "DiffRowsListBox")!;
            var scroll = Avalonia.Controls.NameScopeExtensions.Find<Avalonia.Controls.ScrollViewer>(window, "DiffRowsScrollViewer")!;
            Pump(window);
            var failures = new List<string>();
            var rows = projection.SelectedDiffRows.ToArray();
            var viewportTop = Avalonia.VisualExtensions.TranslatePoint(scroll, new Avalonia.Point(0, 0), window)!.Value;
            for (var y = 6.0; y < scroll.Viewport.Height - 4; y += 20)
            {
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

        if (label == "enter-probe")
        {
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
            if (gap != null)
            {
                projection.SelectedDiffRow = gap;
                Pump(window);
                var dispatched = new List<string>();
                projection.SetDispatch(msg => dispatched.Add(msg.GetType().Name + " " + msg));
                Press(Avalonia.Input.Key.Enter);
                Console.WriteLine($"after Enter on gap: dispatched={string.Join(" | ", dispatched.Select(d => d.Length > 80 ? d[..80] : d))}");
            }
            return;
        }

        if (label == "ctrl-probe")
        {
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

        if (label == "profile-drag")
        {
            var dragGrid = Avalonia.Controls.NameScopeExtensions.Find<Avalonia.Controls.Grid>(window, "DiffSplitGrid")!;
            for (var i = 0; i < 60; i++)
            {
                dragGrid.ColumnDefinitions[2].Width = new Avalonia.Controls.GridLength(220 + (i % 20) * 12);
                Pump(window);
            }
            return;
        }
        if (label == "fonts")
        {
            foreach (var family in new[] { "Inter", "Segoe UI", "Arial", "sans-serif", "Inter,Segoe UI,Arial,sans-serif", "$Default", "SF Mono,Menlo,Consolas,Liberation Mono,Noto Sans Mono,monospace", "Cascadia Code,Consolas,Monospace", "Helvetica,Arial,Liberation Sans,Noto Sans,sans-serif" })
            {
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
        if (label == "profile-drag")
        {
            var dragGrid = Avalonia.Controls.NameScopeExtensions.Find<Avalonia.Controls.Grid>(window, "DiffSplitGrid")!;
            for (var i = 0; i < 60; i++)
            {
                dragGrid.ColumnDefinitions[2].Width = new Avalonia.Controls.GridLength(220 + (i % 20) * 12);
                Pump(window);
            }
            return;
        }
        int Count<T>() => Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<T>().Count();
        Console.WriteLine($"  realized: ListBoxItem={Count<Avalonia.Controls.ListBoxItem>()} DiffStatBar={Count<DiffStatBar>()} TextBlock={Count<Avalonia.Controls.TextBlock>()} visuals={Count<Avalonia.Visual>()}");

        Measure($"filelist.toggle(tree<->patch)", 10, () =>
        {
            projection.IsDiffFileTreeMode = !projection.IsDiffFileTreeMode;
            Pump(window);
        });

        var grid = Avalonia.Controls.NameScopeExtensions.Find<Avalonia.Controls.Grid>(window, "DiffSplitGrid")!;
        var width = 220.0;
        Measure("splitter.drag(step)", 40, () =>
        {
            width = width >= 600 ? 220 : width + 12;
            grid.ColumnDefinitions[2].Width = new Avalonia.Controls.GridLength(width);
            Pump(window);
        });

        Measure("window.frame(idle)", 40, () => Pump(window));
    }

    private static void CommitListBoxFocus(Avalonia.Controls.Window window) =>
        Avalonia.Controls.NameScopeExtensions.Find<Avalonia.Controls.Control>(window, "CommitListBox")!.Focus();

    private static void Pump(Avalonia.Controls.Window window)
    {
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        using var frame = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    private static T Unwrap<T>(Exit<T, GitError> exit) =>
        exit.IsSuccess ? ((Exit<T, GitError>.Success)exit).Item : throw new InvalidOperationException(exit.ToString());

    private static void Measure(string name, int iterations, Action action)
    {
        for (var i = 0; i < 3; i++) action();
        var samples = new double[iterations];
        for (var i = 0; i < iterations; i++)
        {
            var started = Stopwatch.GetTimestamp();
            action();
            samples[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
        Array.Sort(samples);
        Console.WriteLine($"{name,-32} median={samples[samples.Length / 2],9:F2}ms p90={samples[(int)(samples.Length * 0.9)],9:F2}ms max={samples[^1],9:F2}ms n={iterations}");
    }
}
