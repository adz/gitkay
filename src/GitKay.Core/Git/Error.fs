namespace GitKay.Core

open Axial.Process

/// Expected failures produced by Git operations.
type GitError =
    | RepositoryNotFound of searchedPaths: string list
    | CommitNotFound of hash: string
    | BranchNotFound of name: string
    | TagNotFound of name: string
    | RevisionNotFound of revision: string
    | InvalidRevision of revision: string
    | TagDoesNotPointToCommit of name: string
    | DiffFileNotFound of hash: string * oldPath: string * newPath: string
    | MultipleDiffFilesMatched of hash: string * oldPath: string * newPath: string
    | CommitterIdentityMissing
    | EmptySearchQuery
    | OperationCanceled of operation: string
    | GitProcessFailed of arguments: string list * error: ProcessError
    | OperationFailed of operation: string * status: string

    /// Hand-written ToString: the compiler-generated one uses reflection that NativeAOT removes, and generic
    /// diagnostics (such as `Cause.prettyPrint`) render errors through ToString.
    override this.ToString() =
        match this with
        | RepositoryNotFound paths ->
            let searchedPaths = System.String.Join(", ", paths)
            $"Could not locate a Git repository. Tried: {searchedPaths}"
        | CommitNotFound hash -> $"Commit not found: {hash}"
        | BranchNotFound name -> $"Branch not found: {name}"
        | TagNotFound name -> $"Tag not found: {name}"
        | RevisionNotFound revision -> $"Revision not found: {revision}"
        | InvalidRevision revision -> $"Invalid revision: {revision}"
        | TagDoesNotPointToCommit name -> $"Tag does not point to a commit: {name}"
        | DiffFileNotFound(hash, oldPath, newPath) ->
            $"File not found in commit {hash}: {oldPath} -> {newPath}"
        | MultipleDiffFilesMatched(hash, oldPath, newPath) ->
            $"Multiple files matched in commit {hash}: {oldPath} -> {newPath}"
        | CommitterIdentityMissing ->
            "Could not determine the git user identity. Configure user.name and user.email."
        | EmptySearchQuery -> "Search query must not be empty."
        | OperationCanceled operation -> $"{operation} canceled."
        | GitProcessFailed(_, error) -> ProcessError.describe error
        | OperationFailed(operation, status) -> $"{operation} failed: {status}"

[<RequireQualifiedAccess>]
module GitError =
    let describe (error: GitError) = error.ToString()
