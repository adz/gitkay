# GitKay Tasks

## Done
- Repo foundation, `.slnx`, `mise`, .NET 10.0, Avalonia v12, F# core, C# UI, tests, and centralized artifacts.
- History loading, commit selection, diff loading, search plumbing, graph rendering, and context-menu git operations are in place.

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
7. [x] Tighten graph marker alignment with the commit text baseline.
8. [x] Keep the top history and bottom diff split with a single splitter; the commit-info row should fit content.
9. [x] Make commit search highlight matching commit rows in place, show which fields matched, and stop filtering nonmatching commits out of the history list.
10. [x] Make diff search debounce-apply, highlight matched files, lines, and terms, and keep the diff pane visibly annotated while searching.
11. [x] Increase the default height of the bottom diff section so it has enough room without manual resizing.
12. [x] Increase the default commit list font size slightly so commit rows read more clearly.
13. [ ] Add resizable columns for the history, commit, and diff layouts so the main panes can be tuned without code changes.
14. [ ] Hide search panes by default, but keep them easy to open inline from the history and diff areas.
15. [ ] Add diff presentation modes for `diff`, `side-by-side`, `new`, and `old`, with a user-selectable default.
16. [ ] Add configurable diff context line counts and wire the setting through the UI and backend.
17. [ ] Add syntax highlighting for diffs and any other rendered code/text views that can benefit from it.
18. [ ] Add vim-style navigation bindings such as `j`/`k` for down/up, with the same behavior across the main lists.
19. [ ] Add command-line arguments for the major app options so the startup state can be configured non-interactively.
20. [ ] Add an `Edit > Settings` panel for each major pane, persist settings to the right app-specific location, and load them on startup.
21. [ ] Remember the last open position per repo and restore the window size on startup.
22. [ ] Stop logging to the console and write logs to a local file only when a command-line option enables it.
23. [ ] Reduce startup time so the app becomes interactive sooner.
