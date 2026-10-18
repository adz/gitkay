# Unreleased package dogfood (incomplete)

Status: package version switch and initial Process API conversion attempted; solution does **not** build yet. No migration or runtime validation should be inferred from this document.

| Label | Package | File affected | Finding | Suggested fix |
| --- | --- | --- | --- | --- |
| observed | Axial.Process | `src/GitKay.Core/GitService.fs` | `Process.capture` and `Process.stdin` no longer compile. Converting to `Process.toFlow` and `Process.stdinFromText` clears those compiler errors. | Add a concise 0.10-to-0.11 command/flow recipe to the migration notes. |
| observed | Axial.Guardrails | `src/GitKay.Core/GitService.fs`, `src/GitKay.Core/Debounce.fs` | AXG008 detects scope-owned Cache/Queue returned from `Flow.run`. Existing app constructs these at long-lived boundaries; their ownership needs redesign, not an automatic suppression. | Document how to construct long-lived caches and queues in a host scope. |
| observed | Axial.Guardrails | `src/GitKay.Core/GitService.fs`, `src/GitKay.Core/SelfTest.fs` | AXG001 flags blocking waits in HTTP and self-test command dispatch. | Provide async boundaries; mark only genuinely synchronous test/host boundaries explicitly. |
| observed | Axial.Guardrails | `src/GitKay.Core/GitKay.Core.fsproj`, packaged `build/Axial.Guardrails.targets` | `AXIAL_GUARDRAILS_SEVERITY=warning dotnet build` still reports AXG001/AXG008 as errors: the packaged target writes severity from the MSBuild `AxialGuardrailsSeverity` property, not the environment variable. | Honour `AXIAL_GUARDRAILS_SEVERITY` in the package targets when no explicit MSBuild severity is supplied, or document `-p:AxialGuardrailsSeverity=warning` as the migration command. |
| read | Axial.Elmish | `src/GitKay.Core/App.fs` | New runtime requires a host `Post`, `OnDefect`, and an environment; the local runtime uses static diagnostics and slots. | Include a complete Avalonia host migration example showing diagnostics and shutdown. |

Suppressions before: **25** occurrences of `axial-allow-effect` under `src` (`grep -r ... | wc -l`). After current changes: **26** (one explicit blocking self-test entry-point annotation). Filesystem suppressions have **not** been removed.

## Validation

- `dotnet build GitKay.slnx -p:UseLocalGlue=false`: **failed** with AXG008 and AXG001 after initial Process API fixes.
- `AXIAL_GUARDRAILS_SEVERITY=warning dotnet build GitKay.slnx -p:UseLocalGlue=false --no-restore`: **failed**; packaged targets ignore that environment variable and still emit errors. Retried with `-p:AxialGuardrailsSeverity=warning`; build did not finish within 120 seconds. No successful build verified.
- `timeout 1500 dotnet build GitKay.slnx -p:UseLocalGlue=false -p:AxialGuardrailsSeverity=warning`: **succeeded** after marking the blocking self-test entry-point wait. This is a transitional warning-level build, not a clean guardrails build.
- Tests, Render harness, self-test, AOT publish/self-test: **not run**.
