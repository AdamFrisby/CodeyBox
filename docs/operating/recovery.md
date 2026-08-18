# Crash and restart recovery

CodeyBox detects worker crashes via a heartbeat registry and automatically re-queues any work items that were in-flight when the crash occurred.

## What can be orphaned

If the orchestrator process is killed mid-flight (OOM, host shutdown, `SIGKILL`), any work items in a non-terminal worker-owned state (`Planning`, `PlanReview`, `Working`, `Auditing`, `Reworking`, `Merging`, `UpstreamPushing`) or a durable phase-boundary state (`PlanApproved`, `WorkComplete`, `AuditPassed`, `Merged`) may be left orphaned. On restart, recovery re-queues them at the correct resume point.

The dead-worker reaper fixes this: workers prove they are alive every N seconds; items whose worker hasn't heartbeated in M seconds are presumed dead and transitioned back to a safe pick-up point.

## How it works

### The worker registry

Each worker slot writes a row to `worker_registry` on startup:

| Column | Description |
|---|---|
| `worker_id` | New GUID on every orchestrator start |
| `host_name` | `Environment.MachineName` |
| `process_id` | `Environment.ProcessId` |
| `started_at` | ISO-8601 timestamp |
| `last_heartbeat_at` | Updated every `HeartbeatInterval` |
| `current_work_item_id` | Set when the worker picks up an item; row is deleted on completion |

On **clean shutdown** the row is deleted. On **crash** (OOM, SIGKILL, panic) the row stays stale; the reaper cleans it up.

### Heartbeats

Each active worker fires an `UPDATE worker_registry SET last_heartbeat_at = $now ...` every `HeartbeatInterval` (default **15 s**). Heartbeat failures are **fail-soft**: a transient SQLite write failure is logged at Warning level and retried on the next interval — it never crashes the worker.

### Reaper sweeps

`DeadWorkerReaper` (`IHostedService`) runs a periodic sweep every `CheckInterval` (default **60 s**):

1. Compute `cutoff = now − DeadWorkerThreshold` (default **90 s**).
2. List stale candidates without deleting them. If a candidate owns a claimed
   durable agent-turn checkpoint, first fence that exact owner: a current local
   pipeline is recovery-cancelled and awaited to quiescence; a demonstrably dead
   local process can be reclaimed; a remote or still-live owner fails closed and
   keeps its registry row.
3. Atomically claim each still-stale row with
   `TryClaimDeadWorkerAsync`. Only the reaper that deletes that exact row may
   normally recover it; after a local owner was fenced, the guarded work-item
   state/timestamp write remains the election if the worker deregistered while
   quiescing.
4. For each claimed row whose `current_work_item_id IS NOT NULL`:
   - Look up the work item.
   - If it is in a recoverable worker-owned state (see table below), increment `RecoveryAttempts` and transition it.
   - If it is in a durable phase-boundary state, re-dispatch it without changing state, still consuming a recovery attempt.
   - If `RecoveryAttempts` exceeds `MaxRecoveryAttempts` (default **10**): transition to `AbandonedAfterRecoveryAttempts` with `LastError = "exceeded MaxRecoveryAttempts"`.
   - Fire a `work_item.recovered` webhook event for recovery handoffs, including same-state phase-boundary redispatches.
   - Re-enqueue the item for immediate pick-up.

The reaper also runs **once synchronously at orchestrator startup** (before the worker pool begins pulling from the queue), ensuring that items orphaned by the _previous_ process crash are recovered before any new work starts.

### Durable agent-turn checkpoints

A work or rework agent can die after making useful edits but before the
orchestrator creates the phase commit. For a bounded set of failures — a
recognised quota failure, a transient network failure, an infrastructure
failure carrying the provider's explicit `ExecutionUnavailable` signal, or exit
`137` (SIGKILL/OOM) — CodeyBox captures the dirty tree and the agent's private
CLI state so the *exact* turn can resume rather than restart.

