# Orchestrator

The orchestrator is a .NET `BackgroundService` that drives work items through
the pipeline. It maintains a **worker pool** — a set of concurrent pipeline
executions bounded by configurable limits.

## Worker pool sizing

Two independent knobs control the pool:

| Config key | Type | Default | Purpose |
|---|---|---|---|
| `CodeyBox:WorkerPool:MaxConcurrentWorkers` | `int` | `1` | Hard cap on simultaneous in-flight work items |
| `CodeyBox:WorkerPool:MinSpawnInterval` | `string` (TimeSpan) | `"00:00:00"` (none) | Minimum wall-clock gap between consecutive spawns |

### MaxConcurrentWorkers

Controls how many work items run their full pipeline at the same time.
The orchestrator uses a `SemaphoreSlim` of this size: once the cap is reached
the dispatch loop blocks on the semaphore until a slot is released by a
finishing worker.

Set this to match the host's available compute. Each in-flight item may hold
one running VM (Multipass) or one sandboxed process (Bubblewrap), so RAM and
CPU are the binding constraints.

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
"WorkerPool": { "MaxConcurrentWorkers": 1, "MinSpawnInterval": "00:00:00" }
```
One item at a time, no pacing. Simplest setup for a single-user instance.

**Small team, isolated projects**
```json
"WorkerPool": { "MaxConcurrentWorkers": 4, "MinSpawnInterval": "00:00:00" }
```
Up to four items run concurrently. Suitable when each project uses its own
API key and there is no shared quota.

**Shared Claude Pro / Codex Plus quota**
```json
"WorkerPool": { "MaxConcurrentWorkers": 4, "MinSpawnInterval": "00:00:30" }
```
Up to four workers, but each new spawn is at least 30 s after the last.
This gives each agent a head-start before the next one calls the API,
reducing the chance of hitting the subscription rate limit on the first
request of every session.

## Legacy config key

Prior to the worker pool overhaul, concurrency was set via:

```json
"CodeyBox": { "Concurrency": 4 }
```

This key is still recognised for backward compatibility. At startup the
orchestrator logs a deprecation warning and copies the value into
`MaxConcurrentWorkers`. No config file edits are required to keep an
existing deployment working, but migrating to `WorkerPool` is encouraged.

## Observability

### Audit log events

| Event name | Logged when |
|---|---|
| `worker_pool.spawn_throttled` | A spawn waited non-zero time due to `MinSpawnInterval` (includes actual wait ms) |
| `worker_pool.worker_started` | A worker task starts (worker index + work item ID) |
| `worker_pool.worker_finished` | A worker task completes (worker index + work item ID) |

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
