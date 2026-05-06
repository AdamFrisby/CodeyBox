# Work-Item Timings

Per-step wall-clock timing instrumentation for the CodeyBox orchestrator,
designed to answer "why is CodeyBox 4–5× slower than a human running the
same task?" with data instead of guesswork.

## Overview

Every significant phase of a work item's lifecycle is measured and stored in
the `work_item_timings` SQLite table.  Measurements are surfaced via two REST
endpoints and an admin dashboard.

Timing writes are **best-effort**: any failure to write a timing row is logged
at Warning level and does not affect the work item.

## Instrumented steps

| Phase | Step | Description |
|---|---|---|
| `work` | `vm.clone` | Cloning the Multipass baseline VM |
| `work` | `vm.launch` | Launching a new Multipass VM (no-baseline path) |
| `work` | `vm.mount` | Mounting project directories into the VM |
| `work` | `vm.start` | Starting the Multipass VM |
| `work` | `vm.exec_first` | First sandbox `ExecAsync` call (cloud-init catch-up) |
| `work` | `vm.dispose` | Deleting and purging the Multipass VM |
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

### Tool call counts

After each agent run, `AgentStreamJsonParser` parses the agent's stream-json
(NDJSON) stdout to extract tool call names and invocation counts.

**`agent.tool_call.<name>` rows are emitted** with `duration_ms = 0` and
`{"count": N}` in `metadata_json`, where *N* is the number of invocations of
that tool in the run. Per-event timestamps are unavailable in the buffered
stream-json format, so durations are always zero; the value these rows provide
is the invocation count surfaced in the per-item `/timings` endpoint. These rows
are classified as sub-steps (`IsSubStep`) and are excluded from phase totals and
top-step aggregates to avoid double-counting.

**`agent.thinking_aggregate`** is emitted with `duration_ms` equal to the full
`agent.exec` duration. Without per-event timestamps all tool-call durations are
zero, so thinking time equals exec time by construction. Like `agent.tool_call.*`,
this row is a sub-step and excluded from phase totals.

If the agent's stdout is not in stream-json (NDJSON) format — for example when
run without `--output-format stream-json` — `AgentStreamJsonParser.TryParse`
returns null and no tool-call rows are emitted; the pipeline falls back to the
coarse-grained `agent.exec` row only.

## Database schema

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
(`journal_mode=WAL`, `busy_timeout=5000`) for safe concurrent access.

## REST API

### GET /workitems/{id}/timings

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

### GET /workitems/timings/aggregate?n=50

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

## Admin dashboard

Two pages are available in the admin UI:

- **Work item → Timings button** (`/work-items/{id}/timings`): per-item
  breakdown by phase and step, with in-flight rows shown.
- **Aggregate Timings** (`/timings/aggregate`): system-wide median and p95
  per step across the last N completed work items, with a configurable N
  picker and Refresh button.

## Disabling timing collection

Timing collection is enabled by default when the API starts.  To disable it,
remove or comment out the `ITimingStore` registration in `Program.cs`.  All
timing code paths check for a null store and silently skip collection.

## Known limitations

- **Zero-duration tool-call rows**: `agent.tool_call.*` and
  `agent.thinking_aggregate` rows are emitted but carry `duration_ms = 0` (or
  equal to `agent.exec`) because the Claude stream-json format provides no
  per-event timestamps.  They are sub-steps excluded from totals; their
  utility is the invocation count in `metadata_json` (see §Tool call counts).
- **No cross-item streaming UI**: the aggregate page shows statistics, not
  individual rows.  A future "timings log" page could stream raw rows for
  deeper analysis.
