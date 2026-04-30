# GitKay Agent Notes

## Project Shape
- GitKay is an Avalonia v12 desktop app with a C# UI layer and an F# core.
- The solution uses `.slnx`, `mise`, and .NET 10.0.
- Core code lives in `src/GitKay.Core`; UI code lives in `src/GitKay.UI`; tests live in `tests/GitKay.Tests`.

## Current Architecture
- The app is Elmish-style at the core, bridged into Avalonia through `ElmishGlue`.
- Commit history, graph projection, and git command execution currently live in the F# core.
- UI projections are C# view models with `ObservableObject` / `RelayCommand`.
- The diff pane currently renders structured diffs and inline blame metadata, but blame loading still needs to be made lazy and backend-driven.

## Performance And Design Direction
- Prefer in-process Git access over repeated shelling out when a feature becomes interactive or high frequency.
- The current CLI-based Git path is acceptable as a stopgap, but the next backend step should be a libgit2-based implementation.
- Keep commit selection fast. Expensive work should be lazy, cancellable, and scoped to the current selection.
- File selection in the diff view should drive focus in the left pane rather than only rendering a list.

## Error Handling Conventions
- Prefer `Result` for expected failures and recoverable backend errors.
- Use exceptions only for truly unexpected failures or for a top-level crash path that can be surfaced in a dialog.
- Do not turn ordinary Git failures into exception-heavy control flow.

## Async And Flow Guidance
- Use `FsFlow` for async orchestration, cancellation, and request ordering.
- Keep Elmish messages as UI intents and let flows manage long-running work.
- A good pattern is:
  - UI message starts a flow.
  - Flow returns `Result`.
  - Elmish reducer applies the result only if it is still current.

## Code Style
- Keep changes small and explicit.
- Prefer typed data over raw strings once UI features need structure.
- Add tests for parsers and state transitions when behavior changes.
- Preserve existing work; do not revert user changes unless explicitly asked.

## Current Next Steps
- Replace the CLI Git backend with a libgit2-based backend.
- Make blame lazy and file-scoped instead of eager on commit selection.
- Add file focus/selection behavior in the diff pane.
- Implement search and navigation parity with `gitk`, especially:
  - commit hash search
  - commit message/subject search
  - author search
  - file/path search
  - text search within commits/diffs
  - ref/tag/branch lookup
- Finish graph edge rendering and selection/scroll polish.
- Improve the top-level error dialog path for unrecoverable failures.

