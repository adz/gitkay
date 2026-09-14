module GitKay.Tests.VimTests

open Xunit
open Swensen.Unquote
open GitKay.Core
open GitKay.Core.Vim

let private line = "call(first.second, third) + x"

let private diffContext caret =
    { Pane = VimPane.Diff
      LineText = line
      Caret = caret
      OtherSideText = null
      Side = 0
      HasSelection = false
      HalfPageRows = 10 }

let private paneContext pane = { diffContext 0 with Pane = pane; LineText = null }

/// A keystroke from what it types; named keys (Escape, Left, Down) use their name.
let private key (text: string) =
    match text with
    | "Escape" | "Left" | "Right" | "Up" | "Down" | "Home" | "End" | "LeftShift" ->
        { Name = text; Symbol = null; Control = false; Shift = false; Alt = false }
    | _ when text.StartsWith "C-" ->
        { Name = text.Substring(2).ToUpperInvariant(); Symbol = null; Control = true; Shift = false; Alt = false }
    | _ -> { Name = "?"; Symbol = text; Control = false; Shift = System.Char.IsUpper text[0]; Alt = false }

/// Feeds keys in order against a fixed context; returns every action and whether the last key was consumed.
let private run context (keys: string list) =
    let mutable state = initial
    let actions = ResizeArray()
    let mutable consumed = false
    for k in keys do
        let struct (next, produced, handled) = step state context (key k)
        state <- next
        actions.AddRange produced
        consumed <- handled
    List.ofSeq actions, consumed, state

let private actions context keys =
    let produced, _, _ = run context keys
    produced |> List.map string

let private yanked (produced: string list) =
    produced
    |> List.tryPick (fun action ->
        let parts = action.Split ' '
        if parts[0] = "CopyRange" then
            let range = parts[1].Split ".."
            let from, until = int range[0], int range[1]
            Some(line.Substring(from, until - from))
        else None)

[<Fact>]
let ``counts repeat row moves and name positions`` () =
    test <@ actions (paneContext VimPane.Commits) [ "3"; "j" ] = [ "MoveRows 3" ] @>
    test <@ actions (paneContext VimPane.Commits) [ "1"; "2"; "k" ] = [ "MoveRows -12" ] @>
    test <@ actions (paneContext VimPane.Commits) [ "1"; "0"; "G" ] = [ "GoToPosition 10" ] @>
    test <@ actions (paneContext VimPane.Files) [ "4"; "g"; "g" ] = [ "GoToPosition 4" ] @>
    test <@ actions (paneContext VimPane.Commits) [ "g"; "g" ] = [ "MoveToEdge first" ] @>
    test <@ actions (paneContext VimPane.Commits) [ "G" ] = [ "MoveToEdge last" ] @>
    test <@ actions (paneContext VimPane.Commits) [ "C-d" ] = [ "MoveRows 10" ] @>
    test <@ actions (diffContext 0) [ "2"; "]"; "c" ] = [ "MoveToHunk 1"; "MoveToHunk 1" ] @>

[<Fact>]
let ``z scrolls, but not in the files list`` () =
    test <@ actions (paneContext VimPane.Commits) [ "z"; "z" ] = [ "ScrollFocus 1" ] @>
    test <@ actions (diffContext 0) [ "z"; "t" ] = [ "ScrollFocus 0" ] @>
    let _, consumed, _ = run (paneContext VimPane.Files) [ "z" ]
    test <@ not consumed @>

[<Fact>]
let ``caret motions take counts, except $ ^ and %`` () =
    test <@ actions (diffContext 0) [ "3"; "w" ] = [ "SetCaret 10" ] @>
    test <@ actions (diffContext 0) [ "2"; "e" ] = [ "SetCaret 4" ] @>
    test <@ actions (diffContext 0) [ "5"; "$" ] = [ $"SetCaret {line.Length - 1}" ] @>
    test <@ actions (diffContext 0) [ "%" ] = [ "SetCaret 24" ] @>
    test <@ actions (diffContext 0) [ "2"; "f"; "i" ] = [ "SetCaret 21" ] @>
    test <@ actions (diffContext 0) [ "t"; ")" ] = [ "SetCaret 23" ] @>