The mechanics, the retained-VM fallback for Incus, and the attempt caps are in
[`agent-turn-checkpoints.md`](agent-turn-checkpoints.md).

### Where each state resumes

| State when worker died | Recovered to | Why |
|---|---|---|
| `Working` | `Working` with a typed Git or retained-sandbox recovery boundary; otherwise `Failed` | Valid recovery evidence preserves the interrupted turn for bounded resume. Without it there is no durable mid-turn evidence, so explicit retry is required. |
| `Planning` | `Queued` | Planning edits are discarded; rerun the planning-only turn from a clean sandbox |
| `PlanReview` | `PlanReview` | A plan artifact already exists; rerun the auditor-backed plan-review loop, including plan rework if reviewers still block |
| `PlanApproved` | `PlanApproved` | Re-dispatch implementation from the approved-plan boundary and count the recovery handoff |
| `Reworking` | `Reworking` with a typed Git or retained-sandbox recovery boundary; otherwise `WorkComplete` | Resume the exact interrupted rework when durable evidence exists; otherwise re-run audit against the last published work branch. |
| `WorkComplete` | `WorkComplete` | Re-dispatch audit from the phase boundary and count the recovery handoff |
| `Auditing` | `WorkComplete` | Re-audit the same commit |
| `AuditPassed` | `AuditPassed` | Re-dispatch merge from the phase boundary and count the recovery handoff |
| `Merging` | `AuditPassed` | Re-attempt the merge |
| `Merged` | `Merged` | Re-dispatch upstream push/finalization from the phase boundary and count the recovery handoff |
| `UpstreamPushing` | `Merged` | Re-attempt the upstream push |
| Any terminal state | — (no action) | Already finished |
| `Queued` | — (no action) | Not worker-owned; safe without intervention |

## Configuration

All options live under `CodeyBox:DeadWorker`:

| Key | Default | Description |
|---|---|---|
| `HeartbeatInterval` | `00:00:15` | How often each worker updates its heartbeat row |
| `DeadWorkerThreshold` | `00:01:30` | Workers not seen in this window are presumed dead |
| `CheckInterval` | `00:01:00` | How often the reaper periodic sweep runs |
| `MaxRecoveryAttempts` | `10` | Cap on automatic recovery transitions before the item is abandoned for operator triage |

**Constraint**: `DeadWorkerThreshold` must be ≥ 3 × `HeartbeatInterval`. Startup validation throws if the constraint is violated.

### Example (`appsettings.json`)

```json
{
  "CodeyBox": {
    "DeadWorker": {
      "HeartbeatInterval": "00:00:15",
      "DeadWorkerThreshold": "00:01:30",
      "CheckInterval": "00:01:00",
      "MaxRecoveryAttempts": 2
    }
  }
}
```

## Observability

### Audit log events

| Event name | When |
|---|---|
| `worker.registered` | Worker slot wrote its registry row |
| `worker.deregistered` | Worker row deleted on clean exit |
| `work_item.worker_dead_recovered` | Item recovered by the reaper (below cap) |
| `work_item.worker_dead_failed_terminal` | Item hit `MaxRecoveryAttempts`; abandoned for operator triage |

### Webhook events

