# Transition-health metric

The transition-health metric reports infrastructure health of the pipeline
independently of work throughput. Done-rate conflates plumbing health with
hard-vs-easy work mix, quota state, and concurrency throttling: a healthy
system can have zero completions for hours (hard work + low concurrency) and
a degrading system can keep completing while its infra-failure rate climbs.
This metric isolates plumbing.

It is computed from data the orchestrator already persists; no new
instrumentation was added.

## What it measures

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

## Legitimate vs. infra-failure taxonomy

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
  `agent_unavailable`, `build`, `infrastructure`, `configuration`}.
- Terminal `MergeConflictResolutionFailed` (always classified as infra).
- Terminal `AbandonedAfterRecoveryAttempts` — the recovery loop gave up
  after `MaxRecoveryAttempts` host-shutdown / worker-died cycles. This is
  the canonical worker-died-without-preempt-checkpoint signature.

A transition is **SKIPPED** (counted in neither numerator nor denominator)
when it neither represents healthy progress nor an infra failure:

- Operator-driven cancellation (`failure:cancelled` / `cancelled`).
- An involvement row that is still in flight (`outcome` not yet finalised).
- Terminal `Failed` with `failure_kind` ∈ {`cancelled`, `other`, null}. The
  `other` kind is the catch-all PipelineRunner uses when it has not yet
  classified a failure; counting it as infra would over-pessimise the score,
  counting it as legitimate would under-pessimise. Documented and excluded.

## Source data

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

## Endpoint

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

## Configuration

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

## Architectural notes for contributors

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
