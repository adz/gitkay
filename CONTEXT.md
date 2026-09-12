# GitKay Domain Context

## Commit history
An ordered projection of commits reachable from the selected startup targets. History loading may be bounded initially and expanded later.

## Startup target
A branch, tag, commit SHA, revision, or all refs that defines the roots of commit history traversal.

## Commit graph
The lane and edge projection derived from commit parent relationships. It is presentation data, not repository state.

## Commit selection
The currently focused commit. Selection changes immediately; expensive diff hydration follows as cancellable latest-wins work.

## Diff summary
Cheap commit-level change metadata and the changed-file list.

## File diff
The lazily hydrated patch for one changed file at a configured context size.

## Blame
File-scoped line attribution loaded explicitly rather than as part of commit selection.

## Search
A query over commit hash, message, author, changed paths, diff text, and refs. Search may require lazy diff hydration.

## Git environment
The explicit capabilities supplied to Git workflows: repository path, runtime services, process execution, and diff-cache ownership. Operational effects must enter through this environment.

## Git failure
An expected failure while discovering or operating on a repository. Git failures remain structured as `GitError` through Git workflows and Elmish messages; text is produced only for UI status and diagnostic rendering.

## Repository discovery
The host-startup operation that locates the repository GitKay should open. Its platform-specific implementation is hidden under the Git entry module rather than mixed with repository operations.

## Persisted settings
User preferences and UI state admitted and encoded through Reified schemas. Wire defaults and field names belong to those schemas.
