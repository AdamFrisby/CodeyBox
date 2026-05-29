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

### Slotting opencode

opencode fronts multiple model providers (DeepSeek, Anthropic, OpenAI, …)
under one subscription credential. It pairs naturally with the existing
frontier members as a cheap-tokens bulk-volume option (DeepSeek default)
plus an optional redundant high-quality fallback (Anthropic-via-opencode
or OpenAI-via-opencode). Suggested starting scores:

```json
{
  "Agent": "opencode",
  "Billing": "Subscription",
  "ModelId": "deepseek/deepseek-coder",
  "QualityScore": 90
}
```

The default 88–92 range is intentionally below Claude / Codex / Cursor
(all ~98–100) because DeepSeek is strong but not Opus-class on the
heaviest refactors. opencode slotted with `ModelId: "anthropic/…"` or
`ModelId: "openai/…"` should be scored alongside the underlying
provider's own member; it is a redundancy path, not a quality upgrade.

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
| `Capabilities` | no | List of clearance/trust tags this member is allowed to handle (e.g. `["sensitive", "architectural"]`). Default empty — a member with no tags can only run work items that require no tags. See [Capability gate](#capability-gate) below. |

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

Trust/clearance is governed by **`RequiredCapabilities`** on the work item (see
[Capability gate](#capability-gate) below). `QualityScore` orders the
already-eligible members by preference. During the legacy-eligibility transition
window a `MinModelScore` floor still composes (AND) with the capability gate.

1. **Eligibility.** Filter to members that (a) declare every tag in
   `RequiredCapabilities` and (b) meet the `MinModelScore` floor (default 0 —
   open-by-default). Both gates ignore TOD modifiers.
2. **Effective score.** Time-of-day modifiers (see below) are added to the base
   score to produce each eligible member's *effective* score for this pickup.
3. **Sort.** Members are sorted descending by effective score. Ties are broken
   by billing (`Subscription` before `PayPerApi`), then original config order.
4. **Quota probe.** Members are probed in sorted order; the first one with
   sufficient quota wins.

The effective score of a member can drop *below* the floor after a TOD modifier
is applied — that is intentional. The floor check uses the *base* score because
TOD modifiers are preference-shaping tiebreakers, not eligibility gates. A model
with base score 95 remains eligible even if a −1 modifier makes its effective
score 94.

### No eligible member

If no member is eligible, the work item **fails immediately** with error
`ROUTING_NO_ELIGIBLE: no member of class '...' meets MinModelScore=N /
RequiredCapabilities=[...]`. The item is not retried; the operator must relax
the work item's clearance/floor or add a capable member to the class.

---

## Capability gate

`QualityScore` is a **routing preference** — "which eligible model is the
strongest." It is not the right place to express **trust** — "which models may
touch this sensitive code at all." Conflating the two means a strong model at
QS 92 is wrongly excluded from a sensitive item gated at QS 95, and adjusting a
score for unrelated reasons silently changes who is allowed to do the work.

Each member can declare a `Capabilities` tag list, and each work item can require
a `RequiredCapabilities` set. The router routes the item only to members whose
declared capabilities cover every required tag. Members with no tags can still
run any item whose required set is empty (open-by-default).

```json
{
  "Agent": "claude",
  "Billing": "Subscription",
  "ModelId": "claude-opus-4-7",
  "QualityScore": 100,
  "Capabilities": ["sensitive", "architectural"]
}
```

A work item then declares what it needs:

```http
POST /workitems
{
  "projectId": "core",
  "title": "Rewrite the auth middleware",
  "prompt": "…",
  "agentClassId": "frontier-coding",
  "requiredCapabilities": ["sensitive"]
}
```

Eligibility composes:

- `RequiredCapabilities` is the **clearance/trust gate**.
- `MinModelScore` is the legacy capability floor, retained alongside the
  capability gate during the transition window. Both must pass.
- `QualityScore` ranks among eligible members (highest effective score wins).
  It is **never** the gate.

Recommended tag vocabulary (start small, extend as needs emerge):

| Tag | Use for |
|-----|---------|
| `sensitive` | Anything you would not want a weaker or unverified model to touch — auth flows, secrets handling, billing logic. |
| `architectural` | Cross-cutting refactors and design-doc-shaped work. |
| `security` | Threat-modelling, dependency vulns, anything in a security review. |

Tag comparison is case-insensitive; values are otherwise free-form so you can
extend the vocabulary without code changes. The builder de-dupes and trims, so
`"Sensitive"` and `"sensitive"` collapse to a single tag.

### Default-open

A work item created without `requiredCapabilities` (or with an empty list) is
eligible on every member of its class. Most items should run on whatever agent
is free; restrict via `requiredCapabilities` only for the small set of items
that genuinely demand it.

### Migration from `MinModelScore`

The `MinModelScore` floor still works during the transition window — set both
on an item and it must pass both gates. To move existing restricted items:

1. Tag your frontier members with the relevant clearance, e.g. add
   `"Capabilities": ["sensitive"]` to the Claude/Codex frontier members.
2. Replace `minModelScore: 95` on items that need restriction with
   `requiredCapabilities: ["sensitive"]`.
3. The floor can then default to 0; the capability gate carries the trust
   semantics.

A follow-up item will deprecate and remove `MinModelScore` once existing items
have migrated.

---

## Capability gate

`QualityScore` is a **routing preference** — "which eligible model is the
strongest." It is not the right place to express **trust** — "which models may
touch this sensitive code at all." Conflating the two means a strong model at
QS 92 is wrongly excluded from a sensitive item gated at QS 95, and adjusting a
score for unrelated reasons silently changes who is allowed to do the work.

Each member can declare a `Capabilities` tag list, and each work item can require
a `RequiredCapabilities` set. The router routes the item only to members whose
declared capabilities cover every required tag. Members with no tags can still
run any item whose required set is empty (open-by-default).

```json
{
  "Agent": "claude",
  "Billing": "Subscription",
  "ModelId": "claude-opus-4-7",
  "QualityScore": 100,
  "Capabilities": ["sensitive", "architectural"]
}
```

A work item then declares what it needs:

```http
POST /workitems
{
  "projectId": "core",
  "title": "Rewrite the auth middleware",
  "prompt": "…",
  "agentClassId": "frontier-coding",
  "requiredCapabilities": ["sensitive"]
}
```

Eligibility composes:

- `RequiredCapabilities` is the **clearance/trust gate**.
- `MinModelScore` is the legacy capability floor, retained alongside the
  capability gate during the transition window. Both must pass.
- `QualityScore` ranks among eligible members (highest effective score wins).
  It is **never** the gate.

Recommended tag vocabulary (start small, extend as needs emerge):

| Tag | Use for |
|-----|---------|
| `sensitive` | Anything you would not want a weaker or unverified model to touch — auth flows, secrets handling, billing logic. |
| `architectural` | Cross-cutting refactors and design-doc-shaped work. |
| `security` | Threat-modelling, dependency vulns, anything in a security review. |

Tag comparison is case-insensitive; values are otherwise free-form so you can
extend the vocabulary without code changes. The builder de-dupes and trims, so
`"Sensitive"` and `"sensitive"` collapse to a single tag.

### Default-open

A work item created without `requiredCapabilities` (or with an empty list) is
eligible on every member of its class. Most items should run on whatever agent
is free; restrict via `requiredCapabilities` only for the small set of items
that genuinely demand it.

### Migration from `MinModelScore`

The `MinModelScore` floor still works during the transition window — set both
on an item and it must pass both gates. To move existing restricted items:

1. Tag your frontier members with the relevant clearance, e.g. add
   `"Capabilities": ["sensitive"]` to the Claude/Codex frontier members.
2. Replace `minModelScore: 95` on items that need restriction with
   `requiredCapabilities: ["sensitive"]`.
3. The floor can then default to 0; the capability gate carries the trust
   semantics.

A follow-up item will deprecate and remove `MinModelScore` once existing items
have migrated.

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
2. **Filter** members by eligibility — both gates must pass:
   - The legacy floor: base `QualityScore ≥ item.MinModelScore`.
   - The capability gate: the member's `Capabilities` covers every tag in
     `item.RequiredCapabilities` (an empty required list always passes).
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
      "ProbeMaxRetries": 2,
      "ProbeRetryInitialDelayMs": 250,
      "ProbeMaxConsecutiveFailures": 3,
      "ProbeMaxStalenessSeconds": 300
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
| `ProbeMaxRetries` | `2` | Additional retries on a transient probe failure (network error / timeout / 5xx) before recording the failure. Hot-reloadable; currently honoured by the Claude probe. |
| `ProbeRetryInitialDelayMs` | `250` | Base retry backoff in milliseconds; doubles each attempt. Hot-reloadable. |
| `ProbeMaxConsecutiveFailures` | `3` | Consecutive probe failures tolerated before the probe stops returning the retained last-known-good snapshot. A single transient blip cannot silently disable the `MinQuotaPct` floor. Hot-reloadable. |
| `ProbeMaxStalenessSeconds` | `300` | Maximum age of a retained last-known-good snapshot before it is dropped in favour of `AvailablePct=-1` (unknown). Hot-reloadable. |

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
