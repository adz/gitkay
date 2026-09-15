namespace GitKay.Core

open GitKay.Kit

/// <summary>
/// Find-in-diff: what to look for, which rows it looks at, and how its position reads.
/// </summary>
module DiffFind =

    /// <summary>Where the text being found came from.</summary>
    type Source =
        /// <summary>Typed in the find box or at the / prompt.</summary>
        | Own
        /// <summary>Borrowed from the applied commit search's diff term while the find box is empty.</summary>
        | SearchTerm

    type Find = { Query: TextQuery; Source: Source }

    /// <summary>
    /// Your own find text wins; when it's blank, the commit search's diff term is used without writing it into the box.
    /// None when both are blank.
    /// </summary>
    let choose (ownText: string) (ownRegex: bool) (searchTerm: string) (searchRegex: bool) : Find option =
        let own = TextQuery.Create(ownRegex, (if isNull ownText then "" else ownText.Trim()))
        if not own.IsEmpty then Some { Query = own; Source = Own }
        else
            let borrowed = TextQuery.Create(searchRegex, (if isNull searchTerm then "" else searchTerm.Trim()))
            if borrowed.IsEmpty then None else Some { Query = borrowed; Source = SearchTerm }

    /// <summary>
    /// Whether a find also stops at file and hunk headers. Your own text does, so file names can be found; a borrowed
    /// search term is a changed-line term, so it stops only at changed lines.
    /// </summary>
    let includesHeaders (find: Find) = find.Source = Own

    /// <summary>"3 of 12", marked "· search" when the text was borrowed from the commit search.</summary>
    let status (find: Find) (index: int) (count: int) =
        let summary = Cycle.summary index count
        match find.Source with
        | SearchTerm when count > 0 -> summary + " · search"
        | _ -> summary
