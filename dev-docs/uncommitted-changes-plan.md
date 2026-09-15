# Uncommitted changes

Status: implemented (phases 1–6). Target: 0.6.0.

## What users see

- An **Uncommitted changes** row above the newest commit when the working tree or index differs from HEAD: a hollow,
  dashed graph node into HEAD's lane, and counts ("2 staged · 2 unstaged · 1 untracked") where a commit shows its
  subject. It has no hash, author or date.
- Selecting it shows its changes **grouped into Staged, Unstaged and Untracked** sections, in the files list (flat and
  tree modes) and in the diff pane, where section headers collapse and stick like file headers. A file changed in
  both the index and the working tree appears in both sections, each with its own diff. The all-files tree stays one
  tree with a marker per file (● staged, ○ unstaged, ◐ both, + untracked).
- It stays current: a file watcher on the working tree and index (debounced), a refresh when the window regains focus,
  and "Reread refs".
- The main window stays read-only. Staging, committing and discarding belong to a separate git-gui-style commit
  window (later), opened from this row.

## Design

### Reading changes: the git command line

- Status: `git status --porcelain=v2 -z --untracked-files=all` gives each path's index and working-tree state,
  renames, and untracked files in one fast call (git keeps the index's stat cache, which libgit2's status lacks on
  large trees).
- Staged diff: `git diff --cached --no-color --no-ext-diff --find-renames -U<context>`; unstaged:
  `git diff --no-color --no-ext-diff -U<context>`. Both parse with the existing `Parsing.parseDiff`, and respect the
  repository's attributes, filters and line-ending settings, as a user's own `git diff` would.
- Untracked files become all-added diffs from their contents (text, under the search size limit; binary files show no
  lines).
- LibGit2Sharp's `Compare<Patch>` crashes under NativeAOT and writing working-tree blobs into the object database has
  side effects, so the command line is the reader. When git isn't available the row doesn't appear, and the
  diagnostics window says why.

### Model

- `WorkingTree` (F#): pure parsing of porcelain v2 status into entries (path, original path, index state,
  working-tree state, untracked), sectioning into `Staged | Unstaged | Untracked` changes, and the row's counts.
- The Elmish model replaces `SelectedCommitHash: string option` with a `Selection` union
  (`NoSelection | CommitSelected of hash | WorkingTreeSelected`), so nothing that needs a real commit (parents, copy
  hash, branch actions, search) can act on the working tree by accident.
- The diff pane's file keys carry the section (`DiffFileKey.Section`, empty for a commit), so the same path in two
  sections is two files. The core model's commit diff keys are unchanged: uncommitted diffs arrive whole in
  `WorkingTreeChanges`, and their context expansion is presentation state in the projection.
- Loading: status loads with history, on refresh messages, and after watcher events (debounced 300 ms). The working
  tree diff loads when its row is selected and reloads when status changes while selected.

### Presentation

- Commit list: the row is shown when the loaded history starts at the checked-out branch (or while it's selected); the
  row is a projection with `IsWorkingTree`; the commit surface draws it with the dashed node and
  count badges. `j`/`k`, `gg`, `/` quick find and navigation treat it as a row; commit-only actions are absent from its
  menu.
- Diff pane: a section header row before each section's files; clicking it collapses the section. File headers stick
  within a section; section headers themselves don't stick yet.
- Files list: section header rows in flat and tree modes; markers in all-files mode.

## Phases

1. `WorkingTree` parsing, sectioning and loading in F#, with tests against temporary repositories.
2. `Selection` union and working-tree messages in the Elmish model.
3. The commit list row.
4. Diff pane and files list sections.
5. Watcher and focus refresh.
6. Whole-file popup, VS Code, context menus and context expansion for working-tree files.

## Notes from building it

- `RepoPath` is the git directory; `git status` and `git diff` fail there, so working tree commands run in the
  working directory. The tests and the AOT self-test load changes through the git directory to guard this.
- Status runs with `--no-optional-locks` so it never rewrites the index, which the watcher would see as a change.
- When everything is committed or discarded while the row is selected, the newest commit is selected.

## Later

- Sticky section headers.

- The commit window (git gui style): stage and unstage files, hunks and lines, amend, sign off, commit, push.
- Commit search over uncommitted changes.
