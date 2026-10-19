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

## Remaining guardrail suppressions

Suppressions under `src` went from **28** to **13** `axial-allow-effect` occurrences (commits f4839ef, 2705fa2:
trash-folder access and untracked-file reads now route through `IFileSystem`, removing all 14 `Git/Trash.fs` markers and
one `GitService.fs` marker).

Two boundaries stay by design:

- `src/GitKay.Core/FolderSource.fs` keeps its file-level `axial-allow-effect-file: filesystem`. It is the documented
  synchronous filesystem boundary for folder comparison and previewing: the C# UI calls it synchronously
  (`App.axaml.cs`, `FolderWindow.axaml.cs`, `SelfTest.cs`).
- `src/GitKay.Core/Debounce.fs` stays as the C# projection's debounce (`MainProjection.cs` uses it directly), rather than
  moving behind an Elmish command, and carries no suppression marker.

The other 12 markers, by category:

| Category | Where | Why it stays |
| --- | --- | --- |
| environment | `App.fs:577` | Resolves startup targets against `Environment.CurrentDirectory` before a `GitEnv` flow exists. |
| environment | `Git/Internal/RepositoryDiscovery.fs:11` | Host startup lists candidate repository roots from the process environment. |
| environment | `GitService.fs:313` | Sizes diff-worker parallelism from `Environment.ProcessorCount`; no behavioural effect worth routing through `env`. |
| environment | `GitService.fs:1486` | Sizes search-worker parallelism from `Environment.ProcessorCount`. |
| clock | `CmdDiagnostics.fs:63` | Timestamps an unobserved defect with `DateTimeOffset.UtcNow` outside a flow. |
| filesystem | `GitService.fs:539` | Synchronous command-line argument resolution (`File.Exists`/`Directory.Exists`) before a flow exists. |
| filesystem | `GitService.fs:1167` | Synchronous compatibility preview API reads the working-tree image path; no flow environment. |
| filesystem | `GitService.fs:1173` | The same preview API reads the image bytes. |
| http | `GitService.fs:371` | One shared `HttpClient` at the live service boundary. |
| blocking | `GitService.fs:394` | Synchronous repository-open boundary; the cache scope outlives this call. |
| blocking | `SelfTest.fs:33` | Self-test entry point blocks on command dispatch (`Task.Wait`). |
| sleep | `SelfTest.fs:2` (file-level) | The self-test runs at a process entry point and waits on real commands and time. |

## Verification

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
