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

[<assembly: CollectionBehavior(DisableTestParallelization = true)>]
do ()

type HeadlessTestApp() =
    inherit Application()
    override this.Initialize() = this.Styles.Add(FluentTheme())

    static member BuildAvaloniaApp() =
        AppBuilder
            .Configure<HeadlessTestApp>()
            .UseSkia()
            .UseHeadless(AvaloniaHeadlessPlatformOptions(UseHeadlessDrawing = false))

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
    let ``the status bar takes its space from the diff, leaving the commit list where it is`` () =
        Headless.run (fun () ->
            let projection = MainProjection()
            let window = MainWindow(Width = 1200.0, Height = 800.0, DataContext = projection)
            try
                window.Show()
                window.UpdateLayout()
                Headless.pump ()
                let rows = window.FindControl<Grid>("MainSplitGrid").RowDefinitions
                let diffPane = window.FindControl<Border>("DiffContentPart")
                let commitBefore = rows[0].ActualHeight
                let diffTopBefore = diffPane.Bounds.Top

                // A status message brings the bar in at the bottom of the window.
                projection.Status <- "Staged 3 files"
                window.UpdateLayout()
                Headless.pump ()
                test <@ projection.IsStatusVisible @>

                // The commit list keeps its height, so nothing above the bar moves: only the diff gives up the space.
                test <@ abs (rows[0].ActualHeight - commitBefore) < 0.5 @>
                test <@ abs (diffPane.Bounds.Top - diffTopBefore) < 0.5 @>
            finally
                window.Close()
                Headless.pump ())

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

module CommitColumnTests =

    [<Fact>]
    let ``a row full of branches widens the commit column instead of losing them`` () =
        Headless.run (fun () ->
            let projection = MainProjection()
            let window = MainWindow(Width = 900.0, Height = 700.0, DataContext = projection)
            try
                window.Show()
                Headless.pump ()
                let header = window.FindControl<Grid>("HistoryHeaderGrid")
                let authorColumn = header.ColumnDefinitions[3]
                let plain =
                    { Hash = String.replicate 40 "a"; AuthorName = "A"; AuthorEmail = "a@x"; Timestamp = 1L; Parents = []
                      Subject = "A commit with a reasonably long subject"; Message = "body"; Refs = [] } : Models.Commit
                let model0, _ = App.init [||]
                projection.Update { model0 with Commits = Graph.calculateLanes [ plain ]; ShowBranchRefs = true }
                Headless.pump ()
                let before = authorColumn.ActualWidth


                let manyRefs =
                    { plain with
                        Refs =
                            [ for name in [ "main"; "feature/login-and-signup"; "feature/user-preferences"; "release/2026-09-candidate"
                                            "hotfix/urgent-production-fix"; "experiment/rendering-rewrite" ] ->
                                ({ Name = name; Kind = Models.CommitRefKind.Branch; IsCurrentHead = false } : Models.CommitRef) ] }
                projection.Update { model0 with Commits = Graph.calculateLanes [ manyRefs ]; ShowBranchRefs = true }
                for _ in 1..10 do
                    window.UpdateLayout()
                    Headless.pump ()
                let withRefs = authorColumn.ActualWidth
                // The badges are wider than the commit column has spare, so the author column lends it space.
                test <@ withRefs < before && withRefs >= authorColumn.MinWidth @>
            finally
                window.Close()
                Headless.pump ())

module SyntaxHighlightingTests =
    let private kinds (flavour: SyntaxFlavour) (text: string) =
        SyntaxHighlighting.Tokenize(text, flavour) |> Seq.map (fun token -> token.Text, token.Kind) |> List.ofSeq

    [<Fact>]
    let ``markdown prose keeps its own colours, not the code ones`` () =
        // As code, "# Heading" is a comment and "Type" and "for" are keywords: that is what made READMEs look scrambled.
        let asCode = kinds SyntaxFlavour.Code "# Heading for Type"
        test <@ asCode |> List.exists (fun (_, kind) -> kind = HighlightKind.Comment) @>

        let heading = kinds SyntaxFlavour.Markdown "# Heading for Type"
        test <@ heading = [ "# Heading for Type", HighlightKind.Keyword ] @>

        // Ordinary prose stays plain, whatever words it uses.
        test <@ kinds SyntaxFlavour.Markdown "Use let and for when you type." = [ "Use let and for when you type.", HighlightKind.Plain ] @>

        // Markdown's own marks are what gets colour.
        test <@ kinds SyntaxFlavour.Markdown "- a `code` span" = [ "- ", HighlightKind.Comment; "a ", HighlightKind.Plain; "`code`", HighlightKind.String; " span", HighlightKind.Plain ] @>
        test <@ kinds SyntaxFlavour.Markdown "See [docs](https://example.com) here" |> List.contains ("(https://example.com)", HighlightKind.TypeName) @>
        test <@ kinds SyntaxFlavour.Markdown "> quoted line" |> List.head = ("> ", HighlightKind.Comment) @>

    [<Fact>]
    let ``a file's extension picks how its lines are coloured`` () =
        test <@ SyntaxHighlighting.FlavourFor "docs/README.md" = SyntaxFlavour.Markdown @>
        test <@ SyntaxHighlighting.FlavourFor "notes.txt" = SyntaxFlavour.PlainText @>
        test <@ SyntaxHighlighting.FlavourFor "src/App.fs" = SyntaxFlavour.Code @>
        // Plain text is left alone entirely.
        test <@ kinds SyntaxFlavour.PlainText "# not a comment" = [ "# not a comment", HighlightKind.Plain ] @>

