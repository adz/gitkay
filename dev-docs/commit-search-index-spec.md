# Commit search index

Status: proposed. Not implemented.

## Problem

Path and Diff search re-read commit contents on every search. After 0.3.0 (metadata first, parallel workers, blob
cache, streaming), searching a 10,579-commit repository measures:

| Search | Time | Dominant cost |
| --- | --- | --- |
| Path, full history | 8.6 s | Tree comparisons for every candidate commit |
| Diff, common term (`zstream`) | about 12 min | Reading and diffing every changed blob |
| Diff, rare term | longer than common terms | No early exit: every changed blob of every commit is read |

About 1,600 of ~10,000 commits in that repository change more than 200 files. Reading a 773-file commit's blobs
costs roughly 3 s (lookup and decompression) before any diffing. `git log -G` has the same cost profile and is not
faster. Only a persistent index removes the repeated work.

## Goals

- Path search answers from the index in milliseconds for indexed commits.
- Diff search rules out most commits without reading git objects, and confirms the rest exactly.
- Results are always complete and correct. Unindexed commits fall back to the existing scan.
- The index needs no user action: it builds in the background, grows as history changes, and cleans up after itself.
- Rewritten history (rebase, amend, force-push) never produces stale results.

## Non-goals

- Indexing file contents at a revision (code search). Only what each commit changed is indexed.
- Replacing the scan. The scan remains the fallback and the source of truth for confirmation.
- Sharing indexes between machines or users.

## Prior art

- **git commit-graph changed-path Bloom filters.** Per-commit Bloom filters of changed paths. GitKay 0.3.0 already uses
  them through `git log -- <paths>`. They cover paths, not contents.
- **JetBrains IDE VCS log index.** A persistent, incremental, background index of commit messages, authors and changed
  paths for fast log filtering. As far as we know it does not index diff contents.
- **Trigram code search (Zoekt, Google Code Search).** Trigram indexes over file contents at a revision, not over
  changes between revisions.
- **Desktop Git clients** (gitk, Sublime Merge, Fork, GitKraken, Tower, Magit) generally search added/removed text with
  git's pickaxe (`-S`/`-G`), a full scan. We know of none that index changed lines; this has not been verified against
  current versions.

## Why entries never expire

A commit SHA is a hash of the commit's tree, parents and metadata. The same SHA always names the same change against
the same parent, so an entry keyed by SHA cannot become stale. Rewriting history creates new SHAs: they are unindexed
until the indexer reaches them, and the entries for the old commits become unreachable and are pruned.

An entry is valid only when all of these match the current request:

| Key part | Why |
| --- | --- |
| Commit SHA | Identifies the change. |
| First-parent SHA used for the diff | Grafts, `refs/replace`, and deepening a shallow clone can change a commit's effective parent. Root commits record no parent. |
| Index format version | Encoding changes. |
| Content settings | Rename mode, blob size limit, binary detection and text normalisation change which lines are indexed. |

A mismatch makes the entry absent, not wrong. Absent entries fall back to the scan and are rebuilt lazily.

## What is indexed

### Tier 1: changed paths

Per commit, the (old path, new path) pairs against the first parent, without rename detection. Small, and makes Path
search a lookup.

### Tier 2: changed-line filter

Per commit, a Bloom filter of the trigrams in its added and removed lines:

- Take each added or removed line from zero-context diffs of text blobs within the size limit.
- Lowercase with invariant culture, since search is case-insensitive.
- Insert every 3-character sequence. Lines shorter than 3 characters contribute their whole text as one token.
- Size the filter to the commit's distinct trigram count at about 10 bits per trigram (about 1% false positives), so
  small commits stay small and bulk commits stay accurate.

### Tier 3 (optional): per-file filters

The same filter per changed file. A candidate commit then diffs only the files whose filters pass, which matters for
common terms that match thousands of commits.

## Query

1. Split each Diff term into lowercase trigrams.
2. For each indexed candidate (after metadata terms), test every trigram against the commit's filter. Any miss rules
   the commit out.
3. Confirm the remaining candidates with the existing exact matcher, reading only files whose tier 3 filter passes
   when available.
4. Scan unindexed candidates as today.
5. Stream results in history order as now.

Terms shorter than 3 characters test themselves as a single token when possible; otherwise they skip the filter and
use the scan.

For regular expressions, derive the trigrams every match must contain, as Google Code Search does: literals must
appear, alternations become OR, and repetition ranges drop requirements. When no required trigrams can be derived
(for example `a.*b`), skip the filter and use the scan.

## Building and maintenance

- A low-priority Axial fiber indexes commits newest-first, in parallel chunks, reusing the search reader.
- It starts after history loads and after "Reread refs", indexes only commits missing from the index, and backs off
  while the user is interacting or a foreground search is running.
- It is cancellable. Progress and coverage appear in the diagnostics window, for example
  "index: 8,912 of 10,579 commits".
- Compaction runs in the background when more than 20% of entries are unreachable from all refs and reflogs, or when
  the index exceeds its size cap (evicting the least recently used entries).

## Storage

- Location: `$GIT_COMMON_DIR/gitkay/search-index/`. Worktrees share it, it is never in the working tree, and it is
  deleted with the repository. A setting moves it to the user cache directory, keyed by a hash of the common
  directory path.
- Append-only immutable segment files. Each has a header (format version and content settings), a sorted SHA table
  with offsets, and per-commit records (parent SHA, path pairs, filters).
- A small manifest lists live segments. Writers write a temporary file and rename it into place, so a crash never
  corrupts the index. A file lock serialises writers across GitKay windows; readers memory-map segments.
- Settings in a segment header that don't match current settings make the whole segment absent until rebuilt.
- Estimated size for the 10,579-commit repository is 20–60 MB. Not measured.

## Correctness and testing

- An equivalence test: for a repository fixture and a set of queries, indexed search must return exactly the scan's
  results, including after rewriting history in the fixture.
- Staleness tests: amend, rebase, grafts or `refs/replace`, shallow deepening, and changed content settings each
  produce fresh entries or fall back, never old results.
- Crash-safety test: interrupt a write and confirm the index opens with the previous manifest.
- A diagnostics action verifies a sample of indexed commits against the scan and reports mismatches.

## Costs and risks

- The first build is a full scan, estimated at 10–20 minutes in the background for the measured repository, then
  milliseconds per new commit.
- Common terms still produce many candidates, each needing confirmation; tier 3 reduces the work.
- Disk use grows with history; the size cap and compaction bound it.
- Writing inside `.git` may surprise some users, which is why the location is a setting.

## Delivery sequence

1. Segment format, manifest, locking and the equivalence-test harness.
2. Tier 1 path index with the background indexer and diagnostics coverage.
3. Tier 2 line filters, query integration and staleness tests.
4. Measure on the large repository. Build tier 3 only if confirming common terms is still slow.
5. Compaction and the size cap.
