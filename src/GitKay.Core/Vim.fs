namespace GitKay.Core

open System

/// <summary>
/// vim-style keyboard navigation for the commit list, the diff and the changed-files list.
/// </summary>
/// <remarks>
/// The keys are a small language: counts, pending operators (y), pending finds (f / t), text objects (i / a) and
/// prefixes (g, z, ] and [). <see cref="M:GitKay.Core.Vim.step"/> interprets one keystroke purely, returning the new
/// state and what should happen; <see cref="T:GitKay.Core.Vim.VimSession"/> applies that to a pane through
/// <see cref="T:GitKay.Core.Vim.IVimHost"/>. Views only translate key events and carry out host calls.
/// </remarks>
module Vim =

    type VimPane =
        | Commits = 0
        | Diff = 1
        | Files = 2

    type VimScroll =
        | Top = 0
        | Center = 1
        | Bottom = 2

    /// <summary>A place on screen for H / M / L.</summary>
    type VimScreenRow =
        | Top = 0
        | Middle = 1
        | Bottom = 2

    /// <summary>One key press, independent of the UI toolkit.</summary>
    /// <param name="Name">The physical key's name, e.g. "J", "D4", "Escape", "Left", "Home".</param>
    /// <param name="Symbol">The character the key typed with the current layout and Shift, e.g. "j", "J", "$"; null when none.</param>
    [<Struct>]
    type KeyStroke =
        { Name: string
          Symbol: string
          Control: bool
          Shift: bool
          Alt: bool }

    /// <summary>What a pane can report and do. Each pane's view implements it.</summary>
    type IVimHost =
        abstract Pane: VimPane
        /// <summary>The focused diff line's text on the caret's side; null when no line is focused.</summary>
        abstract LineText: string
        abstract Caret: int
        /// <summary>The other side's text in side-by-side view, when the caret may cross to it; otherwise null.</summary>
        abstract OtherSideText: string
        /// <summary>0 for the old (or only) side, 1 for the new side.</summary>
        abstract Side: int
        abstract HasSelection: bool
        abstract HalfPageRows: int
        /// <summary>How many rows fit on screen, for PageUp / PageDown.</summary>
        abstract PageRows: int
        abstract SetCaret: column: int -> unit
        abstract SwitchSide: column: int -> unit
        abstract MoveRows: delta: int -> unit
        abstract MoveToEdge: last: bool -> unit
        abstract GoToPosition: position: int -> unit
        abstract MoveToHunk: direction: int -> unit
        abstract ScrollFocus: position: VimScroll -> unit
        /// <summary>Focuses the row at the top, middle or bottom of the screen; offset counts rows inwards from that edge.</summary>
        abstract FocusScreenRow: row: VimScreenRow * offset: int -> unit
        /// <summary>Scrolls by whole rows without moving the focus, unless the focused row would leave the screen.</summary>
        abstract ScrollRows: delta: int -> unit
        abstract ToggleVisual: linewise: bool -> unit
        /// <summary>Clears a selection or visual mode; false when there was nothing to clear.</summary>
        abstract CancelSelection: unit -> bool
        abstract CopySelection: unit -> unit
        /// <summary>Copies [from, until) of the focused line's caret side.</summary>
        abstract CopyRange: from: int * until: int * wholeLine: bool -> unit
        abstract CopyCommitReference: subject: bool -> unit
        abstract FindWord: word: string * forward: bool -> unit
        abstract FindNext: forward: bool -> unit
        abstract GoToParent: index: int -> unit
        abstract GoToChild: unit -> unit

    [<Struct>]
    type CharFind =
        { Target: char
          Forward: bool
          Till: bool }

    type VimAction =
        | SetCaret of column: int
        | SwitchSide of column: int
        | MoveRows of delta: int
        | MoveToEdge of last: bool
        | GoToPosition of position: int
        | MoveToHunk of direction: int
        | ScrollFocus of position: VimScroll
        | FocusScreenRow of row: VimScreenRow * offset: int
        | ScrollRows of delta: int
        | ToggleVisual of linewise: bool
        | CancelSelection
        | CopySelection
        | CopyRange of from: int * until: int * wholeLine: bool
        | CopyCommitReference of subject: bool
        | FindWord of word: string * forward: bool
        | FindNext of forward: bool
        | GoToParent of index: int
        | GoToChild

        // Hand-written: the default structural ToString uses reflection that NativeAOT does not support.
        override this.ToString() =
            match this with
            | SetCaret column -> $"SetCaret {column}"
            | SwitchSide column -> $"SwitchSide {column}"
            | MoveRows delta -> $"MoveRows {delta}"
            | MoveToEdge last -> if last then "MoveToEdge last" else "MoveToEdge first"
            | GoToPosition position -> $"GoToPosition {position}"
            | MoveToHunk direction -> $"MoveToHunk {direction}"
            | ScrollFocus position -> $"ScrollFocus {int position}"
            | FocusScreenRow(row, offset) -> $"FocusScreenRow {int row} {offset}"
            | ScrollRows delta -> $"ScrollRows {delta}"
            | ToggleVisual linewise -> if linewise then "ToggleVisual lines" else "ToggleVisual characters"
            | CancelSelection -> "CancelSelection"
            | CopySelection -> "CopySelection"
            | CopyRange(from, until, wholeLine) -> if wholeLine then $"CopyRange {from}..{until} line" else $"CopyRange {from}..{until}"
            | CopyCommitReference subject -> if subject then "CopyCommitReference subject" else "CopyCommitReference hash"
            | FindWord(word, forward) -> if forward then $"FindWord {word} forward" else $"FindWord {word} backward"
            | FindNext forward -> if forward then "FindNext forward" else "FindNext backward"
            | GoToParent index -> $"GoToParent {index}"
            | GoToChild -> "GoToChild"

    /// <summary>A key waiting for the next one.</summary>
    type Pending =
        | NoPending
        /// <summary>g, z, ] or [ typed; the count typed before it is kept.</summary>
        | Prefix of char
        /// <summary>y typed; a count may follow before the motion.</summary>
        | Yank of motionCount: int
        /// <summary>yi or ya typed; the next character names the object.</summary>
        | TextObject of around: bool
        /// <summary>f, F, t or T typed; the next character is the target.</summary>
        | Find of forward: bool * till: bool * yank: bool

    type VimState =
        { Count: int
          /// <summary>The count typed before y, multiplied with one typed after it (2y3w).</summary>
          YankCount: int
          Pending: Pending
          LastFind: CharFind voption }

    let initial =
        { Count = 0
          YankCount = 1
          Pending = NoPending
          LastFind = ValueNone }

    /// <summary>The focused pane's facts that key interpretation depends on.</summary>
    [<Struct>]
    type KeyContext =
        { Pane: VimPane
          LineText: string
          Caret: int
          OtherSideText: string
          Side: int
          HasSelection: bool
          HalfPageRows: int
          PageRows: int }

    let contextOf (host: IVimHost) =
        { Pane = host.Pane
          LineText = host.LineText
          Caret = host.Caret
          OtherSideText = host.OtherSideText
          Side = host.Side
          HasSelection = host.HasSelection
          HalfPageRows = host.HalfPageRows
          PageRows = host.PageRows }

    // ----- Pure text functions over one line -----

    let private isWordChar (c: char) = Char.IsLetterOrDigit c || c = '_'

    /// <summary>0 for whitespace, 1 for word characters (any non-blank for WORDs), 2 for punctuation.</summary>
    let private charClass (bigWord: bool) (c: char) =
        if Char.IsWhiteSpace c then 0
        elif bigWord || isWordChar c then 1
        else 2

    /// <summary>vim's %: the partner of the first bracket at or after <paramref name="column"/> on the line.</summary>
    let matchingBracket (text: string) (column: int) : int voption =
        let opens = "([{"
        let closes = ")]}"
        let mutable start = max 0 column
        let mutable result = ValueNone
        let mutable searching = true
        while searching && start < text.Length do
            let openIndex = opens.IndexOf text[start]
            let closeIndex = closes.IndexOf text[start]
            if openIndex < 0 && closeIndex < 0 then
                start <- start + 1
            else
                searching <- false
                let self, partner, step =
                    if openIndex >= 0 then opens[openIndex], closes[openIndex], 1
                    else closes[closeIndex], opens[closeIndex], -1
                let mutable depth = 0
                let mutable i = start
                while result.IsNone && i >= 0 && i < text.Length do
                    if text[i] = self then depth <- depth + 1
                    elif text[i] = partner then
                        depth <- depth - 1
                        if depth = 0 then result <- ValueSome i
                    i <- i + step
        result

    /// <summary>
    /// Where a caret motion on one line lands, and whether a yank to that point includes the character there
    /// (vim's e, E, $ and % do; w, b, h, l, 0 and ^ stop before it). None when the key is not a line motion.
    /// </summary>
    let lineMotion (stroke: KeyStroke) (text: string) (column: int) : struct (int * bool) voption =
        let symbol = if isNull stroke.Symbol then "" else stroke.Symbol
        let plain = not stroke.Control && not stroke.Alt
        let lastIndex = max 0 (text.Length - 1)
        if not plain then ValueNone
        else
            match symbol, stroke.Name with
            | "h", _ | _, "Left" -> ValueSome(struct (max 0 (column - 1), false))
            | "l", _ | _, "Right" -> ValueSome(struct (min text.Length (column + 1), false))
            | "0", _ -> ValueSome(struct (0, false))
            | "$", _ -> ValueSome(struct (lastIndex, text.Length > 0))
            | ("^" | "_"), _ ->
                // First non-blank character: code is indented, so 0 often lands on whitespace.
                let mutable target = 0
                while target < text.Length && Char.IsWhiteSpace text[target] do
                    target <- target + 1
                ValueSome(struct ((if target = text.Length then lastIndex else target), false))
            | "%", _ ->
                match matchingBracket text column with
                | ValueSome partner -> ValueSome(struct (partner, true))
                | ValueNone -> ValueSome(struct (column, false))
            | ("w" | "W"), _ ->
                // To the start of the next word: leave this run of word or punctuation characters, then skip spaces.
                let classOf = charClass (symbol = "W")
                let mutable target = column
                if column < text.Length then
                    let start = classOf text[column]
                    while target < text.Length && start <> 0 && classOf text[target] = start do
                        target <- target + 1
                    while target < text.Length && classOf text[target] = 0 do
                        target <- target + 1
                ValueSome(struct (target, false))
            | ("b" | "B"), _ ->
                let classOf = charClass (symbol = "B")
                let mutable target = min column text.Length
                while target > 0 && classOf text[target - 1] = 0 do
                    target <- target - 1
                let run = if target > 0 then classOf text[target - 1] else 0
                while target > 0 && classOf text[target - 1] = run do
                    target <- target - 1
                ValueSome(struct (target, false))
            | ("e" | "E"), _ ->
                // To the last character of this or the next word.
                let classOf = charClass (symbol = "E")
                let mutable next = column + 1
                while next < text.Length && classOf text[next] = 0 do
                    next <- next + 1
                if next >= text.Length then
                    ValueSome(struct (column, false))
                else
                    let run = classOf text[next]
                    while next + 1 < text.Length && classOf text[next + 1] = run do
                        next <- next + 1
                    ValueSome(struct (next, true))
            | _ -> ValueNone

    /// <summary>The column an f / F / t / T find lands on, taking the count'th occurrence; None when there are too few.</summary>
    let findTarget (find: CharFind) (text: string) (column: int) (count: int) (repeat: bool) : int voption =
        // Like vim, repeating t / T steps past the character the caret already stops before.
        let mutable skip = if repeat && find.Till then 1 else 0
        let mutable index = column
        let mutable occurrence = 0
        while index >= 0 && occurrence < max 1 count do
            index <-
                if find.Forward then
                    let from = min text.Length (index + 1 + skip)
                    text.IndexOf(find.Target, from)
                else
                    let from = index - 1 - skip
                    if from < 0 then -1 else text.LastIndexOf(find.Target, from)
            skip <- 0
            occurrence <- occurrence + 1
        if index < 0 then ValueNone
        elif find.Till then ValueSome(if find.Forward then index - 1 else index + 1)
        else ValueSome index

    let private bracketObject (text: string) (column: int) (openChar: char) (closeChar: char) (around: bool) =
        // The innermost pair enclosing the caret (the caret may sit on either bracket).
        let mutable depth = 0
        let mutable start = -1
        let mutable i = if text[column] = closeChar then column - 1 else column
        while start < 0 && i >= 0 do
            if text[i] = closeChar && i <> column then depth <- depth + 1
            elif text[i] = openChar then
                if depth = 0 then start <- i else depth <- depth - 1
            i <- i - 1
        if start < 0 then ValueNone
        else
            let mutable result = ValueNone
            let mutable nested = 0
            let mutable j = start + 1
            while result.IsNone && j < text.Length do
                if text[j] = openChar then nested <- nested + 1
                elif text[j] = closeChar then
                    if nested = 0 then
                        result <- ValueSome(if around then struct (start, j + 1) else struct (start + 1, j))
                    else nested <- nested - 1
                j <- j + 1
            result

    /// <summary>The [from, until) range of a vim text object around <paramref name="column"/>.</summary>
    let textObject (text: string) (column: int) (kind: char) (around: bool) : struct (int * int) voption =
        if String.IsNullOrEmpty text then ValueNone
        else
            let column = min (max 0 column) (text.Length - 1)
            match kind with
            | 'w' | 'W' ->
                let classOf = charClass (kind = 'W')
                let run = classOf text[column]
                let mutable from = column
                let mutable until = column + 1
                while from > 0 && classOf text[from - 1] = run do
                    from <- from - 1
                while until < text.Length && classOf text[until] = run do
                    until <- until + 1
                if not around then ValueSome(struct (from, until))
                else
                    // "a word" takes the whitespace after it, or before it when the word ends the line.
                    let mutable finish = until
                    while finish < text.Length && Char.IsWhiteSpace text[finish] do
                        finish <- finish + 1
                    if finish > until || run = 0 then ValueSome(struct (from, finish))
                    else
                        let mutable begin' = from
                        while begin' > 0 && Char.IsWhiteSpace text[begin' - 1] do
                            begin' <- begin' - 1
                        ValueSome(struct (begin', until))
            | '(' | ')' | 'b' -> bracketObject text column '(' ')' around
            | '[' | ']' -> bracketObject text column '[' ']' around
            | '{' | '}' | 'B' -> bracketObject text column '{' '}' around
            | '<' | '>' -> bracketObject text column '<' '>' around
            | '"' | '\'' | '`' ->
                // Quotes pair up from the start of the line; use the pair around the caret, or the next one after it.
                let quotes =
                    [| for i in 0 .. text.Length - 1 do
                           if text[i] = kind && (i = 0 || text[i - 1] <> '\\') then i |]
                let mutable result = ValueNone
                let mutable pair = 0
                while result.IsNone && pair + 1 < quotes.Length do
                    let openAt, closeAt = quotes[pair], quotes[pair + 1]
                    if closeAt >= column then
                        result <- ValueSome(if around then struct (openAt, closeAt + 1) else struct (openAt + 1, closeAt))
                    pair <- pair + 2
                result
            | _ -> ValueNone

    /// <summary>The word under the caret, or the next word after it on the line, for * and #.</summary>
    let wordAt (text: string) (column: int) : string =
        if String.IsNullOrEmpty text then null
        else
            let mutable start = min (max 0 column) text.Length
            while start < text.Length && not (isWordChar text[start]) do
                start <- start + 1
            if start >= text.Length then null
            else
                while start > 0 && isWordChar text[start - 1] do
                    start <- start - 1
                let mutable until = start
                while until < text.Length && isWordChar text[until] do
                    until <- until + 1
                text.Substring(start, until - start)

    // ----- Key interpretation -----

    let private symbolOf (stroke: KeyStroke) = if isNull stroke.Symbol then "" else stroke.Symbol

    let private isModifierKey (stroke: KeyStroke) =
        match stroke.Name with
        | "LeftShift" | "RightShift" | "LeftCtrl" | "RightCtrl" | "LeftAlt" | "RightAlt" -> true
        | _ -> false

    let private digitOf (stroke: KeyStroke) =
        let symbol = symbolOf stroke
        if stroke.Control || stroke.Alt || symbol.Length <> 1 || not (Char.IsAsciiDigit symbol[0]) then -1
        else int symbol[0] - int '0'

    /// <summary>Copies a range and, like a vim yank, leaves the caret at its start.</summary>
    let private yankRange (from: int) (until: int) (text: string) =
        let from = min (max 0 from) text.Length
        let until = min (max from until) text.Length
        if until = from then [] else [ CopyRange(from, until, false); SetCaret from ]

    let private yankLine (text: string) = [ CopyRange(0, text.Length, true) ]

    /// <summary>A yank from the caret to where a motion lands.</summary>
    let private yankToMotion (context: KeyContext) (stroke: KeyStroke) (count: int) =
        let text = context.LineText
        let column = min context.Caret text.Length
        match lineMotion stroke text column with
        | ValueNone -> ValueNone
        | ValueSome(struct (first, firstInclusive)) ->
            let mutable target = first
            let mutable inclusive = firstInclusive
            let mutable step = 1
            while step < count && target < text.Length do
                match lineMotion stroke text target with
                | ValueSome(struct (next, nextInclusive)) ->
                    target <- next
                    inclusive <- nextInclusive
                | ValueNone -> ()
                step <- step + 1
            // Inclusive motions take the character at both ends; exclusive ones stop before the far end.
            if target >= column then
                ValueSome(yankRange column (if inclusive then target + 1 else target) text)
            else
                ValueSome(yankRange target (if inclusive then column + 1 else column) text)

    let private applyFind (context: KeyContext) (find: CharFind) (count: int) (repeat: bool) (yank: bool) =
        let text = context.LineText
        let column = min context.Caret text.Length
        match findTarget find text column count repeat with
        | ValueNone -> []
        | ValueSome target when not yank -> [ SetCaret target ]
        // Forward finds include the found character; backward finds stop before the caret's own.
        | ValueSome target when find.Forward -> yankRange column (target + 1) text
        | ValueSome target -> yankRange target column text

    let private handled state actions = struct (state, actions, true)
    let private unhandled state = struct (state, [], false)

    /// <summary>
    /// Interprets one keystroke: the next state, the actions to apply, and whether the key was consumed.
    /// </summary>
    let step (state: VimState) (context: KeyContext) (stroke: KeyStroke) : struct (VimState * VimAction list * bool) =
        let symbol = symbolOf stroke
        let plain = not stroke.Control && not stroke.Alt
        let hasLine = context.Pane = VimPane.Diff && not (isNull context.LineText)
        let count = max 1 state.Count
        let cleared = { state with Count = 0; Pending = NoPending }

        if isModifierKey stroke then
            // Shift arrives on its own before a shifted character; keep everything pending.
            unhandled state
        else
            match state.Pending with
            | _ when stroke.Name = "Escape" && state.Pending <> NoPending -> handled cleared []

            | Find(forward, till, yank) ->
                if not hasLine || symbol.Length <> 1 || Char.IsControl symbol[0] then
                    handled cleared []
                else
                    let find = { Target = symbol[0]; Forward = forward; Till = till }
                    let count = if yank then max 1 state.YankCount else count
                    handled { cleared with LastFind = ValueSome find } (applyFind context find count false yank)

            | TextObject around ->
                if not hasLine || symbol.Length <> 1 then
                    handled cleared []
                else
                    match textObject context.LineText context.Caret symbol[0] around with
                    | ValueSome(struct (from, until)) -> handled cleared (yankRange from until context.LineText)
                    | ValueNone -> handled cleared []

            | Yank motionCount ->
                let digit = digitOf stroke
                if digit > 0 || (digit = 0 && motionCount > 0) then
                    // A count between y and the motion (y3w) multiplies one typed before y (2y3w).
                    handled { state with Pending = Yank(min 9999 (motionCount * 10 + digit)) } []
                elif not hasLine then
                    handled cleared []
                else
                    let total = max 1 state.YankCount * max 1 motionCount
                    match symbol with
                    | "y" when plain -> handled cleared (yankLine context.LineText)
                    | ("i" | "a") when plain -> handled { cleared with Pending = TextObject(symbol = "a") } []
                    | ("f" | "F" | "t" | "T") when plain ->
                        let pending = Find(Char.IsLower symbol[0], Char.ToLowerInvariant symbol[0] = 't', true)
                        handled { cleared with Pending = pending; YankCount = total } []
                    | (";" | ",") when plain && state.LastFind.IsSome ->
                        let last = state.LastFind.Value
                        let find = if symbol = ";" then last else { last with Forward = not last.Forward }
                        handled cleared (applyFind context find total true true)
                    | _ ->
                        match yankToMotion context stroke total with
                        | ValueSome actions -> handled cleared actions
                        // Anything else cancels the pending yank, as in vim.
                        | ValueNone -> handled cleared []

            | Prefix prefix ->
                let prefixCount = state.Count
                match prefix, symbol with
                | 'g', "g" when plain ->
                    handled cleared [ (if prefixCount > 0 then GoToPosition prefixCount else MoveToEdge false) ]
                | 'z', ("z" | "t" | "b") when plain ->
                    let position =
                        match symbol with
                        | "t" -> VimScroll.Top
                        | "b" -> VimScroll.Bottom
                        | _ -> VimScroll.Center
                    handled cleared [ ScrollFocus position ]
                | (']' | '['), "c" when plain ->
                    let direction = if prefix = ']' then 1 else -1
                    handled cleared (List.replicate count (MoveToHunk direction))
                | _ -> handled cleared []

            | NoPending ->
                let digit = digitOf stroke
                if digit > 0 || (digit = 0 && state.Count > 0) then
                    handled { state with Count = min 99999 (state.Count * 10 + digit) } []
                else
                    let lineCount = state.Count
                    match stroke.Name, symbol with
                    | ("Down" | "Up"), _ when plain && not stroke.Shift ->
                        handled cleared [ MoveRows(if stroke.Name = "Down" then count else -count) ]
                    | ("Home" | "End"), _ when plain && not stroke.Shift -> handled cleared [ MoveToEdge(stroke.Name = "End") ]
                    | "D", _ when stroke.Control && not stroke.Shift && not stroke.Alt ->
                        handled cleared [ MoveRows(max 1 context.HalfPageRows) ]
                    | "U", _ when stroke.Control && not stroke.Shift && not stroke.Alt ->
                        handled cleared [ MoveRows(-(max 1 context.HalfPageRows)) ]
                    | ("PageDown" | "PageUp"), _ when plain && not stroke.Shift ->
                        let rows = count * max 1 context.PageRows
                        handled cleared [ MoveRows(if stroke.Name = "PageDown" then rows else -rows) ]
                    | ("E" | "Y"), _ when stroke.Control && not stroke.Shift && not stroke.Alt && context.Pane <> VimPane.Files ->
                        handled cleared [ ScrollRows(if stroke.Name = "E" then count else -count) ]
                    | _ when not plain -> unhandled cleared
                    | _, ("j" | "k") -> handled cleared [ MoveRows(if symbol = "j" then count else -count) ]
                    | _, "g" -> handled { state with Pending = Prefix 'g' } []
                    | _, "G" -> handled cleared [ (if lineCount > 0 then GoToPosition lineCount else MoveToEdge true) ]
                    | _, "z" when context.Pane <> VimPane.Files -> handled { state with Pending = Prefix 'z' } []
                    // H / L take a count as rows in from the screen's edge (3H is the third row from the top).
                    | _, "H" when context.Pane <> VimPane.Files -> handled cleared [ FocusScreenRow(VimScreenRow.Top, count - 1) ]
                    | _, "M" when context.Pane <> VimPane.Files -> handled cleared [ FocusScreenRow(VimScreenRow.Middle, 0) ]
                    | _, "L" when context.Pane <> VimPane.Files -> handled cleared [ FocusScreenRow(VimScreenRow.Bottom, count - 1) ]
                    | _, ("]" | "[") when context.Pane = VimPane.Diff -> handled { state with Pending = Prefix symbol[0] } []
                    | _, ("n" | "N") -> handled cleared (List.replicate count (FindNext(symbol = "n")))
                    | _, ("p" | "P") -> handled cleared [ GoToParent(if symbol = "P" then 1 else 0) ]
                    | _, "c" -> handled cleared [ GoToChild ]
                    | _, ("v" | "V") when context.Pane = VimPane.Diff -> handled cleared [ ToggleVisual(symbol = "V") ]
                    | _, ("y" | "Y") when context.HasSelection && context.Pane = VimPane.Diff -> handled cleared [ CopySelection ]
                    | _, "y" when hasLine -> handled { cleared with Pending = Yank 0; YankCount = count } []
                    | _, "Y" when hasLine -> handled cleared (yankLine context.LineText)
                    | _, ("y" | "Y") -> handled cleared [ CopyCommitReference(symbol = "Y") ]
                    | "Escape", _ when context.HasSelection -> handled cleared [ CancelSelection ]
                    | _ when not hasLine -> unhandled cleared
                    | _, ("*" | "#") ->
                        match wordAt context.LineText context.Caret with
                        | null -> handled cleared []
                        | word ->
                            let forward = symbol = "*"
                            handled cleared (FindWord(word, forward) :: List.replicate (count - 1) (FindNext forward))
                    | _, ("f" | "F" | "t" | "T") ->
                        let pending = Find(Char.IsLower symbol[0], Char.ToLowerInvariant symbol[0] = 't', false)
                        handled { cleared with Pending = pending; Count = state.Count } []
                    | _, (";" | ",") ->
                        match state.LastFind with
                        | ValueSome last ->
                            let find = if symbol = ";" then last else { last with Forward = not last.Forward }
                            handled cleared (applyFind context find count true false)
                        | ValueNone -> handled cleared []
                    | _ ->
                        let text = context.LineText
                        let column = min context.Caret text.Length
                        let leftward = symbol = "h" || stroke.Name = "Left"
                        let rightward = symbol = "l" || stroke.Name = "Right"
                        // In side-by-side, h at the start of the new side and l at the end of the old side cross over.
                        if leftward && column = 0 && context.Side = 1 && not (isNull context.OtherSideText) then
                            handled cleared [ SwitchSide context.OtherSideText.Length ]
                        elif rightward && column >= text.Length && context.Side = 0 && not (isNull context.OtherSideText) then
                            handled cleared [ SwitchSide 0 ]
                        // At the edge, arrows keep their pane navigation.
                        elif stroke.Name = "Left" && column = 0 || stroke.Name = "Right" && column >= text.Length then
                            unhandled cleared
                        else
                            match lineMotion stroke text column with
                            | ValueNone -> unhandled cleared
                            | ValueSome(struct (first, _)) ->
                                // $, ^, _ and % ignore a count; the rest move count times.
                                let repeats = if symbol = "$" || symbol = "^" || symbol = "_" || symbol = "%" then 1 else count
                                let mutable target = first
                                for _ in 2..repeats do
                                    match lineMotion stroke text target with
                                    | ValueSome(struct (next, _)) -> target <- next
                                    | ValueNone -> ()
                                handled cleared [ SetCaret target ]

    let apply (host: IVimHost) (action: VimAction) =
        match action with
        | SetCaret column -> host.SetCaret column
        | SwitchSide column -> host.SwitchSide column
        | MoveRows delta -> host.MoveRows delta
        | MoveToEdge last -> host.MoveToEdge last
        | GoToPosition position -> host.GoToPosition position
        | MoveToHunk direction -> host.MoveToHunk direction
        | ScrollFocus position -> host.ScrollFocus position
        | FocusScreenRow(row, offset) -> host.FocusScreenRow(row, offset)
        | ScrollRows delta -> host.ScrollRows delta
        | ToggleVisual linewise -> host.ToggleVisual linewise
        | CancelSelection -> host.CancelSelection() |> ignore
        | CopySelection -> host.CopySelection()
        | CopyRange(from, until, wholeLine) -> host.CopyRange(from, until, wholeLine)
        | CopyCommitReference subject -> host.CopyCommitReference subject
        | FindWord(word, forward) -> host.FindWord(word, forward)
        | FindNext forward -> host.FindNext forward
        | GoToParent index -> host.GoToParent index
        | GoToChild -> host.GoToChild()

    /// <summary>The vim key state for one window, shared by its panes.</summary>
    [<Sealed; AllowNullLiteral>]
    type VimSession() =
        let mutable state = initial

        /// <summary>A key is pending (a count, y, f / t, a text object or a prefix), so the next key belongs to vim.</summary>
        member _.IsAwaitingKey = state.Count > 0 || state.Pending <> NoPending

        /// <summary>Interprets a keystroke for a pane and applies it; true when the key was consumed.</summary>
        member _.Handle(host: IVimHost, stroke: KeyStroke) =
            let struct (next, actions, consumed) = step state (contextOf host) stroke
            state <- next
            for action in actions do
                apply host action
            consumed