module PaneChromeTests =
    let private styled effect highlight border style =
        PaneChrome.Settings(4.0, true, highlight, effect, PaneEffectColor.AccentEffectColor, PaneEffectIntensity.FullIntensity,
                            border, style, PaneEffectColor.PinkEffectColor, 3.0, border)

    let private settings effect highlight border = styled effect highlight border PaneBorderStyle.SubtleBorder

    [<Fact>]
    let ``pane chrome follows the focused pane, not the pointer`` () =
        Headless.run (fun () ->
            // A pane of two rows: a header bar, and the content under it.
            let header = Border(Height = 30.0, Background = Media.Brushes.Gray)
            let content = Border(Background = Media.Brushes.Gray)
            Grid.SetRow(header, 0)
            Grid.SetRow(content, 1)
            let effect = Border()
            Grid.SetRow(effect, 0)
            Grid.SetRowSpan(effect, 2)
            let grid = Grid(RowDefinitions = RowDefinitions("Auto,*"))
            grid.Children.Add header
            grid.Children.Add content
            grid.Children.Add effect
            let window = Window(Width = 200.0, Height = 200.0, Content = grid)
            // The app's palette isn't loaded under the bare test theme, so the brushes each style asks for go here.
            window.Resources.Add("GitKayPaneSubtleBorderBrush", Media.SolidColorBrush(Media.Color.Parse "#2B333C"))
            window.Resources.Add("GitKayBorderBrush", Media.SolidColorBrush(Media.Color.Parse "#3C444D"))
            let chrome = PaneChrome()
            let hovered = ResizeArray<string>()
            chrome.add_PaneHovered (fun key -> hovered.Add key)
            chrome.Add("diff", effect, header, content)
            chrome.Update(settings PaneFocusEffect.PaneGlow false false)
            let separator = Border()
            chrome.AddSeparator separator
            try
                window.Show()
                window.UpdateLayout()
                Headless.pump ()

                // Pointing at a pane does nothing on its own: the window says which pane has the keys.
                window.MouseMove(Point(100.0, 100.0))
                Headless.pump ()
                test <@ effect.BoxShadow.Count = 0 @>

                // The pane is hovered over the whole of itself: its header, and the padding the inner control leaves.
                test <@ List.ofSeq hovered = [ "diff" ] @>
                window.MouseMove(Point(100.0, header.Bounds.Top + 2.0))
                window.MouseMove(Point(100.0, 100.0))
                Headless.pump ()
                test <@ List.ofSeq hovered = [ "diff" ] @>

                chrome.SetFocused "diff"
                Headless.pump ()
                test <@ effect.BoxShadow.Count > 0 @>

                // A glow is light, not a frame: the outline belongs to the border setting alone.
                test <@ effect.BorderThickness.Top = 0.0 @>

                // Highlight is the switch that draws one, and it is independent of the effect.
                chrome.Update(settings PaneFocusEffect.PaneGlow true false)
                Headless.pump ()
                test <@ effect.BorderThickness.Top = 1.0 && effect.BoxShadow.Count > 0 @>

                chrome.Update(settings PaneFocusEffect.NoPaneEffect true false)
                Headless.pump ()
                test <@ effect.BorderThickness.Top = 1.0 && effect.BoxShadow.Count = 0 @>

                // Focus elsewhere takes all of it away.
                chrome.SetFocused null
                Headless.pump ()
                test <@ effect.BorderThickness.Top = 0.0 && effect.BoxShadow.Count = 0 @>

                // Bordered panes are outlined whether focused or not, and the hairline between panes steps aside.
                test <@ separator.Opacity = 1.0 @>
                chrome.Update(settings PaneFocusEffect.PaneGlow false true)
                Headless.pump ()
                test <@ effect.BorderThickness.Top = 1.0 && separator.Opacity = 0.0 @>

                // Each border style is its own edge: subtle, normal, and one the user chose outright.
                let brushOf () = effect.BorderBrush |> string
                chrome.Update(styled PaneFocusEffect.PaneGlow false true PaneBorderStyle.SubtleBorder)
                let subtle = brushOf ()
                chrome.Update(styled PaneFocusEffect.PaneGlow false true PaneBorderStyle.NormalBorder)
                let normal = brushOf ()
                test <@ subtle <> normal @>

                chrome.Update(styled PaneFocusEffect.PaneGlow false true PaneBorderStyle.CustomBorder)
                test <@ brushOf () <> normal && effect.BorderThickness.Top = 3.0 @>
            finally
                window.Close()
                Headless.pump ())

    [<Fact>]
    let ``a header bar and the content under it leave no gap between them`` () =
        Headless.run (fun () ->
            let header = Border()
            let content = Border()
            let overlay = Border()
            Grid.SetRow(header, 0)
            Grid.SetRow(content, 1)
            Grid.SetRow(overlay, 1)
            let chrome = PaneChrome()
            chrome.Add("diff", Border(), header, content, overlay)
            chrome.Update(settings PaneFocusEffect.PaneGlow false false)
            // The dark band between the bar and the content was the window showing through two facing margins.
            test <@ header.Margin = Thickness(4.0, 4.0, 4.0, 0.0) @>
            test <@ content.Margin = Thickness(4.0, 0.0, 4.0, 4.0) @>
            // The focus overlay sits over the content, so it is inset the same way.
            test <@ overlay.Margin = content.Margin @>)

    [<Fact>]
    let ``a pane of one part keeps the gap all the way round`` () =
        Headless.run (fun () ->
            let content = Border()
            let chrome = PaneChrome()
            chrome.Add("commits", Border(), content)
            chrome.Update(settings PaneFocusEffect.PaneGlow false false)
            test <@ content.Margin = Thickness 4.0 @>)

