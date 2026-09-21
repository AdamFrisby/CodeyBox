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

## Fleet map: axis, layout, camera, stages, inbox, ghosts, releases

Pure pieces behind the `/map` screen, all in this project:

- **Time axis (`TimeAxisScale`, `QueueForecast`)** — x is time. Running
  and stuck work sits at now (x = 0); queued work sits right by *predicted
  batch* — a topological order over the dependency graph cut into batches
  by the concurrency cap, ties by queue position, agent-benched items last,
  compressed beyond `FutureNearBatches`. The past is warped by what it
  contains rather than by a curve, and warped **once per snapshot with no
  camera input**: inside a burst, spacing is elapsed time at
  `PastMinutesPerColumn`; a stretch with no landing longer than
  `QuietGapMinutes` is cut out and replaced by an `AxisBreak` of fixed
  `BreakWidth` that says what it skipped; two landings in one lane closer
  than `PastMinSpacing` are spaced to it (older pushed left, flagged
  `Spaced`) so boxes in a lane never overlap in the world. Zoom is a view
  transform and nothing else — a landed item keeps its world position for
  the life of the snapshot. What is drawn as several dots or as one counted
  cluster is the renderer's call at draw time from screen distance, the
  same rule as a dot becoming a card; clicking such a glyph zooms until its
  members sit apart. "Now" is quantised to `NowBucketMinutes`; the stretch
  since the latest landing follows the quiet rule, so a quiet fleet's past
  stops moving altogether. Positions in the future are an ordering, never
  a timestamp.
- **`EdgeRouter`** — an edge whose straight run would pass through a node
  between its endpoints arcs over it (into the gap above the row, or over
  the lane when the obstacle is on another row) and names the obstacle, so
  a crossing is deliberate, never a graze. The renderer draws every body
  with a background halo so anything passing beneath breaks cleanly, and
  places every label through one occupancy registry (free spot first,
  plated over content otherwise).
- **`FleetMapBuilder`** — dependency is the hard constraint and time the
  objective within it: an item takes its time position unless that would put
  it left of something it waits on, in which case it is pushed right of the
  blocker and flagged `PushedByDependency` — history included. The layout
  never reads the camera: zoom is a view transform,
  and nothing about where a box sits depends on it. Lanes group by chain and by
  release (a release's lanes are contiguous); rows pack by horizontal overlap
  and stay sticky where free; a chain that splits when its hub lands keeps
  its lane. Observed positions move only when the bucket steps; predicted
  ones move when the forecast changes. `PreviewDependents` refreshes the
  layout with ghost members so the "+ dependent" and suggestion ghosts sit
  exactly where promotion would put them.
- **`TerminalVisibility`** — settled work stays while something in flight
  builds on it (through other settled items) or while inside the operator's
  horizon (default 180 days — the whole list is fetched anyway, so history
  costs nothing extra to show); failures that need a decision
  never fold on their own.
- **`CameraDirector`** — eased travel, urgency holds with a minimum and a
  recency guard (`UrgentMaxAgeHours`: a week-old failure is on the rail, not
  the camera), the idle lap over active chains, `SemanticZoom` for what is in
  view at pipeline zoom. The camera reads the layout; nothing reads the
  camera. The renderer glides a box only when the data moved it (a landing,
  a forecast change, the axis bucket stepping) and rings it while it glides.
- **`ItemStagePipelineBuilder`** — the circuit `plan → work → audit ⇒ merge →
  landed`: audit is a gate whose fail path is a counted `Rework` return edge
  to work; interruptions and operator retries are their own edge kinds; the
  merge conflict circuit is a self-return on merge. `StageLoopRouter` tiers
  the return edges so arcs and labels never collide.
- **`AttentionRail`** — the inbox: items needing a person, stacked by node
  position; `AssignChannels` routes elbow leaders with the fewest crossings
  from real screen positions (greedy insertion; the renderer mirrors it).
  `DecisionBriefBuilder` carries the decision onto the bubble — findings by
  auditor and severity, the last error, the open question — and says out loud
  when there are no findings, because park text is not evidence.
- **`MapItemActions`** — what an item admits: add dependent, retry from
  work/audit, delegate, answer, cancel; destructive ones carry `Confirm`.
- **`SuggestionGhosts`** — open suggestions as provisional boxes beside the
  item that produced them: only where that parent is on the map, most severe
  first, three per parent with the rest folded; dismissed and promoted ones
  are never ghosts.
- **`ReleaseContainers`** — a release as the frame around its members'
  bounds with name, state and done/total/blocking counts; empty or shipped
  releases fold; remediation items are linked from the frame.

| Input | Existing surface | Notes |
|---|---|---|
| Finish time | `GET /workitems` `updatedAt` of settled items | The list has no explicit `completedAt`; `updatedAt` is the last change, which for a Done item is the landing (an upstream-push retry could nudge it). |
| Capacity / running | `GET /concurrency` | Feeds the forecast's batch width; defaults to `DefaultCapacity` when absent. |
| Queue position | `GET /workitems` `queuePosition` | Tie-break inside a forecast wave. |
| Release membership | `GET /workitems` `releaseId`; `GET /releases`; `GET /releases/{id}/audit-iterations` | `releaseId` is on the list read model (it was not declared on the DTO before). |
| Suggestions | `GET /suggestions?state=open&limit=500` | Added as `GetOpenSuggestionsAsync`; the API pages at 200 by default. |

## Gaps (genuinely not derivable — reported, not speculated into existence)

- **Per-stage durations on the map.** `GET /workitems/{id}/timings` exists
  but is not fetched for the map's open item (it would be a fourth per-item
  call on every open); the journey page shows them. The stage pipeline
  therefore draws visits and loops, not wall-clock.
- **Plan stage evidence.** Items filed without a planning phase never enter
  `Planning`; the stage renders "not reached" — which is true, not a gap —
  but the list response carries no flag saying whether planning was
  *configured*, so "skipped" and "not applicable" look the same.

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
