# Quota gating

The pre-pickup quota gate has three inputs:

- quota probes for Claude and Codex subscription accounts
- observed quota-shaped failures from recent agent stderr
- operator-configured local spend budgets (see [`budgets.md`](budgets.md))

The router checks all three before dispatching a subscription-billed class
member, taking `MIN(real probe AvailablePct, local budget AvailablePct)`.
Pay-per-API members are not quota-gated.

## Per-window floors

`AvailablePct` is the MIN across the provider's windows (e.g. claude's
`five_hour` + `seven_day`). A single overall `MinQuotaPct` applied to the
min treats 10% of the much smaller `five_hour` window the same as 10% of
the `seven_day` window — but a 5-hour budget has far less absolute headroom
for in-flight + cache-staleness overshoot during a burst (up to
`MaxConcurrent` dispatched runs already burning + new dispatches inside
`QuotaCacheTtlSeconds`). To stop that overshoot from blowing through the
small window, the gate also enforces a per-window floor: dispatch requires
EVERY window's `AvailablePct` to be at or above its own configured floor,
using the per-window readings the probe surfaces in
`AgentQuotaSnapshot.Windows` / `PerModel[].Windows`. Any window below its
floor blocks dispatch; an unlisted window falls back to `MinQuotaPct`.

Floors are configured via `CodeyBox:QuotaRouter:MinQuotaPctByWindow`,
keyed by provider window name (e.g. `five_hour`, `seven_day`). Default
`{"five_hour": 25}` for `MaxConcurrent=4` — tune up for higher fleet
concurrency. Hot-reloadable.

Per-window floors and the time-based ramp are orthogonal: the ramp
controls the aggregated min-across-windows reading over time, while the
per-window map gates each window independently regardless of where in the
ramp the window happens to be.

## Time-based floor ramp

The gate's minimum-available-quota floor is a linear ramp from
`StartFloorPct` (just after a window reset) down to `EndFloorPct` (as the
window approaches reset), keyed off the probe's `ResetAt` and the
configured `RampWindow`. Early in a weekly cycle the floor is high (default
25%) so CodeyBox doesn't burn the shared subscription down to a sliver and
starve the operator's own Claude Code session or monitoring. Late in the
cycle the floor drops (default 3%) so otherwise-stranded quota gets drained
before the use-it-or-lose-it reset rather than sitting unused.

`fractionElapsed = 1 - timeUntilReset / RampWindow`, clamped to `[0, 1]`, and
`effectiveFloorPct = lerp(StartFloorPct, EndFloorPct, fractionElapsed)`.
Per-agent floor overrides go in `FloorByAgent`, keyed by agent kind:

```json
"QuotaRouter": {
  "FloorByAgent": {
    "codex": {
      "StartFloorPct": 1,
      "EndFloorPct": 0,
      "MinQuotaPct": 1
    }
  }
}
```

Each `FloorByAgent` entry may set `StartFloorPct`, `EndFloorPct`,
`MinQuotaPct`, and optionally `RampWindowSeconds`; omitted fields inherit the
global `QuotaRouter` values. Omitted agents use the global ramp unchanged. A
near-zero floor lets a work-only subscription agent burn close to empty, while
an oversight agent left on the global defaults keeps the protective reserve.
When an agent sets `MinQuotaPct`, that value is also the fallback floor for its
provider windows, so global `MinQuotaPctByWindow` reserves do not accidentally
hold back that burn-to-zero agent.

`RampWindowByAgentSeconds` overrides the ramp window alone for one agent;
agents absent from that map use the default `RampWindowSeconds`. The ramp
applies to Subscription members only — PayPerApi members fall back to the fixed
`MinQuotaPct` because their `AvailablePct` is driven by the operator's
local budget, not an agent quota window. Unknown windows (no `ResetAt`)
also fall back to `MinQuotaPct`.

