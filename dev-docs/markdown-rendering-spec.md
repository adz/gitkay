# Rendered markdown

Status: implemented.

## Problem

Markdown files are prose. Reading a README change as a line diff shows markup and rewrapped paragraphs instead of
what the reader will see, and a one-word edit in a rewrapped paragraph looks like the whole paragraph changed.

## Goals

1. **Rendered file (whole-file popup).** A markdown file at a revision reads as formatted text: headings, emphasis,
   lists, tables, code blocks, quotes and images.
2. **Rendered diff (diff pane).** A markdown file's change reads as the rendered new document with what changed marked:
   added, removed and modified blocks, and inserted and deleted words inside modified paragraphs.
3. **Opt-in.** Source stays the default. A per-file toggle switches to rendered; a setting can make rendered the default.
4. **Images load.** Images stored in the repository show at the file's revision. Remote images need an explicit
   choice (see Images).
5. Everything already true of the diff keeps working: scrolling, sticky file headers, collapsing, the overview ruler,
   zoom, vim keys, `/` search and copying.

## Non-goals

- Editing, or a live preview of the working tree.
- Mermaid, math, and other embedded diagram languages (they render as code blocks).
- Running raw HTML. HTML blocks and inline HTML show as code.
- Rendering other markup languages (AsciiDoc, reStructuredText). The design leaves room for them.

## Prior art

- **GitHub rich diff** for markdown and prose: rendered document, changed blocks tinted, word-level changes inside them.
- **VS Code markdown preview:** `Ctrl+Shift+V` opens a preview; the preview follows the source.
- **GitLab and Gitea** render markdown files but diff them as source only.

## Parsing: Markdig

Markdig 1.3.2 parses CommonMark plus the extensions GitHub users expect (tables, task lists, strikethrough,
autolinks, footnotes, front matter). A NativeAOT probe built with no trim or AOT warnings and parsed every block and
inline kind above, including source line numbers. No reflection-based setup is needed.

`GitKay.Core` gains a Markdig dependency and converts Markdig's syntax tree into an F# document model at once, so the
rest of the feature depends on GitKay's own types.

### Document model (F#, `GitKay.Core.Markdown`)

```fsharp
type Inline =
    | Text of string
    | Emphasis of Inline list          // *em*
    | Strong of Inline list            // **strong**
    | Strikethrough of Inline list
    | Code of string
    | Link of target: string * Inline list
    | Image of source: string * alt: string * title: string
    | LineBreak
    | Html of string                   // shown as code

type Block =
    | Heading of level: int * Inline list
    | Paragraph of Inline list
    | ListItem of depth: int * marker: ListMarker * checked: bool option * Block list
    | Quote of Block list
    | CodeBlock of language: string * text: string
    | Table of header: Inline list list * alignments: Alignment list * rows: Inline list list list
    | Rule
    | HtmlBlock of string
    | Footnote of label: string * Block list

/// A block with where it came from, for mapping to the source and diff.
type Located = { Block: Block; FirstLine: int; LastLine: int }
```

Front matter is dropped from the rendered view (it shows in source). Link reference definitions are resolved into
their links.

## Rendered diff: block alignment

A rendered diff compares the old and new documents' blocks, not their lines, so a rewrapped paragraph with one changed
word is one modified block with one changed word.

### Leaf blocks

Both documents flatten into a sequence of leaf blocks, the smallest units shown as changed:

| Source | Leaf |
| --- | --- |
| Heading, paragraph, rule, code block, HTML block | itself |
| List | each item's own content, with its depth and marker |
| Quote | each block inside, marked as quoted |
| Table | the header row, and each body row |
| Footnote | each block inside, with its label |

Each leaf has a **fingerprint**: its kind, depth, and its text with whitespace collapsed, so rewrapping doesn't
count as a change.

### Alignment

1. **Match:** a Myers diff over the two fingerprint sequences gives unchanged leaves and runs of removed and added
   leaves. It's the same algorithm git uses, applied to blocks, and runs in O(n·d).
2. **Pair:** inside each run, a removed leaf and an added leaf of the same kind become one **modified** leaf when
   their words are similar enough: at least half of the longer leaf's words shared, in order. Pairing is greedy in
   document order, which handles the usual edit (the same paragraph, reworded) and leaves real insertions and deletions alone.
3. **Words:** a modified leaf gets a word-level diff (Myers over word tokens, with punctuation as separate tokens)
   into inserted, deleted and kept spans, mapped back onto the new leaf's inline formatting.

```fsharp
type LeafChange =
    | Unchanged of Located
    | Added of Located
    | Removed of Located
    | Modified of old: Located * current: Located * words: WordSpan list
```

Moved blocks show as removed and added; detecting moves is future work.

Code blocks are leaves too. A modified code block shows its lines with GitKay's normal intraline highlight rather
than word spans, since code is line-oriented.

## Presentation

### One surface, new row kinds

The diff pane and the whole-file popup keep `DiffSurfaceControl`. A rendered file contributes **rendered block
rows** instead of line rows, one row per leaf change, each laid out at the current width and zoom:

```fsharp
// DiffRows gains:
| RenderedBlockRow of change: LeafChange
```

This keeps the existing machinery for free: the file header and its stickiness, collapsing, scroll anchoring, the
overview ruler (block marks by change kind), the caret-free row navigation (`j`/`k` move by block, `]c` jumps to the
next changed block), and `/` search (over block text).

