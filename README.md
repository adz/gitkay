# GitKay

GitKay is a fast Git history viewer built with Avalonia and F#.

It's design goal is to be as close as possible to gitk/git-gui in layout
and interactions with a more modern style.

In addition it adds:

- Syntax colouring
- Commit search: more capable, improved UX & moved to top
- More right-click contextual help (open in vscode, filter)
- Open file in full (double click, or right-click)
- Vim movement keys
- Ctrl-P for go to file
- Ctrl-G for go to commit/tag/branch

Commit window (like git gui):
- `gitkay gui` (or `gitkay-gui`, installed next to `gitkay`) opens only the commit window for the repository you're in:
  stage and unstage files, hunks or lines, amend, sign off, commit and push. Closing it exits.
- From GitKay's history, Ctrl+Shift+C or double-click the "Uncommitted changes" row. F1 shows its keys.

Publish:
- `bash scripts/publish-gitkay.sh` builds a self-contained single-file app.
- `bash scripts/publish-gitkay.sh` builds both `win-x64` and `linux-x64` outputs under `artifacts/publish/gitkay/`.
- `bash scripts/publish-gitkay.sh --aot --linux` tries NativeAOT on Linux; use the matching host OS for AOT, and the build stays to one executable with symbols embedded.
