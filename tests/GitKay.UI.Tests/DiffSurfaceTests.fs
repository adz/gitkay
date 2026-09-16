namespace GitKay.Tests

open System
open System.Threading
open Avalonia
open Avalonia.Collections
open Avalonia.Controls
open Avalonia.Headless
open Avalonia.Input
open Avalonia.Themes.Fluent
open Avalonia.Threading
open CommunityToolkit.Mvvm.Input
open Xunit
open Swensen.Unquote
open GitKay.Core
open GitKay.UI

type HeadlessTestApp() =
    inherit Application()
    override this.Initialize() = this.Styles.Add(FluentTheme())

    static member BuildAvaloniaApp() =
        AppBuilder
            .Configure<HeadlessTestApp>()
            .UseHeadless(AvaloniaHeadlessPlatformOptions(UseHeadlessDrawing = true))

module private Headless =
    let session = lazy (HeadlessUnitTestSession.StartNew(typeof<HeadlessTestApp>))

    let run (action: unit -> unit) =
        let work = session.Value.Dispatch(Action action, CancellationToken.None)
        if not (work.Wait(TimeSpan.FromSeconds 30.0)) then failwith "Headless UI work did not finish within 30 seconds."

    let pump () = Dispatcher.UIThread.RunJobs()

/// A diff surface over three files whose lines are identical, as the diff pane hosts it.
type private DiffFixture(layout: DiffLayout) =
    let files =
        [ for name in [ "a.txt"; "b.txt"; "c.txt" ] ->
              let file = DiffFileProjection({ OldPath = name; NewPath = name; DisplayPath = name } : GitService.DiffFileSummary)
              let lines =
                  [ for number in 1..40 ->
                        let line : Models.DiffLine =
                            { Type = if number = 3 then Models.Context elif number % 2 = 0 then Models.Added else Models.Removed
                              Content =
                                if number = 5 then "long = " + String.replicate 60 "abcdefghij"
                                elif number = 7 then "call(first.second, third) + x"
                                elif number = 9 then "    if (a[i] == b) { go(); }"
                                else $"value = {number}"
                              OldLineNo = if number % 2 = 0 && number <> 3 then None else Some number
                              NewLineNo = if number % 2 = 1 && number <> 3 then None else Some number }
                        DiffLineProjection line :> IDiffRowProjection ]
              file, lines ]

    let rows = AvaloniaList<IDiffRowProjection>()

    let render () =
        let next = ResizeArray<IDiffRowProjection>()
        for (file: DiffFileProjection), lines in files do
            next.Add file.Header
            if not file.IsCollapsed then next.AddRange lines
        rows.Clear()
        rows.AddRange next

    let surface = DiffSurfaceControl(DiffLayout = layout, ItemsSource = rows)
    let scroller = ScrollViewer(Height = 300.0, Content = surface, HorizontalScrollBarVisibility = Primitives.ScrollBarVisibility.Disabled)
    let window = Window(Width = 800.0, Height = 300.0, Content = scroller)

    do
        surface.ToggleFileCommand <-
            RelayCommand<DiffFileProjection>(fun file ->
                file.IsCollapsed <- not file.IsCollapsed
                render ())
        render ()
        window.Show()
        Headless.pump ()

    member _.Surface = surface
    member _.Scroller = scroller
    member _.Window = window
    member _.Header index = (fst files[index]).Header :> IDiffRowProjection
    member _.Line fileIndex lineIndex = (snd files[fileIndex])[lineIndex]

    member _.ViewportTop(row: IDiffRowProjection) =
        let index = rows.IndexOf row
        let top = Seq.sum [ for i in 0 .. index - 1 -> surface.RowHeightAt i ]
        top - scroller.Offset.Y

    interface IDisposable with
        member _.Dispose() =
            window.Close()
            Headless.pump ()

    member _.Press(key: Key, ?modifiers: RawInputModifiers, ?symbol: string) =
        window.KeyPress(key, defaultArg modifiers RawInputModifiers.None, PhysicalKey.None, defaultArg symbol null)
        Headless.pump ()