All knobs are hot-reloadable via the `CodeyBox:QuotaRouter` config block —
edits to `~/codeybox-extra.json` take effect on the next gate decision.
`IntraKindRoutingPolicy` controls how eligible class members are ordered after
the quality and capability gates: `MostQuotaFirst` (default), `RoundRobin`,
`Sticky`, or `DeadlineAwareDrain`. `DeadlineAwareDrain` can reorder across agent
kinds that already satisfy the item quality bar, using quota headroom divided by
hours to the nearest live or configured expected reset. `DrainAggressiveness`
and per-agent `ExpectedResets` are hot-reloadable in the same block.

## What a probe returns

`AgentQuotaSnapshot.AvailablePct` is the overall account quota. `PerModel`
contains model-specific buckets keyed by model id. When a class member has
`ModelId`, the router uses `PerModel[ModelId]` when present; otherwise it falls
back to the overall percentage.

Codex's WHAM endpoint may name buckets by display limit instead of CLI model
id. The captured response uses `GPT-5.3-Codex-Spark`; the Codex parser also
stores that bucket under the routed default `gpt-5.5` so the router can block
the configured Codex member when that bucket is exhausted.

For multi-window quota responses, the parser uses the most constrained window:

- `primary_window` = 5-hour rolling
- `secondary_window` = weekly

Unexpected JSON fields are ignored. Missing fields make only that part unknown.

## Unknown readings

A probe that cannot produce a real reading returns an **unknown** snapshot
(`AgentQuotaSnapshot.IsKnown == false`) tagged with a `QuotaUnknownReason`
(`Transient` / `Permanent` / `NoCredential`) — not a magic `AvailablePct = -1`.
The reason drives two layers:

### Last-known-good substitution

Every probe is wrapped in `LastKnownGoodQuotaProbe`. On a **Transient** unknown
(network/5xx/timeout, or an inner throw) it substitutes the most recent real
reading for that `(RouteKey, ModelId)` — bounded by `ProbeMaxStalenessSeconds`
and the reading's own reset — so a momentary blip keeps the floor enforced
instead of collapsing to unknown + fall-open. A **Permanent** (revoked token,
4xx, unparseable body) or **NoCredential** unknown discards the retained reading
(the prior value can no longer be trusted). This applies uniformly to all probes;
there is no per-probe retention code.

### Unknown policy

`CodeyBox:QuotaRouter:UnknownPolicy` controls snapshots that are still unknown
after the last-known-good layer:

- `UseObservedFailures` default. Allow unknown only when this agent/model has no
  recent quota-shaped failure.
- `FailCautious`. Treat unknown as exhausted.
- `FailOpen`. Unknown is treated as available. Opt in only when a broken
  probe blocking dispatch is worse than overrunning the provider's cap.

## Rate-aware burn gate

For subscription-billed members with known quota, the router can also compare
the live in-flight count with how many recent average work-item burns fit in
the remaining window: `fit = AvailablePct / AvgBurnPctPerItem`.
`AvgBurnPctPerItem` is computed only when recent token-cost samples exist and
`CodeyBox:AgentBurnEstimator:WindowTokenBudget:<agent>` is configured to a
positive token budget. If samples exist but that budget is missing or zero, the
router fails open for that agent rather than throttling on the hardcoded
cold-start default. The `/concurrency` surface reports the sample count with
status `NoWindowBudget` so operators can tell this apart from true no-history
cold start.

## Observed-failure breaker

When an agent exits unsuccessfully and stderr **or** stdout contains one of
the documented quota patterns, CodeyBox records
`(agent, modelId, failureKind, observedAt)` in `state.db`, with `projectId`
retained only as diagnostic metadata when available. Subscription quota walls
are per-`(agent, modelId)` (some vendor quotas are per-model rolling windows
invisible to the per-agent probe), so the router skips the exact
`(agent, modelId)` for `ObservedFailureWindowMinutes` across all projects
even if the next probe still reports the per-agent ceiling as available.
Other models on the same agent are not blocked.

Recognized patterns are intentionally conservative:

- `hit your usage limit`
- `hit your limit`
- `rate_limit_exceeded`
- `RESOURCE_EXHAUSTED`
- `exceeded the rate limit`
- `quota exceeded`
- `exhausted your capacity` (Gemini per-model wall)

