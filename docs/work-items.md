# Work Items and Dependencies

## Overview

A *work item* is the unit of work CodeyBox submits to an agent. Each item belongs to a project, carries a natural-language prompt, and progresses through a defined [state machine](architecture.md).

Work items can declare that they depend on other work items. The orchestrator will not start a dependent until every item it depends on has reached `Done` — successful completion. Failed / AuditFailed / Cancelled prerequisites block dependents until an operator retries-and-resolves them.

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

## The dependency gate

A dependency is **satisfied** when it has reached `Done` — successful end-to-end completion. The gate is recomputed from the live store on every dispatch tick, so a dep that lands after a kick is honored on the very next pickup; no cached `dependsOnSatisfied` flag is consulted.

| Dep state | Gate satisfied? | Operator action |
|-----------|-----------------|-----------------|
| `Done` | ✅ Yes | None — dependent dispatches automatically |
| `Failed` | ❌ No | `POST /workitems/{depId}/retry` and let it reach `Done` |
| `AuditFailed` | ❌ No | `POST /workitems/{depId}/retry` (typically after raising `Audit.MaxIterations` or amending the prompt) |
| `MergeConflictResolutionFailed` | ❌ No | Resolve the conflict manually, then `POST /workitems/{depId}/retry` |
| `Cancelled` | ❌ No | `POST /workitems/{depId}/uncancel` (cascade) or `/resume` (operator-cancelled), then let it reach `Done` |
| `AbandonedAfterRecoveryAttempts` | ❌ No | Investigate the stuck root cause, then `POST /workitems/{depId}/retry` |
| Any non-terminal state | ❌ No | None — wait for completion |

Rationale: a dependent built on a failed prerequisite cannot be validated end-to-end. Running it anyway burns agent quota on speculative work the operator will likely discard once the parent is retried. The conservative posture matches the CB-12 quota-conservation policy.

If the operator wants to keep a dependent alive after deciding the parent is no longer needed, they can edit the dependent's `dependsOn` (PATCH endpoint) to drop the entry — the gate then re-evaluates without the parent.

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

* **At create time**: if all dependencies are already `Done` (or there are none), the item is enqueued immediately. Otherwise it is persisted in `Queued` state but not placed in the worker queue.
* **When a dependency reaches `Done`**: the orchestrator scans for `Queued` items whose full dependency set is now satisfied and enqueues them automatically. Reaching a non-`Done` terminal state (`Failed`, `AuditFailed`, `Cancelled`, …) does NOT enqueue dependents — they wait until an operator retries-and-resolves the parent.
* **At startup (recovery)**: `Queued` items with unsatisfied dependencies are not re-enqueued; they will be picked up when their in-progress dependencies reach `Done`.
* **At every dispatch tick**: the dispatcher refuses to pick up a Queued item with unsatisfied `dependsOn`, even when a kick exists for the item and a worker slot is free. The gate is the single source of truth for ordering — no path bypasses it.

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

### Closing terminal-failure items (operator bookkeeping)