module DiffSurfaceTests =
    let private toggleKeepsHeaderInPlace (headerTop: float) =
        Headless.run (fun () ->
            use fixture = new DiffFixture(DiffLayout.Unified)
            let header = fixture.Header 1
            fixture.Scroller.Offset <- Vector(0.0, fixture.ViewportTop header - headerTop)
            Headless.pump ()
            let before = fixture.ViewportTop header
            fixture.Surface.Focus() |> ignore
            fixture.Surface.SelectedItem <- header
            fixture.Press Key.Enter
            let collapsed = fixture.ViewportTop header
            fixture.Press Key.Enter
            let reopened = fixture.ViewportTop header
            // Plain numbers keep Unquote from rendering the Avalonia object graph when this fails.
            let positions = [ before; collapsed; reopened ] |> List.map (fun y -> Math.Round(y, 1))
            test <@ positions = List.replicate 3 (Math.Round(before, 1)) @>)

    [<Fact>]
    let ``collapsing and reopening a file at the top of the viewport keeps its header in place`` () =
        toggleKeepsHeaderInPlace 0.0

    [<Fact>]
    let ``collapsing and reopening a file below other lines keeps its header in place`` () =
        toggleKeepsHeaderInPlace 120.0

    [<Fact>]
    let ``collapsing a file whose header is stuck to the top leaves it at the top`` () =
        Headless.run (fun () ->
            use fixture = new DiffFixture(DiffLayout.Unified)
            let header = fixture.Header 1
            // Scrolled into the file: its header row is above the viewport and drawn as the sticky header.
            fixture.Scroller.Offset <- Vector(0.0, fixture.ViewportTop header + 200.0)
            Headless.pump ()
            let stuck = fixture.ViewportTop header < 0.0
            fixture.Surface.Focus() |> ignore
            fixture.Surface.SelectedItem <- header
            fixture.Press Key.Enter
            let collapsedTop = Math.Round(fixture.ViewportTop header, 1)
            // The sticky card sits flush with the top; the row has a 12px margin above its card.
            test <@ stuck && collapsedTop = -12.0 @>)

    [<Fact>]
    let ``a stuck header lands in place without a frame at the old offset`` () =
        Headless.run (fun () ->
            use fixture = new DiffFixture(DiffLayout.Unified)
            let header = fixture.Header 1
            fixture.Scroller.Offset <- Vector(0.0, fixture.ViewportTop header + 200.0)
            Headless.pump ()
            let offsetBefore = fixture.Scroller.Offset.Y
            fixture.Surface.Focus() |> ignore
            fixture.Surface.SelectedItem <- header
            // The collapse runs the command and rebuilds rows; the offset must already be corrected, with no pump.
            fixture.Window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null)
            let offsetAfterCollapse = fixture.Scroller.Offset.Y
            Headless.pump ()
            let settled = fixture.Scroller.Offset.Y
            test <@ offsetAfterCollapse <> offsetBefore && Math.Round(offsetAfterCollapse, 1) = Math.Round(settled, 1) @>)

    [<Fact>]
    let ``expanding a file again returns to where its content was`` () =
        Headless.run (fun () ->
            use fixture = new DiffFixture(DiffLayout.Unified)
            let header = fixture.Header 1
            // Scroll well into the second file.
            fixture.Scroller.Offset <- Vector(0.0, fixture.ViewportTop header + 260.0)
            Headless.pump ()
            let insideFile = Math.Round(-fixture.ViewportTop header, 1)
            fixture.Surface.Focus() |> ignore
            fixture.Surface.SelectedItem <- header
            fixture.Press Key.Enter
            let whileCollapsed = Math.Round(-fixture.ViewportTop header, 1)
            fixture.Press Key.Enter
            let afterExpanding = Math.Round(-fixture.ViewportTop header, 1)
            // Collapsed, the header sits at the top; expanded, the view is back inside the file.
            test <@ insideFile = 260.0 && whileCollapsed = 12.0 && afterExpanding = insideFile @>)

    [<Fact>]
    let ``v selects from the caret on the side the caret is on`` () =
        Headless.run (fun () ->
            use fixture = new DiffFixture(DiffLayout.SideBySide)
            fixture.Surface.Focus() |> ignore
            // Line 2 is added: only the new side has text, so the caret starts there.
            fixture.Surface.SelectedItem <- fixture.Line 0 1
            fixture.Press Key.L
            fixture.Press Key.L
            fixture.Press Key.V
            fixture.Press Key.W
            test <@ fixture.Surface.GetCopyText() = "lue =" @>

            fixture.Press Key.Escape
            // Line 1 is removed: the caret yields to the old side, and V takes the whole line.
            fixture.Surface.SelectedItem <- fixture.Line 0 0
            fixture.Press(Key.V, RawInputModifiers.Shift)
            test <@ fixture.Surface.GetCopyText() = "value = 1" @>)

    [<Fact>]
    let ``h at the start of the new side crosses to the end of the old side`` () =
        Headless.run (fun () ->
            use fixture = new DiffFixture(DiffLayout.SideBySide)
            fixture.Surface.Focus() |> ignore
            // Line 3 is context, with text on both sides; the caret starts on the new side.
            fixture.Surface.SelectedItem <- fixture.Line 0 2
            fixture.Press Key.D0
            fixture.Press Key.H
            fixture.Press Key.H
            fixture.Press Key.V
            test <@ fixture.Surface.GetCopyText() = "3" @>)

    [<Fact>]
    let ``shift+wheel scrolls long lines sideways and the caret keeps itself in view`` () =
        Headless.run (fun () ->
            use fixture = new DiffFixture(DiffLayout.Unified)
            let point = Point(400.0, 60.0)
            fixture.Window.MouseWheel(point, Vector(0.0, -3.0), RawInputModifiers.Shift)
            Headless.pump ()
            let scrolled = fixture.Surface.HorizontalOffset
            fixture.Window.MouseWheel(point, Vector(0.0, 30.0), RawInputModifiers.Shift)
            Headless.pump ()
            let back = fixture.Surface.HorizontalOffset
            test <@ scrolled > 0.0 && back = 0.0 @>

            fixture.Surface.Focus() |> ignore
            fixture.Surface.SelectedItem <- fixture.Line 0 4
            fixture.Press(Key.D4, RawInputModifiers.Shift, "$")
            let atEnd = fixture.Surface.HorizontalOffset
            fixture.Press Key.D0
            let atStart = fixture.Surface.HorizontalOffset
            test <@ atEnd > 0.0 && atStart = 0.0 @>)

    [<Fact>]
    let ``f t W B e and repeats move the caret like vim`` () =
        Headless.run (fun () ->
            use fixture = new DiffFixture(DiffLayout.Unified)
            fixture.Surface.Focus() |> ignore
            // "call(first.second, third) + x"
            fixture.Surface.SelectedItem <- fixture.Line 0 6
            let selectOne () =
                fixture.Press Key.V
                let text = fixture.Surface.GetCopyText()
                fixture.Press Key.Escape
                text

            fixture.Press(Key.F, symbol = "f")
            fixture.Press(Key.OemComma, symbol = ",")
            let afterF = selectOne ()
            fixture.Press(Key.OemSemicolon, symbol = ";")
            let afterRepeat = selectOne ()
            fixture.Press(Key.T, symbol = "t")
            fixture.Press(Key.D0, RawInputModifiers.Shift, ")")
            let afterT = selectOne ()
            fixture.Press(Key.F, RawInputModifiers.Shift, "F")
            fixture.Press(Key.J, symbol = "(")
            let afterBackF = selectOne ()
            fixture.Press(Key.W, RawInputModifiers.Shift)
            let afterBigW = selectOne ()
            fixture.Press(Key.B, RawInputModifiers.Shift)
            let afterBigB = selectOne ()
            fixture.Press Key.E
            let afterE = selectOne ()
            test <@ [ afterF; afterRepeat; afterT; afterBackF; afterBigW; afterBigB; afterE ] = [ ","; ","; "d"; "("; "t"; "c"; "l" ] @>)

    let private yanked (fixture: DiffFixture) (line: string) =
        let yank = fixture.Surface.LastYank
        if yank.HasValue then
            let struct (_, _, from, until) = yank.Value
            line.Substring(from, until - from)
        else "<nothing>"

    [<Fact>]
    let ``y takes a motion, a find, or an inside or around text object`` () =
        Headless.run (fun () ->
            use fixture = new DiffFixture(DiffLayout.Unified)
            let line = "call(first.second, third) + x"
            fixture.Surface.Focus() |> ignore
            fixture.Surface.SelectedItem <- fixture.Line 0 6
            let results = ResizeArray<string>()
            let keep () = results.Add(yanked fixture line)

            fixture.Press Key.Y; fixture.Press Key.Y; keep ()                                       // yy
            fixture.Press Key.Y; fixture.Press Key.E; keep ()                                       // ye
            fixture.Press(Key.F, symbol = "f"); fixture.Press(Key.OemPeriod, symbol = "."); fixture.Press Key.L // caret to "second"
            fixture.Press Key.Y; fixture.Press Key.I; fixture.Press(Key.W, symbol = "w"); keep ()   // yiw
            fixture.Press Key.Y; fixture.Press Key.I; fixture.Press(Key.D9, RawInputModifiers.Shift, "("); keep () // yi(
            fixture.Press Key.Y; fixture.Press Key.A; fixture.Press(Key.B, symbol = "b"); keep ()   // yab
            fixture.Press Key.Y; fixture.Press(Key.T, symbol = "t"); fixture.Press(Key.D0, RawInputModifiers.Shift, ")"); keep () // yt)
            fixture.Press Key.Y; fixture.Press(Key.D4, RawInputModifiers.Shift, "$"); keep ()      // y$
            // Like vim, each yank leaves the caret at the start of what it copied.
            test <@ List.ofSeq results = [ line; "call"; "second"; "first.second, third"; "(first.second, third)"; "(first.second, third"; "(first.second, third) + x" ] @>)
    [<Fact>]
    let ``counts repeat motions, finds and yanks`` () =
        Headless.run (fun () ->
            use fixture = new DiffFixture(DiffLayout.Unified)
            let line = "call(first.second, third) + x"
            fixture.Surface.Focus() |> ignore
            fixture.Surface.SelectedItem <- fixture.Line 0 6
            let counted (n: int) (key: Key) (modifiers: RawInputModifiers) (symbol: string) =
                fixture.Press(enum<Key> (int Key.D0 + n))
                fixture.Press(key, modifiers, symbol)

            counted 3 Key.W RawInputModifiers.None "w"                 // call ( first → "."
            fixture.Press Key.Y; fixture.Press Key.L
            let afterThreeW = yanked fixture line
            fixture.Press Key.D0
            counted 2 Key.F RawInputModifiers.None "f"; fixture.Press(Key.I, symbol = "i")   // second i: "third"
            fixture.Press Key.Y; fixture.Press Key.L
            let afterTwoFi = yanked fixture line
            fixture.Press Key.D0
            fixture.Press Key.Y; fixture.Press Key.D3; fixture.Press(Key.W, symbol = "w")   // y3w
            let yThreeW = yanked fixture line
            test <@ (afterThreeW, afterTwoFi, yThreeW) = (".", "i", "call(first") @>)

    [<Fact>]
    let ``caret, percent, first non-blank, word under caret and G`` () =
        Headless.run (fun () ->
            use fixture = new DiffFixture(DiffLayout.Unified)
            let line = "    if (a[i] == b) { go(); }"
            fixture.Surface.Focus() |> ignore
            fixture.Surface.SelectedItem <- fixture.Line 0 8
            fixture.Press(Key.D6, RawInputModifiers.Shift, "^")
            fixture.Press Key.Y; fixture.Press Key.L
            let firstNonBlank = yanked fixture line
            fixture.Press(Key.D5, RawInputModifiers.Shift, "%")          // from "if": next bracket is "(", jump to ")"
            fixture.Press Key.Y; fixture.Press Key.L
            let matched = yanked fixture line
            fixture.Press Key.Y; fixture.Press(Key.D5, RawInputModifiers.Shift, "%")   // y% from ")" back to "("
            let yankPercent = yanked fixture line
            test <@ (firstNonBlank, matched, yankPercent) = ("i", ")", "(a[i] == b)") @>

            fixture.Surface.GoToLine 21
            test <@ Object.ReferenceEquals(fixture.Surface.SelectedItem, fixture.Line 0 20) @>)

    [<Fact>]
    let ``H M L focus screen rows and Ctrl+E scrolls without moving the focus`` () =
        Headless.run (fun () ->
            use fixture = new DiffFixture(DiffLayout.Unified)
            fixture.Surface.Focus() |> ignore
            let focusedTop () =
                let row = fixture.Surface.SelectedItem
                fixture.ViewportTop row
            let rowBottomFits () = focusedTop () + fixture.Surface.RowHeightAt(1) <= 300.0 + 0.5

            fixture.Press(Key.H, RawInputModifiers.Shift, "H")
            let top = focusedTop ()
            fixture.Press(Key.L, RawInputModifiers.Shift, "L")
            let bottom = focusedTop ()
            let bottomFits = rowBottomFits ()
            fixture.Press(Key.M, RawInputModifiers.Shift, "M")
            let middle = focusedTop ()
            test <@ top >= 0.0 && top < 40.0 && bottom > 200.0 && bottomFits && middle > top && middle < bottom @>

            let focused = fixture.Surface.SelectedItem
            let offsetBefore = fixture.Scroller.Offset.Y
            fixture.Press(Key.E, RawInputModifiers.Control)
            let scrolled = fixture.Scroller.Offset.Y > offsetBefore
            let kept = Object.ReferenceEquals(fixture.Surface.SelectedItem, focused)
            test <@ scrolled && kept @>)


