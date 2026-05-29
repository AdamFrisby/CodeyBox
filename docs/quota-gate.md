# Quota Gate

The pre-pickup quota gate has three inputs:

- quota probes for Claude and Codex subscription accounts
- observed quota-shaped failures from recent agent stderr
- operator-configured local spend budgets (see [`agent-budgets.md`](agent-budgets.md))

The router checks all three before dispatching a subscription-billed class
member, taking `MIN(real probe AvailablePct, local budget AvailablePct)`.
Pay-per-API members are not quota-gated.

## Probe Model

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

## Unknown Policy

`CodeyBox:QuotaRouter:UnknownPolicy` controls snapshots where
`AvailablePct < 0`:

- `UseObservedFailures` default. Allow unknown only when this agent/model has no
  recent quota-shaped failure.
- `FailCautious`. Treat unknown as exhausted.
- `FailOpen`. Preserve the old behavior; unknown is treated as available.

## Observed-Failure Breaker

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

`API Error: 401` from the Claude CLI is **not** included. Anthropic's
single-use OAuth refresh tokens combined with concurrent host + in-VM CLI
invocations produced intermittent 401s that tripped the breaker even when the
subscription was fully available. The 401 is now recorded via the
`agent.claude_unauthorized` audit-log event rather than the breaker; the
shared-refresh race is structurally closed by stripping the refresh_token from
the bundle materialised into the sandbox (see
`ClaudeOAuthFileCredentialProvider` for the rationale).

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

### Operator-Extensible Patterns

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

## Operator Endpoint

`GET /quota` returns:

- current quota router thresholds and unknown policy
- each registered probe's latest snapshot
- per-model quota breakdowns
- observed failure counters from the last 60 minutes
- overall and per-model `wouldAllow` decisions

The endpoint requires the normal API bearer token; it is not included in the
anonymous health-check surface.

See [quota-endpoints.md](quota-endpoints.md) for the captured response shapes
that drive parser fixtures.
