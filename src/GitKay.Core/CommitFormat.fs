namespace GitKay.Core

open System

/// <summary>How commits and their refs read in lists.</summary>
module CommitFormat =

    /// <summary>The first eight characters of a hash, or all of a shorter one.</summary>
    let shortHash (hash: string) =
        if isNull hash then "" elif hash.Length > 8 then hash.Substring(0, 8) else hash

    /// <summary>Up to three names joined with " · ", then "+N" for the rest: "main · v1.0 +2".</summary>
    let refsSummary (names: string list) =
        let shown = names |> List.truncate 3
        let hidden = names.Length - shown.Length
        match String.Join(" · ", shown), hidden with
        | summary, 0 -> summary
        | "", _ -> $"+{hidden}"
        | summary, _ -> $"{summary} +{hidden}"

    /// <summary>
    /// Whether a ref shows as a badge: tags and remote branches always; local branches when branch refs are on, or when
    /// it's the checked-out branch; stashes when stashes are on.
    /// </summary>
    let isRefShown (showBranchRefs: bool) (showStashes: bool) (reference: Models.CommitRef) =
        match reference.Kind with
        | Models.CommitRefKind.Stash -> showStashes
        | Models.CommitRefKind.Branch -> showBranchRefs || reference.IsCurrentHead
        | _ -> true

    /// <summary>The refs shown as badges, in badge order: tags, the checked-out branch, then by kind and name.</summary>
    let shownRefs (showBranchRefs: bool) (showStashes: bool) (refs: Models.CommitRef list) =
        refs
        |> List.filter (isRefShown showBranchRefs showStashes)
        |> List.sortWith (fun a b ->
            compare (a.Kind <> Models.CommitRefKind.Tag) (b.Kind <> Models.CommitRefKind.Tag)
            |> fun c -> if c <> 0 then c else compare (not a.IsCurrentHead) (not b.IsCurrentHead)
            |> fun c -> if c <> 0 then c else compare (int a.Kind) (int b.Kind)
            |> fun c -> if c <> 0 then c else StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name))

    /// <summary>"Files (5): a, b, c +2 more" — a count, up to three values, and how many more; empty for none.</summary>
    let countedList (singular: string) (values: string list) =
        let values = values |> List.distinctBy (fun value -> value.ToLowerInvariant())
        match values with
        | [] -> ""
        | _ ->
            let label = if values.Length = 1 then singular else singular + "s"
            let shown = values |> List.truncate 3
            let more = if values.Length > shown.Length then $" +{values.Length - shown.Length} more" else ""
            $"""{label} ({values.Length}): {String.Join(", ", shown)}{more}"""
