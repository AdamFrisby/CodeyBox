# REST API

The orchestrator exposes a minimal HTTP API for queueing and inspecting work.

## Authentication

All endpoints except `GET /healthz` require a bearer token.

Configure: set `CODEYBOX_API_KEY` to a 32+ character random secret. The
service refuses to start without it. Send it on every request:

```
Authorization: Bearer <CODEYBOX_API_KEY>
```

Tokens are compared in constant time. `WWW-Authenticate: Bearer` is
returned on 401.

For local-machine development you can opt out by setting
`CodeyBox:DangerouslyDisableAuth=true`. **Do not do this on a host
reachable from anywhere but loopback** — the orchestrator can spawn
sandboxes and trigger merges, so anyone who can hit the API can run
arbitrary work items.

The default Kestrel bind is `127.0.0.1:5000`. To expose externally,
front it with a TLS-terminating reverse proxy (nginx, Caddy, …) and set
`ASPNETCORE_URLS` to the local address the proxy connects to.

## Observability

When OpenTelemetry is enabled (see [`observability.md`](observability.md)), all incoming HTTP requests are automatically traced as spans via `AspNetCore` instrumentation. The `traceparent` response header is set on every request so callers can correlate client-side traces with server-side spans.

## Endpoints

### `POST /workitems`

Queue a new work item.

```json
{
  "projectId": "my-app",
  "externalId": "JIRA-1234",
  "title": "Add JSON config support",
  "prompt": "Add a --config flag that reads settings from a JSON file. Update README.",
  "agent": null,
  "baseBranch": null,
  "workBranch": null,
  "pushUpstream": true,
  "dependsOn": []
}
```

* `projectId` — required. Must match a configured project (see
  [`projects.md`](projects.md)). Unknown ids are rejected.
* `externalId` — optional caller-supplied identifier (e.g. `"JIRA-1234"`,
  `"GH-456"`, `"sprint-7:ticket-99"`). Must be 1–256 ASCII printable
  characters (no whitespace, no `/`, no `?`, no `;` `<` `=` `>`), must not start with `wi-`,
  must not be a UUID. Unique per project. Rejected with `400` on
  duplicate. See [`external-ids.md`](external-ids.md) for the full contract.
* `title` — short label, ≤ 200 chars, no leading dash, no control chars.
* `prompt` — what to give to the agent. ≤ 64 KB.
* `agent` — optional override. `"claude"`, `"copilot"`, `"codex"`, `"gemini"`, or any
  kind registered with the `IAgentRegistry`. Defaults to the project's
  configured agent. Unknown agents are rejected with the list of
  available kinds.
* `baseBranch` — optional override. Defaults to the project's
  `BaseBranch` (or the resolved default branch). Conservative branch-name
  rules apply (ASCII alnum + `._/-`, no leading dash, no `..`, no
  `.lock`).
* `workBranch` — defaults to `codeybox/<short id>`. Must differ from
  `baseBranch` (the merge sandbox is the agent containment boundary; a
  caller cannot bypass it by aliasing the two).
* `pushUpstream` — if `true` *and* the project has an upstream configured,
  push to it after merge.
