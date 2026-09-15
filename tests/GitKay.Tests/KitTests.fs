module GitKay.Tests.KitTests

open Xunit
open Swensen.Unquote
open GitKay.Kit
open GitKay.Core

let private spansOf (query: TextQuery) text =
    query.Spans text |> List.map (fun struct (start, length) -> start, length)

[<Fact>]
let ``a literal query matches ignoring case, every occurrence`` () =
    let query = TextQuery.Create(false, "ab")
    test <@ query.IsMatch "xABy" && spansOf query "ab_AB_aB" = [ 0, 2; 3, 2; 6, 2 ] @>

[<Fact>]
let ``a regex query ignores case unless it opts out`` () =
    test <@ spansOf (TextQuery.Create(true, @"a\d")) "A1 a2" = [ 0, 2; 3, 2 ] @>
    test <@ spansOf (TextQuery.Create(true, @"(?-i)a\d")) "A1 a2" = [ 3, 2 ] @>

[<Fact>]
let ``an invalid regex falls back to its literal text; blank matches nothing`` () =
    let broken = TextQuery.Create(true, "a(b")
    test <@ broken.IsMatch "xa(by" && not (broken.IsMatch "ab") @>
    let blank = TextQuery.Create(true, "  ")
    test <@ blank.IsEmpty && not (blank.IsMatch "anything") && spansOf blank "anything" = [] @>
    test <@ not (TextQuery.None.IsMatch "x") && (TextQuery.Create(false, "x")).FirstIndex "axa" = 1 @>

[<Fact>]
let ``empty regex matches are skipped`` () =
    test <@ spansOf (TextQuery.Create(true, "x*")) "axxb" = [ 1, 2 ] @>

[<Fact>]
let ``the cache returns the same query for the same text and mode`` () =
    let cache = TextQueryCache 2
    let first = cache.Get(true, "a")
    test <@ obj.ReferenceEquals(first, cache.Get(true, "a")) && not (obj.ReferenceEquals(first, cache.Get(false, "a"))) @>

[<Theory>]
// matches at positions 2, 5, 9
[<InlineData(-1, true, false, 0)>]
[<InlineData(-1, false, false, 2)>]
[<InlineData(5, true, false, 2)>]
[<InlineData(5, false, false, 0)>]
[<InlineData(5, true, true, 1)>]
[<InlineData(9, true, false, 0)>]
[<InlineData(2, false, false, 2)>]
[<InlineData(6, true, false, 2)>]
[<InlineData(6, false, false, 1)>]
[<InlineData(10, true, false, 0)>]
[<InlineData(0, false, false, 2)>]
let ``cycle steps to the nearest match with wrap-around`` (focus: int, forward: bool, includeFocus: bool, expected: int) =
    let direction = if forward then Forward else Backward
    test <@ Cycle.step [| 2; 5; 9 |] focus direction includeFocus = expected @>

[<Fact>]
let ``cycle positions and summaries`` () =
    test <@ Cycle.step [||] 3 Forward false = -1 @>
    test <@ Cycle.positions 6 (fun i -> i % 2 = 1) = [| 1; 3; 5 |] @>
    test <@ [ Cycle.summary 0 3; Cycle.summary -1 3; Cycle.summary -1 1; Cycle.summary -1 0 ] = [ "1 of 3"; "3 matches"; "1 match"; "No matches" ] @>

[<Fact>]
let ``file changes read from git's old and new paths`` () =
    test <@ FileChange.kind "/dev/null" "a" = FileChange.Added && FileChange.kind "a" "/dev/null" = FileChange.Deleted @>
    test <@ FileChange.kind "a" "b" = FileChange.Renamed && FileChange.kind "a" "a" = FileChange.Modified @>
    test <@ [ FileChange.path "/dev/null" "a"; FileChange.path "a" "/dev/null"; FileChange.path "a" "b" ] = [ "a"; "a"; "a -> b" ] @>
    test <@ [ FileChange.displayPath "/dev/null" "a"; FileChange.displayPath "a" "/dev/null"; FileChange.displayPath "a" "a" ] = [ "a (new file)"; "a (deleted)"; "a" ] @>

[<Fact>]
let ``diff find prefers your own text and borrows the search term otherwise`` () =
    let own = DiffFind.choose " foo " false "bar" false
    let borrowed = DiffFind.choose "" false "bar" true
    test <@ own.IsSome && own.Value.Source = DiffFind.Own && DiffFind.includesHeaders own.Value @>
    test <@ borrowed.IsSome && borrowed.Value.Source = DiffFind.SearchTerm && not (DiffFind.includesHeaders borrowed.Value) @>
    test <@ (DiffFind.choose " " false "" false).IsNone @>
    test <@ DiffFind.status borrowed.Value 1 4 = "2 of 4 · search" && DiffFind.status own.Value 1 4 = "2 of 4" @>
    test <@ DiffFind.status borrowed.Value -1 0 = "No matches" @>

