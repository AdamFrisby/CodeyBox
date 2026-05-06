# Quota Gate

The pre-pickup quota gate has two inputs:

- quota probes for Claude and Codex subscription accounts
- observed quota-shaped failures from recent agent stderr

The router checks both before dispatching a subscription-billed class member.
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

When an agent exits unsuccessfully and stderr contains one of the documented
quota patterns, CodeyBox records
`(projectId, agent, modelId, failureKind, observedAt)` in `state.db`. Agent
stderr is untrusted, so runtime observations are scoped to the triggering
project; the router skips the same `(projectId, agent, modelId)` for
`ObservedFailureWindowMinutes` even if the next probe is unknown or stale.

Recognized patterns are intentionally conservative:

- `hit your usage limit`
- `hit your limit`
- `rate_limit_exceeded`
- `API Error: 401`

Records are retained for `ObservedFailureRetentionMinutes`.

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
