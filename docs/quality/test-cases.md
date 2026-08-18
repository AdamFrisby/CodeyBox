# Test Cases

CodeyBox treats **test cases** as a first-class artifact attached to a work
item. They are the data model that E2E execution, the mutation / regression
gates, conformance accounting, and JobTrack propagation all hang off.

The model is deliberately **lean and execution-focused**. CodeyBox is *not* a
test-management tool; the management taxonomy (browsable hierarchies, surface
areas, sort orders) is owned by JobTrack and applied at propagation time.

## Lean schema

A test case persists exactly the fields a downstream executor or coverage
gate needs:

| Field | Type | Notes |
|---|---|---|
| `Id` | string | Stable id. The API accepts a client-supplied id or auto-generates one. |
| `Name` | string | Required, free-form. |
| `Description` | string | Required, may be empty. |
| `SourceWorkItemId` | string | Immutable cross-system provenance link to the owning work item. **The analogue of JobTrack's `SourceTaskId`** — see [JobTrack mapping](#jobtrack-mapping) below. |
| `CreatedAt`, `UpdatedAt` | DateTimeOffset | Audit timestamps; ISO 8601 ("O") round-trip. |
| `IsArchived` | bool | Soft archive flag. Archived rows still list (no automatic hiding). |
| `AutomationKind` | enum? | `Manual` \| `Unit` \| `Integration` \| `E2eReplay`. Optional — a purely manual case has no automation kind. |
| `ExecutableArtifactJson` | string? | Opaque JSON payload describing the executable artifact. For `E2eReplay`: recorded steps + selectors + assertions. The shape is owned by the consuming executor; the store does not validate it. |
| `ConformanceJson` | string? | Opaque JSON payload describing the conformance condition — the "must fail when `<branch>` is broken" rule the case has to satisfy to count toward coverage. |
| `Label` | string? | Optional flat tag for coverage grouping (capability / area). One value, no hierarchy. |
| `LastRunPassed` | bool? | Most-recent execution outcome. |
| `LastRunAt` | DateTimeOffset? | Most-recent execution timestamp. |
| `LastRunResult` | string? | Free-form most-recent execution detail. |

All automation fields (`AutomationKind`, `ExecutableArtifactJson`,
`ConformanceJson`, `Label`, `LastRun*`) are **optional**, so a freshly-created
manual placeholder is a valid test case.

### Persistence

`SqliteTestCaseStore` shares the orchestrator state database with
`SqliteWorkItemStore`. The `test_cases` table is created with an idempotent
`CREATE TABLE IF NOT EXISTS` migration that runs on store construction; it
applies cleanly against an existing database (verified by
`TestCasePersistenceTests.Migration_AppliesToExistingDb`).

A foreign key on `source_work_item_id REFERENCES work_items(id) ON DELETE
CASCADE` ties the test case's lifetime to its owning work item — deleting the
work item automatically removes its cases.

Reads and writes are serialised through the shared `SqliteDatabaseWriteGate`.
Microsoft.Data.Sqlite explicitly does not support overlapping commands on a
single connection, so the gate guards both writes and the row-buffering side of
`GetAsync` / `ListAsync` / `ListByWorkItemAsync`.

## API

The REST surface is the minimum needed to support the two near-term
consumers — the planning phase emitting cases in bulk, and the E2E executor
reading replay artifacts:

| Verb | Path | Purpose |
|---|---|---|
| `POST` | `/testcases` | Create a single test case. |
| `POST` | `/testcases/bulk` | Create up to `CodeyBox:MaxBulkItems` cases in one atomic transaction. |
| `GET` | `/testcases` | List all test cases. |
| `GET` | `/testcases/{id}` | Retrieve a single test case. |
| `PUT` | `/testcases/{id}` | Update a test case. `SourceWorkItemId` is immutable. |
| `DELETE` | `/testcases/{id}` | Delete a test case. |
| `GET` | `/workitems/{workItemId}/testcases` | List the cases attached to a work item. |

`CodeyBox:MaxBulkItems` defaults to `1000` and is capped at `10_000` by the
options validator (see `CodeyBoxOptions.MaximumMaxBulkItems`).

## Emission from an approved plan

When the optional planning phase is on (the `plan` knob — see
[the planning docs](../../AGENTS.md)) and a plan is **approved**, CodeyBox turns
the plan's declared test intentions into real, queryable test cases so the
downstream coverage / execution gates consume structured cases instead of
prose. The plan artifact's `testStrategy` array is the source: each entry is
one scenario the plan commits to, and each becomes one `TestCase` linked to the
work item via `SourceWorkItemId`.

`PlanTestCaseReconciler` (in `CodeyBox.Core`) drives this at the plan-approval
transition inside `PipelineRunner`:

- **`AutomationKind`** is inferred from lexical markers in each scenario
  (`PlanTestCaseSynthesizer.ClassifyAutomationKind`): `e2e` / `end-to-end` /
  `replay` / a browser-driver name → `E2eReplay`; `integration` → `Integration`;
  `unit` → `Unit`; anything else → `Manual`. When more than one marker is
  present the broader scope wins (`e2e` > `integration` > `unit`).
