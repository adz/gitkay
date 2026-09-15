namespace GitKay.Kit

/// <summary>Which way to step through matches.</summary>
type Direction =
    | Forward
    | Backward

/// <summary>
/// Stepping through matches in a list with wrap-around, as find-next does: from a focused item that may or may not be
/// a match, to the nearest match in a direction.
/// </summary>
module Cycle =

    /// <summary>
    /// The next match, as an index into <paramref name="matches"/>: the ascending positions of the matching items.
    /// </summary>
    /// <param name="focus">The focused item's position, or -1 for none.</param>
    /// <param name="includeFocus">Whether a matching focused item is itself the answer (an incremental search) rather than the start.</param>
    /// <returns>-1 when there are no matches.</returns>
    let step (matches: int[]) (focus: int) (direction: Direction) (includeFocus: bool) : int =
        let count = matches.Length
        if count = 0 then -1
        elif focus < 0 then (match direction with Forward -> 0 | Backward -> count - 1)
        else
            match System.Array.BinarySearch(matches, focus), direction with
            | found, _ when found >= 0 && includeFocus -> found
            | found, Forward when found >= 0 -> (found + 1) % count
            | found, Backward when found >= 0 -> (found - 1 + count) % count
            // Not a match: ~found is where it would be inserted, the first match after it.
            | notFound, Forward -> let after = ~~~notFound in if after < count then after else 0
            | notFound, Backward -> let after = ~~~notFound in if after > 0 then after - 1 else count - 1

    /// <summary>The ascending positions of the items satisfying <paramref name="isMatch"/>.</summary>
    let positions (count: int) (isMatch: int -> bool) : int[] =
        [| for index in 0 .. count - 1 do
               if isMatch index then index |]

    /// <summary><see cref="positions"/> for callers passing a .NET delegate.</summary>
    let positionsWhere (count: int) (isMatch: System.Func<int, bool>) = positions count isMatch.Invoke

    /// <summary>"3 of 12" for the match at <paramref name="index"/>; "12 matches" without one; "No matches" when there are none.</summary>
    let summary (index: int) (count: int) =
        if count = 0 then "No matches"
        elif index >= 0 then $"{index + 1} of {count}"
        elif count = 1 then "1 match"
        else $"{count} matches"
