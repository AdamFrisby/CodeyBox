# Pipeline health and timings

Two operator questions, two endpoints. *Where is the time going?* —
`/workitems/{id}/timings`, per step, per phase. *Is the pipeline itself
breaking?* — `/fleet/transition-health`, which separates plumbing failures from
work that is simply hard.

## Where the time goes

Every significant step of a work item is measured into the `work_item_timings`
SQLite table, so "why did this take 40 minutes?" is answerable from data. Timing
writes are best-effort: a failed write is logged at Warning and never affects
the work item.

### Instrumented steps

| Phase | Step | Description |
|---|---|---|
| `work` | `vm.clone` | Cloning the baseline VM (Multipass or Incus) |
| `work` | `vm.launch` | Launching a fresh VM (no-baseline path) |
| `work` | `vm.mount` | Mounting project directories into the VM |
| `work` | `vm.start` | Starting the VM |
| `work` | `vm.exec_first` | First sandbox `ExecAsync` call (cloud-init catch-up) |
| `work` | `vm.dispose` | Deleting and purging the VM |
| `work` | `bwrap.exec_setup` | Setting up the bubblewrap sandbox root |
| `work` | `bwrap.exec_first` | First bubblewrap `ExecAsync` call |
| `work` | `bwrap.teardown` | Removing the bubblewrap sandbox root |
| `work` | `git.clone_into_sandbox` | Cloning the bare repo into the sandbox |
| `work` | `agent.exec` | Full agent execution (from spawn to exit) |
| `work` | `git.commit` | Staging + committing the agent's changes |
| `work` | `git.push_back_to_bare_repo` | Pushing the work branch to the bare repo |
| `rework` | same as `work` | Second (or later) work attempt after audit failure |
| `audit` | `auditor.<name>` | Each auditor's full build + analysis run |
| `audit` | `<language>.build` | Language build phase parsed from auditor stdout, for example `csharp.build` |
| `audit` | `<language>.test_run` | Language test execution phase parsed from auditor stdout, for example `csharp.test_run` |
| `audit` | `gitleaks.scan` | gitleaks scan phase parsed from auditor stdout |
| `audit` | `semgrep.scan` | semgrep scan phase parsed from auditor stdout |
| `merge` | `git.clone_into_sandbox` | Cloning the bare repo for the merge agent |
| `merge` | `agent.exec` | Merge agent execution |
| `upstream_push` | `upstream.complete` | Full upstream push wrapper (PipelineRunner) |
| `upstream_push` | `upstream.push_branch` | Git push of the work branch to the upstream remote |
| `upstream_push` | `upstream.api_create_pr` | GitHub API call to create the pull request |
| `upstream_push` | `upstream.api_merge_pr` | GitHub API call to merge the pull request |

The `metadata_json` column on each row carries extra context:

- `agent.exec` rows include `{"agent":"<kind>"}` (e.g., `"claude"`).
- `auditor.<name>` rows include `{"agent":"<kind>"}` — the auditor runner kind.
  The auditor name is already in the `step` column; the iteration is in the
  `iteration` column, not in `metadata_json`.
- `upstream.complete` rows include `{"attempt":<n>}`.

#### Agent-internal timing

Timings stop at `agent.exec` — what the agent did *inside* that span is not
broken out here. Tool-call and thinking/executing splits come from the captured
NDJSON streams instead, summarised into `agent_stream_summaries` once an item
reaches a terminal state; see [`agent-streams.md`](agent-streams.md). No
`agent.tool_call.*` rows are written to `work_item_timings`.

### Database schema

```sql
CREATE TABLE IF NOT EXISTS work_item_timings (
    id          TEXT    PRIMARY KEY,
    work_item_id TEXT   NOT NULL,
    phase       TEXT    NOT NULL,
    iteration   INTEGER,
    step        TEXT    NOT NULL,
    started_at  TEXT    NOT NULL,   -- ISO-8601 with offset
    ended_at    TEXT,               -- NULL while in-flight
    duration_ms INTEGER,            -- NULL while in-flight
    metadata_json TEXT NOT NULL DEFAULT '{}'
);
CREATE INDEX IF NOT EXISTS idx_timings_work_item_phase
    ON work_item_timings (work_item_id, phase, iteration, started_at);
```

The table lives in the same SQLite file as `work_items` and uses WAL mode
(`journal_mode=WAL`, `busy_timeout=30000`) for safe concurrent access.

### REST API

#### `GET /workitems/{id}/timings`

Returns timing data for a single work item.

**Response (200 OK):**

```json
{
  "workItemId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "totalDurationMs": 62000,
  "topSteps": [
    { "step": "agent.exec", "totalMs": 45000, "count": 2 },
    { "step": "git.clone_into_sandbox", "totalMs": 10000, "count": 2 }
  ],
  "byPhase": {
    "work": {
      "durationMs": 55000,
      "steps": [
        { "step": "git.clone_into_sandbox", "startedAt": "...", "endedAt": "...", "durationMs": 5000 },
        { "step": "agent.exec", "startedAt": "...", "endedAt": "...", "durationMs": 45000, "metadataJson": "{\"agent\":\"claude\"}" },
        { "step": "git.commit", "startedAt": "...", "endedAt": "...", "durationMs": 300 },
        { "step": "git.push_back_to_bare_repo", "startedAt": "...", "endedAt": "...", "durationMs": 200 }
      ]
    }
  }
}
```

