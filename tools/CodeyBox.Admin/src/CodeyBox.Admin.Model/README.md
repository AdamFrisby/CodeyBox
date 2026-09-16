# CodeyBox.Admin.Model

A pure, testable projection layer that turns the orchestrator's existing
surfaces into the shapes the admin screens need. A function of
`(items, agents, workers, quota, now)`: no I/O, no cache, no clock of its own.
The same inputs always produce the same screen.

Entry point: `FleetProjectionBuilder.Project(snapshot, options)` →
`FleetProjection { Chains, Activities, Vitals, Attention, ChainAttention }`.

## Where each input comes from (all existing endpoints)

| Model input | Existing surface | Notes |
|---|---|---|
| `AdminWorkItem` (id, title, state, agent, timestamps, `DependsOn`) | `GET /workitems` | Field-for-field from the list response. |
| `DependsOnSatisfied` bit | `GET /workitems` (`dependsOnSatisfied`) | Fallback only, for dep ids outside the snapshot (e.g. a filtered view). Ids in view are re-evaluated from their states. |
| `AdminAgentStatus.Paused` | `GET /agents/paused` | |
| `AdminAgentStatus.Available` | In-VM smoke / fast-fail state (fleet + supervision surfaces) | Null (unknown) never counts as unavailable. |
| `AdminAgentStatus.QuotaAvailablePct` / `ResetAt` | `GET /quota`, `GET /stats/capacity` | `IsKnown == false` never counts as exhausted. |
| `AdminWorkerCapacity` | `GET /workers/status`, `GET /concurrency` | Global and per-agent caps. |
| `VitalHistories` | Retained by the admin from its own polling loop | One value per projection; the model never fetches. `GET /quota/history` and capacity intervals are alternative history feeds a later screen may adopt. |
| `FleetSnapshot.Now` | Caller-supplied (injected clock) | Tests pass a fixed instant. |

No new API endpoints were added for this package — everything above is
derivable from what already exists.

## Work-item journey (`WorkItemJourneyBuilder`)

`Build(JourneySnapshot)` projects one item's recorded history into its
journey graph: visited phase nodes with loop counts, the audit↔rework cycle
with per-iteration recurring/new/resolved finding ids and a convergence
verdict (`Converging`, `Cycling`, `Stuck`, `Converged`, `AtBudget`,
`NotYetAuditing`), the configured audit budget with remaining attempts,
conflict-rework and upstream-push loops, infra interruptions listed
separately, and the current phase plus wait. Finding-id comparison is exact
ordinal match — never substring.

| Journey input | Existing surface | Notes |
|---|---|---|
| Item state, budget override, retry counters, waiting-for timers | `GET /workitems/{id}` | `AuditMaxIterations`, `ConflictReworkAttempts`, `UpstreamPushAttempts`, `NextQuotaRetryAt`, … |
| Project default budget | `GET /projects` (`AuditMaxIterations`) | Falls back to the latest audit row's `MaxIterations` when absent. |
| Per-iteration verdicts + stable finding ids | `GET /workitems/{id}/audit-progress` | Rows with a non-`complete` status are infra interruptions, never rework iterations. Only the latest work-attempt partition counts toward the budget. |
| Who ran each phase | `GET /workitems/{id}/agent-history` | Involvement trail; `failure:infrastructure` runs are also listed as infra interruptions. |
| Per-phase durations | `GET /workitems/{id}/timings` (byPhase `durationMs`) | Caller-summed per phase. |
| Per-phase sandbox evidence | `GET /workitems/{id}/agent-streams` (file list) | A phase renders a sandbox link only when a retained file exists for it. |
| Diff presence | `GET /workitems/{id}/diff` (204 = none) | |

## Gaps (genuinely not derivable — reported, not speculated into existence)

- **Per-item "blocked by quota" before first pickup.** A `Queued` item whose
  agent is quota-exhausted is reported as `BlockedByAgentAvailability` from the
  fleet quota surface, but the exact per-item reset timer
  (`NextQuotaRetryAt`) is only exposed on the single-item response, not the
  list. A queue screen showing per-row countdowns would need that field added
  to the list response — deliberately not added here.
- **Historical vital series from the server.** History is admin-retained
  (above). If the fleet ever needs server-side history, `GET /quota/history`
  covers quota only; queue-depth/in-flight history has no endpoint. Again,
  not added: retention policy belongs to a later work item.
- **Per-iteration diffs.** Audit rows record only the work-branch tip SHA;
  reconstructing the diff *at* an earlier iteration needs git history access
  the admin does not have. The journey links to the current diff only.
- **Live sandbox deep-links.** Sandboxes are ephemeral VMs with no operator
  URL; the journey links to the retained per-phase agent-stream capture
  (`GET /workitems/{id}/agent-streams/{fileName}`) instead.
- **Per-item failure-event history.** `GET /workitems/failure-events` is a
  global feed with no per-item query; the journey derives infra context from
  audit-progress verdicts, involvement outcomes, and the item's current
  `FailureKind`. A per-item query param would close this without a new
  endpoint.
