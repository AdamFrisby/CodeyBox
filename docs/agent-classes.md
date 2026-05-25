# Agent Classes

An **agent class** is a named group of interchangeable agents. Instead of
binding a work item directly to `claude` or `codex`, you bind it to a class
such as `frontier-coding`. At pickup time the orchestrator scores each class
member by capability, applies a small time-of-day modifier if configured, and
picks the highest-scoring member above the quota threshold.

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
          { "Agent": "claude", "Billing": "Subscription", "ModelId": "claude-opus-4-7", "QualityScore": 100 },
          { "Agent": "codex",  "Billing": "Subscription", "ModelId": "gpt-5.5",         "QualityScore": 100 },
          { "Agent": "gemini", "Billing": "Subscription", "ModelId": "gemini-3-flash-preview", "QualityScore": 95, "ReasoningMode": "high" },
          { "Agent": "claude", "Billing": "PayPerApi",    "ModelId": "claude-opus-4-7", "QualityScore": 100 }
        ]
      }
    ]
  }
}
```

The JSON order no longer determines preference (effective score does); it is
only a tiebreaker when scores are equal. Keep the obvious order for readability
— operators read the config.

### Fields

| Field | Required | Description |
|-------|----------|-------------|
| `Id` | yes | Stable identifier used in work items and projects. Case-insensitive. |
| `DisplayName` | no | Human label for logs. Defaults to `Id`. |
| `Members` | yes | One or more members. Order is a last-resort tiebreaker; `QualityScore` drives selection. |

### Member fields

| Field | Required | Description |
|-------|----------|-------------|
| `Agent` | yes | Agent kind value: `claude`, `codex`, `copilot`, `gemini`, or any custom kind. |
| `Billing` | yes | `Subscription` or `PayPerApi` (see below). |
| `ModelId` | no | Optional model override passed to the agent CLI as `--model`. |
| `QualityScore` | **yes** | Operator-curated capability score on a 0–200 scale. No silent default; startup rejects missing scores with a migration message. |
| `ReasoningMode` | no* | Agent CLI reasoning knob, e.g. `"high"`. *Required for Gemini members with `QualityScore` ≥ 90. |

---

## Quality scores

`QualityScore` is an operator-controlled integer (0–200) that encodes how
capable the member is relative to its peers. Higher = more capable.

### Recommended seed values

| Model | Score | Notes |
|-------|-------|-------|
| `claude-opus-4-7` | **100** | Frontier |
| `gpt-5.5` | **100** | Frontier, tied |
| Gemini 3 Flash (high reasoning) | **95** | Frontier-adjacent |
| `claude-sonnet-4-6`, GPT-5 base | **80** | Mid-tier |
| Gemini 3 Flash (standard) | **70** | Standard |
| Claude Haiku, mini variants | **50** | Economy |

These are starting points — adjust them freely. `QualityScore=100` on two
models means "interchangeable for this work; swap freely".

### How scores drive selection

1. **Floor filter.** Each work item carries a `MinModelScore` (default 95).
   Members whose *base* score is below the floor are excluded before any quota
   probe. The router never silently downgrades to a weaker model.
2. **Effective score.** Time-of-day modifiers (see below) are added to the base
   score to produce each member's *effective* score for this pickup.
3. **Sort.** Members are sorted descending by effective score. Ties are broken
   by billing (`Subscription` before `PayPerApi`), then original config order.
4. **Quota probe.** Members are probed in sorted order; the first one with
   sufficient quota wins.

The effective score of a member can drop *below* the floor after a TOD modifier
is applied — that is intentional. The floor check uses the *base* score because
TOD modifiers are preference-shaping tiebreakers, not capability gates. A model
with base score 95 remains eligible even if a −1 modifier makes its effective
score 94.

### No eligible member

If no member's base score meets the floor, the work item **fails immediately**
with error `ROUTING_NO_ELIGIBLE: no member of class '...' meets MinModelScore=N`.
The item is not retried; the operator must lower `MinModelScore` on the item or
add a capable member to the class.

---

## Time-of-day score modifiers

Small score deltas that fire during defined UTC time windows act as tiebreakers
between near-equivalent models. See `docs/configuration.md` for the full
`CodeyBox:AgentScoreModifiers` schema.

**Design intent:** a modifier of −1 is *only* enough to break a tie between two
models with equal base score (e.g. Opus 100 → effective 99 vs Codex 100 →
effective 100). It never demotes a genuinely superior model below an inferior
one (Opus eff 99 still beats Gemini eff 95). Modifiers are bounded to ±5 at
startup to prevent accidental gating.

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
{ "Agent": "claude", "Billing": "PayPerApi", "ModelId": "claude-opus-4-7", "QualityScore": 100 }
```