See [`../reference/webhooks.md`](../reference/webhooks.md#recovered-details) for the `work_item.recovered` payload.

### API

`GET /workers` lists currently-registered workers (heartbeating and stale). See [`../reference/api.md`](../reference/api.md#get-workers) for the response shape.

## Idempotency

Running the reaper twice in quick succession (e.g., startup-sync + the first periodic tick) is safe:

- Each candidate is re-checked and deleted by `TryClaimDeadWorkerAsync` inside a
  transaction. A concurrent heartbeat or another successful claimant makes the
  claim return no row, so recovery is skipped.
- A claimed durable dispatch is never orphaned by deleting its worker row first:
  unfenceable remote/live owners keep the row; a current local owner is cancelled
  and awaited before the row claim. Recovery then uses an exact work-item
  state/`UpdatedAt` comparison so concurrent lifecycle progress wins.
- If the reaper crashes after claiming the registry row but before writing the
  work-item update, the item remains mid-flight. The startup stranded-item sweep
  sees that no worker owns it and replays the same bounded recovery policy.

## Multi-host

Today CodeyBox is single-host. The same registry and reaper design works unchanged in a future multi-host configuration: each host's workers write to the same shared SQLite (or a future shared store), and the reaper on any host can claim and recover orphans from dead workers on other hosts. The `worker_id` GUID and `host_name`/`process_id` fields provide full attribution for cross-host debugging.

## Restarting the process

CodeyBox is a single ASP.NET process owning one port — in production
`http://127.0.0.1:5000` by default (`UseUrls` in `src/CodeyBox.Api/Program.cs`,
used when `ASPNETCORE_URLS`, `urls`, and `Kestrel:Endpoints:Default` are all
unset); the `dotnet run` profile uses `5036`. A binary swap or operator restart
therefore refuses TCP connections for a short window. There is **no blue/green
port handover, by choice**: the design accepts the gap and expects callers to
retry.

| Phase | Typical duration | What a caller sees |
|---|---|---|
| Old process draining | up to `CodeyBox:Shutdown:GraceSeconds` (default 60 s) | still bound and serving until the listener stops |
| Port unbound → new process listening | 5–30 s | `connection refused` |
| Warm-up | < 1 s | first request takes a cold path |

The drain figure is a worker-drain ceiling, not a fixed wait — a shutdown with
nothing to tear down returns in seconds. With a suspend-capable provider the
ceiling rises to the RAM-scaled suspend budget (about 30 minutes for the default
12 GiB VM), because `Shutdown:SandboxTeardownMode` can be hot-reloaded to
`Suspend` immediately before shutdown. The HTTP listener stops accepting almost
immediately on SIGTERM, so plan for **up to 30 seconds of refused connections**.

What that means for each caller:

- **Pollers** (anything on a timer reading `/workitems`, `/quota`, …) recover on
  their next tick, since the queries are read-only and recompute from live
  state. Keep the interval under 5 minutes and send an `Idempotency-Key` header
  on mutating calls — `IdempotencyMiddleware` caches the response for
  `(method, path, body)` for 24 hours, so a retry after the server already
  applied the change returns the cached `2xx` instead of applying it twice.
- **Outbound webhooks** are unaffected by a CodeyBox restart, and tolerate a
  receiver restart: `HttpWebhookDispatcher` retries `MaxAttempts` times with a
  backoff that doubles from `InitialBackoffSeconds`. The defaults (`3`, `1 s`)
  put attempts at t+0, t+1, t+3. Because the tail grows geometrically —
  `MaxAttempts` of 4, 5, 6, 7 ends at t+7, t+15, t+31, t+63 — set **≥ 6** if the
  receiver itself may take 30 s to restart. Delivery runs on a background
  channel, never blocking the pipeline, and drains for up to 30 s on shutdown.
- **GitHub release webhooks** are retried by GitHub up to 8 times over ~3.5
  days, so a 30-second window costs at most a delayed delivery.
- **Nothing inside CodeyBox calls its own API.** The orchestrator, audit, merge
  and push paths use in-process services.

**Known gap.** The changelog webhook handler creates its work item with a fresh
id and does not key on `X-GitHub-Delivery` or the release tag. A delivery that
the server fully processed but could not acknowledge — a crash after
`CreateAsync`, before the `202` reaches GitHub — produces a duplicate changelog
work item when GitHub retries. A refused connection does not trigger this,
since the handler never runs.

To rehearse the window: `kill -SIGTERM` the API, wait for the port to free,
leave it down for 30 s, start it again, and check that work-item count is
unchanged, that your poller's next tick succeeds, and that GitHub's "Recent
deliveries" panel shows a successful retry. The new process logs its recovery
banner as the reaper resets in-flight items to their safe restart point.
