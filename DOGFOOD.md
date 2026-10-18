# Unreleased package dogfood

Status: Axial packages are on `0.11.0-local.2`; the in-repo `src/Axial.Elmish` shim is gone and GitKay now uses the
packaged `Axial.Elmish` (namespace `Axial.ElmishFlows`) with the per-command `Flow.localEnv` recipe. The solution builds
and the full test suite passes at the transitional warning-level guardrails setting. The AXG008/AXG001 findings are still
real ownership/blocking-boundary questions, not yet redesigned; they are downgraded with `-p:AxialGuardrailsSeverity=warning`.

| Label | Package | File affected | Finding | Suggested fix |
| --- | --- | --- | --- | --- |
| resolved | Axial.Elmish | `src/GitKay.Core/App.fs`, `CommitWindow.fs`, `SelfTest.fs`, `Debounce.fs` | Replaced the in-repo `Axial.Elmish` project with the `Axial.Elmish` package. `AxialElmishRuntime` → `ElmishRuntime<ClockEnvironment>` (`Post` wired to Glue's `ElmishHost.post` from `GitKay.UI`, `OnDefect` to the existing `CmdDiagnostics` failure path); `Cmd.OfFlow.ofFlow` → `Cmd.OfFlow.either` with `Flow.localEnv (fun _ -> gitEnv)`; `ofFlowLatest`/`slot.Cancel` → `Cmd.OfFlow.Latest.either`/`cancel` keyed by the slot name (and `context:<old>\0<new>` per context file); `CmdDiagnostics.Registry` → `runtime.Registry`; `runtime.Host` → `App.startWithCancellation` with an app-owned token. | Document the recipe in the Axial.Elmish guide (it now has "Per-command environments"). |
| observed | Axial.Guardrails | `Directory.Build.props`, packaged `build/Axial.Guardrails.props` | The package now honours `AXIAL_GUARDRAILS_SEVERITY`, but only when `AxialGuardrailsSeverity` is still empty. GitKay's `Directory.Build.props` pins `<AxialGuardrailsSeverity>error</AxialGuardrailsSeverity>`, so the environment variable has no effect here; `-p:AxialGuardrailsSeverity=warning` (a command-line global property) is required to downgrade. | Remove the hard `error` default (or gate it on `CI`) so the documented env var works locally, and document `-p:AxialGuardrailsSeverity=warning` as the explicit override. |
| observed | Axial.Guardrails | `src/GitKay.Core/Debounce.fs`, `GitService.fs` | AXG008: scope-owned Cache/Queue returned from `Flow.run` at long-lived boundaries. | Document how to construct long-lived caches and queues in a host scope. |
| observed | Axial.Guardrails | `src/GitKay.Core/GitService.fs` | AXG001: a blocking wait remains at the HTTP boundary (`GitService.fs:1122`). | Provide an async boundary; the self-test entry-point wait is already annotated. |
| read | Axial.Elmish | `src/GitKay.Core/CmdDiagnostics.fs` | The package's `CmdDiagnostics` equivalent is per-runtime; the diagnostics windows still need one stable surface, so GitKay keeps a thin `GitKay.Core.CmdDiagnostics` shim that `App` points at the live runtime's `Registry` (and `SelfTest` at its local runtime). | n/a (app-level adapter, not a package gap). |

Suppressions before: **25** `axial-allow-effect` occurrences under `src`. After current changes: **26** (one explicit blocking
self-test entry-point annotation). Filesystem suppressions have **not** been removed.

## Validation

- `timeout 1500 dotnet build GitKay.slnx -p:UseLocalGlue=false -p:AxialGuardrailsSeverity=warning`: **succeeded**.
- `timeout 1500 dotnet test GitKay.slnx -p:UseLocalGlue=false -p:AxialGuardrailsSeverity=warning`: **passed** — GitKay.Tests 341, GitKay.UI.Tests 90.
- `AXIAL_GUARDRAILS_SEVERITY=warning dotnet build GitKay.slnx -p:UseLocalGlue=false --no-restore`: **failed** with AXG008/AXG001 because `Directory.Build.props` forces `AxialGuardrailsSeverity=error`, so the package's env-var override (which applies only when the property is empty) never runs.
- Render harness, published self-test, and AOT publish/self-test: **not run** in this pass.
