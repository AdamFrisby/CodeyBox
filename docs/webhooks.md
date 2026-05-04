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
| `work_item.recovered` | Dead-worker reaper recovered a work item that was mid-flight when its worker crashed (see [Details](#recovered-details)) |
| `work_item.suggestion` | Agent emitted a suggestion (one event per suggestion entry; see [Details](#suggestion-details)) |

`work_item.audit_iteration` fires **after every audit iteration**, regardless of pass or fail, and carries per-iteration counts in the `details` field.

---

## Payload shape

Every event is a JSON object POSTed as the request body.

```json
{
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
  "details": null
}
```

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

### `recovered` details

When `event` is `work_item.recovered`, the `details` field is populated:

```json
{
  "details": {
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

## Request headers

| Header | Value |
|---|---|
| `Content-Type` | `application/json; charset=utf-8` |
| `X-CodeyBox-Event` | Event name, e.g. `work_item.done` |
| `X-CodeyBox-Delivery` | Random UUID, unique per delivery attempt batch |
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
