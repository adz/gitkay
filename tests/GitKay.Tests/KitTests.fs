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
