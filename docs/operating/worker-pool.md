# Worker pool and queue control

The orchestrator is a .NET `BackgroundService` that drives work items through
the pipeline, running several at once under a set of admission limits. This
page covers sizing those limits, detecting a hung agent, and pausing dispatch —
globally, per project, or per agent.

## Worker pool sizing

Three independent knobs control worker admission and sandbox pressure:

| Config key | Type | Default | Purpose |
|---|---|---|---|
| `CodeyBox:WorkerPool:MaxConcurrentWorkers` | `int` | `1` | Hard cap on simultaneous in-flight work items |
| `CodeyBox:WorkerPool:MaxConcurrentSandboxes` | `int` | `ceil(MaxConcurrentWorkers * 1.5)` | Global cap on live sandboxes/VMs across every phase |
| `CodeyBox:WorkerPool:MinSpawnInterval` | `string` (TimeSpan) | `"00:00:00"` (none) | Minimum wall-clock gap between consecutive spawns |

### MaxConcurrentWorkers

Controls how many work items run their full pipeline at the same time.
The orchestrator uses a `SemaphoreSlim` of this size: once the cap is reached
the dispatch loop blocks on the semaphore until a slot is released by a
finishing worker.

Set this to match how many independent pipeline items should make progress at
once. It is not a VM ceiling: one item can create additional sandboxes during
audit, merge/rebase, required-build verification, smoke checks, or security
review.

### MaxConcurrentSandboxes

Controls the total number of live sandboxes admitted by the process. The API
wraps the selected `ISandboxProvider` with a single admission gate, so every
`CreateAsync` call path shares this budget. A token is acquired before provider
provisioning starts and released on the first disposal of the returned sandbox
handle. The provider therefore never has more than this many concurrently-live
sandboxes from this orchestrator process, even when several worker items enter audit or
merge at the same time.

When unset, the default is `ceil(MaxConcurrentWorkers * 1.5)`: enough headroom
for routine audit/merge overlap without making `MaxConcurrentWorkers` and
per-item audit fan-out multiply into the host VM count. Set it explicitly on
hosts with a known VM capacity. This is a startup-captured value because the
live admission queue is not resized in place; restart CodeyBox to apply changes.

`MaxLlmAuditorParallelism` remains a per-project audit policy. It bounds how
many LLM auditors one item may try to run concurrently, but those auditor
sandbox creates still queue behind `MaxConcurrentSandboxes`. The effective VM
ceiling is:

```
live sandboxes <= CodeyBox:WorkerPool:MaxConcurrentSandboxes
```

not:

```
MaxConcurrentWorkers * MaxLlmAuditorParallelism
```

#### Deadlock safety

Worker, audit, merge, smoke, and verification phases use `await using` sandbox
handles, so a phase releases its token when that sandbox is disposed before the
next phase fans out. Auditor creation is cancellable and queued FIFO at the
provider boundary. If `MaxConcurrentSandboxes` is smaller than
`MaxConcurrentWorkers * MaxLlmAuditorParallelism`, excess auditors wait without
holding tokens; as active auditors finish and dispose their sandboxes, the gate
admits the next queued create. No worker keeps its work-phase sandbox token
while waiting for audit tokens, so the queue can drain rather than forming a
permanent cycle.

### MinSpawnInterval

Enforces a minimum wall-clock delay between two successive worker spawns.
This is unrelated to how long items take to run — a worker that *finishes*
does not reset or consume the interval; only new *spawns* are paced.

Use this when several work items share a rate-limited API quota (e.g. Claude
Pro or Codex Plus subscription tokens). Firing all workers at once saturates
the quota immediately; pacing them spreads the API calls out over time so
each agent actually gets useful work done before hitting a limit.

The value is a .NET `TimeSpan` string, e.g. `"00:00:30"` for 30 seconds.

### How spawning works

```
t=0s   spawn worker 1  ─────────┐
t=10s  spawn worker 2  ───────┐ │  (MinSpawnInterval=10s, MaxConcurrent=4)
t=20s  spawn worker 3  ─────┐ │ │
t=30s  spawn worker 4  ───┐ │ │ │
                          │ │ │ │
                          ▼ ▼ ▼ ▼  (workers finish at varying times)
t=35s  worker 3 done → spawn worker 5 at max(t=35s, t=40s) = t=40s
```

The pacing floor and the concurrency cap interact: if `MaxConcurrentWorkers=1`
and `MinSpawnInterval=30s`, at most one item runs at a time and the next item
waits at least 30 s *after the previous spawn* (not after it finishes).

### Worked examples

**Single solo developer**
```json
"WorkerPool": {
  "MaxConcurrentWorkers": 1,
  "MaxConcurrentSandboxes": 2,
  "MinSpawnInterval": "00:00:00"
}
```
One item at a time, no pacing. Simplest setup for a single-user instance.