module MainWindowPaneTests =
    [<Fact>]
    let ``Ctrl+W o maximises the focused pane and Ctrl+W = restores the layout`` () =
        Headless.run (fun () ->
            let window = MainWindow(Width = 1200.0, Height = 800.0)
            window.Show()
            Headless.pump ()
            let rows = window.FindControl<Grid>("MainSplitGrid").RowDefinitions
            let columns = window.FindControl<Grid>("DiffSplitGrid").ColumnDefinitions
            let diff = window.FindControl<DiffSurfaceControl>("DiffRowsListBox")
            let commands = window :> IVimCommands
            let before = (rows[0].Height, rows[2].Height, columns[2].Width)
            diff.Focus() |> ignore
            Headless.pump ()

            commands.PaneCommand Vim.VimPaneCommand.Only
            Headless.pump ()
            let maximised = rows[0].Height.Value = 0.0 && columns[2].Width.Value = 0.0 && diff.IsEffectivelyVisible
            let commitsHidden = not (window.FindControl<CommitSurfaceControl>("CommitListBox").IsEffectivelyVisible)

            commands.PaneCommand Vim.VimPaneCommand.Equalize
            Headless.pump ()
            let after = (rows[0].Height, rows[2].Height, columns[2].Width)
            let commitsBack = window.FindControl<CommitSurfaceControl>("CommitListBox").IsEffectivelyVisible
            window.Close()
            Headless.pump ()
            test <@ maximised && commitsHidden && commitsBack && after = before @>)

