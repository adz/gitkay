# GitKay Tasks

## Done
- Repo foundation, `.slnx`, `mise`, .NET 10.0, Avalonia v12, F# core, C# UI, tests, and centralized artifacts.
- History loading, commit selection, diff loading, search plumbing, graph rendering, and context-menu git operations are in place.
- `dev-docs/design-decisions.md` captures the architectural changes that should guide future work.

## Target
- Keep selection, search, and diff navigation fast.
- Keep the UI close to gitk without losing responsiveness.
- Prefer in-process Git access and lazy work on the hot path.

## Current Bottlenecks
- Search still requires an explicit action instead of filtering as you type.
- The search surface still reads as a separate area instead of an overlay on the commit list.
- The history pane still needs user-resizable sections and tighter gitk-like ref/graph treatment.
- Stash visibility needs explicit show/hide controls.

## In Progress
- Keep commit selection fast and predictable.
- Reduce backend work to the minimum needed for the selected item.

## Next
1. [x] Make search results update as you type with a 1s debounce.
2. [x] Present search as a filtered overlay on the normal commit list, with subtle match highlighting instead of a separate results layout.
3. [x] Add resize handles for the main sections and commit/diff columns.
4. [x] Rework branch, tag, remote-ref, and stash visuals to match gitk more closely.
5. [x] Restore graph continuity through forks and merges so branch lines stay readable.
6. [x] Hide stashes by default and add an explicit option to show them.
7. [ ] Tighten graph marker alignment with the commit text baseline.