module FatalErrorDialogTests =

    [<Fact>]
    let ``fatal error dialog shows the heading and diagnostic details`` () =
        Headless.run (fun () ->
            let dialog = FatalErrorDialog("GitKay hit an unrecoverable UI error.", "System.InvalidOperationException: broken")
            try
                dialog.Show()
                Headless.pump ()
                let message = dialog.FindControl<TextBlock>("MessageText")
                let details = dialog.FindControl<TextBox>("DetailsText")
                test <@ message.Text = "GitKay hit an unrecoverable UI error." @>
                test <@ details.Text.Contains("InvalidOperationException") @>
            finally
                dialog.Close()
                Headless.pump ())

module RenderedMarkdownVisualTests =
    [<Fact>]
    let ``rich rendered markdown lays out compactly at viewport width`` () =
        Headless.run (fun () ->
            let markdown = """# Rendered Markdown

Plain text with *emphasis*, **strong**, ~~removed~~, `code`, and [a link](https://example.com).

> A quoted paragraph should remain visually distinct.

- first item
- [x] completed task

| Name | State | Notes |
| --- | --- | --- |
| renderer | ready | shared surface |
| images | loading | anchored layout |

```fsharp
let answer = 40 + 2
printfn $"value = {answer}"
```
"""
            let rows =
                GitKay.Core.Markdown.renderDocument markdown
                |> Seq.map (fun row -> RenderedMarkdownRowProjection(row) :> IDiffRowProjection)
                |> AvaloniaList
            let surface = DiffSurfaceControl(ItemsSource = rows, Width = 920.0)
            let scroller = ScrollViewer(Width = 920.0, Height = 650.0, Content = surface)
            let window = Window(Width = 920.0, Height = 650.0, Content = scroller)
            try
                window.Show()
                Headless.pump ()
                let bitmap = window.CaptureRenderedFrame()
                test <@ bitmap.PixelSize.Width = 920 && bitmap.PixelSize.Height = 650 @>
                test <@ surface.DesiredSize.Height > 200.0 && surface.DesiredSize.Height <= 400.0 @>
            finally
                window.Close()
                Headless.pump ())

    [<Fact>]
    let ``wrapped rendered prose hit testing maps each visual line to its exact text range`` () =
        Headless.run (fun () ->
            let markdown = String.replicate 12 "wrapped words remain selectable "
            let rows = Markdown.renderDocument markdown |> Seq.map (RenderedMarkdownRowProjection >> fun row -> row :> IDiffRowProjection) |> AvaloniaList
            let surface = DiffSurfaceControl(ItemsSource = rows, Width = 280.0)
            let window = Window(Width = 280.0, Height = 180.0, Content = surface)
            try
                window.Show()
                Headless.pump ()
                window.CaptureRenderedFrame() |> ignore
                let struct (firstRow, firstCharacter) = surface.TextPositionAtForTest(Point(28.0, 12.0))
                let struct (secondRow, secondCharacter) = surface.TextPositionAtForTest(Point(28.0, 34.0))
                test <@ firstRow = 0 && secondRow = 0 && secondCharacter > firstCharacter @>
            finally
                window.Close()
                Headless.pump ())

    [<Fact>]
    let ``side by side rendered prose wraps inside both columns`` () =
        Headless.run (fun () ->
            let oldText = String.replicate 18 "old prose wraps inside the left column "
            let newText = String.replicate 18 "new prose wraps inside the right column "
            let rows =
                GitKay.Core.Markdown.renderDiff oldText newText
                |> Seq.map (fun row -> RenderedMarkdownRowProjection(row) :> IDiffRowProjection)
                |> AvaloniaList
            let surface = DiffSurfaceControl(ItemsSource = rows, DiffLayout = DiffLayout.SideBySide, Width = 600.0)
            let window = Window(Width = 600.0, Height = 350.0, Content = ScrollViewer(Content = surface))
            try
                window.Show()
                Headless.pump ()
                test <@ surface.RowHeightAt(0) > 180.0 @>
            finally
                window.Close()
                Headless.pump ())

    [<Fact>]
    let ``preview action fires for an added-only markdown file`` () =
        Headless.run (fun () ->
            let summary : GitService.DiffFileSummary =
                { OldPath = "/dev/null"; NewPath = "README.md"; DisplayPath = FileChange.displayPath "/dev/null" "README.md" }
            let file = DiffFileProjection(summary)
            let model : Models.FileDiff =
                { OldPath = "/dev/null"; NewPath = "README.md"; NewLineCount = Some 1
                  Hunks = [ { Header = "@@ -0,0 +1 @@"; Lines = [ { Type = Models.Added; Content = "# Added"; OldLineNo = None; NewLineNo = Some 1 } ] } ] }
            file.ApplyContent(model)
            let rows = AvaloniaList<IDiffRowProjection>([ file.Header :> IDiffRowProjection ])
            let surface = DiffSurfaceControl(ItemsSource = rows, Width = 500.0)
            let window = Window(Width = 500.0, Height = 100.0, Content = surface)
            let mutable requested : DiffFileProjection option = None
            surface.PreviewRequested.Add(fun selected -> requested <- Some selected)
            try
                window.Show()
                Headless.pump ()
                // Card inset + chevron + path width + padding places the preview action immediately after README.md.
                window.MouseDown(Point(202.0, 31.0), MouseButton.Left)
                window.MouseUp(Point(202.0, 31.0), MouseButton.Left)
                Headless.pump ()
                test <@ requested = Some file @>
            finally
                window.Close()
                Headless.pump ())

    [<Fact>]
    let ``blocked remote image placeholder requests an explicit load when clicked`` () =
        Headless.run (fun () ->
            let rows =
                Markdown.renderDocument "![diagram](https://example.com/diagram.png)"
                |> Seq.map (fun row -> RenderedMarkdownRowProjection(row) :> IDiffRowProjection)
                |> AvaloniaList
            let surface = DiffSurfaceControl(ItemsSource = rows, Width = 500.0)
            let window = Window(Width = 500.0, Height = 140.0, Content = surface)
            let mutable requested = ""
            surface.RenderedLinkRequested.Add(fun link -> requested <- link)
            try
                window.Show()
                Headless.pump ()
                window.MouseDown(Point(40.0, 25.0), MouseButton.Left)
                window.MouseUp(Point(40.0, 25.0), MouseButton.Left)
                Headless.pump ()
                test <@ requested = "gitkay-load-image:https://example.com/diagram.png" @>
            finally
                window.Close()
                Headless.pump ())

    [<Fact>]
    let ``whole file popup projection switches from source to rendered markdown`` () =
        Headless.run (fun () ->
            let main = MainProjection()
            let target = FileTarget("README.md", "README.md", "README.md", null)
            let projection = WholeFileProjection(main, "abc123", "Docs", target)
            let line : Models.DiffLine = { Type = Models.Context; Content = "# Preview works"; OldLineNo = Some 1; NewLineNo = Some 1 }
            let file : Models.FileDiff =
                { OldPath = "README.md"; NewPath = "README.md"
                  Hunks = [ { Header = "@@ -1 +1 @@"; Lines = [ line ] } ]; NewLineCount = Some 1 }
            let rendered : RenderedMarkdownContent =
                { File = file
                  DiffRows = Markdown.renderDiff "# Preview works" "# Preview works"
                  OldRows = Markdown.renderDocument "# Preview works"
                  NewRows = Markdown.renderDocument "# Preview works"
                  Images = [] }
            let payload : GitService.WholeFilePayload = { File = file; Rendered = Some rendered; ImageBytes = None }
            projection.ApplyPayload(payload)
            projection.TogglePreviewCommand.Execute(null)
            test <@ projection.IsMarkdownPreview && projection.Rows |> Seq.exists (fun row -> row :? RenderedMarkdownRowProjection) @>)

    [<Fact>]
    let ``old and new layouts use their unmarked rendered documents`` () =
        let oldSource, newSource = "# Old\n\nold text", "# New\n\nnew text"
        let fileModel : Models.FileDiff = { OldPath = "README.md"; NewPath = "README.md"; Hunks = []; NewLineCount = None }
        let content : RenderedMarkdownContent =
            { File = fileModel; DiffRows = Markdown.renderDiff oldSource newSource
              OldRows = Markdown.renderDocument oldSource; NewRows = Markdown.renderDocument newSource; Images = [] }
        let summary : GitService.DiffFileSummary =
            { OldPath = "README.md"; NewPath = "README.md"; DisplayPath = "README.md" }
        let file = DiffFileProjection(summary)
        file.ApplyContent(fileModel)
        file.ApplyRenderedContent(content)
        let oldRows = ResizeArray<IDiffRowProjection>()
        let newRows = ResizeArray<IDiffRowProjection>()
        DiffRowBuilder.AppendFile(oldRows, file, DiffLayout.OldFile)
        DiffRowBuilder.AppendFile(newRows, file, DiffLayout.NewFile)
        let oldRendered = oldRows |> Seq.choose (function :? RenderedMarkdownRowProjection as row -> Some row | _ -> None) |> Seq.toList
        let newRendered = newRows |> Seq.choose (function :? RenderedMarkdownRowProjection as row -> Some row | _ -> None) |> Seq.toList
        test <@ oldRendered |> List.forall (fun row -> row.Kind = MarkdownChangeKind.Unchanged) @>
        test <@ newRendered |> List.forall (fun row -> row.Kind = MarkdownChangeKind.Unchanged) @>
        test <@ oldRendered.Head.Text = "Old" && newRendered.Head.Text = "New" @>

    [<Fact>]
    let ``side by side rendered markdown keeps both code versions in one stable row`` () =
        Headless.run (fun () ->
            let oldText = "# Guide\n\nUse the **old** workflow for setup.\n\n```fsharp\nlet value = 1\n```"
            let newText = "# Guide\n\nUse the **new improved** workflow for setup.\n\n```fsharp\nlet value = 2\n```"
            let rows =
                GitKay.Core.Markdown.renderDiff oldText newText
                |> Seq.map (fun row -> RenderedMarkdownRowProjection(row) :> IDiffRowProjection)
                |> AvaloniaList
            let surface = DiffSurfaceControl(ItemsSource = rows, DiffLayout = DiffLayout.SideBySide, Width = 920.0)
            let window = Window(Width = 920.0, Height = 350.0, Content = ScrollViewer(Content = surface))
            try
                window.Show()
                Headless.pump ()
                let bitmap = window.CaptureRenderedFrame()
                test <@ bitmap.PixelSize.Width = 920 @>
                test <@ rows.Count = 3 && surface.DesiredSize.Height < 180.0 @>
            finally
                window.Close()
                Headless.pump ())

    [<Fact>]
    let ``a markdown anchor deep in a later file restores inside that file`` () =
        Headless.run (fun () ->
            use fixture = new DiffFixture(DiffLayout.Unified)
            // Every file numbers its lines from 1, so a bare line number matches all three.
            let anchored = fixture.Line 2 12
            fixture.Scroller.Offset <- Vector(0.0, fixture.ViewportTop anchored + fixture.Scroller.Offset.Y)
            Headless.pump ()
            let before = fixture.Scroller.Offset.Y
            let anchor = fixture.Surface.CaptureMarkdownViewAnchor()
            fixture.Surface.RestoreMarkdownViewAnchor anchor
            Headless.pump ()
            let after = fixture.Scroller.Offset.Y
            let anchoredFile = if anchor.HasValue then anchor.Value.File else "<none>"
            test <@ anchoredFile = "c.txt" @>
            test <@ Math.Round(after, 1) = Math.Round(before, 1) @>)

