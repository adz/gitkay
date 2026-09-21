# GitKay Agent Notes

## Project Shape
- GitKay is an Avalonia v12 desktop app with a C# UI layer and an F# core.
- The solution uses `.slnx`, `mise`, and .NET 11.0.
- Core code lives in `src/GitKay.Core`; UI code lives in `src/GitKay.UI`; tests live in `tests/GitKay.Tests`.

## Current Architecture
- The app is Elmish-style at the core, bridged into Avalonia through `ElmishGlue`.
- Commit history, graph projection, and git command execution currently live in the F# core.
- UI projections are C# view models with `ObservableObject` / `RelayCommand`.
- The diff pane renders structured diffs. History and diff loading use LibGit2Sharp on the read path. Blame is absent from diff loading: `GitService.fetchFileBlame` exists and is file-scoped, but nothing in the UI calls it yet.

## Performance And Design Direction
- Prefer in-process Git access over repeated shelling out when a feature becomes interactive or high frequency.
- Interactive reads (a commit, its files, a diff, a whole file) stay on LibGit2Sharp.
- History-wide scans that git accelerates run the `git` executable through Axial.Process, with a LibGit2Sharp fallback when it can't run: path-limited history uses `git log -- <paths>`, which reads commit-graph changed-path Bloom filters. Network operations (push, pull, fetch) also use the CLI.
- Commit search reads contents through per-worker LibGit2Sharp handles in parallel, streams matches as they are found, and never caches diffs. libgit2's blob cache is enabled at startup (`NativeGitOptions`), and freed native memory is trimmed on Linux (`NativeMemory`).
- Keep commit selection fast. Expensive work should be lazy, cancellable, and scoped to the current selection.

## Visual Stability (non-negotiable)
- **Nothing the user is looking at may jump.** Disorienting jolts are a major antipattern: treat them as bugs, not polish.
- Opening a prompt, bar, banner or panel must not resize or shift the content under it. Overlay transient UI (search
  prompts, palettes, status messages) on top of content, or reserve its space up front.
- Never lose the scroll position or the focused row: collapsing, expanding, refreshing, rescanning, reloading or
  re-rendering keeps what was on screen in the same place (anchor to the row the user sees, as sticky file headers do).
- Content that loads late (images, diffs, counts) must not push what's already visible; anchor or reserve space.
- Check it: take a before/after headless screenshot of any change that shows, hides or reloads UI, and compare positions.

## Error Handling Conventions
- Prefer `Result` for expected failures and recoverable backend errors.
- Use exceptions only for truly unexpected failures or for a top-level crash path that can be surfaced in a dialog.
- Do not turn ordinary Git failures into exception-heavy control flow.

## Async And Flow Guidance
- Use `Axial` for async orchestration, cancellation, and request ordering.
- Keep Elmish messages as UI intents and let flows manage long-running work.
- A good pattern is:
  - UI message starts a flow.
  - Flow returns `Result`.
  - Elmish reducer applies the result only if it is still current.

## Where Code Lives
- **Anything that could be tested without a window belongs in F#.** The split is by purity, not by layer: C# is for
  drawing, input and binding; every decision those make is a pure function that belongs in `GitKay.Core`.
- Arithmetic over sizes, scales, thresholds, colours, byte counts and text is core logic even when it exists only to
  serve one control. `Presentation.fs` is where that lives when it has no better home.
- The tell is a test: if checking a rule means showing a window and reading a control's `DesiredSize`, the rule is in
  the wrong language. Those tests are slow, indirect, and fail for reasons unrelated to what they assert.
- A C# `Render` or `MeasureOverride` should read as a sequence of draw and arrange calls over answers computed in the
  core, not as a place where the answers are worked out.

## Code Style
- Keep changes small and explicit.
- Prefer typed data over raw strings once UI features need structure.
- Add tests for parsers and state transitions when behavior changes.
- Preserve existing work; do not revert user changes unless explicitly asked.

## Task Format For `scripts/ralph-loop-tasks.sh`
The loop reads `TASKS.md`, which is absent while there is no queued work. Recreate it when queueing some.
- Keep loopable work items in `TASKS.md` as a numbered checklist under `## Next`.
- Use the exact syntax `1. [ ] Task text` for open items.
- Use the exact syntax `1. [x] Task text` for completed items.
- Keep the number, bracket state, and period in that order so the script can find the next unchecked task.
- Do not use bullet lists, nested checkboxes, or alternate checkbox syntax for looped tasks.
- Preserve the task number when marking an item complete; the script expects the same number to switch from `[ ]` to `[x]`.
