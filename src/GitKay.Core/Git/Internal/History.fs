namespace GitKay.Core

open System
open LibGit2Sharp
open GitKay.Core.Models

/// Repository traversal details hidden behind the GitService history entry point.
module internal History =
    let commitRefs includeStashes (repo: Repository) =
        let refsByCommit = Collections.Generic.Dictionary<string, Collections.Generic.HashSet<CommitRef>>()

        let addRef hash kind name isCurrentHead =
            if not (String.IsNullOrWhiteSpace hash) && not (String.IsNullOrWhiteSpace name) then
                let bucket =
                    match refsByCommit.TryGetValue hash with
                    | true, existing -> existing
                    | false, _ ->
                        let created = Collections.Generic.HashSet<CommitRef>()
                        refsByCommit.[hash] <- created
                        created

                bucket.Add { Name = name; Kind = kind; IsCurrentHead = isCurrentHead } |> ignore

        for branch in repo.Branches do
            if not (isNull branch.Tip)
               && not (branch.IsRemote && branch.FriendlyName.EndsWith("/HEAD", StringComparison.OrdinalIgnoreCase)) then
                let kind = if branch.IsRemote then CommitRefKind.Remote else CommitRefKind.Branch
                addRef branch.Tip.Sha kind branch.FriendlyName branch.IsCurrentRepositoryHead

        for tag in repo.Tags do
            match tag.PeeledTarget with
            | :? LibGit2Sharp.Commit as commit -> addRef commit.Sha CommitRefKind.Tag tag.FriendlyName false
            | _ -> ()

        if includeStashes then
            repo.Stashes
            |> Seq.mapi (fun index stash -> index, stash)
            |> Seq.iter (fun (index, stash) ->
                if not (isNull stash.WorkTree) then
                    addRef stash.WorkTree.Sha CommitRefKind.Stash (sprintf "stash@{%d}" index) false)

        refsByCommit
        |> Seq.map (fun pair ->
            pair.Key,
            pair.Value
            |> Seq.sortWith (fun left right ->
                match compare (int left.Kind) (int right.Kind) with
                | 0 -> StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name)
                | comparison -> comparison)
            |> Seq.toList)
        |> Map.ofSeq

    let defaultRoots includeStashes (repo: Repository) =
        seq {
            yield!
                repo.Refs
                |> Seq.filter (fun reference ->
                    not (String.Equals(reference.CanonicalName, "refs/stash", StringComparison.Ordinal)))
                |> Seq.map box

            if includeStashes then
                yield!
                    repo.Stashes
                    |> Seq.choose (fun stash ->
                        if isNull stash.WorkTree then None else Some(box stash.WorkTree))
        }
