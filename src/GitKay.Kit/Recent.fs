namespace GitKay.Kit

/// <summary>A most-recent-first list without duplicates, capped at a size: recent searches, recent files.</summary>
module Recent =

    /// <summary>Puts <paramref name="item"/> first, removing an earlier copy and anything past <paramref name="limit"/>.</summary>
    let add (limit: int) (item: 'item) (items: 'item list) : 'item list =
        item :: List.filter ((<>) item) items |> List.truncate (max 0 limit)

    /// <summary>A list from stored items, most recent first: duplicates keep their first position.</summary>
    let ofSeq (limit: int) (items: 'item seq) : 'item list =
        items |> Seq.distinct |> Seq.truncate (max 0 limit) |> List.ofSeq

    /// <summary>Up to <paramref name="limit"/> items satisfying <paramref name="isMatch"/>, most recent first.</summary>
    let matching (limit: int) (isMatch: 'item -> bool) (items: 'item list) : 'item list =
        items |> List.filter isMatch |> List.truncate (max 0 limit)