module SearchPromptTests =
    let private line number (content: string) : IDiffRowProjection =
        DiffLineProjection({ Type = Models.Context; Content = content; OldLineNo = Some number; NewLineNo = Some number } : Models.DiffLine)

    [<Fact>]
    let ``slash searches the diff from the status bar; Enter keeps it for n, Esc restores`` () =
        Headless.run (fun () ->
            let projection = MainProjection()
            let window = MainWindow(Width = 1200.0, Height = 800.0, DataContext = projection)
            window.Show()
            Headless.pump ()
            let rows = [ line 1 "let alpha = 1"; line 2 "let Beta = 2"; line 3 "alpha + beta"; line 4 "done" ]
            projection.SelectedDiffRows.AddRange rows
            let diff = window.FindControl<DiffSurfaceControl>("DiffRowsListBox")
            let prompt = window.FindControl<TextBox>("SearchPromptBox")
            Headless.pump ()
            diff.Focus() |> ignore
            diff.SelectedItem <- rows[3]
            Headless.pump ()
            let press key symbol =
                window.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, symbol)
                Headless.pump ()

            press Key.Oem2 "/"
            let keys = window.FindControl<TextBlock>("SearchPromptKeys")
            let opened =
                projection.IsSearchPromptOpen && prompt.IsFocused && (prompt.Text = "" || isNull prompt.Text)
                && keys.IsEffectivelyVisible && keys.MaxWidth > 0.0 && keys.Text.Contains "Esc cancel"
            prompt.Text <- "beta"                                   // smartcase: matches "Beta" and "beta"
            Headless.pump ()
            let incremental = Object.ReferenceEquals(projection.SelectedDiffRow, rows[1])
            press Key.Enter null
            let accepted = not projection.IsSearchPromptOpen && diff.IsFocused && projection.CommitFindQuery = "beta"
            press Key.N "n"
            let afterN = Object.ReferenceEquals(projection.SelectedDiffRow, rows[2])

            press Key.Oem2 "/"
            prompt.Text <- "done"
            Headless.pump ()
            let movedWhileTyping = Object.ReferenceEquals(projection.SelectedDiffRow, rows[3])
            press Key.Escape null
            let restored = Object.ReferenceEquals(projection.SelectedDiffRow, rows[2]) && projection.CommitFindQuery = "beta"
            window.Close()
            Headless.pump ()
            test <@ [ opened; incremental; accepted; afterN; movedWhileTyping; restored ] = List.replicate 6 true @>)