`DELETE /workitems/{id}` also accepts items already in a terminal-failure state
— `Failed`, `AuditFailed`, `MergeConflictResolutionFailed`, and
`AbandonedAfterRecoveryAttempts` — and transitions them to `Cancelled` with
`cancellationReason=OperatorRequested`. This is the operator-facing close path
for items the orchestrator could not finish but the operator has resolved
out-of-band (e.g. manually merging the work after a
`MergeConflictResolutionFailed`). The transition is pure bookkeeping: no
pipeline state is replayed, no cascade is triggered (those dependents already
saw a non-`Done` parent), and the underlying terminal-failure rows are not
re-dispatched (Cancelled and the failure states are all excluded from the
dispatcher's pickup query).

Optional query parameters:

| Parameter | Purpose |
|-----------|---------|
| `reason` | Free-form note (≤500 chars, no control characters), appended to `lastError` so the audit trail captures the operator's justification. |
| `resolutionSha` | 7–40 character hex SHA of the commit on `main` that carries the resolution. Included in `lastError` so triage tooling can link back to the manual fix. |

```
DELETE /workitems/{id}?reason=manually+merged+as+abc123&resolutionSha=abc123de
```

A subsequent `DELETE` on an already-`Cancelled` item is a no-op (`202 Accepted`)
so retry-safe operator scripts do not need to read the current state first.
`DELETE` on `Done` still returns `409 Conflict` — a successful merge cannot be
cancelled away.

The audit-log event `work_item.cancelled` and the corresponding
`work_item.cancelled` webhook event carry the prior state, reason, and
resolution SHA in the webhook `details` payload so trackers can attribute the
manual close.

### Uncancelling items

Use `POST /workitems/{id}/uncancel` to reset a `Cancelled` item back to `Queued` when:

- `cancellationReason=ParentCascaded` — the parent has since been retried.
- `cancellationReason=null` — legacy item whose reason is ambiguous (likely a pre-fix host-shutdown victim; see Operations Guide).

Returns 409 when `cancellationReason=OperatorRequested` — use `POST /workitems/{id}/resume` instead (see below).

### Resuming an operator-cancelled item

When the operator hits `DELETE /workitems/{id}` mid-iteration — for example to pause the pipeline while triaging a flaky auditor — the bare repo at `~/.codeybox/repos/{id}.git` and the work-branch with every commit the agent has already produced stay on disk. `POST /workitems/{id}/resume` re-enters the pipeline on top of that preserved state instead of throwing the work away.

```
POST /workitems/{id}/resume
{
  "from": "work" | "audit" | "merge",   // optional, default "work"
  "reason": "<operator-supplied reason>"   // optional, free-form, appears in audit log + webhook
}
```

| `from` | Resume state | When to use |
|--------|--------------|-------------|
| `work` (default) | `Queued` | Continue rework on top of the existing work-branch — the next pickup sees N commits ahead of base and produces the next rework iteration. |
| `audit` | `WorkComplete` | Re-run the audit phase against the existing tip — useful when the cancel was triggered to wait for an auditor fix. `auditIterations` continues from its prior value (not reset to 0). |
| `merge` | `AuditPassed` | Attempt the merge against the latest base. Rare; offered for completeness. |

**Preserves** the `Id`, `ExternalId`, `WorkBranch`, every commit in the bare repo, `FallbackHistory`, `UsageTotal`, `AuditIterations`, `QuotaResetAt`, and `Priority`. **Resets** `LastError`, `CancellationReason`, `CancellationSource`, `FailureKind`, and `RecoveryAttempts`.

Returns `412 Precondition Failed` when the bare repo or the work-branch ref is no longer present on disk — fall back to `POST /workitems/{id}/replay` for a fresh start. Returns `409` when the item is not in `Cancelled` state.

Distinct from `/uncancel` (operator cancels are refused there by design — the operator chose to stop, so undoing that needs its own verb) and from `/retry` (which is scoped to terminal-failed states, not Cancelled).

### AbandonedAfterRecoveryAttempts

When the recovery loop has retried an item more than `CodeyBox:WorkerPool:MaxRecoveryAttempts` times (default 3) without it ever reaching a terminal state, the item is transitioned to `AbandonedAfterRecoveryAttempts` with a descriptive `lastError`. Use `POST /workitems/{id}/retry` to resume manually after investigating the root cause.

### Cancellation source attribution

When a pipeline phase is interrupted by `OperationCanceledException`, the orchestrator now attributes the cancellation to a stable source label, persisted as `cancellationSource` on the work item and surfaced in `lastError` / webhook events. This replaces the previously-conflated `failureKind=timeout` / `lastError='A task was canceled.'` shape that made it impossible to tell apart a real configured timeout from a transient host-side cancellation.

| `cancellationSource` | Meaning | `failureKind` on Failed | Auto-retry? |
|----------------------|---------|-------------------------|-------------|
| `operator` | `DELETE /workitems/{id}` (or the orchestrator's per-item registration token) | (state goes to `Cancelled`, not Failed) | No |
| `host-shutdown` | `IHostApplicationLifetime.ApplicationStopping` fired | (item left mid-flight for recovery loop) | n/a — recovery owns it |
| `host-shutdown-deadline` | Host shutdown grace expired before the phase drained | (item left mid-flight for recovery loop) | n/a — recovery owns it (host is going away; auto-retry would race the shutdown) |
| `stuck-probe` | Stuck-probe detected zero-activity threshold and killed the phase | `agent` (via `AgentStuckException`, not via `cancellationSource`) | Per `AutoRetryOnStuck` |
| `timeout:<phase>` | Configured wall-clock cap fired. Work/rework and merge use fresh per-agent-attempt budgets (`WorkTimeout` / `MergeTimeout`) inside an absolute fallback-chain cap (`PhaseAbsoluteTimeoutMultiplier`, default `3.0`); audit uses `Audit.PerIterationTimeout`. | `timeout` | No |
| `unknown` | OCE propagated past every attribution hook — typically a leaked supervisor token in the orchestrator host | `cancelled` | Yes (transient) |

#### Transient-cancel auto-retry

When `cancellationSource` resolves to `unknown` — an `OperationCanceledException` that propagated past every attribution hook, neither operator intent, host shutdown, nor a configured timeout — the orchestrator auto-retries the item from a recoverable pre-phase state instead of failing it outright. This matches the "pause not fail" guarantee for transient issues and avoids losing hours of rework when an unattributed host hiccup interrupts an expensive run.

The retry counter is `transientCancelRetries`, capped by `CodeyBox:WorkerPool:MaxTransientCancelRetries` (default 3). Past the cap, the item transitions to `Failed` with `failureKind=cancelled` and a pointed error message identifying the suspected cause (typically: "supervisor/cancellation-token leak in the orchestrator host"). Set `MaxTransientCancelRetries=0` to disable the auto-retry path entirely and surface every transient cancellation immediately.

The resume-state mapping mirrors the dead-worker reaper / startup replay:

| Cancelled phase | Resume state |
|-----------------|--------------|
| `work` | `Queued` (work phase rerun) |
| `rework-resume` / `rework` / `audit` | `WorkComplete` (audit phase rerun; committed work on the work branch is preserved) |
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

`dependsOnSatisfied` is `true` when every item in `dependsOn` is in `Done`. For items with no dependencies it is always `true`. Failed / AuditFailed / Cancelled deps leave it `false` until an operator retries the parent to success.

### `GET /workitems/{id}/dependents`

Returns the array of work item records (same shape as `GET /workitems/{id}`) that directly declare a dependency on `{id}`.

### `DELETE /workitems/{id}` — new optional query parameters

```
DELETE /workitems/{id}?reason=<text>&resolutionSha=<7-40 hex chars>
```

`reason` and `resolutionSha` are surfaced in `lastError` on the resulting
`Cancelled` row and in the webhook `details` payload. Accepted from any
non-`Done` state, including the terminal-failure states
(`Failed` / `AuditFailed` / `MergeConflictResolutionFailed` /
`AbandonedAfterRecoveryAttempts`) — see *Closing terminal-failure items* above.

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

The minimum `QualityScore` the router will accept for this item. Default `0`
(open to any agent — the router still picks the strongest free member by
quality score). Set a high floor only for the few sensitive or major-
architectural items that must be restricted to frontier agents.

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

> **Deprecated as the eligibility gate.** `minModelScore` is being replaced by
> the explicit `requiredCapabilities` mechanism below. Both are honoured during
> the transition window (a member must pass both gates) so existing items keep
> working unchanged.

### `requiredCapabilities`

A list of clearance/trust tags every routed agent member must declare. Empty
or omitted (the default) means "no clearance required" — any member of the
resolved agent class is eligible. When non-empty, the router only routes the
item to members whose `Capabilities` list covers every tag here.

```jsonc
{
  "projectId": "core",
  "title": "Rewrite auth middleware",
  "prompt": "…",
  "agentClassId": "frontier-coding",
  "requiredCapabilities": ["sensitive"]
}
```

Trust vs. preference:

- `requiredCapabilities` expresses **trust** ("only these models may touch
  this work") — an explicit, declared property of the agent membership.
- `QualityScore` continues to drive **preference** ("of the eligible models,
  pick the strongest") — never the gate.

See [Capability gate in `docs/agent-classes.md`](agent-classes.md#capability-gate)
for the recommended tag vocabulary, member declaration syntax, and migration
notes from `minModelScore`.

Tag values are compared case-insensitively; duplicates are de-duped and
whitespace trimmed at create / patch time. The list is editable via
`PATCH /workitems/{id}` while the item is still `Queued` and is preserved
across `/replay`.

---

## Editing dependencies post-hoc

`dependsOn` is editable via `PATCH /workitems/{id}` with replace-set
semantics — pass the full new array (a GUID, a namespaced `ns:value`
externalId, or a bare externalId for each entry) and it overwrites the
item's dependency list. Same validation as the create path: cap at 100,
existence check, self-loop and cycle rejection.

Allowed on any **non-terminal** state. Terminal items
(`Done` / `Cancelled` / `Failed` / `AuditFailed` /
`MergeConflictResolutionFailed` / `AbandonedAfterRecoveryAttempts`)
reject with `409` because dependencies on closed work are moot. Editing
an in-flight item (`Working` / `Auditing` / …) does not affect the
current iteration — the gate has already passed — but is recorded for
any future re-dispatch (recovery / retry paths).

The change is persisted via a partial UPDATE that touches only
`depends_on_json` and `updated_at`, so a concurrent worker mid-pipeline
is not stomped. An audit-log entry (`work_item.dependencies_changed`)
records the pre- and post-edit ID sets.
