# Webhooks

CodeyBox can POST JSON events to one or more HTTPS endpoints as a work item moves through the pipeline. Use this to integrate with Slack, audit-log services, custom dashboards, or any HTTP receiver.

---

## Event taxonomy

One event is fired per state transition. Events follow the naming convention `work_item.<state>`:

| Event | Fired when |
|---|---|
| `work_item.working` | Agent starts the work phase |
| `work_item.work_complete` | Work phase succeeded; agent committed changes |
| `work_item.auditing` | Audit phase starts (once per iteration) |
| `work_item.audit_iteration` | Each audit iteration completes (see [Details](#audit_iteration-details)) |
| `work_item.audit_passed` | All iterations passed; moving to merge |
| `work_item.reworking` | Audit found blocking findings; agent starts rework |
| `work_item.audit_failed` | Audit did not converge after max iterations |
| `work_item.merging` | Merge phase starts |
| `work_item.merged` | Merge phase succeeded |
| `work_item.upstream_pushing` | Upstream push starts |
| `work_item.pull_request_opened` | A GitHub pull request was opened for the work branch (only fires from `Upstream.Kind=github`; see [Details](#pull_request_opened-details)) |
| `work_item.done` | Work item completed successfully |
| `work_item.failed` | Work item failed (unrecoverable error) |
| `work_item.cancelled` | Work item was cancelled via the API |
| `work_item.agent_stuck` | Stuck-agent probe detected a hang and killed the agent (see [Details](#agent_stuck-details)) |
| `agent.smoke_failed` | Credential smoke test failed at startup or work-item pickup (see [Details](#agent_smoke_failed-details)) |
| `queue.paused` | Operator paused the global pickup queue (see [Details](#queue_paused-details)) |
| `queue.resumed` | Operator resumed the global pickup queue (see [Details](#queue_resumed-details)) |
| `budget.deferred` | A work item was deferred by a per-project budget cap (see [Details](#budget_deferred-details)) |
| `project.budget_warning` | Project's 30-day spend crossed the warning threshold (see [Details](#projectbudget_warning-details)) |
| `project.budget_exceeded` | Project's 30-day spend crossed the hard cap; project queue auto-paused (see [Details](#projectbudget_warning-details)) |
| `project.budget_recovered` | Project's 30-day spend dropped back below the warning threshold (see [Details](#projectbudget_warning-details)) |
| `project.queue_paused` | Per-project queue was paused (manual or auto) |
| `project.queue_resumed` | Per-project queue was resumed |
| `work_item.recovered` | Dead-worker reaper recovered a work item that was mid-flight when its worker crashed (see [Details](#recovered-details)) |
| `work_item.auto_retry` | Quota auto-retry scheduler re-queued a Failed work item once its quota window reopened (see [Details](#auto_retry-details)) |
| `work_item.suggestion` | Agent emitted a suggestion (one event per suggestion entry; see [Details](#suggestion-details)) |
| `work_item.needs_operator_input` | Work item parked waiting for operator to answer one or more questions |
| `work_item.question_asked` | Agent emitted a `<codeybox-question>` block; item parked at `NeedsOperatorInput` (see [Details](#question_asked-details)) |
| `work_item.question_answered` | Operator answered a question via `POST /workitems/{id}/answer` (see [Details](#question_answered-details)) |
| `work_item.question_dismissed` | Operator dismissed a question via `POST /workitems/{id}/dismiss-question` (see [Details](#question_dismissed-details)) |
| `sandbox.leak_detected` | A leaked `codeybox-*` Multipass VM was detected (see [Details](#sandbox_leak-details)) |
| `sandbox.leak_disposed` | A leaked sandbox was successfully auto-disposed |
| `sandbox.leak_dispose_failed` | Auto-disposal of a leaked sandbox failed |
| `iteration.started` | A work or rework iteration was dispatched to the agent (see [Intermediate events](#intermediate-progress-events)) |
| `iteration.completed` | A work or rework iteration finished and committed |
| `audit.started` | An audit iteration started; carries the scheduled auditor list |
| `audit.findings.emitted` | Audit iteration produced findings; carries the full finding list so trackers can render comments without polling |
| `audit.completed` | Audit iteration finished with a `pass` or `fail` verdict |
| `merge.started` | Merge phase started |
| `merge.completed` | Merge phase succeeded; carries the merge commit SHA |

`work_item.audit_iteration` fires **after every audit iteration**, regardless of pass or fail, and carries per-iteration counts in the `details` field.

---

## Payload shape

Every event is a JSON object POSTed as the request body.

```json
{
  "eventSchemaVersion": "1.0",
  "event": "work_item.audit_passed",
  "occurredAt": "2026-04-29T12:34:56.789+00:00",
  "workItem": {
    "id": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
    "externalId": "JIRA-1234",
    "projectId": "my-project",
    "title": "Add dark-mode support",
    "agent": "claude",
    "repositoryUrl": "https://github.com/example/repo",
    "baseBranch": "main",
    "workBranch": "codeybox/a1b2c3d4",
    "state": "AuditPassed",
    "createdAt": "2026-04-29T12:00:00.000+00:00",
    "updatedAt": "2026-04-29T12:34:56.789+00:00",
    "lastError": null,
    "upstreamPushAttempts": 0
  },
  "project": {
    "id": "my-project",
    "displayName": "My Project",
    "repositoryUrl": "https://github.com/example/repo"
  },
  "details": null,
  "usage": {
    "iteration": 2,
    "tokensInput": 8000,
    "tokensOutput": 900,
    "tokensReasoning": 0,
    "tokensCached": 500,
    "costUsd": 0.2310,
    "elapsedMs": 6500
  },
  "usageTotal": {
    "tokensInput": 16500,
    "tokensOutput": 1590,
    "tokensReasoning": 0,
    "tokensCached": 500,
    "costUsd": 0.4012,
    "elapsedMs": 14000
  }
}
```

`usage` covers the most recent iteration's contribution; `usageTotal` is the
cumulative spend across every iteration of the work item. Both blocks are
omitted entirely when no cost data is available (e.g. an agent without a
registered cost extractor) — receivers should treat absent as "unknown".
`tokensReasoning` is reserved for future model surfaces and is `0` today.

`eventSchemaVersion` is stamped on every payload and identifies the envelope
contract version (top-level fields, identifier semantics, signing). New event
types or new optional `details` fields are additive and do **not** bump this
version. Receivers should treat unknown event names as a no-op and ignore any
unknown fields inside `details`.

### `audit_iteration` details

When `event` is `work_item.audit_iteration`, the `details` field is populated:

```json
{
  "details": {
    "iteration": 1,
    "totalIterations": 3,
    "blockingFindings": 2,
    "nonBlockingFindings": 1
  }
}
```

### `pull_request_opened` details

When `event` is `work_item.pull_request_opened` (only emitted by `Upstream.Kind=github`), the `details` field carries the PR coordinates and, if `Upstream.AutoMerge=true`, the merge SHA:

```json
{
  "details": {
    "workBranch": "codeybox/a1b2c3d4",
    "baseBranch": "main",
    "pullRequestNumber": 42,
    "pullRequestUrl": "https://github.com/example/repo/pull/42",
    "mergedSha": "abc123def456..."
  }
}
```

`mergedSha` is `null` when `AutoMerge=false` or when GitHub refused the auto-merge (e.g. branch protection); the PR is still left open in that case.

### `agent_stuck` details

When `event` is `work_item.agent_stuck`, the `details` field is populated:

```json
{
  "details": {
    "phase": "work",
    "agentKind": "claude",
    "stuckSeconds": 602,
    "killed": true
  }
}
```

| Field | Type | Description |
|---|---|---|
| `phase` | string | Pipeline phase where the hang was detected: `"work"`, `"rework"`, or `"merge"` |
| `agentKind` | string | Agent binary that was killed (e.g. `"claude"`, `"codex"`) |
| `stuckSeconds` | int | Approximate idle duration in seconds when the probe fired |
| `killed` | bool | Always `true`; reserved for future graceful-shutdown paths |

`work_item.agent_stuck` fires **before** the terminal state event
(`work_item.failed` or the state preceding auto-retry). Operators should
subscribe to this event to alert on hung agents even when `AutoRetryOnStuck`
automatically re-queues the item.

### `queue_paused` details

When `event` is `queue.paused`:

```json
{
  "details": {
    "pausedAt": "2026-05-01T14:00:00.000+00:00",
    "reason": "pre-maintenance window",
    "pausedBy": "api"
  }
}
```

`externalId` is the caller-supplied identifier set at creation, or `null` when not provided. Receivers that don't need it can ignore the field.

`workItem` and `project` are `null` for queue-level events.

### `queue_resumed` details

When `event` is `queue.resumed`:

```json
{
  "details": {
    "resumedAt": "2026-05-01T15:30:00.000+00:00"
  }
}
```

### `budget_deferred` details

When `event` is `budget.deferred`, `workItem` and `project` carry the affected
item and its project:

```json
{
  "details": {
    "reason": "hourly limit: 10/10 items started in last hour",
    "suggestedRetryAt": "2026-05-01T14:05:00.000+00:00"
  }
}
```

| Field | Type | Description |
|---|---|---|
| `reason` | string | Which cap was exceeded and the current/max counts |
| `suggestedRetryAt` | ISO-8601 | Approximate time when the item may be eligible to start |

Subscribe to `budget.deferred` to alert when a project is consistently
throttled — it may indicate the budget caps need adjustment or the project's
work-item generation rate is unexpectedly high.

### `suggestion` details

When `event` is `work_item.suggestion`, the `details` field carries the
suggestion metadata. **`rationale` is excluded from the payload** to keep
webhook bodies small; retrieve it via `GET /suggestions/{id}` if needed.

```json
{
  "details": {
    "id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
    "title": "Add unit tests for the parser",
    "category": "test-coverage",
    "severity": "notable",
    "estimatedEffort": "medium",
    "filesReferenced": ["src/parser.ts"]
  }
}
```

| Field | Type | Description |
|---|---|---|
| `id` | string | Suggestion ID — use with `GET /suggestions/{id}` to fetch full details |
| `title` | string | Short human-readable label (≤ 120 chars) |
| `category` | string | One of `test-coverage`, `refactor`, `dead-code`, `security`, `dependency`, `docs`, `other` |
| `severity` | string | One of `minor`, `notable`, `important` |
| `estimatedEffort` | string | One of `tiny`, `small`, `medium`, `large` |
| `filesReferenced` | string[] | File paths the agent considered relevant (may be empty) |

One event fires **per suggestion entry**, not per file. A suggestions.json with
three entries produces three separate `work_item.suggestion` events. All three
carry the same `workItem` and `project` context.

See [`suggestions.md`](suggestions.md) for the full agent contract and operator
workflow.

### `question_asked` details

When `event` is `work_item.question_asked`, the `details` field carries the question that was parked:

```json
{
  "details": {
    "questionId": "q-001",
    "questionText": "Which database migration strategy should I use?"
  }
}
```

One event fires **per new question**. If the agent emits three `<codeybox-question>` blocks in a single run, three events fire. Questions that already exist (duplicate `questionId` for the same work item) are silently ignored and do not fire a new event.

| Field | Type | Description |
|---|---|---|
| `questionId` | string | The `id` attribute from the `<codeybox-question>` tag (≤ 64 alphanumeric/dash/underscore chars) |
| `questionText` | string | The trimmed question text (≤ 4000 chars; redacted of secrets) |

Subscribe to `work_item.question_asked` to alert operators when a work item needs human input before it can proceed.

### `question_answered` details

When `event` is `work_item.question_answered`, the `details` field carries the answered question:
### `project.budget_warning` details

When `event` is `project.budget_warning`, `project.budget_exceeded`, or `project.budget_recovered`, the `details` field is populated. `workItem` is `null` (these are project-level events, not tied to a specific work item).
### `recovered` details

When `event` is `work_item.recovered`, the `details` field is populated:

```json
{
  "details": {
    "workItemId": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
    "projectId": "my-project",
    "questionId": "q-001",
    "answer": "Use approach B; we standardise on those across the codebase.",
    "answeredBy": null
    "projectId": "my-app",
    "currentSpendUsd": 432.18,
    "budgetUsd": 500.00,
    "pct": 86.4,
    "thresholdPct": 80
    "workItemId": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
    "projectId": "my-project",
    "fromState": "Working",
    "toState": "Queued",
    "reason": "dead worker detected",
    "recoveryAttempt": 1,
    "maxRecoveryAttempts": 2
  }
}
```

| Field | Type | Description |
|---|---|---|
| `workItemId` | string | UUID of the work item |
| `projectId` | string | Project the work item belongs to |
| `questionId` | string | The question ID that was answered |
| `answer` | string | The operator's answer (redacted of secrets) |
| `answeredBy` | string\|null | Identity of the operator who answered; currently always `null` (auth layer does not yet populate caller identity) |

### `auto_retry` details

When `event` is `work_item.auto_retry`, the `details` field is populated:

```json
{
  "details": {
    "workItemId": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
    "reason": "quota",
    "attemptNumber": 1,
    "triggeredBy": "targeted"
  }
}
```

| Field | Type | Description |
|---|---|---|
| `workItemId` | string | UUID of the work item that was auto-retried |
| `reason` | string | Why the retry was scheduled. Currently always `"quota"`. |
| `attemptNumber` | int | Which auto-retry attempt this is (1-indexed). Capped at `AutoRetryOnQuotaFailure:MaxAutoRetriesPerWorkItem`. |
| `triggeredBy` | string | `"targeted"` if fired by the per-item timer at `QuotaResetAt + ClockDriftSafetyMargin`; `"periodic"` if fired by the safety-net sweep. |

### `question_dismissed` details

When `event` is `work_item.question_dismissed`, the `details` field carries the dismissed question:

```json
{
  "details": {
    "workItemId": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
    "projectId": "my-project",
    "questionId": "q-001",
    "reason": "Out of scope for this PR."
  }
}
```

| Field | Type | Description |
|---|---|---|
| `workItemId` | string | UUID of the work item |
| `projectId` | string | Project the work item belongs to |
| `questionId` | string | The question ID that was dismissed |
| `reason` | string | The operator's reason for dismissal (redacted of secrets) |

---
| `projectId` | string | Project whose budget triggered the event |
| `currentSpendUsd` | decimal | 30-day rolling spend at time of event |
| `budgetUsd` | decimal | Configured `MonthlyCostBudgetUsd` |
| `pct` | number | `currentSpendUsd / budgetUsd * 100` |
| `thresholdPct` | int | The threshold that was crossed (`CostWarningThresholdPct` or `CostHardCapPct`) |

On restart, the first sweep tick re-fires any events that apply (idempotency requirement). Receivers should de-duplicate by `(projectId, thresholdState)` — the `pct` value lets you derive which band you're in.

See [`budget-alerts.md`](budget-alerts.md) for configuration and edge-trigger semantics.

---
| `workItemId` | string | UUID of the recovered work item |
| `projectId` | string | Project the item belongs to |
| `fromState` | string | State the item was in when the worker was declared dead |
| `toState` | string | State the item was transitioned to; `"Failed"` if `MaxRecoveryAttempts` was exceeded |
| `reason` | string | Always `"dead worker detected"` |
| `recoveryAttempt` | int | Which recovery attempt this is (1-based) |
| `maxRecoveryAttempts` | int | The configured cap before the item is failed permanently |

`work_item.recovered` fires even when `toState` is `"Failed"` (i.e. the cap was exceeded). Subscribe to this event to monitor crash recovery and alert when an item keeps crashing. See [`recovery.md`](recovery.md) for the full state-mapping rules and configuration.

### `agent_smoke_failed` details

When `event` is `agent.smoke_failed`, the `details` field is always populated.
`workItem` and `project` are `null` when the event fires at **startup** (no
work-item context). At **work-item pickup**, both `workItem` and `project` are
populated with the affected item and its project.

```json
{
  "details": {
    "agentKind": "claude",
    "reason": "auth",
    "occurredAt": "2026-04-29T12:00:00.000+00:00"
  }
}
```

| Field | Type | Description |
|---|---|---|
| `agentKind` | string | Agent whose credential failed (e.g. `"claude"`, `"codex"`) |
| `reason` | string\|null | `"auth"` for 401/403, `"transient: try later"` for 5xx/network errors, `"timeout"` if the probe timed out, `"no token"` if no credential is configured |
| `occurredAt` | ISO-8601 | When the failure was recorded |

`agent.smoke_failed` can fire at **startup** (no `workItem`, no `project`) or
at **work-item pickup** (a subsequent `work_item.failed` event also fires and
carries the work-item context). Subscribe to `agent.smoke_failed` to alert on
credential problems independently of whether any work items were affected.

---

### `sandbox_leak` details

When `event` is `sandbox.leak_detected`, `sandbox.leak_disposed`, or
`sandbox.leak_dispose_failed`, the `details` field carries the sandbox's
diagnostic information. `workItem` and `project` are always `null` — leaked
sandboxes are not associated with a specific work item.

```json
{
  "details": {
    "name": "codeybox-a1b2c3d4e5f6",
    "ageMinutes": 127.3,
    "diskMb": null,
    "reason": "untracked_sandbox_age_threshold_exceeded"
  }
}
```

| Field | Type | Present on | Description |
|---|---|---|---|
| `name` | string | all | VM name matching the `codeybox-*` prefix |
| `ageMinutes` | number | all | Age of the sandbox in minutes at detection time |
| `diskMb` | number\|null | all | Disk usage in MiB, if available; null otherwise |
| `reason` | string | all | Stable classification reason code (added in event schema `1.1`), e.g. `untracked_sandbox_age_threshold_exceeded` or `untracked_sandbox_missing_creation_metadata` |
| `disposedAt` | ISO-8601 | `sandbox.leak_disposed` | Timestamp when the sandbox was successfully disposed |
| `error` | string | `sandbox.leak_dispose_failed` | Human-readable failure reason (e.g. `"timeout"` or multipass error) |

---

## Intermediate progress events

In addition to the terminal `work_item.<state>` transitions, the pipeline emits
fine-grained progress events at every internal state-machine boundary. Trackers
(Jira, Linear, etc.) can subscribe to these to surface live progress comments
on issues without polling the audit-findings or work-item endpoints.

All events are **additive**: existing subscribers that filter on `work_item.*`
keep working unchanged. To opt in, add the desired event names to the endpoint's
`EventFilter` array. Receivers should ignore unknown event names so future
additions don't require downstream changes.

The events fire in this order across one successful lifecycle:

```
iteration.started(work)
iteration.completed(work)
audit.started
audit.findings.emitted
audit.completed             ← repeats from iteration.started(rework) if blocking findings
merge.started
merge.completed
```

When audit iteration N produces blocking findings and rework is permitted,
`iteration.started(rework)` and `iteration.completed(rework)` fire with
`iteration = N + 1` (the rework is the "next attempt", evaluated by audit
iteration N+1). The audit phase events use the audit iteration number directly.

### `iteration.started` details

```json
{
  "details": {
    "workItemId": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
    "iteration": 1,
    "phase": "work",
    "dispatchedAt": "2026-05-18T12:00:00.000+00:00"
  }
}
```

| Field | Type | Description |
|---|---|---|
| `workItemId` | string | UUID of the work item |
| `iteration` | int | 1-indexed iteration number; aligns with the audit iteration that will evaluate this attempt |
| `phase` | string | `"work"` for the initial attempt, `"rework"` for subsequent attempts driven by audit findings |
| `dispatchedAt` | ISO-8601 | When the iteration was dispatched to the agent |

### `iteration.completed` details

```json
{
  "details": {
    "workItemId": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
    "iteration": 1,
    "phase": "work",
    "commitSha": "abc123def456...",
    "durationMs": 18234,
    "success": true
  }
}
```

| Field | Type | Description |
|---|---|---|
| `workItemId` | string | UUID of the work item |
| `iteration` | int | Matches the `iteration` from the paired `iteration.started` |
| `phase` | string | `"work"` or `"rework"` |
| `commitSha` | string\|null | Tip of the work branch after the iteration committed; null when the host could not resolve it |
| `durationMs` | int | Wall-clock duration from `iteration.started` to this event |
| `success` | bool | `true` when the iteration produced a commit. Failed iterations surface via `work_item.failed` and do not fire this event today. |

### `audit.started` details

```json
{
  "details": {
    "workItemId": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
    "iteration": 1,
    "auditorsScheduled": ["build", "lint", "llm-review"]
  }
}
```

| Field | Type | Description |
|---|---|---|
| `workItemId` | string | UUID of the work item |
| `iteration` | int | Audit iteration number, 1-indexed |
| `auditorsScheduled` | string[] | Names of the auditors that will run this iteration, in stable order |

### `audit.findings.emitted` details

```json
{
  "details": {
    "workItemId": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
    "iteration": 1,
    "findings": [
      {
        "auditor": "build",
        "severity": "Error",
        "title": "compilation failed",
        "location": "src/Foo.cs:42",
        "description": "expected ';' after expression"
      }
    ],
    "blocking": 1,
    "nonBlocking": 0
  }
}
```

| Field | Type | Description |
|---|---|---|
| `workItemId` | string | UUID of the work item |
| `iteration` | int | Audit iteration number |
| `findings` | array | Full finding list. May be empty when the audit produced no findings. |
| `findings[].auditor` | string | Name of the auditor that produced the finding |
| `findings[].severity` | string | `"Info"`, `"Warning"`, or `"Error"` |
| `findings[].title` | string | Short label suitable as a comment heading |
| `findings[].location` | string\|null | `path:line` hint, or `null` when the auditor didn't point at a location |
| `findings[].description` | string | Free-form description of the finding |
| `blocking` | int | Findings whose severity is ≥ the project's `FailingSeverity` |
| `nonBlocking` | int | Findings below the failing severity |

`audit.findings.emitted` fires for every iteration, including ones with zero
findings — receivers can use it as a "this iteration emitted no comments" signal.

### `audit.completed` details

```json
{
  "details": {
    "workItemId": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
    "iteration": 1,
    "verdict": "fail",
    "durationMs": 7123
  }
}
```

| Field | Type | Description |
|---|---|---|
| `workItemId` | string | UUID of the work item |
| `iteration` | int | Audit iteration number |
| `verdict` | string | `"pass"` when no blocking findings; `"fail"` when blocking findings drove rework or the final-iteration failure |
| `durationMs` | int | Wall-clock duration from `audit.started` to this event |

### `merge.started` details

```json
{
  "details": {
    "workItemId": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
    "baseBranch": "main",
    "workBranch": "codeybox/a1b2c3d4"
  }
}
```

### `merge.completed` details

```json
{
  "details": {
    "workItemId": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
    "baseBranch": "main",
    "workBranch": "codeybox/a1b2c3d4",
    "mergeSha": "abc123def456...",
    "conflicts": null
  }
}
```

| Field | Type | Description |
|---|---|---|
| `workItemId` | string | UUID of the work item |
| `baseBranch` | string | Branch the work branch was merged into |
| `workBranch` | string | The merged feature branch |
| `mergeSha` | string\|null | Resulting merge commit on the work branch |
| `conflicts` | string[]\|null | Conflicted paths the agent resolved; currently always `null` (reserved for future surface) |

---

## Request headers

| Header | Value |
|---|---|
| `Content-Type` | `application/json; charset=utf-8` |
| `X-CodeyBox-Event` | Event name, e.g. `work_item.done` |
| `X-CodeyBox-Delivery` | Random UUID, unique per delivery attempt batch |
| `X-CodeyBox-Schema-Version` | Event-payload schema version (semver), e.g. `1.1`. See [`EVENT_SCHEMA.md`](EVENT_SCHEMA.md) for evolution rules. |
| `X-CodeyBox-Signature` | `sha256=<hex>` — only present when `SecretEnvVar` is configured |

---

## Signing (HMAC-SHA256)

When `SecretEnvVar` is set, CodeyBox computes an HMAC-SHA256 over the **raw UTF-8 request body** using the secret read from that environment variable, then sends it in the `X-CodeyBox-Signature` header as `sha256=<lowercase hex>`.

### Verification example (Python)

```python
import hashlib, hmac

def verify(secret: str, body: bytes, signature_header: str) -> bool:
    expected = "sha256=" + hmac.new(
        secret.encode("utf-8"),
        body,
        hashlib.sha256,
    ).hexdigest()
    return hmac.compare_digest(expected, signature_header)
```

### Verification example (Node.js)

```js
const crypto = require("crypto");

function verify(secret, bodyBuffer, signatureHeader) {
  const expected = "sha256=" + crypto
    .createHmac("sha256", secret)
    .update(bodyBuffer)
    .digest("hex");
  return crypto.timingSafeEqual(
    Buffer.from(expected),
    Buffer.from(signatureHeader),
  );
}
```

Always use a constant-time comparison (`hmac.compare_digest` / `timingSafeEqual`) to avoid timing attacks.

---

## Configuration

Add a `Webhooks` array inside the `CodeyBox` config section. Each entry configures one endpoint.

```json
{
  "CodeyBox": {
    "Webhooks": [
      {
        "Name": "slack-notifications",
        "Url": "https://hooks.slack.com/services/...",
        "SecretEnvVar": "WEBHOOK_SECRET_SLACK",
        "EventFilter": ["work_item.done", "work_item.failed", "work_item.audit_failed"],
        "MaxAttempts": 3,
        "InitialBackoffSeconds": 1,
        "TimeoutSeconds": 10
      },
      {
        "Name": "audit-log",
        "Url": "https://audit.example.com/codeybox",
        "SecretEnvVar": "WEBHOOK_SECRET_AUDIT",
        "MaxAttempts": 5,
        "InitialBackoffSeconds": 2,
        "TimeoutSeconds": 15
      }
    ]
  }
}
```

### Field reference

| Field | Required | Default | Description |
|---|---|---|---|
| `Name` | yes | — | Human label used in logs. Must be unique. |
| `Url` | yes | — | HTTPS (or HTTP) URL to POST events to. |
| `SecretEnvVar` | no | — | Name of the env var holding the HMAC key. Omit to send unsigned. |
| `EventFilter` | no | `[]` | List of event names to deliver. Empty = deliver ALL events. |
| `MaxAttempts` | no | `3` | Delivery attempts before giving up. |
| `InitialBackoffSeconds` | no | `1` | Seconds before first retry; doubles each attempt (1s, 2s, 4s, …). |
| `TimeoutSeconds` | no | `10` | Per-request HTTP timeout. |

The HMAC secret itself must **never** appear in config files. Put it in an environment variable and reference it by name via `SecretEnvVar`.

---

## Delivery semantics

- Delivery is **fire-and-forget** from the pipeline's perspective. Webhook failures never affect work-item state.
- The dispatcher runs a background channel-drain loop; `PublishAsync` enqueues and returns immediately.
- Failed deliveries are retried up to `MaxAttempts` times with exponential back-off. After that, a warning is logged and the delivery is abandoned.
- On graceful shutdown the dispatcher drains in-flight deliveries (up to 30 s timeout).
- When no endpoints are configured, a no-op dispatcher is used — zero overhead.

---

## Release events

These events fire during the release management lifecycle (opt-in per project;
see [`releases.md`](releases.md)).

| Event | Fired when |
|---|---|
| `release.created` | New release created via `POST /releases` |
| `release.closed` | Release transitioned `open → closed` |
| `release.abandoned` | Release was abandoned |
| `release.reopened` | Failed release was re-opened (`failed → open`) |
| `release.has_failed_work_items` | Release closed but some linked work items failed/cancelled |
| `release.in_review` | Release transitioned `closed → in_review`; deep audit starting |
| `release.deep_audit_iteration_complete` | One deep audit iteration finished (both pass and fail) |
| `release.deep_audit_remediation_dispatched` | Remediation work item auto-created for a deep-audit iteration |
| `release.work_item_added` | Work item linked to this release via `POST /workitems` with `releaseId` |
| `release.published` | Release merged to main; GitHub release created (if configured) |
| `release.failed` | Deep audit exceeded max iterations; human review required |
| `release.sync_conflict` | `ReleaseMainSyncService` detected a merge conflict merging `main` into a release branch |

### `release.sync_conflict` details

```json
{
  "event": "release.sync_conflict",
  "occurredAt": "...",
  "release": { "id": "...", "name": "v1.4.0", "state": "open", ... },
  "project": { ... },
  "details": {
    "sourceBranch": "main",
    "targetBranch": "release/v1.4.0"
  }
}
```

The conflict must be resolved manually. The sync service backs off for one
sweep interval before retrying (does not spam retries).

### `release.deep_audit_iteration_complete` details

```json
{
  "event": "release.deep_audit_iteration_complete",
  "details": {
    "iteration": 2,
    "maxIterations": 5,
    "blockingFindings": 3,
    "totalFindings": 7
  }
}
```