module FileRowLayoutTests =
    open Avalonia.Controls.Primitives
    open Avalonia.Layout

    /// A changed-file row at a fixed width: a path, then the counts, then the change graph.
    let private row (width: float) (path: string) =
        let name = TextBlock(Text = path, TextWrapping = Media.TextWrapping.NoWrap, TextTrimming = Media.TextTrimming.CharacterEllipsis)
        let counts = TextBlock(Text = "+12 −3")
        let graph = DiffStatBar(Added = 12, Removed = 3, IsKnown = true, BlockSize = 5.0)
        let layout = FileRowLayout(Spacing = 7.0)
        layout.Children.Add name
        layout.Children.Add counts
        layout.Children.Add graph
        let window = Window(Width = width, Height = 60.0, Content = Border(Width = width, Child = layout))
        window, layout, name, counts, graph

    [<Fact>]
    let ``a short file name leaves room for the change graph`` () =
        Headless.run (fun () ->
            let window, _, name, counts, graph = row 400.0 "a.txt"
            try
                window.Show()
                Headless.pump ()
                test <@ graph.IsVisible && counts.IsVisible @>
                // The name is not trimmed: it got everything it asked for.
                test <@ name.Bounds.Width >= name.DesiredSize.Width - 0.5 @>
            finally
                window.Close()
                Headless.pump ())

    [<Fact>]
    let ``a file name that needs the room drops the change graph and keeps the counts`` () =
        Headless.run (fun () ->
            let longPath = "src/GitKay.UI/" + String.replicate 12 "verylongfolder/" + "File.fs"
            let window, _, _, counts, graph = row 260.0 longPath
            try
                window.Show()
                Headless.pump ()
                test <@ not graph.IsVisible @>
                test <@ counts.IsVisible @>
            finally
                window.Close()
                Headless.pump ())

    [<Fact>]
    let ``the dropped graph stays dropped instead of flickering back in`` () =
        Headless.run (fun () ->
            let longPath = "src/GitKay.UI/" + String.replicate 12 "verylongfolder/" + "File.fs"
            let window, layout, _, _, graph = row 260.0 longPath
            try
                window.Show()
                Headless.pump ()
                let first = graph.IsVisible
                // A hidden child measures as zero wide; deciding on that would make it fit again every pass.
                for _ in 1..3 do
                    layout.InvalidateMeasure()
                    Headless.pump ()
                test <@ [ first; graph.IsVisible ] = [ false; false ] @>
            finally
                window.Close()
                Headless.pump ())

    [<Fact>]
    let ``dropping the graph does not change the row's height`` () =
        Headless.run (fun () ->
            let longPath = "src/GitKay.UI/" + String.replicate 12 "verylongfolder/" + "File.fs"
            let measure width path =
                let window, layout, _, _, _ = row width path
                try
                    window.Show()
                    Headless.pump ()
                    Math.Round(layout.Bounds.Height, 1)
                finally
                    window.Close()
                    Headless.pump ()
            // The rows sit in one list: a file whose graph dropped out must not be a different height from its neighbours.
            let withGraph = measure 400.0 "a.txt"
            let withoutGraph = measure 260.0 longPath
            test <@ withGraph = withoutGraph @>)

    [<Fact>]
    let ``the graph costs more to bring back than it does to keep, so a boundary width settles`` () =
        Headless.run (fun () ->
            let name = TextBlock(Text = "src/GitKay.UI/SomeModeratelyLongFileName.fs", TextWrapping = Media.TextWrapping.NoWrap)
            let counts = TextBlock(Text = "+12 \u22123")
            let graph = DiffStatBar(Added = 12, Removed = 3, IsKnown = true, BlockSize = 5.0)
            let layout = FileRowLayout(Spacing = 7.0)
            layout.Children.Add name
            layout.Children.Add counts
            layout.Children.Add graph
            let host = Border(Child = layout)
            let window = Window(Width = 700.0, Height = 60.0, Content = host)
            try
                window.Show()
                Headless.pump ()
                let visibleAt w =
                    host.Width <- w
                    window.UpdateLayout()
                    Headless.pump ()
                    graph.IsVisible

                // Narrowing from wide: the width at which the graph gives up its place.
                let widths = [ 500.0 .. -1.0 .. 200.0 ]
                let hidesAt = widths |> List.find (fun w -> not (visibleAt w))
                // Widening from there: the width at which it comes back.
                let showsAt = [ hidesAt .. 1.0 .. 500.0 ] |> List.find visibleAt

                // A single shared boundary would make these equal, and a splitter resting there would flicker.
                test <@ showsAt - hidesAt >= 8.0 @>
            finally
                window.Close()
                Headless.pump ())

