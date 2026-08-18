# Dead-Worker Recovery

CodeyBox detects worker crashes via a heartbeat registry and automatically re-queues any work items that were in-flight when the crash occurred.

---

## Problem

If the orchestrator process is killed mid-flight (OOM, host shutdown, `SIGKILL`), any work items in a non-terminal worker-owned state (`Planning`, `PlanReview`, `Working`, `Auditing`, `Reworking`, `Merging`, `UpstreamPushing`) or a durable phase-boundary state (`PlanApproved`, `WorkComplete`, `AuditPassed`, `Merged`) may be left orphaned. On restart, recovery re-queues them at the correct resume point.

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

### Durable agent-turn recovery boundaries

A work or rework agent can fail after making useful edits but before the
orchestrator creates the normal phase commit. When the remaining failure is a
recognised quota failure, transient-network failure, infrastructure failure
carrying the sandbox provider's explicit `ExecutionUnavailable` signal, or
process exit `137` (SIGKILL/OOM), CodeyBox attempts a durable checkpoint.
Exhausting a live CLI resume budget does not by itself make another generic
failure eligible for durable re-dispatch.

The dirty source tree and private CLI state have separate persistence
boundaries. Git receives only the source commit at
`refs/heads/codeybox/preempt/<work-item-id>/<source-commit>-<archive-sha256>`.
The bounded archive and its restore manifest are captured under the private
`/run/codeybox/agent-turn` tmpfs, removed before source staging, and saved as an
immutable host-private SQLite BLOB capped at 32 MiB and keyed by the exact ref.
The ref binds the work item, source commit, and archive SHA-256; restore verifies
all three before exposing the archive to the exact route. Clearing checkpoint
metadata deletes its private archives through a SQLite trigger. Startup
reconciliation removes orphaned or no-longer-referenced BLOB rows, and a
successfully committed replacement checkpoint removes older generations.

The immutable checkpoint is preferred because it is safe to replay in a fresh
sandbox. There is one provider-specific fallback: if an Incus
`ExecutionUnavailable` incident also prevents the capture commands from
running, CodeyBox can retain the exact stopped VM that contains the dirty
`/work` tree. The provider prepares an opaque recovery token and immutable
private manifest while the VM is healthy; only their hashes are bound into the
Incus instance configuration. On failure, Incus durably records the exact
interrupted exec and returns the pre-created token without depending on the
unavailable daemon.

The work-item store publishes the retained lease and its typed turn metadata in
one lifecycle compare-and-set. That same SQL statement enforces the global
`CodeyBox:PipelineTuning:MaxRetainedAgentTurnSandboxes` cap (16 by default,
valid range 1–256), including across concurrent orchestrator processes. If the
cap or lifecycle comparison rejects publication, sandbox preservation is
disarmed and the original agent failure remains authoritative. A lease is
internal capability data and is never exposed in public work-item JSON or log
formatting.

### Retained Incus adoption and conversion

A retry of a retained boundary must use the exact recorded agent route and
lease provider. Before any VM mutation, the pipeline atomically records a
`Preparation` claim. Unlike a `Dispatched` claim, it does not increment the
agent-turn attempt count because no agent CLI is allowed to run in mutable
recovery evidence. The provider-cutover router sends the request to the
lease-named provider even if the selected backend changed after the outage.

Incus additionally takes an exclusive lock in the sandbox's private staging
tree. It validates the exact project, instance and token/manifest hashes, then
checks the creation-time sandbox specification, storage and guest identities,
network and effective device topology, inode-pinned host mount sources, and
recorded guest links. VM start is authorized again at each lifecycle sink.
Recovery uses an isolated boot with host devices detached, validates guest
paths, restores the exact devices, non-persistent tmpfs mounts and links, waits
for mount readiness, and removes the recorded exec control files. Any
uncertainty refuses recovery, keeps the lease preserved, and makes a
best-effort authoritative force-stop when a start may have occurred.

After adoption, the pipeline validates the work branch and isolated Git origin,
captures the CLI scratchpad, commits and pushes the dirty tree, and publishes
the content-bound private archive. SQLite atomically replaces the lease with
that immutable checkpoint. Only after publication does CodeyBox disarm
preservation and delete the retained VM. The item is then enqueued
automatically; its next pickup performs the ordinary immutable resumed-agent
dispatch. A failed adoption or conversion keeps the lease, releases the
preparation claim, and remains retryable after infrastructure is repaired. If
the post-conversion queue write fails, the immutable checkpoint remains paired
with an infrastructure-shaped `Failed` item for a later retry; the lease and VM
are not falsely restored.