Rows with `durationMs: null` are in-flight (the work item is currently running
that step).

**404** if the work item does not exist.
**400** if `{id}` is not a valid GUID.

#### `GET /workitems/timings/aggregate?n=50`

Returns aggregate statistics across the last *n* completed work items
(default: 50, max: 500).  The query streams the SQLite cursor rather than
loading all rows into memory.

**Response (200 OK):**

```json
{
  "workItemCount": 12,
  "stepStats": [
    {
      "phase": "work",
      "step": "agent.exec",
      "count": 14,
      "medianMs": 42000,
      "p95Ms": 110000
    },
    {
      "phase": "work",
      "step": "git.clone_into_sandbox",
      "count": 14,
      "medianMs": 3200,
      "p95Ms": 8000
    }
  ]
}
```

Percentiles are computed via sorted-array indexing (not approximate streaming
algorithms) which is acceptable because N is bounded at 500.

### Admin dashboard

Two pages are available in the admin UI:

- **Work item → Timings button** (`/work-items/{id}/timings`): per-item
  breakdown by phase and step, with in-flight rows shown.
- **Aggregate Timings** (`/timings/aggregate`): system-wide median and p95
  per step across the last N completed work items, with a configurable N
  picker and Refresh button.

### Disabling timing collection

Timing collection is enabled by default when the API starts.  To disable it,
remove or comment out the `ITimingStore` registration in `Program.cs`.  All
timing code paths check for a null store and silently skip collection.

## Is the plumbing healthy?

The transition-health metric reports infrastructure health of the pipeline
independently of work throughput. Done-rate conflates plumbing health with
hard-vs-easy work mix, quota state, and concurrency throttling: a healthy
system can have zero completions for hours (hard work + low concurrency) and
a degrading system can keep completing while its infra-failure rate climbs.
This metric isolates plumbing.

It is computed entirely from data the orchestrator already persists.

### What it measures

Over a configurable rolling window (default 24 h), the score is

```
score = legitimate_transitions / (legitimate_transitions + infra_failure_transitions)
```

Throughput (terminal `Done` items) is intentionally excluded from the source
data — the score cannot rise simply because more items completed.

A `Total = 0` window (genuinely idle fleet) scores 1.0 ("nothing has gone
wrong, so plumbing health is unknown but not bad"); this is the
conventional health-check choice. The companion `total_transitions` field
lets a dashboard surface "low-confidence" when the sample is small.

### Legitimate vs. infra-failure taxonomy

A stage transition is **LEGITIMATE** when it represents forward progress or
the audit loop working as designed:

- A successful agent run in Work / Rework / Merge.
- An audit that completed and reported genuine blocking findings (real
  Error-severity findings whose titles are NOT one of the LlmReviewAuditor
  infra patterns). The next iteration's `Auditing → Reworking` transition is
  the loop fulfilling its purpose, not a failure.
- An audit that passed cleanly.

A stage transition is an **INFRA failure** when the plumbing — not the work
— broke:

- Agent transport failure: non-zero exit, SIGTERM-kill (exit 143), the silent
  "produced no changes to commit" path (surfaces as `failure:agent` on the
  involvement row).
- Quota exhaustion mid-run (`failure:quota`).
- Per-attempt timeout (`failure:timeout`).
- Audit-stage infra: the LlmReviewAuditor's three "auditor died" finding
  titles — `review agent failed to run`, `agent did not write
  audit/result.json` (matched by prefix), `review agent produced invalid
  JSON`. Plus the build-verification gate's
  `required build unavailable: <command>` finding (matched by prefix —
  the trailing `<command>` is the display command). Note that the sibling
  `required build failed: <command>` finding is a real, legitimate
  blocking finding (the build genuinely broke); it counts as LEGITIMATE,
  not INFRA.
- Terminal `Failed` with `failure_kind` ∈ {`quota`, `timeout`, `agent`,
  `agent_unavailable`, `infrastructure`, `configuration`}.
- Terminal `MergeConflictResolutionFailed` (always classified as infra).
- Terminal `AbandonedAfterRecoveryAttempts` — the recovery loop gave up
  after `MaxRecoveryAttempts` host-shutdown / worker-died cycles. This is
  the canonical worker-died-without-preempt-checkpoint signature.

A transition is **SKIPPED** (counted in neither numerator nor denominator)
when it neither represents healthy progress nor an infra failure:

- Operator-driven cancellation (`failure:cancelled` / `cancelled`).
- An involvement row that is still in flight (`outcome` not yet finalised).
- Conflict-rework's `failure:semantic-incompatible` outcome (the agent
  declared the upstream/downstream branches semantically irreconcilable —
  a real, intended disposition, not infra failure).