A startup warning is emitted when a class has only Subscription members.

---

## Routing algorithm

On every pickup attempt for a work item with an `AgentClassId`:

1. Resolve the class from the catalog (case-insensitive on `Id`).
2. **Filter** members to those whose base `QualityScore ≥ item.MinModelScore`.
   If none qualify, fail immediately with `ROUTING_NO_ELIGIBLE`.
3. **Compute effective scores**: `effective = base + sum(active TOD modifiers)`.
4. **Sort** descending by effective score. Ties: Subscription before PayPerApi,
   then original config order.
5. **Probe quota** in sorted order:
   - `PayPerApi` → treat as available (no HTTP call).
   - `Subscription` → call the registered `IAgentQuotaProbe`, cache result for
     `QuotaCacheTtl` (default 60 s).
   - If `ModelId` is set and the snapshot includes `PerModel[ModelId]`, gate
     on the model bucket instead of the overall quota.
   - Unknown (`AvailablePct < 0`) follows `UnknownPolicy` (`UseObservedFailures`
     by default).
   - Pick the first member that the quota gate allows.
6. If no member qualifies (all exhausted):
   - Class has at least one Subscription member → `ShouldWait = true`,
     schedule re-enqueue after `QuotaRecheckInterval`.
   - Class has only PayPerApi members → fire the first member anyway (this
     path is unreachable in normal operation since PayPerApi probes always
     return 100 %).

When `AgentClassId` is null and the project has no `DefaultAgentClass`, the
router is skipped entirely — no probe call, no wait.

---

## Quota probes

Two probes are bundled:

### `ClaudeQuotaProbe`

Calls `https://api.anthropic.com/api/oauth/usage` with
`Authorization: Bearer <Claude OAuth access token>`.

Parses overall `rate_limit` plus per-model `additional_rate_limits`; see
[`quota-endpoints.md`](quota-endpoints.md).

### `CodexQuotaProbe`

Calls `https://chatgpt.com/backend-api/wham/usage` with
`Authorization: Bearer <ChatGPT access token>` and, when available,
`ChatGPT-Account-Id`.

Parses overall `rate_limit` plus per-model `additional_rate_limits`.
The WHAM response can use display bucket names such as
`GPT-5.3-Codex-Spark`; the parser also stores known buckets under their routed
CLI model id, including `gpt-5.5`.

### Unknown snapshots

Both probes return `AvailablePct = -1` on:
- HTTP 4xx / 5xx
- Network error
- Unrecognised JSON shape
- Token not configured

`AvailablePct = -1` follows `UnknownPolicy`. The default is
`UseObservedFailures`, not blind fail-open.

---

## Quota router tuning

Configured under `CodeyBox:QuotaRouter`:

```json
{
  "CodeyBox": {
    "QuotaRouter": {
      "MinQuotaPct": 10,
      "QuotaRecheckIntervalSeconds": 300,
      "QuotaCacheTtlSeconds": 60,
      "UnknownPolicy": "UseObservedFailures",
      "ObservedFailureWindowMinutes": 10,
      "ObservedFailureRetentionMinutes": 30,
      "HeadroomProjectionEnabled": true,
      "HeadroomHistoryItemCount": 20,
      "HeadroomHistoryWindowDays": 14,
      "HeadroomTokensPerQuotaPct": 10000,
      "HeadroomTokensPerQuotaPctByAgent": {}
    }
  }
}
```