[<Fact>]
let ``a search highlight splits terms by what they underline`` () =
    let highlight = GitSearch.highlight GitSearch.Commit false "fix author:ada path:src diff:todo"
    let counts = [ highlight.Subject; highlight.Hash; highlight.Ref; highlight.Author; highlight.Path; highlight.Line ] |> List.map List.length
    test <@ counts = [ 1; 1; 1; 1; 1; 1 ] && not highlight.IsEmpty @>
    let spans = GitSearch.spans highlight.Subject "Fix the fix" |> List.map (fun struct (s, l) -> s, l)
    test <@ spans = [ 0, 3; 8, 3 ] @>
    test <@ (GitSearch.highlight GitSearch.Commit false "").IsEmpty && (GitSearch.commitHighlight TextQuery.None).IsEmpty @>

[<Fact>]
let ``recent lists keep the newest first without duplicates`` () =
    test <@ Recent.add 3 "b" [ "a"; "b"; "c" ] = [ "b"; "a"; "c" ] && Recent.add 2 "d" [ "a"; "b" ] = [ "d"; "a" ] @>
    test <@ Recent.ofSeq 2 [ "a"; "a"; "b"; "c" ] = [ "a"; "b" ] && Recent.matching 1 ((<>) "a") [ "a"; "b"; "c" ] = [ "b" ] @>
    test <@ GitSearch.recentSearches [ " x "; ""; "x"; "y" ] = [ "x"; "y" ] @>
    test <@ GitSearch.rememberSearch "  " [ "x" ] = [ "x" ] && GitSearch.rememberSearch " y " [ "x"; "y" ] = [ "y"; "x" ] @>
    test <@ GitSearch.suggestSearches "fo" [ "foo"; "bar"; "fo"; "FOX" ] = [ "foo"; "FOX" ] @>

[<Fact>]
let ``field text reads and replaces one field of a query`` () =
    let query = "fix author:ada author:bob path:src"
    test <@ GitSearch.fieldText GitSearch.Commit query GitSearch.Author = "ada bob" @>
    test <@ GitSearch.withFieldText GitSearch.Commit query GitSearch.Author "Grace Hopper" = "fix path:src author:\"Grace Hopper\"" @>
    test <@ GitSearch.withFieldText GitSearch.Commit query GitSearch.ChangedPath " " = "fix author:ada author:bob" @>
    test <@ GitSearch.fieldNamed "Author" = GitSearch.Author && GitSearch.fieldNamed "subject" = GitSearch.CommitInfo @>
    test <@ GitSearch.firstTermText GitSearch.Diff "needle path:a" GitSearch.ChangedLine = Some "needle" @>

[<Fact>]
let ``a diff mark prefers the changed-line term and follows the search's regex setting`` () =
    let lines = GitSearch.diffMark GitSearch.Commit false "diff:todo path:src"
    let paths = GitSearch.diffMark GitSearch.Commit true "path:^src/"
    test <@ lines.Scope = GitSearch.ChangedLines && lines.Text = "todo" @>
    test <@ GitSearch.marksLine lines "a TODO here" && not (GitSearch.marksPath lines "src/a" "src/a" "src/a") @>
    test <@ paths.Scope = GitSearch.ChangedPaths && GitSearch.marksPath paths "/dev/null" "src/new.fs" "src/new.fs (new file)" @>
    test <@ not (GitSearch.marksPath paths "lib/src/x" "lib/src/x" "lib/src/x") && not (GitSearch.marksLine paths "src/") @>
    test <@ (GitSearch.diffMark GitSearch.Commit false "fix").Scope = GitSearch.AnyChange @>

[<Fact>]
let ``fuzzy ranking puts title matches first, then detail matches; a blank query keeps order`` () =
    let candidates = [ struct ("Show stashes", "refs"); struct ("Open file", "side panel"); struct ("View: side-by-side", "layout") ]
    test <@ Fuzzy.rank 10 "side" candidates = [ 2; 1 ] @>
    test <@ Fuzzy.rank 10 " " candidates = [ 0; 1; 2 ] && Fuzzy.rank 1 "" candidates = [ 0 ] @>
    test <@ Fuzzy.score "sbs" "side-by-side" > Fuzzy.score "sbs" "subsystems" && Fuzzy.score "" "x" = 0 @>