**Small team, isolated projects**
```json
"WorkerPool": {
  "MaxConcurrentWorkers": 4,
  "MaxConcurrentSandboxes": 6,
  "MinSpawnInterval": "00:00:00"
}
```
Up to four items run concurrently, with six total sandboxes/VMs admitted across
work, audit, and merge. Suitable when each project uses its own API key and
there is no shared quota.

**Shared Claude Pro / Codex Plus quota**
```json
"WorkerPool": {
  "MaxConcurrentWorkers": 4,
  "MaxConcurrentSandboxes": 6,
  "MinSpawnInterval": "00:00:30"
}
```
Up to four workers, but each new spawn is at least 30 s after the last.
This gives each agent a head-start before the next one calls the API,
reducing the chance of hitting the subscription rate limit on the first
request of every session.

## The older `Concurrency` key

```json
"CodeyBox": { "Concurrency": 4 }
```

is still read. Resolution order is `WorkerPool:MaxConcurrentWorkers`, then
`Concurrency`, then `1`, so an existing deployment keeps working untouched —
but every other pool knob lives under `WorkerPool`, so set it there.

## Stuck-agent detection

The orchestrator monitors each running agent for liveness. If an agent stops
consuming CPU **and** has no open network sockets for a configurable period, it
is classified as _stuck_ (deadlocked, blocked on a closed socket, blocked on
stdin, etc.) and killed so the slot can be recovered.

### How it works

While a work, rework, or merge phase is running, a background probe samples
agent activity every 30 seconds:

| Dimension | What is measured | "Active" if … |
|---|---|---|
| CPU | `utime + stime` from `/proc/<pid>/stat` | tick counter increased between two samples |
| Network | Open socket file-descriptors in `/proc/<pid>/fd` | at least one socket FD exists |

The agent is classified **stuck** when both dimensions show zero activity for
`StuckThresholdMinutes` consecutive minutes (default 10 min = 20 samples ×
30 s). Once stuck is detected:

1. Audit event `agent.stuck_detected` is logged.
2. The phase's `CancellationToken` is cancelled, causing the sandbox to kill
   the agent process.
3. Audit event `agent.killed_by_stuck_probe` is logged.
4. Webhook event `work_item.agent_stuck` is fired (see
   [webhooks.md](../reference/webhooks.md)).
5. The work item transitions to **Failed** (or is re-queued if
   `AutoRetryOnStuck` is enabled — see [projects.md](../concepts/projects.md)).

### Platform support

| Provider | CPU probe | Network probe |
|---|---|---|
| **ProcessSandbox** (dev) | ✓ | ✓ (host processes, host netns) |
| **Bubblewrap** (production) | ✓ | ✓ (host-visible PIDs, per-process netns) |
| **Multipass** (production KVM) | ✗ — agent runs inside VM | ✗ |

On Multipass the agent process is not visible from the host `/proc` filesystem.
The probe's `TryRead()` returns `null` every sample, so no stuck classification
occurs. The existing coarse phase timeout remains the only protection in that
configuration.

On non-Linux hosts (macOS, Windows) the probe is also silently disabled (null
source).

### Threshold tuning

**Default: 10 minutes (20 × 30 s samples).**

Choosing a threshold involves two failure modes:

| Too low | Too high |
|---|---|
| Kills agents that are just slow (large LLM response streaming, big `git clone`) | Wastes compute and human time on a genuinely deadlocked agent |

Rules of thumb:
- Set to **at most half** of your phase timeout. If `WorkTimeout = 30 min`, a
  threshold ≤ 15 min ensures the probe fires before the timeout.
- For phases that routinely produce large diffs or audit many files, start with
  the default (10 min) and only reduce if you're confident your slowest
  legitimate run completes within that window.
- Set `StuckThresholdMinutes = 0` to **disable** the probe entirely for a
  project (e.g. when you want to investigate hangs manually via the VM console
  rather than auto-recovering).

### Observability

| Audit event | Logged when |
|---|---|
| `agent.stuck_detected` | Probe classified the agent as stuck (includes phase, agent kind, stuck duration in seconds) |
| `agent.killed_by_stuck_probe` | Phase CTS was cancelled and the agent was killed |

Both events are at **Warning** level. Per-sample CPU-delta and socket-count
readings are logged at **Debug** level (30 s × multi-hour runs → very noisy at
higher levels).

## Queue control

Operators can pause and resume the global pickup queue without restarting the
orchestrator. A **paused** queue blocks all new work-item pickup; in-flight
items continue normally.

### State machine

```
Running ──(POST /queue/pause)──► Paused
Paused  ──(POST /queue/resume)──► Running
```

