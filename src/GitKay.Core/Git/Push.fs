namespace GitKay.Core

/// <summary>
/// What pushing a branch should actually do. Pure: it decides from the branch's configuration, so the rule can be
/// tested without a remote.
/// </summary>
module Push =

    type Plan =
        /// <summary>Push the branch to its upstream, which carries the same name.</summary>
        | ToUpstream of remote: string * refspec: string
        /// <summary>The branch has no upstream yet: push it and set one.</summary>
        | SetUpstream of remote: string * branch: string
        /// <summary>
        /// The branch tracks an upstream under a different name. Pushing would write this branch onto that other
        /// branch, which is almost never what "push" is meant to do, so it is refused with what to do instead.
        /// </summary>
        | Refused of reason: string
        | NoRemote

    /// <summary>The branch name a <c>refs/heads/…</c> merge ref names, or the ref itself when it is not one.</summary>
    let upstreamBranchName (merge: string) =
        let prefix = "refs/heads/"
        if merge.StartsWith prefix then merge.Substring prefix.Length else merge

    /// <summary>
    /// How to push <paramref name="branch"/>. A tracking branch whose upstream has a different name is refused:
    /// git's own default (<c>push.default=simple</c>) refuses it too, because pushing `feature` onto `main` is a
    /// mistake far more often than an intention, and `git checkout -b feature origin/main` configures exactly that.
    /// </summary>
    let plan (branch: string) (remote: string option) (merge: string option) (remotes: string list) : Plan =
        match remote, merge with
        | Some remote, Some merge ->
            let upstream = upstreamBranchName merge
            if upstream = branch then ToUpstream(remote, $"refs/heads/{branch}:{merge}")
            else
                let advice = $"git branch --set-upstream-to={remote}/{branch} {branch}"
                Refused $"{branch} tracks {remote}/{upstream}: pushing it would write {branch} onto {upstream}. Point it at its own branch first with: {advice}"
        | _ ->
            match remotes |> List.tryFind ((=) "origin") |> Option.orElse (List.tryHead remotes) with
            | Some target -> SetUpstream(target, branch)
            | None -> NoRemote
