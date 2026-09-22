namespace GitKay.Core

/// <summary>
/// The keys that mean the same thing in every window. They were written out separately in each one, which is how
/// the folder window ended up without most of them and the commit window without some: this is the one list, and a
/// window consults it rather than repeating it.
/// </summary>
module Keys =

    /// <summary>A key as a window reports it, independent of any toolkit.</summary>
    type Chord =
        { Key: string
          Ctrl: bool
          Shift: bool
          Alt: bool }

    let chord key ctrl shift alt = { Key = key; Ctrl = ctrl; Shift = shift; Alt = alt }

    /// <summary>Something every window can do, whatever it is looking at.</summary>
    type Command =
        | ShowShortcuts
        | Refresh
        | CommandPalette
        | GoToFile
        | FindInView
        | FindNext
        | FindPrevious
        | UndoLast
        | ZoomIn
        | ZoomOut
        | ZoomReset
        | Dismiss

    /// <summary>The shared bindings, in the order a shortcut sheet should list them.</summary>
    let bindings : (Chord * Command * string) list =
        [ chord "F1" false false false, ShowShortcuts, "Keyboard shortcuts"
          chord "F5" false false false, Refresh, "Re-read from disk"
          chord "P" true true false, CommandPalette, "Run a command"
          chord "P" true false false, GoToFile, "Go to file"
          chord "F" true false false, FindInView, "Find in this view"
          chord "N" false false false, FindNext, "Next match"
          chord "N" false true false, FindPrevious, "Previous match"
          chord "Z" true false false, UndoLast, "Undo the last discard"
          chord "Plus" true false false, ZoomIn, "Larger text"
          chord "Minus" true false false, ZoomOut, "Smaller text"
          chord "D0" true false false, ZoomReset, "Reset text size"
          chord "Escape" false false false, Dismiss, "Close what is open" ]

    /// <summary>What a key means, or nothing when it is the window's own business.</summary>
    let command (pressed: Chord) =
        bindings
        |> List.tryFind (fun (chord, _, _) -> chord = pressed)
        |> Option.map (fun (_, command, _) -> command)

    /// <summary>How a chord is written on a shortcut sheet.</summary>
    let describe (chord: Chord) =
        let parts =
            [ if chord.Ctrl then "Ctrl"
              if chord.Shift then "Shift"
              if chord.Alt then "Alt"
              match chord.Key with
              | "Plus" -> "+"
              | "Minus" -> "−"
              | "D0" -> "0"
              | key -> key ]
        String.concat "+" parts

    /// <summary>The shared keys as a sheet lists them: the chord, then what it does.</summary>
    let sheet = bindings |> List.map (fun (chord, _, description) -> describe chord, description)
