namespace GitKay.Core

/// <summary>
/// What happened to a file between two trees, from its old and new paths as git reports them: a missing side is
/// <c>/dev/null</c>.
/// </summary>
module FileChange =

    type Kind =
        | Added
        | Deleted
        | Renamed
        | Modified

    /// <summary>
    /// The path git writes for a side a file does not have. Everything that asks "is this side there?" asks here,
    /// so the sentinel is spelled once instead of compared inline wherever a path is handled.
    /// </summary>
    [<Literal>]
    let missing = "/dev/null"

    /// <summary>Whether a side exists at all. An added file has no old side; a deleted one has no new side.</summary>
    let isMissing (path: string) = path = missing

    let kind (oldPath: string) (newPath: string) =
        if isMissing oldPath then Added
        elif isMissing newPath then Deleted
        elif oldPath <> newPath then Renamed
        else Modified

    let isAdded (oldPath: string) (newPath: string) = kind oldPath newPath = Added
    let isDeleted (oldPath: string) (newPath: string) = kind oldPath newPath = Deleted
    let isRenamed (oldPath: string) (newPath: string) = kind oldPath newPath = Renamed

    /// <summary>Both paths the file is known by, without the side it does not have and without repeating itself.</summary>
    let paths (oldPath: string) (newPath: string) =
        [ oldPath; newPath ] |> List.filter (isMissing >> not) |> List.distinct

    /// <summary>"added", "deleted", "renamed" or "modified".</summary>
    let kindName kind =
        match kind with
        | Added -> "added"
        | Deleted -> "deleted"
        | Renamed -> "renamed"
        | Modified -> "modified"

    /// <summary>A one-character marker for lists: + − → •.</summary>
    let glyph kind =
        match kind with
        | Added -> "+"
        | Deleted -> "−"
        | Renamed -> "→"
        | Modified -> "•"

    /// <summary>Where the file is now, or where it was when it was deleted.</summary>
    let currentPath (oldPath: string) (newPath: string) = if isMissing newPath then oldPath else newPath

    /// <summary>Where the file was before, or where it is now for a file that was only just added.</summary>
    let previousPath (oldPath: string) (newPath: string) = if isMissing oldPath then newPath else oldPath

    /// <summary>The path a reader knows the file by: "old -> new" for renames, otherwise the side that exists.</summary>
    let path (oldPath: string) (newPath: string) =
        match kind oldPath newPath with
        | Added | Modified -> newPath
        | Deleted -> oldPath
        | Renamed -> $"{oldPath} -> {newPath}"

    /// <summary>The path with what happened to it, for diff headers: "a.txt (new file)", "b.txt (deleted)".</summary>
    let displayPath (oldPath: string) (newPath: string) =
        match kind oldPath newPath with
        | Added -> newPath + " (new file)"
        | Deleted -> oldPath + " (deleted)"
        | Renamed | Modified -> path oldPath newPath
