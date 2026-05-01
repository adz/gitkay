# Design Decisions

## 2026-04-30 - Split selection from diff loading (`3385e0b`)
The UI now tracks selected commit, selected diff, and selected diff-file state separately. Commit clicks can update immediately while diff and file hydration continue in the background, which keeps focus and scroll position stable even when content is still loading.

## 2026-04-30 - Cache diff data by commit hash (`a8539ea`, `26dbecf`, `90ddbba`)
Diff summary, file list, and per-file content are cached by commit hash and hydrated lazily. The reducer starts cancellation-aware flow jobs with latest-wins semantics so stale work never overwrites the current selection.

## 2026-04-30 - Keep blame off the hot open path (`7cac3a3`)
Blame is not part of normal commit selection. It stays behind explicit, file-scoped access so selecting a commit remains cheap and predictable.

## 2026-04-30 - Treat diff projection as a cached view model (`604e5f7`, `a6e34a8`)
The UI avoids rebuilding the full diff object graph on each update and renders diff rows through a virtualization-friendly model. This keeps diff navigation responsive as histories and commits grow.

## 2026-04-30 - Keep commit list refreshes lightweight (`b4468ef`)
Commit rows are updated from stable source identity instead of being rebuilt on every projection pass. The history pane should stay cheap to rebind so selection and search can run without causing remeasure churn.

## 2026-05-01 - Make graph rendering explicit and cheap (`11d144e`, `6b64f52`)
Commit graph edges are derived from lane segments and rendered in a custom control. The control caches its segment arrays and draws only the geometry it needs so lane continuity stays readable on larger histories.

## 2026-05-01 - Move write operations to LibGit2Sharp (`2582b49`)
Write-side operations are preferred in-process when they become user-visible enough to matter. This keeps the hot path consistent with the read-side decision to avoid repeated CLI shelling out.
