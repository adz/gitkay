# GitKay Tasks

## Done
- Repo foundation, `.slnx`, `mise`, .NET 10.0, Avalonia v12, F# core, C# UI, tests, and centralized artifacts.
- Commit history loading, lane-based graph rendering, commit selection, metadata pane, diff pane, and context-menu git operations.
- Structured diff parsing and the first inline blame pass.
- Swapped the hot read path from CLI Git to LibGit2Sharp for history and diff reads.

## Target
- Match gitk’s interaction model, not just its feature set.
- Clicking a commit must update selection and scroll/focus state immediately.
- The click path must not wait on full diff generation, full diff projection, or blame.
- The UI must only realize what is needed for the current viewport or selected item.

## Current Bottlenecks
- `src/GitKay.Core/App.fs` still ties commit selection to full diff loading.
- `src/GitKay.Core/GitService.fs` still returns a whole parsed diff blob for each selection.
- `src/GitKay.UI/MainProjection.cs` rebuilds the full diff projection tree on every update.
- `src/GitKay.UI/MainWindow.axaml` renders the diff with nested `ItemsControl`s inside a `ScrollViewer`, which is not a gitk-like virtualization model.
- `src/GitKay.UI/DiffProjection.cs` materializes every file, hunk, and line eagerly.

## In Progress
- Keep commit selection fast and predictable.
- Reduce backend work to the minimum needed for the currently selected item.

## Next
1. [x] Add timing instrumentation for commit click, diff load, UI projection, and first paint in `src/GitKay.Core/App.fs`, `src/GitKay.Core/GitService.fs`, and `src/GitKay.UI/MainProjection.cs`.
2. [x] Split selection state from diff state in `src/GitKay.Core/App.fs` so commit clicks update immediately even when diff data is not ready.
3. [x] Auto-select the first commit after history load so the app opens with a selected commit and visible diff state.
4. [x] Accept CLI startup targets for branch, sha, tag, and `--all`, and initialize the commit history view from the selected ref set.
5. [x] Introduce a gitk-style diff cache in `src/GitKay.Core/GitService.fs` keyed by commit hash, with immutable entries for summary, file list, and selected-file content.
6. [x] Add `FsFlow` orchestration for diff and cache jobs in `src/GitKay.Core/App.fs`, with cancellation and latest-wins semantics.
7. [x] Remove blame from the normal commit-open path entirely; keep blame behind an explicit user action and file scope only.
8. [x] Stop rebuilding the whole diff object graph on every update in `src/GitKay.UI/MainProjection.cs`; bind to cached model data instead.
9. [x] Replace the nested diff `ItemsControl` tree in `src/GitKay.UI/MainWindow.axaml` with a virtualized or custom-rendered line view that only realizes visible rows.
10. [x] Add file selection and focus so the right-side file list drives the left-side diff view instead of only showing a static list.
11. [x] Make the diff pane scroll to the selected file immediately when commit selection changes.
12. [x] Keep the commit list virtualization lightweight and ensure selection does not trigger unnecessary remeasure/rebind work.
13. [x] Add lazy loading for per-file diff content so one commit can open instantly while individual files hydrate on demand.
14. [x] Keep write-side git operations working, but move them off the CLI only if they become hot or user-visible enough to matter.
15. [x] Add search UI and search plumbing for commit hash, commit message/subject, author, file/path, text across commit contents and diffs, and ref/tag/branch lookup.
16. [x] Implement gitk-style keyboard control for the commit list and diff pane, including up/down, page up/down, home/end, and focus changes between panes.
17. [ ] Draw proper commit graph edges for branches and merges.
18. [ ] Improve graph rendering performance on larger histories.
19. [ ] Add a top-level error dialog for unrecoverable failures.
20. [ ] Finish UI/UX polish to get closer to gitk parity.

## Later
1. [ ] Port any remaining hot write operations to LibGit2Sharp if the CLI remains measurable.
2. [ ] Add tests for cache invalidation, selection ordering, and latest-wins async behavior.
3. [ ] Add tests for diff viewer focus and file selection behavior once the UI state is explicit.