| Key | Default | Description |
|-----|---------|-------------|
| `MinQuotaPct` | `10` | Minimum available percentage before a Subscription member is skipped. |
| `QuotaRecheckIntervalSeconds` | `300` | Seconds to wait before re-probing when all Subscription members are exhausted. |
| `QuotaCacheTtlSeconds` | `60` | Seconds to cache a probe result. Keeps the pickup loop cheap under load. |
| `UnknownPolicy` | `UseObservedFailures` | How to handle unknown probe responses: recent quota failures block, otherwise allow. `FailCautious` blocks all unknowns; `FailOpen` is opt-in legacy behavior. |
| `ObservedFailureWindowMinutes` | `10` | Minutes a quota-shaped stderr failure blocks the same agent/model. |
| `ObservedFailureRetentionMinutes` | `30` | Minutes observed failures are retained in `state.db`. |
| `HeadroomProjectionEnabled` | `true` | Refuse a subscription member when recent project cost history predicts the next iteration would cross below `MinQuotaPct`. |
| `HeadroomHistoryItemCount` | `20` | Number of recent project work items sampled for the estimate. |
| `HeadroomHistoryWindowDays` | `14` | Maximum age of cost rows used for the estimate. |
| `HeadroomTokensPerQuotaPct` | `10000` | Fallback conversion from recent iteration tokens to quota percentage points. |
| `HeadroomTokensPerQuotaPctByAgent` | `{}` | Optional per-agent conversion overrides. |

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

### `WorkItem.MinModelScore`

The minimum `QualityScore` the router will accept for this item. Default `95`
allows Gemini-3-Flash-high-reasoning (score 95) as a frontier-adjacent fallback.
Lower it (e.g. `70`) for low-stakes work that can tolerate a weaker model:

```json
{
  "projectId": "my-app",
  "title": "Fix typo in README",
  "prompt": "...",
  "minModelScore": 70
}
```

There is no `AllowEconomyFallback` flag — `MinModelScore` is the single concept.

### `Project.DefaultAgentClass`

Set once per project so all work items inherit quota routing without specifying
`agentClassId` on every item:

```json
{
  "Id": "my-app",
  "DefaultAgentClass": "frontier-coding",
  ...
}
```

A per-item `AgentClassId` overrides the project default.

---

## Audit agent vs. agent class

The agent-class router described above applies to the **work phase** (and
rework) of a work item. The **audit phase** uses a separate resolution path
via `Project.Audit.AuditAgent` / `Project.Audit.PerAuditorAgent` — see
`docs/audit.md` for the full cross-review documentation.

Key differences:

| | Agent class (work phase) | AuditAgent (audit phase) |
|---|---|---|
| Configured on | `AgentClasses` catalog + `WorkItem.AgentClassId` | `Project.Audit.AuditAgent` |
| Resolution | Score + quota probe across members | Three-level: PerAuditorAgent → AuditAgent → work agent |
| Quota events | `quota_router.probed`, `.scored`, `.waiting`, `.deferred` | `quota_router.audit_fallthrough` |
| Applies to | Every phase (work, rework, merge) | LLM auditors only |

Auditors are **not** class-routed — they pin to a specific agent kind, not a
class. This is intentional: cross-review requires a known model identity (so
operators can correlate which model reviewed which diff), whereas class-routing
deliberately hides which member runs.

## Audit events

All routing decisions are emitted as `Audit=true` events:

| Event name | When |
|------------|------|
| `quota_router.probed` | After each probe call (agent, class, available %). |
| `quota_router.scored` | Once per pickup: chosen member's base/effective score and applied modifiers; all rejected members with their scores and reasons. |
| `quota_router.waiting` | When all Subscription members are exhausted. |
| `quota_router.deferred` | When the orchestrator schedules a deferred re-enqueue. |
| `quota_router.audit_fallthrough` | When the audit agent's quota was low and the pipeline fell through to the work agent. |

---

## Startup validation

At startup the orchestrator validates the `AgentClasses` config:

- Each class `Id` is unique (case-insensitive).
- Each class has at least one member.
- Each member `Agent` is non-empty.
- Each member `Billing` is `Subscription` or `PayPerApi`.
- Each member has a `QualityScore` in 0–200. Missing scores are **rejected**
  with a migration message: add `QualityScore=N; see docs/agent-classes.md`.
- Gemini members with `QualityScore ≥ 90` must have `ReasoningMode="high"`.
- A class with only Subscription members emits a startup **warning**:
  > AgentClass 'X' has no PayPerApi fallback — items may wait indefinitely
