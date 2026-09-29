namespace GitKay.Core

open System

/// <summary>Finds http(s) URLs in plain text (git's "remote: View pull request ..." lines, error output) so the UI
/// can make them clickable. Pure text work, so it is tested without a window.</summary>
module Links =
    /// <summary>A URL at <c>Start</c>, <c>Length</c> characters long.</summary>
    [<Struct>]
    type LinkSpan = { Start: int; Length: int }

    let private isStop (c: char) =
        Char.IsWhiteSpace c || Char.IsControl c || c = '<' || c = '>' || c = '"' || c = '`'

    let private count (c: char) (s: string) (start: int) (stop: int) =
        let mutable n = 0
        for i in start .. stop - 1 do
            if s[i] = c then n <- n + 1
        n

    /// Sentence punctuation and unbalanced closers do not belong to the URL: "(see https://x.io/a)." ends at "a".
    let private trimEnd (text: string) (start: int) (stop: int) =
        let mutable e = stop
        let mutable go = true
        while go && e > start do
            match text[e - 1] with
            | '.' | ',' | ';' | ':' | '!' | '?' | '\'' | '*' -> e <- e - 1
            | ')' when count ')' text start e > count '(' text start e -> e <- e - 1
            | ']' when count ']' text start e > count '[' text start e -> e <- e - 1
            | '}' when count '}' text start e > count '{' text start e -> e <- e - 1
            | _ -> go <- false
        e

    let private isBoundary (text: string) (i: int) =
        i = 0 || not (Char.IsLetterOrDigit text[i - 1])

    /// Every http:// or https:// URL in the text, in order.
    let find (text: string) : LinkSpan list =
        if String.IsNullOrEmpty text then []
        else
            let found = ResizeArray<LinkSpan>()
            let mutable i = 0
            while i < text.Length do
                let scheme =
                    if String.Compare(text, i, "https://", 0, 8, StringComparison.OrdinalIgnoreCase) = 0 then 8
                    elif String.Compare(text, i, "http://", 0, 7, StringComparison.OrdinalIgnoreCase) = 0 then 7
                    else 0
                if scheme > 0 && isBoundary text i then
                    let mutable e = i + scheme
                    while e < text.Length && not (isStop text[e]) do e <- e + 1
                    let e = trimEnd text i e
                    if e > i + scheme then
                        found.Add { Start = i; Length = e - i }
                    i <- max e (i + scheme)
                else
                    i <- i + 1
            List.ofSeq found

    /// The URL covering character <paramref name="index"/>, if any.
    let linkAt (text: string) (index: int) : string option =
        find text
        |> List.tryFind (fun s -> index >= s.Start && index < s.Start + s.Length)
        |> Option.map (fun s -> text.Substring(s.Start, s.Length))
