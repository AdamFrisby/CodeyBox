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