module ChromeBrushTests =
    /// A control carrying the two theme tones the chrome is mixed from.
    let private host () =
        let border = Border()
        border.Resources.Add("GitKayWindowBrush", Media.SolidColorBrush(Media.Color.FromRgb(0x0Duy, 0x11uy, 0x17uy)))
        border.Resources.Add("GitKaySurfaceBrush", Media.SolidColorBrush(Media.Color.FromRgb(0x1Buy, 0x22uy, 0x2Cuy)))
        border

    let private colorOf (brush: Media.IBrush option) =
        match brush with
        | Some (:? Media.ISolidColorBrush as solid) -> Some solid.Color
        | _ -> None

    let private resolve background color =
        ChromeBrush.Resolve(host (), background, color) |> Option.ofObj |> colorOf

    [<Fact>]
    let ``the default chrome is the surface tone, not the window behind it`` () =
        Headless.run (fun () ->
            let surface = resolve ChromeBackground.SurfaceChrome PaneEffectColor.AccentEffectColor
            let window = resolve ChromeBackground.TransparentChrome PaneEffectColor.AccentEffectColor
            test <@ surface = Some (Media.Color.FromRgb(0x1Buy, 0x22uy, 0x2Cuy)) @>
            test <@ window = Some (Media.Color.FromRgb(0x0Duy, 0x11uy, 0x17uy)) @>
            // The point of the setting: chrome that does not share the content's background.
            test <@ surface <> window @>)

    [<Fact>]
    let ``a tinted chrome is opaque and leans towards the chosen colour`` () =
        Headless.run (fun () ->
            let window = Media.Color.FromRgb(0x0Duy, 0x11uy, 0x17uy)
            match resolve ChromeBackground.TintedChrome PaneEffectColor.GreenEffectColor with
            | Some tinted ->
                // Opaque, or the content would scroll through the bar; and greener than the background it sits on.
                let alpha = int tinted.A
                let greenGain = int tinted.G - int window.G
                let redGain = int tinted.R - int window.R
                let differs = tinted <> window
                test <@ alpha = 255 @>
                test <@ greenGain > redGain @>
                test <@ differs @>
            | None -> failwith "no tinted brush")

