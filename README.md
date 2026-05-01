# GitKay

GitKay is a fast Git history viewer built with Avalonia and F#.

Current focus:
- commit history and graph browsing
- commit details and diffs
- branch, tag, cherry-pick, reset, and revert actions

Publish:
- `bash scripts/publish-gitkay.sh` builds a self-contained single-file app.
- `bash scripts/publish-gitkay.sh` builds both `win-x64` and `linux-x64` outputs under `artifacts/publish/gitkay/`.
- `bash scripts/publish-gitkay.sh --aot --linux` tries NativeAOT on Linux; use the matching host OS for AOT, and the build stays to one executable with symbols embedded.
