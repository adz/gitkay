# Commit window

Status: implemented (phases 1–5, 6 partly). Target: 0.7.0.

## Why a separate window

GitKay's main window is a read-only history browser, like gitk. Staging and committing live in their own window, like
git gui next to gitk, so the history view stays simple and nothing in it can change the repository by accident.

## What users see

Opened from the uncommitted changes row (double-click, Enter, or its menu), from the File menu, or with `Ctrl+Shift+C`.
One window per repository; asking again brings it forward.

```
┌ Commit — gitkay (main) ──────────────────────────────────────────────────────────┐
│ Unstaged (3)                 [Stage all] │ src/App.fs · unstaged                  │
│  ○ src/App.fs                            │ @@ -10,6 +10,8 @@          [Stage hunk]│
│  ○ README.md                             │  context                               │
│  + notes.txt                             │ +added line                            │
│──────────────────────────────────────────│ -removed line                          │
│ Staged (1)                 [Unstage all] │                                        │
│  ● src/Graph.fs                          │                                        │
│──────────────────────────────────────────┴────────────────────────────────────────│
│ Commit message                                          ☐ Amend last commit       │
│ ┌──────────────────────────────────────────────────────────────────────────────┐ │
│ │ Subject line                                                          52/72 │ │
│ │                                                                              │ │
│ └──────────────────────────────────────────────────────────────────────────────┘ │
│ [Rescan] [Sign off]                        [Commit  Ctrl+Enter] [Commit and push] │
└───────────────────────────────────────────────────────────────────────────────────┘
```

- **Two file lists**, Unstaged (with untracked files) above and Staged below, each with the ●/○/+ markers. Selecting a
  file shows its diff for that list. Double-click, Enter or `s`/`u` moves a file to the other list.
- **Diff pane** uses the main window's diff surface: hunk headers get **Stage hunk** / **Unstage hunk**, and selecting
  lines (mouse or `v`) offers **Stage lines** / **Unstage lines**. Unstaged files and hunks can also be **discarded**
  after a confirmation that names what is lost.
- **Message editor:** subject length hint (50 soft, 72 hard), a blank line enforced before the body when committing,
  **Amend** loads the last commit's message and commits with `--amend`, **Sign off** appends a
  `Signed-off-by:` trailer from the configured identity.
- **Commit** (`Ctrl+Enter`) is enabled when something is staged (or amending) and the subject isn't empty. **Commit and
  push** pushes the current branch afterwards through the existing push operation window.
- Hooks run (pre-commit, commit-msg); their output shows in a panel if the commit fails. The message is kept on failure.
- The draft message survives closing the window (per repository, in UI state) until a commit succeeds.

## Design

### Git operations (F#, `GitService`, git CLI in the working tree)

| Action | Command |
| --- | --- |
| Stage files | `git add -- <paths>` (deleted files included) |
| Unstage files | `git restore --staged -- <paths>` (`git rm --cached` before the first commit) |
| Stage hunk or lines | `git apply --cached --unidiff-zero --whitespace=nowarn -` with a built patch on stdin |
| Unstage hunk or lines | the staged diff's patch, `git apply --cached --reverse …` |
| Discard hunk or lines | the unstaged diff's patch, `git apply --reverse …` (no `--cached`) |
| Discard file | `git restore -- <path>`; untracked: delete the file |
| Commit | `git commit -F -` (`--amend`, `--signoff`) with the message on stdin |
| Last message | `git log -1 --format=%B` |

### Patch building (F#, pure: `GitKay.Core.PatchBuilder`)

git gui's approach: from one file's diff and a set of selected changed lines, build a patch containing only those
changes.

- **Forward (stage from the unstaged diff, discard from it):** a selected `+` stays `+`; an unselected `+` is dropped;
  a selected `-` stays `-`; an unselected `-` becomes context. Hunk headers are recounted.
- **Reverse (unstage from the staged diff):** applied with `--reverse`, so the roles swap: an unselected `+` becomes
  context and an unselected `-` is dropped.
- Hunks with no selected changes are left out; a whole hunk is "all its changed lines selected".
- New and deleted files, renames and `\ No newline at end of file` markers carry through. Partial selections of a new
  untracked file stage it with `git add --intent-to-add` first, so the patch has a file to apply to.
- Binary files stage and unstage only as whole files.

### Model (F#, Elmish: `GitKay.Core.CommitWindow`)

- State: the working tree changes (reusing `WorkingTreeChanges`), the selected list and file, line selection, message,
  amend and sign-off flags, the running operation, and the last error output.
- Messages: `Rescan`, `ChangesLoaded`, `SelectFile`, `StageFiles`, `UnstageFiles`, `ApplySelection of kind`,
  `Discard…`, `SetMessage`, `SetAmend`, `SignOff`, `Commit`, `Committed`, `OperationFailed`.
- Every operation ends with a rescan; the selection stays on the same path where it still exists, otherwise the next
  file in that list.
- The main window refreshes through its watcher; a commit also asks it to reread refs so the new commit appears.

### UI (C#)

- `CommitWindow` (Avalonia window) with two list boxes, `DiffSurfaceControl`, and the message box; its own
  `ElmishHost`.
- Layout: a "Current Branch" bar across the top; Unstaged Changes (salmon title) and Staged Changes (Will Commit)
  (green title) down the whole left side; the diff above the commit message on the right, as in git gui.
- Keys (F1 or `?` shows them all): the main window's movement keys through the shared vim session (`j`/`k`, `gg`/`G`,
  `Ctrl+D`/`Ctrl+U`, `]c`/`[c`, `v`/`V`, `y`, `zz`, `H`/`M`/`L`, zoom); its pane keys (`Ctrl+1`–`4`,
  `Ctrl+h`/`j`/`k`/`l`, `Ctrl+W` commands, `Tab`); git gui's staging keys (`Ctrl+T` stage, `Ctrl+U` unstage in a list,
  `Ctrl+I` stage all, `Ctrl+Enter` commit, `Ctrl+S` sign off, `F5` rescan) plus `s`/`u`, `Enter` and `Delete` (discard).
  git gui's `Ctrl+J` (revert) stays pane-down, as in the main window. Search, palettes and history navigation stay in
  the main window.
- Right-click: on a file, stage/unstage, discard, stage or unstage all, copy path, open in VS Code; on diff lines,
  stage/unstage the lines or hunk, discard, open in VS Code at the line.

## Phases

1. `PatchBuilder` with tests (line and hunk selections, new/deleted files, no-newline markers).
2. Git operations with temporary-repository tests, including applying built patches.
3. `CommitWindow` Elmish model with tests.
4. The window: lists, diff with stage/unstage hunk and lines, message, commit.
5. Opening from the main window; discard; amend; sign off; commit and push; draft persistence.
6. AOT self-test coverage, screenshots, docs.

## Not yet built

- Keeping the draft message across closing the window (it lives only while the window is open).
- Context expansion in the commit window's diff (hunks show without "more lines" gaps).
- Partial staging of untracked files: they stage whole (`--intent-to-add` first would allow lines).
- User-facing docs and the shortcut sheet entries.