module CommitQuickFindTests =
    [<Fact>]
    let ``slash in the commit list focuses and underlines matches; n steps; Esc restores`` () =
        Headless.run (fun () ->
            let projection = MainProjection()
            let commit (subject: string) (author: string) =
                CommitProjection(Subject = subject, Author = author, FullHash = Guid.NewGuid().ToString("N"), Hash = "abc")
            let commits = [ commit "Initial import" "ada"; commit "Fix parser" "bob"; commit "Refactor" "ada"; commit "fix tests" "cy" ]
            for c in commits do projection.Commits.Add c
            let window = MainWindow(Width = 1200.0, Height = 800.0, DataContext = projection)
            window.Show()
            Headless.pump ()
            let list = window.FindControl<CommitSurfaceControl>("CommitListBox")
            let prompt = window.FindControl<TextBox>("SearchPromptBox")
            list.Focus() |> ignore
            Headless.pump ()
            let focused () = commits |> List.findIndex (fun c -> Object.ReferenceEquals(c, list.FocusedCommit))
            let press key symbol =
                window.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, symbol)
                Headless.pump ()

            press Key.Oem2 "/"
            prompt.Text <- "fix"
            Headless.pump ()
            let whileTyping = focused (), not (isNull (box list.QuickFindHighlight)) && not list.QuickFindHighlight.IsEmpty
            press Key.Enter null
            press Key.N "n"
            let afterN = focused ()
            press Key.Oem2 "/"
            prompt.Text <- "refactor"
            Headless.pump ()
            press Key.Escape null
            let afterEscape = focused (), not (isNull (box list.QuickFindHighlight))
            window.Close()
            Headless.pump ()
            test <@ (whileTyping, afterN, afterEscape) = ((1, true), 3, (3, true)) @>)

