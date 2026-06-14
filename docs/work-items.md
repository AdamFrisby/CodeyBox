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

When the recovery loop has retried an item more than `CodeyBox:DeadWorker:MaxRecoveryAttempts` times (default 10) without it ever completing the recovered phase, the item is transitioned to `AbandonedAfterRecoveryAttempts` with a descriptive `lastError`. Use `POST /workitems/{id}/retry` to resume manually after investigating the root cause.

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

### Per-phase agent involvement

The single `agent` field reflects only the **current** phase's agent and is overwritten as an item moves through Work → Audit → Rework → Merge. To see who-did-what at every stage, the read model exposes a per-phase audit trail.

`GET /workitems/{id}` includes (when the involvement store is wired):

```jsonc
{
  "agent":     "claude",   // current-phase agent (unchanged contract)
  "workAgent": "cursor",   // agent that ran the original Work phase, or null
  "agentHistory": [
    { "id": "…", "agentKind": "cursor", "modelId": "composer-2.5",
      "phase": "work",  "startedAt": "…", "endedAt": "…", "iteration": null, "outcome": "success" },
    { "id": "…", "agentKind": "claude", "modelId": null,
      "phase": "audit:security", "startedAt": "…", "endedAt": null, "iteration": 1, "outcome": null }
  ]
}
```

A new `agentHistory` row is appended on every phase transition — once per agent attempt for the Work, Rework, and Merge phases, and once per LLM auditor for each Audit iteration. Quota/timeout fallbacks append an additional row for the agent that took over, so every agent that touched the item is recorded.

Because each audit iteration re-runs the **full** auditor list (a rework can regress a dimension a previously-passing auditor would catch), a `Work → Audit → Rework → Audit → Merge` progression with `N` LLM auditors produces `1 + N + 1 + N + 1 = 2N + 3` rows. So the canonical seven-row trail corresponds to two auditors (`N = 2`); three auditors honestly produce nine rows, not seven. Entries are an immutable audit trail: `endedAt` / `outcome` are `null` while the phase is in progress and stamped exactly once on completion (`outcome` is `"success"` or `"failure:<reason>"`). `agentHistory` is `[]` (not omitted) when the store is wired but nothing has run yet; history starts empty for items created before the feature existed.

`workAgent` is the original implementer — the `work`-phase entry that completed with `outcome: "success"`, falling back to the first `work` attempt while none has succeeded yet. After a work-phase quota/timeout fallback (e.g. codex `failure:quota` then claude `success`), this reports the agent that actually produced the implementation, not the exhausted first attempt. It is distinct from "who's currently auditing it".

### `GET /workitems/{id}/agent-history`

Returns just the involvement trail — cheaper than the full work-item read for UI polling:

```jsonc
{ "workItemId": "<id>", "workAgent": "cursor", "agentHistory": [ … ] }
```

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

## Check-and-act work items

A *check-and-act* work item is a special `JobType` that runs ONE agent
invocation in the project sandbox — exactly the same in-VM execution path
normal items use, no raw provider HTTP — to answer a yes/no question against
the project repository. The agent is required to return a structured JSON
verdict; on a matching verdict the orchestrator enqueues a follow-up Normal
work item back into the same project, parented to the check.

The check item itself never opens a PR, never commits, never pushes upstream.
Its only deliverable is the verdict (persisted on the row, queryable via
`GET /workitems/{id}`, and surfaced in the timeline).

### Creating a check-and-act item

Add an optional `check` block to the `POST /workitems` payload:

```json
POST /workitems
{
  "projectId": "my-app",
  "title":     "Check for SQL injection",
  "prompt":    "evaluate the repo",
  "check": {
    "question": "Is any user-facing SQL built via string concatenation / interpolation (SQL-injection risk)?",
    "actionableAnswer": true,
    "onYes": {
      "title":         "Fix all SQL injection vulnerabilities and verify none remain",
      "prompt":        "Replace every string-interpolated SQL with parameterised queries. Add tests.",
      "minModelScore": 50,
      "priority":      100
    }
  }
}
```

