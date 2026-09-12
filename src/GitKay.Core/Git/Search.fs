namespace GitKay.Core

open System
open Axial
open GitKay.Core.Models

/// Search vocabulary shared by the Git query engine and application model.
module GitSearch =
    type Scope =
        | All
        | Hash
        | Message
        | Author
        | Path
        | Text
        | Ref

    type Result =
        { Commit: Models.Commit
          MatchKinds: string list
          MatchSummary: string
          MatchedPaths: string list
          MatchedRefs: string list }

    let private containsIgnoreCase (haystack: string) (needle: string) =
        haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0

    let private buildDisplayPath oldPath newPath =
        if oldPath = newPath then newPath
        elif oldPath = "/dev/null" then newPath
        elif newPath = "/dev/null" then oldPath
        else sprintf "%s -> %s" oldPath newPath

    let private buildSearchSummary (matchKinds: string list) (paths: string list) (refs: string list) =
        let details = System.Collections.Generic.List<string>()

        if matchKinds |> List.contains "hash" then details.Add "hash"
        if matchKinds |> List.contains "message" then details.Add "message"
        if matchKinds |> List.contains "author" then details.Add "author"
        if matchKinds |> List.contains "path" then details.Add "path"
        if matchKinds |> List.contains "text" then details.Add "text"
        if matchKinds |> List.contains "ref" then details.Add "ref"

        if paths.Length > 0 then
            details.Add (sprintf "paths: %s" (String.Join(", ", paths)))

        if refs.Length > 0 then
            details.Add (sprintf "refs: %s" (String.Join(", ", refs)))

        String.Join("; ", details)

    let private searchCommitDiff
        (query: string)
        (files: FileDiff list)
        (searchPaths: bool)
        (searchText: bool) =
        let matchedPaths = System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
        let mutable textMatched = false

        for file in files do
            if searchPaths && (containsIgnoreCase file.OldPath query || containsIgnoreCase file.NewPath query || containsIgnoreCase (buildDisplayPath file.OldPath file.NewPath) query) then
                matchedPaths.Add (buildDisplayPath file.OldPath file.NewPath) |> ignore

            if searchText && not textMatched then
                let fileTextMatched =
                    file.Hunks
                    |> List.exists (fun hunk ->
                        hunk.Lines
                        |> List.exists (fun line -> containsIgnoreCase line.Content query))

                if fileTextMatched then
                    textMatched <- true

        let pathMatches = matchedPaths |> Seq.toList

        if (searchPaths && pathMatches.Length > 0) || (searchText && textMatched) then
            Some(pathMatches, textMatched)
        else
            None

    let searchCommitsWithDiffLoader
        (commits: Models.Commit list)
        (query: string)
        (scope: Scope)
        (loadDiff: string -> Flow<'env, GitError, FileDiff list>) : Flow<'env, GitError, Result list> =
        flow {
            let normalizedQuery = query.Trim()

            if String.IsNullOrWhiteSpace normalizedQuery then
                return! Error GitError.EmptySearchQuery
            else
                let results = ResizeArray<Result>()
                
                for commit in commits do
                    do! Flow.Runtime.ensureNotCanceled (GitError.OperationCanceled "Search")
                    
                    let matchKinds = System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    let matchedPaths = System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    let matchedRefs = System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)

                    let addMetadataMatches () =
                        match scope with
                        | Scope.Hash ->
                            if containsIgnoreCase commit.Hash normalizedQuery then
                                matchKinds.Add "hash" |> ignore
                        | Scope.Message ->
                            if containsIgnoreCase commit.Subject normalizedQuery || containsIgnoreCase commit.Message normalizedQuery then
                                matchKinds.Add "message" |> ignore
                        | Scope.Author ->
                            if containsIgnoreCase commit.AuthorName normalizedQuery || containsIgnoreCase commit.AuthorEmail normalizedQuery then
                                matchKinds.Add "author" |> ignore
                        | Scope.Ref ->
                            let refMatches =
                                commit.Refs
                                |> List.filter (fun reference -> containsIgnoreCase reference.Name normalizedQuery)

                            if refMatches.Length > 0 then
                                matchKinds.Add "ref" |> ignore
                                refMatches |> List.iter (fun reference -> matchedRefs.Add reference.Name |> ignore)
                        | Scope.Path
                        | Scope.Text ->
                            ()
                        | Scope.All ->
                            if containsIgnoreCase commit.Hash normalizedQuery then
                                matchKinds.Add "hash" |> ignore

                            if containsIgnoreCase commit.Subject normalizedQuery || containsIgnoreCase commit.Message normalizedQuery then
                                matchKinds.Add "message" |> ignore

                            if containsIgnoreCase commit.AuthorName normalizedQuery || containsIgnoreCase commit.AuthorEmail normalizedQuery then
                                matchKinds.Add "author" |> ignore

                            let refMatches =
                                commit.Refs
                                |> List.filter (fun reference -> containsIgnoreCase reference.Name normalizedQuery)

                            if refMatches.Length > 0 then
                                matchKinds.Add "ref" |> ignore
                                refMatches |> List.iter (fun reference -> matchedRefs.Add reference.Name |> ignore)

                    addMetadataMatches ()

                    match scope with
                    | Scope.Path
                    | Scope.Text
                    | Scope.All ->
                        let searchPaths, searchText =
                            match scope with
                            | Scope.Path -> true, false
                            | Scope.Text -> false, true
                            | Scope.All -> true, true
                            | _ -> false, false

                        let! diffResult = loadDiff commit.Hash
                        match searchCommitDiff normalizedQuery diffResult searchPaths searchText with
                        | Some(pathMatches, textMatched) ->
                            if pathMatches.Length > 0 then
                                matchKinds.Add "path" |> ignore
                                pathMatches |> List.iter (fun path -> matchedPaths.Add path |> ignore)

                            if textMatched then
                                matchKinds.Add "text" |> ignore
                        | None ->
                            ()
                    | _ ->
                        ()

                    if matchKinds.Count > 0 then
                        let kinds = matchKinds |> Seq.toList
                        let paths = matchedPaths |> Seq.toList
                        let refs = matchedRefs |> Seq.toList

                        results.Add
                            {
                                Commit = commit
                                MatchKinds = kinds
                                MatchSummary = buildSearchSummary kinds paths refs
                                MatchedPaths = paths
                                MatchedRefs = refs
                            }

                return List.ofSeq results
        }

