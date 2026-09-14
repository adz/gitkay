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
type private DiffFixture(mode: string) =
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

    let surface = DiffSurfaceControl(Mode = mode, ItemsSource = rows)
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
            use fixture = new DiffFixture "diff"
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
    let ``v selects from the caret on the side the caret is on`` () =
        Headless.run (fun () ->
            use fixture = new DiffFixture "side-by-side"
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
            use fixture = new DiffFixture "side-by-side"
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
            use fixture = new DiffFixture "diff"
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
            fixture.Press(Key.D4, RawInputModifiers.Shift)
            let atEnd = fixture.Surface.HorizontalOffset
            fixture.Press Key.D0
            let atStart = fixture.Surface.HorizontalOffset
            test <@ atEnd > 0.0 && atStart = 0.0 @>)

    [<Fact>]
    let ``f t W B e and repeats move the caret like vim`` () =
        Headless.run (fun () ->
            use fixture = new DiffFixture "diff"
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
            use fixture = new DiffFixture "diff"
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

    [<Theory>]
    [<InlineData("say \"hello there\" now", 7, '"', false, "hello there")>]
    [<InlineData("say \"hello there\" now", 7, '"', true, "\"hello there\"")>]
    [<InlineData("f(a, [b, c])", 7, '[', false, "b, c")>]
    [<InlineData("f(a, [b, c])", 7, ')', true, "(a, [b, c])")>]
    [<InlineData("one  two three", 5, 'w', true, "two ")>]
    [<InlineData("one two", 5, 'w', true, " two")>]
    [<InlineData("x = a.b(c)", 5, 'W', false, "a.b(c)")>]
    let ``text objects find the span around the caret`` (text: string, column: int, kind: char, around: bool, expected: string) =
        let span = DiffSurfaceControl.TextObject(text, column, kind, around)
        let actual =
            if span.HasValue then
                let struct (from, until) = span.Value
                Some(text.Substring(from, until - from))
            else None
        test <@ actual = Some expected @>

    [<Fact>]
    let ``counts repeat motions, finds and yanks`` () =
        Headless.run (fun () ->
            use fixture = new DiffFixture "diff"
            let line = "call(first.second, third) + x"
            fixture.Surface.Focus() |> ignore
            fixture.Surface.SelectedItem <- fixture.Line 0 6
            let counted (n: int) (key: Key) (modifiers: RawInputModifiers) (symbol: string) =
                fixture.Surface.PendingCount <- n
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
            use fixture = new DiffFixture "diff"
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
            // From "(": w stops on "a", then on "["; the word under the caret is the next one, "i".
            fixture.Press Key.W; fixture.Press Key.W
            let word = fixture.Surface.WordUnderCaret()
            test <@ (firstNonBlank, matched, yankPercent, word) = ("i", ")", "(a[i] == b)", "i") @>

            fixture.Surface.GoToLine 21
            test <@ Object.ReferenceEquals(fixture.Surface.SelectedItem, fixture.Line 0 20) @>)

    [<Theory>]
    [<InlineData("f(a[b]) x", 0, 6)>]
    [<InlineData("f(a[b]) x", 3, 5)>]
    [<InlineData("f(a[b]) x", 6, 1)>]
    [<InlineData("no brackets", 0, -1)>]
    let ``percent finds the partner of the next bracket`` (text: string, column: int, expected: int) =
        let found = DiffSurfaceControl.MatchingBracket(text, column)
        let actual = if found.HasValue then found.Value else -1
        test <@ actual = expected @>