State is persisted to the same SQLite database as work items (`queue_state`
table, single row). A restart with the queue in `Paused` state logs a
**Warning** audit event (`queue.started_while_paused`) so operators don't
forget they left it paused.

### When to use it

- **Incident response**: a project is generating runaway items. Pause the
  queue, cancel the offending items via the API, then resume.
- **Maintenance window**: pause before a dependency upgrade, resume after.
- **Investigations**: stop the dispatch loop to inspect state before it changes.

### Behaviour during pause

| What | Behaviour |
|---|---|
| New item pickup | **Blocked** — items stay in the Queued state |
| In-flight workers | **Unchanged** — run to completion normally |
| Webhook delivery | **Unchanged** — audit logging and webhooks continue |
| State persistence | **Yes** — survives a restart |

Pausing is **not** the same as cancelling. Items blocked by the pause gate
remain Queued and are picked up automatically on resume.

### API

```
GET  /queue/status          → { state, pausedAt, pausedReason, refactorGates }
POST /queue/pause           body: { "reason": "..." }  → { state, pausedAt }
POST /queue/resume          → { state }
```

Operators must supply a non-empty reason when pausing. The reason is stored
in the audit log and shown in the admin dashboard banner.

`refactorGates` lists project-scoped refactor drains and locks. A
`draining` entry means a queued refactor has reached its normal dispatch turn
for that project and is holding all fresh same-project non-refactor starts,
including later higher-priority items, while existing in-flight work completes.
A `locked` entry means the refactor itself is in flight.

### Webhook events

| Event | Payload |
|---|---|
| `queue.paused` | `{ pausedAt, reason, pausedBy }` |
| `queue.resumed` | `{ resumedAt }` |

### Admin dashboard

The queue index page shows a coloured banner at the top:

- **Paused (red)**: "QUEUE PAUSED — reason: … — paused at …" + **Resume
  queue** button.
- **Running (green)**: subtle dot + **Pause queue** button (opens a modal
  asking for a reason).

## Per-agent pause

Operators can pause and resume one agent kind without stopping the whole
queue. The pause is a pickup gate only: in-flight runs are not killed, and all
other agents continue dispatching normally.
When an agent class pools multiple subscriptions for one kind, pause the
specific route key (`claude/acct-a`) to leave its siblings dispatching.

### API and CLI

```
GET  /agents/paused
POST /agents/{kind}/pause   body: { "reason": "...", "durationSeconds": 21600 }
POST /agents/{kind}/resume  body: { "reason": "..." }
POST /agents/{kind}/instances/{instanceId}/pause
POST /agents/{kind}/instances/{instanceId}/resume

codeybox agents pause claude --reason "reserve quota" --for 6h
codeybox agents pause claude/acct-a --reason "account flagged today"
codeybox agents resume claude
codeybox agents paused
```

The paused set is persisted in SQLite and survives orchestrator restart. A
pause can be indefinite or have an expiry; expired pauses auto-resume on the
next pause-state read.

### Routing behaviour

| What | Behaviour |
|---|---|
| New work/rework/audit/merge dispatch | Paused agent is excluded from eligible candidates |
| One pooled instance is paused | That route key is excluded; same-kind siblings remain eligible |
| Only eligible agent is paused | Item parks at `WaitingForAgentResume` and resumes automatically on unpause |
| In-flight run on paused agent | Continues normally |
| `/quota` and dashboard | Show paused status separately from quota exhaustion |
| Queued control work item | `agentControl` work items bypass agent routing so they can resume a paused agent |

The audit log records `agent.paused`, `agent.resumed`,
`agent.pause_dispatch_deferred`, and
`agent.pause_waiting_item_resumed` entries for operator traceability.

## Observability

### Audit log events

| Event name | Logged when |
|---|---|
| `worker_pool.spawn_throttled` | A spawn waited non-zero time due to `MinSpawnInterval` (includes actual wait ms) |
| `worker_pool.worker_started` | A worker task starts (worker index + work item ID) |
| `worker_pool.worker_finished` | A worker task completes (worker index + work item ID) |
| `queue.paused` | Operator paused the queue (includes reason) |
| `queue.resumed` | Operator resumed the queue |
| `queue.started_while_paused` | Orchestrator started with queue already paused |
| `budget.deferred` | Work item deferred by a per-project budget cap |

### Status endpoint

```
GET /workers/status
```

Returns a JSON snapshot of the pool:

```json
{
  "maxConcurrent": 4,
  "currentlyRunning": 2,
  "queuedCount": 3,
  "lastSpawnAt": "2026-04-30T12:34:56.789+00:00"
}
```

`lastSpawnAt` is `null` if no worker has been spawned since startup.