`API Error: 401` from the Claude CLI is deliberately **not** a quota pattern.
Anthropic's single-use OAuth refresh tokens, combined with concurrent host and
in-VM CLI invocations, produce intermittent 401s on a fully available
subscription. A 401 is recorded as an `agent.claude_unauthorized` audit-log
event instead, and the underlying race is closed by stripping the refresh token
from the bundle materialised into the sandbox — see
`ClaudeOAuthFileCredentialProvider`.

Detection scans stderr, plain-text stdout, and (when present) the structured
NDJSON stream-json events emitted by claude/codex/gemini CLIs. Stream-json
error envelopes recognised:

- `{"type":"result","status":"error","error":{"message":"..."}}` (gemini)
- `{"type":"result","is_error":true,"result":"..."}` (claude)
- `{"type":"error","message":"..."}` and `{"msg":{"type":"error","message":"..."}}` (codex)

The reset interval (e.g. `reset after 21h41m24s`, `try again after 5m17s`)
is parsed and persisted as `WorkItem.QuotaResetAt` so the targeted retry
timer fires once the wall expires.

Routing-log rejections distinguish three cases so audit-log readers can tell
breaker hits apart from probe-derived rejections:

- `observed quota failure 8 minutes ago` — per-(agent, model) breaker hit.
- `quota exhausted` — probe returned a value below `MinQuotaPct`.
- `below floor (X < Y)` — member's `QualityScore` is below the work item's
  `MinModelScore`.

Records are retained for `ObservedFailureRetentionMinutes`.

### Adding quota patterns

`CodeyBox:QuotaFailurePatterns` lets operators append per-agent stderr/stdout
patterns to a detector's built-in defaults without recompiling. Each key is an
agent kind value; each entry is `{ pattern, kind }` where `kind` is one of
`LimitReached`, `RateLimitExceeded`, `Unauthorized`. Cursor is the first
supported agent kind:

```json
"CodeyBox": {
  "QuotaFailurePatterns": {
    "cursor": [
      { "pattern": "exceeded your subscription", "kind": "LimitReached" }
    ]
  }
}
```

Built-in cursor defaults already cover the observed exhaustion stderr
(`out of usage`, `Switch to Auto`, `increase your limit`) — the config hook is
for follow-on shapes that surface before a code release can land.

### Adding login-prompt patterns

`CodeyBox:AuthFailurePatterns` lets operators append per-agent stdout/stderr
substrings for CLI login prompts without recompiling. This detector is separate
from quota detection because the broken CLI can exit `0` and produce no diff,
which would otherwise look like a benign "agent produced no changes" outcome.

Built-in defaults cover prompts such as `Authentication required`, OAuth consent
URLs, `Please visit the URL to log in`, `authentication timed out`, and common
`run \`... login\`` hints. Configured patterns default to stderr-only because
stdout can contain model-controlled task prose. Add `stream: "stdout"` only for
tightly-formed CLI transcripts that are safe to trust when printed on stdout:

```json
"CodeyBox": {
  "AuthFailurePatterns": {
    "antigravity": [
      { "pattern": "complete browser sign-in before continuing", "stream": "stderr" },
      { "pattern": "Waiting for browser sign-in confirmation", "stream": "stdout" }
    ]
  }
}
```

When a runtime auth/login prompt is detected, the agent is benched via the
availability registry, an `agent.smoke_failed` webhook with
`category=persistent` is emitted, and the affected work item fails with
`failureKind=auth_required` rather than being treated as a normal no-diff run
or retryable infrastructure failure.

## `GET /quota`

`GET /quota` returns:

- current quota router thresholds and unknown policy
- each configured subscription instance's latest snapshot, including
  `agentInstanceId`, reset windows, and its containing class
- kind aggregates so operators can distinguish per-instance rows from the
  broader agent kind