Row heights come from text layout and are cached per (block, width, zoom); a width change re-measures visible rows
first. Images start at a placeholder size from the image header when it's already cached, otherwise a fixed
placeholder, and adjust when loaded. The scroll anchor already keeps the view steady as rows grow.

### Drawing

- Headings, paragraphs and list items lay out rich text (bold, italic, code spans, links) with GitKay's font stacks
  and theme brushes. Headings scale with level; the diff zoom scales everything.
- Code blocks use the monospace font, GitKay's syntax highlighter and a panel background.
- Quotes draw a bar; list items draw bullets, numbers or checkboxes at their depth; tables draw a grid with column
  alignment and truncate cells past a maximum width.
- **Change marks** follow the diff's colours:
  - added: green bar in the gutter and a faint green tint
  - removed: red bar, faint red tint, text dimmed
  - modified: amber bar; inserted words green-tinted, deleted words struck through in red
  - unchanged: no mark
- A **changes only** option collapses long runs of unchanged blocks into "⋯ 12 unchanged sections" rows, like
  context gaps. The default shows the whole document, since the rendered view is for reading.

### Toggle

- A **Rendered / Source** toggle appears on the file header of markdown files (`.md`, `.markdown`) in the diff, and in
  the whole-file popup's layout buttons.
- `Ctrl+Shift+V` toggles the focused file, as in VS Code.
- The choice persists per file path per repository, across restarts (UI state). Setting: **Render markdown by
  default** (off). Uncommitted markdown files get the toggle too.
- Old file and new file layouts show that side's rendered document unmarked.
- **Side-by-side rendered:** the old document on the left and the new on the right, with aligned leaves on the same
  row (a removed leaf leaves a blank on the right, an added one on the left) and word changes marked on each side.

### Links

- A relative link to a markdown file opens it rendered in the whole-file popup at the same revision.
- `#heading` links scroll to the heading.
- Other links open in the system browser. Hovering shows the target.

### Copying and keys

- `y`/`Ctrl+C` on a focused block copies its markdown source (from the located lines), not the rendered text.
- Text selection within rendered blocks is a follow-up; the first version copies whole blocks.

## Images

- **Repository images** (relative paths, or absolute paths from the repository root): read from the tree at the
  revision being shown. The old document's images come from the parent commit, the new document's from the commit.
- Decoded with Avalonia's bitmap loader: PNG, JPEG, GIF (first frame), BMP, WebP. SVG shows its alt text with an
  "SVG image" marker; SVG rendering is a follow-up.
- Limits: 10 MB per image, scaled to the column width, cached per (commit, path).
- An image whose file changed between the revisions shows as a modified block with old and new side by side, even if
  the markdown line didn't change.
- **Remote images** (`http`/`https`) are off by default, because loading them contacts the host (and can reveal that
  you opened the file). Each shows a "Load image from example.com" placeholder; a setting turns them on everywhere.
  Loading goes through a size-limited fetch with a timeout, and failures show the alt text.

## Effects and structure

| Part | Where | Notes |
| --- | --- | --- |
| Markdig → document model | `GitKay.Core.Markdown` | Pure |
| Leaf flattening, alignment, word diff | `GitKay.Core.Markdown` | Pure, generic Myers in `GitKay.Kit` |
| Row layout (`RenderedBlockRow`) | `GitKay.Core.DiffRows` | Extends existing layout |
| Link and image path resolution | `GitKay.Core.Markdown` | Pure: (file path, revision, target) → repository path or URL |
| Reading images from a tree | `GitService` | Axial flow, like the whole-file load |
| Remote image fetch | `GitService`-adjacent service | Axial flow with timeout and size cap |
| Layout, drawing, image cache | C# renderer used by `DiffSurfaceControl` | Measurement and drawing only |

Parsing and alignment run off the UI thread when a markdown file's diff or content loads, and are cached per
(commit, path).

## Limits and fallbacks

- Files over 1 MB, or with more than 5,000 leaves, stay in source view with a note.
- A parse or alignment failure falls back to source view and records the error in diagnostics.
- Binary or non-UTF-8 content never offers the toggle.

## Testing

- **Model:** Markdig conversion for every block and inline kind, front matter, reference links, nested lists and quotes.
- **Alignment:** unchanged documents; a rewrapped paragraph; a reworded sentence; inserted and deleted blocks; a
  heading rename; list items reordered; table row edits; code block edits; moved sections (removed + added).
- **Words:** insertions, deletions, punctuation, repeated words, emphasis spanning changed words.
- **Paths:** relative, `../`, root-absolute and remote images and links.
- **UI (headless):** toggle on and off keeps the header in place; row heights at two widths; image placeholder then
  loaded height with a steady scroll position; `]c` visits changed blocks; copying a block's source.
- **AOT self-test:** parse and align a sample document, and decode a PNG.

## Delivery

1. **Model and alignment in F#,** with tests (no UI).
2. **Renderer in the whole-file popup** (goal 1): blocks, rich text, code, lists, quotes, tables; repository images;
   toggle.
3. **Rendered diff in the diff pane** (goal 2): rendered rows, change marks, word spans, overview marks, `]c`; then
   side-by-side rendered.
4. **Links, `Ctrl+Shift+V`, copying source, `/` search** over rendered text.
5. **Remote images** behind their setting; **changes only** collapsing; limits and fallbacks.
6. Follow-ups: SVG, text selection inside rendered blocks, move detection.

## Decisions

1. Remote images are off by default, with click-to-load per image and a setting to load them everywhere.
2. The Rendered/Source choice persists per file per repository across restarts.
3. Side-by-side rendered is in scope.
