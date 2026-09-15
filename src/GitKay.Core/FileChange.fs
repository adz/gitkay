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

    [<Literal>]
    let private Missing = "/dev/null"

    let kind (oldPath: string) (newPath: string) =
        if oldPath = Missing then Added
        elif newPath = Missing then Deleted
        elif oldPath <> newPath then Renamed
        else Modified

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
    let currentPath (oldPath: string) (newPath: string) = if newPath = Missing then oldPath else newPath

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
