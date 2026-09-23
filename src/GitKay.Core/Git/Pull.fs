namespace GitKay.Core

/// <summary>Human-readable summary of Git's configured pull behavior.</summary>
module Pull =
    let private isValue expected (value: string option) =
        value |> Option.exists (fun text -> text.Trim().ToLowerInvariant() = expected)

    let describe (branchRebase: string option) (pullRebase: string option) (pullFf: string option) (pullSquash: string option) =
        let rebase = branchRebase |> Option.orElse pullRebase
        let integration =
            if isValue "true" pullSquash then "squash into the working tree"
            elif isValue "true" rebase then "rebase local commits"
            elif isValue "merges" rebase || isValue "m" rebase then "rebase local commits and preserve merges"
            elif isValue "interactive" rebase || isValue "i" rebase then "interactive rebase"
            elif isValue "false" rebase then "merge when branches diverge"
            else "Git default; divergent branches may require a configured method"

        let fastForward =
            if isValue "only" pullFf then "fast-forward only"
            elif isValue "false" pullFf then "create a merge commit instead of fast-forwarding when merging"
            elif isValue "true" pullFf then "fast-forward when possible"
            else "Git default fast-forward policy"

        $"Pull behavior: {integration}; {fastForward}."