module HistoryChipTests =
    open Avalonia.Controls.Shapes
    open Avalonia.VisualTree

    [<Fact>]
    let ``branch and file chips show a close cross that clears their filter`` () =
        Headless.run (fun () ->
            let projection = MainProjection()
            let messages = System.Collections.Concurrent.ConcurrentQueue<App.Msg>()
            projection.SetDispatch(fun message -> messages.Enqueue message)
            let window = MainWindow(Width = 1400.0, Height = 800.0, DataContext = projection)
            window.Show()
            let model0, _ = App.init [||]
            projection.Update { model0 with StartupTargets = [ GitStartup.Revision "main"; GitStartup.Path "src/a.fs" ] }
            Headless.pump ()

            let closeOf (chip: Border) =
                let button = chip.GetVisualDescendants() |> Seq.pick (function :? Button as b -> Some b | _ -> None)
                let cross = button.GetVisualDescendants() |> Seq.pick (function :? Path as p -> Some p | _ -> None)
                button, cross
            let branchButton, branchCross = closeOf (window.FindControl<Border>("BranchFilterChip"))
            let fileButton, fileCross = closeOf (window.FindControl<Border>("FileFilterChip"))
            let visible (cross: Path) = cross.IsEffectivelyVisible && cross.Bounds.Width > 0.0 && not (isNull cross.Stroke) && cross.StrokeThickness > 0.0

            branchButton.Command.Execute null
            let afterBranch = messages.ToArray() |> Array.last
            fileButton.Command.Execute null
            let afterFile = messages.ToArray() |> Array.last
            window.Close()
            Headless.pump ()
            test <@ visible branchCross && visible fileCross @>
            test <@ afterBranch = App.Msg.SetHistoryTargets [ GitStartup.Path "src/a.fs" ] @>
            test <@ afterFile = App.Msg.SetHistoryTargets [ GitStartup.Revision "main" ] @>)

    [<Fact>]
    let ``the commit window takes keyboard focus when opened with Ctrl+Shift+C`` () =
        let root = IO.Path.Combine(IO.Path.GetTempPath(), "gitkay-ui-" + Guid.NewGuid().ToString("N"))
        IO.Directory.CreateDirectory root |> ignore
        let git (args: string) =
            let info = Diagnostics.ProcessStartInfo("git", args, WorkingDirectory = root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true)
            use p = Diagnostics.Process.Start info
            p.WaitForExit()
        git "init -q -b main"
        IO.File.WriteAllText(IO.Path.Combine(root, "a.txt"), "one\n")
        try
            let mutable result = (false, false, false, false)
            Headless.run (fun () ->
                let projection = MainProjection(RepositoryPath = IO.Path.Combine(root, ".git"))
                let window = MainWindow(Width = 1200.0, Height = 800.0, DataContext = projection)
                try
                    window.Show()
                    Headless.pump ()
                    window.KeyPressQwerty(PhysicalKey.C, RawInputModifiers.Control ||| RawInputModifiers.Shift)
                    Headless.pump ()
                    match window.OpenCommitWindowForTests with
                    | null -> ()
                    | commit ->
                        try
                            Headless.pump ()
                            let focused = TopLevel.GetTopLevel(commit).FocusManager.GetFocusedElement()
                            commit.KeyTextInput "Fix"
                            Headless.pump ()
                            // The headless platform keeps every window "active", so check what matters: keys land in the commit window.
                            result <- (true, obj.ReferenceEquals(focused, commit.MessageBoxForTests), commit.IsActive, commit.MessageBoxForTests.Text = "Fix")
                        finally
                            commit.Close()
                finally
                    window.Close()
                    Headless.pump ())
            let opened, messageFocused, active, typed = result
            test <@ opened && messageFocused && active && typed @>
        finally
            try IO.Directory.Delete(root, true) with _ -> ()