- Terminal `Failed` with `failure_kind` ∈ {`build`, `cancelled`, `other`,
  null}. `build` comes from `RequiredBuildFailedException` — the agent's
  work-product left the branch non-compiling, the gate working as designed
  (a work-quality failure, **not** infra; the infra-equivalent is
  `failure_kind="infrastructure"` from `RequiredBuildVerificationUnavailableException`).
  The `other` kind is the catch-all PipelineRunner uses when it has not
  yet classified a failure; counting it as infra would over-pessimise the
  score, counting it as legitimate would under-pessimise.

### Source data

Transitions come from three persisted signals (no new tables):

1. `agent_involvement` rows (Work / Rework / Audit / Merge agent runs) with
   their finalised `outcome`. Audit-stage involvement rows are intentionally
   skipped — the audit stage is scored from audit reports so the "auditor
   ran cleanly but did not produce a result" infra failure is captured (its
   involvement row is `outcome=success`).
2. `audit_reports` rows for the Audit stage. The classifier inspects each
   row's findings to discriminate real blocking findings (LEGITIMATE) from
   the auditor failing to run (INFRA).
3. `work_items` rows with `state ∈ {Failed, MergeConflictResolutionFailed,
   AbandonedAfterRecoveryAttempts}` for terminal failures. `AuditFailed`
   is intentionally excluded — it represents the rework cap being hit
   (a work-quality outcome, not an infra failure), and the preceding
   audit_report rows are already counted by signal #2.

### Endpoint

```
GET /fleet/transition-health
```

Returns:

```jsonc
{
  "score": 0.95,
  "infraFailureRate": 0.05,
  "window": {
    "start": "2026-06-13T12:00:00+00:00",
    "end":   "2026-06-14T12:00:00+00:00",
    "durationSeconds": 86400,
    "maxTransitions": null  // null = use wall-clock window only
  },
  "totalTransitions": 412,
  "legitimateTransitions": 391,
  "infraFailureTransitions": 21,
  "worstStage": "Audit",
  "stages": [
    {
      "stage": "Work",     "score": 0.99, "total": 100,
      "legitimate": 99, "infraFailure": 1, "infraByKind": { "agent": 1 }
    },
    {
      "stage": "Rework",   "score": 0.96, "total": 50,
      "legitimate": 48, "infraFailure": 2, "infraByKind": { "agent": 2 }
    },
    {
      "stage": "Audit",    "score": 0.93, "total": 200,
      "legitimate": 186, "infraFailure": 14,
      "infraByKind": { "auditor_failed": 13, "build_unavailable": 1 }
    },
    {
      "stage": "Merge",    "score": 1.0,  "total": 60,
      "legitimate": 60, "infraFailure": 0, "infraByKind": { }
    },
    {
      "stage": "Terminal", "score": 0.0,  "total": 4,
      "legitimate": 0, "infraFailure": 4,
      "infraByKind": { "quota": 2, "merge_conflict_resolution_failed": 1, "infrastructure": 1 }
    }
  ],
  "infraByKind": {
    "agent": 3, "auditor_failed": 13, "build_unavailable": 1,
    "quota": 2, "merge_conflict_resolution_failed": 1, "infrastructure": 1
  }
}
```

`worstStage` is the stage with the most infra failures, so operators can
localise "audits dying" vs. "merges dying" vs. "work-phase agents crashing"
without parsing the full breakdown.

When the endpoint is disabled, a `404` is returned with
`{ "error": "transition-health is disabled" }`.

### Configuration

```jsonc
{
  "CodeyBox": {
    "TransitionHealth": {
      "Enabled": true,
      "WindowHours": 24,
      // Optional: cap the scored set to the most recent N transitions
      // regardless of how long ago they happened. Null = use WindowHours
      // only.
      "MaxTransitions": null
    }
  }
}
```

All three knobs are hot-reloadable — edits take effect on the next request
to `/fleet/transition-health` without a process restart.

Clamps applied at binding time:

- `WindowHours` is clamped to `[5min, 30d]`. Zero / negative is treated as
  "default 24 h".
- `MaxTransitions` is clamped to `[50, 100_000]` when set.

### For contributors

- The classifier (`TransitionHealthClassifier`) is a pure function over
  hand-authored snapshots: it takes a `TransitionDataSnapshot` and an
  `Options` and returns a `TransitionHealthReport`. The classifier has no
  DB I/O and is straightforward to unit-test (see
  `TransitionHealthClassifierTests`).
- The data source (`SqliteTransitionHealthDataSource`) opens a read-only
  SQLite connection to the state database; WAL mode lets it run alongside
  the writer stores without contention. Tables that do not yet exist (a
  fresh deployment that has not recorded an audit report) are detected via
  `sqlite_master` so the source returns empty for that signal instead of
  throwing.
- `TransitionHealthService` is the thin façade that joins the two together
  for the HTTP endpoint, reading the live `TransitionHealthOptionsSnapshot`
  on each call.
- The hot-reload wiring lives in `AgentConfigHotReload.ApplyTransitionHealthIfChanged`
  alongside the other per-block reload methods. Hot-reload emits an
  `AuditLog.ConfigReloaded("TransitionHealth", …)` entry only when the
  serialised value actually changed.
