# Unreleased package dogfood

Status: Axial packages are on `0.12.0-local.1`, Reified on `0.12.1`; the Elmish package is `Axial.ElmishFlows`
(namespace unchanged). The repository caches live in a root scope owned by the repository environment. Guardrails use
`Axial.Guardrails.toml` roles and boundaries; `AxialGuardrailsSeverity` was removed from `Directory.Build.props`.

| Label | Package | File affected | Finding | Suggested fix |
| --- | --- | --- | --- | --- |
| resolved | Axial.Elmish | `src/GitKay.Core/App.fs`, `CommitWindow.fs`, `SelfTest.fs`, `Debounce.fs` | Replaced the in-repo `Axial.Elmish` project with the `Axial.Elmish` package. `AxialElmishRuntime` → `ElmishRuntime<ClockEnvironment>` (`Post` wired to Glue's `ElmishHost.post` from `GitKay.UI`, `OnDefect` to the existing `CmdDiagnostics` failure path); `Cmd.OfFlow.ofFlow` → `Cmd.OfFlow.either` with `Flow.localEnv (fun _ -> gitEnv)`; `ofFlowLatest`/`slot.Cancel` → `Cmd.OfFlow.Latest.either`/`cancel` keyed by the slot name (and `context:<old>\0<new>` per context file); `CmdDiagnostics.Registry` → `runtime.Registry`; `runtime.Host` → `App.startWithCancellation` with an app-owned token. | Document the recipe in the Axial.Elmish guide (it now has "Per-command environments"). |
| resolved | Axial.Guardrails | `Directory.Build.props` | The 0.11 severity property was removed; 0.12 rules carry their own levels and the policy lives in `Axial.Guardrails.toml`. | n/a |
| observed | Axial.Guardrails | `src/GitKay.Core/Debounce.fs`, `GitService.fs` | AXG008: scope-owned Cache/Queue returned from `Flow.run` at long-lived boundaries. | Document how to construct long-lived caches and queues in a host scope. |
| resolved | Axial.HttpClient | `src/GitKay.Core/GitService.fs`, `App.fs` | Remote Markdown image requests now use `Http.send` with a 10 MB body limit and 10 s timeout through `GitEnv.IHasHttp`; the UI's existing cancellable image command binds the flow. The one shared live `HttpClient` needs an explicit `axial-allow-effect: http` annotation at its construction boundary. | No API friction; `Request.maxResponseBytes` enforces the limit even without `Content-Length`. |
| read | Axial.Elmish | `src/GitKay.Core/CmdDiagnostics.fs` | The package's `CmdDiagnostics` equivalent is per-runtime; the diagnostics windows still need one stable surface, so GitKay keeps a thin `GitKay.Core.CmdDiagnostics` shim that `App` points at the live runtime's `Registry` (and `SelfTest` at its local runtime). | n/a (app-level adapter, not a package gap). |

## Guardrail policy and remaining suppressions

`Axial.Guardrails.toml` assigns the default `domain` role and makes `FolderSource.fs`, `HostCancellation.fs` and
`SelfTestBridge.fs` `capabilities`: the folder-reading boundary, the process-lifetime token source and the blocking
self-test bridge. `FolderSource.fs` no longer performs raw filesystem access: it walks and reads through
`Axial.FileSystem`'s `IFileSystem`, so its old file-level suppression is gone. The TOML also lists the four exact files
that keep one-off AXG001 allow comments: `App.fs`, `CmdDiagnostics.fs`, `GitService.fs` and
`Git/Internal/RepositoryDiscovery.fs`.

Remaining AXG001 allow comments:

| Category | Where | Why it stays |
| --- | --- | --- |
| clock | `CmdDiagnostics.fs` | Timestamps an unobserved defect outside a flow. |
| filesystem | `GitService.fs` | Synchronous command-line argument resolution and the compatibility preview API, both before a flow environment exists. |
| http | `GitService.fs` | One shared `HttpClient` at the live service boundary. |
| blocking | `GitService.fs` | Synchronous repository-open boundary; the cache scope outlives this call. |
| environment | `GitService.fs` | Sizes diff and search workers from `Environment.ProcessorCount`; no behavioural effect. |

The remaining AXG010 handlers are acknowledged with `// axial-note AXG010: <reason>` so they stay visible as Info
instead of warnings. `Axial.Guardrails.lock` matches the current policy; ordinary builds pass `axial check`.

## 0.12 migration verification

- `dotnet build GitKay.slnx` and `dotnet build GitKay.slnx -c Release`: passed.
- `dotnet test GitKay.slnx`: passed (344 core, 90 UI).
- `dotnet run --project src/GitKay.UI/GitKay.UI.csproj --no-build -- --self-test`: passed.
- `dotnet restore` used `--source` for the local 0.12 feed and nuget.org; no machine paths are committed.

## Previous validation (0.11)

- `timeout 1500 dotnet build GitKay.slnx -p:UseLocalGlue=false`: passed at default guardrails severity, no AXG errors. The two cache AXG008 findings disappeared after moving construction into `App.startWith`; unscoped Debounce queues no longer report AXG008. Repository-open synchronously publishes the cache scope (`axial-allow-effect: blocking` at this host boundary); the environment stops it on switch and shutdown.
- `timeout 1500 dotnet test GitKay.slnx -p:UseLocalGlue=false`: passed (341 core, 90 UI).
- Render harness (UI test project, headless pixel tests): passed (90).
- `timeout 1500 dotnet run --project src/GitKay.UI/GitKay.UI.csproj -p:UseLocalGlue=false --no-build -- --self-test`: passed.
- Filesystem suppression count under `src`: 19 before, 4 after (FolderSource plus three synchronous compatibility or argument-resolution boundaries); trash-folder and untracked-file reads now go through `IFileSystem`.

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