- **e2e cases are emitted without a committed replay** — `AutomationKind =
  E2eReplay` with `ExecutableArtifactJson = null`. The separate replay-authoring
  orchestration fills the artifact in later; reconcile preserves it.
- **Idempotent / reconciling.** The case id is deterministic in
  `(workItemId, ordinal)`, so plan-rework updates changed scenarios in place,
  appends new ones, and prunes the tail a shorter plan dropped — it never
  duplicates. A re-approval of an unchanged plan is a no-op. Reconcile only
  touches the plan-derived ids; manually-authored cases (random ids) and any
  committed replay / conformance / `LastRun*` history an authoring or execution
  item filled in are left untouched.
- **Gated.** Only planned items (the `plan` knob on) ever reach this path, so
  unplanned items are unaffected. Set `CodeyBox:EmitPlanTestCases=false` to keep
  the planning phase without materialising test cases (captured at startup;
  edits require a restart). Emission is best-effort: a store or parse failure is
  logged and swallowed rather than stranding an already-approved plan.

## JobTrack mapping

JobTrack (the test-management app this artifact will export to) carries a
management-oriented `TestCase` model:

- `SurfaceArea` — a grouping entity for humans browsing the suite.
- `ParentTestCaseId` / `Path` / `Level` / `SortOrder` — a deep, browsable
  hierarchy.
- `SourceTaskId` — a stable cross-system provenance id pointing back at the
  task that produced the case.

CodeyBox **intentionally does not model the SurfaceArea entity or the
hierarchy fields**. Those are JobTrack-app concerns, applied at propagation
time — not data the orchestrator or any executor needs.

The propagation contract is single-field: CodeyBox's `SourceWorkItemId` is the
analogue of JobTrack's `SourceTaskId`. The pair lets the propagation exporter
(see [Export to JobTrack](#export-to-jobtrack)) push a CodeyBox case to
JobTrack, where JobTrack maps it into its own SurfaceArea + hierarchy on its
side. Because `SourceWorkItemId` is the only stable link, the API rejects
attempts to change it on an existing test case (PUT requests with a different
`SourceWorkItemId` return HTTP 400).

| Concern | CodeyBox | JobTrack |
|---|---|---|
| Stable provenance id | `SourceWorkItemId` | `SourceTaskId` |
| Group / area | `Label` (optional flat tag) | `SurfaceArea` (entity, assigned at propagation) |
| Browsable hierarchy | — | `ParentTestCaseId` / `Path` / `Level` / `SortOrder` |
| Executable artifact | `ExecutableArtifactJson` (opaque, executor-defined) | — |
| Conformance condition | `ConformanceJson` (opaque, mutation-gate-defined) | — |

## Export to JobTrack

When a work item reaches the terminal `Done` state, its linked test cases are
propagated to JobTrack — **opt-in per project and strictly best-effort**. The
export is wired through `IJobTrackTestCaseExporter` and never fails the
already-completed item: on any transport/HTTP error a case is retried (bounded,
backed off) then counted as failed, and even a bug in the exporter is logged
and swallowed. Propagation is **idempotent**: JobTrack upserts on the case's
`ExternalSourceId` (the CodeyBox `TestCase.Id`), so re-export updates the
existing JobTrack row instead of creating a duplicate.

The `SourceTaskId` is read from the work item's external-id namespace
(`ExternalIdNamespace`, default `jobtrack`); an item without a JobTrack task id
in that namespace is skipped. CodeyBox's `AutomationKind` enum maps to JobTrack
tokens (`Manual→manual`, `Unit→unit`, `Integration→integration`,
`E2eReplay→e2e-replay`); the hierarchy fields are never sent (JobTrack-owned).

### Configuration

Per project under `CodeyBox:Projects:<id>:JobTrackExport` (all fields
hot-reloadable; a project with no `JobTrackExport` block exports nothing):

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | `false` | Master opt-in. When true, `BaseUrl` is required and validated at config load. |
| `BaseUrl` | — | Absolute http(s) base URL of the JobTrack instance. |
| `ImportPath` | `/api/test-cases/import` | Import endpoint path appended to `BaseUrl`. |
| `TokenEnvVar` | — | Name of the env var holding the JobTrack bearer token. Only the **name** is stored; the value is read at export time, sent per-request, never persisted. Null → unauthenticated. |
| `ExternalIdNamespace` | `jobtrack` | Work-item external-id namespace holding the owning JobTrack task id. |
| `DefaultSurfaceArea` | — | Optional default SurfaceArea placement; null lets JobTrack apply its own. |
| `MaxAttempts` | `3` | Upsert attempts per case before it counts as failed (≥ 1). |
| `RetryBaseDelayMs` | `250` | Base back-off; the nth retry waits `n × RetryBaseDelayMs`. `0` disables the delay. |

## Out of scope

This data model is the foundation only. The following land as separate items:

- **Executing** test cases on cheap cloud VMs (E2E infra).
- The mutation / regression gates that use `ConformanceJson` to score
  coverage.