- per-model quota breakdowns
- observed failure counters from the last 60 minutes
- overall and per-model `wouldAllow` decisions
- paused-agent status. Paused agents are reported distinctly from quota
  exhaustion with `dispatchStatus: "paused"` and reason text of the form
  `paused by operator: <reason>`.

The endpoint requires the normal API bearer token; it is not included in the
anonymous health-check surface.

## Probe response shapes

CodeyBox probes subscription quotas without logging OAuth tokens or response
bodies. These redacted captures are what the defensive parsers are written
against; the same shapes back the parser fixtures.

### Claude

Probe:

```http
GET https://api.anthropic.com/api/oauth/usage
Authorization: Bearer <redacted>
```

Captured redacted response structure, mirrored in
`tests/CodeyBox.Tests/Fixtures/Quota/claude-oauth-usage.redacted.json`:

```json
{
  "plan_type": "max",
  "rate_limit": {
    "allowed": true,
    "limit_reached": false,
    "primary_window": {
      "used_percent": 20,
      "limit_window_seconds": 18000,
      "reset_after_seconds": 3600,
      "reset_at": 1778091218
    },
    "secondary_window": {
      "used_percent": 10,
      "limit_window_seconds": 604800,
      "reset_after_seconds": 500000,
      "reset_at": 1778605571
    }
  },
  "additional_rate_limits": [
    {
      "limit_name": "claude-sonnet-4-6",
      "metered_feature": "claude_sonnet",
      "rate_limit": {
        "primary_window": { "used_percent": 30, "limit_window_seconds": 18000, "reset_at": 1778091218 },
        "secondary_window": { "used_percent": 40, "limit_window_seconds": 604800, "reset_at": 1778605571 }
      }
    },
    {
      "limit_name": "claude-opus-4-7",
      "metered_feature": "claude_opus",
      "rate_limit": {
        "primary_window": { "used_percent": 100, "limit_window_seconds": 18000, "reset_at": 1778091218 },
        "secondary_window": { "used_percent": 95, "limit_window_seconds": 604800, "reset_at": 1778605571 }
      }
    }
  ]
}
```

`primary_window` is treated as the 5-hour rolling window and
`secondary_window` as the weekly window. The parser uses the most constrained
available percentage across windows.

### Codex

The installed Codex CLI binary references `/backend-api/wham/usage` for account
rate limits. The dashboard billing endpoints are API-key billing and are not
used for ChatGPT subscription quota.

Probe:

```http
GET https://chatgpt.com/backend-api/wham/usage
Authorization: Bearer <redacted>
ChatGPT-Account-Id: <redacted>
```

Captured redacted response structure, mirrored in
`tests/CodeyBox.Tests/Fixtures/Quota/codex-wham-usage.redacted.json`:

```json
{
  "user_id": "user_REDACTED",
  "account_id": "acct_REDACTED",
  "email": "redacted@example.com",
  "plan_type": "prolite",
  "rate_limit": {
    "allowed": true,
    "limit_reached": false,
    "primary_window": {
      "used_percent": 34,
      "limit_window_seconds": 18000,
      "reset_after_seconds": 5865,
      "reset_at": 1778091218
    },
    "secondary_window": {
      "used_percent": 37,
      "limit_window_seconds": 604800,
      "reset_after_seconds": 520217,
      "reset_at": 1778605571
    }
  },
  "additional_rate_limits": [
    {
      "limit_name": "GPT-5.3-Codex-Spark",
      "metered_feature": "codex_bengalfox",
      "rate_limit": {
        "allowed": true,
        "limit_reached": false,
        "primary_window": {
          "used_percent": 0,
          "limit_window_seconds": 18000,
          "reset_after_seconds": 18000,
          "reset_at": 1778103354
        },
        "secondary_window": {
          "used_percent": 0,
          "limit_window_seconds": 604800,
          "reset_after_seconds": 519837,
          "reset_at": 1778605191
        }
      }
    }
  ],
  "credits": {
    "has_credits": false,
    "unlimited": false,
    "overage_limit_reached": false,
    "balance": "0"
  },
  "rate_limit_reached_type": null
}
```

