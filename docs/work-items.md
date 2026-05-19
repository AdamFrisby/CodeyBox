# Work Items and Dependencies

## Overview

A *work item* is the unit of work CodeyBox submits to an agent. Each item belongs to a project, carries a natural-language prompt, and progresses through a defined [state machine](architecture.md).

Work items can declare that they depend on other work items. The orchestrator will not start a dependent until every item it depends on has reached a **terminal state**.

---

## Declaring dependencies

Pass the `dependsOn` field when creating a work item:

```json
POST /workitems
{
  "projectId": "my-app",
  "title":     "Quota-aware dispatcher",
  "prompt":    "Implement the dispatcher described in the design doc …",
  "dependsOn": ["5b6e7c410a234b5c8d9e0f1a2b3c4d5e"]
}
```

`dependsOn` is an optional array of work item IDs (32-character no-hyphen UUIDs). Omitting it or passing `[]` is equivalent — the item is independent and queued immediately.

Prompts can target any language used by the project. Examples:

```json
POST /workitems
{
  "projectId": "python-api",
  "title": "Add request validation",
  "prompt": "In the FastAPI user endpoint, validate email format before persistence and add pytest coverage for invalid input."
}
```

```json
POST /workitems
{
  "projectId": "node-web",
  "title": "Handle expired sessions",
  "prompt": "Update the Express session middleware to return 401 for expired sessions and add npm test coverage."
}
```

---

## Terminal states and the dependency gate

A dependency is **satisfied** when it has reached any of these terminal states:

| State | Meaning |
|-------|---------|
| `Done` | Completed successfully |
| `Failed` | Pipeline error (work, audit, or merge phase) |
| `AuditFailed` | Audit did not converge within `MaxIterations` |
| `Cancelled` | Operator-requested stop or parent-cascaded cancel (see below) |
| `AbandonedAfterRecoveryAttempts` | Recovery loop exceeded `MaxRecoveryAttempts`; operator intervention needed |

The gate is satisfied by *any* terminal state, not only `Done`. This is a deliberate design choice: if an upstream item fails, its dependents become eligible so the operator can inspect, manually retry the failed parent, and let the queue resume automatically — no manual re-enqueuing needed.

---

## Create-time validation

`POST /workitems` rejects new items that would violate the dependency contract:

| Condition | HTTP status | Body |
|-----------|-------------|------|
| A dep ID does not exist in the store | `400` | `{ "error": "dependency <id> not found" }` |
| An ID in `dependsOn` equals the new item's own ID | `400` | `{ "error": "a work item cannot depend on itself" }` |
| Adding the item would introduce a cycle | `400` | `{ "error": "circular dependency detected: <id> -> <id> -> …" }` |

Cycle detection runs DFS over the full dependency graph (O(V + E) where V is the number of work items in the store and E is the total number of dependency edges).

---

## Enqueueing behaviour

* **At create time**: if all dependencies are already terminal (or there are none), the item is enqueued immediately. Otherwise it is persisted in `Queued` state but not placed in the worker queue.
* **When a dependency reaches a terminal state**: the orchestrator scans for `Queued` items whose full dependency set is now satisfied and enqueues them automatically.
* **At startup (recovery)**: `Queued` items with unsatisfied dependencies are not re-enqueued; they will be picked up when their in-progress dependencies complete.

---

## Cancellation vs interruption

`state=Cancelled` means **"operator said stop — don't retry"**. It is only written for:

| Reason (`cancellationReason`) | Cause |
|-------------------------------|-------|
| `OperatorRequested` | `DELETE /workitems/{id}` or equivalent explicit operator action |
| `ParentCascaded` | A `dependsOn` parent ended in `Cancelled` state |

Items interrupted by a **host shutdown** (SIGTERM, OOM, reboot) are **not** transitioned to `Cancelled`. They remain in their mid-flight state (`Working`, `Auditing`, `Reworking`, `Merging`, etc.) and the recovery loop resets them to a safe restart point on the next startup.

### Cancellation propagation

When `DELETE /workitems/{id}` is called:

1. The target item is cancelled (`cancellationReason=OperatorRequested`) either by signalling its active `CancellationToken` if in-flight, or by a direct state transition to `Cancelled` if still queued.
2. All **Queued** items that transitively depend on the cancelled item are also transitioned to `Cancelled` with `cancellationReason=ParentCascaded` and `lastError = "parent dependency cancelled"`.
3. **In-flight** dependents (state ≠ `Queued`) are left to run their course — they were already past the dependency gate and may still succeed.

Cascade cancellation is transitive: if A → B → C and A is cancelled, B and C (both `Queued`) are cancelled.

### Uncancelling items

Use `POST /workitems/{id}/uncancel` to reset a `Cancelled` item back to `Queued` when:

- `cancellationReason=ParentCascaded` — the parent has since been retried.
- `cancellationReason=null` — legacy item whose reason is ambiguous (likely a pre-fix host-shutdown victim; see Operations Guide).

Returns 409 when `cancellationReason=OperatorRequested` — use `POST /workitems` to re-create the item instead.

### AbandonedAfterRecoveryAttempts

When the recovery loop has retried an item more than `CodeyBox:WorkerPool:MaxRecoveryAttempts` times (default 3) without it ever reaching a terminal state, the item is transitioned to `AbandonedAfterRecoveryAttempts` with a descriptive `lastError`. Use `POST /workitems/{id}/retry` to resume manually after investigating the root cause.