[<Fact>]
let ``yanks copy from the caret and leave it at the start`` () =
    test <@ yanked (actions (diffContext 0) [ "y"; "e" ]) = Some "call" @>
    test <@ yanked (actions (diffContext 0) [ "y"; "3"; "w" ]) = Some "call(first" @>
    test <@ yanked (actions (diffContext 0) [ "2"; "y"; "2"; "w" ]) = Some "call(first." @>
    test <@ yanked (actions (diffContext 11) [ "y"; "i"; "w" ]) = Some "second" @>
    test <@ yanked (actions (diffContext 11) [ "y"; "a"; "(" ]) = Some "(first.second, third)" @>
    test <@ yanked (actions (diffContext 5) [ "y"; "t"; ")" ]) = Some "first.second, third" @>
    test <@ yanked (actions (diffContext 24) [ "y"; "%" ]) = Some "(first.second, third)" @>
    test <@ actions (diffContext 11) [ "y"; "i"; "w" ] |> List.last = "SetCaret 11" @>
    test <@ actions (diffContext 7) [ "Y" ] = [ $"CopyRange 0..{line.Length} line" ] @>

[<Fact>]
let ``y copies the commit outside a diff line, and the selection when there is one`` () =
    test <@ actions (paneContext VimPane.Commits) [ "y" ] = [ "CopyCommitReference hash" ] @>
    test <@ actions (paneContext VimPane.Files) [ "Y" ] = [ "CopyCommitReference subject" ] @>
    let selected = { diffContext 0 with HasSelection = true }
    test <@ actions selected [ "y" ] = [ "CopySelection" ] @>

[<Fact>]
let ``pending keys wait, and Escape or an unknown key cancels them`` () =
    let summary keys =
        let produced, consumed, state = run (diffContext 0) keys
        produced.Length, consumed, (state.Pending = NoPending), state.Count
    test <@ summary [ "y" ] = (0, true, false, 0) @>
    test <@ summary [ "y"; "LeftShift" ] = (0, false, false, 0) @>
    test <@ summary [ "y"; "Escape" ] = (0, true, true, 0) @>
    test <@ summary [ "y"; "q" ] = (0, true, true, 0) @>
    test <@ summary [ "4" ] = (0, true, true, 4) @>

[<Fact>]
let ``finds repeat with ; and reverse with ,`` () =
    // The context's caret stays put between keys, so each find starts from the same column.
    test <@ actions (diffContext 6) [ "f"; "i"; "," ] = [ "SetCaret 21" ] @>
    test <@ actions (diffContext 20) [ "F"; "i"; ";"; "," ] = [ "SetCaret 6"; "SetCaret 6"; "SetCaret 21" ] @>

[<Fact>]
let ``* and # find the word under the caret; n repeats`` () =
    test <@ actions (diffContext 12) [ "*" ] = [ "FindWord second forward" ] @>
    test <@ actions (diffContext 17) [ "2"; "#" ] = [ "FindWord third backward"; "FindNext backward" ] @>
    test <@ actions (paneContext VimPane.Commits) [ "3"; "N" ] = List.replicate 3 "FindNext backward" @>

[<Fact>]
let ``arrows at a line edge leave the pane, and h crosses sides in side-by-side`` () =
    let _, consumed, _ = run (diffContext 0) [ "Left" ]
    test <@ not consumed @>
    let newSide = { diffContext 0 with Side = 1; OtherSideText = "old text" }
    let oldSide = { diffContext line.Length with OtherSideText = "new" }
    test <@ actions newSide [ "h" ] = [ "SwitchSide 8" ] @>
    test <@ actions oldSide [ "l" ] = [ "SwitchSide 0" ] @>

