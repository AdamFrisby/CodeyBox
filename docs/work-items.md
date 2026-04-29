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

---

## Terminal states and the dependency gate

A dependency is **satisfied** when it has reached any of these terminal states:

| State | Meaning |
|-------|---------|
| `Done` | Completed successfully |
| `Failed` | Pipeline error (work, audit, or merge phase) |
| `AuditFailed` | Audit did not converge within `MaxIterations` |
| `Cancelled` | Cancelled via `DELETE /workitems/{id}` |

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

## Cancellation propagation

When `DELETE /workitems/{id}` is called:

1. The target item is cancelled (either by signalling its active `CancellationToken` if in-flight, or by a direct state transition to `Cancelled` if still queued).
2. All **Queued** items that transitively depend on the cancelled item are also transitioned to `Cancelled` with `lastError = "parent dependency cancelled"`.
3. **In-flight** dependents (state ≠ `Queued`) are left to run their course — they were already past the dependency gate and may still succeed.

Cascade cancellation is transitive: if A → B → C and A is cancelled, B and C (both `Queued`) are cancelled.

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

## Immutability

`dependsOn` is immutable after creation. There is no endpoint to add, remove, or reorder dependencies. Mutating the dependency graph of an in-flight DAG is a footgun (a running worker could be blocked by a newly-added dependency it never knew about); out of scope for this release.
