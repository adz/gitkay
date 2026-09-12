namespace GitKay.Core

open System
open System.IO
open LibGit2Sharp

/// Host startup implementation for locating the repository GitKay should open.
module internal RepositoryDiscovery =
    let private candidateRoots () =
        seq {
            yield Environment.CurrentDirectory // axial-allow-effect: environment
            yield AppContext.BaseDirectory

            match Environment.ProcessPath with
            | null -> ()
            | processPath ->
                yield Path.GetDirectoryName processPath

                try
                    let processFile = FileInfo processPath
                    match processFile.ResolveLinkTarget true with
                    | null -> ()
                    | resolved -> yield Path.GetDirectoryName resolved.FullName
                with _ ->
                    ()
        }
        |> Seq.choose (fun path ->
            if String.IsNullOrWhiteSpace path then None else Some path)
        |> Seq.distinct
        |> Seq.toList

    let discover () =
        let rec tryRoots attempted = function
            | [] -> Error (GitError.RepositoryNotFound(List.rev attempted))
            | root :: remaining ->
                try
                    match Repository.Discover root with
                    | repoPath when String.IsNullOrWhiteSpace repoPath ->
                        tryRoots (root :: attempted) remaining
                    | repoPath ->
                        Ok (Path.TrimEndingDirectorySeparator(Path.GetFullPath repoPath))
                with _ ->
                    tryRoots (root :: attempted) remaining

        let roots = candidateRoots ()
        tryRoots [] roots