The `check` block is data, not policy — the question text, the actionable
condition, and the on-yes follow-up's title/prompt/agent/priority are all
supplied by the caller. The orchestrator never hardcodes a question.

| `check` field | Required | Default | Notes |
|---|---|---|---|
| `question` | yes | — | The yes/no question. ≤ 64 KB. |
| `actionableAnswer` | no | `true` | Which boolean answer triggers `onYes`. Set to `false` for inverse-shape checks (e.g. "if no tests cover X, write some"). |
| `onYes.title` | yes | — | Follow-up work item title (≤ 200 chars, no leading dash, no control chars). |
| `onYes.prompt` | yes | — | Follow-up prompt (≤ 64 KB). |
| `onYes.agent` | no | inherit | Override agent kind for the follow-up. |
| `onYes.agentClassId` | no | inherit | Override agent class for the follow-up. |
| `onYes.minModelScore` | no | `0` | Min quality score floor. Clamped to `[0, 200]`. |
| `onYes.priority` | no | `0` | Dispatch priority. Clamped to `[-1000, 1000]`. |
| `onYes.dependsOn` | no | `[]` | Dependency list (UUIDs / externalIds). Resolution mirrors the regular create path. |

### Verdict protocol

The orchestrator builds the agent prompt by wrapping `check.question` with a
strict response protocol. The agent MUST emit exactly one JSON object between
the sentinels `<<<CODEYBOX_VERDICT>>>` and `<<<END_VERDICT>>>`:

```
<<<CODEYBOX_VERDICT>>>
{"answer": true, "evidence": "src/Foo.cs L42 builds SQL via interpolation", "confidence": "high"}
<<<END_VERDICT>>>
```

The parser is strict — missing sentinels, missing required fields (`answer`,
`evidence`), or unparseable JSON all transition the check to `Failed` with
`failureKind="other"`. The orchestrator never guesses a yes/no from free text.

When multiple verdict blocks appear (e.g. the agent revised mid-run), the
LAST block wins.

### Outcomes

| Verdict | Check state | Follow-up |
|---|---|---|
| `answer == actionableAnswer` | `Done` (verdict persisted) | New `Normal` work item Queued + kicked on the dispatch queue, parented to the check via `originCheckWorkItemId`. |
| `answer != actionableAnswer` | `Done` (verdict persisted) | None. |
| Malformed / missing verdict | `Failed` (`failureKind="other"`) | None. |
| Agent error / sandbox failure | `Failed` (`failureKind="other"`) | None. |

The verdict and the back-pointer are exposed in the work-item DTO:

```jsonc
{
  "id": "…",
  "jobType": "CheckAndAct",
  "check": { "question": "…", "actionableAnswer": true, "onYes": { … } },
  "verdict": { "answer": true, "evidence": "…", "confidence": "high" },
  "originCheckWorkItemId": null            // set ONLY on follow-ups; null on the check itself
}
```

A successful enqueue also publishes a `work_item.check_followup_enqueued`
webhook event carrying the parent check ID and the new follow-up's ID.

### Post-act re-validation

The check-and-act loop closes itself: when a follow-up work item (the *act*)
completes its normal pipeline phase — work → audit/rework → just-before-merge —
the orchestrator RE-RUNS the originating check's question against the modified
repo as a final validation before accepting the act as `Done`. The re-check
uses the same in-VM execution path as the initial check (sandbox clone +
`<<<CODEYBOX_VERDICT>>>` sentinels + structured verdict). The question and
the actionable answer are read from the originating check item's stored
`CheckAndActSpec` — no new hardcoded question.