* `dependsOn` — optional array of work item IDs **or externalIds**. Each
  entry is resolved: if it parses as a UUID it is looked up by internal ID;
  otherwise it is looked up by `externalId` within the same project. This
  allows batching dependent items without waiting for UUID responses — see
  [`external-ids.md`](external-ids.md#dependency-batching-without-round-trips).
  Unknown IDs, self references, and cycles are rejected with `400`. See
  [`work-items.md`](work-items.md) for details.

Response: `201 Created` with the work item record.

### `GET /workitems`

List all work items, newest first.

### `GET /workitems/{id}`

Fetch a single work item. The `{id}` path segment accepts either:

- A UUID: `GET /workitems/abcd-1234-...`
- A composite `<projectId>:<externalId>`: `GET /workitems/my-app:JIRA-1234`

The composite form is unambiguous and works with all endpoints that accept `{id}`
(`GET`, `DELETE`, `PATCH /workitems/{id}`, `POST /workitems/{id}/retry`,
`GET /workitems/{id}/dependents`, `GET /workitems/{id}/timeline`, etc.).

Returns `400 Bad Request` when the colon form has an empty project or externalId part.
Returns `404 Not Found` when the project exists but has no item with that externalId.

### `GET /workitems/{id}/dependents`

List work items that directly depend on this one. Useful for inspecting
blast radius before cancelling. Returns the same record shape as
`GET /workitems/{id}`.

### `GET /workitems/{id}/timeline`

Replay the audit-log events for a work item as a structured timeline.

**Query parameters** (all optional):

| Param | Description |
|-------|-------------|
| `kind` | Comma-separated list of event kinds to include (`state_transition`, `agent_started`, `agent_finished`, `auditor_run`, `iteration_complete`, `webhook_delivered`). Omit for all kinds. |
| `since` | ISO-8601 timestamp. Only events at or after this time are returned. |
| `iteration` | Integer. Only `auditor_run` and `iteration_complete` events for this audit iteration number are returned. |

**Response** `200 OK`:

```json
{
  "workItemId": "aabbccdd00000000000000000000001a",
  "entries": [
    {
      "occurredAt": "2026-05-01T10:00:00+00:00",
      "kind": "state_transition",
      "summary": "Created (Queued): Add JSON config support",
      "details": { "from": null, "to": "Queued", "title": "Add JSON config support" }
    },
    {
      "occurredAt": "2026-05-01T10:00:05+00:00",
      "kind": "agent_started",
      "summary": "claude (work) started",
      "details": { "agent": "claude", "phase": "work", "sandbox": "vm-abc123" }
    },
    {
      "occurredAt": "2026-05-01T10:03:12+00:00",
      "kind": "agent_finished",
      "summary": "claude succeeded in 3m 7s",
      "details": {
        "agent": "claude", "success": true, "exitCode": null,
        "durationMs": 187000, "stdoutTail": "All done.", "stderrTail": "", "sandbox": "vm-abc123"
      }
    },
    {
      "occurredAt": "2026-05-01T10:03:15+00:00",
      "kind": "auditor_run",
      "summary": "csharp:format-check (iter 1) — 0 findings",
      "details": { "name": "csharp:format-check", "iteration": 1, "severity": "None", "durationMs": 4200 }
    },
    {
      "occurredAt": "2026-05-01T10:03:16+00:00",
      "kind": "iteration_complete",
      "summary": "Audit iteration 1 of 3: 0 blocking, 0 non-blocking",
      "details": { "iteration": 1, "totalIterations": 3, "blocking": 0, "nonBlocking": 0 }
    }
  ]
}
```

Entries are sorted chronologically. The endpoint streams the audit log
files line-by-line; it never loads a full log file into memory.

**Caching**: timelines for work items in a terminal state (`Done`,
`Failed`, `Cancelled`, `AuditFailed`) are cached in memory after the
first request. In-flight items are re-read from disk on every call.

* Returns `400 Bad Request` when `id` is not a valid UUID.
* Returns `404 Not Found` when the work item does not exist.

### `GET /workitems/{id}/audit-reports`

Returns all stored per-auditor reports for a work item, grouped by
iteration. See [`audit-reports.md`](audit-reports.md) for the full
schema and semantics.

```json
{
  "workItemId": "...",
  "iterations": [
    {
      "iteration": 1,
      "blockingCount": 2,
      "nonBlockingCount": 1,
      "auditors": [
        {
          "name": "DiffPatternAuditor",
          "kind": "diff-pattern",
          "worstSeverity": "Error",
          "durationMs": 120,
          "rawOutputAvailable": true,
          "findings": [
            {
              "id": "f-a1b2c3d4",
              "severity": "Error",
              "title": "Missing null check",
              "message": "The foo method does not validate its input.",
              "files": ["src/Foo.cs"],
              "lineHints": [42]
            }
          ]
        }
      ]
    }
  ]
}
```

`blockingCount` counts Error-severity findings; `nonBlockingCount`
counts all others. `rawOutputAvailable` is true when raw auditor output
is stored; fetch it via the `/raw` endpoint below.

* Returns `400 Bad Request` when `id` is not a valid UUID.
* Returns `404 Not Found` when the work item does not exist.
* Returns an empty `iterations` array when no reports have been
  persisted yet (e.g. the work item has not yet entered the audit phase).

### `GET /workitems/{id}/audit-reports/{iteration}/{auditor}/raw`

Returns the raw stdout/stderr captured from a single auditor invocation
as `text/plain; charset=utf-8`.

The output is pre-redacted (GitHub PATs, Anthropic API keys, and Google
API keys replaced with `***`) and capped at 256 KB. A `[...truncated]`
suffix is appended when the original exceeded the cap.

* Returns `404 Not Found` when the work item, iteration, or auditor row
  does not exist, or when `raw_output` is `NULL` for that row.

### `GET /workitems/{id}/costs`

Returns token usage and estimated cost data for a single work item, aggregated
by phase and by agent.  See [`cost-reporting.md`](cost-reporting.md) for the
cost model and pricing configuration.

**Response (200 OK):**

```json
{
  "workItemId": "aabbccdd00000000000000000000001a",
  "totals": {
    "inputTokens": 12345,
    "cachedInputTokens": 3000,
    "outputTokens": 678,
    "estimatedUsd": 0.0234
  },
  "byPhase": {
    "work": {
      "inputTokens": 8000,
      "cachedInputTokens": 2000,
      "outputTokens": 400,
      "estimatedUsd": 0.015,
      "byIteration": []
    },
    "audit": {
      "inputTokens": 4345,
      "cachedInputTokens": 1000,
      "outputTokens": 278,
      "estimatedUsd": 0.0084,
      "byIteration": [
        { "iteration": 1, "inputTokens": 4345, "cachedInputTokens": 1000, "outputTokens": 278, "estimatedUsd": 0.0084 }
      ]
    }
  },
  "byAgent": [
    {
      "agent": "claude",
      "modelId": "claude-sonnet-4-6",
      "inputTokens": 12345,
      "cachedInputTokens": 3000,
      "outputTokens": 678,
      "estimatedUsd": 0.0234
    }
  ]
}
```

`byPhase` contains only phases for which at least one cost row exists.
`byIteration` within a phase lists per-iteration subtotals (used for `rework`
and `audit` phases; empty for `work` and `merge`).

* Returns `200 OK` with zero-valued totals and empty breakdowns when the work
  item exists but no cost rows have been recorded yet.
* Returns `404 Not Found` when the work item does not exist.
* Returns `400 Bad Request` when `{id}` is not a valid UUID.

### `GET /projects/{id}/costs`

Returns aggregated token usage and estimated cost for all work items in a
project, optionally restricted to a time window.

**Query parameters:**

| Param | Description |
|-------|-------------|
| `from` | ISO-8601 timestamp (inclusive). Defaults to 30 days ago. |
| `to`   | ISO-8601 timestamp (inclusive). Defaults to now. |

**Response (200 OK):**

```json
{
  "projectId": "my-app",
  "from": "2026-04-01T00:00:00+00:00",
  "to": "2026-05-01T00:00:00+00:00",
  "totals": {
    "inputTokens": 500000,
    "cachedInputTokens": 120000,
    "outputTokens": 30000,
    "estimatedUsd": 4.56
  },
  "byAgent": [
    {
      "agent": "claude",
      "modelId": null,
      "inputTokens": 500000,
      "cachedInputTokens": 120000,
      "outputTokens": 30000,
      "estimatedUsd": 4.56
    }
  ],
  "byWorkItem": [
    {
      "workItemId": "aabbccdd00000000000000000000001a",
      "inputTokens": 12345,
      "cachedInputTokens": 3000,
      "outputTokens": 678,
      "estimatedUsd": 0.0234
    }
  ]
}
```

`byAgent` rows have `modelId: null` because the project-level rollup may
span multiple models.  `byWorkItem` is sorted by `started_at` descending.

* Returns `404 Not Found` when the project does not exist.
* Returns `400 Bad Request` when `from` or `to` cannot be parsed as ISO-8601.

### `GET /workitems/{id}/stdout-tail`

Returns the recent tail of the agent's live stdout, buffered in the orchestrator's
in-memory ring buffer (capped at 16 KB). Useful for late-joining dashboard clients
that missed the beginning of a stream. Returns `text/plain; charset=utf-8`.

The content is pre-redacted: GitHub PATs, Anthropic API keys, and Google API keys
are replaced with `***` before the output ever reaches the buffer.

* Returns `200 OK` with the buffered tail as `text/plain`. Response body is empty
  when the ring buffer has no entry for this work item (item hasn't started, or
  orchestrator was restarted since the run).
* Returns `400 Bad Request` when `{id}` is not a valid GUID.
* Returns `404 Not Found` when the work item does not exist.

**Note:** This endpoint returns a static snapshot. For a live stream use the
SignalR hub below.

### `GET /workitems/{id}/timings`

Returns per-step wall-clock timing data for a single work item as a structured
tree grouped by phase and step.  See [`timings.md`](timings.md) for the full
response shape and field descriptions.

* Returns `200 OK` with the timing tree.
* Returns `404 Not Found` when the work item does not exist.
* Returns `400 Bad Request` when `{id}` is not a valid GUID.

### `GET /workitems/timings/aggregate`

Returns aggregate statistics (median and p95) per step across the last *n*
completed work items.  Default `n` = 50; max `n` = 500.  The query streams the
SQLite cursor rather than loading all rows into memory.

**Query parameters:**

| Param | Description |
|-------|-------------|
| `n` | Number of completed work items to include (default 50, max 500). |

**Response (200 OK):** step-stat array.  See [`timings.md`](timings.md) for
the full schema.

### `DELETE /workitems/{id}`

Cancel a non-terminal work item.

* If the item is currently being processed by a worker, its
  `CancellationToken` is signalled. The pipeline catches the cancellation,
  tears down the live sandbox via `IAsyncDisposable`, and transitions the
  item to `Cancelled`.
* If the item is queued but not yet picked up, it's marked `Cancelled`
  directly; the worker's pre-run check skips it.
* All `Queued` items that transitively depend on this one are also
  transitioned to `Cancelled` (`lastError = "parent dependency cancelled"`).
  In-flight dependents are left to run their course.

Returns `202 Accepted`.

### `PATCH /workitems/{id}`

Partially update a **Queued** work item's `title`, `prompt`, and/or `agent`.
Only fields provided (non-null) in the body are updated.

```json
{
  "title": "optional new title",
  "prompt": "optional new prompt text",
  "agent": "optional agent override"
}
```

* Returns `200 OK` with the updated work item record.
* Returns `409 Conflict` when the item is not in `Queued` state (in-flight items are read-only).
* Validation rules for `title`, `prompt`, and `agent` are identical to `POST /workitems`.

### `POST /workitems/reorder`

Reorder the set of **Queued** work items. The body must list **exactly** the current queued item IDs (no more, no fewer). Any mismatch indicates a stale view and is rejected with `400`.

```json
{
  "ids": ["<id1>", "<id2>", "<id3>"]
}
```

* Returns `204 No Content` on success.
* Returns `400 Bad Request` with `missingFromRequest` and `unknownInRequest` arrays when the provided ID set does not exactly match the current Queued items.
* The order of `ids` sets the new queue priority: index 0 = highest priority.
* Items not in `Queued` state are unaffected even if their IDs appear in the body (the store's conditional update skips non-Queued rows).

### `GET /projects`

List all configured projects.

Response: `200 OK` with a JSON array of project records:

```json
[
  {
    "id": "my-app",
    "displayName": "My App",
    "repositoryUrl": "/repos/my-app",
    "defaultBaseBranch": "main",
    "defaultAgent": "claude",
    "upstreamKind": "None",
    "auditLanguages": [],
    "auditTypes": [],
    "auditMaxIterations": 3
  }
]
```

### `GET /projects/{id}`

Fetch a single project by its id.

* Returns `200 OK` with the project record.
* Returns `400 Bad Request` if `id` is not a valid project identifier.
* Returns `404 Not Found` if the project does not exist.

### `GET /workers`

List currently-registered worker slots from the heartbeat registry. Useful for operator-grade introspection of what the process is currently doing, and for diagnosing stale rows after a crash.

Response: `200 OK` with a JSON array:

```json
[
  {
    "workerId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
    "hostName": "codeybox-host-01",
    "processId": 12345,
    "startedAt": "2026-05-04T10:00:00.000+00:00",
    "lastHeartbeatAt": "2026-05-04T10:05:15.000+00:00",
    "currentWorkItemId": "a1b2c3d4-e5f6-7890-abcd-ef1234567890"
  }
]
```

| Field | Description |
|---|---|
| `workerId` | GUID unique to this process start |
| `hostName` | Hostname of the machine running the worker |
| `processId` | OS process ID |
| `startedAt` | When this worker slot registered |
| `lastHeartbeatAt` | When the heartbeat last fired (updated every `HeartbeatInterval`, default 15 s) |
| `currentWorkItemId` | UUID of the work item being processed, or `null` if none |

An empty array means no workers are currently registered. A row with a stale `lastHeartbeatAt` means the worker process has crashed and the dead-worker reaper will recover it on the next sweep (or has already done so and the row wasn't cleaned up). See [`recovery.md`](recovery.md) for the full reaper design.

### `GET /sandboxes/leaked`

Returns the list of `codeybox-*` Multipass VMs that were detected as leaked on
the most recent reaper sweep (default every 15 minutes). An empty array means
no leaks were found on the last sweep.

```json
[
  {
    "name": "codeybox-a1b2c3d4e5f6",
    "createdAt": "2026-05-04T02:00:00+00:00",
    "ageMinutes": 127.3,
    "diskMb": null
  }
]
```

See [`sandbox-leaks.md`](sandbox-leaks.md) for full leak detection semantics.

### `POST /sandboxes/leaked/{name}/dispose`

Operator-triggered dispose of a leaked sandbox by name. The name must start with
`codeybox-`. Returns `{ "disposed": "<name>" }` on success.

* Returns `400` if the name does not start with `codeybox-`.
* Returns `404` if the sandbox is not present in the latest leaked list (use `GET /sandboxes/leaked` to verify it is detected as a leak before calling).
* Returns `504` if the dispose times out (5-minute per-sandbox cap).
* Returns `500` on other errors.

### `GET /healthz`

Liveness probe. Returns `{ "status": "ok" }`.

### `POST /projects/{id}/release`

Generate a CHANGELOG.md entry for a release. Enumerates merged PRs between
`fromTag` and `toTag`, calls the configured LLM to categorise and summarise
them, and returns the generated markdown immediately.

Requires the project to have a GitHub upstream configured with a valid PAT.

```json
{
  "fromTag": "v1.2.0",
  "toTag":   "v1.3.0"
}
```

**Response (200 OK):**

```json
{
  "markdown": "## [v1.3.0] - 2026-05-15\n\n### Added\n- ...\n",
  "categoryToPrNumbers": {
    "Added": [16, 18],
    "Fixed": [17]
  },
  "wasCapped": false
}
```

`wasCapped: true` means the release contained more than 200 PRs and the
oldest were omitted.

* Returns `400 Bad Request` when `fromTag` or `toTag` is missing, the
  project has no GitHub upstream configured, or changelog automation is
  disabled for the project.
* Returns `404 Not Found` when the project does not exist.

See [`changelog-automation.md`](changelog-automation.md) for configuration
and the webhook flow.

### `POST /webhooks/github/release`

Receive a GitHub `release` webhook event. Validates the
`X-Hub-Signature-256` HMAC, resolves the project by repository URL,
generates a changelog entry, and creates a work item to apply it to
`CHANGELOG.md`. Returns `202 Accepted` immediately; the work item runs
through the normal pipeline.

This endpoint is **exempt from API-key authentication**. It validates the
GitHub HMAC instead.

* A missing or invalid `X-Hub-Signature-256` returns `401` with no body.
* Non-`release` event types (e.g. `ping`) return `202 Accepted` silently.
* Non-`published`/`released` actions (e.g. `deleted`) return `202 Accepted` silently.
* Repositories not matching any configured project return `202 Accepted` silently.

See [`changelog-automation.md`](changelog-automation.md) for setup instructions.

## SignalR hub — live agent stdout

The orchestrator exposes a SignalR hub at `/hubs/agent-stdout` for streaming
agent output in real time to connected clients.

### Authentication

The hub endpoint is protected by the same bearer-token middleware as the REST
API. Pass the token as a request header on the WebSocket upgrade:

```
Authorization: Bearer <CODEYBOX_API_KEY>
```

### Hub methods (client → server)

| Method | Arguments | Description |
|--------|-----------|-------------|
| `SubscribeAsync` | `workItemId: string` | Join the group for a work item. |
| `UnsubscribeAsync` | `workItemId: string` | Leave the group for a work item. |

### Hub events (server → client)

| Event | Payload | Description |
|-------|---------|-------------|
| `stdoutChunk` | `{ workItemId, phase, chunk }` | A batch of stdout. `phase` is `"work"`, `"merge"`, or the audit phase name. `chunk` is a pre-redacted UTF-8 string. Chunks are debounced: at most one push per 100 ms or 4 KB, whichever comes first. |
| `streamComplete` | `{ workItemId }` | Fired when the pipeline exits (success or failure). No more chunks will follow for this work item. |

### Late-joining clients

Clients that connect after an agent has already started can fetch the recent tail
from `GET /workitems/{id}/stdout-tail` and then subscribe to the hub to receive
subsequent chunks.

### Security

Secret patterns are redacted **before** they reach the ring buffer or the hub:
GitHub PATs (`gho_*`, `ghp_*`, `github_pat_*`), Anthropic keys (`sk-ant-*`),
and Google API keys (`AIza…`) are replaced with `***`. Unknown secret formats
are **not** redacted — treat agent stdout as potentially sensitive.

The orchestrator never broadcasts the work item prompt over the hub.

## Work item record

```json
{
  "id": "5b6e...",
  "externalId": "JIRA-1234",
  "projectId": "my-app",
  "title": "...",
  "prompt": "Add a --config flag …",
  "agent": "claude",
  "baseBranch": "main",
  "workBranch": "codeybox/5b6e7c41",
  "state": "Working",
  "createdAt": "2026-04-27T10:18:11+00:00",
  "updatedAt": "2026-04-27T10:18:14+00:00",
  "lastError": null,
  "upstreamPushAttempts": 0,
  "dependsOn": [],
  "dependsOnSatisfied": true,
  "dependsOnExternalIds": {},
  "queuePosition": 0
}
```

`externalId` is the caller-supplied identifier, or `null` when not provided. See
[`external-ids.md`](external-ids.md) for the validation rules and usage patterns.

`prompt` is the full task text given to the agent (≤ 64 KB).

`dependsOnExternalIds` is a map of dependency UUID → externalId for each item in
`dependsOn`. Values are `null` for dependencies that have no externalId. Useful
for displaying human-readable dependency labels.

`queuePosition` is an ordering hint for Queued items set by `POST /workitems/reorder`. Smaller values sort first. Items not yet explicitly reordered have a position derived from their creation timestamp and sort after explicitly positioned items.

`state` is one of: `Queued`, `Working`, `WorkComplete`, `Auditing`,
`AuditPassed`, `Reworking`, `AuditFailed`, `Merging`, `Merged`,
`UpstreamPushing`, `Done`, `Failed`, `Cancelled`. Audit states only
appear when the deployment has registered auditors (see
[`audit.md`](audit.md)).

`dependsOn` lists the IDs of work items this item depends on.
`dependsOnSatisfied` is `true` when all dependencies are in a terminal
state (or when there are no dependencies). See [`work-items.md`](work-items.md).

## Configuration

`appsettings.json` (or env vars `CodeyBox__*`):

```json
{
  "CodeyBox": {
    "SandboxProvider": "multipass",
    "GitRootDirectory": "/var/lib/codeybox/repos",
    "StateDatabasePath": "/var/lib/codeybox/state.db",
    "SandboxImageReference": "codeybox/agent@sha256:…",
    "Concurrency": 2,
    "UpstreamPushMaxAttempts": 5,
    "UpstreamPushBackoffSeconds": 15,
    "SandboxNetworkProfiles": {
      "isolated": "cb-iso",
      "claude":   "cb-claude",
      "internet-only": "cb-net"
    },
    "Defaults": { "Agent": "claude", "BaseBranch": "main" },
    "Projects": [
      { "Id": "my-app", "RepositoryUrl": "...", "Upstream": { "Kind": "github", "TokenEnvVar": "MY_APP_GITHUB_TOKEN", "...": "..." } }
    ]
  }
}
```

Per-project config (upstream, audit policy, per-phase network profiles)
lives under `Projects[]` — see [`projects.md`](projects.md). Host-side
network profile setup lives in [`host-firewall.md`](host-firewall.md).

Secrets come from environment variables (never `appsettings.json`):

* `CODEYBOX_API_KEY` (REST bearer token)
* `CODEYBOX_CLAUDE_API_KEY`
* `CODEYBOX_COPILOT_TOKEN`
* `CODEYBOX_CODEX_API_KEY`
* The env var named in each project's `Upstream.TokenEnvVar` (per-project
  upstream credentials — never shared across projects).
