# C# to F# migration audit

## Purpose

GitKay's language boundary is based on what code does: F# owns decisions and workflows that can be tested without a
window; C# owns Avalonia drawing, input, and binding. This audit records where the UI project still mixes those roles
and a practical order for moving the non-UI work.

The audit covered the 55 C# files under `src/GitKay.UI` and the two C# files under
`benchmarks/GitKay.Rendering.Benchmarks`.

## Completed

`AppSettingsStore` and `AppUiStateStore` moved from C# into
[`src/GitKay.Serialization/Persistence.fs`](../src/GitKay.Serialization/Persistence.fs). The UI calls the F#
`SettingsStore` and `UiStateStore` modules. The stores preserve default paths, normalization through the Reified JSON
codecs, fallback behavior, and trace logging.

## Target boundary

- Keep Avalonia windows, controls, converters, custom layout, font and brush creation, keyboard event adapters, and
  observable view-model shells in C#.
- Put application decisions, tokenization, formatting, and row planning in F# modules with typed inputs and outputs.
- Put process and filesystem workflows behind F# Axial flows and declared capabilities. C# should pass UI callbacks or
  dispatch results to the UI thread; it should not own the operation's policy.
- Keep an interop shim in C# only when a narrow runtime or ABI boundary is materially simpler or safer there. The
  libgit2 variadic call in `NativeGitOptions` is the current candidate for that exception.

## Recommended migration order

1. **Git operations and branch inspection.** Split `GitOperations` and `GitRunner` from
   [`src/GitKay.UI/GitOperationWindow.cs`](../src/GitKay.UI/GitOperationWindow.cs). Represent operations and their
   outcomes in F#, use `GitKay.Core.Push` and `GitKay.Core.Pull` for the existing push/pull decisions, and run network
   operations as cancellable Axial flows through the declared process capability. Keep the window's progress display
   and UI-thread callback in C#. Move `DeleteBranchDialog.Inspect` from
   [`src/GitKay.UI/DeleteBranchDialog.cs`](../src/GitKay.UI/DeleteBranchDialog.cs) into `GitService`, returning a
   typed branch inspection value for the dialog.

2. **Syntax tokenization.** Move file-flavour selection, token kinds, code and Markdown tokenization, and cache
   policy from [`src/GitKay.UI/SyntaxHighlighting.cs`](../src/GitKay.UI/SyntaxHighlighting.cs) into an F# presentation
   module. Keep Avalonia brushes and `Run` creation in C#. This gives the lexer a direct F# test surface without
   changing rendering.

3. **Standalone host services.** Move policy and lifecycle code from
   [`ExternalTools.cs`](../src/GitKay.UI/ExternalTools.cs),
   [`WorkingTreeWatcher.cs`](../src/GitKay.UI/WorkingTreeWatcher.cs), and
   [`NativeMemory.cs`](../src/GitKay.UI/NativeMemory.cs) into F#. Model process launch, watcher ownership, debounce,
   path filtering, and memory trimming as host services with explicit effects and lifetimes. Keep any Avalonia thread
   dispatch at the UI edge. Move libgit2 library discovery, status, and configuration policy from
   [`NativeGitOptions.cs`](../src/GitKay.UI/NativeGitOptions.cs) to F#, evaluating whether only the unmanaged
   variadic invocation needs a C# shim.

4. **Diagnostics service and projection.** Move file logging, retention, report writing, and diagnostic formatting
   from [`DiagnosticsLog.cs`](../src/GitKay.UI/DiagnosticsLog.cs) behind F# services. Keep its Avalonia dispatcher
   heartbeat as a small C# adapter. Keep `DiagnosticsProjection` as an observable view model, while moving row
   construction and formatting to F# functions that accept diagnostic snapshots and timestamps as values.

5. **Pure view-model projection work.** Keep `MainProjection`, `SearchProjection`, `CommitProjection`,
   `DiffProjection`, and the window projections as C# binding adapters. Move preference parsing, display formatting,
   tree construction, row ordering, and selection calculations into F# presentation modules. In particular, inspect
   [`MainProjection.Navigation.cs`](../src/GitKay.UI/MainProjection.Navigation.cs),
   [`DiffProjection.cs`](../src/GitKay.UI/DiffProjection.cs), and
   [`CommitFileList.cs`](../src/GitKay.UI/CommitFileList.cs). Return typed immutable row descriptions; let C# apply
   `ObservableCollection`, `Thickness`, brushes, and control state. Reuse existing `Presentation`, `GitSearch`,
   `CommitFormat`, `Markdown`, `Kit.PathTree`, `Folder`, and `FolderSource` APIs instead of duplicating their rules.

6. **Folder-selection rules.** Keep the storage picker and dialog controls in
   [`OpenFoldersDialog.cs`](../src/GitKay.UI/OpenFoldersDialog.cs). Move existing-path lookup and left/right folder
   validation into F#, building on `FolderSource` and `Folder`.

7. **Startup and self-test shells.** Keep `Program.Main` and `BuildAvaloniaApp` in C# as the Avalonia entry point.
   Keep CLI parsing and startup decisions in the existing F# `GitStartup` modules; move remaining non-UI setup into
   F# host services where it fits, leaving console attachment and Avalonia startup at the edge. Use the existing
   `GitKay.Core.SelfTest` for core checks; retain C# smoke checks only where they exercise Avalonia-specific behavior.

## Keep in C#

Windows, controls, converters, layout and rendering code, font and brush adapters, keyboard input adapters, and
observable binding classes have a direct Avalonia role. The two benchmark files use Avalonia headless rendering and
also remain C#. `AssemblyInfo.cs` is assembly metadata rather than application logic.

## Sequencing notes

- Migrate one vertical slice at a time: F# API and tests first, then replace the C# policy call, then remove the old
  C# implementation.
- Keep UI behavior stable while extracting projections. C# should consume immutable F# results and remain responsible
  for applying observable changes and preserving control state.
- Make effect ownership explicit through Axial rather than moving direct `Process`, clock, or filesystem calls into
  an F# module unchanged.
- This document is an audit and proposal. It does not queue these items as active work or declare each migration
  complete.