| Re-check verdict | Follow-up outcome |
|---|---|
| `answer != actionableAnswer` (e.g. "no" = vulnerability no longer present) | Merge phase proceeds; the act reaches `Done`. |
| `answer == actionableAnswer` (problem persists) | The act is sent back to rework with the failing verdict as feedback; the orchestrator re-validates again after each rework. |
| Cap exhausted while still actionable | The act transitions to `Failed` with `failureKind="other"` and `LastError = "remediation did not satisfy the check after N attempt(s) …"`. The item needs a human / re-scoping. |

The cap is the project's existing `audit.maxIterations` — the same setting
that bounds the audit/rework loop, reused here so there's no parallel
configuration path to manage.

Every re-check verdict (initial post-act check + each rework re-check) is
appended to the follow-up's `reCheckVerdicts` history in order; the
originating check's initial verdict remains on the check item itself.
The two together form the full re-validation timeline:

```jsonc
{
  "id": "…",                                  // the act item
  "originCheckWorkItemId": "…",               // back-pointer to the check
  "reCheckVerdicts": [
    {"answer": true,  "evidence": "iter1 still present", "confidence": "high"},
    {"answer": true,  "evidence": "iter2 still present", "confidence": "medium"},
    {"answer": false, "evidence": "iter3 now clean",     "confidence": "high"}
  ]
}
```

Each iteration also publishes a `work_item.post_act_recheck_completed`
webhook event with the iteration number, the verdict's answer, and the
originating check id — operators can stream this to build a per-item
re-validation timeline without re-reading the DTO.

The gate fires only for items whose `originCheckWorkItemId` is set;
plain work items go straight from audit-pass to merge with no re-check
invocation. Orphaned follow-ups (whose originating check has been
deleted) skip the gate and proceed normally — losing the check item
must not strand the follow-up.

---

## Refactor work items

A *refactor* work item is a `JobType` flag that runs the same
work → audit → merge → upstream pipeline as a `Normal` item, but the
dispatcher treats it as **project-exclusive**: refactors touch broad surface
area and would conflict badly with concurrent work in the same project, so
the orchestrator gates them at pickup time.

### Dispatch rules

For each project independently:

1. A refactor item only STARTS once the project has **zero other in-flight
   work items** (any non-terminal, actively-running state — `Working`,
   `Auditing`, `Reworking`, `Merging`, etc.).
2. While a refactor item is in flight for a project, **no other work item
   for that project may start** — the refactor holds an exclusive
   project-scoped lock.
3. Refactors are themselves mutually exclusive per project: only one
   refactor can be in flight per project at a time.

The gate is strictly project-scoped: a refactor on project X does not
delay anything in project Y. Items that would otherwise dispatch are
*deferred* (not failed), and re-enqueued automatically once the gate
opens; this matches the existing per-project budget-cap deferral pattern.

The recheck interval is configurable via
`CodeyBox:BudgetDeferralRecheck:RefactorExclusivityRecheck` (default
60 seconds).

### Creating a refactor item

Add `"isRefactor": true` to the `POST /workitems` payload. The flag is
mutually exclusive with the `check` and `agentControl` blocks (the API
returns `400` if more than one is supplied):

```json
POST /workitems
{
  "projectId": "my-app",
  "title":     "Rename FooService → BarService throughout the codebase",
  "prompt":    "Rename the FooService class and all references…",
  "isRefactor": true
}
```

The returned work-item DTO reports `"jobType": "Refactor"` and behaves
identically to a Normal item in every later phase — same audit profile,
same merge path, same upstream push. The only difference is the
dispatch-time exclusivity gate.

### Anti-starvation

A refactor can in principle sit deferred indefinitely if the project
never drains. Anti-starvation policy (e.g. quiescing new pickups to let
a refactor through) is intentionally out of scope for the gate itself
and will be tracked as a separate follow-up. Until that ships,
operators that want a refactor to land on a busy project should pause
the project queue (`POST /projects/{id}/pause`) until the in-flight
items finish, then unpause; the refactor will pick up at the next
dispatch tick.

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
