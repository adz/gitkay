# GitKay Tasks

## Done
- Repo foundation, `.slnx`, `mise`, .NET 10.0, Avalonia v12, F# core, C# UI, tests, and centralized artifacts.
- Three-pane shell, menu bar, commit history loading, graph rendering, commit selection, metadata pane, and diff pane.
- Commit context-menu operations: tag, branch, cherry-pick, reset, and revert.
- Structured diff parsing and a first-pass inline blame view.

## In Progress
- Keep commit selection fast and predictable.
- Reduce backend work to the minimum needed for the currently selected item.

## Next
- [ ] Replace the CLI Git backend with a libgit2-based backend.
- [ ] Make blame lazy, file-scoped, and cancelable.
- [ ] Add file selection in the diff view that focuses the left pane.
- [ ] Add search UI and search plumbing:
  - [ ] commit hash search
  - [ ] commit message/subject search
  - [ ] author search
  - [ ] file/path search
  - [ ] text search across commit contents and diffs
  - [ ] ref/tag/branch search
- [ ] Implement smooth scrolling, selection, and keyboard navigation in the commit tree.
- [ ] Draw proper commit graph edges for branches and merges.
- [ ] Improve graph rendering performance on larger histories.
- [ ] Add a top-level error dialog for unrecoverable failures.
- [ ] Finish UI/UX polish to get closer to gitk parity.

