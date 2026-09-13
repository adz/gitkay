# File-scoped diff expansion

## Goal
Expand collapsed context before, between, and after diff hunks without reloading or repositioning unrelated files and gaps.

## Invariants
- Expansion is scoped to one file and one collapsed range.
- Expanding upward keeps the first previously visible source line at the same viewport Y.
- Expanding downward leaves the scroll offset unchanged.
- Existing rows never disappear while content is loading.
- Newly inserted rows expand over 100 ms; unchanged rows do not fade.
- Only explicit bounded controls are actionable.
- Stale expansion results are rejected when commit, file, or request generation changes.

## Model
- Add a typed `DiffGap` identity containing file key, direction, old/new boundaries, and hidden count when known.
- Represent leading, internal, and trailing gaps.
- Store loaded full-file context separately from the ordinary configured-context diff.
- Store expanded ranges as presentation state; do not mutate the global context preference.

## Flow
1. UI sends an expansion intent for a stable gap identity.
2. An Axial/latest-request flow lazily loads complete context for only that file.
3. Result is accepted only for the current commit/file generation.
4. Projection reveals ten lines from the requested edge or the complete requested range.
5. Surface anchors the appropriate existing boundary and animates inserted row heights for 100 ms.

## Delivery sequence
1. Pure range calculation and merge tests.
2. File-scoped core intent/result with cancellation and stale-result tests.
3. Leading/internal/trailing gap projection tests.
4. Explicit renderer hit regions and hover/pressed states.
5. Direction-aware viewport anchoring.
6. 100 ms height interpolation for inserted rows.
7. Performance and application smoke validation.
