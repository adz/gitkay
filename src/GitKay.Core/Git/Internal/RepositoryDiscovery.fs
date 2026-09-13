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

    /// Walks up from a start directory to the first repository and returns its git directory, matching
    /// Repository.Discover. Discover itself returns null under NativeAOT, while IsValid and opening work.
    let private discoverFrom (start: string) =
        let rec walk (directory: DirectoryInfo) =
            if isNull directory then
                None
            elif Repository.IsValid directory.FullName then
                use repo = new Repository(directory.FullName)
                Some repo.Info.Path
            else
                walk directory.Parent

        walk (DirectoryInfo start)

    let discover () =
        let rec tryRoots attempted = function
            | [] -> Error (GitError.RepositoryNotFound(List.rev attempted))
            | root :: remaining ->
                try
                    match discoverFrom root with
                    | Some repoPath when not (String.IsNullOrWhiteSpace repoPath) ->
                        Ok (Path.TrimEndingDirectorySeparator(Path.GetFullPath repoPath))
                    | _ ->
                        tryRoots (root :: attempted) remaining
                with _ ->
                    tryRoots (root :: attempted) remaining

        let roots = candidateRoots ()
        tryRoots [] roots