module HashCopyTests =
    let private commit () =
        CommitProjection(
            FullHash = "74f3490a58e81f4ece0f19ad470bf0495f648604",
            Hash = "74f3490a",
            Subject = "Prepare version 0.11.0")

    /// Clicks the middle of a control, as a pointer press and release in the same place.
    let private click (window: Window) (target: Control) =
        let middle = Point(target.Bounds.Width / 2.0, target.Bounds.Height / 2.0)
        let position = target.TranslatePoint(middle, window)
        if not position.HasValue then failwith "the hash is not in the window"
        window.MouseDown(position.Value, MouseButton.Left)
        window.MouseUp(position.Value, MouseButton.Left)
        Headless.pump ()

    [<Fact>]
    let ``clicking the hash in the diff header copies the whole commit hash`` () =
        Headless.run (fun () ->
            let projection = MainProjection(SelectedCommit = commit ())
            let window = MainWindow(Width = 1200.0, Height = 800.0, DataContext = projection)
            try
                window.Show()
                window.UpdateLayout()
                Headless.pump ()
                let hash = window.FindControl<SelectableTextBlock>("HeaderHashText")
                // The header shows the short hash; the whole one is what gets copied.
                test <@ hash.Text = "74f3490a" @>
                click window hash
                // Read back out of the clipboard, not just off the status bar.
                let copied = Avalonia.Input.Platform.ClipboardExtensions.TryGetTextAsync(window.Clipboard).GetAwaiter().GetResult()
                test <@ copied = "74f3490a58e81f4ece0f19ad470bf0495f648604" @>
                test <@ projection.Status = "Copied: 74f3490a58e81f4ece0f19ad470bf0495f648604" @>
            finally
                window.Close()
                Headless.pump ())

    [<Fact>]
    let ``clicking the full hash in the commit details copies it too`` () =
        Headless.run (fun () ->
            let projection = MainProjection(SelectedCommit = commit (), IsCommitDetailsExpanded = true)
            let window = MainWindow(Width = 1200.0, Height = 800.0, DataContext = projection)
            try
                window.Show()
                window.UpdateLayout()
                Headless.pump ()
                let hash = window.FindControl<SelectableTextBlock>("DetailHashText")
                click window hash
                test <@ projection.Status = "Copied: 74f3490a58e81f4ece0f19ad470bf0495f648604" @>
            finally
                window.Close()
                Headless.pump ())

    [<Fact>]
    let ``a click on the hash does not also open the commit details`` () =
        Headless.run (fun () ->
            let projection = MainProjection(SelectedCommit = commit ())
            let window = MainWindow(Width = 1200.0, Height = 800.0, DataContext = projection)
            try
                window.Show()
                window.UpdateLayout()
                Headless.pump ()
                let expandedBefore = projection.IsCommitDetailsExpanded
                click window (window.FindControl<SelectableTextBlock>("HeaderHashText"))
                // Copying is the whole of it: the bar the hash sits in does not toggle underneath the click.
                test <@ [ expandedBefore; projection.IsCommitDetailsExpanded ] = [ false; false ] @>
            finally
                window.Close()
                Headless.pump ())

