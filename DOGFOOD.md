# Unreleased package dogfood

Status: Axial packages are on `0.11.0-local.4`; the Elmish package is now `Axial.ElmishFlows` (namespace unchanged).
The repository caches live in a root scope owned by the repository environment. Default-severity guardrails build passes.

| Label | Package | File affected | Finding | Suggested fix |
| --- | --- | --- | --- | --- |
| resolved | Axial.Elmish | `src/GitKay.Core/App.fs`, `CommitWindow.fs`, `SelfTest.fs`, `Debounce.fs` | Replaced the in-repo `Axial.Elmish` project with the `Axial.Elmish` package. `AxialElmishRuntime` → `ElmishRuntime<ClockEnvironment>` (`Post` wired to Glue's `ElmishHost.post` from `GitKay.UI`, `OnDefect` to the existing `CmdDiagnostics` failure path); `Cmd.OfFlow.ofFlow` → `Cmd.OfFlow.either` with `Flow.localEnv (fun _ -> gitEnv)`; `ofFlowLatest`/`slot.Cancel` → `Cmd.OfFlow.Latest.either`/`cancel` keyed by the slot name (and `context:<old>\0<new>` per context file); `CmdDiagnostics.Registry` → `runtime.Registry`; `runtime.Host` → `App.startWithCancellation` with an app-owned token. | Document the recipe in the Axial.Elmish guide (it now has "Per-command environments"). |
| observed | Axial.Guardrails | `Directory.Build.props`, packaged `build/Axial.Guardrails.props` | The package now honours `AXIAL_GUARDRAILS_SEVERITY`, but only when `AxialGuardrailsSeverity` is still empty. GitKay's `Directory.Build.props` pins `<AxialGuardrailsSeverity>error</AxialGuardrailsSeverity>`, so the environment variable has no effect here; `-p:AxialGuardrailsSeverity=warning` (a command-line global property) is required to downgrade. | Remove the hard `error` default (or gate it on `CI`) so the documented env var works locally, and document `-p:AxialGuardrailsSeverity=warning` as the explicit override. |
| observed | Axial.Guardrails | `src/GitKay.Core/Debounce.fs`, `GitService.fs` | AXG008: scope-owned Cache/Queue returned from `Flow.run` at long-lived boundaries. | Document how to construct long-lived caches and queues in a host scope. |
| resolved | Axial.HttpClient | `src/GitKay.Core/GitService.fs`, `App.fs` | Remote Markdown image requests now use `Http.send` with a 10 MB body limit and 10 s timeout through `GitEnv.IHasHttp`; the UI's existing cancellable image command binds the flow. The one shared live `HttpClient` needs an explicit `axial-allow-effect: http` annotation at its construction boundary. | No API friction; `Request.maxResponseBytes` enforces the limit even without `Content-Length`. |
| read | Axial.Elmish | `src/GitKay.Core/CmdDiagnostics.fs` | The package's `CmdDiagnostics` equivalent is per-runtime; the diagnostics windows still need one stable surface, so GitKay keeps a thin `GitKay.Core.CmdDiagnostics` shim that `App` points at the live runtime's `Registry` (and `SelfTest` at its local runtime). | n/a (app-level adapter, not a package gap). |

Suppressions before: **25** `axial-allow-effect` occurrences under `src`. After current changes: **26** (one explicit blocking
self-test entry-point annotation). Filesystem suppressions have **not** been removed.

## Verification

- `timeout 1500 dotnet build GitKay.slnx -p:UseLocalGlue=false`: passed at default guardrails severity, no AXG errors. The two cache AXG008 findings disappeared after moving construction into `App.startWith`; unscoped Debounce queues no longer report AXG008. Repository-open synchronously publishes the cache scope (`axial-allow-effect: blocking` at this host boundary); the environment stops it on switch and shutdown.
- `timeout 1500 dotnet test GitKay.slnx -p:UseLocalGlue=false`: passed (341 core, 90 UI).
- Render harness (UI test project, headless pixel tests): passed (90).
- `timeout 1500 dotnet run --project src/GitKay.UI/GitKay.UI.csproj -p:UseLocalGlue=false --no-build -- --self-test`: passed.
- Filesystem suppression count under `src`: 19 before, 19 after. Filesystem migration deferred.

## Previous validation (local.2)

- `timeout 1500 dotnet build GitKay.slnx -p:UseLocalGlue=false -p:AxialGuardrailsSeverity=warning`: **succeeded**.
- `timeout 1500 dotnet test GitKay.slnx -p:UseLocalGlue=false -p:AxialGuardrailsSeverity=warning`: **passed** — GitKay.Tests 341, GitKay.UI.Tests 90.
- `AXIAL_GUARDRAILS_SEVERITY=warning dotnet build GitKay.slnx -p:UseLocalGlue=false --no-restore`: **failed** with AXG008/AXG001 because `Directory.Build.props` forces `AxialGuardrailsSeverity=error`, so the package's env-var override (which applies only when the property is empty) never runs.
- Render harness, published self-test, and AOT publish/self-test: **not run** in this pass.

## Released-package verification

With the stale NuGet HTTP cache cleared, forced restore from nuget.org succeeds using Axial 0.11.0, Reified 0.11.0 and Glue 0.2.0 (`dotnet restore GitKay.slnx -p:UseLocalGlue=false --force-evaluate`). Each verification command below ran under `timeout 1500`:

| Check | Command | Result |
| --- | --- | --- |
| Default-severity build | `dotnet build GitKay.slnx -p:UseLocalGlue=false` | Passed; 10 obsolete API warnings, 0 errors. |
| Tests | `dotnet test GitKay.slnx -p:UseLocalGlue=false` | Passed; 341 core and 90 UI tests. |
| Render harness | `dotnet test tests/GitKay.UI.Tests/GitKay.UI.Tests.fsproj -p:UseLocalGlue=false --filter FullyQualifiedName~Render` | Passed; 15 render tests. |
| App self-test | `dotnet run --project src/GitKay.UI/GitKay.UI.csproj -p:UseLocalGlue=false --no-build -- --self-test` | Passed. |
| Linux NativeAOT publish | `bash scripts/publish-gitkay.sh --aot --linux -p:UseLocalGlue=false` | Passed; existing FSharp.Core AOT/trim and obsolete API warnings. |
| Published self-test | `artifacts/publish/gitkay/gitkay --self-test` | Passed. |