### Cancellation source attribution

When a pipeline phase is interrupted by `OperationCanceledException`, the orchestrator now attributes the cancellation to a stable source label, persisted as `cancellationSource` on the work item and surfaced in `lastError` / webhook events. This replaces the previously-conflated `failureKind=timeout` / `lastError='A task was canceled.'` shape that made it impossible to tell apart a real configured timeout from a transient host-side cancellation.

| `cancellationSource` | Meaning | `failureKind` on Failed | Auto-retry? |
|----------------------|---------|-------------------------|-------------|
| `operator` | `DELETE /workitems/{id}` (or the orchestrator's per-item registration token) | (state goes to `Cancelled`, not Failed) | No |
| `host-shutdown` | `IHostApplicationLifetime.ApplicationStopping` fired | (item left mid-flight for recovery loop) | n/a — recovery owns it |
| `host-shutdown-deadline` | Host shutdown grace expired before the phase drained | `cancelled` | Yes (transient) |
| `stuck-probe` | Stuck-probe detected zero-activity threshold and killed the phase | `agent` | Per `AutoRetryOnStuck` |
| `timeout:<phase>` | Configured per-phase wall-clock cap fired (`WorkTimeout`, `MergeTimeout`, `Audit.PerIterationTimeout`) | `timeout` | No |
| `unknown` | OCE propagated past every attribution hook — typically a leaked supervisor token in the orchestrator host | `cancelled` | Yes (transient) |

#### Transient-cancel auto-retry

When `cancellationSource` resolves to a transient label (`unknown` or `host-shutdown-deadline`) — i.e. neither operator intent nor a configured timeout — the orchestrator auto-retries the item from a recoverable pre-phase state instead of failing it outright. This matches the "pause not fail" guarantee for transient issues and avoids losing hours of rework when an unattributed host hiccup interrupts an expensive run.

The retry counter is `transientCancelRetries`, capped by `CodeyBox:WorkerPool:MaxTransientCancelRetries` (default 3). Past the cap, the item transitions to `Failed` with `failureKind=cancelled` and a pointed error message identifying the suspected cause (typically: "supervisor/cancellation-token leak in the orchestrator host"). Set `MaxTransientCancelRetries=0` to disable the auto-retry path entirely and surface every transient cancellation immediately.

The resume-state mapping mirrors the dead-worker reaper / startup replay:

| Cancelled phase | Resume state |
|-----------------|--------------|
| `work` / `rework-resume` | `Queued` (work phase rerun) |
| `rework` / `audit` | `WorkComplete` (audit phase rerun) |
| `merge` | `AuditPassed` (merge phase rerun) |
| `upstream` | `Merged` (upstream push rerun) |

Auto-retry events emit `work_item.transient_cancel_retried` (audit-log level Warning) with phase, source, attempt, and max so dashboards can isolate host-hiccup churn from quota or operator-driven retries.

---

## Inspecting blast radius

Before cancelling an item, check what depends on it:

```
GET /workitems/{id}/dependents
```

Returns the list of work items that have this item in their `dependsOn`. Useful for understanding the downstream impact before taking action.

---

## API additions

### `POST /workitems` — new field

```jsonc
{
  "dependsOn": ["<id>", "<id>"]   // optional; default []
}
```

### `GET /workitems/{id}` — new response fields

```jsonc
{
  "dependsOn":          ["<id>", "<id>"],
  "dependsOnSatisfied": true
}
```

`dependsOnSatisfied` is `true` when every item in `dependsOn` is in a terminal state. For items with no dependencies it is always `true`.

### `GET /workitems/{id}/dependents`

Returns the array of work item records (same shape as `GET /workitems/{id}`) that directly declare a dependency on `{id}`.

---

## Model quality routing

Work items carry two fields that control which agent member is selected by the
quota router. See [docs/agent-classes.md](agent-classes.md) for the full
routing algorithm.

### `agentClassId`

Binds the work item to a named agent class instead of a specific agent. When
set, the router picks the highest-scoring available member of that class.

```jsonc
{
  "projectId": "my-app",
  "title": "Refactor auth module",
  "prompt": "…",
  "agentClassId": "frontier-coding"
}
```

When null, falls back to `Project.DefaultAgentClass`, then to the legacy
direct `Agent` field. Per-item `agentClassId` always overrides the project
default.

### `minModelScore`

The minimum `QualityScore` the router will accept for this item. Default `95`
allows Gemini-3-Flash with high reasoning (score 95) as a frontier-adjacent
fallback. Lower it for low-stakes work:

```jsonc
{
  "projectId": "my-app",
  "title": "Fix typo in README",
  "prompt": "…",
  "minModelScore": 70
}
```

Valid range: `0`–`200`. Values outside the range are clamped at the API layer.
If no class member meets the floor the item **fails immediately** with error
`ROUTING_NO_ELIGIBLE: no member of class '...' meets MinModelScore=N` — it is
not retried. Lower `minModelScore` or add a capable member to the class.

---

## Immutability

`dependsOn` is immutable after creation. There is no endpoint to add, remove, or reorder dependencies. Mutating the dependency graph of an in-flight DAG is a footgun (a running worker could be blocked by a newly-added dependency it never knew about); out of scope for this release.