module CommitContextMenuTests =
    let private headers (menu: ContextMenu) =
        menu.Items
        |> Seq.choose (function :? MenuItem as item -> Some (string item.Header) | _ -> None)
        |> List.ofSeq

    [<Fact>]
    let ``the commit row's right-click menu offers copy hash and subject`` () =
        Headless.run (fun () ->
            let commit =
                CommitProjection(
                    FullHash = "74f3490a58e81f4ece0f19ad470bf0495f648604",
                    Hash = "74f3490a",
                    Subject = "Prepare version 0.11.0")
            let commits = AvaloniaList<CommitProjection>([ commit ])
            let surface = CommitSurfaceControl(ItemsSource = commits, SelectedItem = commit)
            let copied = ResizeArray<string>()
            surface.add_CopyRequested (fun text -> copied.Add text)
            let window = Window(Width = 900.0, Height = 300.0, Content = surface)
            try
                window.Show()
                Headless.pump ()
                let menu = surface.BuildContextMenu()
                test <@ headers menu |> List.contains "Copy commit hash" @>
                test <@ headers menu |> List.contains "Copy commit subject" @>

                // The whole hash goes to the clipboard, not the shortened one the row shows.
                let item = menu.Items |> Seq.pick (function
                    | :? MenuItem as item when string item.Header = "Copy commit hash" -> Some item
                    | _ -> None)
                item.RaiseEvent(Interactivity.RoutedEventArgs(MenuItem.ClickEvent))
                Headless.pump ()
                test <@ List.ofSeq copied = [ "74f3490a58e81f4ece0f19ad470bf0495f648604" ] @>
            finally
                window.Close()
                Headless.pump ())

    [<Fact>]
    let ``the uncommitted changes row offers no commit to copy`` () =
        Headless.run (fun () ->
            let working = CommitProjection(IsWorkingTree = true, Subject = "Uncommitted changes")
            let commits = AvaloniaList<CommitProjection>([ working ])
            let surface = CommitSurfaceControl(ItemsSource = commits, SelectedItem = working)
            let window = Window(Width = 900.0, Height = 300.0, Content = surface)
            try
                window.Show()
                Headless.pump ()
                // There is no commit there: copying its hash would copy an empty string.
                test <@ headers (surface.BuildContextMenu()) = [ "Open commit window…" ] @>
            finally
                window.Close()
                Headless.pump ())

module CommitVocabularyTests =
    /// Every visible label in the window, so a word used for two different things shows up here.
    let private labels (window: Window) =
        Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window)
        |> Seq.choose (fun (visual: Avalonia.Visual) ->
            match visual with
            | :? TextBlock as text -> Option.ofObj text.Text
            | _ -> None)
        |> List.ofSeq

    [<Fact>]
    let ``subject, hash and message each have one name`` () =
        Headless.run (fun () ->
            let projection =
                MainProjection(
                    SelectedCommit =
                        CommitProjection(
                            FullHash = "74f3490a58e81f4ece0f19ad470bf0495f648604",
                            Hash = "74f3490a",
                            Subject = "Prepare version 0.11.0",
                            Message = "Prepare version 0.11.0\n\nWith a body under it."),
                    IsCommitDetailsExpanded = true)
            let window = MainWindow(Width = 1200.0, Height = 800.0, DataContext = projection)
            try
                window.Show()
                window.UpdateLayout()
                Headless.pump ()
                let shown = labels window
                // The column shows the message's first line, which git calls the subject.
                test <@ shown |> List.contains "SUBJECT" @>
                test <@ shown |> List.contains "COMMIT" |> not @>
                // The details row labelled "Commit" held a hash, which the column beside it already calls HASH.
                test <@ shown |> List.contains "Hash" @>
                test <@ shown |> List.contains "Message" @>
            finally
                window.Close()
                Headless.pump ())