The typed Git or retained-lease boundary stays paired with the item while it is
`Working`, `Reworking`, `WaitingForQuotaReset`, `WaitingForTransientRetry`,
`WaitingForAgentResume`, `NeedsOperatorInput`,
`AbandonedAfterRecoveryAttempts`, or an infrastructure-shaped `Failed`. The
last two states deliberately keep typed evidence available for an operator;
legacy Git-only preempt records do not gain that broader persistence. Once
quota/network/provider conditions recover, the corresponding automatic
scheduler—or a manual retry with no explicit phase—restores the original
`Working`/`Reworking` boundary. The first agent dispatch is pinned to the saved
route. That route receives the private archive through `RunResumedAsync`, and
Claude/Codex resume the exact saved session when an id was captured. A later
agent-class fallback receives only the source tree; because the archive was
never committed to Git, another route cannot recover it from the checkout,
history, or bare origin.

If a resumed invocation exits cleanly without creating a new diff, CodeyBox
accepts it only when the checkpoint already contains meaningful source changes
relative to the pre-turn work-branch tip. An allow-empty checkpoint followed by
a no-op resume uses the normal initial-work/rework no-diff failure path.

Durable re-dispatches are bounded by
`CodeyBox:PipelineTuning:AgentSessionResumeMaxAttempts`. The attempt is claimed
atomically immediately before the resume hook rather than at enqueue time, so
manual, scheduler, startup, and dead-worker paths share the same cap. If typed
resume preparation fails while restoring private state or prerequisites before
the agent CLI starts, the exact claim is released and that attempt is refunded;
an outage after CLI dispatch remains consumed. A value of `0` refuses an
agent-turn redispatch. This is independent of `DeadWorker.MaxRecoveryAttempts`
and of legacy suspend/preempt recovery. A retained-VM `Preparation` claim is
also independent: it consumes no agent attempt and is released when conversion
does not publish. Changing the prompt or explicitly choosing a different retry
phase discards an immutable checkpoint; a retained lease cannot be discarded by
selecting another phase because provider cleanup must remain authoritative. If
private archive capture/storage, source commit/push, or content verification
fails outside the bounded retained-Incus path, the original error remains
authoritative and normal phase recovery applies.

The item-stale watchdog does not release a live dispatch claim merely because
`UpdatedAt` is old. It must recovery-cancel a pipeline registered in this
process and observe that registration become inactive within
`WorkerProgressWatchdog.PostAgentTransitionTimeout`; the row is then re-read
and changed through its state/`UpdatedAt` compare-and-set. A claim owned by
another process on the same host remains untouched for confirmed
process-death/startup recovery. A claim owned by another host fails closed
until that host is externally fenced; heartbeat expiry alone cannot prove the
remote agent has stopped writing.

Once a resumed agent's resulting tree is pushed and synced to the work branch,
CodeyBox clears the older turn checkpoint before required-build and other
post-agent verification. A later verification outage therefore retries from
the published branch phase boundary instead of restoring stale pre-turn source
or provider session state.

`SandboxLeakReaper` treats every provider-scoped lease still referenced by a
work item as protected, including leases in operator and abandoned-recovery
states. When cancellation or another lifecycle transition clears the lease,
the VM is no longer protected and normal provider inventory cleanup can remove
it. This couples database authority and resource cleanup without relying on a
process-local handle.

### 4. State-mapping rules

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

---

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

---

## Observability

### Audit log events

| Event name | When |
|---|---|
| `worker.registered` | Worker slot wrote its registry row |
| `worker.deregistered` | Worker row deleted on clean exit |
| `work_item.worker_dead_recovered` | Item recovered by the reaper (below cap) |
| `work_item.worker_dead_failed_terminal` | Item hit `MaxRecoveryAttempts`; abandoned for operator triage |

### Webhook events

See [`webhooks.md`](webhooks.md#recovered-details) for the `work_item.recovered` payload.

### API

`GET /workers` lists currently-registered workers (heartbeating and stale). See [`api.md`](api.md#get-workers) for the response shape.

---

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

---

## Multi-host future

Today CodeyBox is single-host. The same registry and reaper design works unchanged in a future multi-host configuration: each host's workers write to the same shared SQLite (or a future shared store), and the reaper on any host can claim and recover orphans from dead workers on other hosts. The `worker_id` GUID and `host_name`/`process_id` fields provide full attribution for cross-host debugging.
