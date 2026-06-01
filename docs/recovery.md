# Dead-Worker Recovery

CodeyBox detects worker crashes via a heartbeat registry and automatically re-queues any work items that were in-flight when the crash occurred.

---

## Problem

If the orchestrator process is killed mid-flight (OOM, host shutdown, `SIGKILL`), any work items in a non-terminal worker-owned state (`Working`, `Auditing`, `Reworking`, `Merging`, `UpstreamPushing`) or a durable phase-boundary state (`WorkComplete`, `AuditPassed`, `Merged`) may be left orphaned. On restart, recovery re-queues them at the correct resume point.

The dead-worker reaper fixes this: workers prove they are alive every N seconds; items whose worker hasn't heartbeated in M seconds are presumed dead and transitioned back to a safe pick-up point.

---

## How it works

### 1. Worker registry

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

### 2. Heartbeat

Each active worker fires an `UPDATE worker_registry SET last_heartbeat_at = $now ...` every `HeartbeatInterval` (default **15 s**). Heartbeat failures are **fail-soft**: a transient SQLite write failure is logged at Warning level and retried on the next interval — it never crashes the worker.

### 3. Reaper sweeps

`DeadWorkerReaper` (`IHostedService`) runs a periodic sweep every `CheckInterval` (default **60 s**):

1. Compute `cutoff = now − DeadWorkerThreshold` (default **90 s**).
2. Atomically `DELETE FROM worker_registry WHERE last_heartbeat_at < cutoff` inside a single transaction (acts as a distributed lock — only the one process that deletes the row performs recovery).
3. For each deleted row whose `current_work_item_id IS NOT NULL`:
   - Look up the work item.
   - If it is in a recoverable worker-owned state (see table below), increment `RecoveryAttempts` and transition it.
   - If it is in a durable phase-boundary state, re-dispatch it without changing state or consuming a recovery attempt.
   - If `RecoveryAttempts` exceeds `MaxRecoveryAttempts` (default **2**): transition to `Failed` with `LastError = "exceeded MaxRecoveryAttempts"`.
   - Fire a `work_item.recovered` webhook event for state-changing recovery transitions.
   - Re-enqueue the item for immediate pick-up.

The reaper also runs **once synchronously at orchestrator startup** (before the worker pool begins pulling from the queue), ensuring that items orphaned by the _previous_ process crash are recovered before any new work starts.

### 4. State-mapping rules

| State when worker died | Recovered to | Why |
|---|---|---|
| `Working` | `Failed` | No committed work to preserve; explicit retry required unless a preempt checkpoint exists |
| `Reworking` | `Queued` | Re-run the work phase from scratch |
| `WorkComplete` | `WorkComplete` | Re-dispatch audit from the phase boundary without consuming a recovery attempt |
| `Auditing` | `WorkComplete` | Re-audit the same commit |
| `AuditPassed` | `AuditPassed` | Re-dispatch merge from the phase boundary without consuming a recovery attempt |
| `Merging` | `AuditPassed` | Re-attempt the merge |
| `Merged` | `Merged` | Re-dispatch upstream push/finalization from the phase boundary without consuming a recovery attempt |
| `UpstreamPushing` | `Merged` | Re-attempt the upstream push |
| Any terminal state | — (no action) | Already finished |
| `Queued` | — (no action) | Not worker-owned; safe without intervention |

---

## Configuration

All options live under `CodeyBox:DeadWorker`:

| Key | Default | Description |
|---|---|---|
| `HeartbeatInterval` | `00:00:15` | How often each worker updates its heartbeat row |
| `DeadWorkerThreshold` | `00:01:30` | Workers not seen in this window are presumed dead |
| `CheckInterval` | `00:01:00` | How often the reaper periodic sweep runs |
| `MaxRecoveryAttempts` | `2` | Cap on automatic recovery transitions before the item is failed permanently |

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

---

## Observability

### Audit log events

| Event name | When |
|---|---|
| `worker.registered` | Worker slot wrote its registry row |
| `worker.deregistered` | Worker row deleted on clean exit |
| `work_item.worker_dead_recovered` | Item recovered by the reaper (below cap) |
| `work_item.worker_dead_failed_terminal` | Item hit `MaxRecoveryAttempts`; failed permanently |

### Webhook events

See [`webhooks.md`](webhooks.md#recovered-details) for the `work_item.recovered` payload.

### API

`GET /workers` lists currently-registered workers (heartbeating and stale). See [`api.md`](api.md#get-workers) for the response shape.

---

## Idempotency

Running the reaper twice in quick succession (e.g., startup-sync + the first periodic tick) is safe:

- The `DELETE WHERE last_heartbeat_at < $cutoff` is inside a single `BEGIN IMMEDIATE` transaction — only the thread that successfully deletes a row performs recovery.
- If the reaper crashes after deleting the registry row but before writing the work-item update, the item remains in its mid-flight state. The **next restart** will not find a registry row (it was deleted), so `ReplayPendingAsync` picks up the item from its stale state and re-queues it as normal.

---

## Multi-host future

Today CodeyBox is single-host. The same registry and reaper design works unchanged in a future multi-host configuration: each host's workers write to the same shared SQLite (or a future shared store), and the reaper on any host can claim and recover orphans from dead workers on other hosts. The `worker_id` GUID and `host_name`/`process_id` fields provide full attribution for cross-host debugging.
