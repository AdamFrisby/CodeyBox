# Agent Classes

An **agent class** is a named group of interchangeable agents. Instead of
binding a work item directly to `claude` or `codex`, you bind it to a class
such as `frontier-coding`. At pickup time the orchestrator probes each class
member in preference order, picks the first one above the quota threshold,
and falls back to peers if the preferred one is exhausted.

This solves two real problems:

1. **Wasted compute from mid-run rate-limits.** Starting a 30-minute Claude
   work item with 5 % quota left means the agent exits mid-task, consuming
   audit-sandbox time for nothing. Probing before starting prevents this.
2. **No peer fallback.** Even if Claude is exhausted, Codex is roughly
   equivalent and could finish the same task — but without agent classes
   there is no way to express that relationship.

---

## Agent class config

Classes are configured under `CodeyBox:AgentClasses` in `appsettings.json`:

```json
{
  "CodeyBox": {
    "AgentClasses": [
      {
        "Id": "frontier-coding",
        "DisplayName": "Frontier coding agents",
        "Members": [
          { "Agent": "claude", "Billing": "Subscription", "ModelId": "claude-opus-4-7" },
          { "Agent": "codex",  "Billing": "Subscription", "ModelId": "codex-5.5" },
          { "Agent": "claude", "Billing": "PayPerApi",    "ModelId": "claude-opus-4-7" }
        ]
      }
    ]
  }
}
```

### Fields

| Field | Required | Description |
|-------|----------|-------------|
| `Id` | yes | Stable identifier used in work items and projects. Case-insensitive. |
| `DisplayName` | no | Human label for logs. Defaults to `Id`. |
| `Members` | yes | One or more members in **preference order**. First member is tried first. |

### Member fields

| Field | Required | Description |
|-------|----------|-------------|
| `Agent` | yes | Agent kind value: `claude`, `codex`, `copilot`, or any custom kind. |
| `Billing` | yes | `Subscription` or `PayPerApi` (see below). |
| `ModelId` | no | Optional model override passed to the agent CLI. Reserved for CLIs that accept a `--model` flag; currently advisory. |

---

## Billing modes

Billing determines whether the orchestrator will wait when quota is low.

### `Subscription`

The quota is fixed per billing period (e.g. Claude Pro, Codex Plus). Running
a task when quota is near zero risks a mid-run rate-limit. The orchestrator
probes the usage endpoint before firing. If `AvailablePct < MinQuotaPct`
(default 10 %) the member is skipped; if all subscription members are
exhausted, the item is deferred and re-probed after `QuotaRecheckInterval`
(default 5 minutes).

### `PayPerApi`

Pure metered billing: every API call costs money but none fail due to a
quota cap. The orchestrator never waits for PayPerApi members.

**Best practice:** include at least one PayPerApi member as a final fallback
so items are never blocked indefinitely:

```json
{ "Agent": "claude", "Billing": "PayPerApi", "ModelId": "claude-opus-4-7" }
```

A startup warning is emitted when a class has only Subscription members.

---

## Routing algorithm

On every pickup attempt for a work item with an `AgentClassId`:

1. Resolve the class from the catalog (case-insensitive on `Id`).
2. For each member in preference order:
   - If `Billing = PayPerApi`: treat as available (no HTTP call).
   - Otherwise: call the registered `IAgentQuotaProbe` for the agent kind.
     - Probe result is cached per probe instance for `QuotaCacheTtl` (default
       60 s) to avoid hammering the endpoint on every pickup.
     - Unknown (`AvailablePct < 0`) → fail-open, treat as available.
   - If `AvailablePct ≥ MinQuotaPct` (or unknown): pick this member and stop.
3. If no member qualifies:
   - Class has at least one Subscription member → set `ShouldWait = true`,
     schedule re-enqueue after `QuotaRecheckInterval`, emit
     `quota_router.deferred` audit event.
   - Class has only PayPerApi members → fire the first member anyway (this
     path is unreachable in normal operation since PayPerApi probes always
     return 100 %).

When `AgentClassId` is null and the project has no `DefaultAgentClass`, the
router is skipped entirely — no probe call, no wait, identical to the
pre-quota-router behaviour.

---

## Quota probes

Two probes are bundled:

### `ClaudeQuotaProbe`

Calls `https://api.anthropic.com/api/oauth/usage` with
`Authorization: Bearer <CODEYBOX_CLAUDE_API_KEY>`.

Expected response shape:
```json
{ "usedTokens": 500000, "quotaTokens": 1000000, "resetAt": "2026-05-01T00:00:00Z" }
```

`AvailablePct = 100 × (1 − usedTokens / quotaTokens)`

### `CodexQuotaProbe`

Calls `https://api.openai.com/v1/usage` with
`Authorization: Bearer <CODEYBOX_CODEX_API_KEY>`.

Same response shape and normalisation formula.

### Fail-open guarantee

Both probes return `AvailablePct = -1` on:
- HTTP 4xx / 5xx
- Network error
- Unrecognised JSON shape
- Token not configured (`CODEYBOX_*_API_KEY` not set)

`AvailablePct = -1` is treated as "unknown → available", so a broken endpoint
never blocks work items.

---

## Quota router tuning

Configured under `CodeyBox:QuotaRouter`:

```json
{
  "CodeyBox": {
    "QuotaRouter": {
      "MinQuotaPct": 10,
      "QuotaRecheckIntervalSeconds": 300,
      "QuotaCacheTtlSeconds": 60
    }
  }
}
```

| Key | Default | Description |
|-----|---------|-------------|
| `MinQuotaPct` | `10` | Minimum available percentage before a Subscription member is skipped. |
| `QuotaRecheckIntervalSeconds` | `300` | Seconds to wait before re-probing when all Subscription members are exhausted. |
| `QuotaCacheTtlSeconds` | `60` | Seconds to cache a probe result. Keeps the pickup loop cheap under load. |

---

## Work item and project fields

### `WorkItem.AgentClassId`

Set via the `POST /workitems` API:

```json
{
  "title": "Refactor auth module",
  "prompt": "...",
  "projectId": "my-app",
  "agentClassId": "frontier-coding"
}
```

When set, the orchestrator routes via the named class. When null, falls back
to `Project.DefaultAgentClass`, then to direct `Agent` pick (legacy behaviour).

### `Project.DefaultAgentClass`

Set once per project in config so all work items inherit quota routing
without specifying `agentClassId` on every item:

```json
{
  "Id": "my-app",
  "DefaultAgentClass": "frontier-coding",
  ...
}
```

A per-item `AgentClassId` overrides the project default.

---

## Audit events

All routing decisions are emitted as `Audit=true` events:

| Event name | When |
|------------|------|
| `quota_router.probed` | After each probe call (agent, class, available %). |
| `quota_router.waiting` | When all Subscription members are exhausted. |
| `quota_router.deferred` | When the orchestrator schedules a deferred re-enqueue. |

---

## Startup validation

At startup the orchestrator validates the `AgentClasses` config:

- Each class `Id` is unique (case-insensitive).
- Each class has at least one member.
- Each member `Agent` is non-empty.
- Each member `Billing` is `Subscription` or `PayPerApi`.
- A class with only Subscription members emits a startup **warning**:
  > AgentClass 'X' has no PayPerApi fallback — items may wait indefinitely if all subscriptions are exhausted