[<Fact>]
let ``navigation goes back and forward, skipping places that are gone`` () =
    let visited = Navigation.empty |> Navigation.visit "a" |> Navigation.visit "b" |> Navigation.visit "c" |> Navigation.visit "c"
    test <@ visited = { Back = [ "b"; "a" ]; Current = Some "c"; Forward = [] } @>
    let available place = place <> "b"
    let backed = Navigation.back available visited
    test <@ backed |> Option.map fst = Some "a" @>
    let _, atA = backed.Value
    test <@ atA = { Back = []; Current = Some "a"; Forward = [ "c" ] } && not (Navigation.canGoBack atA) @>
    let forwarded = Navigation.forward available atA
    test <@ forwarded |> Option.map (fun (place, nav) -> place, nav.Back, nav.Forward) = Some("c", [ "a" ], []) @>
    test <@ (Navigation.visit "d" atA).Forward = [] && Navigation.back (fun _ -> false) visited = None @>

[<Fact>]
let ``path trees merge single-folder chains, sort, and hide collapsed folders`` () =
    let entries =
        [ "src/App/Main.fs", Some 1
          "src/App/util.fs", None
          "dev-docs/releases/0.3.0.md", Some 2
          "README.md", None ]
    let describe row =
        match row with
        | FolderRow(name, _, depth, expanded, items) ->
            let marker = if expanded then "" else "+"
            $"{depth}:[{name}]{marker}{items}"
        | FileRow(name, _, depth, Some item) -> $"{depth}:{name}#{item}"
        | FileRow(name, _, depth, None) -> $"{depth}:{name}"
    let open' = PathTree.rows (fun _ _ -> true) entries |> List.map describe
    test <@ open' = [ "0:[dev-docs/releases][2]"; "1:0.3.0.md#2"; "0:[src/App][1]"; "1:Main.fs#1"; "1:util.fs"; "0:README.md" ] @>
    let changedOnly = PathTree.rows (fun path hasItem -> hasItem && path <> "src/App") entries |> List.map describe
    test <@ changedOnly = [ "0:[dev-docs/releases][2]"; "1:0.3.0.md#2"; "0:[src/App]+[1]"; "0:README.md" ] @>

[<Fact>]
let ``commit formatting: short hashes, ref summaries, badges and counted lists`` () =
    test <@ CommitFormat.shortHash "0123456789abcdef" = "01234567" && CommitFormat.shortHash "abc" = "abc" && CommitFormat.shortHash null = "" @>
    test <@ CommitFormat.refsSummary [ "a"; "b"; "c"; "d"; "e" ] = "a · b · c +2" && CommitFormat.refsSummary [] = "" @>
    let reference name kind head : Models.CommitRef = { Name = name; Kind = kind; IsCurrentHead = head }
    let refs =
        [ reference "feature" Models.CommitRefKind.Branch false
          reference "main" Models.CommitRefKind.Branch true
          reference "stash@{0}" Models.CommitRefKind.Stash false
          reference "origin/main" Models.CommitRefKind.Remote false
          reference "v2" Models.CommitRefKind.Tag false
          reference "V1" Models.CommitRefKind.Tag false ]
    test <@ CommitFormat.shownRefs false false refs |> List.map _.Name = [ "V1"; "v2"; "main"; "origin/main" ] @>
    test <@ CommitFormat.shownRefs true true refs |> List.map _.Name = [ "V1"; "v2"; "main"; "feature"; "origin/main"; "stash@{0}" ] @>
    test <@ CommitFormat.countedList "File" [ "a"; "A"; "b"; "c"; "d" ] = "Files (4): a, b, c +1 more" @>
    test <@ CommitFormat.countedList "Ref" [ "x" ] = "Ref (1): x" && CommitFormat.countedList "Ref" [] = "" @>

[<Fact>]
let ``match kinds have summary keys and list labels`` () =
    test <@ GitSearch.matchKindLabel GitSearch.PathMatch = "File / path" && GitSearch.matchKindKey GitSearch.TextMatch = "text" @>

[<Fact>]
let ``search progress reads as a status line`` () =
    let texts =
        [ GitSearch.NotSearching
          GitSearch.Searching(0, None)
          GitSearch.Searching(12, Some(21, 50))
          GitSearch.Searching(0, Some(0, 0))
          GitSearch.Searched(12, 2)
          GitSearch.Searched(12, -1)
          GitSearch.Searched(0, -1) ]
        |> List.map GitSearch.progressText
    test <@ texts = [ ""; "Searching…"; "12 so far · 42%"; "Searching…"; "3 of 12"; "12 matches"; "No matches" ] @>