[<Fact>]
let ``commit relations and visual mode`` () =
    test <@ actions (paneContext VimPane.Commits) [ "P" ] = [ "GoToParent 1" ] @>
    test <@ actions (paneContext VimPane.Files) [ "c" ] = [ "GoToChild" ] @>
    test <@ actions (diffContext 0) [ "V" ] = [ "ToggleVisual lines" ] @>
    let selected = { diffContext 0 with HasSelection = true }
    test <@ actions selected [ "Escape" ] = [ "CancelSelection" ] @>

[<Theory>]
[<InlineData("say \"hello there\" now", 7, '"', false, "hello there")>]
[<InlineData("say \"hello there\" now", 7, '"', true, "\"hello there\"")>]
[<InlineData("f(a, [b, c])", 7, '[', false, "b, c")>]
[<InlineData("f(a, [b, c])", 7, ')', true, "(a, [b, c])")>]
[<InlineData("one  two three", 5, 'w', true, "two ")>]
[<InlineData("one two", 5, 'w', true, " two")>]
[<InlineData("x = a.b(c)", 5, 'W', false, "a.b(c)")>]
let ``text objects find the span around the caret`` (text: string, column: int, kind: char, around: bool, expected: string) =
    let actual =
        match textObject text column kind around with
        | ValueSome(struct (from, until)) -> text.Substring(from, until - from)
        | ValueNone -> "<none>"
    test <@ actual = expected @>

[<Theory>]
[<InlineData("f(a[b]) x", 0, 6)>]
[<InlineData("f(a[b]) x", 3, 5)>]
[<InlineData("f(a[b]) x", 6, 1)>]
[<InlineData("no brackets", 0, -1)>]
let ``percent finds the partner of the next bracket`` (text: string, column: int, expected: int) =
    let actual = matchingBracket text column |> ValueOption.defaultValue -1
    test <@ actual = expected @>

[<Fact>]
let ``word at the caret, or the next one`` () =
    test <@ wordAt line 12 = "second" @>
    test <@ wordAt line 25 = "x" @>
    test <@ isNull (wordAt "  ++  " 0) @>

/// Records host calls, so the session's wiring can be checked without a UI.
type private RecordingHost() =
    member val Calls = ResizeArray<string>()
    interface IVimHost with
        member _.Pane = VimPane.Diff
        member _.LineText = line
        member _.Caret = 0
        member _.OtherSideText = null
        member _.Side = 0
        member _.HasSelection = false
        member _.HalfPageRows = 10
        member this.SetCaret column = this.Calls.Add $"caret {column}"
        member this.SwitchSide column = this.Calls.Add $"side {column}"
        member this.MoveRows delta = this.Calls.Add $"rows {delta}"
        member this.MoveToEdge last = this.Calls.Add $"edge {last}"
        member this.GoToPosition position = this.Calls.Add $"position {position}"
        member this.MoveToHunk direction = this.Calls.Add $"hunk {direction}"
        member this.ScrollFocus position = this.Calls.Add $"scroll {int position}"
        member this.ToggleVisual linewise = this.Calls.Add $"visual {linewise}"
        member this.CancelSelection() = this.Calls.Add "cancel"; true
        member this.CopySelection() = this.Calls.Add "copy selection"
        member this.CopyRange(from, until, wholeLine) = this.Calls.Add $"copy {from} {until} {wholeLine}"
        member this.CopyCommitReference subject = this.Calls.Add $"commit {subject}"
        member this.FindWord(word, forward) = this.Calls.Add $"word {word} {forward}"
        member this.FindNext forward = this.Calls.Add $"next {forward}"
        member this.GoToParent index = this.Calls.Add $"parent {index}"
        member this.GoToChild() = this.Calls.Add "child"

[<Fact>]
let ``a session keeps pending keys between presses and applies actions to the host`` () =
    let session = VimSession()
    let host = RecordingHost()
    let press text = session.Handle(host, key text)
    let consumedY = press "y"
    let waiting = session.IsAwaitingKey
    let consumedE = press "e"
    let idle = not session.IsAwaitingKey
    let calls = List.ofSeq host.Calls
    test <@ consumedY && waiting && consumedE && idle @>
    test <@ calls = [ "copy 0 4 False"; "caret 0" ] @>

