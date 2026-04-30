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

## Endpoints

### `POST /workitems`

Queue a new work item.

```json
{
  "projectId": "my-app",
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
* `dependsOn` — optional array of work item IDs. The item will not be
  picked up until every listed dependency has reached a terminal state
  (`Done`, `Failed`, `AuditFailed`, or `Cancelled`). Unknown IDs, self
  references, and cycles are rejected with `400`. See
  [`work-items.md`](work-items.md) for details.

Response: `201 Created` with the work item record.

### `GET /workitems`

List all work items, newest first.

### `GET /workitems/{id}`

Fetch a single work item by id (UUID).

### `GET /workitems/{id}/dependents`

List work items that directly depend on this one. Useful for inspecting
blast radius before cancelling. Returns the same record shape as
`GET /workitems/{id}`.

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

### `GET /healthz`

Liveness probe. Returns `{ "status": "ok" }`.

## Work item record

```json
{
  "id": "5b6e...",
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
  "queuePosition": 0
}
```

`prompt` is the full task text given to the agent (≤ 64 KB).

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