module DiffLayoutTests =
    open GitKay.Core.DiffRows

    let private kindOf (line: string) =
        match line[0] with
        | '+' -> Models.Added
        | '-' -> Models.Removed
        | _ -> Models.Context

    let private blocks =
        [ Gap "top"
          Hunk("@@1", [ " a"; "-b"; "+B"; "+c" ])
          Hunk("@@2", [ "-gone" ])
          Gap "middle"
          Hunk("@@3", [ " d"; "+e" ]) ]

    let private describe row =
        match row with
        | GapRow(gap, Some hunk) -> $"gap {gap} {hunk}"
        | GapRow(gap, None) -> $"gap {gap}"
        | HunkHeaderRow hunk -> hunk
        | LineRow line -> line
        | PairRow(removed, added) -> $"{removed}|{added}"

    [<Fact>]
    let ``unified keeps every line; a header after a gap merges into it`` () =
        test <@ layout Unified kindOf blocks |> List.map describe = [ "gap top @@1"; " a"; "-b"; "+B"; "+c"; "@@2"; "-gone"; "gap middle @@3"; " d"; "+e" ] @>

    [<Fact>]
    let ``side by side pairs a removed line with the added line after it`` () =
        test <@ layout SideBySide kindOf blocks |> List.map describe = [ "gap top @@1"; " a"; "-b|+B"; "+c"; "@@2"; "-gone"; "gap middle @@3"; " d"; "+e" ] @>

    [<Fact>]
    let ``new and old file layouts drop the other side, and empty hunks disappear with their header`` () =
        test <@ layout NewFile kindOf blocks |> List.map describe = [ "gap top @@1"; " a"; "+B"; "+c"; "gap middle @@3"; " d"; "+e" ] @>
        test <@ layout OldFile kindOf blocks |> List.map describe = [ "gap top @@1"; " a"; "-b"; "@@2"; "-gone"; "gap middle @@3"; " d" ] @>

module DiffNavigationTests =
    open GitKay.Core.DiffNavigation

    let private row kind oldLine newLine = { Kind = kind; OldLine = oldLine; NewLine = newLine }
    let private rows =
        [| row FileHeader -1 -1       // 0
           row HunkHeader -1 -1       // 1
           row Line 10 10             // 2
           row Line 11 -1             // 3
           row Line -1 11             // 4
           row CollapsedGap -1 -1     // 5
           row Line 40 42             // 6
           row FileHeader -1 -1       // 7
           row HunkHeader -1 -1       // 8
           row Line 1 1 |]            // 9

    [<Fact>]
    let ``go to line finds the line in the focused file, or the next shown one`` () =
        test <@ goToLine rows 3 Unified 11 = 4 && goToLine rows 3 OldFile 11 = 3 @>
        test <@ goToLine rows 2 Unified 20 = 6 && goToLine rows 2 Unified 99 = -1 @>
        test <@ goToLine rows 9 Unified 1 = 9 && goToLine rows -1 Unified 10 = 2 @>

    [<Fact>]
    let ``hunk targets land on the first line after a header or gap`` () =
        test <@ hunkTarget rows -1 true = 2 && hunkTarget rows 2 true = 6 && hunkTarget rows 6 true = 9 @>
        test <@ hunkTarget rows 9 true = -1 && hunkTarget rows 6 false = 2 && hunkTarget rows -1 false = 9 @>

    [<Fact>]
    let ``selections order, extend like vim, and copy only line text`` () =
        let texts = [| null; null; "alpha"; "beta"; "gamma"; null; "delta"; null; null; "one" |]
        let lengthAt row = match texts[row] with null -> 0 | text -> text.Length
        let position r c = { Row = r; Column = c }
        test <@ ordered (position 4 1) (position 2 3) = struct (position 2 3, position 4 1) @>
        test <@ characterwise (position 4 1) (position 2 3) lengthAt = struct (position 2 3, position 4 2) @>
        test <@ linewise rows 1 5 lengthAt = ValueSome(struct (position 2 0, position 4 5)) && linewise rows 5 5 lengthAt = ValueNone @>
        let struct (first, last) = characterwise (position 2 3) (position 6 1) lengthAt
        test <@ selectedText first last (fun row -> texts[row]) = "ha\nbeta\ngamma\nde" @>
        test <@ DiffText.changedSpan "let x = 1" "let x = 42" = ValueSome(struct (8, 1, 2)) @>
        test <@ DiffText.changedSpan "same" "same" = ValueNone && DiffText.changedSpan "" "x" = ValueNone @>