module ChangesOnlyToggleTests =
    /// A rendered markdown file, as the diff pane holds one.
    let private renderedFile () =
        // Long enough that filtering has unchanged blocks to collapse: changesOnly keeps 2 either side of a change.
        let unchanged = [ for i in 1 .. 8 -> $"Paragraph {i} stays exactly as it was." ] |> String.concat "\n\n"
        let source = $"# Guide\n\n{unchanged}\n\nOld line."
        let current = $"# Guide\n\n{unchanged}\n\nNew line."
        let model : Models.FileDiff = { OldPath = "README.md"; NewPath = "README.md"; Hunks = []; NewLineCount = None }
        let content : RenderedMarkdownContent =
            { File = model; DiffRows = Markdown.renderDiff source current
              OldRows = Markdown.renderDocument source; NewRows = Markdown.renderDocument current; Images = [] }
        let file = DiffFileProjection({ OldPath = "README.md"; NewPath = "README.md"; DisplayPath = "README.md" } : GitService.DiffFileSummary)
        file.ApplyContent model
        file.ApplyRenderedContent content
        file

    [<Fact>]
    let ``the changes-only icon is offered only while the file is rendered`` () =
        Headless.run (fun () ->
            let file = renderedFile ()
            let rows = AvaloniaList<IDiffRowProjection>([ file.Header :> IDiffRowProjection ])
            let surface = DiffSurfaceControl(ItemsSource = rows, Width = 500.0)
            let window = Window(Width = 500.0, Height = 120.0, Content = surface)
            let asked = ResizeArray<DiffFileProjection>()
            surface.add_ChangesOnlyRequested (fun _ f -> asked.Add f)
            try
                window.Show()
                Headless.pump ()
                // The icons sit after the path, whose width depends on the font: find the one that answers.
                let clickAt x =
                    let point = Point(x, 31.0)
                    window.MouseDown(point, MouseButton.Left)
                    window.MouseUp(point, MouseButton.Left)
                    Headless.pump ()
                let hit =
                    [ 100.0 .. 2.0 .. 300.0 ]
                    |> List.tryFind (fun x -> asked.Clear(); clickAt x; asked.Count > 0)
                test <@ hit.IsSome @>

                // Source mode has no unchanged sections to hide, so the icon is not there to click.
                file.ClearRendered()
                surface.InvalidateVisual()
                Headless.pump ()
                asked.Clear()
                for x in [ 100.0 .. 2.0 .. 300.0 ] do clickAt x
                test <@ List.ofSeq asked = [] @>
            finally
                window.Close()
                Headless.pump ())

    [<Fact>]
    let ``toggling changes only drops the unchanged blocks and puts them back`` () =
        Headless.run (fun () ->
            let projection = MainProjection()
            let file = renderedFile ()
            projection.SelectedDiffFiles.Add file
            projection.RefreshDiffRows()
            let whole = projection.SelectedDiffRows.Count
            projection.ToggleRenderedChangesOnly file
            let filtered = projection.SelectedDiffRows.Count
            projection.ToggleRenderedChangesOnly file
            let restored = projection.SelectedDiffRows.Count
            test <@ file.RenderedChangesOnly = false @>
            test <@ filtered < whole && restored = whole @>)

module FormattedPreviewProjectionTests =
    let private jsonFile () =
        let file = DiffFileProjection({ OldPath = "config.json"; NewPath = "config.json"; DisplayPath = "config.json" } : GitService.DiffFileSummary)
        let line number text : Models.DiffLine =
            { Type = Models.Context; Content = text; OldLineNo = Some number; NewLineNo = Some number }
        file.ApplyContent({ OldPath = "config.json"; NewPath = "config.json"; NewLineCount = Some 1
                            Hunks = [ { Header = "@@ -1 +1 @@"; Lines = [ line 1 """{"a":1}""" ] } ] } : Models.FileDiff)
        file

    [<Fact>]
    let ``a formatted file goes back to the source it was committed as`` () =
        Headless.run (fun () ->
            let file = jsonFile ()
            let sourceRows = file.Hunks |> Seq.collect _.Lines |> Seq.length
            let formatted : Models.FileDiff =
                { OldPath = "config.json"; NewPath = "config.json"; NewLineCount = Some 3
                  Hunks = [ { Header = "@@ -1,3 +1,3 @@"
                              Lines = [ for i, text in List.indexed [ "{"; "  \"a\": 1"; "}" ] ->
                                          ({ Type = Models.Context; Content = text; OldLineNo = Some(i + 1); NewLineNo = Some(i + 1) } : Models.DiffLine) ] } ] }
            file.ApplyFormatted formatted
            let formattedRows = file.Hunks |> Seq.collect _.Lines |> Seq.length
            test <@ file.IsFormattedPreview && formattedRows = 3 @>

            // Going back is exact: the preview never becomes what the file is.
            file.ClearFormatted()
            test <@ not file.IsFormattedPreview @>
            test <@ (file.Hunks |> Seq.collect _.Lines |> Seq.length) = sourceRows @>
            test <@ (file.Hunks |> Seq.collect _.Lines |> Seq.head).Content = """{"a":1}""" @>)

module SearchBoxFocusTests =
    [<Fact>]
    let ``a letter typed while the search panel is opening is not selected away`` () =
        Headless.run (fun () ->
            let projection = MainProjection()
            let window = MainWindow(Width = 1200.0, Height = 800.0, DataContext = projection)
            try
                window.Show()
                window.UpdateLayout()
                Headless.pump ()
                let box = window.FindControl<TextBox>("SearchBox")

                // Opening the panel queues the focus; a keystroke outruns it, because input is dispatched first.
                projection.IsSearchPanelExpanded <- true
                box.Text <- "a"
                box.CaretIndex <- 1
                Headless.pump ()

                // The letter must survive: selected, the next keystroke would replace it.
                test <@ box.Text = "a" @>
                test <@ box.SelectionStart = box.SelectionEnd @>
                test <@ box.CaretIndex = 1 @>
            finally
                window.Close()
                Headless.pump ())

    [<Fact>]
    let ``opening the panel over an old query still selects it for replacement`` () =
        Headless.run (fun () ->
            let projection = MainProjection()
            let window = MainWindow(Width = 1200.0, Height = 800.0, DataContext = projection)
            try
                window.Show()
                window.UpdateLayout()
                Headless.pump ()
                let box = window.FindControl<TextBox>("SearchBox")
                box.Text <- "old query"
                Headless.pump ()

                projection.IsSearchPanelExpanded <- true
                Headless.pump ()

                // Nothing was typed, so the previous search is selected and typing replaces it.
                test <@ box.SelectionStart = 0 && box.SelectionEnd = "old query".Length @>
            finally
                window.Close()
                Headless.pump ())
